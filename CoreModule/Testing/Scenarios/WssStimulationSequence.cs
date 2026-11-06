using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Wss.CoreModule;

namespace Wss.Testing
{
    /// <summary>Defines ordered public API operations with independent wire expectations.</summary>
    public sealed class WssStimulationSequence
    {
        internal WssStimulationSequence(string id, int deviceCount, IEnumerable<WssStimulationStep> steps,
            string modelMode = null, string amplitudeMode = "PW")
        {
            Id = id;
            DeviceCount = deviceCount;
            Steps = Array.AsReadOnly(steps.ToArray());
            ModelMode = modelMode;
            AmplitudeMode = amplitudeMode;
        }

        /// <summary>Gets the stable behavioral case identifier.</summary>
        public string Id { get; }
        /// <summary>Gets the number of emulated devices required by this case.</summary>
        public int DeviceCount { get; }
        /// <summary>Gets operations in execution order; each must settle before the next starts.</summary>
        public IReadOnlyList<WssStimulationStep> Steps { get; }
        /// <summary>Gets the model fixture mode, or null for direct analog/normalized scenarios.</summary>
        public string ModelMode { get; }
        /// <summary>Gets the calibration mode used by the fixture.</summary>
        public string AmplitudeMode { get; }

        /// <summary>Writes the deterministic configuration for this case into an existing disposable directory.</summary>
        /// <param name="directory">Existing fixture directory owned by the caller.</param>
        public void WriteFixture(string directory)
        {
            var curve = WssBehaviorScenarios.StimulationFixture;
            var channels = new Dictionary<string, object>();
            for (int channel = 1; channel <= DeviceCount * 3; channel++)
                channels[channel.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new
                {
                    ampMode = AmplitudeMode, minPW = 11 + 10 * channel,
                    maxPW = (ModelMode == null ? 111 : 139) + 10 * channel,
                    minPA = ModelMode == null ? 0 : channel - 1,
                    maxPA = ModelMode == null ? 0 : channel + 3,
                    defaultPA = channel + 1, defaultPW = ModelMode == null ? 50 : 40 + channel, IPI = 11 + 2 * channel
                };
            File.WriteAllText(Path.Combine(directory, "stimParams.json"),
                JsonConvert.SerializeObject(new { stim = new { ch = channels } }));
            File.WriteAllText(Path.Combine(directory, "stimConfig.json"), JsonConvert.SerializeObject(new
            {
                maxWSS = DeviceCount, firmware = "J03", broadcastTarget = "0x8F",
                wssTargets = new[] { "0x81", "0x82", "0x83" }, useConfigAmpCurves = true,
                ampCurves = Enumerable.Range(0, DeviceCount).Select(_ => new
                {
                    LowThreshold = curve.CurveLowThreshold, LowConst = curve.CurveLowConstant,
                    ExpPower = curve.CurveExponent, LinearOffset = curve.CurveLinearOffset,
                    LinearSlope = curve.CurveLinearSlope
                }).ToArray()
            }));
            if (ModelMode != null)
                File.WriteAllText(Path.Combine(directory, "modelConfig.json"), JsonConvert.SerializeObject(new
                {
                    calib = new { mode = ModelMode },
                    constants = new { PModeProportional = 0.5, PModeOffset = 0.125,
                        PDModeProportional = 0.5, PDModeOffset = 0.125, PDModeDerivative = 0.0 }
                }));
        }
    }

    /// <summary>Pairs one public API input with the expected device-local tuple.</summary>
    public sealed class WssStimulationStep
    {
        internal WssStimulationStep(string id, int channel, bool normalized, float input,
            int amplitude, int width, int ipi, bool modeled = false, bool reloadParameters = false, bool reloadModel = false,
            float modelOffset = 0.25f)
        {
            Id = id;
            GlobalChannel = channel;
            Normalized = normalized;
            Input = input;
            Modeled = modeled;
            ReloadParameters = reloadParameters;
            ReloadModel = reloadModel;
            ModelOffset = modelOffset;
            Expectation = new WssStimulationExpectation((byte)(0x81 + (channel - 1) / 3),
                (channel - 1) % 3 + 1, amplitude, width, ipi, (byte)WSSMessageIDs.StreamChangeAll);
        }

