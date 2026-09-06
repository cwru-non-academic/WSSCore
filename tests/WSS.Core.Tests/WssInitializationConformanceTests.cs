using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Wss.CoreModule;
using Wss.Testing;

namespace WSS.Core.Tests
{
    [TestFixture]
    public sealed class WssInitializationConformanceTests
    {
        [Test]
        public async Task RealCoreInitializationReachesReadyAndPassesConformance()
        {
            string configPath = Path.Combine(Path.GetTempPath(), $"wss-core-{Guid.NewGuid():N}.json");
            File.WriteAllText(configPath,
                "{\"maxWSS\":1,\"firmware\":\"J03\",\"broadcastTarget\":\"0x8F\",\"wssTargets\":[\"0x81\",\"0x82\",\"0x83\"]}");

            var transport = new EmulatedWssTransport();
            WssStimulationCore core = null;
            try
            {
                core = new WssStimulationCore(
                    transport,
                    new WssStimulationCoreOptions
                    {
                        ConfigPath = configPath,
                        MaxSetupTries = 1
                    });

                core.Initialize();
                bool reachedReady = false;
                for (int i = 0; i < 2000; i++)
                {
                    core.Tick();
                    if (core.Ready())
                    {
                        reachedReady = true;
                        break;
                    }

                    await Task.Delay(1);
                }

                for (int i = 0; i < 1000 && !HasStreamingObservation(transport); i++)
                    await Task.Delay(1);

                var result = transport.Conformance.ValidateInitialization();
                Assert.Multiple(() =>
                {
                    Assert.That(reachedReady, Is.True, "Core did not reach Ready within the finite tick limit.");
                    Assert.That(core.Ready(), Is.True, "Core left Ready before conformance validation.");
                    Assert.That(result.Passed, Is.True, string.Join(Environment.NewLine, result.Failures));
                });
            }
            finally
            {
                core?.Dispose();
                if (core == null)
                    transport.Dispose();
                if (File.Exists(configPath))
                    File.Delete(configPath);
            }
        }

        [Test]
        public async Task MissingRequiredMessageReportsTargetAndOperation()
        {
            using var transport = new EmulatedWssTransport();
            await transport.ConnectAsync();
            var codec = new WssFrameCodec();
            await transport.SendAsync(codec.Frame(
                0x00,
                0x81,
                new byte[] { (byte)WSSMessageIDs.Clear, 0x01, 0x00 }));

            var result = transport.Conformance.ValidateInitialization();
            var check = result.RequiredMessageChecks.Single(item => item.Name == "ModuleQuerySettings");

            Assert.Multiple(() =>
            {
                Assert.That(result.Passed, Is.False);
                Assert.That(check.Passed, Is.False);
                Assert.That(check.Details, Is.EqualTo("Target 0x81 did not receive ModuleQuery(settings)."));
            });
        }

        [Test]
        public async Task InvalidOrderReportsBothObservedSequenceNumbers()
        {
            using var transport = new EmulatedWssTransport();
            await transport.ConnectAsync();
            var codec = new WssFrameCodec();
            await transport.SendAsync(codec.Frame(
                0x00,
                0x81,
                new byte[] { (byte)WSSMessageIDs.Clear, 0x01, 0x00 }));

            var eventPayload = new byte[18];
            eventPayload[0] = (byte)WSSMessageIDs.CreateEvent;
            eventPayload[1] = 16;
            eventPayload[2] = 1;
            eventPayload[4] = 1;
            await transport.SendAsync(codec.Frame(0x00, 0x81, eventPayload));
            await transport.SendAsync(codec.Frame(
                0x00,
                0x81,
                new byte[] { (byte)WSSMessageIDs.ModuleQuery, 0x01, 0x01 }));

            var result = transport.Conformance.ValidateInitialization();
            var check = result.OrderingChecks.Single(item => item.Name == "ModuleQueryBeforeEventCreation");

            Assert.Multiple(() =>
            {
                Assert.That(result.Passed, Is.False);
                Assert.That(check.Passed, Is.False);
                Assert.That(check.Details, Does.Contain("ModuleQuery target 0x81 observed at #3"));
                Assert.That(check.Details, Does.Contain("CreateEvent observed at #2"));
                Assert.That(check.Details, Does.Contain("Expected ModuleQuery before CreateEvent"));
            });
        }

        private static bool HasStreamingObservation(EmulatedWssTransport transport)
        {
            return transport.Conformance.MessageHistory.Any(item =>
                item.Target == 0x81 && item.MessageId == (byte)WSSMessageIDs.StreamChangeNoIPI);
        }
    }
}
