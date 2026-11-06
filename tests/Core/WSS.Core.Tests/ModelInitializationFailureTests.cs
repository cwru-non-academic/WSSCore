using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Wss.CalibrationModule;
using Wss.CoreModule;
using Wss.ModelModule;
using Wss.Testing;

namespace WSS.Core.Tests
{
    [TestFixture]
    public sealed class ModelInitializationFailureTests
    {
        [TestCase("initialize"), TestCase("topology"), TestCase("clock-origin"), TestCase("clock-timestamp")]
        public void FailedInitializationStopsInnerAndRetainsCompleteHistory(string failure)
        {
            using var fixture = new Fixture();
            using var control = new Fixture();
            foreach (var item in new[] { fixture, control })
            {
                item.Model.Initialize();
                item.Time = 1;
                Assert.That(item.Sample(2, 0.5f), Is.EqualTo(0.375f));
            }
            fixture.Spy.Calls.Clear();
            fixture.Time = 10;
            var original = new ApplicationException("Injected initialization failure");
            if (failure == "initialize") fixture.Spy.InitializationError = original;
            if (failure == "topology") fixture.Spy.AfterInitialize = () => fixture.Core.GetCoreConfigController().SetMaxWss(2);
            if (failure.StartsWith("clock"))
                fixture.ThrowOnClockRead = fixture.ClockReads + (failure == "clock-origin" ? 1 : 2);
            Exception error = Assert.Catch(() => fixture.Model.Initialize());
            Assert.Multiple(() =>
            {
                Assert.That(fixture.Spy.Calls, Is.EqualTo(new[] { "Initialize", "Shutdown" }));
                Assert.That(fixture.Model.Started(), Is.False);
                if (failure == "initialize") Assert.That(error, Is.SameAs(original));
                else if (failure == "topology") Assert.That(error.Message, Does.Contain("parameter controller capacity"));
                else Assert.That(error.Message, Is.EqualTo("Injected clock failure"));
            });
            fixture.Time = control.Time = 11;
            // Recording inner only: this probes retained math state without emitting stimulation or restarting a session.
            Assert.That(fixture.Sample(2, 0.75f), Is.EqualTo(control.Sample(2, 0.75f)).Within(0.000001f));
            Assert.That(fixture.Spy.LastNormalized, Is.EqualTo(0.38125f).Within(0.000001f));
            Assert.That(fixture.Model.Started(), Is.False);
        }

        [Test]
        public void CleanupFailureIsAttachedWithoutReplacingOriginalInitializationException()
        {
            using var fixture = new Fixture();
            fixture.Model.Initialize();
            fixture.Time = 1;
            fixture.Sample(2, 0.5f);
            var original = new ApplicationException("Original initialization error");
            var cleanup = new IOException("Cleanup error");
            fixture.Spy.InitializationError = original;
            fixture.Spy.ShutdownError = cleanup;
            fixture.Spy.Calls.Clear();
            var observed = Assert.Throws<ApplicationException>(() => fixture.Model.Initialize());
            Assert.Multiple(() =>
            {
                Assert.That(observed, Is.SameAs(original));
                Assert.That(observed.Data["Wss.ModelInitialization.ShutdownException"], Is.SameAs(cleanup));
                Assert.That(fixture.Spy.Calls, Is.EqualTo(new[] { "Initialize", "Shutdown" }));
            });
            fixture.Time = 2;
            Assert.That(fixture.Sample(2, 0.75f), Is.EqualTo(0.4375f));
            // Shutdown was deliberately prevented, so this test makes no assertion that the inner session stopped.
        }

