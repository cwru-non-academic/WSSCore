using System;
using System.Collections.Generic;

namespace Wss.Testing
{
    /// <summary>
    /// Contains the structured result of validating post-baseline stimulation behavior.
    /// </summary>
    public sealed class StimulationConformanceResult
    {
        internal StimulationConformanceResult(
            WssStimulationExpectation expected,
            WssStimulationObservation observed,
            IReadOnlyList<StimulationConformanceCheck> fieldChecks,
            IReadOnlyList<string> failures)
        {
            Expected = expected;
            Observed = observed;
            FieldChecks = Copy(fieldChecks);
            Failures = Copy(failures);
            Passed = Failures.Count == 0;
        }

        /// <summary>Gets whether every stimulation check passed.</summary>
        public bool Passed { get; }

        /// <summary>Gets the requested stimulation expectation.</summary>
        public WssStimulationExpectation Expected { get; }

        /// <summary>Gets the selected post-baseline observation, or null when none was received.</summary>
        public WssStimulationObservation Observed { get; }

        /// <summary>Gets the individual expected-versus-observed field checks.</summary>
        public IReadOnlyList<StimulationConformanceCheck> FieldChecks { get; }

        /// <summary>Gets diagnostics for every failed field or post-baseline protocol error.</summary>
        public IReadOnlyList<string> Failures { get; }

        private static IReadOnlyList<T> Copy<T>(IReadOnlyList<T> source)
        {
            if (source == null || source.Count == 0)
                return Array.Empty<T>();

            var copy = new T[source.Count];
            for (int i = 0; i < source.Count; i++)
                copy[i] = source[i];
            return copy;
        }
    }
}
