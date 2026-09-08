using System;

namespace Wss.Testing
{
    /// <summary>
    /// Defines a reusable normalized stimulation behavior contract.
    /// </summary>
    public sealed class WssNormalizedStimulationScenario
    {
        /// <summary>
        /// Creates a normalized stimulation scenario.
        /// </summary>
        /// <param name="id">Stable scenario identifier.</param>
        /// <param name="target">Expected wire target address.</param>
        /// <param name="channel">One-based stimulation channel.</param>
        /// <param name="input">Normalized input, including values used to prove clamping.</param>
        /// <param name="expectation">Expected wire-level stimulation state.</param>
        /// <exception cref="ArgumentException">Thrown when <paramref name="id"/> is empty.</exception>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="expectation"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="input"/> is not finite.</exception>
        public WssNormalizedStimulationScenario(
            string id,
            byte target,
            int channel,
            float input,
            WssStimulationExpectation expectation)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A scenario ID is required.", nameof(id));
            if (float.IsNaN(input) || float.IsInfinity(input))
                throw new ArgumentOutOfRangeException(nameof(input), "Normalized input must be finite.");
            if (expectation == null) throw new ArgumentNullException(nameof(expectation));
            if (expectation.Target != target || expectation.Channel != channel)
                throw new ArgumentException("The stimulation expectation must match the scenario target and channel.", nameof(expectation));

            Id = id;
            Target = target;
            Channel = channel;
            Input = input;
            Expectation = expectation;
        }

        /// <summary>Gets the stable scenario identifier.</summary>
        public string Id { get; }

        /// <summary>Gets the expected wire target address.</summary>
        public byte Target { get; }

        /// <summary>Gets the one-based stimulation channel.</summary>
        public int Channel { get; }

        /// <summary>Gets the normalized input, including any out-of-range value used to prove clamping.</summary>
        public float Input { get; }

        /// <summary>Gets the expected wire-level stimulation state.</summary>
        public WssStimulationExpectation Expectation { get; }
    }
}
