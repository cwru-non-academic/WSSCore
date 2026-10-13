using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Wss.CalibrationModule;
using Wss.CoreModule;
using Wss.Testing;

namespace WSS.Core.Tests
{
    [TestFixture]
    public sealed class WssStimulationConformanceTests
    {
        [Test]
        public async Task DirectAnalogStimulationPassesWireLevelConformance()
        {
            var scenario = WssBehaviorScenarios.DirectAnalog;
            string fixtureDirectory = CreateFixtureDirectory(out string coreConfigPath, out _);
            var transport = new EmulatedWssTransport();
            WssStimulationCore core = null;
            try
            {
                core = CreateCore(transport, coreConfigPath);
                await InitializeAndValidateAsync(core, transport);
                var baseline = transport.Conformance.CaptureStimulationBaseline();

                core.StimulateAnalog(
                    scenario.Channel,
                    scenario.PulseWidth,
                    scenario.AmplitudeMa,
                    scenario.InterPulseInterval);

                var result = await WaitForConformanceAsync(core, transport, baseline, scenario.Expectation);
                Assert.That(result.Passed, Is.True, string.Join(Environment.NewLine, result.Failures));
            }
            finally
            {
                core?.Dispose();
                if (core == null) transport.Dispose();
                DeleteFixtureDirectory(fixtureDirectory);
            }
        }

        [TestCaseSource(nameof(NormalizedStimulationCases))]
        public async Task NormalizedStimulationPassesWireLevelConformance(
            WssNormalizedStimulationScenario scenario)
        {
            string fixtureDirectory = CreateFixtureDirectory(out string coreConfigPath, out string stimConfigPath);
            var transport = new EmulatedWssTransport();
            StimParamsLayer layer = null;
            try
            {
                var core = CreateCore(transport, coreConfigPath);
                layer = new StimParamsLayer(core, stimConfigPath);
                await InitializeAndValidateAsync(layer, transport);
                var baseline = transport.Conformance.CaptureStimulationBaseline();

                layer.StimulateNormalized(scenario.Channel, scenario.Input);

                var result = await WaitForConformanceAsync(layer, transport, baseline, scenario.Expectation);
                Assert.That(result.Passed, Is.True, string.Join(Environment.NewLine, result.Failures));
            }
            finally
            {
                layer?.Dispose();
                if (layer == null) transport.Dispose();
                DeleteFixtureDirectory(fixtureDirectory);
            }
        }

        [Test]
        public void NormalizedCaseSourceEnumeratesSharedCatalog()
        {
            var sourcedScenarios = NormalizedStimulationCases
                .Select(testCase => testCase.Arguments.Single())
                .Cast<WssNormalizedStimulationScenario>()
                .ToArray();

            Assert.That(sourcedScenarios, Is.EqualTo(WssBehaviorScenarios.NormalizedStimulation));
        }

        private static IEnumerable<TestCaseData> NormalizedStimulationCases =>
            WssBehaviorScenarios.NormalizedStimulation.Select(scenario =>
                new TestCaseData(scenario).SetName(scenario.Id));

        private static WssStimulationCore CreateCore(EmulatedWssTransport transport, string coreConfigPath)
        {
            return new WssStimulationCore(
                transport,
                new WssStimulationCoreOptions
                {
                    ConfigPath = coreConfigPath,
                    MaxSetupTries = 1,
                    DefaultAmp = 1.0f,
                    DefaultIpi = 10
                });
        }

        private static async Task InitializeAndValidateAsync(IStimulationCore core, EmulatedWssTransport transport)
        {
            core.Initialize();
            bool readyObserved = false;
            bool streamObserved = false;
            for (int i = 0; i < 2000; i++)
            {
                core.Tick();
                readyObserved = readyObserved || core.Ready();
                streamObserved = streamObserved || transport.Conformance.StimulationHistory.Any();
                if (readyObserved && streamObserved)
                    break;
                await Task.Delay(1);
            }

            var initialization = transport.Conformance.ValidateInitialization();
            Assert.Multiple(() =>
            {
                Assert.That(readyObserved, Is.True, "Core did not reach Ready within the finite tick limit.");
                Assert.That(streamObserved, Is.True, "Core did not emit a supported startup stream within the finite observation limit.");
                Assert.That(initialization.Passed, Is.True, string.Join(Environment.NewLine, initialization.Failures));
            });
        }

        private static async Task<StimulationConformanceResult> WaitForConformanceAsync(
            IStimulationCore core,
            EmulatedWssTransport transport,
            WssStimulationBaseline baseline,
            WssStimulationExpectation expectation)
        {
            StimulationConformanceResult result = null;
            for (int i = 0; i < 1000; i++)
            {
                core.Tick();
                result = transport.Conformance.ValidateStimulation(expectation, baseline);
                if (result.Passed)
                    return result;
                await Task.Delay(1);
            }

            return result;
        }

        private static string CreateFixtureDirectory(out string coreConfigPath, out string stimConfigPath)
        {
            var profile = WssBehaviorScenarios.StimulationFixture;
            string directory = Path.Combine(Path.GetTempPath(), $"wss-stimulation-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            coreConfigPath = Path.Combine(directory, "stimConfig.json");
            stimConfigPath = Path.Combine(directory, "stimParams.json");

            File.WriteAllText(coreConfigPath,
                $"{{\"maxWSS\":1,\"firmware\":\"J03\",\"broadcastTarget\":\"0x8F\"," +
                $"\"wssTargets\":[\"0x81\",\"0x82\",\"0x83\"],\"useConfigAmpCurves\":true," +
                $"\"ampCurves\":[{{\"LowThreshold\":{profile.CurveLowThreshold.ToString(CultureInfo.InvariantCulture)}," +
                $"\"LowConst\":{profile.CurveLowConstant.ToString(CultureInfo.InvariantCulture)}," +
                $"\"ExpPower\":{profile.CurveExponent.ToString(CultureInfo.InvariantCulture)}," +
                $"\"LinearOffset\":{profile.CurveLinearOffset.ToString(CultureInfo.InvariantCulture)}," +
                $"\"LinearSlope\":{profile.CurveLinearSlope.ToString(CultureInfo.InvariantCulture)}" +
                "}]}");
            File.WriteAllText(stimConfigPath,
                $"{{\"stim\":{{\"ch\":{{\"1\":{{\"ampMode\":\"{profile.AmplitudeMode}\"," +
                $"\"minPW\":{profile.MinimumPulseWidth},\"maxPW\":{profile.MaximumPulseWidth}," +
                $"\"minPA\":0.0,\"maxPA\":0.0," +
                $"\"defaultPA\":{profile.DefaultAmplitudeMa.ToString(CultureInfo.InvariantCulture)}," +
                $"\"defaultPW\":50,\"IPI\":{profile.InterPulseInterval}" +
                "}}}}");
            return directory;
        }

        private static void DeleteFixtureDirectory(string directory)
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }
}
