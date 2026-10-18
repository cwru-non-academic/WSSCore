using System;

namespace Wss.Testing
{
    /// <summary>
    /// Describes one wire frame rejected by the emulated WSS device.
    /// </summary>
    public sealed class WssProtocolError
    {
        private readonly byte[] _rawFrame;

        internal WssProtocolError(
            long sequenceNumber,
            WssProtocolErrorKind kind,
            string description,
            byte[] rawFrame)
        {
            SequenceNumber = sequenceNumber;
            Kind = kind;
            Description = description ?? string.Empty;
            _rawFrame = Clone(rawFrame);
        }

        /// <summary>Gets the one-based protocol-error sequence number.</summary>
        public long SequenceNumber { get; }

        /// <summary>Gets the category of protocol error.</summary>
        public WssProtocolErrorKind Kind { get; }

        /// <summary>Gets a diagnostic description of the rejected input.</summary>
        public string Description { get; }

        /// <summary>Gets a copy of the rejected wire bytes. Complete frames include their END terminator.</summary>
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
