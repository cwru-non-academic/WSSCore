using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Unity-free implementation that ports Stimulation.cs behavior into an IStimulationCore.
/// It wraps a SerialToWSS transport and exposes the same stimulation controls without
/// any UnityEngine dependencies (no MonoBehaviour, Coroutines, or JsonUtility).
/// </summary>
public sealed class LegacyStimulationCore : IStimulationCore
{
    // ---- transport & config ----
    private readonly StimConfigController _config;
    private readonly bool _testMode;
    private readonly string _comPort = null;
    private readonly string _jsonPath;
    private readonly int _maxSetupTries;
    private readonly int _delayMsBetweenPackets = 12; // minimum wait time between streaming packets due to radio limitations
    private SerialToWSS _wss;

    // ---- runtime state ----
    private bool _ready;
    private bool _running;
    private bool _validMode;
    private bool _setupRunning;
    private bool _setupComplete;
    private int _currentSetupTries;
    private int _maxWSS = 1;
    private readonly Stopwatch _timer = new Stopwatch();

    // background streaming task (used to send messages at the radio's update rate)
    private CancellationTokenSource _streamCts;
    private Task _streamTask;

    // ---- channels & controller state ----
    private int[] _chAmps;  // mA (domain-specific nonlinear mapping to 0..255)
    private int[] _chPWs;   // us (0..255 or per device spec)
    private int _currentIPD = 50; // us
    private float[] _prevMagnitude;
    private float[] _currentMag;
    private float[] _d_dt;
    private float[] _dt;

    // ---- construction ----

    /// <summary>
    /// Uses explicit COM port string.
    /// </summary>
    public LegacyStimulationCore(string comPort, string JSONpath, bool testMode = false, int maxSetupTries = 5) {
        _comPort = comPort;
        _jsonPath = JSONpath;
        _config = new StimConfigController(_jsonPath);
        _testMode = testMode;
        _maxSetupTries = maxSetupTries;
    }

    /// <summary>
    /// Lowest-level constructor providing the SerialToWSS instance directly.
    /// </summary>
    public LegacyStimulationCore(string JSONpath, bool testMode = false, int maxSetupTries = 5)
    {
        _jsonPath = JSONpath;
        _config = new StimConfigController(_jsonPath);
        _testMode = testMode;
        _maxSetupTries = maxSetupTries;
    }

    // ---- lifecycle ----

    public void Initialize()
    {
        if(_ready || _setupRunning)
        {
            Shutdown();
        }
        // reset high-level flags
        _ready = false;
        _setupComplete = false;
        _setupRunning = false;
        _currentSetupTries = 0;
        _validMode = false;
        _running = false;

        // (re)load app config
        _config.LoadJSON();
        _maxWSS = _config._config.maxWSS;

        _timer.Reset();
        _timer.Start();

        InitStimArrays();
        VerifyStimMode();

        if (_testMode)
        {
            // do nothing with hardware; caller can still exercise API
            return;
        }

        if(_comPort != null)
        {
            _wss = new SerialToWSS(_comPort);
        }
        else
        {
            _wss = new SerialToWSS();
        }

        // run the same default schedule setup as the Unity script
        NormalSetup();
    }

    public void Tick()
    {
        if (_testMode || _wss == null) return;

        // fetch / forward any transport errors
        _wss.checkForErrors();
        if(_wss.msgs.Count > 0)
        {
            foreach (var msg in _wss.msgs)
            {
                if (msg.StartsWith("Error: "))
                {
                    Log.Error(msg);
                    // If an error occurs during setup, retry up to _maxSetupTries times
                    if (_setupRunning && _currentSetupTries < _maxSetupTries)
                    {
                        Log.Error("Error in setup. Retrying. " + _currentSetupTries.ToString() + " out of " + _maxSetupTries.ToString() + " attempts.");
                        _currentSetupTries++;
                        _wss.clearQueue();
                        Initialize();
                    }
                }
                else if (msg.StartsWith("Warning: "))
                {
                    Log.Warn(msg);
                }
                else
                {
                    Log.Info(msg);
                }
                _wss.msgs.Remove(msg);
            }
        }

        // When queue becomes empty during setup, mark ready
        if (_wss.isQueueEmpty() && (_setupComplete || _setupRunning))
        {
            if (_setupRunning)
            {
                _currentSetupTries = 0;
                _setupRunning = false;
                _setupComplete = true;
            }
            _ready = true;
        }
    }

