using System;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

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
    private CoreState _state = CoreState.Disconnected;
    private int _currentSetupTries;
    private bool _validMode;
    private readonly Dictionary<WssTarget, int> _setupCursor = new(); // per-target step index
    private int _maxWSS = 1;
    private readonly Stopwatch _timer = new Stopwatch();

    // background tasks (used to send messages at the radio's update rate, setup, and connect)
    private CancellationTokenSource _streamCts;
    private Task _streamTask;
    private Task _connectTask;
    private Task _setupTask;

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
        if (_state == CoreState.Ready || _state == CoreState.SettingUp)
        {
            Shutdown();
        }
        // reset high-level flags
        _currentSetupTries = 0;
        _validMode = false;

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

        _wss = _comPort != null
            ? new WssClient(new SerialPortTransport(_comPort), new WssFrameCodec())
            : new WssClient(new SerialPortTransport(), new WssFrameCodec());

        _state = CoreState.Connecting;
        _connectTask = _wss.ConnectAsync();
    }

    public void Tick()
    {
        switch (_state)
        {
            case CoreState.Connecting:
                if (_testMode) return;
                if (_connectTask == null)
                    _connectTask = _wss.ConnectAsync();
                else if (_connectTask.IsFaulted)
                {
                    Log.Error("Connect failed: " + _connectTask.Exception?.GetBaseException().Message);
                    _state = CoreState.Error;
                }
                else if (_connectTask.IsCompletedSuccessfully)
                {
                    _state = CoreState.SettingUp;
                }
                break;

            case CoreState.SettingUp:
                if (_setupTask == null) _setupTask = Task.Run(()
                    => RunNormalSetupResume());
                else if (_setupTask.IsFaulted)
                {
                    Log.Error("Setup failed: " + _setupTask.Exception?.GetBaseException().Message);
                    _setupTask = null;
                    if (++_currentSetupTries > _maxSetupTries) _state = CoreState.Error;
                }
                else if (_setupTask.IsCompletedSuccessfully && ((Task<bool>)_setupTask).Result)
                {
                    _state = CoreState.Ready;
                }
                break;

            case CoreState.Ready:
                StartStreamingInternal();
                _state = CoreState.Streaming;
                break;

            case CoreState.Error:
                // optional backoff/retry policy
                StopStreamingInternal();
                SafeDisconnect();
                break;

            default:
                break;
        }
    }

    public void Shutdown()
    {
        StopStreamingInternal();
        if (!_testMode && _wss != null)
        {
            try { _wss.ZeroOutStim(); } catch { }
            SafeDisconnect();
            try { _wss.Dispose(); } catch { }
        }

        _state = CoreState.Disconnected;
        _streamCts?.Cancel();
        _wss = null;
    }

    public void LoadConfigFile()
    {
        _config.LoadJSON();
    }

    public void Dispose() => Shutdown();

    // ---- status ----
    public bool Started() => _testMode ? _state == CoreState.Streaming : (_wss?.Started ?? false);
    public bool Ready()
    {
        switch (_state)
        {
            case CoreState.Ready: return true;
            case CoreState.Streaming: return true;
            default: return false;
        }
    }

    public bool IsModeValid()
    {
        VerifyStimMode();
        return _validMode;
    }

    // ---- streaming / control ----

    public void StreamChange(int[] PA, int[] PW, int[] IPI, WssTarget target)
    {
        if (_testMode) return;
        _wss.StreamChange(PA, PW, IPI, target);
    }

    public void StimulateAnalog(string finger, bool rawValues, int PW, int amp = 3)
    {
        int channel = FingerToChannel(finger);
        if (channel <= 0 || channel > _maxWSS * 3) return;

        _chAmps[channel - 1] = amp;
        _chPWs[channel - 1] = PW;
    }

    public void ZeroOutStim(WssTarget wsstarget = WssTarget.Broadcast)
    {
        if (_testMode) return;
        _wss.ZeroOutStim(wsstarget);
    }

    public void StartStim(WssTarget targetWSS = WssTarget.Broadcast)
    {
        if (_wss == null) return;

        if (_state == CoreState.Ready)
        {
            if (!_testMode) _wss.StartStim(targetWSS);
            _currentSetupTries = 0;
            StartStreamingInternal();
        }
        
    }

    public void StopStim(WssTarget targetWSS = WssTarget.Broadcast)
    {
        if (_wss == null) return;

        switch (_state)
        {
            case CoreState.SettingUp:
                if (!_testMode) _wss.StopStim(targetWSS);
                break;
            case CoreState.Ready:
                if (!_testMode) _wss.StopStim(targetWSS);
                break;
            case CoreState.Streaming:
                if (!_testMode) _wss.StopStim(targetWSS);
                StopStreamingInternal();
                _state = CoreState.Ready;
                break;
            default:
                break;
        }
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

    public void UpdateIPD(int IPD, WssTarget targetWSS = WssTarget.Broadcast)
    {
        if (_testMode) return;
        if (_state == CoreState.Streaming)
        {
            StopStreamingInternal();
            _state = CoreState.SettingUp;
        }
        if (IPD > 1000) IPD = 1000; // handle by the validate inside but if not handle here too currentIPD is not correct
        _currentIPD = IPD;
        _wss.EditEventPw(1, new int[] { 0, 0, _currentIPD }, targetWSS);
        _wss.EditEventPw(2, new int[] { 0, 0, _currentIPD }, targetWSS);
        _wss.EditEventPw(3, new int[] { 0, 0, _currentIPD }, targetWSS);
    }

    public void UpdateFrequency(int FR, WssTarget targetWSS = WssTarget.Broadcast)
    {
        if (_testMode) return;
        if (_state == CoreState.Streaming)
        {
            StopStreamingInternal();
            _state = CoreState.SettingUp;
        }
        if (FR <= 0) throw new ArgumentOutOfRangeException(nameof(FR));
        int periodMs = (int)(1000.0f / FR);
        _wss.StreamChange(null, new int[] { 0, 0, 0 }, new int[] { periodMs, periodMs, periodMs }, targetWSS);
    }

    public void UpdatePeriod(int periodMs, WssTarget targetWSS = WssTarget.Broadcast)
    {
        if (_testMode) return;
        if (_state == CoreState.Streaming)
        {
            StopStreamingInternal();
            _state = CoreState.SettingUp;
        }
        if (periodMs <= 0) throw new ArgumentOutOfRangeException(nameof(periodMs));
        _wss.StreamChange(null, new int[] { 0, 0, 0 }, new int[] { periodMs, periodMs, periodMs }, targetWSS);
    }

    public void UpdateWaveform(int[] waveform, int eventID, WssTarget targetWSS = WssTarget.Broadcast)
    {
        if (_testMode) return;
        if (_state == CoreState.Streaming)
        {
            StopStreamingInternal();
            _state = CoreState.SettingUp;
        }
        WaveformBuilder stimShape = new WaveformBuilder(waveform);
        WaveformSetup(stimShape, eventID, targetWSS);
    }

    public void UpdateWaveform(int cathodicWaveform, int anodicWaveform, int eventID, WssTarget targetWSS = WssTarget.Broadcast)
    {
        if (_testMode) return;
        if (_state == CoreState.Streaming)
        {
            StopStreamingInternal();
            _state = CoreState.SettingUp;
        }
        _wss.EditEventShape(eventID, cathodicWaveform, anodicWaveform, targetWSS);
    }

    public void UpdateWaveform(WaveformBuilder waveform, int eventID, WssTarget targetWSS = WssTarget.Broadcast)
    {
        if (_testMode) return;
        if (_state == CoreState.Streaming)
        {
            StopStreamingInternal();
            _state = CoreState.SettingUp;
        }
        WaveformSetup(waveform, eventID, targetWSS);
    }

    public void LoadWaveform(string fileName, int eventID)
    {
        // Unity-free JSON loading; expects "<fileName>WF.json" in current working directory
        // or an absolute/relative path passed by caller.
        string candidatePath = Path.Combine(_jsonPath, fileName);
        if (!candidatePath.EndsWith("WF.json", StringComparison.OrdinalIgnoreCase))
        {
            var stem = Path.GetFileNameWithoutExtension(fileName) + "WF";
            candidatePath = Path.Combine(_jsonPath, Path.ChangeExtension(stem, "json"));
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

    public void WaveformSetup(WaveformBuilder wave, int eventID, WssTarget targetWSS = WssTarget.Broadcast)
    {
        if (_testMode) return;
        _wss.SetCustomWaveform(0, wave.getCatShapeArray()[0..^24], 0, targetWSS);
        _wss.SetCustomWaveform(0, wave.getCatShapeArray()[8..^16], 1, targetWSS);
        _wss.SetCustomWaveform(0, wave.getCatShapeArray()[16..^8], 2, targetWSS);
        _wss.SetCustomWaveform(0, wave.getCatShapeArray()[24..^0], 3, targetWSS);

    }

    // ---- setup & edits ----

    public void Save(WssTarget targetWSS = WssTarget.Broadcast)
    {
        if (_testMode) return;
        if (_state == CoreState.Streaming)
        {
            StopStreamingInternal();
            _state = CoreState.SettingUp;
        }
        _wss.PopulateFramSettings(targetWSS);
    }

    public void Load(WssTarget targetWSS = WssTarget.Broadcast)
    {
        if (_testMode) return;
        if (_state == CoreState.Streaming)
        {
            StopStreamingInternal();
            _state = CoreState.SettingUp;
        }
        _wss.PopulateBoardSettings(targetWSS);
    }

    public void Request_Configs(int command, int id, WssTarget targetWSS = WssTarget.Broadcast)
    {
        if (_testMode) return;
        if (_state == CoreState.Streaming)
        {
            StopStreamingInternal();
            _state = CoreState.SettingUp;
        }
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
        if (_streamTask is { IsCompleted: false }) return;
        _streamCts = new CancellationTokenSource();
        var tk = _streamCts.Token;

        _streamTask = Task.Run(async () =>
        {
            while (!tk.IsCancellationRequested)
            {
                // WSS 1..N
                for (int w = 1; w <= _maxWSS; w++)
                {
                    int baseIdx = (w - 1) * 3;
                    if (!_testMode)
                    {
                        // fire-and-forget; no reply expected
                        _=_wss.StreamChange(
                            new int[] { AmpTo255Convention(_chAmps[baseIdx + 0]), AmpTo255Convention(_chAmps[baseIdx + 1]), AmpTo255Convention(_chAmps[baseIdx + 2]) },
                            new int[] { _chPWs[baseIdx + 0], _chPWs[baseIdx + 1], _chPWs[baseIdx + 2] },
                            null, IntToWssTarget(w));
                    }
                    await Task.Delay(_delayMsBetweenPackets, tk);
                }
            }
        }, tk);
    }

    private void StopStreamingInternal()
    {
        _streamCts?.Cancel();
        try { _streamTask?.Wait(250); } catch { }
        _streamTask = null;
    }

    private void SafeDisconnect()
    {
        try { _wss?.DisconnectAsync().GetAwaiter().GetResult(); } catch { }
    }

    private WssTarget IntToWssTarget(int i)
    {
        switch (i)
        {
            case 0: return WssTarget.Broadcast;
            case 1: return WssTarget.Wss1;
            case 2: return WssTarget.Wss2;
            case 3: return WssTarget.Wss3;
            default: return WssTarget.Wss1;
        }
    }

    /// <summary>
    /// Sends the setup sequence and remember where it failed
    /// </summary>
    private bool RunNormalSetupResume()
    {
        _state = CoreState.SettingUp;

        try
        {
            int count = Math.Max(1, Math.Min(_maxWSS, 3)); // your IntToWssTarget supports 1..3
            for (int i = 1; i <= count; i++)
            {
                var t = IntToWssTarget(i);
                var steps = BuildSetupSteps(t);

                // where did we fail last time for this target?
                if (!_setupCursor.TryGetValue(t, out var cursor)) cursor = 0;

                // run from the cursor forward; after each success advance the cursor
                for (; cursor < steps.Count; cursor++)
                {
                    _setupCursor[t] = cursor;                 // remember the step we are about to run
                    steps[cursor]();                          // runs and throws on error/timeout
                }

                // finished this target successfully
                _setupCursor[t] = steps.Count;                // mark as done
            }
            _state = CoreState.Ready;
            StartStreamingInternal();
            return true;
        }
        catch (Exception ex)
        {
            _currentSetupTries++;
            Log.Error($"[WssStimulationCore] Setup error at step #{_setupCursor.Values.DefaultIfEmpty(0).Max()}: {ex.Message} (retry {_currentSetupTries}/{_maxSetupTries})");

            // Leave _setupCursor as-is so the next call resumes at the failed step.
            if (_currentSetupTries < _maxSetupTries)
                return false; // caller can invoke ResumeAllTargets() again (or you can schedule it)
            _state = CoreState.Error; //give up until reset
            return false;
        }
    }

    private static string RequireOk(Task<string> op, string name)
    {
        try
        {
            var res = op.GetAwaiter().GetResult();
            if (res.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{name} failed: {res}");
            return res; // optional: Log.Info($"{name}: {res}");
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"{name} timed out (no reply).");
        }
    }

    private List<Func<string>> BuildSetupSteps(WssTarget t)
    {
        return new List<Func<string>>
        {
            () => RequireOk(_wss.Clear(0, t),                      $"Clear[{t}]"),
            () => RequireOk(_wss.CreateSchedule(1, 13, 170, t),    $"CreateSchedule#1[{t}]"),
            () => RequireOk(_wss.CreateContactConfig(1, new[]{0,0,2,1}, new[]{0,0,1,2}, t), $"CreateContactConfig#1[{t}]"),
            () => RequireOk(_wss.CreateEvent(1, 0, 1, 11, 11, new[]{11,11,0,0}, new[]{11,11,0,0}, new[]{0,0,_currentIPD}, t), $"CreateEvent#1[{t}]"),
            () => RequireOk(_wss.EditEventRatio(1, 8, t),          $"EditEventRatio#1[{t}]"),
            () => RequireOk(_wss.AddEventToSchedule(1, 1, t),      $"AddEventToSchedule#1[{t}]"),

            () => RequireOk(_wss.CreateSchedule(2, 13, 170, t),    $"CreateSchedule#2[{t}]"),
            () => RequireOk(_wss.CreateContactConfig(2, new[]{0,2,0,1}, new[]{0,1,0,2}, t), $"CreateContactConfig#2[{t}]"),
            () => RequireOk(_wss.CreateEvent(2, 2, 2, 11, 11, new[]{11,0,11,0}, new[]{11,0,11,0}, new[]{0,0,_currentIPD}, t), $"CreateEvent#2[{t}]"),
            () => RequireOk(_wss.EditEventRatio(2, 8, t),          $"EditEventRatio#2[{t}]"),
            () => RequireOk(_wss.AddEventToSchedule(2, 2, t),      $"AddEventToSchedule#2[{t}]"),

            () => RequireOk(_wss.CreateSchedule(3, 13, 170, t),    $"CreateSchedule#3[{t}]"),
            () => RequireOk(_wss.CreateContactConfig(3, new[]{2,0,0,1}, new[]{1,0,0,2}, t), $"CreateContactConfig#3[{t}]"),
            () => RequireOk(_wss.CreateEvent(3, 4, 3, 11, 11, new[]{11,0,0,11}, new[]{11,0,0,11}, new[]{0,0,_currentIPD}, t), $"CreateEvent#3[{t}]"),
            () => RequireOk(_wss.EditEventRatio(3, 8, t),          $"EditEventRatio#3[{t}]"),
            () => RequireOk(_wss.AddEventToSchedule(3, 3, t),      $"AddEventToSchedule#3[{t}]"),

            () => RequireOk(_wss.SyncGroup(170, t),                $"SyncGroup[{t}]"),
            () => RequireOk(_wss.StartStim(t),                     $"StartStim[{t}]"), // "Start Acknowledged"
        };
    }

    private void ResetSetupProgress()
    {
        _setupCursor.Clear();
    }

    enum CoreState { Disconnected, Connecting, SettingUp, Ready, Streaming, Error }

}

