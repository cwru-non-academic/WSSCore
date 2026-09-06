using System;
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
            string fixtureDirectory = CreateFixtureDirectory(out string coreConfigPath, out _);
            var transport = new EmulatedWssTransport();
            WssStimulationCore core = null;
            try
            {
                core = CreateCore(transport, coreConfigPath);
                await InitializeAndValidateAsync(core, transport);
                var baseline = transport.Conformance.CaptureStimulationBaseline();

                core.StimulateAnalog(1, 237, 4.0f, 13);

                var result = await WaitForConformanceAsync(core, transport, baseline, 237);
                Assert.That(result.Passed, Is.True, string.Join(Environment.NewLine, result.Failures));
            }
            finally
            {
                core?.Dispose();
                if (core == null) transport.Dispose();
                DeleteFixtureDirectory(fixtureDirectory);
            }
        }

        // PW mode fixture: x > 0 maps to Round(21 + (221 - 21) * x); zero uses the explicit zero branch.
        [TestCase(0.0f, 0, TestName = "Normalized_0_0_PassesWireLevelConformance")]
        [TestCase(0.37f, 95, TestName = "Normalized_0_37_PassesWireLevelConformance")]
        [TestCase(0.5f, 121, TestName = "Normalized_0_5_PassesWireLevelConformance")]
        [TestCase(1.0f, 221, TestName = "Normalized_1_0_PassesWireLevelConformance")]
        [TestCase(-0.2f, 0, TestName = "Normalized_BelowZero_ClampsToZero")]
        [TestCase(1.2f, 221, TestName = "Normalized_AboveOne_ClampsToOne")]
        public async Task NormalizedStimulationPassesWireLevelConformance(float normalized, int expectedPulseWidth)
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

                layer.StimulateNormalized(1, normalized);

                var result = await WaitForConformanceAsync(layer, transport, baseline, expectedPulseWidth);
                Assert.That(result.Passed, Is.True, string.Join(Environment.NewLine, result.Failures));
            }
            finally
            {
                layer?.Dispose();
                if (layer == null) transport.Dispose();
                DeleteFixtureDirectory(fixtureDirectory);
            }
        }

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
            for (int i = 0; i < 2000; i++)
            {
                core.Tick();
                readyObserved = readyObserved || core.Ready();
                if (readyObserved && transport.Conformance.StimulationHistory.Any())
                    break;
                await Task.Delay(1);
            }

            var initialization = transport.Conformance.ValidateInitialization();
            Assert.Multiple(() =>
            {
                Assert.That(readyObserved, Is.True, "Core did not reach Ready within the finite tick limit.");
                Assert.That(initialization.Passed, Is.True, string.Join(Environment.NewLine, initialization.Failures));
            });
        }

        private static async Task<StimulationConformanceResult> WaitForConformanceAsync(
            IStimulationCore core,
            EmulatedWssTransport transport,
            WssStimulationBaseline baseline,
            int pulseWidth)
        {
            var expectation = new WssStimulationExpectation(
                0x81,
                1,
                4,
                pulseWidth,
                13,
                (byte)WSSMessageIDs.StreamChangeAll);
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
            string directory = Path.Combine(Path.GetTempPath(), $"wss-stimulation-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            coreConfigPath = Path.Combine(directory, "stimConfig.json");
            stimConfigPath = Path.Combine(directory, "stimParams.json");

            // This custom linear segment makes the hardware PA equal to mA: ((amp - 1) / 1) + 1.
            File.WriteAllText(coreConfigPath,
                "{\"maxWSS\":1,\"firmware\":\"J03\",\"broadcastTarget\":\"0x8F\"," +
                "\"wssTargets\":[\"0x81\",\"0x82\",\"0x83\"],\"useConfigAmpCurves\":true," +
                "\"ampCurves\":[{\"LowThreshold\":0.0,\"LowConst\":1.0,\"ExpPower\":1.0," +
                "\"LinearOffset\":-1.0,\"LinearSlope\":1.0}]}");
            File.WriteAllText(stimConfigPath,
                "{\"stim\":{\"ch\":{\"1\":{\"ampMode\":\"PW\",\"minPW\":21,\"maxPW\":221," +
                "\"minPA\":0.0,\"maxPA\":0.0,\"defaultPA\":4.0,\"defaultPW\":50,\"IPI\":13}}}}");
            return directory;
        }

        private static void DeleteFixtureDirectory(string directory)
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }
}
