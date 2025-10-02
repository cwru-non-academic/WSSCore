using System;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Unity-agnostic WSS stimulation core that manages connection, setup (via a queued step runner),
/// and a background streaming loop. Public mutator methods enqueue device edits and return immediately.
/// </summary>
public sealed class WssStimulationCore : IStimulationCore
{
    #region ========== Fields & nested types ==========
    // ---- transport & config ----
    private readonly StimConfigController _config;
    private readonly bool _testMode;
    private readonly string _comPort = null;
    private readonly string _jsonPath;
    private readonly int _maxSetupTries;
    private readonly int _delayMsBetweenPackets = 12; // radio throttling
    private WssClient _wss;
    private bool _resumeStreamingAfter;

    // ---- runtime state ----
    private CoreState _state = CoreState.Disconnected;
    private int _currentSetupTries;
    private bool _validMode;
    private readonly Dictionary<WssTarget, int> _cursor = new(); // per-target step index
    private readonly Dictionary<WssTarget, List<Func<Task<string>>>> _steps = new();
    private int _maxWSS = 1;
    private WSSVersionHandler _WssVersionHandler;
    private readonly Stopwatch _timer = new Stopwatch();
    private readonly SemaphoreSlim _setupGate = new(1, 1);

    // ---- background tasks ----
    private CancellationTokenSource _streamCts;
    private Task _streamTask;
    private Task _connectTask;
    private Task _setupRunner;

    // ---- channels & controller state ----
    private float[] _chAmps;  // mA (mapped to 0..255 for device)
    private int[] _chPWs;   // us
    private int _currentIPD = 50; // us
    private float[] _prevMagnitude;
    private float[] _currentMag;
    private float[] _d_dt;
    private float[] _dt;

    private enum CoreState { Disconnected, Connecting, SettingUp, Ready, Started, Streaming, Error }
    #endregion

    #region ========== Construction ==========
    /// <summary>Construct with explicit COM port.</summary>
    public WssStimulationCore(string comPort, string JSONpath, bool testMode = false, int maxSetupTries = 5)
    {
        _comPort = comPort;
        _jsonPath = JSONpath;
        _config = new StimConfigController(_jsonPath);
        _testMode = testMode;
        _maxSetupTries = maxSetupTries;
    }

    /// <summary>Construct without specifying COM port (let transport decide).</summary>
    public WssStimulationCore(string JSONpath, bool testMode = false, int maxSetupTries = 5)
    {
        _jsonPath = JSONpath;
        _comPort = null;
        _config = new StimConfigController(_jsonPath);
        _testMode = testMode;
        _maxSetupTries = maxSetupTries;
    }
    #endregion

    #region ========== Lifecycle (Initialize / Tick / Shutdown) ==========
    /// <summary>
    /// Initializes the core: loads JSON config, prepares buffers, and begins connecting
    /// to the WSS transport. Non-blocking; call <see cref="Tick"/> each frame to advance.
    /// </summary>
    public void Initialize()
    {
        if (_state is CoreState.Ready or CoreState.SettingUp) Shutdown();

        _currentSetupTries = 0;
        _validMode = false;

        // (re)load app config
        _config.LoadJSON();
        _maxWSS = _config._config.maxWSS;
        _WssVersionHandler = new WSSVersionHandler(_config._config.WSSFirmwareVersion);

        _timer.Reset();
        _timer.Start();

        InitStimArrays();
        VerifyStimMode();

        //if test mode use fake transport, otheriwse use a serial transport with port if given or auto method if not given.
        if (_testMode)
        {
            _wss = new WssClient(new TestModeTransport(), new WssFrameCodec());
        } else {
            if (_comPort != null)
            {
                _wss = new WssClient(new SerialPortTransport(_comPort), new WssFrameCodec());
            } else {
                _wss = new WssClient(new SerialPortTransport(),      new WssFrameCodec());
            }
        }
        _state = CoreState.Connecting;
        _connectTask = _wss.ConnectAsync();
    }

