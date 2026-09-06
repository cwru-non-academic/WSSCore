using System;
using System.Collections.Generic;
using Wss.CoreModule;

namespace Wss.Testing
{
    /// <summary>
    /// Consumes WSS wire-byte chunks, records decoded requests, and produces framed WSS replies.
    /// </summary>
    /// <remarks>
    /// Input may end at arbitrary byte boundaries or contain multiple complete frames. Normal behavior
    /// is deterministic and uses <see cref="EmulatedWssDeviceProfile.Default"/>.
    /// </remarks>
    public sealed class EmulatedWssDevice
    {
        private const byte End = 0xC0;
        private const int MaxAccumulatedBytes = 256;

        private readonly object _gate = new object();
        private readonly List<byte> _accumulator = new List<byte>(256);
        private readonly List<WssMessageObservation> _messageHistory = new List<WssMessageObservation>();
        private readonly List<WssProtocolError> _protocolErrors = new List<WssProtocolError>();
        private readonly WssFrameCodec _codec = new WssFrameCodec();
        private long _messageSequence;
        private long _errorSequence;

        /// <summary>
        /// Creates a device using the fixed deterministic emulator profile.
        /// </summary>
        public EmulatedWssDevice()
        {
            Profile = EmulatedWssDeviceProfile.Default;
        }

        /// <summary>Gets the capability profile reported by this device.</summary>
        public EmulatedWssDeviceProfile Profile { get; }

        /// <summary>
        /// Processes one arbitrary chunk of WSS wire bytes and returns zero or more complete wire replies.
        /// </summary>
        /// <param name="chunk">Raw escaped WSS bytes. Chunks may contain partial or multiple frames.</param>
        /// <returns>Complete escaped response frames in request order.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="chunk"/> is null.</exception>
        public IReadOnlyList<byte[]> ProcessBytes(byte[] chunk)
        {
            if (chunk == null) throw new ArgumentNullException(nameof(chunk));

            lock (_gate)
            {
                var replies = new List<byte[]>();
                for (int i = 0; i < chunk.Length; i++)
                {
                    byte value = chunk[i];
                    if (value != End)
                    {
                        if (_accumulator.Count >= MaxAccumulatedBytes)
                        {
                            var oversizedInput = new byte[_accumulator.Count + 1];
                            _accumulator.CopyTo(oversizedInput, 0);
                            oversizedInput[oversizedInput.Length - 1] = value;
                            _accumulator.Clear();
                            RecordError(
                                WssProtocolErrorKind.MalformedFrame,
                                "Frame exceeded the 256-byte accumulation limit.",
                                oversizedInput);
                            continue;
                        }

                        _accumulator.Add(value);
                        continue;
                    }

                    if (_accumulator.Count == 0)
                        continue;

                    var preEscaped = _accumulator.ToArray();
                    _accumulator.Clear();
                    var rawFrame = AppendEnd(preEscaped);
                    var reply = ProcessFrame(preEscaped, rawFrame);
                    if (reply != null)
                        replies.Add(reply);
                }

                return replies.ToArray();
            }
        }

        internal IReadOnlyList<WssMessageObservation> GetMessageHistorySnapshot()
        {
            lock (_gate)
            {
                return _messageHistory.ToArray();
            }
        }

        internal IReadOnlyList<WssProtocolError> GetProtocolErrorsSnapshot()
        {
            lock (_gate)
            {
                return _protocolErrors.ToArray();
            }
        }

