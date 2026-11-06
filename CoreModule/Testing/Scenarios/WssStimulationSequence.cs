using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Wss.CoreModule;

namespace Wss.Testing
{
    /// <summary>Defines ordered public API operations with independent wire expectations.</summary>
    public sealed class WssStimulationSequence
    {
        internal WssStimulationSequence(string id, int deviceCount, IEnumerable<WssStimulationStep> steps)
        {
            Id = id;
            DeviceCount = deviceCount;
            Steps = Array.AsReadOnly(steps.ToArray());
        }

        /// <summary>Gets the stable behavioral case identifier.</summary>
        public string Id { get; }
        /// <summary>Gets the number of emulated devices required by this case.</summary>
        public int DeviceCount { get; }
        /// <summary>Gets operations in execution order; each must settle before the next starts.</summary>
        public IReadOnlyList<WssStimulationStep> Steps { get; }

        /// <summary>Writes the deterministic configuration for this case into an existing disposable directory.</summary>
        /// <param name="directory">Existing fixture directory owned by the caller.</param>
        public void WriteFixture(string directory)
        {
            var curve = WssBehaviorScenarios.StimulationFixture;
            var channels = new Dictionary<string, object>();
            for (int channel = 1; channel <= DeviceCount * 3; channel++)
                channels[channel.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new
                {
                    ampMode = "PW", minPW = 11 + 10 * channel, maxPW = 111 + 10 * channel,
                    minPA = 0, maxPA = 0, defaultPA = channel + 1, defaultPW = 50, IPI = 11 + 2 * channel
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
        }
    }

    /// <summary>Pairs one public API input with the expected device-local tuple.</summary>
    public sealed class WssStimulationStep
    {
        internal WssStimulationStep(string id, int channel, bool normalized, float input,
            int amplitude, int width, int ipi)
        {
            Id = id;
            GlobalChannel = channel;
            Normalized = normalized;
            Input = input;
            Expectation = new WssStimulationExpectation((byte)(0x81 + (channel - 1) / 3),
                (channel - 1) % 3 + 1, amplitude, width, ipi, (byte)WSSMessageIDs.StreamChangeAll);
        }

        /// <summary>Gets the stable step identifier for diagnostics.</summary>
        public string Id { get; }
        /// <summary>Gets the one-based channel passed to the public API, across devices.</summary>
        public int GlobalChannel { get; }
        /// <summary>Gets whether to call StimulateNormalized rather than StimulateAnalog.</summary>
        public bool Normalized { get; }
        /// <summary>Gets the normalized input; analog steps use the expectation tuple as input.</summary>
        public float Input { get; }
        /// <summary>Gets the expected device address, local slot and effective tuple.</summary>
        public WssStimulationExpectation Expectation { get; }
    }

    /// <summary>Owns the shared multi-channel behavior catalog.</summary>
    public static class WssStimulationSequences
    {
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
