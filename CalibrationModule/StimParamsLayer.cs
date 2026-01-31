using System;
using System.Collections.Generic;
using Wss.CoreModule;

/// <summary>
/// Layer that wraps an <see cref="IStimulationCore"/> and a <see cref="StimParamsConfigController"/>.
/// Computes pulse widths from normalized inputs and forwards stimulation to the core.
/// Also exposes dotted-key parameter access and an optional BASIC capability.
/// </summary>

namespace Wss.CalibrationModule
{
    public sealed class StimParamsLayer : IStimParamsCore
    {
        private readonly IStimulationCore _core;          // required core
        private readonly StimParamsConfigController _ctrl;      // params context
        private readonly IBasicStimulation _basic;       // optional BASIC capability
        private int _totalChannels;
        private float[] _lastAmp;

        private enum ChannelValueKind { Min, Max, Default }

        /// <summary>
        /// Constructs the layer over an existing core and a params context path.
        /// </summary>
        /// <param name="core">Initialized stimulation core to wrap.</param>
        /// <param name="pathOrDir">Params file path or directory.</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="core"/> is null.</exception>
        public StimParamsLayer(IStimulationCore core, string pathOrDir)
        {
            _core = core ?? throw new ArgumentNullException(nameof(core));

            int maxWss = _core.GetCoreConfigController().MaxWss;
            _ctrl = new StimParamsConfigController(pathOrDir, maxWss);
            _basic = _core as IBasicStimulation;

            _totalChannels = _ctrl.PerWss * Math.Max(1, maxWss);
            _lastAmp = new float[_totalChannels];
            InitArrays();
        }

        // ---- IStimParamsCore ----

        /// <inheritdoc/>
        public void StimulateNormalized(int channel, float value01)
        {
            if (channel < 1 || channel > _totalChannels)
                throw new ArgumentOutOfRangeException(nameof(channel), $"Channel must be 1..{_totalChannels}.");

            // Clamp without Math.Clamp for netstandard2.0
            if (value01 < 0f) value01 = 0f; else if (value01 > 1f) value01 = 1f;

            string baseKey = $"stim.ch.{channel}";
            string ampMode = _ctrl.GetChannelAmpMode(channel);
            float ipi  = _ctrl.TryGetStimParam($"{baseKey}.IPI", out var ii) ? ii : 20f;

            int pulseWidth;
            float amp;
            float reportedDrive;

            if (ampMode == "PA")
            {
                float paMin = _ctrl.TryGetStimParam($"{baseKey}.minPA", out var minPa) ? minPa : 0f;
                float paMax = _ctrl.TryGetStimParam($"{baseKey}.maxPA", out var maxPa) ? maxPa : paMin;
                if (paMax < paMin) { var tmp = paMin; paMin = paMax; paMax = tmp; }

                float defPw = _ctrl.TryGetStimParam($"{baseKey}.defaultPW", out var dPw) ? dPw : 50f;
                pulseWidth = (int)Math.Round(defPw);
                if (value01 <= 0f)
                {
                    amp = 0f;
                }
                else
                {
                    amp = paMin + (paMax - paMin) * value01;
                }
                reportedDrive = amp;
            }
            else
            {
                float pwMin = _ctrl.TryGetStimParam($"{baseKey}.minPW", out var mn) ? mn : 50f;
                float pwMax = _ctrl.TryGetStimParam($"{baseKey}.maxPW", out var mx) ? mx : 500f;
                if (pwMax < pwMin) { var tmp = pwMin; pwMin = pwMax; pwMax = tmp; }

                amp = _ctrl.TryGetStimParam($"{baseKey}.defaultPA", out var defaultPa) ? defaultPa : 1f;
                if (value01 <= 0f)
                {
                    pulseWidth = 0;
                }
                else
                {
                    pulseWidth = (int)Math.Round(pwMin + (pwMax - pwMin) * value01);
                }
                reportedDrive = pulseWidth;
            }

            _lastAmp[channel - 1] = reportedDrive;
            _core.StimulateAnalog(channel, pulseWidth, amp, (int)Math.Round(ipi));
        }

