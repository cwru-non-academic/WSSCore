namespace Wss.Testing
{
    /// <summary>
    /// Describes one required-message or ordering check performed for WSS initialization.
    /// </summary>
    public sealed class InitializationConformanceCheck
    {
        internal InitializationConformanceCheck(string name, bool passed, string details)
        {
            Name = name;
            Passed = passed;
            Details = details;
        }

        /// <summary>Gets the stable name of the check.</summary>
        public string Name { get; }

        /// <summary>Gets whether the observed initialization satisfied the check.</summary>
        public bool Passed { get; }

        /// <summary>Gets a diagnostic description of the observed or missing behavior.</summary>
        public string Details { get; }
    }
}
