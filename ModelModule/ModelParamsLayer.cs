// ModelParamsLayer.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json.Linq;
using Wss.CalibrationModule;
using Wss.CoreModule;
namespace Wss.ModelModule
{
    /// <summary>
    /// Model-driven layer (P/PD) that sits on top of an existing params layer.
    /// Computes controller output from magnitudes and stimulates via the wrapped core.
    /// </summary>
    public sealed class ModelParamsLayer : IModelParamsCore
    {
        private readonly IStimParamsCore _inner;          // params + core underneath
        private readonly ModelConfigController _modelCfg;  // model settings (mode + constants)
        private readonly Func<float> _elapsedSeconds;
        private float _timeOrigin;

        private JObject _appliedCore;
        internal StimParamsLayer Calibration => _inner as StimParamsLayer;

        internal void InitializeInstalledConfiguration()
        {
            lock (_inner.GetCoreConfigController().OperationGate)
                InitializeSession(() => Calibration.InitializeInstalledConfiguration(), _appliedCore);
        }

        internal void InstallSnapshot(ConfigurationBundle snapshot)
        {
            var core = _inner.GetCoreConfigController();
            lock (core.ConfigurationGate)
            {
                core.ApplySnapshot(snapshot.CorePath, snapshot.CoreValues);
                _inner.GetStimParamsConfigController().ApplySnapshot(snapshot.ParamsPath, snapshot.Params, snapshot.CoreValues.maxWSS);
                _modelCfg.ApplySnapshot(snapshot.ModelPath, snapshot.Model);
                Calibration.RefreshChannels();
                _appliedCore = (JObject)snapshot.Core.DeepClone();
            }
        }

        // model math state
        private bool _validMode;
        private float[] _prevMagnitude;
        private float[] _currentMag;
        private float[] _d_dt;
        private float[] _tPrev;
        private Stopwatch _timer;

        /// <summary>
        /// Initializes a model layer over an existing params/core implementation.
        /// </summary>
        /// <param name="inner">Wrapped stimulation params core.</param>
        /// <param name="modelPathOrDir">Model JSON file path or directory.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="inner"/> is null.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="modelPathOrDir"/> is null or whitespace.</exception>
        public ModelParamsLayer(IStimParamsCore inner, string modelPathOrDir)
            : this(inner, modelPathOrDir, null)
        {
        }

        // Instance-specific test clock; public callers retain the production stopwatch and min-dt rule.
        internal ModelParamsLayer(IStimParamsCore inner, string modelPathOrDir, Func<float> elapsedSeconds)
            : this(inner, modelPathOrDir, null, elapsedSeconds) { }

        internal ModelParamsLayer(IStimParamsCore inner, WssConfigurationSnapshot snapshot)
            : this(inner, snapshot.ModelPath, snapshot.CreateModelController(), null) { }

        private ModelParamsLayer(IStimParamsCore inner, string modelPathOrDir, ModelConfigController configuration, Func<float> elapsedSeconds)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _modelCfg = configuration ?? new ModelConfigController(modelPathOrDir);
            _modelCfg.UseGate(inner.GetCoreConfigController().ConfigurationGate);
            _elapsedSeconds = elapsedSeconds;
        }

        // ===== IModelParamsCore (extras) =====

        /// <inheritdoc/>
        public bool IsModeValid()
        {
            VerifyStimMode();
            return _validMode;
        }

        /// <inheritdoc/>
        public void StimWithMode(int ch, float magnitudeIn)
        {
            lock (_inner.GetCoreConfigController().OperationGate)
                _inner.StimulateNormalized(ch, CalculateMagnitude(ch, magnitudeIn));
        }

        /// <inheritdoc/>
        public ModelConfigController GetModelConfigController() => _modelCfg;

        // ===== IStimParamsCore (params surface → delegate) =====
        /// <inheritdoc/>
        public void StimulateNormalized(int channel, float normalizedValue) => _inner.StimulateNormalized(channel, normalizedValue);
        /// <inheritdoc/>
        public float GetStimIntensity(int channel) => _inner.GetStimIntensity(channel);
        /// <inheritdoc/>
        public void SaveParamsJson() => _inner.SaveParamsJson();
        /// <inheritdoc/>
        public void LoadParamsJson() => ReloadBundle(null, false, false);
        /// <inheritdoc/>
        public void LoadParamsJson(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Invalid configuration path.", nameof(path));
            ReloadBundle(path, false, false);
        }

