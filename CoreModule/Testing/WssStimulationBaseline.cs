using System.Collections.Generic;

namespace Wss.Testing
{
    /// <summary>
    /// Identifies a non-destructive point in an emulator transcript and its effective stimulation state.
    /// </summary>
    public sealed class WssStimulationBaseline
    {
        private readonly Dictionary<int, WssStimulationObservation> _states;

        internal WssStimulationBaseline(
            long sequenceNumber,
            long protocolErrorSequence,
            IEnumerable<WssStimulationObservation> states)
        {
            SequenceNumber = sequenceNumber;
            ProtocolErrorSequence = protocolErrorSequence;
            _states = new Dictionary<int, WssStimulationObservation>();
            if (states == null)
                return;

            foreach (var state in states)
                _states[Key(state.Target, state.Channel)] = state;
        }

        /// <summary>Gets the last message sequence included in this baseline.</summary>
        public long SequenceNumber { get; }

        internal long ProtocolErrorSequence { get; }

        internal bool TryGetState(byte target, int channel, out WssStimulationObservation observation)
            => _states.TryGetValue(Key(target, channel), out observation);

        private static int Key(byte target, int channel) => (target << 8) | channel;
    }
}
