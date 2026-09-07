namespace Wss.Testing
{
    /// <summary>
    /// Describes the effective stimulation state of one event slot after a decoded stream message.
    /// </summary>
    public sealed class WssStimulationObservation
    {
        internal WssStimulationObservation(
            long sequenceNumber,
            byte target,
            int channel,
            byte messageId,
            int pulseAmplitude,
            int pulseWidth,
            int interPulseInterval)
        {
            SequenceNumber = sequenceNumber;
            Target = target;
            Channel = channel;
            MessageId = messageId;
            PulseAmplitude = pulseAmplitude;
            PulseWidth = pulseWidth;
            InterPulseInterval = interPulseInterval;
        }

        /// <summary>Gets the receive sequence number of the stream message.</summary>
        public long SequenceNumber { get; }

        /// <summary>Gets the wire target address.</summary>
        public byte Target { get; }

        /// <summary>Gets the one-based event/channel slot within the target.</summary>
        public int Channel { get; }

        /// <summary>Gets the stream message identifier that produced this state.</summary>
        public byte MessageId { get; }

        /// <summary>Gets the effective raw pulse amplitude received by the device.</summary>
        public int PulseAmplitude { get; }

        /// <summary>Gets the effective pulse width received by the device, in microseconds.</summary>
        public int PulseWidth { get; }

        /// <summary>Gets the effective inter-pulse interval received by the device, in milliseconds.</summary>
        public int InterPulseInterval { get; }
    }
}