        private void ReloadBundle(string path, bool activePaths, bool forceRestart)
        {
            var calibration = _inner as StimParamsLayer;
            var core = calibration?.ConcreteCore;
            if (core == null)
            {
                // Preserve compatibility with alternate IStimParamsCore implementations.
                if (forceRestart) Initialize();
                else if (path == null) _inner.LoadParamsJson(); else _inner.LoadParamsJson(path);
                return;
            }
            var coreConfig = _inner.GetCoreConfigController();
            var parameters = _inner.GetStimParamsConfigController();
            lock (coreConfig.OperationGate)
            {
                string corePath = activePaths ? coreConfig._configPath : coreConfig.StartupPath;
                string paramsPath = activePaths ? parameters.ActivePath : parameters.StartupPath;
                string modelPath = activePaths ? _modelCfg.ActivePath : _modelCfg.StartupPath;
                if (path != null)
                {
                    paramsPath = ConfigurationBundle.Resolve(path, "stimParams.json");
                    if (Directory.Exists(path) || path.EndsWith(Path.DirectorySeparatorChar.ToString()) || path.EndsWith(Path.AltDirectorySeparatorChar.ToString()))
                    {
                        corePath = ConfigurationBundle.Resolve(path, "stimConfig.json");
                        modelPath = ConfigurationBundle.Resolve(path, "modelConfig.json");
                    }
                    // A file overrides parameters only; the other two retain startup defaults.
                }
                var snapshot = ConfigurationBundle.Read(corePath, paramsPath, modelPath);
                var current = _appliedCore == null ? null : (JObject)_appliedCore.DeepClone();
                if (current != null)
                {
                    var values = coreConfig.CopyValues();
                    ConfigurationBundle.NormalizeAddresses(values);
                    foreach (var property in JObject.FromObject(values).Properties()) current[property.Name] = property.Value.DeepClone();
                }
                bool restart = forceRestart || current == null || !JToken.DeepEquals(current, snapshot.Core);
                if (restart) _inner.Shutdown();
                try
                {
                    if (restart)
                    {
                        InstallSnapshot(snapshot);
                        InitializeInstalledConfiguration();
                    }
                    else core.PublishConfiguration(() => InstallSnapshot(snapshot));
                    _appliedCore = (JObject)snapshot.Core.DeepClone();
                }
                catch (Exception error)
                {
                    // Validated paths/snapshots remain selected for an explicit retry.
                    // Do not restore old nonzero output after a restart failure.
                    try { _inner.Shutdown(); }
                    catch (Exception cleanupError) { error.Data["Wss.ModelInitialization.ShutdownException"] = cleanupError; }
                    throw;
                }
            }
        }
        /// <inheritdoc/>
        public StimParamsConfigController GetStimParamsConfigController() => _inner.GetStimParamsConfigController();

        /// <inheritdoc/>
        public void AddOrUpdateStimParam(string key, float value) => _inner.AddOrUpdateStimParam(key, value);
        /// <inheritdoc/>
        public float GetStimParam(string key) => _inner.GetStimParam(key);
        /// <inheritdoc/>
        public bool TryGetStimParam(string key, out float value) => _inner.TryGetStimParam(key, out value);
        /// <inheritdoc/>
        public Dictionary<string, float> GetAllStimParams() => _inner.GetAllStimParams();

        /// <inheritdoc/>
        public void SetChannelAmp(int ch, float mA) => _inner.SetChannelAmp(ch, mA);
        /// <inheritdoc/>
        public void SetChannelPAMin(int ch, float mA) => _inner.SetChannelPAMin(ch, mA);
        /// <inheritdoc/>
        public void SetChannelPAMax(int ch, float mA) => _inner.SetChannelPAMax(ch, mA);
        /// <inheritdoc/>
        public void SetChannelPWMin(int ch, int us) => _inner.SetChannelPWMin(ch, us);
        /// <inheritdoc/>
        public void SetChannelPWMax(int ch, int us) => _inner.SetChannelPWMax(ch, us);
        /// <inheritdoc/>
        public void SetChannelDefaultPW(int ch, int us) => _inner.SetChannelDefaultPW(ch, us);
        /// <inheritdoc/>
        public void SetChannelIPI(int ch, int ms) => _inner.SetChannelIPI(ch, ms);
        /// <inheritdoc/>
        public void SetChannelAmpMode(int ch, string mode) => _inner.SetChannelAmpMode(ch, mode);
        /// <inheritdoc/>
        public void SetAllChannelsAmpMode(string mode) => _inner.SetAllChannelsAmpMode(mode);
        /// <inheritdoc/>
        public void SetChannelMin(int ch, float value) => _inner.SetChannelMin(ch, value);
        /// <inheritdoc/>
        public void SetChannelMax(int ch, float value) => _inner.SetChannelMax(ch, value);
        /// <inheritdoc/>
        public void SetChannelDefault(int ch, float value) => _inner.SetChannelDefault(ch, value);

