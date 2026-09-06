using System.Collections.Generic;

namespace Wss.Testing
{
    /// <summary>
    /// Provides read-only protocol observations collected by an emulated WSS device.
    /// </summary>
    public interface IWssConformance
    {
        /// <summary>
        /// Gets a snapshot of valid decoded request messages in receive order.
        /// </summary>
        IReadOnlyList<WssMessageObservation> MessageHistory { get; }

        /// <summary>
        /// Gets a snapshot of rejected protocol input in receive order.
        /// </summary>
        IReadOnlyList<WssProtocolError> ProtocolErrors { get; }

        /// <summary>
        /// Gets snapshots of effective event/channel stimulation state after decoded stream messages.
        /// </summary>
        IReadOnlyList<WssStimulationObservation> StimulationHistory { get; }

        /// <summary>
        /// Validates the observed messages against the deterministic normal initialization contract.
        /// </summary>
        /// <returns>Structured required-message, ordering, protocol-error, and failure results.</returns>
        InitializationConformanceResult ValidateInitialization();

        /// <summary>
        /// Captures the current transcript sequence and effective stimulation state without clearing history.
        /// </summary>
        /// <returns>An immutable baseline for subsequent stimulation validation.</returns>
        WssStimulationBaseline CaptureStimulationBaseline();

        /// <summary>
        /// Validates stimulation received after a baseline against a wire-level expectation.
        /// </summary>
        /// <param name="expectation">Expected target, event/channel, stream message, and stimulation values.</param>
        /// <param name="baseline">Previously captured observation baseline.</param>
        /// <returns>Structured field checks and failure diagnostics.</returns>
        StimulationConformanceResult ValidateStimulation(
            WssStimulationExpectation expectation,
            WssStimulationBaseline baseline);
    }
}
