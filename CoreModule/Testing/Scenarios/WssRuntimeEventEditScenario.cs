using System;

namespace Wss.Testing
{
    /// <summary>
    /// Defines a reusable runtime Event ratio-edit behavior contract.
    /// </summary>
    public sealed class WssRuntimeEventEditScenario
    {
        /// <summary>
        /// Creates a runtime Event ratio-edit scenario.
        /// </summary>
        /// <param name="id">Stable scenario identifier.</param>
        /// <param name="target">Target wire address.</param>
        /// <param name="eventId">Event identifier to edit.</param>
        /// <param name="ratio">Ratio supplied through the public API.</param>
        /// <param name="expectedMessageId">Expected wire message identifier.</param>
        /// <param name="expectedSubcommand">Expected Event-edit subcommand.</param>
        /// <param name="expectedValue">Expected encoded ratio value.</param>
        /// <param name="resumeStreamingExpected">Whether streaming must resume after the edit.</param>
        /// <param name="additionalSyncGroupExpected">Whether the edit is expected to transmit another SyncGroup.</param>
        /// <param name="requiresSuccessfulConformance">Whether initialization conformance must remain successful.</param>
        /// <exception cref="ArgumentException">Thrown when <paramref name="id"/> is empty.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the Event ID or ratio is outside its wire range.</exception>
        public WssRuntimeEventEditScenario(
            string id,
            byte target,
            int eventId,
            int ratio,
            byte expectedMessageId,
            byte expectedSubcommand,
            byte expectedValue,
            bool resumeStreamingExpected,
            bool additionalSyncGroupExpected,
            bool requiresSuccessfulConformance)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A scenario ID is required.", nameof(id));
            ValidateByte(eventId, nameof(eventId));
            ValidateByte(ratio, nameof(ratio));

            Id = id;
            Target = target;
            EventId = eventId;
            Ratio = ratio;
            ExpectedMessageId = expectedMessageId;
            ExpectedSubcommand = expectedSubcommand;
            ExpectedValue = expectedValue;
            ResumeStreamingExpected = resumeStreamingExpected;
            AdditionalSyncGroupExpected = additionalSyncGroupExpected;
            RequiresSuccessfulConformance = requiresSuccessfulConformance;
        }

        /// <summary>Gets the stable scenario identifier.</summary>
        public string Id { get; }

        /// <summary>Gets the target wire address.</summary>
        public byte Target { get; }

        /// <summary>Gets the Event identifier to edit.</summary>
        public int EventId { get; }

        /// <summary>Gets the ratio supplied through the public API.</summary>
        public int Ratio { get; }

        /// <summary>Gets the expected wire message identifier.</summary>
        public byte ExpectedMessageId { get; }

        /// <summary>Gets the expected Event-edit subcommand.</summary>
        public byte ExpectedSubcommand { get; }

        /// <summary>Gets the expected encoded ratio value.</summary>
        public byte ExpectedValue { get; }

        /// <summary>Gets whether streaming must resume after the edit.</summary>
        public bool ResumeStreamingExpected { get; }

        /// <summary>Gets whether the edit is expected to transmit another SyncGroup.</summary>
        public bool AdditionalSyncGroupExpected { get; }

        /// <summary>Gets whether initialization conformance must remain successful.</summary>
        public bool RequiresSuccessfulConformance { get; }

        private static void ValidateByte(int value, string parameterName)
        {
            if (value < byte.MinValue || value > byte.MaxValue)
                throw new ArgumentOutOfRangeException(parameterName, "Value must be from 0 through 255.");
        }
    }
}
