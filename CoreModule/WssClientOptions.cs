using System;

namespace Wss.CoreModule
{
    /// <summary>
    /// Holds behavioral settings for <see cref="WssClient"/>.
    /// </summary>
    public sealed class WssClientOptions
    {
        /// <summary>
        /// Gets or sets the sender address byte placed in outbound frames.
        /// The default is <c>0x00</c>.
        /// </summary>
        public byte Sender { get; set; } = 0x00;

        /// <summary>
        /// Gets or sets whether disposing the client also disposes the underlying transport.
        /// The default is true.
        /// </summary>
        public bool OwnsTransport { get; set; } = true;

        /// <summary>
        /// Gets or sets the on-wire broadcast receiver address used for logical <see cref="WssTarget.Broadcast"/>.
        /// The default is <c>0x8F</c>.
        /// </summary>
        public byte BroadcastTarget { get; set; } = 0x8F;

        /// <summary>
        /// Gets or sets the on-wire receiver addresses for logical <see cref="WssTarget.Wss1"/>,
        /// <see cref="WssTarget.Wss2"/>, and <see cref="WssTarget.Wss3"/>.
        /// </summary>
        /// <remarks>
        /// Index 0 maps to <see cref="WssTarget.Wss1"/>, index 1 to <see cref="WssTarget.Wss2"/>, and index 2 to
        /// <see cref="WssTarget.Wss3"/>. When null or shorter than three entries, the client falls back to the
        /// historical defaults for missing values.
        /// </remarks>
        public byte[] WssTargets { get; set; } = new byte[] { 0x81, 0x82, 0x83 };

        /// <summary>
        /// Gets or sets the maximum time to wait for a correlated reply before canceling the request.
        /// This must be greater than zero. The default is 2 seconds.
        /// </summary>
        public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(2);
    }
}
