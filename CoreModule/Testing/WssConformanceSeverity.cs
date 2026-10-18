namespace Wss.Testing
{
    /// <summary>
    /// Describes the effect of one initialization or configuration conformance finding.
    /// </summary>
    public enum WssConformanceSeverity
    {
        /// <summary>The observed behavior is valid and is reported for context.</summary>
        Info,

        /// <summary>The configuration is potentially problematic but is not known to be invalid.</summary>
        Warning,

        /// <summary>Correctness depends on device state that predates the observation session.</summary>
        NotVerifiable,

        /// <summary>The observed behavior is known to violate a conformance rule.</summary>
        Error
    }
}