    public void Shutdown()
    {
        try
        {
            StopStreamingInternal();
        }
        catch { /* ignore */ }

        if (!_testMode)
        {
            _running = false;
            try { _wss.zero_out_stim(); } catch { }
            try { _wss.releaseCOM_port(); } catch { }
        }

        _ready = false;
        _streamCts?.Cancel();
    }

    public void ReleaseTransportLayer()
    {
        
    }

    public void ResetTransportLayer()
    {
        Shutdown();
        Initialize();
    }

    public void LoadConfigFile()
    {
        _config.LoadJSON();
    }

    public void Dispose() => Shutdown();

    // ---- status ----

    public bool Started() => _testMode ? _running : _wss.Started();
    public bool Ready() => _ready;

    public bool IsModeValid()
    {
        VerifyStimMode();
        return _validMode;
    }

    // ---- streaming / control ----

    public void Stream_change(int targetWSS, int[] PA, int[] PW, int[] IPI)
    {
        if (_testMode) return;
        _ready = false;
        _wss.stream_change(targetWSS, PA, PW, IPI);
    }

    public void StimulateAnalog(string finger, bool rawValues, int PW, int amp = 3)
    {
        int channel = FingerToChannel(finger);
        if (channel <= 0 || channel > _maxWSS * 3) return;

        _chAmps[channel - 1] = amp;
        _chPWs[channel - 1] = PW;
    }

    public void Zero_out_stim()
    {
        if (_testMode) return;
        _ready = false;
        _wss.zero_out_stim();
    }

    public void StartStim(int targetWSS = 0)
    {
        if (_testMode)
        {
            StartStreamingInternal();
            return;
        }
        _wss.startStim(targetWSS);
        StartStreamingInternal();
    }

    public void StopStim(int targetWSS = 0)
    {
        if (_testMode)
        {
            _ready = false;
            _running = false;
            return;
        }
        _ready = false;
        _wss.stopStim(targetWSS);
        _running = false;
    }

    public void StimWithMode(string finger, float magnitude)
    {
        int channel = FingerToChannel(finger);
        if (channel <= 0 || channel > _maxWSS * 3) return;

        _chAmps[channel - 1] = (int)_config.getStimParam($"Ch{channel}Amp");
        _chPWs[channel - 1] = CalculateStim(channel, magnitude,
            _config.getStimParam($"Ch{channel}Max"),
            _config.getStimParam($"Ch{channel}Min"));
    }

    public void UpdateChannelParams(string finger, int max, int min, int amp)
    {
        int channel = FingerToChannel(finger);
        if (channel <= 0 || channel > _maxWSS * 3) return;

        _config.modifyStimParam($"Ch{channel}Amp", amp);
        _config.modifyStimParam($"Ch{channel}Max", max);
        _config.modifyStimParam($"Ch{channel}Min", min);
    }

    public void UpdateIPD(int targetWSS, int IPD)
    {
        if (_testMode) return;
        _ready = false;
        if (IPD > 1000) IPD = 1000;
        _currentIPD = IPD;
        _wss.edit_event_PW(targetWSS, 1, new int[] { 0, 0, _currentIPD });
        _wss.edit_event_PW(targetWSS, 2, new int[] { 0, 0, _currentIPD });
        _wss.edit_event_PW(targetWSS, 3, new int[] { 0, 0, _currentIPD });
    }

    public void UpdateIPD(int IPD)
    {
        if (_testMode) return;
        _ready = false;
        if (IPD > 1000) IPD = 1000;
        _currentIPD = IPD;
        for (int i = 1; i < _maxWSS + 1; i++)
        {
            _wss.edit_event_PW(i, 1, new int[] { 0, 0, _currentIPD });
            _wss.edit_event_PW(i, 2, new int[] { 0, 0, _currentIPD });
            _wss.edit_event_PW(i, 3, new int[] { 0, 0, _currentIPD });
        }
    }

    public void UpdateFrequency(int targetWSS, int FR)
    {
        if (_testMode) return;
        _ready = false;
        if (FR <= 0) throw new ArgumentOutOfRangeException(nameof(FR));
        int periodMs = (int)(1000.0f / FR);
        _wss.stream_change(targetWSS, null, new int[] { 0, 0, 0 }, new int[] { periodMs, periodMs, periodMs });
    }

