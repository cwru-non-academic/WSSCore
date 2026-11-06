using System;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Wss.CalibrationModule;
using Wss.CoreModule;
using Wss.ModelModule;
using Wss.Testing;

namespace WSS.Core.Tests
{
    [TestFixture]
    public sealed class ModelHistoryTests
    {
        [TestCase("P", "PModeProportional", 0.25f, 16)]
        [TestCase("P", "PModeOffset", 0.25f, 32)]
        [TestCase("PD", "PDModeProportional", 0.5f, 32)]
        [TestCase("PD", "PDModeDerivative", 0.5f, 32)]
        [TestCase("PD", "PDModeOffset", 0.25f, 32)]
        public void ChangingEveryNestedConstantChangesOutputThroughReloadAndPublicSetter(
            string mode, string key, float value, int expected)
        {
            using var fixture = new Fixture(1);
            var json = ConstantFixture(mode);
            File.WriteAllText(fixture.ModelPath, json.ToString());
            fixture.Model.GetModelConfigController().LoadModelJson();
            fixture.Model.Initialize();
            fixture.Time = 1;
            Assert.That(fixture.Sample(2, 0.5f), Is.Zero);
            json["constants"][key] = value;
            json[key] = 99f; // Conflicting top-level values must never override nested constants.
            File.WriteAllText(fixture.ModelPath, json.ToString());
            fixture.Model.GetModelConfigController().LoadModelJson();
            Assert.That(fixture.Sample(3, 0.5f), Is.EqualTo(expected));
            fixture.Model.GetModelConfigController().SetModelParam("constants." + key, value * 2);
            Assert.That(fixture.Sample(1, 0.5f), Is.EqualTo(expected * 2));
        }

        [TestCase("P", "PModeProportional", 64)]
        [TestCase("P", "PModeOffset", 0)]
        [TestCase("PD", "PDModeProportional", 32)]
        [TestCase("PD", "PDModeDerivative", 13)]
        [TestCase("PD", "PDModeOffset", 0)]
        public void FailedNestedLookupUsesBuiltInDefaultAndIgnoresRootOverrides(string mode, string key, int expected)
        {
            foreach (string kind in new[] { "missing", "unconvertible", "zero" })
            {
                using var fixture = new Fixture(1);
                var json = ConstantFixture(mode);
                ((JObject)json["constants"]).Remove(key);
                json[key] = 99f;
                if (kind == "unconvertible") json["constants"][key] = new JObject { ["invalid"] = 1 };
                if (kind == "zero") json["constants"][key] = 0f;
                File.WriteAllText(fixture.ModelPath, json.ToString());
                fixture.Model.GetModelConfigController().LoadModelJson();
                fixture.Model.Initialize();
                fixture.Time = 1;
                Assert.That(fixture.Sample(2, 0.5f), Is.EqualTo(kind == "zero" ? 0 : expected), key + "/" + kind);
            }
        }

        private static JObject ConstantFixture(string mode) => new JObject
        {
            ["calib"] = new JObject { ["mode"] = mode },
            ["constants"] = new JObject { ["PModeProportional"] = 0f, ["PModeOffset"] = 0f,
                ["PDModeProportional"] = 0f, ["PDModeDerivative"] = 0f, ["PDModeOffset"] = 0f }
        };

        [TestCase(1, 3), TestCase(2, 6), TestCase(3, 9)]
        public void LastSupportedChannelHasModelHistory(int devices, int channel)
        {
            using var fixture = new Fixture(devices);
            fixture.Model.Initialize();
            fixture.Time = 1;
            Assert.That(fixture.Sample(channel, 0.5f), Is.EqualTo(48));
        }