        [Test]
        public async Task MalformedCoreConfigurationDuringStreamingStopsBeforeReloadFailureAndDoesNotResume()
        {
            using var fixture = new Fixture();
            fixture.Spy.CaptureOnly = false;
            fixture.Model.Initialize();
            bool streaming = false;
            for (int poll = 0; poll < 3000; poll++)
            {
                fixture.Model.Tick();
                streaming = fixture.Model.Started() && fixture.Transport.Conformance.StimulationHistory.Count > 0;
                if (streaming) break;
                await Task.Delay(1);
            }
            Assert.That(streaming, Is.True, "The failure must occur in an existing streaming session.");
            fixture.Spy.Calls.Clear();
            File.WriteAllText(fixture.CorePath, "{");
            Assert.Throws<Newtonsoft.Json.JsonSerializationException>(() => fixture.Model.Initialize());
            int completedCount = fixture.Transport.Conformance.StimulationHistory.Count;
            for (int poll = 0; poll < 20; poll++) { fixture.Model.Tick(); await Task.Delay(1); }
            Assert.Multiple(() =>
            {
                Assert.That(fixture.Model.Started(), Is.False);
                Assert.That(fixture.Transport.Conformance.StimulationHistory.Count, Is.EqualTo(completedCount));
                Assert.That(fixture.Spy.Calls, Is.EqualTo(new[] { "Initialize", "Shutdown" }),
                    "The model must delegate initialization once and must not preflight LoadConfigFile.");
            });
        }

        public class RecordingParameters : DispatchProxy
        {
            public IStimParamsCore Target;
            public readonly List<string> Calls = new List<string>();
            public Exception InitializationError;
            public Exception ShutdownError;
            public Action AfterInitialize;
            public bool CaptureOnly = true;
            public float LastNormalized;

            protected override object Invoke(MethodInfo method, object[] arguments)
            {
                if (method.Name == "Initialize" || method.Name == "Shutdown" || method.Name == "LoadConfigFile")
                    Calls.Add(method.Name);
                if (method.Name == "Initialize" && InitializationError != null) throw InitializationError;
                if (method.Name == "Shutdown" && ShutdownError != null) throw ShutdownError;
                if (method.Name == "StimulateNormalized" && CaptureOnly)
                {
                    LastNormalized = (float)arguments[1];
                    return null;
                }
                object result;
                try { result = method.Invoke(Target, arguments); }
                catch (TargetInvocationException error) when (error.InnerException != null)
                {
                    ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                    throw;
                }
                if (method.Name == "Initialize") AfterInitialize?.Invoke();
                return result;
            }
        }

        private sealed class Fixture : IDisposable
        {
            private readonly string _directory;
            public readonly EmulatedWssTransport Transport;
            public readonly WssStimulationCore Core;
            public readonly RecordingParameters Spy;
            public readonly ModelParamsLayer Model;
            public float Time;
            public int ClockReads;
            public int ThrowOnClockRead = -1;
            public string CorePath => Path.Combine(_directory, "stimConfig.json");

            public Fixture()
            {
                _directory = Path.Combine(Path.GetTempPath(), "wss-init-failure-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_directory);
                WssStimulationSequences.SingleDevice[0].WriteFixture(_directory);
                File.WriteAllText(Path.Combine(_directory, "modelConfig.json"), new JObject
                {
                    ["calib"] = new JObject { ["mode"] = "PD" },
                    ["constants"] = new JObject { ["PDModeProportional"] = 0.5f,
                        ["PDModeDerivative"] = 0.25f, ["PDModeOffset"] = 0f }
                }.ToString());
                Transport = new EmulatedWssTransport();
                Core = new WssStimulationCore(Transport, new WssStimulationCoreOptions { ConfigPath = CorePath });
                var real = new StimParamsLayer(Core, _directory);
                var proxy = DispatchProxy.Create<IStimParamsCore, RecordingParameters>();
                Spy = (RecordingParameters)(object)proxy;
                Spy.Target = real;
                Model = new ModelParamsLayer(proxy, _directory, () =>
                {
                    if (++ClockReads == ThrowOnClockRead) throw new ApplicationException("Injected clock failure");
                    return Time;
                });
            }

            public float Sample(int channel, float input)
            {
                Model.StimWithMode(channel, input);
                return Spy.LastNormalized;
            }

            public void Dispose()
            {
                Core.Dispose();
                Directory.Delete(_directory, true);
            }
        }
    }
}
