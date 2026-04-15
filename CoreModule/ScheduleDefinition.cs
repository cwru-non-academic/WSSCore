namespace Wss.CoreModule
{
    /// <summary>
    /// Describes a schedule to create on the WSS device.
    /// </summary>
    public sealed class ScheduleDefinition
    {
        /// <summary>
        /// Gets or sets the schedule ID (0..255).
        /// </summary>
        public int ScheduleId { get; set; }

        /// <summary>
        /// Gets or sets the schedule duration, in milliseconds.
        /// </summary>
        public int DurationMs { get; set; }

        /// <summary>
        /// Gets or sets the sync-signal group ID (0..255).
        /// </summary>
        public int SyncSignal { get; set; }
    }
}
