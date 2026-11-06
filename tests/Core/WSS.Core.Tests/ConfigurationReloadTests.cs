using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Wss.CalibrationModule;
using Wss.CoreModule;
using Wss.ModelModule;
using Wss.Testing;
using Wss.Transports;

namespace WSS.Core.Tests
{
    [TestFixture]
    public sealed class ConfigurationReloadTests
    {
        [TestCase("maxWSS", 2)]
        [TestCase("firmware", "H03")]
        [TestCase("broadcastTarget", "0x80")]
        [TestCase("wssTargets[0]", "0x84")]
        [TestCase("useConfigAmpCurves", false)]
        [TestCase("ampCurves[0].LowThreshold", 2)]
        [TestCase("ampCurves[0].LowConst", 2)]
        [TestCase("ampCurves[0].ExpPower", 2)]
        [TestCase("ampCurves[0].LinearOffset", 2)]
        [TestCase("ampCurves[0].LinearSlope", 2)]
        public async Task EveryCoreSettingChangeReinitializes(string key, object value)
        {
            using var f = new Fixture();
            await f.Start();
            string path = Path.Combine(f.Directory, "stimConfig.json");
            var json = JObject.Parse(File.ReadAllText(path));
            json.SelectToken(key)?.Replace(JToken.FromObject(value));
            if (json.SelectToken(key) == null) json[key] = JToken.FromObject(value);
            File.WriteAllText(path, json.ToString());
            f.Layer.LoadParamsJson();
            Assert.That(f.Transport.Connections, Is.EqualTo(2));
        }

        [Test]
        public async Task MutableCurveReferenceCannotHideAChangeFromTheSavedSnapshot()
        {
            using var f = new Fixture();
            await f.Start();
            var old = f.Layer.GetCoreConfigController().AmpCurves;
            old[0].LinearSlope = 10;
            f.Layer.LoadParamsJson();
            Assert.That(f.Transport.Connections, Is.EqualTo(2));
            old[0].LinearSlope = 20;
            Assert.That(f.Layer.GetCoreConfigController().AmpCurves[0].LinearSlope, Is.EqualTo(1));
        }

        [Test]
        public async Task RestartFailureIsNotReadyAndCanRetryWithoutReplayingOldOutput()
        {
            using var f = new Fixture();
            await f.Start();
            await f.Expect(1, .5f, 100, 2, 10);
            f.WriteCore(f.Directory, 2);
            f.Transport.ThrowNextConnect = true;
            Assert.Throws<IOException>(() => f.Layer.LoadParamsJson());
            Assert.That(f.Layer.Ready() || f.Layer.Started(), Is.False);
            long before = f.Transport.Inner.Conformance.MessageHistory.Last().SequenceNumber;
            f.Layer.LoadConfigFile();
            await f.Start(false);
            Assert.That(f.Transport.Inner.Conformance.StimulationHistory.Where(item => item.SequenceNumber > before).All(item => item.PulseWidth == 0), Is.True);
        }

        [Test]
        public async Task ReloadStimulationAndResetAreSerialized()
        {
            using var f = new Fixture();
            await f.Start();
            f.WriteCore(f.Directory, 2);
            f.Transport.HoldDisconnect = true;
            Task first = Task.Run(() => f.Layer.LoadParamsJson());
            Assert.That(await Task.WhenAny(f.Transport.Disconnecting.Task, Task.Delay(3000)), Is.SameAs(f.Transport.Disconnecting.Task));
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task stimulation = Task.Run(() => { entered.TrySetResult(true); f.Layer.StimWithMode(2, .5f); });
            await entered.Task;
            Task second = Task.Run(() => f.Layer.LoadParamsJson());
            Task reset = Task.Run(() => f.Layer.LoadConfigFile());
            Assert.That(await Task.WhenAny(stimulation, Task.Delay(100)), Is.Not.SameAs(stimulation));
            f.Transport.ReleaseDisconnect.TrySetResult(true);
            Task all = Task.WhenAll(first, stimulation, second, reset);
            Assert.That(await Task.WhenAny(all, Task.Delay(5000)), Is.SameAs(all));
            await all;
            await f.Start(false);
            Assert.That(f.Layer.GetCoreConfigController().MaxWss, Is.EqualTo(2));
        }