    public void UpdateFrequency(int FR)
    {
        if (_testMode) return;
        _ready = false;
        if (FR <= 0) throw new ArgumentOutOfRangeException(nameof(FR));
        int periodMs = (int)(1000.0f / FR);
        for (int i = 1; i < _maxWSS + 1; i++)
        {
            _wss.stream_change(i, null, new int[] { 0, 0, 0 }, new int[] { periodMs, periodMs, periodMs });
        }
    }

    public void UpdateWaveform(int[] waveform, int eventID)
    {
        if (_testMode) return;
        _ready = false;
        WaveformBuilder stimShape = new WaveformBuilder(waveform);
        WaveformSetup(stimShape, eventID);
    }

    public void UpdateWaveform(int targetWSS, int[] waveform, int eventID)
    {
        if (_testMode) return;
        _ready = false;
        WaveformBuilder stimShape = new WaveformBuilder(waveform);
        WaveformSetup(targetWSS, stimShape, eventID);
    }

    public void UpdateWaveform(int cathodicWaveform, int anodicWaveform, int eventID)
    {
        if (_testMode) return;
        _ready = false;
        for (int i = 1; i < _maxWSS + 1; i++)
        {
            _wss.edit_event_shape(i, eventID, cathodicWaveform, anodicWaveform);
        }
    }

    public void UpdateWaveform(int targetWSS, int cathodicWaveform, int anodicWaveform, int eventID)
    {
        if (_testMode) return;
        _ready = false;
        _wss.edit_event_shape(targetWSS, eventID, cathodicWaveform, anodicWaveform);
    }

    public void UpdateWaveform(WaveformBuilder waveform, int eventID)
    {
        if (_testMode) return;
        _ready = false;
        WaveformSetup(waveform, eventID);
    }

    public void UpdateWaveform(int targetWSS, WaveformBuilder waveform, int eventID)
    {
        if (_testMode) return;
        _ready = false;
        WaveformSetup(targetWSS, waveform, eventID);
    }

    public void LoadWaveform(string fileName, int eventID)
    {
        // Unity-free JSON loading; expects "<fileName>WF.json" in current working directory
        // or an absolute/relative path passed by caller.
        string candidatePath = Path.Combine(_jsonPath, fileName);
        if (!candidatePath.EndsWith("WF.json", StringComparison.OrdinalIgnoreCase))
        {
            candidatePath = Path.ChangeExtension(fileName + "WF", "json");
        }

        try
        {
            string json = File.ReadAllText(candidatePath);
            var shape = JsonConvert.DeserializeObject<Waveform>(json);
            if (shape == null) throw new InvalidDataException("Waveform JSON deserialized to null");
            UpdateWaveform(new WaveformBuilder(shape), eventID);
        }
        catch (Exception ex)
        {
            // keep Unity-free: do not rely on Debug.Log*
            Log.Error($"[LegacyStimulationCore] JSON loading error: {ex.Message} ({candidatePath})");
        }
    }

    public void WaveformSetup(WaveformBuilder wave, int eventID)
    {
        if (_testMode) return;
        for (int i = 1; i < _maxWSS + 1; i++)
        {
            _wss.set_costume_waveform(i, 0, wave.getCatShapeArray()[0..^24], 0);
            _wss.set_costume_waveform(i, 0, wave.getCatShapeArray()[8..^16], 1);
            _wss.set_costume_waveform(i, 0, wave.getCatShapeArray()[16..^8], 2);
            _wss.set_costume_waveform(i, 0, wave.getCatShapeArray()[24..^0], 3);
            _wss.set_costume_waveform(i, 1, wave.getAnodicShapeArray()[0..^24], 0);
            _wss.set_costume_waveform(i, 1, wave.getAnodicShapeArray()[8..^16], 1);
            _wss.set_costume_waveform(i, 1, wave.getAnodicShapeArray()[16..^8], 2);
            _wss.set_costume_waveform(i, 1, wave.getAnodicShapeArray()[24..^0], 3);
            _wss.edit_event_shape(i, eventID, 11, 12);
        }
    }

