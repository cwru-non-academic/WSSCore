using System;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Wss.CoreModule;
using Wss.Testing;

namespace WSS.Core.Tests
{
    [TestFixture]
    public sealed class WssStimulationWindowTests
    {
        [Test]
        public async Task CompleteDelayedOldFramesBeforeConvergenceAreAllowed()
        {
            using var transport = new EmulatedWssTransport();
            using var client = Client(transport);
            await transport.ConnectAsync();
            await Send(client, 0x81, 2, 41, 13);
            var baseline = transport.Conformance.CaptureStimulationBaseline();
            await Send(client, 0x81, 2, 41, 13);
            await Send(client, 0x81, 2, 41, 13);
            await Converge(client);
            var result = WssStimulationWindow.Evaluate(transport.Conformance, baseline, Expected(), 1);
            Assert.That(result.Passed, Is.True, result.Diagnostics);
        }

        [TestCase("mixed"), TestCase("neighbor-amplitude"), TestCase("neighbor-width"), TestCase("neighbor-ipi")]
        [TestCase("wrong-slot"), TestCase("wrong-target"), TestCase("regression")]
        public async Task WrongFrameThenCorrectFramesCannotHideViolation(string fault)
        {
            using var transport = new EmulatedWssTransport();
            using var client = Client(transport);
            await transport.ConnectAsync();
            await Send(client, 0x81, 2, 41, 13);
            await Send(client, 0x82, 2, 41, 13);
            var baseline = transport.Conformance.CaptureStimulationBaseline();
            if (fault == "regression") await Converge(client);
            if (fault == "mixed") await Send(client, 0x81, 4, 41, 13);
            else if (fault.StartsWith("neighbor-")) await Send(client, 0x81, 2, 41, 13,
                fault == "neighbor-amplitude" ? 9 : 3, fault == "neighbor-width" ? 99 : 51,
                fault == "neighbor-ipi" ? 99 : 15);
            else if (fault == "wrong-slot") await Send(client, 0x81, 2, 41, 13, 4, 71, 17);
            else if (fault == "wrong-target") await Send(client, 0x82, 4, 71, 17);
            else await Send(client, 0x81, 2, 41, 13);
            await Converge(client);
            await Send(client, 0x82, 2, 41, 13);
            var result = WssStimulationWindow.Evaluate(transport.Conformance, baseline, Expected(), 2);
            Assert.Multiple(() =>
            {
                Assert.That(result.Passed, Is.False);
                Assert.That(result.Failures, Is.Not.Empty, result.Diagnostics);
                Assert.That(result.Diagnostics, Does.Contain("sequence=#"));
            });
        }

        [Test]
        public async Task WidthAndAmplitudeWithoutIpiDeliveryCannotSettle()
        {
            using var transport = new EmulatedWssTransport();
            using var client = Client(transport);
            await transport.ConnectAsync();
            await Send(client, 0x81, 2, 41, 13);
            var baseline = transport.Conformance.CaptureStimulationBaseline();
            for (int i = 0; i < 3; i++) await Send(client, 0x81, 4, 71, null);
            var result = WssStimulationWindow.Evaluate(transport.Conformance, baseline, Expected(), 1);
            Assert.That(result.Passed, Is.False, result.Diagnostics);
        }

        private static WssClient Client(EmulatedWssTransport transport) => new WssClient(transport,
            new WssFrameCodec(), new WSSVersionHandler("J03"), new WssClientOptions { OwnsTransport = false });
        private static WssStimulationExpectation Expected() => new WssStimulationExpectation(0x81, 1, 4, 71, 17, 0x30);
        private static Task Send(WssClient client, byte target, int amplitude, int width, int? ipi,
            int neighborAmplitude = 3, int neighborWidth = 51, int neighborIpi = 15) => client.StreamChange(new StreamChangeRequest
            {
                PulseAmplitudes = new[] { amplitude, neighborAmplitude, 5 },
                PulseWidths = new[] { width, neighborWidth, 61 },
                InterPulseIntervals = ipi.HasValue ? new[] { ipi.Value, neighborIpi, 19 } : null
            }, (WssTarget)target);
        private static async Task Converge(WssClient client)
        {
            await Send(client, 0x81, 4, 71, 17);
            await Send(client, 0x81, 4, 71, null);
            await Send(client, 0x81, 4, 71, null);
        }
    }
}