        [Test]
        public void InterleavedDerivativeHistoryIsIndependentAcrossDeviceBoundary()
        {
            using var fixture = new Fixture(2);
            fixture.Model.Initialize();
            fixture.Time = 1;
            Assert.Multiple(() =>
            {
                Assert.That(fixture.Sample(3, 0.5f), Is.EqualTo(48));
                Assert.That(fixture.Sample(4, 0.5f), Is.EqualTo(48));
                Assert.That(fixture.Sample(6, 0.5f), Is.EqualTo(48));
            });
            fixture.Time = 2;
            Assert.Multiple(() =>
            {
                Assert.That(fixture.Sample(3, 0.75f), Is.EqualTo(56));
                Assert.That(fixture.Sample(4, 0.5f), Is.EqualTo(32));
                Assert.That(fixture.Sample(6, 0.25f), Is.EqualTo(8));
            });
        }

        [Test]
        public void SameTimeAndFirstSamplesKeepProductionMinimumDtAndClamping()
        {
            using var fixture = new Fixture(1);
            fixture.Model.Initialize();
            Assert.That(fixture.Sample(2, 0.5f), Is.EqualTo(128), "First-sample derivative must not be suppressed.");
            Assert.That(fixture.Sample(2, 0.75f), Is.EqualTo(128));
            Assert.That(fixture.Sample(2, 0.25f), Is.Zero);
            fixture.Time = 1;
            Assert.That(fixture.Sample(2, 0.25f), Is.EqualTo(16), "An ordinary call must reuse the previous magnitude.");
        }

        [Test]
        public void SuccessfulReinitializationMatchesFreshStackOnTheSameRelativeTimeline()
        {
            using var reused = new Fixture(2);
            reused.Time = 10;
            reused.Model.Initialize();
            reused.Time = 11;
            reused.Sample(2, 1);
            reused.Sample(6, 0.75f);
            reused.Time = 20;
            reused.Model.Initialize();
            var actual = Trace(reused, 20);
            using var fresh = new Fixture(2);
            fresh.Time = 100;
            fresh.Model.Initialize();
            Assert.That(actual, Is.EqualTo(Trace(fresh, 100)));
            Assert.That(actual, Is.EqualTo(new[] { 128f, 32f, 48f, 128f, 32f }));
        }

        [TestCase(false), TestCase(true)]
        public void ReloadPreservesHistoryAndUsesNewConfiguration(bool modelReload)
        {
            using var fixture = new Fixture(1);
            fixture.Model.Initialize();
            fixture.Time = 1;
            Assert.That(fixture.Sample(2, 0.5f), Is.EqualTo(48));
            if (modelReload)
            {
                fixture.WriteModel("PD", 0.5f, 0.5f, 0);
                fixture.Model.GetModelConfigController().LoadModelJson();
            }
            else
            {
                var json = JObject.Parse(File.ReadAllText(fixture.ParamsPath));
                json["stim"]["ch"]["2"]["maxPW"] = 64;
                File.WriteAllText(fixture.ParamsPath, json.ToString());
                fixture.Model.LoadParamsJson();
            }
            fixture.Time = 2;
            Assert.That(fixture.Sample(2, 0.5f), Is.EqualTo(modelReload ? 32 : 16));
        }

        [TestCase(1, 2, false), TestCase(2, 1, false)]
        [TestCase(1, 2, true), TestCase(2, 1, true)]
        public void ChangedTopologyFailsClearlyBeforeInitializationOrReinitialization(int before, int after, bool reinitialize)
        {
            using var fixture = new Fixture(before);
            if (reinitialize) fixture.Model.Initialize();
            var json = JObject.Parse(File.ReadAllText(fixture.CorePath));
            json["maxWSS"] = after;
            File.WriteAllText(fixture.CorePath, json.ToString());
            var error = Assert.Throws<InvalidOperationException>(() => fixture.Model.Initialize());
            Assert.Multiple(() =>
            {
                Assert.That(error.Message, Does.Contain("parameter controller capacity"));
                Assert.That(error.Message, Does.Contain("new complete stimulation stack"));
                Assert.That(fixture.Model.Started(), Is.False);
            });
        }

