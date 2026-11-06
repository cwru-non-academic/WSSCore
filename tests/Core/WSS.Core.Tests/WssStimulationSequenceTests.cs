using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Wss.CalibrationModule;
using Wss.CoreModule;
using Wss.Testing;
using Wss.ModelModule;

namespace WSS.Core.Tests
{
    [TestFixture]
    public sealed class WssStimulationSequenceTests
    {
        private static IEnumerable<TestCaseData> SingleCases => WssStimulationSequences.SingleDevice
            .Concat(WssStimulationSequences.LayeredSingleDevice)
            .Select(item => new TestCaseData(item).SetName(item.Id));
        private static IEnumerable<TestCaseData> MultiCases => WssStimulationSequences.MultipleDevices
            .Concat(WssStimulationSequences.LayeredMultipleDevices)
            .Select(item => new TestCaseData(item).SetName(item.Id));

        [TestCaseSource(nameof(SingleCases))]
        public Task SingleDeviceSequence(WssStimulationSequence scenario) => Run(scenario);

        [TestCaseSource(nameof(MultiCases)), Category("MultiDevice")]
        public Task MultipleDeviceSequence(WssStimulationSequence scenario) => Run(scenario);

        [Test]
        public void RequiredSequenceInventoryCannotDisappearWithCatalogEntries()
        {
            Assert.That(WssStimulationSequences.SingleDevice.Concat(WssStimulationSequences.MultipleDevices)
                .Concat(WssStimulationSequences.LayeredSingleDevice).Concat(WssStimulationSequences.LayeredMultipleDevices)
                .Select(item => item.Id), Is.EquivalentTo(new[] {
                    "analog.multichannel-isolation", "normalized.multichannel-isolation",
                    "analog.multidevice-isolation", "normalized.multidevice-isolation",
                    "modeled.p.pw.single", "modeled.p.pa.single", "modeled.pd.pw.single", "modeled.pd.pa.single",
                    "modeled.p.pw.multi", "modeled.p.pa.multi", "modeled.pd.pw.multi", "modeled.pd.pa.multi" }));
        }

        private static async Task Run(WssStimulationSequence scenario)
        {
            string directory = Path.Combine(Path.GetTempPath(), "wss-sequence-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                scenario.WriteFixture(directory);
                using var transport = new EmulatedWssTransport();
                using var core = new WssStimulationCore(transport, new WssStimulationCoreOptions
                { ConfigPath = Path.Combine(directory, "stimConfig.json"), MaxSetupTries = 1 });
                using var layer = new StimParamsLayer(core, Path.Combine(directory, "stimParams.json"));
                var model = new ModelParamsLayer(layer, directory);
                model.Initialize();
                bool initialized = false;
                for (int poll = 0; poll < 3000; poll++)
                {
                    model.Tick();
                    initialized = model.Started() && Enumerable.Range(0, scenario.DeviceCount).All(unit =>
                        transport.Conformance.StimulationHistory.Any(item => item.Target == 0x81 + unit));
                    if (initialized) break;
                    await Task.Delay(1);
                }
                Assert.That(initialized, Is.True, scenario.Id + ": not all targets started.");
                Assert.That(transport.Conformance.ValidateInitialization().Passed, Is.True);
                foreach (var step in scenario.Steps)
                {
                    var baseline = transport.Conformance.CaptureStimulationBaseline();
                    step.PrepareReload(directory);
                    if (step.ReloadParameters) model.LoadParamsJson();
                    if (step.ReloadModel) model.GetModelConfigController().LoadModelJson();
                    if (step.Modeled) model.StimWithMode(step.GlobalChannel, step.Input);
                    else if (step.Normalized) model.StimulateNormalized(step.GlobalChannel, step.Input);
                    else model.StimulateAnalog(step.GlobalChannel, step.Expectation.PulseWidth,
                        step.Expectation.PulseAmplitude, step.Expectation.InterPulseInterval);
                    WssStimulationWindowResult result = null;
                    for (int poll = 0; poll < 2000; poll++)
                    {
                        model.Tick();
                        result = WssStimulationWindow.Evaluate(transport.Conformance, baseline, step.Expectation, scenario.DeviceCount);
                        if (result.Failures.Count > 0 || result.Settled) break;
                        await Task.Delay(1);
                    }
                    Assert.That(result.Passed, Is.True, scenario.Id + "/" + step.Id + ": " + result.Diagnostics);
                }
            }
            finally { Directory.Delete(directory, true); }
        }
    }
}