        /// <inheritdoc/>
        public float GetChannelAmp(int ch) => _inner.GetChannelAmp(ch);
        /// <inheritdoc/>
        public float GetChannelPAMin(int ch) => _inner.GetChannelPAMin(ch);
        /// <inheritdoc/>
        public float GetChannelPAMax(int ch) => _inner.GetChannelPAMax(ch);
        /// <inheritdoc/>
        public int GetChannelPWMin(int ch) => _inner.GetChannelPWMin(ch);
        /// <inheritdoc/>
        public int GetChannelPWMax(int ch) => _inner.GetChannelPWMax(ch);
        /// <inheritdoc/>
        public int GetChannelDefaultPW(int ch) => _inner.GetChannelDefaultPW(ch);
        /// <inheritdoc/>
        public int GetChannelIPI(int ch) => _inner.GetChannelIPI(ch);
        /// <inheritdoc/>
        public string GetChannelAmpMode(int ch) => _inner.GetChannelAmpMode(ch);
        /// <inheritdoc/>
        public float GetChannelMin(int ch) => _inner.GetChannelMin(ch);
        /// <inheritdoc/>
        public float GetChannelMax(int ch) => _inner.GetChannelMax(ch);
        /// <inheritdoc/>
        public float GetChannelDefault(int ch) => _inner.GetChannelDefault(ch);
        /// <inheritdoc/>
        public bool IsChannelInRange(int ch) => _inner.IsChannelInRange(ch);

        /// <inheritdoc/>
        public bool TryGetBasic(out IBasicStimulation basic) => _inner.TryGetBasic(out basic);

        /// <inheritdoc/>
        public bool TryGetAdvancedEventProgrammer(out IAdvancedEventProgrammer programmer) => _inner.TryGetAdvancedEventProgrammer(out programmer);

        // ===== IStimulationCore (lifecycle/core → delegate) =====
        /// <inheritdoc/>
        /// <remarks>
        /// A successful synchronous initialization starts a new model-history session. Reloads that do not restart
        /// Core preserve history. Direct initialization rejects a device count that differs from parameter capacity.
        /// Core owns shutdown and configuration loading during initialization. A failed attempt shuts down
        /// the inner session and retains prior model history. If cleanup fails, that exception is attached
        /// to the original error's Data under Wss.ModelInitialization.ShutdownException.
        /// </remarks>
        /// <exception cref="InvalidOperationException">The loaded topology differs from the parameter controller capacity.</exception>
        public void Initialize()
        {
            lock (_inner.GetCoreConfigController().OperationGate)
                InitializeSession(_inner.Initialize);
        }

        private void InitializeSession(Action initialize, JObject installedCore = null)
        {
            try
            {
                initialize();
                ValidateTopology();
                var applied = installedCore ?? JObject.FromObject(_inner.GetCoreConfigController().CopyValues());
                ResetModelArrays();
                _appliedCore = applied;
            }
            catch (Exception error)
            {
                try { _inner.Shutdown(); }
                catch (Exception cleanupError)
                {
                    error.Data["Wss.ModelInitialization.ShutdownException"] = cleanupError;
                }
                throw;
            }
        }

        private void ValidateTopology()
        {
            var parameters = _inner.GetStimParamsConfigController();
            int devices = _inner.GetCoreConfigController().MaxWss;
            if (devices < 1 || (long)devices * parameters.PerWss != parameters.TotalChannels)
            {
                throw new InvalidOperationException(
                    $"Configured device count {devices} does not match the parameter controller capacity " +
                    $"of {parameters.TotalChannels} channels. Construct a new complete stimulation stack after changing device count.");
            }
        }

        /// <inheritdoc/>
        public void Tick()
        {
            _inner.Tick(); // advance underlying core first
        }

        /// <inheritdoc/>
        public void Shutdown()
        {
            lock (_inner.GetCoreConfigController().OperationGate) _inner.Shutdown();
        }
        /// <inheritdoc/>
        public bool Started() => _inner.Started();
        /// <inheritdoc/>
        public bool Ready() => _inner.Ready();
        /// <inheritdoc/>
        public void StimulateAnalog(int ch, int pw, float amp, int ipi) => _inner.StimulateAnalog(ch, pw, amp, ipi);
        /// <inheritdoc/>
        public void ZeroOutStim(WssTarget t) => _inner.ZeroOutStim(t);
        /// <inheritdoc/>
        public void StartStim(WssTarget t) => _inner.StartStim(t);
        /// <inheritdoc/>
        public void StopStim(WssTarget t) => _inner.StopStim(t);
        /// <inheritdoc/>
        public void LoadConfigFile()
        {
            if (Calibration?.ConcreteCore == null) _inner.LoadConfigFile();
            else ReloadBundle(null, true, true);
        }
        /// <inheritdoc/>
        public CoreConfigController GetCoreConfigController() => _inner.GetCoreConfigController();
        /// <inheritdoc/>
        public void Dispose() => _inner.Dispose();

        // ===== helpers =====

