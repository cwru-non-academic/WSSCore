using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using Wss.CoreModule;
using Wss.Testing;
using Wss.Transports;

namespace WSS.Core.Tests
{
    [TestFixture]
    public sealed class EmulatedWssTransportTests
    {
        [Test]
        public void ImplementsITransport()
        {
            using var transport = new EmulatedWssTransport();

            Assert.That(transport, Is.InstanceOf<ITransport>());
        }

        [Test]
        public void ImplementsIConformanceProvider()
        {
            using var transport = new EmulatedWssTransport();

            Assert.That(transport, Is.InstanceOf<IConformanceProvider>());
        }

        [Test]
        public void ConstructorCreatesOneStableConformanceObject()
        {
            using var transport = new EmulatedWssTransport();
            var provider = (IConformanceProvider)transport;

            Assert.That(provider.Conformance, Is.Not.Null);
            Assert.That(provider.Conformance, Is.SameAs(transport.Conformance));
        }

        [Test]
        public async Task WssClientRequestIsDecodedAndReplyUsesNormalReceivePath()
        {
            using var transport = new EmulatedWssTransport();
            using var client = new WssClient(
                transport,
                new WssFrameCodec(),
                new WSSVersionHandler("J03"),
                new WssClientOptions { OwnsTransport = false });
            await transport.ConnectAsync();

            string result = await client.StartStim(WssTarget.Wss1);

            Assert.That(result, Is.EqualTo("Start Acknowledged"));
            Assert.That(transport.Conformance.MessageHistory, Has.Count.EqualTo(1));
            var observation = transport.Conformance.MessageHistory[0];
            Assert.Multiple(() =>
            {
                Assert.That(observation.SequenceNumber, Is.EqualTo(1));
                Assert.That(observation.Sender, Is.EqualTo(0x00));
                Assert.That(observation.Target, Is.EqualTo((byte)WssTarget.Wss1));
                Assert.That(observation.MessageId, Is.EqualTo((byte)WSSMessageIDs.StimulationSwitch));
                Assert.That(observation.Payload, Is.EqualTo(new byte[] { 0x0B, 0x01, 0x03 }));
                Assert.That(observation.RawFrame[^1], Is.EqualTo(0xC0));
            });
        }

        [Test]
        public async Task ValidWireFrameRaisesFramedReply()
        {
            using var transport = new EmulatedWssTransport();
            await transport.ConnectAsync();
            byte[] reply = null;
            transport.BytesReceived += bytes => reply = bytes;
            var codec = new WssFrameCodec();
            var request = codec.Frame(0x00, 0x81, new byte[] { 0x07, 0x00 });

            await transport.SendAsync(request);

            Assert.That(reply, Is.Not.Null);
            var decoded = new WssFrameCodec().Deframe(reply).Single();
            Assert.That(decoded.AsSpan(0, decoded.Length - 1).ToArray(),
                Is.EqualTo(new byte[] { 0x81, 0x00, 0x07, 0x00 }));
        }

        [Test]
        public async Task EscapedPayloadBytesRoundTripThroughDevice()
        {
            using var transport = new EmulatedWssTransport();
            await transport.ConnectAsync();
            byte[] reply = null;
            transport.BytesReceived += bytes => reply = bytes;
            var requestPayload = new byte[] { 0x07, 0x02, 0xC0, 0xDB };
            var request = new WssFrameCodec().Frame(0x00, 0x81, requestPayload);

            await transport.SendAsync(request);

            Assert.Multiple(() =>
            {
                Assert.That(ContainsSequence(request, 0xDB, 0xDC), Is.True);
                Assert.That(ContainsSequence(request, 0xDB, 0xDD), Is.True);
                Assert.That(transport.Conformance.MessageHistory[0].Payload, Is.EqualTo(requestPayload));
            });
            var decodedReply = new WssFrameCodec().Deframe(reply).Single();
            Assert.That(decodedReply.AsSpan(2, decodedReply.Length - 3).ToArray(), Is.EqualTo(requestPayload));
        }

        [Test]
        public async Task InvalidChecksumIsRecordedAndRejected()
        {
            using var transport = new EmulatedWssTransport();
            await transport.ConnectAsync();
            int replies = 0;
            transport.BytesReceived += _ => replies++;
            var request = new WssFrameCodec().Frame(0x00, 0x81, new byte[] { 0x07, 0x00 });
            request[request.Length - 2] ^= 0x01;

            await transport.SendAsync(request);

            Assert.Multiple(() =>
            {
                Assert.That(replies, Is.Zero);
                Assert.That(transport.Conformance.MessageHistory, Is.Empty);
                Assert.That(transport.Conformance.ProtocolErrors, Has.Count.EqualTo(1));
                Assert.That(transport.Conformance.ProtocolErrors[0].Kind,
                    Is.EqualTo(WssProtocolErrorKind.InvalidChecksum));
                Assert.That(transport.Conformance.ProtocolErrors[0].RawFrame, Is.EqualTo(request));
            });
        }

