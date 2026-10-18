using System;

namespace Wss.Testing
{
    /// <summary>
    /// Describes one valid WSS request decoded from wire bytes by the emulator.
    /// </summary>
    public sealed class WssMessageObservation
    {
        private readonly byte[] _payload;
        private readonly byte[] _rawFrame;

        internal WssMessageObservation(
            long sequenceNumber,
            byte sender,
            byte target,
            byte messageId,
            byte[] payload,
            byte[] rawFrame)
        {
            SequenceNumber = sequenceNumber;
            Sender = sender;
            Target = target;
            MessageId = messageId;
            _payload = Clone(payload);
            _rawFrame = Clone(rawFrame);
        }

        /// <summary>Gets the one-based receive sequence number.</summary>
        public long SequenceNumber { get; }

        /// <summary>Gets the decoded sender address.</summary>
        public byte Sender { get; }

        /// <summary>Gets the decoded target address.</summary>
        public byte Target { get; }

        /// <summary>Gets the decoded WSS message identifier.</summary>
        public byte MessageId { get; }

        /// <summary>Gets a copy of the decoded protocol payload, including message ID and length.</summary>
        public byte[] Payload => Clone(_payload);

        /// <summary>Gets a copy of the escaped wire frame, including its END terminator.</summary>
        public byte[] RawFrame => Clone(_rawFrame);

        private static byte[] Clone(byte[] value)
        {
            if (value == null || value.Length == 0)
                return Array.Empty<byte>();

            var copy = new byte[value.Length];
            Buffer.BlockCopy(value, 0, copy, 0, value.Length);
            return copy;
        }
    }
}
