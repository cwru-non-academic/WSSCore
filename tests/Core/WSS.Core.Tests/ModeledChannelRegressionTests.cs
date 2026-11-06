using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Wss.CalibrationModule;
using Wss.CoreModule;
using Wss.ModelModule;
using Wss.Testing;

namespace WSS.Core.Tests
{
    [TestFixture]
    public sealed class ModeledChannelRegressionTests
    {
        // Synthetic positive inputs isolate the model path; the user's actual input/configuration is unknown.
        [TestCase(0.25f, 62), TestCase(0.5f, 75), TestCase(1.0f, 100)]
        public async Task ChannelTwoIdentityModelMatchesDirectNormalizedWithNonzeroCalibration(float input, int expectedWidth)
        {
            string directory = Path.Combine(Path.GetTempPath(), "wss-modeled-regression-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                WssStimulationSequences.SingleDevice[0].WriteFixture(directory);
                // Supplied stimulation parameters. Model constants below remain an explicit controlled fixture.
                File.WriteAllText(Path.Combine(directory, "stimParams.json"),
                    "{\"stim\":{\"ch\":{\"1\":{\"ampMode\":\"PW\",\"minPW\":50,\"maxPW\":100,\"defaultPA\":5,\"IPI\":10}," +
                    "\"2\":{\"ampMode\":\"PW\",\"minPW\":50,\"maxPW\":100,\"maxPA\":1.0,\"minPA\":3.0,\"defaultPA\":3.0,\"defaultPW\":40,\"IPI\":10}}}}");
                File.WriteAllText(Path.Combine(directory, "modelConfig.json"),
                    "{\"calib\":{\"mode\":\"P\"},\"constants\":{\"PModeProportional\":1.0,\"PModeOffset\":0.0}}");
                using var transport = new EmulatedWssTransport();
                using var core = new WssStimulationCore(transport, new WssStimulationCoreOptions
                { ConfigPath = Path.Combine(directory, "stimConfig.json"), MaxSetupTries = 1 });
                using var parameters = new StimParamsLayer(core, Path.Combine(directory, "stimParams.json"));
                var model = new ModelParamsLayer(parameters, directory);
                model.Initialize();
                bool started = false;
                for (int poll = 0; poll < 3000; poll++)
                {
                    model.Tick();
                    started = model.Started() && transport.Conformance.StimulationHistory.Any();
                    if (started) break;
                    await Task.Delay(1);
                }
                Assert.That(started, Is.True, "The emulator must be streaming before comparing API paths.");

                var baseline = transport.Conformance.CaptureStimulationBaseline();
                model.StimWithMode(1, input);
                await AssertWindow(model, transport, baseline, 5, expectedWidth, 10, "channel one modeled control", 1);

                baseline = transport.Conformance.CaptureStimulationBaseline();
                model.StimulateAnalog(2, 10, 10, 10);
                await AssertWindow(model, transport, baseline, 10, 10, 10, "analog control");

                baseline = transport.Conformance.CaptureStimulationBaseline();
                model.StimulateNormalized(2, input);
                await AssertWindow(model, transport, baseline, 3, expectedWidth, 10, "normalized control");

                baseline = transport.Conformance.CaptureStimulationBaseline();
                model.StimulateAnalog(2, 10, 10, 10);
                await AssertWindow(model, transport, baseline, 10, 10, 10, "restore analog control");

                baseline = transport.Conformance.CaptureStimulationBaseline();
                model.StimWithMode(2, input);
                await AssertWindow(model, transport, baseline, 3, expectedWidth, 10, "identity P model");
            }
            finally { Directory.Delete(directory, true); }
        }

        private static async Task AssertWindow(ModelParamsLayer model, EmulatedWssTransport transport,
            WssStimulationBaseline baseline, int amplitude, int width, int ipi, string operation, int channel = 2)
        {
            var expectation = new WssStimulationExpectation(0x81, channel, amplitude, width, ipi, 0x30);
            WssStimulationWindowResult result = null;
            for (int poll = 0; poll < 2000; poll++)
            {
                model.Tick();
                result = WssStimulationWindow.Evaluate(transport.Conformance, baseline, expectation, 1);
                if (result.Settled || result.Failures.Count > 0) break;
                await Task.Delay(1);
            }
            Assert.That(result.Passed, Is.True, operation + ": " + result.Diagnostics);
        }
    }
}