        /// <inheritdoc/>
        public float GetStimIntensity(int channel)
        {
            if (channel < 1 || channel > _totalChannels)
                throw new ArgumentOutOfRangeException(nameof(channel), $"Channel must be 1..{_totalChannels}.");
            return _lastAmp[channel - 1];
        }

        /// <inheritdoc/>
        public StimParamsConfigController GetStimParamsConfigController()
        {
            return _ctrl;
        }

        /// <inheritdoc cref="StimParamsConfigController.SaveParamsJson"/>
        public void SaveParamsJson() => _ctrl.SaveParamsJson();

        /// <inheritdoc cref="StimParamsConfigController.LoadParamsJson()"/>
        public void LoadParamsJson() => _ctrl.LoadParamsJson();

        /// <inheritdoc cref="StimParamsConfigController.LoadParamsJson(string)"/>
        public void LoadParamsJson(string path) => _ctrl.LoadParamsJson(path);

        /// <inheritdoc cref="StimParamsConfigController.AddOrUpdateStimParam(string,float)"/>
        public void AddOrUpdateStimParam(string key, float value) => _ctrl.AddOrUpdateStimParam(key, value);

        /// <inheritdoc cref="StimParamsConfigController.GetStimParam(string)"/>
        public float GetStimParam(string key) => _ctrl.GetStimParam(key);

        /// <inheritdoc cref="StimParamsConfigController.TryGetStimParam(string,out float)"/>
        public bool TryGetStimParam(string key, out float value) => _ctrl.TryGetStimParam(key, out value);

        /// <inheritdoc cref="StimParamsConfigController.GetAllStimParams"/>
        public Dictionary<string, float> GetAllStimParams() => _ctrl.GetAllStimParams();

        /// <inheritdoc cref="StimParamsConfigController.SetChannelAmp(int,float)"/>
        public void SetChannelAmp(int ch, float mA) => _ctrl.SetChannelAmp(ch, mA);

        /// <inheritdoc cref="StimParamsConfigController.SetChannelPWMin(int,int)"/>
        public void SetChannelPWMin(int ch, int us) => _ctrl.SetChannelPWMin(ch, us);

        /// <inheritdoc cref="StimParamsConfigController.SetChannelPAMin(int,float)"/>
        public void SetChannelPAMin(int ch, float mA) => _ctrl.SetChannelPAMin(ch, mA);

        /// <inheritdoc cref="StimParamsConfigController.SetChannelPAMax(int,float)"/>
        public void SetChannelPAMax(int ch, float mA) => _ctrl.SetChannelPAMax(ch, mA);

        /// <inheritdoc cref="StimParamsConfigController.SetChannelPWMax(int,int)"/>
        public void SetChannelPWMax(int ch, int us) => _ctrl.SetChannelPWMax(ch, us);

        /// <inheritdoc cref="StimParamsConfigController.SetChannelDefaultPW(int,int)"/>
        public void SetChannelDefaultPW(int ch, int us) => _ctrl.SetChannelDefaultPW(ch, us);

        /// <inheritdoc cref="StimParamsConfigController.SetChannelIPI(int,int)"/>
        public void SetChannelIPI(int ch, int ms) => _ctrl.SetChannelIPI(ch, ms);

        /// <inheritdoc cref="StimParamsConfigController.SetChannelAmpMode(int,string)"/>
        public void SetChannelAmpMode(int ch, string mode) => _ctrl.SetChannelAmpMode(ch, mode);

        /// <inheritdoc cref="StimParamsConfigController.SetAllChannelsAmpMode(string)"/>
        public void SetAllChannelsAmpMode(string mode) => _ctrl.SetAllChannelsAmpMode(mode);

