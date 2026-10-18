using System;

namespace Wss.Testing
{
    /// <summary>
    /// Defines a reusable initialization behavior contract for a WSS public API.
    /// </summary>
    public sealed class WssInitializationScenario
    {
        /// <summary>
        /// Creates an initialization scenario.
        /// </summary>
        /// <param name="id">Stable scenario identifier.</param>
        /// <param name="target">Expected wire target address.</param>
        /// <param name="requiresOperationalState">Whether initialization must reach an operational state.</param>
        /// <param name="requiresStreamObservation">Whether at least one valid stream observation is required.</param>
        /// <param name="requiresSuccessfulConformance">Whether initialization conformance must pass.</param>
        /// <exception cref="ArgumentException">Thrown when <paramref name="id"/> is empty.</exception>
        public WssInitializationScenario(
            string id,
            byte target,
            bool requiresOperationalState,
            bool requiresStreamObservation,
            bool requiresSuccessfulConformance)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A scenario ID is required.", nameof(id));

            Id = id;
            Target = target;
            RequiresOperationalState = requiresOperationalState;
            RequiresStreamObservation = requiresStreamObservation;
            RequiresSuccessfulConformance = requiresSuccessfulConformance;
        }

        /// <summary>Gets the stable scenario identifier.</summary>
        public string Id { get; }

        /// <summary>Gets the expected wire target address.</summary>
        public byte Target { get; }

        /// <summary>Gets whether initialization must reach an operational state.</summary>
        public bool RequiresOperationalState { get; }

        /// <summary>Gets whether at least one valid stream observation is required.</summary>
        public bool RequiresStreamObservation { get; }

        /// <summary>Gets whether initialization conformance must pass.</summary>
        public bool RequiresSuccessfulConformance { get; }
    }
}
