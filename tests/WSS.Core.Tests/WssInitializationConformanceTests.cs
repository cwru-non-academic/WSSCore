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
        public async Task CreateEventBeforeModuleQueryFailsForKnownFreshCapableDevice()
        {
            using var transport = new EmulatedWssTransport();
            await transport.ConnectAsync();
            var codec = new WssFrameCodec();
            await SendAsync(transport, codec, WSSMessageIDs.Clear, 0x00);
            await SendAsync(transport, codec, WSSMessageIDs.CreateContactConfig, 0x01, 0x00, 0x00);
            await SendAsync(transport, codec, WSSMessageIDs.CreateEvent, 0x01, 0x00, 0x01);

            var result = transport.Conformance.ValidateInitialization();
            var check = result.Errors.Single(item => item.Name == "ModuleQueryBeforeEventCreation");

            Assert.Multiple(() =>
            {
                Assert.That(result.Passed, Is.False);
                Assert.That(check.Severity, Is.EqualTo(WssConformanceSeverity.Error));
                Assert.That(check.Details, Does.Contain("CreateEvent 1 requires ModuleQuery(settings) first"));
                Assert.That(check.Target, Is.EqualTo(0x81));
            });
        }

        [Test]
        public async Task ModuleQueryAfterEventDoesNotRepairInvalidOrdering()
        {
            using var transport = new EmulatedWssTransport();
            await transport.ConnectAsync();
            var codec = new WssFrameCodec();
            await SendAsync(transport, codec, WSSMessageIDs.Clear, 0x00);
            await SendAsync(transport, codec, WSSMessageIDs.CreateContactConfig, 0x01, 0x00, 0x00);
            await SendAsync(transport, codec, WSSMessageIDs.CreateEvent, 0x01, 0x00, 0x01);
            await SendAsync(transport, codec, WSSMessageIDs.ModuleQuery, 0x01);

            var result = transport.Conformance.ValidateInitialization();
            var check = result.Errors.Single(item => item.Name == "ModuleQueryBeforeEventCreation");

            Assert.Multiple(() =>
            {
                Assert.That(result.Passed, Is.False);
                Assert.That(check.Passed, Is.False);
                Assert.That(check.SequenceNumber, Is.EqualTo(3));
                Assert.That(check.Details, Does.Contain("Observed sequence: #3"));
            });
        }

        private static Task SendAsync(
            EmulatedWssTransport transport,
            WssFrameCodec codec,
            WSSMessageIDs messageId,
            params byte[] data)
        {
            var payload = new byte[data.Length + 2];
            payload[0] = (byte)messageId;
            payload[1] = (byte)data.Length;
            Buffer.BlockCopy(data, 0, payload, 2, data.Length);
            return transport.SendAsync(codec.Frame(0x00, 0x81, payload));
        }

        private static bool HasStreamingObservation(EmulatedWssTransport transport)
        {
            return transport.Conformance.MessageHistory.Any(item =>
                item.Target == 0x81 && item.MessageId == (byte)WSSMessageIDs.StreamChangeNoIPI);
        }
    }
}
