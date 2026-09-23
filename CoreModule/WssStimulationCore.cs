using System;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Wss.Transports;

namespace Wss.CoreModule
{
    /// <summary>
    /// Stimulation core that manages connection, setup (via a queued step runner), and a background
    /// streaming loop. Public mutator methods enqueue device edits and return immediately.
    /// </summary>
    public sealed class WssStimulationCore : IStimulationCore, IBasicStimulation, IAdvancedEventProgrammer
    {
        #region ========== Fields & nested types ==========
        // ---- transport & config ----
        private readonly ITransport _transport;
        private readonly CoreConfigController _coreConfig;
        private readonly string _jsonPath;
        private readonly int _maxSetupTries;
        private readonly int _delayMsBetweenPackets = 10; // radio throttling
        private WssClient _wss;
        private bool _resumeStreamingAfter;
        private bool _disposed;

        // ---- runtime state ----
        private CoreState _state = CoreState.Disconnected;
        private int _currentSetupTries;
        private readonly Dictionary<WssTarget, int> _cursor = new Dictionary<WssTarget, int>(); // per-target step index
        private readonly Dictionary<WssTarget, List<Func<Task<string>>>> _steps = new Dictionary<WssTarget, List<Func<Task<string>>>>();
        private readonly Dictionary<WssTarget, ModuleSettings> _unitSettings = new Dictionary<WssTarget, ModuleSettings>();
        private int _maxWSS = 1;
        private readonly SemaphoreSlim _setupGate = new(1, 1);
        private readonly object _streamGate = new object();
        private int _streamStopsInProgress;

        // ---- background tasks ----
        private CancellationTokenSource _streamCts;
        private Task _streamTask;
        private Task _connectTask;
        private Task _setupRunner;

        // ---- channels & controller state ----
        private float[] _chAmps;  // mA (mapped to 0..255 for device)
        private int[] _chPWs;   // us
        private int[] _chIPIs;   // ms period
        private int[] _lastIpiSentPerCh; //used to check changes since frequency reset schedule only send if there is a change
        private int _maxWSSChannels = 0;

        //defaults vaules used for initial setup
        private readonly int _defaultIPI = 10;
        private readonly float _defaultAmp = 1.0f;
        private readonly int _defaultSync = 170;
        private readonly int _defaultRatio = 8;
        private readonly int _defaultIPD = 50;
        

        private enum CoreState { Disconnected, Connecting, SettingUp, Ready, Started, Streaming, Error }
        #endregion

        #region ========== Construction ==========
        /// <summary>
        /// Initializes a stimulation core over a caller-provided transport.
        /// </summary>
        /// <param name="transport">Transport used to communicate with the WSS device(s).</param>
        /// <param name="options">Core configuration path, setup retry behavior, and default stimulation values used during initial setup.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="transport"/> or <paramref name="options"/> is null.</exception>
        /// <exception cref="ArgumentException">Thrown when <see cref="WssStimulationCoreOptions.ConfigPath"/> is null, empty, or whitespace.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <see cref="WssStimulationCoreOptions.MaxSetupTries"/> is less than 1.</exception>
        /// <remarks>
        /// The core owns the lifetime of <paramref name="transport"/> and disposes it when the core is disposed.
        /// Call <see cref="Shutdown"/> to stop streaming/disconnect without disposing the transport.
        /// </remarks>
        public WssStimulationCore(ITransport transport, WssStimulationCoreOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            if (string.IsNullOrWhiteSpace(options.ConfigPath)) throw new ArgumentException("Invalid config path.", nameof(WssStimulationCoreOptions.ConfigPath));
            if (options.MaxSetupTries < 1) throw new ArgumentOutOfRangeException(nameof(WssStimulationCoreOptions.MaxSetupTries), "Max setup tries must be at least 1.");

            _jsonPath = options.ConfigPath;
            _coreConfig = new CoreConfigController(_jsonPath);
            _maxSetupTries = options.MaxSetupTries;
            _defaultIPI = options.DefaultIpi;
            _defaultAmp = options.DefaultAmp;
            _defaultSync = options.DefaultSync;
            _defaultRatio = options.DefaultRatio;
            _defaultIPD = options.DefaultIpd;
        }
        #endregion

        #region ========== Lifecycle (Initialize / Tick / Shutdown) ==========
        /// <inheritdoc/>
        /// <exception cref="ObjectDisposedException">Thrown when the core has been disposed.</exception>
        public void Initialize()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(WssStimulationCore));

            if (_state != CoreState.Disconnected) Shutdown();

            _currentSetupTries = 0;

            // (re)load app config
            _coreConfig.LoadJson();
            _maxWSS = _coreConfig.MaxWss;
            _unitSettings.Clear();

            InitStimArrays();

            _wss = new WssClient(
                _transport,
                new WssFrameCodec(),
                new WSSVersionHandler(_coreConfig.Firmware),
                new WssClientOptions
                {
                    OwnsTransport = false,
                    BroadcastTarget = _coreConfig.BroadcastTarget,
                    WssTargets = _coreConfig.WssTargets
                });

