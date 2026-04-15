namespace Wss.CoreModule
{
    /// <summary>
    /// Describes a contact configuration to create on the WSS device.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The setup arrays use caller-facing ordering where index 0 is closest to the switch and index 3 is farthest.
    /// Each element must be 0 (unused), 1 (source), or 2 (sink).
    /// </para>
    /// <para>
    /// LED configuration is optional and may be ignored by firmware that does not support LED settings.
    /// </para>
    /// </remarks>
    public sealed class ContactConfigDefinition
    {
        /// <summary>
        /// Gets or sets the contact configuration ID (0..255).
        /// </summary>
        public int ContactId { get; set; }

        /// <summary>
        /// Gets or sets the stimulation-phase contact roles.
        /// Provide exactly 4 values where index 0 is closest to the switch.
        /// </summary>
        public int[] StimSetup { get; set; }

        /// <summary>
        /// Gets or sets the recharge-phase contact roles.
        /// Provide exactly 4 values where index 0 is closest to the switch.
        /// </summary>
        public int[] RechargeSetup { get; set; }

        /// <summary>
        /// Gets or sets the optional LED bitmask.
        /// Leave null to omit LED configuration from the command payload.
        /// </summary>
        public int? Leds { get; set; }
    }
}
