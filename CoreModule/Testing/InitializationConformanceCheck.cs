namespace Wss.Testing
{
    /// <summary>
    /// Describes one finding produced while validating WSS initialization or configuration.
    /// </summary>
    public sealed class InitializationConformanceCheck
    {
        internal InitializationConformanceCheck(
            string name,
            WssConformanceSeverity severity,
            string details,
            byte? target = null,
            long? sequenceNumber = null)
        {
            Name = name;
            Severity = severity;
            Details = details;
            Target = target;
            SequenceNumber = sequenceNumber;
        }

        /// <summary>Gets the stable name of the check.</summary>
        public string Name { get; }

        /// <summary>Gets whether the finding is non-fatal.</summary>
        public bool Passed => Severity != WssConformanceSeverity.Error;

        /// <summary>Gets the severity of the finding.</summary>
        public WssConformanceSeverity Severity { get; }

        /// <summary>Gets a diagnostic description of the observed or missing behavior.</summary>
        public string Details { get; }

        /// <summary>Gets the affected target, or <see langword="null"/> for a global finding.</summary>
        public byte? Target { get; }

        /// <summary>Gets the observed message sequence, when applicable.</summary>
        public long? SequenceNumber { get; }
    }
}