            _state = CoreState.Connecting;
            _connectTask = _wss.ConnectAsync();
        }

        /// <inheritdoc/>
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
                    else if (_connectTask.IsCompleted && !_connectTask.IsFaulted && !_connectTask.IsCanceled)
                    {
                        NormalSetup();                // seed all targets once
                        _state = CoreState.SettingUp;
                    }
                    break;

                case CoreState.SettingUp:
                    if (!_setupGate.Wait(0))
                        break;
                    try
                    {
                        if (_state != CoreState.SettingUp || _streamStopsInProgress > 0)
                            break;

                        // Start or keep the runner. If a pass finished but queue isn't empty (new steps arrived),
                        // restart the runner to drain remaining work.
                        if (_setupRunner == null)
                        {
                            EnsureSetupRunner();
                        }
                        else if (_setupRunner.IsFaulted)
                        {
                            var root = _setupRunner.Exception?.GetBaseException().Message ?? "Unknown error";
                            _setupRunner = null;
                            if (++_currentSetupTries > _maxSetupTries)
                            {
                                Log.Error($"Setup failed: {root} (exceeded {_maxSetupTries} attempts)");
                                _state = CoreState.Error;
                            }
                            else
                            {
                                Log.Warn($"Setup failed: {root}. Retrying {_currentSetupTries}/{_maxSetupTries}...");
                            }
                        }
                        else if (_setupRunner.IsCompleted && !SetupQueueEmpty())
                        {
                            EnsureSetupRunner();
                        }
                        else if (_setupRunner.IsCompleted && SetupQueueEmpty())
                        {
                            _state = CoreState.Ready;
                        }
                    }
                    finally { _setupGate.Release(); }
                    break;

                case CoreState.Ready:
                    if (!_setupGate.Wait(0))
                        break;
                    try
                    {
                        if (_state == CoreState.Ready && _streamStopsInProgress == 0 && _wss.Started)
                            _state = CoreState.Started;
                    }
                    finally { _setupGate.Release(); }
                    break;

                case CoreState.Started:
                    if (!_setupGate.Wait(0))
                        break;
                    try
                    {
                        if (_state == CoreState.Started && _streamStopsInProgress == 0)
                        {
                            StartStreamingInternal();
                            _state = CoreState.Streaming;
                        }
                    }
                    finally { _setupGate.Release(); }
                    break;

                case CoreState.Streaming:
                    // background streaming task is running; Tick has nothing to do
                    break;

                case CoreState.Error:
                    StopStreamingInternal().GetAwaiter().GetResult();
                    SafeDisconnect();
                    break;
            }
        }

        /// <inheritdoc/>
        public void Shutdown()
        {
            StopStreamingInternal().GetAwaiter().GetResult();
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
        public void LoadConfigFile() => _coreConfig.LoadJson();

        /// <inheritdoc/>
        /// <remarks>
        /// Calls <see cref="Shutdown"/> and then disposes the transport instance provided at construction time.
        /// Safe to call multiple times.
        /// </remarks>
        public void Dispose()
        {
            if (_disposed) return;
            Shutdown();
            try { _transport.Dispose(); } catch { }
            _disposed = true;
        }
        #endregion

        #region ========== Status ==========
        /// <inheritdoc/>
        public bool Started() => _state is CoreState.Started or CoreState.Streaming;

        /// <inheritdoc/>
        public bool Ready() => _state is CoreState.Ready;
        #endregion

        #region ========== Public control API (non-blocking) ==========
        /// <inheritdoc/>
        public void StimulateAnalog(int channel, int PW, float amp, int IPI)
        {
            if (channel <= 0 || channel > _maxWSS * 3) return;

            _chAmps[channel - 1] = amp;
            _chPWs[channel - 1] = PW;
            _chIPIs[channel - 1] = IPI;
        }

        /// <inheritdoc/>
        public void ZeroOutStim(WssTarget wsstarget = WssTarget.Broadcast)
        {
            _ = _wss.ZeroOutStim(wsstarget);
        }

        /// <inheritdoc/>
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

        /// <inheritdoc/>
        public void StopStim(WssTarget targetWSS = WssTarget.Broadcast)
        {
            if (_wss == null) return;

            switch (_state)
            {
                case CoreState.SettingUp:
                case CoreState.Ready:
                case CoreState.Started:
                    _ = ScheduleSetupChangeAsync(targetWSS, false,
                        () => StepLogger(_wss.StopStim(targetWSS),      $"StopStim[{targetWSS}]")
                    );
                    break;
                case CoreState.Streaming:
                    _ = ScheduleSetupChangeAsync(targetWSS, false,
                        () => StepLogger(_wss.StopStim(targetWSS),      $"StopStim[{targetWSS}]")
                    );
                    break;
            }
        }

        /// <inheritdoc/>
        public bool IsChannelInRange(int ch)
        {
            return ch > 0 && ch <= _maxWSSChannels;
        }

        /// <inheritdoc/>
        public void UpdateIPD(int IPD, WssTarget targetWSS = WssTarget.Broadcast)
        {
            int _currentIPD = Math.Max(1, Math.Min(IPD, 1000));

            _ = ScheduleSetupChangeAsync(targetWSS,
                () => StepLogger(_wss.EditEventPw(1, new[] { 0, 0, _currentIPD }, targetWSS), $"UpdateIPD[{targetWSS}], Event[1]"),
                () => StepLogger(_wss.EditEventPw(2, new[] { 0, 0, _currentIPD }, targetWSS), $"UpdateIPD[{targetWSS}], Event[2]"),
                () => StepLogger(_wss.EditEventPw(3, new[] { 0, 0, _currentIPD }, targetWSS), $"UpdateIPD[{targetWSS}], Event[3]")
            );
        }

        /// <inheritdoc/>
        public void UpdateIPD(int IPD, int eventID, WssTarget targetWSS = WssTarget.Broadcast)
        {
            int _currentIPD = Math.Max(1, Math.Min(IPD, 1000));

            _ = ScheduleSetupChangeAsync(targetWSS,
                () => StepLogger(_wss.EditEventPw(eventID, new[] { 0, 0, _currentIPD }, targetWSS), $"UpdateIPD[{targetWSS}], Event[{eventID}]")
            );
        }

        /// <inheritdoc/>
        public void UpdateEventRatio(int ratio, WssTarget targetWSS = WssTarget.Broadcast)
        {
            if (ratio != 1 && ratio != 2 && ratio != 4 && ratio != 8)
                throw new ArgumentOutOfRangeException(nameof(ratio), "Ratio must be one of {1,2,4,8}.");

            _ = ScheduleSetupChangeAsync(targetWSS,
                () => StepLogger(_wss.EditEventRatio(1, ratio, targetWSS), $"UpdateEventRatio[{targetWSS}], Event[1]"),
                () => StepLogger(_wss.EditEventRatio(2, ratio, targetWSS), $"UpdateEventRatio[{targetWSS}], Event[2]"),
                () => StepLogger(_wss.EditEventRatio(3, ratio, targetWSS), $"UpdateEventRatio[{targetWSS}], Event[3]")
            );
        }

        /// <inheritdoc/>
        public void UpdateEventRatio(int ratio, int eventID, WssTarget targetWSS = WssTarget.Broadcast)
        {
            if (ratio != 1 && ratio != 2 && ratio != 4 && ratio != 8)
                throw new ArgumentOutOfRangeException(nameof(ratio), "Ratio must be one of {1,2,4,8}.");

            _ = ScheduleSetupChangeAsync(targetWSS,
                () => StepLogger(_wss.EditEventRatio(eventID, ratio, targetWSS), $"UpdateEventRatio[{targetWSS}], Event[{eventID}]")
            );
        }

        /// <inheritdoc/>
        public void UpdateWaveform(int[] waveform, int eventID, WssTarget targetWSS = WssTarget.Broadcast)
        {
            WaveformSetup(new WaveformBuilder(waveform), eventID, targetWSS);
        }

        /// <inheritdoc/>
        public void UpdateWaveform(WaveformBuilder waveform, int eventID, WssTarget targetWSS = WssTarget.Broadcast)
        {
            WaveformSetup(waveform, eventID, targetWSS);
        }

        /// <inheritdoc/>
        public void UpdateEventShape(int cathodicWaveform, int anodicWaveform, int eventID, WssTarget targetWSS = WssTarget.Broadcast)
        {
            _ = ScheduleSetupChangeAsync(targetWSS,
                () => StepLogger(_wss.EditEventShape(eventID, cathodicWaveform, anodicWaveform, targetWSS),      $"UpdateShape[{targetWSS}], Event[{eventID}]")
            );
        }

        /// <inheritdoc/>
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

        /// <inheritdoc/>
        public void Save(WssTarget targetWSS = WssTarget.Broadcast)
        {
            _ = ScheduleSetupChangeAsync(targetWSS, () => StepLogger(_wss.PopulateFramSettings(targetWSS), $"SaveChanges[{targetWSS}]"));
        }

        /// <inheritdoc/>
        public void Load(WssTarget targetWSS = WssTarget.Broadcast)
        {
            _ = ScheduleSetupChangeAsync(targetWSS, () => StepLogger(_wss.PopulateBoardSettings(targetWSS), $"Load[{targetWSS}]"));
        }

        /// <inheritdoc/>
        public void Request_Configs(int command, int id, WssTarget targetWSS = WssTarget.Broadcast)
        {
            _ = ScheduleSetupChangeAsync(targetWSS, () => StepLogger(_wss.RequestConfigs(command, id, targetWSS), $"RequestConfig[{command}, {id}]"));
        }

        /// <inheritdoc/>
        public CoreConfigController GetCoreConfigController()
        {
            return _coreConfig;
        }

        // ---- IBasicStimulation (setup edits) ----

        /// <inheritdoc/>
        public void UpdateEventDelay(int delayMs, int eventID, WssTarget targetWSS = WssTarget.Broadcast)
        {
            _ = ScheduleSetupChangeAsync(targetWSS,
                () => StepLogger(_wss.EditEventDelay(eventID, delayMs, targetWSS), $"UpdateEventDelay[{targetWSS}], Event[{eventID}]")
            );
        }

        /// <inheritdoc/>
        public void UpdateEventDelay(int delayMs, WssTarget targetWSS = WssTarget.Broadcast)
        {
            _ = ScheduleSetupChangeAsync(targetWSS,
                () => StepLogger(_wss.EditEventDelay(1, delayMs, targetWSS), $"UpdateEventDelay[{targetWSS}], Event[1]"),
                () => StepLogger(_wss.EditEventDelay(2, delayMs, targetWSS), $"UpdateEventDelay[{targetWSS}], Event[2]"),
                () => StepLogger(_wss.EditEventDelay(3, delayMs, targetWSS), $"UpdateEventDelay[{targetWSS}], Event[3]")
            );
        }

        /// <inheritdoc/>
        public void SetEventEnabled(int eventID, bool enabled, WssTarget targetWSS = WssTarget.Broadcast)
        {
            _ = ScheduleSetupChangeAsync(targetWSS,
                () => StepLogger(_wss.EditEventEnableBit(eventID, enabled ? 1 : 0, targetWSS), $"SetEventEnabled[{targetWSS}], Event[{eventID}]")
            );
        }

        /// <inheritdoc/>
        public void SetEventEnabled(bool enabled, WssTarget targetWSS = WssTarget.Broadcast)
        {
            _ = ScheduleSetupChangeAsync(targetWSS,
                () => StepLogger(_wss.EditEventEnableBit(1, enabled ? 1 : 0, targetWSS), $"SetEventEnabled[{targetWSS}], Event[1]"),
                () => StepLogger(_wss.EditEventEnableBit(2, enabled ? 1 : 0, targetWSS), $"SetEventEnabled[{targetWSS}], Event[2]"),
                () => StepLogger(_wss.EditEventEnableBit(3, enabled ? 1 : 0, targetWSS), $"SetEventEnabled[{targetWSS}], Event[3]")
            );
        }

        /// <inheritdoc/>
        public void CreateContactConfig(ContactConfigDefinition definition, WssTarget targetWSS = WssTarget.Broadcast)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            _ = ScheduleSetupChangeAsync(targetWSS,
                () => StepLogger(_wss.CreateContactConfig(definition, targetWSS), $"CreateContactConfig[{targetWSS}]")
            );
        }

        /// <inheritdoc/>
        public void DeleteContactConfig(int contactConfigID, WssTarget targetWSS = WssTarget.Broadcast)
        {
            _ = ScheduleSetupChangeAsync(targetWSS,
                () => StepLogger(_wss.DeleteContactConfig(contactConfigID, targetWSS), $"DeleteContactConfig[{targetWSS}], ID[{contactConfigID}]")
            );
        }

        /// <inheritdoc/>
        public void UpdateEventContactConfig(int eventID, int contactConfigID, WssTarget targetWSS = WssTarget.Broadcast)
        {
            _ = ScheduleSetupChangeAsync(targetWSS,
                () => StepLogger(_wss.EditEventContactConfig(eventID, contactConfigID, targetWSS), $"UpdateEventContactConfig[{targetWSS}], Event[{eventID}]")
            );
        }

        /// <inheritdoc/>
        public void UpdateEventContactConfig(int contactConfigID, WssTarget targetWSS = WssTarget.Broadcast)
        {
            _ = ScheduleSetupChangeAsync(targetWSS,
                () => StepLogger(_wss.EditEventContactConfig(1, contactConfigID, targetWSS), $"UpdateEventContactConfig[{targetWSS}], Event[1]"),
                () => StepLogger(_wss.EditEventContactConfig(2, contactConfigID, targetWSS), $"UpdateEventContactConfig[{targetWSS}], Event[2]"),
                () => StepLogger(_wss.EditEventContactConfig(3, contactConfigID, targetWSS), $"UpdateEventContactConfig[{targetWSS}], Event[3]")
            );
        }

        // ---- IAdvancedEventProgrammer ----

        /// <inheritdoc/>
        public void CreateEvent(CreateEventRequest request, WssTarget targetWSS = WssTarget.Broadcast)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            _ = ScheduleSetupChangeAsync(targetWSS,
                () => StepLogger(_wss.CreateEvent(request, targetWSS), $"CreateEvent[{targetWSS}], Event[{request.EventId}]")
            );
        }

        /// <inheritdoc/>
        public void DeleteEvent(int eventID, WssTarget targetWSS = WssTarget.Broadcast)
        {
            _ = ScheduleSetupChangeAsync(targetWSS,
                () => StepLogger(_wss.DeleteEvent(eventID, targetWSS), $"DeleteEvent[{targetWSS}], Event[{eventID}]")
            );
        }

        /// <inheritdoc/>
        public void UpdateEventPulseWidths(int eventID, int standardPw, int rechargePw, int ipd, WssTarget targetWSS = WssTarget.Broadcast)
        {
            _ = ScheduleSetupChangeAsync(targetWSS,
                () => StepLogger(_wss.EditEventPw(eventID, new[] { standardPw, rechargePw, ipd }, targetWSS), $"UpdateEventPulseWidths[{targetWSS}], Event[{eventID}]")
            );
        }

        /// <inheritdoc/>
        public void UpdateEventAmplitudes(int eventID, int[] standardAmplitudes, int[] rechargeAmplitudes, WssTarget targetWSS = WssTarget.Broadcast)
        {
            _ = ScheduleSetupChangeAsync(targetWSS,
                () => StepLogger(_wss.EditEventAmp(eventID, standardAmplitudes, rechargeAmplitudes, targetWSS), $"UpdateEventAmplitudes[{targetWSS}], Event[{eventID}]")
            );
        }

        /// <inheritdoc/>
        public void CreateSchedule(ScheduleDefinition definition, WssTarget targetWSS = WssTarget.Broadcast)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            _ = ScheduleSetupChangeAsync(targetWSS,
                () => StepLogger(_wss.CreateSchedule(definition, targetWSS), $"CreateSchedule[{targetWSS}], Schedule[{definition.ScheduleId}]")
            );
        }

        /// <inheritdoc/>
        public void DeleteSchedule(int scheduleID, WssTarget targetWSS = WssTarget.Broadcast)
        {
            _ = ScheduleSetupChangeAsync(targetWSS,
                () => StepLogger(_wss.DeleteSchedule(scheduleID, targetWSS), $"DeleteSchedule[{targetWSS}], Schedule[{scheduleID}]")
            );
        }

        /// <inheritdoc/>
        public void UpdateScheduleDuration(int scheduleID, int durationMs, WssTarget targetWSS = WssTarget.Broadcast)
        {
            _ = ScheduleSetupChangeAsync(targetWSS,
                () => StepLogger(_wss.ChangeScheduleDuration(scheduleID, durationMs, targetWSS), $"UpdateScheduleDuration[{targetWSS}], Schedule[{scheduleID}]")
            );
        }

        /// <inheritdoc/>
        public void AddEventToSchedule(int eventID, int scheduleID, WssTarget targetWSS = WssTarget.Broadcast)
        {
            _ = ScheduleSetupChangeAsync(targetWSS,
                () => StepLogger(_wss.AddEventToSchedule(eventID, scheduleID, targetWSS), $"AddEventToSchedule[{targetWSS}], Event[{eventID}], Schedule[{scheduleID}]")
            );
        }

        /// <inheritdoc/>
        public void RemoveEventFromSchedule(int eventID, WssTarget targetWSS = WssTarget.Broadcast)
        {
            _ = ScheduleSetupChangeAsync(targetWSS,
                () => StepLogger(_wss.DeleteEventFromSchedule(eventID, targetWSS), $"RemoveEventFromSchedule[{targetWSS}], Event[{eventID}]")
            );
        }

        /// <inheritdoc/>
        public void MoveEventToSchedule(int eventID, int scheduleID, int delayMs, WssTarget targetWSS = WssTarget.Broadcast)
        {
            _ = ScheduleSetupChangeAsync(targetWSS,
                () => StepLogger(_wss.MoveEventToSchedule(eventID, scheduleID, delayMs, targetWSS), $"MoveEventToSchedule[{targetWSS}], Event[{eventID}], Schedule[{scheduleID}]")
            );
        }

        /// <inheritdoc/>
        public void SetScheduleGroup(int scheduleID, int syncSignal, WssTarget targetWSS = WssTarget.Broadcast)
        {
            _ = ScheduleSetupChangeAsync(targetWSS,
                () => StepLogger(_wss.ChangeScheduleGroup(scheduleID, syncSignal, targetWSS), $"SetScheduleGroup[{targetWSS}], Schedule[{scheduleID}]")
            );
        }

        /// <inheritdoc/>
        public void SetScheduleState(int scheduleID, ScheduleState state, WssTarget targetWSS = WssTarget.Broadcast)
        {
            ValidateScheduleState(state);
            _ = ScheduleSetupChangeAsync(targetWSS,
                () => StepLogger(_wss.ChangeScheduleState(scheduleID, (int)state, targetWSS), $"SetScheduleState[{targetWSS}], Schedule[{scheduleID}], State[{state}]")
            );
        }

        /// <inheritdoc/>
        public void SetGroupState(int syncSignal, ScheduleState state, WssTarget targetWSS = WssTarget.Broadcast)
        {
            ValidateScheduleState(state);
            _ = ScheduleSetupChangeAsync(targetWSS,
                () => StepLogger(_wss.ChangeGroupState(syncSignal, (int)state, targetWSS), $"SetGroupState[{targetWSS}], SyncSignal[{syncSignal}], State[{state}]")
            );
        }

        /// <inheritdoc/>
        public void SyncGroup(int syncSignal, WssTarget targetWSS = WssTarget.Broadcast)
        {
            _ = ScheduleSetupChangeAsync(targetWSS,
                () => StepLogger(_wss.SyncGroup(syncSignal, targetWSS), $"SyncGroup[{targetWSS}], SyncSignal[{syncSignal}]")
            );
        }
        #endregion

        #region ========== Setup seeding (NormalSetup) ==========
        /// <summary>
        /// Seeds the per-target setup step lists for a full initial configuration and starts
        /// the setup runner. Used once after connect.
        /// </summary>
        private void NormalSetup()
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
                    //querry settings.
                    () => StepLogger(_wss.ModuleQuery(1, t), $"QuerryCurrent[{t}]"),
                    // capture decoded unit settings (or defaults) and log via StepLogger
                    () => StepLogger(CaptureUnitSettingsAsync(t), $"UnitSettings[{t}]"),
                    // Schedule 1 (Ch1)
                    () => StepLogger(_wss.CreateSchedule(new ScheduleDefinition { ScheduleId = 1, DurationMs = _defaultIPI, SyncSignal = _defaultSync }, t), $"CreateSchedule#1[{t}]"), //TODO frequewncy from config
                    () => StepLogger(_wss.CreateContactConfig(new ContactConfigDefinition { ContactId = 1, StimSetup = new[]{1,2,0,0}, RechargeSetup = new[]{2,1,0,0}, Leds = 2 }, t), $"CreateContactConfig#1[{t}]"),
                    () => StepLogger(_wss.CreateEvent(new CreateEventRequest
                        {
                            EventId = 1,
                            DelayMs = 0,
                            ContactConfigId = 1,
                            StandardShapeId = 0,
                            RechargeShapeId = 0,
                            StandardAmplitudes = new[]{AmpTo255Convention(_defaultAmp, t),AmpTo255Convention(_defaultAmp, t),0,0},
                            RechargeAmplitudes = new[]{AmpTo255Convention(_defaultAmp, t),AmpTo255Convention(_defaultAmp, t),0,0},
                            PulseWidths = new EventPulseWidths { StandardPw = 0, RechargePw = 0, Ipd = _defaultIPD }
                        }, t), $"CreateEvent#1[{t}]"),
                    () => StepLogger(_wss.EditEventRatio(1, _defaultRatio, t), $"EditEventRatio#1[{t}]"),
                    () => StepLogger(_wss.AddEventToSchedule(1, 1, t), $"AddEventToSchedule#1[{t}]"),

                    // Schedule 2 (Ch2)
                    () => StepLogger(_wss.CreateSchedule(new ScheduleDefinition { ScheduleId = 2, DurationMs = _defaultIPI, SyncSignal = _defaultSync }, t), $"CreateSchedule#2[{t}]"),
                    () => StepLogger(_wss.CreateContactConfig(new ContactConfigDefinition { ContactId = 2, StimSetup = new[]{1,0,2,0}, RechargeSetup = new[]{2,0,1,0}, Leds = 4 }, t), $"CreateContactConfig#2[{t}]"),
                    () => StepLogger(_wss.CreateEvent(new CreateEventRequest
                        {
                            EventId = 2,
                            DelayMs = 2,
                            ContactConfigId = 2,
                            StandardShapeId = 0,
                            RechargeShapeId = 0,
                            StandardAmplitudes = new[]{AmpTo255Convention(_defaultAmp, t),AmpTo255Convention(_defaultAmp, t),0,0},
                            RechargeAmplitudes = new[]{AmpTo255Convention(_defaultAmp, t),AmpTo255Convention(_defaultAmp, t),0,0},
                            PulseWidths = new EventPulseWidths { StandardPw = 0, RechargePw = 0, Ipd = _defaultIPD }
                        }, t), $"CreateEvent#2[{t}]"),
                    () => StepLogger(_wss.EditEventRatio(2, _defaultRatio, t), $"EditEventRatio#2[{t}]"),
                    () => StepLogger(_wss.AddEventToSchedule(2, 2, t), $"AddEventToSchedule#2[{t}]"),

                    // Schedule 3 (Ch3)
                    () => StepLogger(_wss.CreateSchedule(new ScheduleDefinition { ScheduleId = 3, DurationMs = _defaultIPI, SyncSignal = _defaultSync }, t), $"CreateSchedule#3[{t}]"),
                    () => StepLogger(_wss.CreateContactConfig(new ContactConfigDefinition { ContactId = 3, StimSetup = new[]{1,0,0,2}, RechargeSetup = new[]{2,0,0,1}, Leds = 8 }, t), $"CreateContactConfig#3[{t}]"),
                    () => StepLogger(_wss.CreateEvent(new CreateEventRequest
                        {
                            EventId = 3,
                            DelayMs = 4,
                            ContactConfigId = 3,
                            StandardShapeId = 0,
                            RechargeShapeId = 0,
                            StandardAmplitudes = new[]{AmpTo255Convention(_defaultAmp, t),AmpTo255Convention(_defaultAmp, t),0,0},
                            RechargeAmplitudes = new[]{AmpTo255Convention(_defaultAmp, t),AmpTo255Convention(_defaultAmp, t),0,0},
                            PulseWidths = new EventPulseWidths { StandardPw = 0, RechargePw = 0, Ipd = _defaultIPD }
                        }, t), $"CreateEvent#3[{t}]"),
                    () => StepLogger(_wss.EditEventRatio(3, _defaultRatio, t), $"EditEventRatio#3[{t}]"),
                    () => StepLogger(_wss.AddEventToSchedule(3, 3, t), $"AddEventToSchedule#3[{t}]"),

                    () => StepLogger(_wss.SyncGroup(_defaultSync, t), $"SyncGroup[{t}]"),
                    () => StepLogger(_wss.StartStim(t),      $"StartStim[{t}]"),
                });
            }
            _resumeStreamingAfter = true; // after full setup, begin streaming automatically
            _state = CoreState.SettingUp;
            EnsureSetupRunner();  // single runner drains all targets
        }
        #endregion

        #region ========== Waveforms (enqueue uploads) ==========
        /// <inheritdoc/>
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
            lock (_streamGate)
            {
                if (_streamTask != null && !_streamTask.IsCompleted) return;
                _streamCts = new CancellationTokenSource();
                var tk = _streamCts.Token;
                _streamTask = Task.Run(() => StreamLoopAsync(tk), tk);
            }
        }

        /// <summary>Push streaming packets until cancellation is requested.</summary>
        private async Task StreamLoopAsync(CancellationToken tk)
        {
            while (!tk.IsCancellationRequested)
            {
                for (int w = 1; w <= _maxWSS; w++)
                {
                    int wIdx    = w - 1;
                    int baseIdx = wIdx * 3;

                    var target = IntToWssTarget(w);
                    var amps = new[] {
                        AmpTo255Convention(_chAmps[baseIdx + 0], target),
                        AmpTo255Convention(_chAmps[baseIdx + 1], target),
                        AmpTo255Convention(_chAmps[baseIdx + 2], target)
                    };
                    var pws = new[] {
                        _chPWs[baseIdx + 0],
                        _chPWs[baseIdx + 1],
                        _chPWs[baseIdx + 2]
                    };

                    // Desired per-channel IPIs for this WSS (source of truth)
                    var desiredIpis = new[] {
                        _chIPIs[baseIdx + 0],
                        _chIPIs[baseIdx + 1],
                        _chIPIs[baseIdx + 2]
                    };

                    // Per-channel change memory, gated by per-WSS cooldown
                    bool anyChanged =
                        _lastIpiSentPerCh[baseIdx + 0] != desiredIpis[0] ||
                        _lastIpiSentPerCh[baseIdx + 1] != desiredIpis[1] ||
                        _lastIpiSentPerCh[baseIdx + 2] != desiredIpis[2];

                    if (anyChanged)
                    {
                        // Send one WSS-level IPI update (array API). This is the only time we send.
                        _ = _wss.StreamChange(new StreamChangeRequest
                        {
                            PulseAmplitudes = amps,
                            PulseWidths = pws,
                            InterPulseIntervals = desiredIpis
                        }, target);

                        // Update per-channel last-sent memory and start per-WSS cooldown
                        _lastIpiSentPerCh[baseIdx + 0] = desiredIpis[0];
                        _lastIpiSentPerCh[baseIdx + 1] = desiredIpis[1];
                        _lastIpiSentPerCh[baseIdx + 2] = desiredIpis[2];
                    }
                    else
                    {
                        // Do not send any IPI during cooldown or if nothing changed
                        _ = _wss.StreamChange(new StreamChangeRequest
                        {
                            PulseAmplitudes = amps,
                            PulseWidths = pws
                        }, target);
                    }

                    await Task.Delay(_delayMsBetweenPackets, tk);
                }
            }
        }

        /// <summary>Stop the streaming background task.</summary>
        private async Task StopStreamingInternal()
        {
            CancellationTokenSource capturedCts;
            Task capturedTask;

            lock (_streamGate)
            {
                capturedCts = _streamCts;
                capturedTask = _streamTask;
                capturedCts?.Cancel();
            }

            try
            {
                if (capturedTask != null)
                    await capturedTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is the expected streaming shutdown path.
            }
            catch (Exception ex)
            {
                Log.Warn($"Streaming task ended during shutdown: {ex.Message}");
            }
            finally
            {
                lock (_streamGate)
                {
                    if (ReferenceEquals(_streamTask, capturedTask))
                        _streamTask = null;
                    if (ReferenceEquals(_streamCts, capturedCts))
                        _streamCts = null;
                }

                capturedCts?.Dispose();
            }
        }

        /// <summary>True when the current streaming task exists and has not terminated.</summary>
        private bool StreamTaskActive()
        {
            lock (_streamGate)
                return _streamTask != null && !_streamTask.IsCompleted;
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
            => await ScheduleSetupChangeAsync(t, true, newSteps);

        /// <summary>Append steps with explicit control over whether paused streaming resumes.</summary>
        private async Task ScheduleSetupChangeAsync(
            WssTarget t,
            bool resumeStreaming,
            params Func<Task<string>>[] newSteps)
        {
            bool stopActiveStream = false;

            await _setupGate.WaitAsync();
            try
            {
                if (!_steps.ContainsKey(t)) { _steps[t] = new List<Func<Task<string>>>(); _cursor[t] = 0; }
                _steps[t].AddRange(newSteps);

                bool streamTaskActive = StreamTaskActive();
                bool shouldResumeStreaming = resumeStreaming &&
                    (_state == CoreState.Streaming || streamTaskActive);

                if (!resumeStreaming)
                    _resumeStreamingAfter = false;
                else if (shouldResumeStreaming && _streamStopsInProgress == 0)
                    _resumeStreamingAfter = true;

                if (streamTaskActive)
                {
                    stopActiveStream = true;
                    _streamStopsInProgress++;
                }
                _state = CoreState.SettingUp;

                if (_streamStopsInProgress == 0)
                    EnsureSetupRunner();
            }
            finally { _setupGate.Release(); }

            if (!stopActiveStream) return;

            await StopStreamingInternal();

            await _setupGate.WaitAsync();
            try
            {
                _streamStopsInProgress--;
                if (_streamStopsInProgress == 0)
                    EnsureSetupRunner();
            }
            finally { _setupGate.Release(); }
        }

        /// <summary>Start the background runner if needed.</summary>
        private void EnsureSetupRunner()
        {
            if (_setupRunner != null && !_setupRunner.IsCompleted) return;
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

                await _setupGate.WaitAsync();
                try
                {
                    if (_streamStopsInProgress == 0 && SetupQueueEmpty())
                    {
                        if (_resumeStreamingAfter) { StartStreamingInternal(); _resumeStreamingAfter = false; }
                    }
                }
                finally { _setupGate.Release(); }
                // else: leave state as SettingUp; Tick() will Observe runner completed + !empty and spawn another pass
            }
            catch (Exception ex)
            {
                Log.Error($"[SetupWorker] {ex.Message}");
                throw;
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
        /// <summary> Initializes arrays to hold values neccessary for streaming</summary>
        private void InitStimArrays()
        {
            _maxWSSChannels = _maxWSS * 3;
            _chAmps = new float[_maxWSSChannels];
            _chPWs = new int[_maxWSSChannels];
            _chIPIs = new int[_maxWSSChannels];
            _lastIpiSentPerCh = new int[_maxWSSChannels];
            for (int i = 0; i < _maxWSSChannels; i++)
            {
                // Store amplitude in mA; convert to 0..255 at send time using per-target curve
                _chAmps[i] = _defaultAmp;
                _chPWs[i] = 0;
                _chIPIs[i] = _defaultIPI;
                _lastIpiSentPerCh[i] = _defaultIPI;
            }
        }

        // Selects curve based on config or per-target unit settings.
        private int AmpTo255Convention(float amp, WssTarget target)
        {
            amp = Math.Max(0f, amp);
            // Choose curve source: config override sets key to Custom, else use device mode mapping
            string key = _coreConfig.UseConfigAmpCurves ? "Custom" : "72mA";
            if (!_coreConfig.UseConfigAmpCurves)
            {
                if (_unitSettings.TryGetValue(target, out var s) && s != null)
                    key = s.AmpCurveKey ?? "72mA";
            }
            double lowThresh;
            double cLow;
            double exp;
            double linOffset;
            double linSlope;
            switch (key)
            {
                case "10mA":
                    {
                        // Proportional scaling of the 72mA curve for a 10mA unit
                        const double scale = 10.0 / 72.0; // ~0.1389
                        lowThresh = 4.0 * scale;         // ~0.5556 mA
                        cLow = 0.0522 * scale;           // scale low-range constant
                        exp = 1.0 / 1.5466;              // same nonlinearity exponent
                        linOffset = 1.7045 * scale;      // scale linear offset
                        linSlope = 0.3396 * scale;       // scale linear slope
                        break;
                    }
                case "Custom":
                    {
                        var curves = _coreConfig.AmpCurves;
                        var idx = TargetToIndex(target);
                        var p = (curves != null && idx >= 0 && idx < curves.Length && curves[idx] != null)
                            ? curves[idx]
                            : AmpCurveParams.Default72mA();
                        lowThresh = p.LowThreshold;
                        cLow = p.LowConst;
                        exp = p.ExpPower;
                        linOffset = p.LinearOffset;
                        linSlope = p.LinearSlope;
                        break;
                    }
                case "72mA":
                default:
                    {
                        lowThresh = 4.0;         // mA
                        cLow = 0.0522;           // low-range constant
                        exp = 1.0 / 1.5466;      // nonlinearity exponent
                        linOffset = 1.7045;      // linear offset
                        linSlope = 0.3396;       // linear slope
                        break;
                    }
            }
            double v;
            if (amp < lowThresh)
            {
                v = Math.Pow(amp / cLow, exp) + 1.0;
            }
            else
            {
                v = ((amp + linOffset) / linSlope) + 1.0;
            }
            return (int)Math.Max(0, Math.Min(255, v));
        }

        /// <summary>  Helper to map WssTarget to index 0..2 for config arrays </summary>
        private static int TargetToIndex(WssTarget t)
        {
            return t switch
            {
                WssTarget.Wss1 => 0,
                WssTarget.Wss2 => 1,
                WssTarget.Wss3 => 2,
                _ => 0
            };
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

        /// <summary> Helper: capture and report unit settings for a target. Logs a warning when not available. </summary>
        private Task<string> CaptureUnitSettingsAsync(WssTarget t)
        {
            if (TryGetModuleSettings(t, out var s) && s != null)
            {
                _unitSettings[t] = s;
                var src = s.ProbeSupported ? "querryProbe" : "default";
                return Task.FromResult($"from {src} are {s.AmpCurveKey}");
            }
            var warn = $"not available";
            Log.Warn($"UnitSettings[{t}]: {warn}");
            return Task.FromResult($"{warn}");
        }
        
        /// <summary>
        /// Tries to obtain the last decoded ModuleSettings for a target by reading
        /// the client's cached ModuleQuery data and decoding it. Returns false if
        /// no data is cached or the payload is incomplete; when false, 'settings'
        /// may still be non-null but marked as partial.
        /// </summary>
        public bool TryGetModuleSettings(WssTarget target, out ModuleSettings settings)
        {
            settings = null;
            if (_wss == null) return false;
            if (!_wss.TryGetModuleQueryData(target, out var data) || data == null)
                return false;
            return ModuleSettings.TryDecode(data, out settings);
        }

        private static void ValidateScheduleState(ScheduleState state)
        {
            if (state != ScheduleState.Active && state != ScheduleState.Ready && state != ScheduleState.Suspend)
                throw new ArgumentOutOfRangeException(nameof(state), "State must be Active, Ready, or Suspend.");
        }
        #endregion
    }
}
