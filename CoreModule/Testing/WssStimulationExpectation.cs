using System;

namespace Wss.Testing
{
    /// <summary>
    /// Defines the expected wire-level stimulation state for one target event/channel slot.
    /// </summary>
    public sealed class WssStimulationExpectation
    {
        /// <summary>
        /// Creates a stimulation expectation.
        /// </summary>
        /// <param name="target">Expected wire target address.</param>
        /// <param name="channel">Expected one-based event/channel slot, from 1 through 3.</param>
        /// <param name="pulseAmplitude">Expected raw pulse amplitude, from 0 through 255.</param>
        /// <param name="pulseWidth">Expected pulse width in microseconds, from 0 through 255.</param>
        /// <param name="interPulseInterval">Expected inter-pulse interval in milliseconds, from 0 through 255.</param>
        /// <param name="messageId">Expected stream message identifier.</param>
        /// <param name="requireUnrelatedChannelsUnchanged">Whether other slots on the target must match the baseline.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when a slot or stimulation value is outside its wire range.</exception>
        public WssStimulationExpectation(
            byte target,
            int channel,
            int pulseAmplitude,
            int pulseWidth,
            int interPulseInterval,
            byte messageId,
            bool requireUnrelatedChannelsUnchanged = true)
        {
            if (channel < 1 || channel > 3) throw new ArgumentOutOfRangeException(nameof(channel));
            ValidateByte(pulseAmplitude, nameof(pulseAmplitude));
            ValidateByte(pulseWidth, nameof(pulseWidth));
            ValidateByte(interPulseInterval, nameof(interPulseInterval));

            Target = target;
            Channel = channel;
            PulseAmplitude = pulseAmplitude;
            PulseWidth = pulseWidth;
            InterPulseInterval = interPulseInterval;
            MessageId = messageId;
            RequireUnrelatedChannelsUnchanged = requireUnrelatedChannelsUnchanged;
        }

        /// <summary>Gets the expected wire target address.</summary>
        public byte Target { get; }

        /// <summary>Gets the expected one-based event/channel slot.</summary>
        public int Channel { get; }

        /// <summary>Gets the expected raw pulse amplitude.</summary>
        public int PulseAmplitude { get; }

        /// <summary>Gets the expected pulse width in microseconds.</summary>
        public int PulseWidth { get; }

        /// <summary>Gets the expected inter-pulse interval in milliseconds.</summary>
        public int InterPulseInterval { get; }

        /// <summary>Gets the expected stream message identifier.</summary>
        public byte MessageId { get; }

        /// <summary>Gets whether event/channel slots other than <see cref="Channel"/> must remain unchanged.</summary>
        public bool RequireUnrelatedChannelsUnchanged { get; }

        private static void ValidateByte(int value, string parameterName)
        {
            if (value < byte.MinValue || value > byte.MaxValue)
                throw new ArgumentOutOfRangeException(parameterName, "Value must be from 0 through 255.");
        }
    }
}