        [Test]
        public void ZeroRawInputCanRemainNonzeroButExplicitZeroCalculatedOutputStaysZero()
        {
            using var fixture = new Fixture(1);
            fixture.WriteModel("P", 0.5f, 0, 0.25f);
            fixture.Model.GetModelConfigController().LoadModelJson();
            fixture.Model.Initialize();
            Assert.That(fixture.Sample(2, 0), Is.EqualTo(32));
            fixture.WriteModel("PD", 0.5f, 0.25f, 0.5f);
            fixture.Model.GetModelConfigController().LoadModelJson();
            fixture.Time = 1;
            fixture.Sample(2, 1);
            fixture.Time = 2;
            Assert.That(fixture.Sample(2, 0), Is.EqualTo(32), "PD history and offset must retain their equation.");
            fixture.WriteModel("P", 1, 0, 0);
            fixture.Model.GetModelConfigController().LoadModelJson();
            Assert.That(fixture.Sample(2, 0), Is.Zero);
        }

        private static float[] Trace(Fixture fixture, float start)
        {
            float first = fixture.Sample(2, 0.5f);
            fixture.Time = start + 1;
            float second = fixture.Sample(2, 0.5f);
            float third = fixture.Sample(6, 0.5f);
            float fourth = fixture.Sample(2, 0.75f);
            fixture.Time = start + 2;
            return new[] { first, second, third, fourth, fixture.Sample(6, 0.5f) };
        }

        private sealed class Fixture : IDisposable
        {
            private readonly string _directory;
            private readonly WssStimulationCore _core;
            internal float Time;
            internal string CorePath => Path.Combine(_directory, "stimConfig.json");
            internal string ParamsPath => Path.Combine(_directory, "stimParams.json");
            internal string ModelPath => Path.Combine(_directory, "modelConfig.json");
            internal ModelParamsLayer Model { get; }

            internal Fixture(int devices)
            {
                _directory = Path.Combine(Path.GetTempPath(), "wss-model-history-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_directory);
                (devices == 1 ? WssStimulationSequences.SingleDevice[0] : WssStimulationSequences.MultipleDevices[0]).WriteFixture(_directory);
                var config = JObject.Parse(File.ReadAllText(CorePath));
                config["maxWSS"] = devices;
                if (devices == 3) ((JArray)config["ampCurves"]).Add(config["ampCurves"][0].DeepClone());
                File.WriteAllText(CorePath, config.ToString());
                var channels = new JObject();
                for (int ch = 1; ch <= devices * 3; ch++)
                    channels[ch.ToString()] = new JObject { ["ampMode"] = "PW", ["minPW"] = 0,
                        ["maxPW"] = 128, ["defaultPA"] = 1, ["IPI"] = 10 };
                File.WriteAllText(ParamsPath, new JObject { ["stim"] = new JObject { ["ch"] = channels } }.ToString());
                WriteModel("PD", 0.5f, 0.25f, 0);
                _core = new WssStimulationCore(new EmulatedWssTransport(), new WssStimulationCoreOptions { ConfigPath = CorePath });
                var parameters = new StimParamsLayer(_core, ParamsPath);
                Model = new ModelParamsLayer(parameters, _directory, () => Time);
            }

            internal void WriteModel(string mode, float proportional, float derivative, float offset)
                => File.WriteAllText(Path.Combine(_directory, "modelConfig.json"), new JObject
                {
                    ["calib"] = new JObject { ["mode"] = mode },
                    ["constants"] = new JObject { ["PModeProportional"] = proportional, ["PModeOffset"] = offset,
                        ["PDModeProportional"] = proportional, ["PDModeDerivative"] = derivative, ["PDModeOffset"] = offset }
                }.ToString());

            internal float Sample(int channel, float input)
            {
                Model.StimWithMode(channel, input);
                return Model.GetStimIntensity(channel);
            }

            public void Dispose()
            {
                _core.Dispose();
                Directory.Delete(_directory, true);
            }
        }
    }
}