        /// <summary>
        /// Ensures model-layer working arrays exist and match the current channel count.
        /// Starts the internal stopwatch on first use and initializes per-channel state.
        /// </summary>
        /// <remarks>
        /// Channel count is derived from the active parameter controller's total channel capacity.
        /// Arrays hold last magnitude, derivative estimate, and previous timestamps per channel.
        /// </remarks>
        private void EnsureModelArrays()
        {
            if (_currentMag == null) ResetModelArrays();
        }

        private float ElapsedSeconds => _elapsedSeconds == null
            ? _timer.ElapsedMilliseconds / 1000.0f
            : _elapsedSeconds() - _timeOrigin;

        private void ResetModelArrays()
        {
            // Prepare without mutating live history: a failed allocation/clock read must not partially reset it.
            var timer = Stopwatch.StartNew();
            float origin = _elapsedSeconds == null ? 0f : _elapsedSeconds();
            int n = _inner.GetStimParamsConfigController().TotalChannels;

            var previous = new float[n];
            var current = new float[n];
            var derivative = new float[n];
            var previousTime = new float[n];

            float now = _elapsedSeconds == null ? timer.ElapsedMilliseconds / 1000.0f : _elapsedSeconds() - origin;
            for (int i = 0; i < n; i++) previousTime[i] = now;
            _timer = timer;
            _timeOrigin = origin;
            _prevMagnitude = previous;
            _currentMag = current;
            _d_dt = derivative;
            _tPrev = previousTime;
        }

        /// <summary>
        /// Verifies that the active controller mode in the model config is supported.
        /// </summary>
        /// <remarks>
        /// Sets the private flag <c>_validMode</c> to true for modes <c>"P"</c> or <c>"PD"</c>, false otherwise.
        /// Uses <c>_modelCfg.GetCalibMode()</c>.
        /// </remarks>
        private void VerifyStimMode()
        {
            string mode = _modelCfg.GetCalibMode(); // e.g., "P" or "PD"
            _validMode = mode == "P" || mode == "PD";
        }

        /// <summary>
        /// Computes a normalized output magnitude for a 1-based channel using the current controller mode.
        /// </summary>
        /// <param name="channel1">1-based channel index.</param>
        /// <param name="magnitude01">Input magnitude, clamped to [0,1].</param>
        /// <returns>
        /// Normalized output in [0,1] after applying P or PD mapping with model constants and a finite-difference derivative.
        /// Returns 0 if the channel index is out of range.
        /// </returns>
        /// <remarks>
        /// Updates per-channel caches: current magnitude, previous magnitude, derivative estimate, and last timestamp (seconds).
        /// PD mode uses <c>constants.PDModeDerivative</c>, <c>constants.PDModeProportional</c>,
        /// and <c>constants.PDModeOffset</c>. P mode uses <c>constants.PModeProportional</c>
        /// and <c>constants.PModeOffset</c>. Failed nested lookups use the existing built-in defaults;
        /// top-level constant keys are ignored. Reloads preserve per-channel model history.
        /// </remarks>
        private float CalculateMagnitude(int channel1, float magnitude01)
        {
            int idx = channel1 - 1;
            EnsureModelArrays();
            if ((uint)idx >= (uint)_currentMag.Length) return 0f;

            // time + derivative bookkeeping
            _currentMag[idx] = Clamp01(magnitude01);
            float tNow = ElapsedSeconds;
            // Avoid MathF for netstandard2.0: compute with double and cast back
            float dt = (float)Math.Max(1e-5, (double)tNow - _tPrev[idx]);
            _d_dt[idx] = (_currentMag[idx] - _prevMagnitude[idx]) / dt;
            _tPrev[idx] = tNow;
            _prevMagnitude[idx] = _currentMag[idx];

            // controllerSystem;
            string mode = _modelCfg.GetCalibMode();
            float GetConst(string key, float fb) => _modelCfg.TryGetModelParam("constants." + key, out float v) ? v : fb;

            float y =
                mode == "PD"
                ? _d_dt[idx] * GetConst("PDModeDerivative", 0.2f)
                + _currentMag[idx] * GetConst("PDModeProportional", 0.5f)
                + GetConst("PDModeOffset", 0.0f)
                : _currentMag[idx] * GetConst("PModeProportional", 1.0f)
                + GetConst("PModeOffset", 0.0f);

            return Clamp01(y);
        }

        /// <summary>
        /// Clamps a float to the inclusive range [0,1].
        /// </summary>
        /// <param name="x">Value to clamp.</param>
        /// <returns><c>0</c> if <paramref name="x"/> &lt; 0, <c>1</c> if &gt; 1, otherwise <paramref name="x"/>.</returns>
        private static float Clamp01(float x) => x < 0f ? 0f : (x > 1f ? 1f : x);
    }
}
