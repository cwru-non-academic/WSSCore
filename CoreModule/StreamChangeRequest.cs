namespace Wss.CoreModule
{
    /// <summary>
    /// Describes a streaming update for PA, PW, and IPI groups.
    /// </summary>
    /// <remarks>
    /// The protocol supports omitting at most one of the three groups. Each supplied array must contain exactly
    /// three values, one per streamed schedule slot. The request itself is required by
    /// <see cref="WssClient.StreamChange(StreamChangeRequest, WssTarget, System.Threading.CancellationToken)"/>.
    /// </remarks>
    public sealed class StreamChangeRequest
    {
        /// <summary>
        /// Gets or sets the three pulse amplitudes (0..255).
        /// Leave null to omit the PA group.
        /// </summary>
        public int[] PulseAmplitudes { get; set; }

        /// <summary>
        /// Gets or sets the three pulse widths (0..255).
        /// Leave null to omit the PW group.
        /// </summary>
        public int[] PulseWidths { get; set; }

        /// <summary>
        /// Gets or sets the three inter-pulse intervals, in milliseconds (0..255).
        /// Leave null to omit the IPI group.
        /// </summary>
        public int[] InterPulseIntervals { get; set; }
    }
}
