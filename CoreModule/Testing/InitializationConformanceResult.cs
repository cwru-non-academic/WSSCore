using System;
using System.Collections.Generic;

namespace Wss.Testing
{
    /// <summary>
    /// Contains the structured result of validating an emulated WSS initialization transcript.
    /// </summary>
    public sealed class InitializationConformanceResult
    {
        internal InitializationConformanceResult(
            IReadOnlyList<InitializationConformanceCheck> requiredMessageChecks,
            IReadOnlyList<InitializationConformanceCheck> orderingChecks,
            IReadOnlyList<WssProtocolError> protocolErrors,
            IReadOnlyList<string> failures)
        {
            RequiredMessageChecks = Copy(requiredMessageChecks);
            OrderingChecks = Copy(orderingChecks);
            ProtocolErrors = Copy(protocolErrors);
            Failures = Copy(failures);
            Passed = Failures.Count == 0;
        }

        /// <summary>Gets whether all required-message and ordering checks passed without protocol errors.</summary>
        public bool Passed { get; }

        /// <summary>Gets the required initialization message checks.</summary>
        public IReadOnlyList<InitializationConformanceCheck> RequiredMessageChecks { get; }

        /// <summary>Gets the semantic partial-order checks.</summary>
        public IReadOnlyList<InitializationConformanceCheck> OrderingChecks { get; }

        /// <summary>Gets wire protocol errors observed during initialization.</summary>
        public IReadOnlyList<WssProtocolError> ProtocolErrors { get; }

        /// <summary>Gets diagnostic text for every failed check and protocol error.</summary>
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
