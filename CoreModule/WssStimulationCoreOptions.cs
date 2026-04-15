namespace Wss.CoreModule
{
    /// <summary>
    /// Holds startup and default stimulation settings for <see cref="WssStimulationCore"/>.
    /// </summary>
    /// <remarks>
    /// These values affect configuration loading, setup retry behavior, and the default schedule/event values used
    /// during the core's initial device setup. Packet spacing is intentionally not exposed here because it remains a
    /// fixed hardware/radio constraint in the core implementation.
    /// </remarks>
    public sealed class WssStimulationCoreOptions
    {
        /// <summary>
        /// Gets or sets the core configuration file path.
        /// Directory paths resolve to <c>stimConfig.json</c> in that directory.
        /// </summary>
        public string ConfigPath { get; set; }

        /// <summary>
        /// Gets or sets the maximum number of setup retries before the core enters the Error state.
        /// This must be at least 1. The default is 5.
        /// </summary>
        public int MaxSetupTries { get; set; } = 5;

        /// <summary>
        /// Gets or sets the default inter-pulse interval used during initial setup, in milliseconds.
        /// The default is 10.
        /// </summary>
        public int DefaultIpi { get; set; } = 10;

        /// <summary>
        /// Gets or sets the default amplitude used during initial setup, in mA.
        /// The default is 1.0.
        /// </summary>
        public float DefaultAmp { get; set; } = 1.0f;

        /// <summary>
        /// Gets or sets the default sync group used during initial setup.
        /// The default is 170.
        /// </summary>
        public int DefaultSync { get; set; } = 170;

        /// <summary>
        /// Gets or sets the default event ratio used during initial setup.
        /// The default is 8.
        /// </summary>
        public int DefaultRatio { get; set; } = 8;

        /// <summary>
        /// Gets or sets the default inter-phase delay used during initial setup, in microseconds.
        /// The default is 50.
        /// </summary>
        public int DefaultIpd { get; set; } = 50;
    }
}