        /// <inheritdoc cref="IStimParamsCore.SetChannelMin(int,float)"/>
        public void SetChannelMin(int ch, float value) => SetChannelControlValue(ch, value, ChannelValueKind.Min);

        /// <inheritdoc cref="IStimParamsCore.SetChannelMax(int,float)"/>
        public void SetChannelMax(int ch, float value) => SetChannelControlValue(ch, value, ChannelValueKind.Max);

        /// <inheritdoc cref="IStimParamsCore.SetChannelDefault(int,float)"/>
        public void SetChannelDefault(int ch, float value) => SetChannelControlValue(ch, value, ChannelValueKind.Default);

        /// <inheritdoc cref="StimParamsConfigController.GetChannelAmp(int)"/>
        public float GetChannelAmp(int ch) => _ctrl.GetChannelAmp(ch);

        /// <inheritdoc cref="StimParamsConfigController.GetChannelPWMin(int)"/>
        public int GetChannelPWMin(int ch) => _ctrl.GetChannelPWMin(ch);

        /// <inheritdoc cref="StimParamsConfigController.GetChannelPAMin(int)"/>
        public float GetChannelPAMin(int ch) => _ctrl.GetChannelPAMin(ch);

        /// <inheritdoc cref="StimParamsConfigController.GetChannelPAMax(int)"/>
        public float GetChannelPAMax(int ch) => _ctrl.GetChannelPAMax(ch);

        /// <inheritdoc cref="StimParamsConfigController.GetChannelPWMax(int)"/>
        public int GetChannelPWMax(int ch) => _ctrl.GetChannelPWMax(ch);

        /// <inheritdoc cref="StimParamsConfigController.GetChannelDefaultPW(int)"/>
        public int GetChannelDefaultPW(int ch) => _ctrl.GetChannelDefaultPW(ch);

        /// <inheritdoc cref="StimParamsConfigController.GetChannelIPI(int)"/>
        public int GetChannelIPI(int ch) => _ctrl.GetChannelIPI(ch);

        /// <inheritdoc cref="StimParamsConfigController.GetChannelAmpMode(int)"/>
        public string GetChannelAmpMode(int ch) => _ctrl.GetChannelAmpMode(ch);

        /// <summary>
        /// Writes the correct backing parameter for the requested control value based on the channel's amp mode.
        /// </summary>
        private void SetChannelControlValue(int ch, float value, ChannelValueKind kind)
        {
            string mode = _ctrl.GetChannelAmpMode(ch);
            switch (mode)
            {
                case "PA":
                    switch (kind)
                    {
                        case ChannelValueKind.Min:
                            _ctrl.SetChannelPAMin(ch, value);
                            break;
                        case ChannelValueKind.Max:
                            _ctrl.SetChannelPAMax(ch, value);
                            break;
                        case ChannelValueKind.Default:
                            _ctrl.SetChannelDefaultPW(ch, (int)Math.Round(value));
                            break;
                    }
                    break;
                case "PW":
                default:
                    int rounded = (int)Math.Round(value);
                    switch (kind)
                    {
                        case ChannelValueKind.Min:
                            _ctrl.SetChannelPWMin(ch, rounded);
                            break;
                        case ChannelValueKind.Max:
                            _ctrl.SetChannelPWMax(ch, rounded);
                            break;
                        case ChannelValueKind.Default:
                            _ctrl.SetChannelAmp(ch, value);
                            break;
                    }
                    break;
            }
        }

