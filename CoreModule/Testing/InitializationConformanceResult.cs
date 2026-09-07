using System;
using System.Collections.Generic;
using System.Linq;

namespace Wss.Testing
{
    /// <summary>
    /// Contains the structured result of validating emulated WSS configuration observations.
    /// </summary>
    public sealed class InitializationConformanceResult
    {
        internal InitializationConformanceResult(
            IReadOnlyList<InitializationConformanceCheck> checks,
            IReadOnlyList<WssProtocolError> protocolErrors)
        {
            Checks = Copy(checks);
            RequiredMessageChecks = Checks;
            OrderingChecks = Array.Empty<InitializationConformanceCheck>();
            ProtocolErrors = Copy(protocolErrors);
            Errors = Checks.Where(check => check.Severity == WssConformanceSeverity.Error).ToArray();
            Warnings = Checks.Where(check => check.Severity == WssConformanceSeverity.Warning).ToArray();
            NotVerifiable = Checks.Where(check => check.Severity == WssConformanceSeverity.NotVerifiable).ToArray();
            Failures = Errors.Select(check => check.Details)
                .Concat(ProtocolErrors.Select(error =>
                    $"Protocol error #{error.SequenceNumber}: {error.Kind}: {error.Description}"))
                .ToArray();
            Passed = Failures.Count == 0;
        }

        /// <summary>Gets whether validation completed without errors or wire protocol failures.</summary>
        public bool Passed { get; }

        /// <summary>Gets all structured conformance findings.</summary>
        public IReadOnlyList<InitializationConformanceCheck> Checks { get; }

        /// <summary>Gets all findings through the original required-message compatibility view.</summary>
        public IReadOnlyList<InitializationConformanceCheck> RequiredMessageChecks { get; }

        /// <summary>Gets the legacy ordering-check view, which is empty for state-based validation.</summary>
        public IReadOnlyList<InitializationConformanceCheck> OrderingChecks { get; }

        /// <summary>Gets findings that represent known conformance violations.</summary>
        public IReadOnlyList<InitializationConformanceCheck> Errors { get; }

        /// <summary>Gets non-fatal configuration warnings.</summary>
        public IReadOnlyList<InitializationConformanceCheck> Warnings { get; }

        /// <summary>Gets findings whose correctness depends on pre-session device state.</summary>
        public IReadOnlyList<InitializationConformanceCheck> NotVerifiable { get; }

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
