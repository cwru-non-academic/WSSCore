using System;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;
using System.Threading;
using System.Threading.Tasks;

public sealed class WssStimulationCore : IStimulationCore
{
    // ---- transport & config ----
    private readonly StimConfigController _config;
    private readonly bool _testMode;
    private readonly string _comPort = null;
    private readonly string _jsonPath;
    private readonly int _maxSetupTries;
    private readonly int _delayMsBetweenPackets = 12; // minimum wait time between streaming packets due to radio limitations
    private WssClient _wss;

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
    public WssStimulationCore(string comPort, string JSONpath, bool testMode = false, int maxSetupTries = 5)
    {
        _comPort = comPort;
        _jsonPath = JSONpath;
        _config = new StimConfigController(_jsonPath);
        _testMode = testMode;
        _maxSetupTries = maxSetupTries;
    }

    /// <summary>
    /// Lowest-level constructor providing the SerialToWSS instance directly.
    /// </summary>
    public WssStimulationCore(string JSONpath, bool testMode = false, int maxSetupTries = 5)
    {
        _jsonPath = JSONpath;
        _config = new StimConfigController(_jsonPath);
        _testMode = testMode;
        _maxSetupTries = maxSetupTries;
    }

    // ---- lifecycle ----

    public void Initialize()
    {
        if (_ready || _setupRunning)
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

        if (_comPort != null)
        {
            _wss = new WssClient(new SerialPortTransport(_comPort), new WssFrameCodec());
        }
        else
        {
            _wss = new WssClient(new SerialPortTransport(), new WssFrameCodec());
        }

        // run the same default schedule setup as the Unity script
        NormalSetup();
    }

    public void Tick()
    {
        if (_testMode || _wss == null) return;

        // fetch / forward any transport errors
        _wss.checkForErrors();
        if (_wss.msgs.Count > 0)
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
            }
            _wss.msgs.Clear();
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

        if (!_testMode && _wss != null)
        {
            _running = false;
            try { _wss.ZeroOutStim(); } catch { }
            try { _wss.Dispose(); } catch { }
        }

