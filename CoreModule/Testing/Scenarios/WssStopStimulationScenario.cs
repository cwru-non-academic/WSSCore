using System;

namespace Wss.Testing
{
    /// <summary>
    /// Defines a reusable stop-stimulation behavior contract.
    /// </summary>
    public sealed class WssStopStimulationScenario
    {
        /// <summary>
        /// Creates a stop-stimulation scenario.
        /// </summary>
        /// <param name="id">Stable scenario identifier.</param>
        /// <param name="target">Target wire address.</param>
        /// <param name="expectedMessageId">Expected wire message identifier.</param>
        /// <param name="expectedOperationValue">Expected encoded STOP value.</param>
        /// <param name="resumeStreamingExpected">Whether streaming must resume after stopping.</param>
        /// <exception cref="ArgumentException">Thrown when <paramref name="id"/> is empty.</exception>
        public WssStopStimulationScenario(
            string id,
            byte target,
            byte expectedMessageId,
            byte expectedOperationValue,
            bool resumeStreamingExpected)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A scenario ID is required.", nameof(id));

            Id = id;
            Target = target;
            ExpectedMessageId = expectedMessageId;
            ExpectedOperationValue = expectedOperationValue;
            ResumeStreamingExpected = resumeStreamingExpected;
        }

        /// <summary>Gets the stable scenario identifier.</summary>
        public string Id { get; }

        /// <summary>Gets the target wire address.</summary>
        public byte Target { get; }

        /// <summary>Gets the expected wire message identifier.</summary>
        public byte ExpectedMessageId { get; }

        /// <summary>Gets the expected encoded STOP value.</summary>
        public byte ExpectedOperationValue { get; }

        /// <summary>Gets whether streaming must resume after stopping.</summary>
        public bool ResumeStreamingExpected { get; }
    }
}