        [Test, NonParallelizable]
        public async Task DefaultPathsStayAnchoredAfterWorkingDirectoryChanges()
        {
            using var f = new Fixture();
            await f.Start();
            string previous = Environment.CurrentDirectory;
            string other = Path.Combine(f.Directory, "decoy");
            System.IO.Directory.CreateDirectory(other);
            f.WriteCore(other, 3);
            try
            {
                Environment.CurrentDirectory = other;
                f.Layer.LoadParamsJson();
                Assert.That(f.Layer.GetCoreConfigController().MaxWss, Is.EqualTo(1));
                await f.Expect(1, .5f, 100, 2, 10);
            }
            finally { Environment.CurrentDirectory = previous; }
        }
        [Test]
        public async Task ReloadUsesNestedModelAndCalibrationValuesWithoutRestartWhenCoreIsUnchanged()
        {
            using var f = new Fixture();
            await f.Start();
            f.WriteParameters(f.Directory, 100, 200, 4, 13);
            File.WriteAllText(Path.Combine(f.Directory, "modelConfig.json"), "{\"calib\":{\"mode\":\"P\"},\"constants\":{\"PModeProportional\":0.4,\"PModeOffset\":0.1},\"PModeProportional\":99}");
            f.Layer.LoadParamsJson();
            Assert.That(f.Transport.Connections, Is.EqualTo(1));
            await f.Expect(1, 0.5f, 130, 4, 13);
        }

        [Test]
        public async Task ExplicitFileUsesStartupDefaultsAndParameterlessReloadReturnsToThem()
        {
            using var f = new Fixture();
            await f.Start();
            string other = Path.Combine(f.Directory, "other profile");
            System.IO.Directory.CreateDirectory(other);
            f.WriteCore(other, 2);
            f.WriteParameters(other, 80, 180, 5, 15);
            File.WriteAllText(Path.Combine(other, "modelConfig.json"), "{\"calib\":{\"mode\":\"P\"},\"constants\":{\"PModeProportional\":0}}");
            string custom = Path.Combine(other, "custom params.json");
            File.Copy(Path.Combine(other, "stimParams.json"), custom);
            f.Layer.LoadParamsJson(custom);
            Assert.That(f.Layer.GetCoreConfigController().MaxWss, Is.EqualTo(1), "Sibling Core file must not be selected.");
            await f.Expect(1, .5f, 130, 5, 15);
            f.Layer.LoadParamsJson();
            await f.Expect(1, .5f, 100, 2, 10);
            f.Layer.LoadParamsJson(other);
            Assert.That(f.Layer.GetCoreConfigController().MaxWss, Is.EqualTo(2));
            await f.Start(false);
            f.Layer.LoadParamsJson();
            await f.Start(false);
            Assert.That(f.Layer.GetCoreConfigController().MaxWss, Is.EqualTo(1));
        }

        [Test]
        public async Task InvalidFileLeavesValuesPathsConnectionsAndFilesUnchanged()
        {
            using var f = new Fixture();
            await f.Start();
            string alternate = Path.Combine(f.Directory, "invalid");
            System.IO.Directory.CreateDirectory(alternate);
            f.WriteCore(alternate, 3);
            f.WriteParameters(alternate, 80, 180, 5, 15);
            File.WriteAllText(Path.Combine(alternate, "modelConfig.json"), "invalid JSON");
            string[] files = System.IO.Directory.GetFiles(alternate);
            var before = files.Select(File.ReadAllText).ToArray();
            string active = f.Layer.GetCoreConfigController()._configPath;
            Assert.Throws<InvalidDataException>(() => f.Layer.LoadParamsJson(alternate));
            Assert.Multiple(() =>
            {
                Assert.That(f.Layer.GetCoreConfigController()._configPath, Is.EqualTo(active));
                Assert.That(f.Layer.GetCoreConfigController().MaxWss, Is.EqualTo(1));
                Assert.That(f.Transport.Connections, Is.EqualTo(1));
                Assert.That(files.Select(File.ReadAllText), Is.EqualTo(before));
            });
            await f.Expect(1, .5f, 100, 2, 10);
        }