        /// <summary>Gets the stable step identifier for diagnostics.</summary>
        public string Id { get; }
        /// <summary>Gets the one-based channel passed to the public API, across devices.</summary>
        public int GlobalChannel { get; }
        /// <summary>Gets whether to call StimulateNormalized rather than StimulateAnalog.</summary>
        public bool Normalized { get; }
        /// <summary>Gets whether to invoke StimWithMode, including model and calibration layers.</summary>
        public bool Modeled { get; }
        /// <summary>Gets whether to reload the step's changed IPI before invoking stimulation.</summary>
        public bool ReloadParameters { get; }
        /// <summary>Gets whether to reload the declared model offset before invoking stimulation.</summary>
        public bool ReloadModel { get; }
        /// <summary>Gets the P/PD offset to write when reloading model configuration.</summary>
        public float ModelOffset { get; }
        /// <summary>Gets the normalized input; analog steps use the expectation tuple as input.</summary>
        public float Input { get; }
        /// <summary>Gets the expected device address, local slot and effective tuple.</summary>
        public WssStimulationExpectation Expectation { get; }

        /// <summary>Writes the declared fixture changes; the host must then call the appropriate public reload API.</summary>
        /// <param name="directory">Fixture directory owned by the host.</param>
        public void PrepareReload(string directory)
        {
            if (ReloadParameters)
            {
                string path = Path.Combine(directory, "stimParams.json");
                var json = JObject.Parse(File.ReadAllText(path));
                json["stim"]["ch"][GlobalChannel.ToString(System.Globalization.CultureInfo.InvariantCulture)]["IPI"] = Expectation.InterPulseInterval;
                File.WriteAllText(path, json.ToString());
            }
            if (ReloadModel)
            {
                string path = Path.Combine(directory, "modelConfig.json");
                var json = JObject.Parse(File.ReadAllText(path));
                json["constants"]["PModeOffset"] = ModelOffset;
                json["constants"]["PDModeOffset"] = ModelOffset;
                File.WriteAllText(path, json.ToString());
            }
        }
    }

    /// <summary>Owns the shared multi-channel behavior catalog.</summary>
    public static class WssStimulationSequences
    {
        /// <summary>Gets the complete P/PD and PW/PA matrix for one device.</summary>
        public static IReadOnlyList<WssStimulationSequence> LayeredSingleDevice { get; } = CreateLayered(1);
        /// <summary>Gets the same matrix for two devices, including channels three, four and six.</summary>
        public static IReadOnlyList<WssStimulationSequence> LayeredMultipleDevices { get; } = CreateLayered(2);