    public void WaveformSetup(int targetWSS, WaveformBuilder wave, int eventID)
    {
        if (_testMode) return;
        _wss.set_costume_waveform(targetWSS, 0, wave.getCatShapeArray()[0..^24], 0);
        _wss.set_costume_waveform(targetWSS, 0, wave.getCatShapeArray()[8..^16], 1);
        _wss.set_costume_waveform(targetWSS, 0, wave.getCatShapeArray()[16..^8], 2);
        _wss.set_costume_waveform(targetWSS, 0, wave.getCatShapeArray()[24..^0], 3);
        _wss.set_costume_waveform(targetWSS, 1, wave.getAnodicShapeArray()[0..^24], 0);
        _wss.set_costume_waveform(targetWSS, 1, wave.getAnodicShapeArray()[8..^16], 1);
        _wss.set_costume_waveform(targetWSS, 1, wave.getAnodicShapeArray()[16..^8], 2);
        _wss.set_costume_waveform(targetWSS, 1, wave.getAnodicShapeArray()[24..^0], 3);
        _wss.edit_event_shape(targetWSS, eventID, 11, 12);
    }

    // ---- setup & edits ----

    public void Save(int targetWSS)
    {
        if (_testMode) return;
        _ready = false;
        _wss.populateFRAMSettings(targetWSS);
    }

    public void Load(int targetWSS)
    {
        if (_testMode) return;
        _ready = false;
        _wss.populateBoardSettings(targetWSS);
    }

    public void Load()
    {
        if (_testMode) return;
        _ready = false;
        for (int i = 1; i < _maxWSS + 1; i++)
        {
            _wss.populateBoardSettings(i);
        }
    }

    public void Request_Configs(int targetWSS, int command, int id)
    {
        if (_testMode) return;
        _ready = false;
        _wss.request_configs(targetWSS, command, id);
    }

    // ---- helpers & internals ----

    private void VerifyStimMode()
    {
        string mode = _config._config.sensationController;
        if (mode == "P" || mode == "PD")
        {
            _validMode = true;
        }
        else
        {
            Log.Error("[LegacyStimulationCore] Unrecognized mode, defaulting to proportional mode.");
            _validMode = false;
        }
    }

    private void InitStimArrays()
    {
        int n = _maxWSS * 3;
        _chAmps = new int[n];
        _chPWs = new int[n];
        _prevMagnitude = new float[n];
        _currentMag = new float[n];
        _d_dt = new float[n];
        _dt = new float[n];

        for (int i = 0; i < n; i++)
        {
            _chAmps[i] = 0;
            _chPWs[i] = 0;
            _prevMagnitude[i] = 0f;
            _dt[i] = _timer.ElapsedMilliseconds / 1000.0f;
        }
    }

    private int FingerToChannel(string finger)
    {
        if (string.IsNullOrEmpty(finger)) return 0;

        switch (finger)
        {
            case "Thumb": return 1;
            case "Index": return 2;
            case "Middle": return 3;
            case "Ring": return 4;
            case "Pinky": return 5;
            case "Palm": return 6;
            default:
                if (finger.StartsWith("ch", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(finger.Substring(2), out int ch)) return ch;
                }
                return 0;
        }
    }

    private int CalculateStim(int channel, float magnitude, float max, float min)
    {
        float output = 0;
        _currentMag[channel] = magnitude;
        float currentTime = _timer.ElapsedMilliseconds / 1000.0f;
        float denom = (currentTime - _dt[channel]);
        _d_dt[channel] = denom > 1e-5f ? (_currentMag[channel] - _prevMagnitude[channel]) / denom : 0f;
        _dt[channel] = currentTime;
        _prevMagnitude[channel] = _currentMag[channel];

        string mode = _config._config.sensationController;
        if (mode == "P")
        {
            output = magnitude * _config.getConstant("PModeProportional") + _config.getConstant("PModeOffsset");
        }
        else if (mode == "PD")
        {
            output = (_d_dt[channel] * _config.getConstant("PDModeDerivative")) +
                     (magnitude * _config.getConstant("PDModeProportional")) +
                      _config.getConstant("PDModeOffsset");
        }
        else
        {
            output = magnitude * _config.getConstant("PModeProportional") + _config.getConstant("PModeOffsset");
        }

        // clamp 0..1, then scale to [min, max]
        if (output > 1f) output = 1f;
        else if (output < 0f) output = 0f;

        if (output > 0f) output = (output * (max - min)) + min;
        else output = 0f;

        return (int)output;
    }