    /// <summary>
    /// Advances the internal state machine (Connecting → SettingUp → Ready → Streaming).
    /// Call regularly from your main loop (e.g., Unity Update). Non-blocking.
    /// </summary>
    public void Tick()
    {
        switch (_state)
        {
            case CoreState.Connecting:
                if (_connectTask == null) _connectTask = _wss.ConnectAsync();
                else if (_connectTask.IsFaulted)
                {
                    Log.Error("Connect failed: " + _connectTask.Exception?.GetBaseException().Message);
                    _state = CoreState.Error;
                }
                else if (_connectTask.IsCompletedSuccessfully)
                {
                    NormalSetup();                // seed all targets once
                    _state = CoreState.SettingUp;
                }
                break;

            case CoreState.SettingUp:
                // Start or keep the runner. If a pass finished but queue isn't empty (new steps arrived),
                // restart the runner to drain remaining work.
                if (_setupRunner == null || (_setupRunner.IsCompleted && !SetupQueueEmpty()))
                    EnsureSetupRunner();
                else if (_setupRunner.IsFaulted)
                {
                    Log.Error("Setup failed: " + _setupRunner.Exception?.GetBaseException().Message);
                    _setupRunner = null;
                    if (++_currentSetupTries > _maxSetupTries) _state = CoreState.Error;
                }
                else if (_setupRunner.IsCompleted && SetupQueueEmpty())
                {
                    _state = CoreState.Ready;
                }
                break;

            case CoreState.Ready:
                if (_wss.Started)
                {
                    _state = CoreState.Started;
                }
                break;

            case CoreState.Started:
                StartStreamingInternal();
                _state = CoreState.Streaming;
                break;

            case CoreState.Streaming:
                // background streaming task is running; Tick has nothing to do
                break;

            case CoreState.Error:
                StopStreamingInternal();
                SafeDisconnect();
                break;
        }
    }

    /// <summary>
    /// Stops streaming, zeroes outputs (best-effort), disconnects the transport,
    /// and releases resources. Safe to call multiple times.
    /// </summary>
    public void Shutdown()
    {
        StopStreamingInternal();
        if (_wss != null)
        {
            try { _wss.ZeroOutStim(); } catch { }
            SafeDisconnect();
            try { _wss.Dispose(); } catch { }
        }

        _state = CoreState.Disconnected;
        _streamCts?.Cancel();
        _wss = null;
    }

    /// <summary>
    /// Reloads the stimulation JSON configuration from disk into memory.
    /// </summary>
    public void LoadConfigFile() => _config.LoadJSON();

    /// <summary>Disposes the core by calling <see cref="Shutdown"/>.</summary>
    public void Dispose() => Shutdown();
    #endregion

    #region ========== Status ==========
    /// <summary>True when device transport is started or streaming.</summary>
    public bool Started() => _state is CoreState.Started or CoreState.Streaming;

    /// <summary>
    /// True when the core is Ready. Use this to decide when it’s safe to send
    /// start stimulation.
    /// </summary>
    public bool Ready() => _state is CoreState.Ready;

    /// <summary>
    /// Validates the current sensation controller mode from config (P or PD).
    /// </summary>
    /// <returns>True if the mode is supported; otherwise false.</returns>
    public bool IsModeValid()
    {
        VerifyStimMode();
        return _validMode;
    }
    #endregion

    #region ========== Public control API (non-blocking) ==========
    /// <summary>
    /// Sends a streaming change packet (PA/PW/IPI). Fire-and-forget; no reply is expected.
    /// Does not pause streaming or setup.
    /// </summary>
    /// <param name="PA">Per-channel amplitudes (device 0–255 mapping) or null to leave unchanged.</param>
    /// <param name="PW">Per-channel pulse widths (device units) or null to leave unchanged.</param>
    /// <param name="IPI">Per-channel periods (ms) or null to leave unchanged.</param>
    /// <param name="target">Which WSS to address (or Broadcast).</param>
    public void StreamChange(int[] PA, int[] PW, int[] IPI, WssTarget target)
    {
        _ = _wss.StreamChange(PA, PW, IPI, target); // discard task intentionally
    }

    /// <summary>
    /// Updates the cached amplitude and PW for a logical finger/channel, using your
    /// analog mapping. No device I/O occurs here; the streaming loop will pick it up.
    /// </summary>
    /// <param name="finger">Finger name (e.g., "Thumb", "Index") or "ch#".</param>
    /// <param name="PW">Pulse width to cache.</param>
    /// <param name="amp">Amplitude (mA domain, will be mapped to device scale during streaming).</param>
    public void StimulateAnalog(string finger, int PW, float amp)
    {
        int channel = FingerToChannel(finger);
        if (channel <= 0 || channel > _maxWSS * 3) return;

        _chAmps[channel - 1] = amp;
        _chPWs[channel - 1] = PW;
    }