        [Test]
        public async Task AppliesValidatedSnapshotsEvenIfFilesChangeDuringShutdown()
        {
            using var f = new Fixture();
            await f.Start();
            f.WriteCore(f.Directory, 2);
            f.WriteParameters(f.Directory, 100, 200, 4, 13);
            f.Transport.HoldDisconnect = true;
            Task reload = Task.Run(() => f.Layer.LoadParamsJson());
            Assert.That(await Task.WhenAny(f.Transport.Disconnecting.Task, Task.Delay(3000)), Is.SameAs(f.Transport.Disconnecting.Task));
            try
            {
                foreach (string file in new[] { "stimConfig.json", "stimParams.json", "modelConfig.json" })
                    File.WriteAllText(Path.Combine(f.Directory, file), "changed after validation");
            }
            finally { f.Transport.ReleaseDisconnect.TrySetResult(true); }
            await reload;
            await f.Start(false);
            Assert.That(f.Layer.GetCoreConfigController().MaxWss, Is.EqualTo(2));
            await f.Expect(2, .5f, 150, 4, 13);
        }

        [Test]
        public async Task GrowthShrinkAndEquivalentAddressesUseSemanticCoreComparison()
        {
            using var f = new Fixture();
            await f.Start();
            var core = JObject.Parse(File.ReadAllText(Path.Combine(f.Directory, "stimConfig.json")));
            core["broadcastTarget"] = "8f";
            core["wssTargets"] = new JArray("81", "82", "83");
            File.WriteAllText(Path.Combine(f.Directory, "stimConfig.json"), core.ToString());
            f.Layer.LoadParamsJson();
            Assert.That(f.Transport.Connections, Is.EqualTo(1), "Equivalent hexadecimal spellings must not restart.");
            foreach (int count in new[] { 3, 1 })
            {
                f.WriteCore(f.Directory, count);
                f.Layer.LoadParamsJson();
                await f.Start(false);
                Assert.That(f.Layer.GetStimParamsConfigController().TotalChannels, Is.EqualTo(count * 3));
                Assert.That(f.Layer.IsChannelInRange(count * 3), Is.True);
                Assert.That(f.Layer.IsChannelInRange(count * 3 + 1), Is.False);
                await f.Expect(count * 3, .5f, 100, 2, 10);
            }
            Assert.That(f.Transport.Connections, Is.EqualTo(3));
        }