    private int AmpTo255Convention(int amp)
    {
        // mirrors Unity code but uses System.Math (float math is OK here)
        if (amp < 4)
        {
            // (int) Mathf.Pow(amp /0.0522f, 1/1.5466f)+1;
            double v = Math.Pow(amp / 0.0522f, 1.0 / 1.5466f) + 1.0;
            return (int)v;
        }
        else
        {
            // (int) ((amp + 1.7045f)/ 0.3396f)+1;
            double v = ((amp + 1.7045f) / 0.3396f) + 1.0;
            return (int)v;
        }
    }

    private void StartStreamingInternal()
    {
        if (_running) return;
        _running = true;
        if (_streamTask != null && !_streamTask.IsCompleted) return;

        _streamCts = new CancellationTokenSource();
        var token = _streamCts.Token;

        _streamTask = Task.Run(async () =>
        {
            while (_running && !token.IsCancellationRequested)
            {
                if (Started() && _ready)
                {
                    // WSS 1..N
                    for (int w = 1; w <= _maxWSS; w++)
                    {
                        int baseIdx = (w - 1) * 3;
                        if (!_testMode)
                        {
                            _wss.stream_change(w,
                                new int[] { AmpTo255Convention(_chAmps[baseIdx + 0]), AmpTo255Convention(_chAmps[baseIdx + 1]), AmpTo255Convention(_chAmps[baseIdx + 2]) },
                                new int[] { _chPWs[baseIdx + 0], _chPWs[baseIdx + 1], _chPWs[baseIdx + 2] },
                                null);
                        }
                        await Task.Delay(_delayMsBetweenPackets, token);
                    }
                }
                else
                {
                    await Task.Delay(_delayMsBetweenPackets, token);
                }
            }
        }, token);
    }

    private void StopStreamingInternal()
    {
        _running = false;
        _streamCts?.Cancel();
    }

    /// <summary>
    /// Sends the same setup sequence as Stimulation.NormalSetup().
    /// </summary>
    private void NormalSetup()
    {
        _setupRunning = true;
        try
        {
            for (int i = 1; i < _maxWSS + 1; i++)
            {
                // mirror Unity setup
                _wss.clear(i, 0);
                _wss.create_schedule(i, 1, 13, 170);
                _wss.creat_contact_config(i, 1, new int[] { 0, 0, 2, 1 }, new int[] { 0, 0, 1, 2 });
                _wss.create_event(i, 1, 0, 1, 0, 0, new int[] { 11, 11, 0, 0 }, new int[] { 11, 11, 0, 0 }, new int[] { 0, 0, 50 });
                _wss.edit_event_ratio(i, 1, 8);
                _wss.add_event_to_schedule(i, 1, 1);

                _wss.create_schedule(i, 2, 13, 170);
                _wss.creat_contact_config(i, 2, new int[] { 0, 2, 0, 1 }, new int[] { 0, 1, 0, 2 });
                _wss.create_event(i, 2, 2, 2, 0, 0, new int[] { 11, 0, 11, 0 }, new int[] { 11, 0, 11, 0 }, new int[] { 0, 0, 50 });
                _wss.edit_event_ratio(i, 2, 8);
                _wss.add_event_to_schedule(i, 2, 2);

                _wss.create_schedule(i, 3, 13, 170);
                _wss.creat_contact_config(i, 3, new int[] { 2, 0, 0, 1 }, new int[] { 1, 0, 0, 2 });
                _wss.create_event(i, 3, 4, 3, 0, 0, new int[] { 11, 0, 0, 11 }, new int[] { 11, 0, 0, 11 }, new int[] { 0, 0, 50 });
                _wss.edit_event_ratio(i, 3, 8);
                _wss.add_event_to_schedule(i, 3, 3);

                _wss.sync_group(i, 170);
                _wss.startStim(i);
            }
            StartStreamingInternal();
        }
        catch (Exception ex)
        {
            _currentSetupTries++;
            Log.Error($"[LegacyStimulationCore] Error in setup. Retrying {_currentSetupTries}/{_maxSetupTries}. {ex.Message}");
            _wss.clearQueue();
            if (_currentSetupTries < _maxSetupTries)
            {
                Initialize(); // re-run full init like Unity script
            }
            else
            {
                _setupRunning = false;
            }
        }
    }
}
