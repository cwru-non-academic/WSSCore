using System;

namespace Wss.Testing
{
    /// <summary>
    /// Defines a reusable direct analog stimulation behavior contract.
    /// </summary>
    public sealed class WssAnalogStimulationScenario
    {
        /// <summary>
        /// Creates a direct analog stimulation scenario.
        /// </summary>
        /// <param name="id">Stable scenario identifier.</param>
        /// <param name="target">Expected wire target address.</param>
        /// <param name="channel">One-based stimulation channel.</param>
        /// <param name="amplitudeMa">Requested pulse amplitude in milliamperes.</param>
        /// <param name="pulseWidth">Requested pulse width in microseconds.</param>
        /// <param name="interPulseInterval">Requested inter-pulse interval in milliseconds.</param>
        /// <param name="expectation">Expected wire-level stimulation state.</param>
        /// <exception cref="ArgumentException">Thrown when <paramref name="id"/> is empty.</exception>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="expectation"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the amplitude is not finite.</exception>
        public WssAnalogStimulationScenario(
            string id,
            byte target,
            int channel,
            float amplitudeMa,
            int pulseWidth,
            int interPulseInterval,
            WssStimulationExpectation expectation)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A scenario ID is required.", nameof(id));
            if (float.IsNaN(amplitudeMa) || float.IsInfinity(amplitudeMa))
                throw new ArgumentOutOfRangeException(nameof(amplitudeMa), "Amplitude must be finite.");
            if (expectation == null) throw new ArgumentNullException(nameof(expectation));
            if (expectation.Target != target || expectation.Channel != channel ||
                expectation.PulseWidth != pulseWidth || expectation.InterPulseInterval != interPulseInterval)
            {
                throw new ArgumentException("The stimulation expectation must match the scenario target and direct inputs.", nameof(expectation));
            }

            Id = id;
            Target = target;
            Channel = channel;
            AmplitudeMa = amplitudeMa;
            PulseWidth = pulseWidth;
            InterPulseInterval = interPulseInterval;
            Expectation = expectation;
        }

        /// <summary>Gets the stable scenario identifier.</summary>
        public string Id { get; }

        /// <summary>Gets the expected wire target address.</summary>
        public byte Target { get; }

        /// <summary>Gets the one-based stimulation channel.</summary>
        public int Channel { get; }

        /// <summary>Gets the requested pulse amplitude in milliamperes.</summary>
        public float AmplitudeMa { get; }

        /// <summary>Gets the requested pulse width in microseconds.</summary>
        public int PulseWidth { get; }

        /// <summary>Gets the requested inter-pulse interval in milliseconds.</summary>
        public int InterPulseInterval { get; }

        /// <summary>Gets the expected wire-level stimulation state.</summary>
        public WssStimulationExpectation Expectation { get; }
    }
}