    /// <summary>
    /// Computes a PW from the given magnitude using the configured controller (P/PD)
    /// and updates the cached amp/PW for the addressed channel.
    /// </summary>
    /// <param name="finger">Finger name (or "ch#"). Note: <c>FingerToChannel</c> returns 1-based (Thumb is Ch1);
    /// arrays use <c>channel-1</c>.</param>
    /// <param name="magnitude">Normalized input (typically 0..1) used by the controller.</param>
    public void StimWithMode(string finger, float magnitude)
    {
        int channel = FingerToChannel(finger);
        int total = _maxWSS * 3;
        if ((uint)channel <= 0 || (uint)channel > (uint)total) return;   // fast bounds check

        _chAmps[channel - 1] = (int)_config.getStimParam($"Ch{channel}Amp");
        _chPWs[channel - 1] = CalculateStim(channel, magnitude,
                                            _config.getStimParam($"Ch{channel}Max"),
                                            _config.getStimParam($"Ch{channel}Min"));
    }

    /// <summary>
    /// Sends a zero-out command to the device (fire-and-forget). Does not alter cached values.
    /// </summary>
    /// <param name="wsstarget">Target device or Broadcast.</param>
    public void ZeroOutStim(WssTarget wsstarget = WssTarget.Broadcast)
    {
        _ = _wss.ZeroOutStim(wsstarget);
    }

    /// <summary>
    /// Starts stimulation on the target and launches the background streaming loop
    /// when the core is <see cref="CoreState.Ready"/>.
    /// </summary>
    /// <param name="targetWSS">Target device or Broadcast.</param>
    public void StartStim(WssTarget targetWSS = WssTarget.Broadcast)
    {
        if (_wss == null) return;
        switch (_state)
        {
            case CoreState.Ready:
                _ = ScheduleSetupChangeAsync(targetWSS,
                    () => StepLogger(_wss.StartStim(targetWSS),      $"StartStim[{targetWSS}]")
                );
                _currentSetupTries = 0;
                break;
            case CoreState.Started:
            case CoreState.Streaming:
                if (Started())
                {
                    Log.Info("WSS already started, to force restart call reset radio, or stop stim and start again.");
                } else {
                    Log.Error("Stim is not started, but it originally passed the started test. Reseting radio now");
                    Shutdown();
                    Initialize();
                }
                break;
            case CoreState.Connecting:
            case CoreState.SettingUp:
                Log.Warn("State must be " + CoreState.Ready.ToString() + " and it is currently " + _state.ToString());
                break;
            case CoreState.Disconnected:
            case CoreState.Error:
                Log.Warn("Wss is disconeted or error out. Trying to stablish connection");
                Shutdown();
                Initialize();
                break;
        }
    }

    /// <summary>
    /// Stops stimulation on the target. If currently Streaming, also stops the background
    /// streaming loop and returns the core to Ready.
    /// </summary>
    /// <param name="targetWSS">Target device or Broadcast.</param>
    public void StopStim(WssTarget targetWSS = WssTarget.Broadcast)
    {
        if (_wss == null) return;

        switch (_state)
        {
            case CoreState.SettingUp:
            case CoreState.Ready:
            case CoreState.Started:
                _ = ScheduleSetupChangeAsync(targetWSS,
                    () => StepLogger(_wss.StopStim(targetWSS),      $"StopStim[{targetWSS}]")
                );
                break;
            case CoreState.Streaming:
                _ = ScheduleSetupChangeAsync(targetWSS,
                    () => StepLogger(_wss.StopStim(targetWSS),      $"StopStim[{targetWSS}]")
                );
                StopStreamingInternal();
                break;
        }
    }

    /// <summary>
    /// Updates the in-memory channel parameters (Max/Min/Amp) in the JSON-backed config.
    /// Does not push changes to the device.
    /// </summary>
    /// <param name="finger">Finger name (or "ch#").</param>
    /// <param name="max">Max PW used by the controller mapping.</param>
    /// <param name="min">Min PW used by the controller mapping.</param>
    /// <param name="amp">Amplitude (mA domain) cached for streaming.</param>
    public void UpdateChannelParams(string finger, int max, int min, float amp)
    {
        int channel = FingerToChannel(finger);
        if (channel <= 0 || channel > _maxWSS * 3) return;

        _config.modifyStimParam($"Ch{channel}Amp", amp);
        _config.modifyStimParam($"Ch{channel}Max", max);
        _config.modifyStimParam($"Ch{channel}Min", min);
    }