        private sealed class Fixture : IDisposable
        {
            internal readonly string Directory = Path.Combine(Path.GetTempPath(), "wss-reload-" + Guid.NewGuid().ToString("N"));
            internal readonly ObservedTransport Transport = new ObservedTransport();
            internal readonly ModelParamsLayer Layer;
            internal Fixture()
            {
                System.IO.Directory.CreateDirectory(Directory);
                WriteCore(Directory, 1);
                WriteParameters(Directory, 50, 150, 2, 10);
                File.WriteAllText(Path.Combine(Directory, "modelConfig.json"), "{\"calib\":{\"mode\":\"P\"},\"constants\":{\"PModeProportional\":1,\"PModeOffset\":0}}");
                Layer = new ModelParamsLayer(new StimParamsLayer(new WssStimulationCore(Transport,
                    new WssStimulationCoreOptions { ConfigPath = Directory, MaxSetupTries = 1 }), Directory), Directory);
            }
            internal void WriteCore(string directory, int count)
            {
                File.WriteAllText(Path.Combine(directory, "stimConfig.json"), "{\"maxWSS\":" + count + ",\"firmware\":\"J03\",\"useConfigAmpCurves\":true,\"ampCurves\":[{\"LowThreshold\":0,\"LowConst\":1,\"ExpPower\":1,\"LinearOffset\":-1,\"LinearSlope\":1},{\"LowThreshold\":0,\"LowConst\":1,\"ExpPower\":1,\"LinearOffset\":-1,\"LinearSlope\":1},{\"LowThreshold\":0,\"LowConst\":1,\"ExpPower\":1,\"LinearOffset\":-1,\"LinearSlope\":1}]}");
            }
            internal void WriteParameters(string directory, int min, int max, int amp, int ipi)
            {
                var channels = new JObject();
                for (int i = 1; i <= 9; i++) channels[i.ToString()] = new JObject { ["ampMode"] = "PW", ["minPW"] = min, ["maxPW"] = max, ["defaultPA"] = amp, ["IPI"] = ipi };
                File.WriteAllText(Path.Combine(directory, "stimParams.json"), new JObject { ["stim"] = new JObject { ["ch"] = channels } }.ToString());
            }
            internal async Task Start(bool initialize = true)
            {
                long before = Transport.Inner.Conformance.MessageHistory.LastOrDefault()?.SequenceNumber ?? 0;
                if (initialize) Layer.Initialize();
                for (int i = 0; i < 3000; i++)
                {
                    Layer.Tick();
                    if (Layer.Started() && Transport.Inner.Conformance.StimulationHistory
                        .Where(item => item.SequenceNumber > before).Select(item => item.Target).Distinct().Count()
                        >= Layer.GetCoreConfigController().MaxWss) return;
                    await Task.Delay(1);
                }
                Assert.Fail("Initialization did not complete.");
            }
            internal async Task Expect(int channel, float magnitude, int pw, int amp, int ipi)
            {
                var baseline = Transport.Inner.Conformance.CaptureStimulationBaseline();
                byte target = (byte)(0x81 + (channel - 1) / 3);
                int slot = (channel - 1) % 3 + 1;
                int previousIpi = Transport.Inner.Conformance.StimulationHistory.Last(item => item.Target == target && item.Channel == slot).InterPulseInterval;
                Layer.StimWithMode(channel, magnitude);
                var expected = new WssStimulationExpectation(target, slot, amp, pw, ipi,
                    (byte)(previousIpi == ipi ? WSSMessageIDs.StreamChangeNoIPI : WSSMessageIDs.StreamChangeAll));
                for (int i = 0; i < 2000; i++)
                {
                    Layer.Tick();
                    if (Transport.Inner.Conformance.ValidateStimulation(expected, baseline).Passed) return;
                    await Task.Delay(1);
                }
                Assert.Fail(string.Join(Environment.NewLine, Transport.Inner.Conformance.ValidateStimulation(expected, baseline).Failures));
            }
            public void Dispose()
            {
                Transport.ReleaseDisconnect.TrySetResult(true);
                Layer.Dispose();
                System.IO.Directory.Delete(Directory, true);
            }
        }

        private sealed class ObservedTransport : ITransport
        {
            internal readonly EmulatedWssTransport Inner = new EmulatedWssTransport();
            internal int Connections;
            internal bool HoldDisconnect;
            internal bool ThrowNextConnect;
            internal readonly TaskCompletionSource<bool> Disconnecting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource<bool> ReleaseDisconnect = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool IsConnected => Inner.IsConnected;
            public event Action<byte[]> BytesReceived { add => Inner.BytesReceived += value; remove => Inner.BytesReceived -= value; }
            public Task ConnectAsync(CancellationToken ct = default)
            {
                Interlocked.Increment(ref Connections);
                if (ThrowNextConnect) { ThrowNextConnect = false; throw new IOException("Controlled emulator connect failure."); }
                return Inner.ConnectAsync(ct);
            }
            public async Task DisconnectAsync(CancellationToken ct = default)
            {
                if (HoldDisconnect) { Disconnecting.TrySetResult(true); await ReleaseDisconnect.Task; }
                await Inner.DisconnectAsync(ct);
            }
            public Task SendAsync(byte[] data, CancellationToken ct = default) => Inner.SendAsync(data, ct);
            public void Dispose() => Inner.Dispose();
        }
    }
}
