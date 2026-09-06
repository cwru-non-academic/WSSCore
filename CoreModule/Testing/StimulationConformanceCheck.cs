namespace Wss.Testing
{
    /// <summary>
    /// Describes one expected-versus-observed stimulation field comparison.
    /// </summary>
    public sealed class StimulationConformanceCheck
    {
        internal StimulationConformanceCheck(
            string name,
            bool passed,
            string expected,
            string observed,
            string details)
        {
            Name = name;
            Passed = passed;
            Expected = expected;
            Observed = observed;
            Details = details;
        }

        /// <summary>Gets the stable field name.</summary>
        public string Name { get; }

        /// <summary>Gets whether the field matched.</summary>
        public bool Passed { get; }

        /// <summary>Gets the formatted expected value.</summary>
        public string Expected { get; }

        /// <summary>Gets the formatted observed value.</summary>
        public string Observed { get; }

        /// <summary>Gets a diagnostic description of the comparison.</summary>
        public string Details { get; }
    }
}