        private static IReadOnlyList<WssStimulationSequence> CreateLayered(int devices)
        {
            var scenarios = new List<WssStimulationSequence>();
            foreach (string model in new[] { "P", "PD" })
                foreach (string calibration in new[] { "PW", "PA" })
                {
                    var steps = new List<WssStimulationStep>();
                    for (int ch = 1; ch <= devices * 3; ch++)
                        steps.Add(new WssStimulationStep("analog-control." + ch, ch, false, 0,
                            ch + 1, 31 + 10 * ch, 11 + 2 * ch));
                    // Same input maps through non-identity P/PD constants to 0.375, not 0.5.
                    for (int ch = 1; ch <= devices * 3; ch++)
                        steps.Add(new WssStimulationStep("modeled." + ch, ch, false, 0.5f,
                            calibration == "PW" ? ch + 1 : ch,
                            calibration == "PW" ? 59 + 10 * ch : 40 + ch, 11 + 2 * ch, modeled: true));
                    // Raw zero still produces calculated output 0.125 because of the model offset.
                    steps.Add(new WssStimulationStep("raw-zero.offset", 2, false, 0,
                        calibration == "PW" ? 3 : 1, calibration == "PW" ? 47 : 42, 15, modeled: true));
                    steps.Add(new WssStimulationStep("reload-parameters", 2, false, 0.5f,
                        calibration == "PW" ? 3 : 2, calibration == "PW" ? 79 : 42, 16,
                        modeled: true, reloadParameters: true));
                    steps.Add(new WssStimulationStep("reload-model", 2, false, 0.5f,
                        3, calibration == "PW" ? 95 : 42, 16, modeled: true, reloadModel: true));
                    steps.Add(new WssStimulationStep("calculated-zero", 2, false, 0.5f,
                        calibration == "PW" ? 3 : 0, calibration == "PW" ? 0 : 42, 16,
                        modeled: true, reloadModel: true, modelOffset: -1f));
                    string id = "modeled." + model.ToLowerInvariant() + "." + calibration.ToLowerInvariant() +
                        (devices == 1 ? ".single" : ".multi");
                    scenarios.Add(new WssStimulationSequence(id, devices, steps, model, calibration));
                }
            return Array.AsReadOnly(scenarios.ToArray());
        }
        /// <summary>Gets mandatory single-device cases, independently inventoried by each host.</summary>
        public static IReadOnlyList<WssStimulationSequence> SingleDevice { get; } =
            Array.AsReadOnly(new[] { CreateAnalog(1), CreateNormalized(1) });

        /// <summary>Gets separately staged multi-device cases, including global channels three, four and six.</summary>
        public static IReadOnlyList<WssStimulationSequence> MultipleDevices { get; } =
            Array.AsReadOnly(new[] { CreateAnalog(2), CreateNormalized(2) });

        private static WssStimulationSequence CreateAnalog(int devices)
        {
            var steps = new List<WssStimulationStep>();
            for (int ch = 1; ch <= devices * 3; ch++)
                steps.Add(new WssStimulationStep("seed." + ch, ch, false, 0, ch + 1, 31 + 10 * ch, 11 + 2 * ch));
            // Each call changes one field while retaining the other two. Non-channel-one updates run first.
            foreach (int ch in Enumerable.Range(2, devices * 3 - 1).Concat(new[] { 1 }))
            {
                steps.Add(new WssStimulationStep("amplitude." + ch, ch, false, 0, ch + 2, 31 + 10 * ch, 11 + 2 * ch));
                steps.Add(new WssStimulationStep("width." + ch, ch, false, 0, ch + 2, 36 + 10 * ch, 11 + 2 * ch));
                steps.Add(new WssStimulationStep("ipi." + ch, ch, false, 0, ch + 2, 36 + 10 * ch, 12 + 2 * ch));
            }
            return new WssStimulationSequence(devices == 1 ? "analog.multichannel-isolation" : "analog.multidevice-isolation", devices, steps);
        }

        private static WssStimulationSequence CreateNormalized(int devices)
        {
            var steps = new List<WssStimulationStep>();
            for (int ch = 1; ch <= devices * 3; ch++)
                steps.Add(new WssStimulationStep("seed." + ch, ch, false, 0, ch + 1, 31 + 10 * ch, 11 + 2 * ch));
            for (int ch = 1; ch <= devices * 3; ch++)
                steps.Add(new WssStimulationStep("half." + ch, ch, true, 0.5f, ch + 1, 61 + 10 * ch, 11 + 2 * ch));
            foreach (int ch in Enumerable.Range(2, devices * 3 - 1).Concat(new[] { 1 }))
            {
                steps.Add(new WssStimulationStep("zero." + ch, ch, true, -0.2f, ch + 1, 0, 11 + 2 * ch));
                steps.Add(new WssStimulationStep("clamp." + ch, ch, true, 1.2f, ch + 1, 111 + 10 * ch, 11 + 2 * ch));
            }
            return new WssStimulationSequence(devices == 1 ? "normalized.multichannel-isolation" : "normalized.multidevice-isolation", devices, steps);
        }
    }
}