        /// <summary>
        /// Reads the active control value for the requested channel, adapting to PW/PA modes.
        /// </summary>
        private float GetChannelControlValue(int ch, ChannelValueKind kind)
        {
            string mode = _ctrl.GetChannelAmpMode(ch);
            return mode switch
            {
                "PA" => kind switch
                {
                    ChannelValueKind.Min => _ctrl.GetChannelPAMin(ch),
                    ChannelValueKind.Max => _ctrl.GetChannelPAMax(ch),
                    ChannelValueKind.Default => _ctrl.GetChannelDefaultPW(ch),
                    _ => 0f
                },
                _ => kind switch
                {
                    ChannelValueKind.Min => _ctrl.GetChannelPWMin(ch),
                    ChannelValueKind.Max => _ctrl.GetChannelPWMax(ch),
                    ChannelValueKind.Default => _ctrl.GetChannelAmp(ch),
                    _ => 0f
                }
            };
        }

        /// <inheritdoc cref="IStimParamsCore.GetChannelMin(int)"/>
        public float GetChannelMin(int ch) => GetChannelControlValue(ch, ChannelValueKind.Min);

        /// <inheritdoc cref="IStimParamsCore.GetChannelMax(int)"/>
        public float GetChannelMax(int ch) => GetChannelControlValue(ch, ChannelValueKind.Max);

        /// <inheritdoc cref="IStimParamsCore.GetChannelDefault(int)"/>
        public float GetChannelDefault(int ch) => GetChannelControlValue(ch, ChannelValueKind.Default);

        /// <inheritdoc cref="StimParamsConfigController.IsChannelInRange(int)"/>
        public bool IsChannelInRange(int ch) => _ctrl.IsChannelInRange(ch);

        /// <inheritdoc/>
        public bool TryGetBasic(out IBasicStimulation basic)
        {
            if (_basic != null)
            {
                basic = _basic;
                return true;
            }
            basic = null!;
            return false;
        }

        // ---- IStimulationCore passthrough ----

        /// <inheritdoc cref="IStimulationCore.Initialize"/>
        public void Initialize()
        {
            _core.Initialize();
            _ctrl.LoadParamsJson(); // load params after core init
            int maxWss = _core.GetCoreConfigController().MaxWss;
            _totalChannels = _ctrl.PerWss * Math.Max(1, maxWss);
            _lastAmp = new float[_totalChannels];
            InitArrays();
        }

        /// <inheritdoc cref="IStimulationCore.Tick"/>
        public void Tick() => _core.Tick();

        /// <inheritdoc cref="IStimulationCore.Shutdown"/>
        public void Shutdown() => _core.Shutdown();

        /// <inheritdoc cref="IStimulationCore.Started"/>
        public bool Started() => _core.Started();

        /// <inheritdoc cref="IStimulationCore.Ready"/>
        public bool Ready() => _core.Ready();

        /// <inheritdoc cref="IStimulationCore.StimulateAnalog(string,int,float,int)"/>
        public void StimulateAnalog(int ch, int pw, float amp, int ipi) => _core.StimulateAnalog(ch, pw, amp, ipi);

        /// <inheritdoc cref="IStimulationCore.ZeroOutStim(WssTarget)"/>
        public void ZeroOutStim(WssTarget t) => _core.ZeroOutStim(t);

        /// <inheritdoc cref="IStimulationCore.StartStim(WssTarget)"/>
        public void StartStim(WssTarget t) => _core.StartStim(t);

        /// <inheritdoc cref="IStimulationCore.StopStim(WssTarget)"/>
        public void StopStim(WssTarget t) => _core.StopStim(t);

        /// <inheritdoc cref="IStimulationCore.LoadConfigFile"/>
        public void LoadConfigFile() => _core.LoadConfigFile();

        /// <inheritdoc cref="IStimulationCore.GetCoreConfigController"/>
        public CoreConfigController GetCoreConfigController() => _core.GetCoreConfigController();

        /// <inheritdoc cref="IDisposable.Dispose"/>
        public void Dispose() => _core.Dispose();

        // ---- StimParamsLayer-specific ----

        /// <summary>
        /// Initializes internal arrays that cache the last normalized drive result.
        /// </summary>
        private void InitArrays()
        {
            for (int i = 0; i < _lastAmp.Length; i++)
                _lastAmp[i] = 0f;
        }
    }
}
