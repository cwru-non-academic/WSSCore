namespace Wss.CoreModule
{
    /// <summary>
    /// Describes an event to create on the WSS device.
    /// </summary>
    /// <remarks>
    /// This request supports the same payload shapes as the previous overload set:
    /// basic event, event with explicit shape IDs, event with amplitude and pulse-width data,
    /// or event with both shape and amplitude data.
    /// </remarks>
    public sealed class CreateEventRequest
    {
        /// <summary>
        /// Gets or sets the event ID (0..255).
        /// </summary>
        public int EventId { get; set; }

        /// <summary>
        /// Gets or sets the delay from schedule start, in milliseconds.
        /// </summary>
        public int DelayMs { get; set; }

        /// <summary>
        /// Gets or sets the contact configuration ID (0..255).
        /// </summary>
        public int ContactConfigId { get; set; }

        /// <summary>
        /// Gets or sets the optional standard-phase shape ID.
        /// Must be supplied together with <see cref="RechargeShapeId"/>.
        /// </summary>
        public int? StandardShapeId { get; set; }

        /// <summary>
        /// Gets or sets the optional recharge-phase shape ID.
        /// Must be supplied together with <see cref="StandardShapeId"/>.
        /// </summary>
        public int? RechargeShapeId { get; set; }

        /// <summary>
        /// Gets or sets the optional four-element amplitude array for the standard phase.
        /// Must be supplied together with <see cref="RechargeAmplitudes"/> and <see cref="PulseWidths"/>.
        /// </summary>
        public int[] StandardAmplitudes { get; set; }

        /// <summary>
        /// Gets or sets the optional four-element amplitude array for the recharge phase.
        /// Must be supplied together with <see cref="StandardAmplitudes"/> and <see cref="PulseWidths"/>.
        /// </summary>
        public int[] RechargeAmplitudes { get; set; }

        /// <summary>
        /// Gets or sets the optional pulse-width payload.
        /// This is required when amplitude arrays are supplied.
        /// </summary>
        public EventPulseWidths PulseWidths { get; set; }
    }

    /// <summary>
    /// Holds pulse-width values for an event definition.
    /// </summary>
    public sealed class EventPulseWidths
    {
        /// <summary>
        /// Gets or sets the standard-phase pulse width, in microseconds.
        /// </summary>
        public int StandardPw { get; set; }

        /// <summary>
        /// Gets or sets the recharge-phase pulse width, in microseconds.
        /// </summary>
        public int RechargePw { get; set; }

        /// <summary>
        /// Gets or sets the inter-phase delay, in microseconds.
        /// </summary>
        public int Ipd { get; set; }
    }
}
