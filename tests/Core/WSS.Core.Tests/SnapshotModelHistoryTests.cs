using System;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Wss.CalibrationModule;
using Wss.CoreModule;
using Wss.ModelModule;
using Wss.Testing;
using Snapshot = Wss.CoreModule.WssConfigurationSnapshot;

namespace WSS.Core.Tests
{
    [TestFixture]
    public sealed class SnapshotModelHistoryTests
    {
        [TestCase(1, 2), TestCase(2, 4)]
        public void NoRestartSnapshotAndPublicReloadPreserveDerivativeHistory(int devices, int channel)
        {
            using var fixture = new Fixture(devices);
            fixture.Model.Initialize();
            fixture.Time = 1;
            Assert.That(fixture.Sample(channel, .5f), Is.EqualTo(375));
            fixture.WriteModel(.5f);
            Snapshot.ReadDefaults(fixture.Directory).ApplyWithoutRestart(fixture.Model);
            fixture.Time = 2;
            Assert.That(fixture.Sample(channel, .75f), Is.EqualTo(500), "The derivative uses the previous sample with the new gain.");
            fixture.WriteModel(.25f);
            fixture.Model.LoadParamsJson();
            fixture.Time = 3;
            Assert.That(fixture.Sample(channel, .5f), Is.EqualTo(188), "Public no-restart reload retains magnitude and timestamp.");
        }

        [TestCase(1), TestCase(2)]
        public void FailedInstalledInitializationRetainsHistoryAndStopsTheSession(int clockRead)
        {
            using var fixture = new Fixture(2);
            using var control = new Fixture(2);
            foreach (var item in new[] { fixture, control })
            {
                item.Model.Initialize();
                item.Time = 1;
                Assert.That(item.Sample(4, .5f), Is.EqualTo(375));
            }
            fixture.Model.InstallSnapshot(ConfigurationBundle.Read(
                Path.Combine(fixture.Directory, "stimConfig.json"),
                Path.Combine(fixture.Directory, "stimParams.json"),
                Path.Combine(fixture.Directory, "modelConfig.json")));
            fixture.Time = 10;
            fixture.ThrowOnRead = fixture.ClockReads + clockRead;
            Assert.That(Assert.Throws<ApplicationException>(() => fixture.Model.InitializeInstalledConfiguration()).Message,
                Is.EqualTo("Clock initialization failed"));
            Assert.That(fixture.Model.Started(), Is.False);
            fixture.Time = control.Time = 11;
            // The emulator is stopped; the public parameter cache observes retained model math without physical I/O.
            Assert.That(fixture.Sample(4, .75f), Is.EqualTo(control.Sample(4, .75f)));
            Assert.That(fixture.Model.GetStimIntensity(4), Is.EqualTo(381));
        }

        private sealed class Fixture : IDisposable
        {
            internal readonly string Directory = Path.Combine(Path.GetTempPath(), "wss-snapshot-history-" + Guid.NewGuid().ToString("N"));
            internal readonly ModelParamsLayer Model;
            internal float Time;
            internal int ClockReads, ThrowOnRead = -1;
            internal Fixture(int devices)
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.WriteAllText(Path.Combine(Directory, "stimConfig.json"), "{\"maxWSS\":" + devices + ",\"firmware\":\"J03\"}");
                var channels = new JObject();
                for (int i = 1; i <= devices * 3; i++) channels[i.ToString()] = new JObject
                { ["ampMode"] = "PW", ["minPW"] = 0, ["maxPW"] = 1000, ["defaultPA"] = 3, ["IPI"] = 10 };
                File.WriteAllText(Path.Combine(Directory, "stimParams.json"), new JObject { ["stim"] = new JObject { ["ch"] = channels } }.ToString());
                WriteModel(.25f);
                var parameters = new StimParamsLayer(new WssStimulationCore(new EmulatedWssTransport(),
                    new WssStimulationCoreOptions { ConfigPath = Directory }), Directory);
                Model = new ModelParamsLayer(parameters, Directory, () =>
                {
                    if (++ClockReads == ThrowOnRead) throw new ApplicationException("Clock initialization failed");
                    return Time;
                });
            }
            internal void WriteModel(float derivative) => File.WriteAllText(Path.Combine(Directory, "modelConfig.json"),
                new JObject { ["calib"] = new JObject { ["mode"] = "PD" }, ["constants"] = new JObject
                { ["PDModeProportional"] = .5f, ["PDModeDerivative"] = derivative, ["PDModeOffset"] = 0f } }.ToString());
            internal float Sample(int channel, float input) { Model.StimWithMode(channel, input); return Model.GetStimIntensity(channel); }
            public void Dispose() { Model.Dispose(); System.IO.Directory.Delete(Directory, true); }
        }
    }
}
