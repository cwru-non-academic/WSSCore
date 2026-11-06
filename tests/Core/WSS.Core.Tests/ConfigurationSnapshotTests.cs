using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Wss.CoreModule;
using WssConfigurationSnapshot = Wss.CoreModule.WssConfigurationSnapshot;
using Wss.ModelModule;
using Wss.Testing;

namespace WSS.Core.Tests
{
    [TestFixture]
    public sealed class ConfigurationSnapshotTests
    {
        [Test]
        public void NoRestartPublicationAndStimulationProduceOnlyCompleteWireValues()
        {
            string directory = Path.Combine(Path.GetTempPath(), "wss-publish-race-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(Path.Combine(directory, "stimConfig.json"), "{\"firmware\":\"J03\",\"useConfigAmpCurves\":true,\"ampCurves\":[{\"LowThreshold\":0,\"LowConst\":1,\"ExpPower\":1,\"LinearOffset\":-1,\"LinearSlope\":1}]}");
                string path = Path.Combine(directory, "stimParams.json");
                File.WriteAllText(path, "{\"stim\":{\"ch\":{\"1\":{\"minPW\":50,\"maxPW\":150,\"defaultPA\":2,\"IPI\":10}}}}");
                var first = WssConfigurationSnapshot.ReadDefaults(directory);
                File.WriteAllText(path, "{\"stim\":{\"ch\":{\"1\":{\"minPW\":100,\"maxPW\":200,\"defaultPA\":4,\"IPI\":13}}}}");
                var second = first.ReloadDefaults();
                var transport = new EmulatedWssTransport();
                using var stack = first.BuildAndInitialize(transport,
                    new WssStimulationCoreOptions { ConfigPath = directory }, WssConfigurationLayer.Model);
                var model = (IModelParamsCore)stack;
                var timer = System.Diagnostics.Stopwatch.StartNew();
                while (!stack.Started() && timer.ElapsedMilliseconds < 3000) { stack.Tick(); Thread.Sleep(1); }
                Assert.That(stack.Started(), Is.True);
                var publisher = Task.Run(() =>
                {
                    for (int i = 0; i < 200; i++) { (i % 2 == 0 ? first : second).ApplyWithoutRestart(stack); Thread.Sleep(1); }
                });
                var producer = Task.Run(() =>
                {
                    for (int i = 0; i < 200; i++) { model.StimWithMode(1, .5f); Thread.Sleep(1); }
                });
                Assert.That(Task.WaitAll(new[] { publisher, producer }, 10000), Is.True);
                var observations = transport.Conformance.StimulationHistory.Where(item => item.Channel == 1 && item.PulseWidth != 0).ToArray();
                Assert.That(observations, Is.Not.Empty);
                Assert.That(observations.All(item =>
                    (item.PulseWidth == 100 && item.PulseAmplitude == 2 && item.InterPulseInterval == 10) ||
                    (item.PulseWidth == 150 && item.PulseAmplitude == 4 && item.InterPulseInterval == 13)), Is.True,
                    "Wire frames must not combine values from different configuration publications.");
            }
            finally { Directory.Delete(directory, true); }
        }
        [Test]
        public void SnapshotReconstructionDoesNotReadOrBackfillFilesAndKeepsStartupPaths()
        {
            string directory = Path.Combine(Path.GetTempPath(), "wss-frozen-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(Path.Combine(directory, "stimConfig.json"), "{\"maxWSS\":1,\"firmware\":\"J03\"}");
                var defaults = WssConfigurationSnapshot.ReadDefaults(directory);
                Assert.That(Directory.GetFiles(directory).Length, Is.EqualTo(1), "Staging must not create missing calibration/model files.");
                string profile = Path.Combine(directory, "profile"); Directory.CreateDirectory(profile);
                File.WriteAllText(Path.Combine(profile, "stimConfig.json"), "{\"maxWSS\":2,\"firmware\":\"J03\"}");
                var snapshot = defaults.Reload(profile);
                File.WriteAllText(Path.Combine(profile, "stimConfig.json"), "invalid after validation");
                using var stack = snapshot.BuildAndInitialize(new EmulatedWssTransport(),
                    new WssStimulationCoreOptions { ConfigPath = directory, MaxSetupTries = 1 }, WssConfigurationLayer.Model);
                Assert.That(stack.GetCoreConfigController().MaxWss, Is.EqualTo(2));
                Assert.That(snapshot.DefaultCorePath, Is.EqualTo(defaults.CorePath));
                Assert.That(Directory.GetFiles(profile).Length, Is.EqualTo(1));
                Assert.That(File.ReadAllText(Path.Combine(profile, "stimConfig.json")), Is.EqualTo("invalid after validation"));
                var curves = stack.GetCoreConfigController().AmpCurves;
                curves[0].LinearSlope = 99;
                using var independent = snapshot.BuildAndInitialize(new EmulatedWssTransport(),
                    new WssStimulationCoreOptions { ConfigPath = directory }, WssConfigurationLayer.Model);
                Assert.That(independent.GetCoreConfigController().AmpCurves[0].LinearSlope, Is.Not.EqualTo(99), "Snapshots cannot retain mutable installed arrays.");
            }
            finally { Directory.Delete(directory, true); }
        }

        [Test]
        public void NoRestartPublicationRejectsCoreChangeBeforeMutatingTheStack()
        {
            string directory = Path.Combine(Path.GetTempPath(), "wss-snapshot-publish-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var original = WssConfigurationSnapshot.ReadDefaults(directory);
                using var stack = original.BuildAndInitialize(new EmulatedWssTransport(),
                    new WssStimulationCoreOptions { ConfigPath = directory }, WssConfigurationLayer.Model);
                File.WriteAllText(Path.Combine(directory, "stimConfig.json"), "{\"maxWSS\":2}");
                var changed = original.ReloadCore();
                Assert.Throws<InvalidOperationException>(() => changed.ApplyWithoutRestart(stack));
                Assert.That(stack.GetCoreConfigController().MaxWss, Is.EqualTo(1));
                Assert.That(Directory.GetFiles(directory).Select(Path.GetFileName), Is.EquivalentTo(new[] { "stimConfig.json" }));
            }
            finally { Directory.Delete(directory, true); }
        }
    }
}