        [Test]
        public async Task MalformedLengthIsRecordedAndRejected()
        {
            using var transport = new EmulatedWssTransport();
            await transport.ConnectAsync();
            int replies = 0;
            transport.BytesReceived += _ => replies++;
            var request = new WssFrameCodec().Frame(0x00, 0x81, new byte[] { 0x07, 0x02, 0x01 });

            await transport.SendAsync(request);

            Assert.Multiple(() =>
            {
                Assert.That(replies, Is.Zero);
                Assert.That(transport.Conformance.MessageHistory, Is.Empty);
                Assert.That(transport.Conformance.ProtocolErrors, Has.Count.EqualTo(1));
                Assert.That(transport.Conformance.ProtocolErrors[0].Kind,
                    Is.EqualTo(WssProtocolErrorKind.MalformedFrame));
            });
        }

        [Test]
        public async Task ModuleQueryProfileIsDeterministicAcrossInstances()
        {
            var first = await QueryModuleAsync();
            var second = await QueryModuleAsync();

            Assert.That(second, Is.EqualTo(first));
            var decoded = new WssFrameCodec().Deframe(first).Single();
            Assert.Multiple(() =>
            {
                Assert.That(decoded[2], Is.EqualTo((byte)WSSMessageIDs.RequestAnalog));
                Assert.That(decoded[3], Is.EqualTo(16));
                Assert.That(decoded[4], Is.EqualTo(0x01));
                Assert.That(decoded[14], Is.EqualTo(0x06));
                Assert.That(decoded[16], Is.EqualTo(50));
                Assert.That(decoded[17], Is.EqualTo(1));
                Assert.That(decoded[18], Is.EqualTo(10));
                Assert.That(decoded[19], Is.EqualTo(255));
            });
        }

        [Test]
        public async Task AccumulatesPartialFramesAndProcessesMultipleFramesPerChunk()
        {
            using var transport = new EmulatedWssTransport();
            await transport.ConnectAsync();
            var replies = new List<byte[]>();
            transport.BytesReceived += replies.Add;
            var codec = new WssFrameCodec();
            var first = codec.Frame(0x00, 0x81, new byte[] { 0x07, 0x00 });
            int split = first.Length / 2;

            await transport.SendAsync(first.AsSpan(0, split).ToArray());
            Assert.That(replies, Is.Empty);
            Assert.That(transport.Conformance.MessageHistory, Is.Empty);

            await transport.SendAsync(first.AsSpan(split).ToArray());
            var second = codec.Frame(0x00, 0x81, new byte[] { 0x04, 0x00 });
            var third = codec.Frame(0x00, 0x81, new byte[] { 0x09, 0x00 });
            await transport.SendAsync(second.Concat(third).ToArray());

            Assert.Multiple(() =>
            {
                Assert.That(replies, Has.Count.EqualTo(3));
                Assert.That(transport.Conformance.MessageHistory, Has.Count.EqualTo(3));
                Assert.That(transport.Conformance.MessageHistory.Select(item => item.SequenceNumber),
                    Is.EqualTo(new long[] { 1, 2, 3 }));
            });
        }