        private byte[] ProcessFrame(byte[] preEscaped, byte[] rawFrame)
        {
            if (!WssFrameCodec.TryUnescapeAndValidate(preEscaped, out var frame))
            {
                var kind = frame.Length >= 3
                    ? WssProtocolErrorKind.InvalidChecksum
                    : WssProtocolErrorKind.MalformedFrame;
                string description = kind == WssProtocolErrorKind.InvalidChecksum
                    ? "Frame checksum is invalid."
                    : "Frame is too short to contain addresses and a checksum.";
                RecordError(kind, description, rawFrame);
                return null;
            }

            // Minimum request: [sender][target][messageId][length][checksum].
            if (frame.Length < 5)
            {
                RecordError(WssProtocolErrorKind.MalformedFrame, "Frame does not contain a complete WSS request header.", rawFrame);
                return null;
            }

            int payloadLength = frame.Length - 3;
            var payload = new byte[payloadLength];
            Buffer.BlockCopy(frame, 2, payload, 0, payloadLength);

            int declaredLength = payload[1];
            if (declaredLength != payload.Length - 2)
            {
                RecordError(WssProtocolErrorKind.MalformedFrame, "Payload length does not match the declared WSS length.", rawFrame);
                return null;
            }

            byte sender = frame[0];
            byte target = frame[1];
            byte messageId = payload[0];
            _messageHistory.Add(new WssMessageObservation(
                ++_messageSequence,
                sender,
                target,
                messageId,
                payload,
                rawFrame));

            if (messageId >= (byte)WSSMessageIDs.StreamChangeAll &&
                messageId <= (byte)WSSMessageIDs.StreamChangeNoPA)
            {
                return null;
            }

            var responsePayload = BuildResponsePayload(messageId, payload);
            return _codec.Frame(target, sender, responsePayload);
        }

        private byte[] BuildResponsePayload(byte messageId, byte[] requestPayload)
        {
            switch (messageId)
            {
                case (byte)WSSMessageIDs.ModuleQuery:
                    return BuildModuleQueryPayload();

                case (byte)WSSMessageIDs.StimulationSwitch:
                {
                    var response = Clone(requestPayload);
                    if (response.Length >= 3)
                    {
                        if (response[2] == 0x03) response[2] = 0x01;
                        else if (response[2] == 0x04) response[2] = 0x00;
                    }
                    return response;
                }

                case (byte)WSSMessageIDs.CreateContactConfig:
                case (byte)WSSMessageIDs.CreateEvent:
                case (byte)WSSMessageIDs.CreateSchedule:
                case (byte)WSSMessageIDs.EditEventConfig:
                case (byte)WSSMessageIDs.AddEventToSchedule:
                    if (requestPayload.Length >= 3)
                        return new[] { messageId, (byte)0x01, requestPayload[2] };
                    return Clone(requestPayload);

                default:
                    return Clone(requestPayload);
            }
        }

        private byte[] BuildModuleQueryPayload()
        {
            var data = new byte[16];
            data[0] = Profile.ModuleType;
            data[10] = (byte)((Profile.SupportsTenMilliAmp ? 0x02 : 0x00) |
                              (Profile.SupportsPulseGuard ? 0x04 : 0x00));
            data[12] = Profile.IpdUs;
            data[13] = Profile.PaStep;
            data[14] = Profile.PaLimit;
            data[15] = Profile.PwLimit;

            var payload = new byte[2 + data.Length];
            payload[0] = (byte)WSSMessageIDs.RequestAnalog;
            payload[1] = (byte)data.Length;
            Buffer.BlockCopy(data, 0, payload, 2, data.Length);
            return payload;
        }

        private void RecordError(WssProtocolErrorKind kind, string description, byte[] rawFrame)
        {
            _protocolErrors.Add(new WssProtocolError(++_errorSequence, kind, description, rawFrame));
        }

        private static byte[] AppendEnd(byte[] preEscaped)
        {
            var frame = new byte[preEscaped.Length + 1];
            Buffer.BlockCopy(preEscaped, 0, frame, 0, preEscaped.Length);
            frame[frame.Length - 1] = End;
            return frame;
        }

        private static byte[] Clone(byte[] value)
        {
            var copy = new byte[value.Length];
            Buffer.BlockCopy(value, 0, copy, 0, value.Length);
            return copy;
        }
    }
}