    /// <summary>
    /// Updates inter-phase delay (IPD) for events 1–3 via setup commands (with replies).
    /// If currently Streaming, the core pauses streaming, sends edits, and resumes when done.
    /// </summary>
    /// <param name="IPD">Inter-phase delay in microseconds (clamped internally).</param>
    /// <param name="targetWSS">Target device or Broadcast.</param>
    public void UpdateIPD(int IPD, WssTarget targetWSS = WssTarget.Broadcast)
    {
        _currentIPD = Math.Clamp(IPD, 1, 1000);

        _ = ScheduleSetupChangeAsync(targetWSS,
            () => StepLogger(_wss.EditEventPw(1, new[] { 0, 0, _currentIPD }, targetWSS),      $"UpdateIPD[{targetWSS}], Event[1]"),
            () => StepLogger(_wss.EditEventPw(2, new[] { 0, 0, _currentIPD }, targetWSS),      $"UpdateIPD[{targetWSS}], Event[2]"),
            () => StepLogger(_wss.EditEventPw(3, new[] { 0, 0, _currentIPD }, targetWSS),      $"UpdateIPD[{targetWSS}], Event[3]")
        );
    }

    /// <summary>
    /// Applies a target stimulation <em>frequency</em> (in Hertz) to a single logical channel
    /// by converting it to a period in milliseconds and delegating to <see cref="UpdatePeriod(string,int,WssTarget)"/>.
    /// </summary>
    /// <param name="finger">
    /// Logical channel selector. Accepts named fingers (e.g., <c>"Thumb"</c>, <c>"Index"</c>, …)
    /// or a direct channel token like <c>"ch1"</c>. The mapping is resolved by <see cref="FingerToChannel(string)"/>.
    /// </param>
    /// <param name="FR">
    /// Frequency in Hertz. Must be greater than zero. The method computes
    /// <c>periodMs = (int)(1000.0 / FR)</c> (integer truncation toward zero).
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="FR"/> is less than or equal to zero.
    /// </exception>
    public void UpdateFrequency(string finger, int FR)
    {
        if (FR <= 0) throw new ArgumentOutOfRangeException(nameof(FR));
        int periodMs = (int)(1000.0f / FR);
        UpdatePeriod(finger, periodMs);
    }

    /// <summary>
    /// Applies a target stimulation <em>period</em> (inter-pulse interval, IPI, in milliseconds)
    /// to a single logical channel by persisting the value to the loaded JSON-backed configuration.
    /// </summary>
    /// <param name="finger">
    /// Logical channel selector. Accepts named fingers (e.g., <c>"Thumb"</c>, <c>"Index"</c>, …)
    /// or a direct channel token like <c>"ch1"</c>. The mapping is resolved by <see cref="FingerToChannel(string)"/>.
    /// </param>
    /// <param name="periodMs">
    /// Desired period (IPI) in milliseconds. Must be greater than zero.
    /// </param>
    public void UpdatePeriod(string finger, int periodMs)
    {
        if (periodMs <= 0) throw new ArgumentOutOfRangeException(nameof(periodMs));
        int channel = FingerToChannel(finger);
        int total = _maxWSS * 3;
        if ((uint)channel <= 0 || (uint)channel > (uint)total) return;   // fast bounds check

        _config.modifyStimParam($"Ch{channel}IPI", periodMs);
        //_wss.StreamChange(null, new int[] { 0, 0, 0 }, new int[] { periodMs, periodMs, periodMs }, targetWSS); TODO
    }

    /// <summary>
    /// Builds a custom waveform from raw points and enqueues the necessary upload steps
    /// (with replies). If Streaming, the core pauses, uploads, then resumes.
    /// </summary>
    /// <param name="waveform">Concatenated waveform definition used by your <see cref="WaveformBuilder"/>.</param>
    /// <param name="eventID">Event ID that will reference the uploaded shapes.</param>
    /// <param name="targetWSS">Target device or Broadcast.</param>
    public void UpdateWaveform(int[] waveform, int eventID, WssTarget targetWSS = WssTarget.Broadcast)
    {
        WaveformSetup(new WaveformBuilder(waveform), eventID, targetWSS);
    }

    /// <summary>
    /// Enqueues a prebuilt waveform upload and event shape assignment (with replies).
    /// If Streaming, the core pauses, uploads, then resumes.
    /// </summary>
    /// <param name="waveform">Prepared <see cref="WaveformBuilder"/>.</param>
    /// <param name="eventID">Event ID to point at the uploaded shapes.</param>
    /// <param name="targetWSS">Target device or Broadcast.</param>
    public void UpdateWaveform(WaveformBuilder waveform, int eventID, WssTarget targetWSS = WssTarget.Broadcast)
    {
        WaveformSetup(waveform, eventID, targetWSS);
    }