        [Test]
        public async Task StreamVariantsDecodeEffectiveStateAndRetainOmittedFields()
        {
            using var transport = new EmulatedWssTransport();
            using var client = new WssClient(
                transport,
                new WssFrameCodec(),
                new WSSVersionHandler("J03"),
                new WssClientOptions { OwnsTransport = false });
            await transport.ConnectAsync();

            var initialBaseline = transport.Conformance.CaptureStimulationBaseline();
            await client.StreamChange(new StreamChangeRequest
            {
                PulseAmplitudes = new[] { 11, 12, 13 },
                PulseWidths = new[] { 21, 22, 23 },
                InterPulseIntervals = new[] { 31, 32, 33 }
            }, WssTarget.Wss1);
            AssertConforms(
                transport,
                initialBaseline,
                WSSMessageIDs.StreamChangeAll,
                new[] { 11, 12, 13 },
                new[] { 21, 22, 23 },
                new[] { 31, 32, 33 });

            var noIpiBaseline = transport.Conformance.CaptureStimulationBaseline();
            await client.StreamChange(new StreamChangeRequest
            {
                PulseAmplitudes = new[] { 14, 15, 16 },
                PulseWidths = new[] { 24, 25, 26 }
            }, WssTarget.Wss1);
            AssertConforms(
                transport,
                noIpiBaseline,
                WSSMessageIDs.StreamChangeNoIPI,
                new[] { 14, 15, 16 },
                new[] { 24, 25, 26 },
                new[] { 31, 32, 33 });

            var noPwBaseline = transport.Conformance.CaptureStimulationBaseline();
            await client.StreamChange(new StreamChangeRequest
            {
                PulseAmplitudes = new[] { 17, 18, 19 },
                InterPulseIntervals = new[] { 34, 35, 36 }
            }, WssTarget.Wss1);
            AssertConforms(
                transport,
                noPwBaseline,
                WSSMessageIDs.StreamChangeNoPW,
                new[] { 17, 18, 19 },
                new[] { 24, 25, 26 },
                new[] { 34, 35, 36 });

            var noPaBaseline = transport.Conformance.CaptureStimulationBaseline();
            await client.StreamChange(new StreamChangeRequest
            {
                PulseWidths = new[] { 27, 28, 29 },
                InterPulseIntervals = new[] { 37, 38, 39 }
            }, WssTarget.Wss1);
            AssertConforms(
                transport,
                noPaBaseline,
                WSSMessageIDs.StreamChangeNoPA,
                new[] { 17, 18, 19 },
                new[] { 27, 28, 29 },
                new[] { 37, 38, 39 });
        }

        [Test]
        public async Task StimulationMismatchReportsFieldTargetValuesAndSequence()
        {
            using var transport = new EmulatedWssTransport();
            using var client = new WssClient(
                transport,
                new WssFrameCodec(),
                new WSSVersionHandler("J03"),
                new WssClientOptions { OwnsTransport = false });
            await transport.ConnectAsync();
            var baseline = transport.Conformance.CaptureStimulationBaseline();
            await client.StreamChange(new StreamChangeRequest
            {
                PulseAmplitudes = new[] { 11, 12, 13 },
                PulseWidths = new[] { 21, 22, 23 },
                InterPulseIntervals = new[] { 31, 32, 33 }
            }, WssTarget.Wss1);

            var result = transport.Conformance.ValidateStimulation(
                new WssStimulationExpectation(
                    0x81,
                    1,
                    11,
                    99,
                    31,
                    (byte)WSSMessageIDs.StreamChangeAll,
                    false),
                baseline);
            var failure = result.FieldChecks.Single(item => item.Name == "PulseWidth");

            Assert.Multiple(() =>
            {
                Assert.That(result.Passed, Is.False);
                Assert.That(failure.Details, Does.Contain("PulseWidth: FAIL"));
                Assert.That(failure.Details, Does.Contain("Target: 0x81"));
                Assert.That(failure.Details, Does.Contain("Expected: 99"));
                Assert.That(failure.Details, Does.Contain("Observed: 21"));
                Assert.That(failure.Details, Does.Contain("Observed sequence: #1"));
            });
        }

        private static async Task<byte[]> QueryModuleAsync()
        {
            using var transport = new EmulatedWssTransport();
            await transport.ConnectAsync();
            byte[] reply = null;
            transport.BytesReceived += bytes => reply = bytes;
            var request = new WssFrameCodec().Frame(
                0x00,
                0x81,
                new byte[] { (byte)WSSMessageIDs.ModuleQuery, 0x01, 0x01 });

            await transport.SendAsync(request);
            return reply;
        }

        private static void AssertConforms(
            EmulatedWssTransport transport,
            WssStimulationBaseline baseline,
            WSSMessageIDs messageId,
            int[] pulseAmplitudes,
            int[] pulseWidths,
            int[] interPulseIntervals)
        {
            for (int channel = 1; channel <= 3; channel++)
            {
                var result = transport.Conformance.ValidateStimulation(
                    new WssStimulationExpectation(
                        0x81,
                        channel,
                        pulseAmplitudes[channel - 1],
                        pulseWidths[channel - 1],
                        interPulseIntervals[channel - 1],
                        (byte)messageId,
                        false),
                    baseline);

                Assert.That(result.Passed, Is.True, string.Join(Environment.NewLine, result.Failures));
            }
        }

        private static bool ContainsSequence(byte[] bytes, byte first, byte second)
        {
            for (int i = 0; i < bytes.Length - 1; i++)
            {
                if (bytes[i] == first && bytes[i + 1] == second)
                    return true;
            }

            return false;
        }
    }
}