        _ready = false;
        _streamCts?.Cancel();
        _wss = null;
    }

    public void LoadConfigFile()
    {
        _config.LoadJSON();
    }

    public void Dispose() => Shutdown();

    // ---- status ----

    public bool Started() => _testMode ? _running : (_wss?.Started ?? false);
    public bool Ready() => _ready;

    public bool IsModeValid()
    {
        VerifyStimMode();
        return _validMode;
    }

    // ---- streaming / control ----

    public void StreamChange(WssClient.WssTarget target, int[] PA, int[] PW, int[] IPI)
    {
        if (_testMode) return;
        _ready = false;
        _wss.StreamChange(PA, PW, IPI, target);
    }

    public void StimulateAnalog(string finger, bool rawValues, int PW, int amp = 3)
    {
        int channel = FingerToChannel(finger);
        if (channel <= 0 || channel > _maxWSS * 3) return;

        _chAmps[channel - 1] = amp;
        _chPWs[channel - 1] = PW;
    }

    public void ZeroOutStim()
    {
        if (_testMode) return;
        _ready = false;
        _wss.ZeroOutStim();
    }

    public void StartStim(WssClient.WssTarget targetWSS = WssClient.WssTarget.Broadcast)
    {
        if (_testMode)
        {
            _ready = true;
            StartStreamingInternal();
            return;
        }
        if (_wss == null) return;

        _ready = false;
        _wss.StartStim(targetWSS);
        _running = true;
        _setupRunning = false; // ensure setup is not running
        _setupComplete = true; // assume setup is complete after starting
        _currentSetupTries = 0;

        StartStreamingInternal();
    }

    public void StopStim(WssClient.WssTarget targetWSS = WssClient.WssTarget.Broadcast)
    {
        if (_testMode)
        {
            _ready = false;
            _running = false;
            return;
        }
        _ready = false;
        _wss.StopStim(targetWSS);
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

    public void UpdateIPD(int IPD, WssClient.WssTarget targetWSS = WssClient.WssTarget.Broadcast)
    {
        if (_testMode) return;
        _ready = false;
        if (IPD > 1000) IPD = 1000; // handle by the validate inside but if not handle here too currentIPD is not correct
        _currentIPD = IPD;
        _wss.EditEventPw(1, new int[] { 0, 0, _currentIPD }, targetWSS);
        _wss.EditEventPw(2, new int[] { 0, 0, _currentIPD }, targetWSS);
        _wss.EditEventPw(3, new int[] { 0, 0, _currentIPD }, targetWSS);
    }

    public void UpdateFrequency(int FR, WssClient.WssTarget targetWSS = WssClient.WssTarget.Broadcast)
    {
        if (_testMode) return;
        _ready = false;
        if (FR <= 0) throw new ArgumentOutOfRangeException(nameof(FR));
        int periodMs = (int)(1000.0f / FR);
        _wss.StreamChange(null, new int[] { 0, 0, 0 }, new int[] { periodMs, periodMs, periodMs }, targetWSS);
    }

    public void UpdatePeriod(int periodMs, WssClient.WssTarget targetWSS = WssClient.WssTarget.Broadcast)
    {
        if (_testMode) return;
        _ready = false;
        if (periodMs <= 0) throw new ArgumentOutOfRangeException(nameof(periodMs));
        _wss.StreamChange(null, new int[] { 0, 0, 0 }, new int[] { periodMs, periodMs, periodMs }, targetWSS);
    }

    public void UpdateWaveform(int[] waveform, int eventID, WssClient.WssTarget targetWSS = WssClient.WssTarget.Broadcast)
    {
        if (_testMode) return;
        _ready = false;
        WaveformBuilder stimShape = new WaveformBuilder(waveform);
        WaveformSetup(stimShape, eventID, targetWSS);
    }

    public void UpdateWaveform(int cathodicWaveform, int anodicWaveform, int eventID, WssClient.WssTarget targetWSS = WssClient.WssTarget.Broadcast)
    {
        if (_testMode) return;
        _ready = false;
        _wss.EditEventShape(eventID, cathodicWaveform, anodicWaveform, targetWSS);
    }

    public void UpdateWaveform(WaveformBuilder waveform, int eventID, WssClient.WssTarget targetWSS = WssClient.WssTarget.Broadcast)
    {
        if (_testMode) return;
        _ready = false;
        WaveformSetup(waveform, eventID, targetWSS);
    }

    public void LoadWaveform(string fileName, int eventID)
    {
        // Unity-free JSON loading; expects "<fileName>WF.json" in current working directory
        // or an absolute/relative path passed by caller.
        string candidatePath = Path.Combine(_jsonPath, fileName);
        if (!candidatePath.EndsWith("WF.json", StringComparison.OrdinalIgnoreCase))
        {
            candidatePath = Path.Combine(_jsonPath, Path.ChangeExtension(fileName + "WF", "json"));
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

    public void WaveformSetup(WaveformBuilder wave, int eventID, WssClient.WssTarget targetWSS = WssClient.WssTarget.Broadcast)
    {
        if (_testMode) return;
        _wss.SetCustomWaveform(0, wave.getCatShapeArray()[0..^24], 0, targetWSS);
        _wss.SetCustomWaveform(0, wave.getCatShapeArray()[8..^16], 1, targetWSS);
        _wss.SetCustomWaveform(0, wave.getCatShapeArray()[16..^8], 2, targetWSS);
        _wss.SetCustomWaveform(0, wave.getCatShapeArray()[24..^0], 3, targetWSS);

    }

    // ---- setup & edits ----

    public void Save(WssClient.WssTarget targetWSS = WssClient.WssTarget.Broadcast)
    {
        if (_testMode) return;
        _ready = false;
        _wss.PopulateFramSettings(targetWSS);
    }

    public void Load(WssClient.WssTarget targetWSS = WssClient.WssTarget.Broadcast)
    {
        if (_testMode) return;
        _ready = false;
        _wss.PopulateBoardSettings(targetWSS);
    }

    public void Request_Configs(int command, int id, WssClient.WssTarget targetWSS = WssClient.WssTarget.Broadcast)
    {
        if (_testMode) return;
        _ready = false;
        _wss.RequestConfigs(command, id, targetWSS);
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
                            _wss.StreamChange(
                                new int[] { AmpTo255Convention(_chAmps[baseIdx + 0]), AmpTo255Convention(_chAmps[baseIdx + 1]), AmpTo255Convention(_chAmps[baseIdx + 2]) },
                                new int[] { _chPWs[baseIdx + 0], _chPWs[baseIdx + 1], _chPWs[baseIdx + 2] },
                                null, IntToWssTarget(w));
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

    private WssClient.WssTarget IntToWssTarget(int i)
    {
        switch (i)
        {
            case 0: return WssClient.WssTarget.Broadcast;
            case 1: return WssClient.WssTarget.Wss1;
            case 2: return WssClient.WssTarget.Wss2;
            case 3: return WssClient.WssTarget.Wss3;
            default: return WssClient.WssTarget.Wss1;
        }
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
                WssClient.WssTarget targetWSS = IntToWssTarget(i);
                // mirror Unity setup
                _wss.Clear(0, targetWSS);
                _wss.CreateSchedule(1, 13, 170, targetWSS);
                _wss.CreateContactConfig(1, new int[] { 1, 2, 0, 0 }, new int[] { 2, 1, 0, 0 }, targetWSS);
                _wss.CreateEvent(1, 0, 1, 0, 0, new int[] { 11, 11, 0, 0 }, new int[] { 11, 11, 0, 0 }, new int[] { 0, 0, 50 }, targetWSS);
                _wss.EditEventRatio(1, 8, targetWSS);
                _wss.AddEventToSchedule(1, 1, targetWSS);

                _wss.CreateSchedule(2, 13, 170, targetWSS);
                _wss.CreateContactConfig(2, new int[] { 1, 0, 2, 0 }, new int[] { 2, 0, 1, 0 }, targetWSS);
                _wss.CreateEvent(2, 2, 2, 0, 0, new int[] { 11, 0, 11, 0 }, new int[] { 11, 0, 11, 0 }, new int[] { 0, 0, 50 }, targetWSS);
                _wss.EditEventRatio(2, 8, targetWSS);
                _wss.AddEventToSchedule(2, 2, targetWSS);

                _wss.CreateSchedule(3, 13, 170, targetWSS);
                _wss.CreateContactConfig(3, new int[] { 1, 0, 0, 2 }, new int[] { 2, 0, 0, 1 }, targetWSS);
                _wss.CreateEvent(3, 4, 3, 0, 0, new int[] { 11, 0, 0, 11 }, new int[] { 11, 0, 0, 11 }, new int[] { 0, 0, 50 }, targetWSS);
                _wss.EditEventRatio(3, 8, targetWSS);
                _wss.AddEventToSchedule(3, 3, targetWSS);

                _wss.SyncGroup(170, targetWSS);
                _wss.StartStim(targetWSS);
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