    /// <summary>
    /// Enqueues setting the event shape IDs directly (with replies). If Streaming, the core pauses,
    /// sends the edit, then resumes.
    /// </summary>
    /// <param name="cathodicWaveform">Shape ID for standard phase.</param>
    /// <param name="anodicWaveform">Shape ID for recharge phase.</param>
    /// <param name="eventID">Event ID to modify.</param>
    /// <param name="targetWSS">Target device or Broadcast.</param>
    public void UpdateEventShape(int cathodicWaveform, int anodicWaveform, int eventID, WssTarget targetWSS = WssTarget.Broadcast)
    {
        _ = ScheduleSetupChangeAsync(targetWSS,
            () => StepLogger(_wss.EditEventShape(eventID, cathodicWaveform, anodicWaveform, targetWSS),      $"UpdateShape[{targetWSS}], Event[{eventID}]")
        );
    }

    /// <summary>
    /// Loads a waveform JSON file (…WF.json) from <c>_jsonPath</c>, builds it, and enqueues
    /// an upload. Logs an error if the file cannot be read or deserialized.
    /// </summary>
    /// <param name="fileName">File name or path (WF suffix enforced).</param>
    /// <param name="eventID">Event ID to point at the uploaded shapes.</param>
    public void LoadWaveform(string fileName, int eventID)
    {
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
            Log.Error($"[WssStimulationCore] JSON loading error: {ex.Message} ({candidatePath})");
        }
    }

    /// <summary>
    /// Saves the board settings to FRAM via a setup command (with reply). If Streaming,
    /// the core pauses, saves, then resumes.
    /// </summary>
    /// <param name="targetWSS">Target device or Broadcast.</param>
    public void Save(WssTarget targetWSS = WssTarget.Broadcast)
    {
        _ = ScheduleSetupChangeAsync(targetWSS, () => StepLogger(_wss.PopulateFramSettings(targetWSS), $"SaveChanges[{targetWSS}]"));
    }

    /// <summary>
    /// Loads the board settings from FRAM via a setup command (with reply). If Streaming,
    /// the core pauses, loads, then resumes.
    /// </summary>
    /// <param name="targetWSS">Target device or Broadcast.</param>
    public void Load(WssTarget targetWSS = WssTarget.Broadcast)
    {
        _ = ScheduleSetupChangeAsync(targetWSS, () => StepLogger(_wss.PopulateBoardSettings(targetWSS), $"Load[{targetWSS}]"));
    }

    /// <summary>
    /// Requests configuration blocks from the device via a setup command (with reply).
    /// If Streaming, the core pauses, requests, then resumes.
    /// </summary>
    /// <param name="command">Command group identifier.</param>
    /// <param name="id">Sub-id / selector.</param>
    /// <param name="targetWSS">Target device or Broadcast.</param>
    public void Request_Configs(int command, int id, WssTarget targetWSS = WssTarget.Broadcast)
    {
        _ = ScheduleSetupChangeAsync(targetWSS, () => StepLogger(_wss.RequestConfigs(command, id, targetWSS), $"RequestConfig[{command}, {id}]"));
    }

    /// <summary>
    /// Gets the JSON-backed stimulation configuration controller currently used by this core.
    /// </summary>
    /// <returns>
    /// The active <see cref="StimConfigController"/> instance that provides read/write access
    /// to stimulation parameters and constants loaded from the configuration file.
    /// </returns>
    /// <remarks>
    /// Returned reference is live, not a copy. Callers should avoid mutating it from multiple
    /// threads without external synchronization.
    /// </remarks>
    public StimConfigController GetStimConfigController()
    {
        return _config;
    }
    #endregion

    #region ========== Setup seeding (NormalSetup) ==========
    /// <summary>
    /// Seeds the per-target setup step lists for a full initial configuration and starts
    /// the setup runner. Used once after connect.
    /// </summary>
    public void NormalSetup()
    {
        var tgts = Targets(_maxWSS);
        foreach (var t in tgts)
        {
            if (!_steps.ContainsKey(t)) { _steps[t] = new List<Func<Task<string>>>(); _cursor[t] = 0; }
            _steps[t].Clear();
            _cursor[t] = 0;

            _steps[t].AddRange(new Func<Task<string>>[]
            {
                () => StepLogger(_wss.Clear(0, t), $"Clear[{t}]"),

                // Schedule 1 (Ch1)
                () => StepLogger(_wss.CreateSchedule(1, 13, 170, t), $"CreateSchedule#1[{t}]"), //TODO frequewncy from config
                () => StepLogger(_wss.CreateContactConfig(1, new[]{0,0,2,1}, new[]{0,0,1,2}, 1, t), $"CreateContactConfig#1[{t}]"),
                () => StepLogger(_wss.CreateEvent(1, 0, 1, 11, 11,
                        new[]{11,11,0,0}, new[]{11,11,0,0}, new[]{0,0,_currentIPD}, t),
                        $"CreateEvent#1[{t}]"),
                () => StepLogger(_wss.EditEventRatio(1, 8, t), $"EditEventRatio#1[{t}]"),
                () => StepLogger(_wss.AddEventToSchedule(1, 1, t), $"AddEventToSchedule#1[{t}]"),

                // Schedule 2 (Ch2)
                () => StepLogger(_wss.CreateSchedule(2, 13, 170, t), $"CreateSchedule#2[{t}]"),
                () => StepLogger(_wss.CreateContactConfig(2, new[]{0,2,0,1}, new[]{0,1,0,2}, 2, t), $"CreateContactConfig#2[{t}]"),
                () => StepLogger(_wss.CreateEvent(2, 2, 2, 11, 11,
                        new[]{11,0,11,0}, new[]{11,0,11,0}, new[]{0,0,_currentIPD}, t),
                        $"CreateEvent#2[{t}]"),
                () => StepLogger(_wss.EditEventRatio(2, 8, t), $"EditEventRatio#2[{t}]"),
                () => StepLogger(_wss.AddEventToSchedule(2, 2, t), $"AddEventToSchedule#2[{t}]"),

                // Schedule 3 (Ch3)
                () => StepLogger(_wss.CreateSchedule(3, 13, 170, t), $"CreateSchedule#3[{t}]"),
                () => StepLogger(_wss.CreateContactConfig(3, new[]{2,0,0,1}, new[]{1,0,0,2}, 3, t), $"CreateContactConfig#3[{t}]"),
                () => StepLogger(_wss.CreateEvent(3, 4, 3, 11, 11,
                        new[]{11,0,0,11}, new[]{11,0,0,11}, new[]{0,0,_currentIPD}, t),
                        $"CreateEvent#3[{t}]"),
                () => StepLogger(_wss.EditEventRatio(3, 8, t), $"EditEventRatio#3[{t}]"),
                () => StepLogger(_wss.AddEventToSchedule(3, 3, t), $"AddEventToSchedule#3[{t}]"),

                () => StepLogger(_wss.SyncGroup(170, t), $"SyncGroup[{t}]"),
                () => StepLogger(_wss.StartStim(t),      $"StartStim[{t}]"),
            });
        }
        _resumeStreamingAfter = true; // after full setup, begin streaming automatically
        _state = CoreState.SettingUp;
        EnsureSetupRunner();  // single runner drains all targets
    }
    #endregion

    #region ========== Waveforms (enqueue uploads) ==========
    /// <summary>
    /// Helper that schedules custom waveform uploads (in 4 chunks per polarity) and
    /// then points <paramref name="eventID"/> at User Program shapes 11/12.
    /// </summary>
    /// <param name="wave">Waveform builder.</param>
    /// <param name="eventID">Event ID to edit.</param>
    /// <param name="targetWSS">Target device or Broadcast.</param>
    public void WaveformSetup(WaveformBuilder wave, int eventID, WssTarget targetWSS = WssTarget.Broadcast)
    {
        var cat = wave.getCatShapeArray();
        var ano = wave.getAnodicShapeArray();

        // Defensive: ensure arrays are long enough for 4 chunks; if not, clamp slices.
        static int[] SliceSafe(int[] a, int start, int end)
        {
            start = Math.Max(0, Math.Min(start, a.Length));
            end   = Math.Max(start, Math.Min(end, a.Length));
            var r = new int[end - start];
            Array.Copy(a, start, r, 0, r.Length);
            return r;
        }

        int Lc = cat.Length, La = ano.Length;
        int s0 = 0,  e0 = Math.Max(0, Lc - 24);
        int s1 = 8,  e1 = Math.Max(8, Lc - 16);
        int s2 = 16, e2 = Math.Max(16, Lc - 8);
        int s3 = 24, e3 = Lc;

        int t0 = 0,  u0 = Math.Max(0, La - 24);
        int t1 = 8,  u1 = Math.Max(8, La - 16);
        int t2 = 16, u2 = Math.Max(16, La - 8);
        int t3 = 24, u3 = La;

        _ = ScheduleSetupChangeAsync(targetWSS,
            () => _wss.SetCustomWaveform(0, SliceSafe(cat, s0, e0), 0, targetWSS),
            () => _wss.SetCustomWaveform(0, SliceSafe(cat, s1, e1), 1, targetWSS),
            () => _wss.SetCustomWaveform(0, SliceSafe(cat, s2, e2), 2, targetWSS),
            () => _wss.SetCustomWaveform(0, SliceSafe(cat, s3, e3), 3, targetWSS),
            () => _wss.SetCustomWaveform(1, SliceSafe(ano, t0, u0), 0, targetWSS),
            () => _wss.SetCustomWaveform(1, SliceSafe(ano, t1, u1), 1, targetWSS),
            () => _wss.SetCustomWaveform(1, SliceSafe(ano, t2, u2), 2, targetWSS),
            () => _wss.SetCustomWaveform(1, SliceSafe(ano, t3, u3), 3, targetWSS),
            () => _wss.EditEventShape(eventID, 11, 12, targetWSS)
        );
    }
    #endregion

    #region ========== Streaming internals ==========
    /// <summary>Background task that pushes streaming packets to each WSS at ~12ms cadence.</summary>
    private void StartStreamingInternal()
    {
        if (_streamTask is { IsCompleted: false }) return;
        _streamCts = new CancellationTokenSource();
        var tk = _streamCts.Token;

        _streamTask = Task.Run(async () =>
        {
            while (!tk.IsCancellationRequested)
            {
                for (int w = 1; w <= _maxWSS; w++)
                {
                    int baseIdx = (w - 1) * 3;
                    _ = _wss.StreamChange(
                        new[] { AmpTo255Convention(_chAmps[baseIdx + 0]), AmpTo255Convention(_chAmps[baseIdx + 1]), AmpTo255Convention(_chAmps[baseIdx + 2]) },
                        new[] { _chPWs[baseIdx + 0], _chPWs[baseIdx + 1], _chPWs[baseIdx + 2] },
                        new[] { (int)_config.getStimParam($"Ch{baseIdx + 0}IPI"), (int)_config.getStimParam($"Ch{baseIdx + 1}IPI"), (int)_config.getStimParam($"Ch{baseIdx + 2}IPI") },
                        IntToWssTarget(w));
                    await Task.Delay(_delayMsBetweenPackets, tk);
                }
            }
        }, tk);
    }

    /// <summary>Stop the streaming background task.</summary>
    private void StopStreamingInternal()
    {
        _streamCts?.Cancel();
        try { _streamTask?.Wait(250); } catch { }
        _streamTask = null;
    }

    /// <summary>Disconnect transport safely.</summary>
    private void SafeDisconnect()
    {
        try { _wss?.DisconnectAsync().GetAwaiter().GetResult(); } catch { }
    }
    #endregion

    #region ========== Setup runner internals ==========
    /// <summary>Append steps to a target, pause streaming if needed, and ensure the runner is active.</summary>
    private async Task ScheduleSetupChangeAsync(WssTarget t, params Func<Task<string>>[] newSteps)
    {
        await _setupGate.WaitAsync();
        try
        {
            if (!_steps.ContainsKey(t)) { _steps[t] = new List<Func<Task<string>>>(); _cursor[t] = 0; }
            _steps[t].AddRange(newSteps);

            // pause streaming once; resume when queue drains
            if (_state == CoreState.Streaming) { StopStreamingInternal(); _resumeStreamingAfter = true; }
            _state = CoreState.SettingUp;

            EnsureSetupRunner();
        }
        finally { _setupGate.Release(); }
    }

    /// <summary>Start the background runner if needed.</summary>
    private void EnsureSetupRunner()
    {
        if (_setupRunner is { IsCompleted: false }) return;
        _setupRunner = Task.Run(SetupWorkerAsync);
    }

    /// <summary>Run a pass over all targets, advancing each cursor. Resume streaming when empty.</summary>
    private async Task SetupWorkerAsync()
    {
        try
        {
            // Iterate deterministic target order (snapshot keys to avoid collection-modified issues)
            foreach (var kvp in _steps.ToArray())
            {
                var t = kvp.Key;
                var list = kvp.Value;
                if (list == null || list.Count == 0) continue;

                for (int i = _cursor[t]; i < list.Count; i++)
                {
                    _cursor[t] = i; // resume point
                    var res = await list[i](); // throws on timeout/IO; returns "Error: ..." on device error
                    if (res.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"{t} step#{i} failed: {res}");
                }
                _cursor[t] = list.Count; // finished this target
            }

            if (SetupQueueEmpty())
            {
                if (_resumeStreamingAfter) { StartStreamingInternal(); _resumeStreamingAfter = false; }
            }
            // else: leave state as SettingUp; Tick() will Observe runner completed + !empty and spawn another pass
        }
        catch (Exception ex)
        {
            Log.Error($"[SetupWorker] {ex.Message}");
            _state = CoreState.Error;
        }
    }

    /// <summary>True when all target cursors have consumed their lists.</summary>
    private bool SetupQueueEmpty()
    {
        foreach (var kvp in _steps)
        {
            if (kvp.Value != null && _cursor.TryGetValue(kvp.Key, out var c))
                if (c < kvp.Value.Count) return false;
        }
        return true;
    }

    /// <summary>Which targets to include (clamped to transport capability).</summary>
    private static WssTarget[] Targets(int maxWss)
    {
        int n = Math.Max(1, Math.Min(maxWss, 3));
        return n switch
        {
            1 => new[] { WssTarget.Wss1 },
            2 => new[] { WssTarget.Wss1, WssTarget.Wss2 },
            _ => new[] { WssTarget.Wss1, WssTarget.Wss2, WssTarget.Wss3 }
        };
    }

    /// <summary>Log per-step result; throw on device "Error:" or timeout. (Non-blocking.)</summary>
    private static async Task<string> StepLogger(Task<string> op, string name)
    {
        try
        {
            var res = await op.ConfigureAwait(false);
            if (res.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{name} failed: {res}");
            Log.Info($"{name}: {res}");
            return res;
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"{name} timed out (no reply).");
        }
    }
    #endregion

    #region ========== Helpers ==========
    /// <summary> Verifies if the stim mode in the config file is one that has been implemented in the stimWithMode</summary>
    private void VerifyStimMode()
    {
        string mode = _config._config.sensationController;
        if (mode == "P" || mode == "PD") _validMode = true;
        else
        {
            Log.Error("[WssStimulationCore] Unrecognized mode, defaulting to proportional mode.");
            _validMode = false;
        }
    }

    /// <summary> Initializes arrays to hold values neccessary for streaming</summary>
    private void InitStimArrays()
    {
        int n = _maxWSS * 3;
        _chAmps = new float[n];
        _chPWs = new int[n];
        _prevMagnitude = new float[n];
        _currentMag = new float[n];
        _d_dt = new float[n];
        _dt = new float[n];

        for (int i = 0; i < n; i++)
        {
            _chAmps[i] = 0f;
            _chPWs[i] = 0;
            _prevMagnitude[i] = 0f;
            _dt[i] = _timer.ElapsedMilliseconds / 1000.0f;
        }
    }

    /// <summary> Given a string like "Thumb" or "Ch1" it will return the correct channel as an int</summary>
    private int FingerToChannel(string finger)
    {
        if (string.IsNullOrEmpty(finger)) return 0;
        return finger switch
        {
            "Thumb" => 1,
            "Index" => 2,
            "Middle" => 3,
            "Ring" => 4,
            "Pinky" => 5,
            "Palm" => 6,
            _ => finger.StartsWith("ch", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(finger.Substring(2), out int ch) ? ch : 0
        };
    }

    /// <summary>
    /// Compute PW (us) using supported controller. Note: uses 0-based arrays, so channel is adjusted internally.
    /// </summary>
    private int CalculateStim(int channel, float magnitude, float max, float min)
    {        
        if ((uint)channel >= (uint)_currentMag.Length) return 0;      
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
        output = Math.Clamp(output, 0f, 1f);
        output = output > 0f ? (output * (max - min)) + min : 0f;
        return (int)output;
    }

    /// <summary> hanndles the  non-linear conversion of an amplitude in mA to bytes depending on WSS capabilities </summary>
    private int AmpTo255Convention(float amp)
    {
        if (amp < 4)
        {
            double v = Math.Pow(amp / 0.0522f, 1.0 / 1.5466f) + 1.0;
            return (int)v;
        }
        else
        {
            double v = ((amp + 1.7045f) / 0.3396f) + 1.0;
            return (int)v;
        }
    }

    /// <summary> Helper to use int instead of targets in for loops </summary>
    private WssTarget IntToWssTarget(int i)
    {
        return i switch
        {
            0 => WssTarget.Broadcast,
            1 => WssTarget.Wss1,
            2 => WssTarget.Wss2,
            3 => WssTarget.Wss3,
            _ => WssTarget.Wss1
        };
    }
    #endregion
}
