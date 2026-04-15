using System;
using System.Threading.Tasks;

namespace Wss.CoreModule
{
    /// <summary>
    /// Holds behavior settings for <see cref="TestModeTransport"/>.
    /// </summary>
    public sealed class TestModeTransportOptions
    {
        /// <summary>
        /// Gets or sets the artificial latency applied to inbound delivery.
        /// </summary>
        public TimeSpan BaseLatency { get; set; } = TimeSpan.Zero;

        /// <summary>
        /// Gets or sets the random jitter added to <see cref="BaseLatency"/>, in milliseconds.
        /// </summary>
        public int JitterMs { get; set; }

        /// <summary>
        /// Gets or sets the maximum inbound chunk size.
        /// Values greater than zero split inbound payloads into chunks up to this size.
        /// </summary>
        public int MaxInboundChunkSize { get; set; }

        /// <summary>
        /// Gets or sets the probability in the range [0..1] of randomly dropping an inbound chunk.
        /// </summary>
        public double InboundDropProbability { get; set; }

        /// <summary>
        /// Gets or sets the random generator used for jitter, chunk sizing, drops, and synthetic replies.
        /// When null, <see cref="TestModeTransport"/> creates a default instance.
        /// </summary>
        public Random Rng { get; set; } = new Random();

        /// <summary>
        /// Gets or sets the payload used when an incoming checksum is invalid.
        /// When null, <see cref="TestModeTransport"/> uses a default fallback payload.
        /// </summary>
        public byte[] FallbackPayload { get; set; } = new byte[] { 0xE1, 0xE2, 0xE3 };

        /// <summary>
        /// Gets or sets an optional override auto-responder.
        /// When null, the built-in responder is used.
        /// </summary>
        public Func<byte[], Task<byte[]>> AutoResponderAsync { get; set; }
    }
}
