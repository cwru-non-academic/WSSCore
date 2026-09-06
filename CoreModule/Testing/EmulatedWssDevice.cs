using System;
using System.Collections.Generic;
using Wss.CoreModule;

namespace Wss.Testing
{
    /// <summary>
    /// Consumes WSS wire-byte chunks, records decoded requests, and produces framed WSS replies.
    /// </summary>
    /// <remarks>
    /// Input may end at arbitrary byte boundaries or contain multiple complete frames. Normal behavior
    /// is deterministic and uses <see cref="EmulatedWssDeviceProfile.Default"/>.
    /// </remarks>
    public sealed class EmulatedWssDevice
    {
        private const byte End = 0xC0;
        private const int MaxAccumulatedBytes = 256;

        private readonly object _gate = new object();
        private readonly List<byte> _accumulator = new List<byte>(256);
        private readonly List<WssMessageObservation> _messageHistory = new List<WssMessageObservation>();
        private readonly List<WssStimulationObservation> _stimulationHistory = new List<WssStimulationObservation>();
        private readonly List<WssProtocolError> _protocolErrors = new List<WssProtocolError>();
        private readonly Dictionary<byte, TargetState> _targetStates = new Dictionary<byte, TargetState>();
        private readonly WssFrameCodec _codec = new WssFrameCodec();
        private long _messageSequence;
        private long _errorSequence;

        /// <summary>
        /// Creates a device using the fixed deterministic emulator profile.
        /// </summary>
        public EmulatedWssDevice()
        {
            Profile = EmulatedWssDeviceProfile.Default;
        }

        /// <summary>Gets the capability profile reported by this device.</summary>
        public EmulatedWssDeviceProfile Profile { get; }

        /// <summary>
        /// Processes one arbitrary chunk of WSS wire bytes and returns zero or more complete wire replies.
        /// </summary>
        /// <param name="chunk">Raw escaped WSS bytes. Chunks may contain partial or multiple frames.</param>
        /// <returns>Complete escaped response frames in request order.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="chunk"/> is null.</exception>
        public IReadOnlyList<byte[]> ProcessBytes(byte[] chunk)
        {
            if (chunk == null) throw new ArgumentNullException(nameof(chunk));

            lock (_gate)
            {
                var replies = new List<byte[]>();
                for (int i = 0; i < chunk.Length; i++)
                {
                    byte value = chunk[i];
                    if (value != End)
                    {
                        if (_accumulator.Count >= MaxAccumulatedBytes)
                        {
                            var oversizedInput = new byte[_accumulator.Count + 1];
                            _accumulator.CopyTo(oversizedInput, 0);
                            oversizedInput[oversizedInput.Length - 1] = value;
                            _accumulator.Clear();
                            RecordError(
                                WssProtocolErrorKind.MalformedFrame,
                                "Frame exceeded the 256-byte accumulation limit.",
                                oversizedInput);
                            continue;
                        }

                        _accumulator.Add(value);
                        continue;
                    }

                    if (_accumulator.Count == 0)
                        continue;

                    var preEscaped = _accumulator.ToArray();
                    _accumulator.Clear();
                    var rawFrame = AppendEnd(preEscaped);
                    var reply = ProcessFrame(preEscaped, rawFrame);
                    if (reply != null)
                        replies.Add(reply);
                }

                return replies.ToArray();
            }
        }

        internal IReadOnlyList<WssMessageObservation> GetMessageHistorySnapshot()
        {
            lock (_gate)
            {
                return _messageHistory.ToArray();
            }
        }

        internal IReadOnlyList<WssProtocolError> GetProtocolErrorsSnapshot()
        {
            lock (_gate)
            {
                return _protocolErrors.ToArray();
            }
        }

        internal IReadOnlyList<WssStimulationObservation> GetStimulationHistorySnapshot()
        {
            lock (_gate)
            {
                return _stimulationHistory.ToArray();
            }
        }

        internal WssStimulationBaseline CaptureStimulationBaseline()
        {
            lock (_gate)
            {
                var states = new List<WssStimulationObservation>();
                foreach (var target in _targetStates)
                {
                    foreach (var eventState in target.Value.EventStates)
                    {
                        states.Add(eventState.Value.ToObservation(
                            _messageSequence,
                            target.Key,
                            eventState.Key,
                            eventState.Value.LastMessageId));
                    }
                }

                return new WssStimulationBaseline(_messageSequence, _errorSequence, states);
            }
        }

        private byte[] ProcessFrame(byte[] preEscaped, byte[] rawFrame)
        {
            if (!WssFrameCodec.TryUnescapeAndValidate(preEscaped, out var frame))
            {
                var kind = frame.Length >= 3
                    ? WssProtocolErrorKind.InvalidChecksum
                    : WssProtocolErrorKind.MalformedFrame;
                string description = kind == WssProtocolErrorKind.InvalidChecksum
                    ? "Frame checksum is invalid."
                    : "Frame is too short to contain addresses and a checksum.";
                RecordError(kind, description, rawFrame);
                return null;
            }

            // Minimum request: [sender][target][messageId][length][checksum].
            if (frame.Length < 5)
            {
                RecordError(WssProtocolErrorKind.MalformedFrame, "Frame does not contain a complete WSS request header.", rawFrame);
                return null;
            }

            int payloadLength = frame.Length - 3;
            var payload = new byte[payloadLength];
            Buffer.BlockCopy(frame, 2, payload, 0, payloadLength);

            int declaredLength = payload[1];
            if (declaredLength != payload.Length - 2)
            {
                RecordError(WssProtocolErrorKind.MalformedFrame, "Payload length does not match the declared WSS length.", rawFrame);
                return null;
            }

            byte sender = frame[0];
            byte target = frame[1];
            byte messageId = payload[0];
            long sequenceNumber = ++_messageSequence;
            _messageHistory.Add(new WssMessageObservation(
                sequenceNumber,
                sender,
                target,
                messageId,
                payload,
                rawFrame));

            RecordTargetState(target, messageId, payload, sequenceNumber);

            if (messageId >= (byte)WSSMessageIDs.StreamChangeAll &&
                messageId <= (byte)WSSMessageIDs.StreamChangeNoPA)
            {
                return null;
            }

            var responsePayload = BuildResponsePayload(messageId, payload);
            return _codec.Frame(target, sender, responsePayload);
        }

        private byte[] BuildResponsePayload(byte messageId, byte[] requestPayload)
        {
            switch (messageId)
            {
                case (byte)WSSMessageIDs.ModuleQuery:
                    return BuildModuleQueryPayload();

                case (byte)WSSMessageIDs.Clear:
                case (byte)WSSMessageIDs.SyncGroup:
                    // The Arduino reference acknowledges these commands by echoing their payload.
                    return Clone(requestPayload);

                case (byte)WSSMessageIDs.StimulationSwitch:
                {
                    var response = Clone(requestPayload);
                    if (response.Length >= 3)
                    {
                        if (response[2] == 0x03) response[2] = 0x01;
                        else if (response[2] == 0x04) response[2] = 0x00;
                    }
                    return response;
                }

                case (byte)WSSMessageIDs.CreateContactConfig:
                case (byte)WSSMessageIDs.CreateEvent:
                case (byte)WSSMessageIDs.CreateSchedule:
                case (byte)WSSMessageIDs.EditEventConfig:
                case (byte)WSSMessageIDs.AddEventToSchedule:
                    if (requestPayload.Length >= 3)
                        return new[] { messageId, (byte)0x01, requestPayload[2] };
                    return Clone(requestPayload);

                default:
                    return Clone(requestPayload);
            }
        }

        private void RecordTargetState(byte target, byte messageId, byte[] payload, long sequenceNumber)
        {
            if (!_targetStates.TryGetValue(target, out var state))
            {
                state = new TargetState();
                _targetStates[target] = state;
            }

            switch (messageId)
            {
                case (byte)WSSMessageIDs.Clear:
                    if (HasData(payload, 1) && payload[2] == 0x00)
                    {
                        state.Reset();
                        state.Cleared = true;
                    }
                    break;

                case (byte)WSSMessageIDs.ModuleQuery:
                    if (HasData(payload, 1) && payload[2] == 0x01)
                        state.ModuleQueried = true;
                    break;

                case (byte)WSSMessageIDs.CreateSchedule:
                    if (HasData(payload, 4))
                    {
                        state.ScheduleIds.Add(payload[2]);
                        int duration = (payload[3] << 8) | payload[4];
                        state.ScheduleDurations[payload[2]] = duration;
                        ApplyScheduleDuration(state, payload[2], duration);
                    }
                    break;

                case (byte)WSSMessageIDs.CreateContactConfig:
                    if (HasData(payload, 3))
                        state.ContactConfigurationIds.Add(payload[2]);
                    break;

                case (byte)WSSMessageIDs.CreateEvent:
                    if (HasData(payload, 16))
                    {
                        state.EventIds.Add(payload[2]);
                        state.EventContactConfigurationIds[payload[2]] = payload[4];
                    }
                    RecordCreatedEventState(state, payload);
                    break;

                case (byte)WSSMessageIDs.EditEventConfig:
                    if (HasData(payload, 3) && payload[3] == 0x07)
                        state.EventRatioIds.Add(payload[2]);
                    RecordEditedEventState(state, payload);
                    break;

                case (byte)WSSMessageIDs.AddEventToSchedule:
                    if (HasData(payload, 2))
                    {
                        state.EventScheduleIds[payload[2]] = payload[3];
                        if (state.ScheduleDurations.TryGetValue(payload[3], out int duration))
                            state.GetEventState(payload[2]).InterPulseInterval = duration;
                    }
                    break;

                case (byte)WSSMessageIDs.ChangeScheduleConfig:
                    if (HasData(payload, 3) && payload[2] == 0x03)
                    {
                        byte scheduleId = payload[3];
                        int duration = payload[4];
                        state.ScheduleDurations[scheduleId] = duration;
                        ApplyScheduleDuration(state, scheduleId, duration);
                    }
                    break;

                case (byte)WSSMessageIDs.SyncGroup:
                    if (HasData(payload, 1))
                    {
                        state.SynchronizationConfigured = true;
                        state.SyncSignal = payload[2];
                    }
                    break;

                case (byte)WSSMessageIDs.StimulationSwitch:
                    if (HasData(payload, 1))
                    {
                        if (payload[2] == 0x03) state.StimulationStarted = true;
                        else if (payload[2] == 0x04) state.StimulationStarted = false;
                    }
                    break;

                case (byte)WSSMessageIDs.StreamChangeAll:
                case (byte)WSSMessageIDs.StreamChangeNoIPI:
                case (byte)WSSMessageIDs.StreamChangeNoPW:
                case (byte)WSSMessageIDs.StreamChangeNoPA:
                    RecordStreamState(state, target, messageId, payload, sequenceNumber);
                    break;
            }
        }

        private void RecordStreamState(
            TargetState state,
            byte target,
            byte messageId,
            byte[] payload,
            long sequenceNumber)
        {
            if (!HasData(payload, 9))
                return;

            bool updatePa = messageId != (byte)WSSMessageIDs.StreamChangeNoPA;
            bool updatePw = messageId != (byte)WSSMessageIDs.StreamChangeNoPW;
            bool updateIpi = messageId != (byte)WSSMessageIDs.StreamChangeNoIPI;
            state.StreamingObserved = true;

            for (int channel = 1; channel <= 3; channel++)
            {
                int offset = channel - 1;
                var eventState = state.GetEventState((byte)channel);
                if (updatePa) eventState.PulseAmplitude = payload[2 + offset];
                if (updatePw) eventState.PulseWidth = payload[5 + offset];
                if (updateIpi) eventState.InterPulseInterval = payload[8 + offset];
                eventState.LastMessageId = messageId;

                _stimulationHistory.Add(eventState.ToObservation(
                    sequenceNumber,
                    target,
                    channel,
                    messageId));
            }
        }

        private static void RecordCreatedEventState(TargetState state, byte[] payload)
        {
            int dataLength = payload[1];
            if (dataLength != 14 && dataLength != 16 && dataLength != 17 && dataLength != 19)
                return;

            byte eventId = payload[2];
            var eventState = state.GetEventState(eventId);
            eventState.PulseAmplitude = payload[5];
            eventState.PulseWidth = dataLength == 17 || dataLength == 19
                ? (payload[13] << 8) | payload[14]
                : payload[13];
        }

        private static void RecordEditedEventState(TargetState state, byte[] payload)
        {
            if (payload.Length < 4)
                return;

            var eventState = state.GetEventState(payload[2]);
            byte subcommand = payload[3];
            if (subcommand == 0x02)
            {
                if (HasData(payload, 5))
                    eventState.PulseWidth = payload[4];
                else if (HasData(payload, 8))
                    eventState.PulseWidth = (payload[4] << 8) | payload[5];
            }
            else if (subcommand == 0x04 && HasData(payload, 10))
            {
                eventState.PulseAmplitude = payload[4];
            }
        }

        private static void ApplyScheduleDuration(TargetState state, byte scheduleId, int duration)
        {
            foreach (var assignment in state.EventScheduleIds)
            {
                if (assignment.Value == scheduleId)
                    state.GetEventState(assignment.Key).InterPulseInterval = duration;
            }
        }

        private static bool HasData(byte[] payload, int dataLength)
        {
            return payload.Length == dataLength + 2 && payload[1] == dataLength;
        }

        private byte[] BuildModuleQueryPayload()
        {
            var data = new byte[16];
            data[0] = Profile.ModuleType;
            data[10] = (byte)((Profile.SupportsTenMilliAmp ? 0x02 : 0x00) |
                              (Profile.SupportsPulseGuard ? 0x04 : 0x00));
            data[12] = Profile.IpdUs;
            data[13] = Profile.PaStep;
            data[14] = Profile.PaLimit;
            data[15] = Profile.PwLimit;

            var payload = new byte[2 + data.Length];
            payload[0] = (byte)WSSMessageIDs.RequestAnalog;
            payload[1] = (byte)data.Length;
            Buffer.BlockCopy(data, 0, payload, 2, data.Length);
            return payload;
        }

        private void RecordError(WssProtocolErrorKind kind, string description, byte[] rawFrame)
        {
            _protocolErrors.Add(new WssProtocolError(++_errorSequence, kind, description, rawFrame));
        }

        private static byte[] AppendEnd(byte[] preEscaped)
        {
            var frame = new byte[preEscaped.Length + 1];
            Buffer.BlockCopy(preEscaped, 0, frame, 0, preEscaped.Length);
            frame[frame.Length - 1] = End;
            return frame;
        }

        private static byte[] Clone(byte[] value)
        {
            var copy = new byte[value.Length];
            Buffer.BlockCopy(value, 0, copy, 0, value.Length);
            return copy;
        }

        private sealed class TargetState
        {
            internal bool Cleared { get; set; }
            internal bool ModuleQueried { get; set; }
            internal HashSet<byte> ScheduleIds { get; } = new HashSet<byte>();
            internal HashSet<byte> ContactConfigurationIds { get; } = new HashSet<byte>();
            internal HashSet<byte> EventIds { get; } = new HashSet<byte>();
            internal Dictionary<byte, byte> EventContactConfigurationIds { get; } = new Dictionary<byte, byte>();
            internal HashSet<byte> EventRatioIds { get; } = new HashSet<byte>();
            internal Dictionary<byte, byte> EventScheduleIds { get; } = new Dictionary<byte, byte>();
            internal Dictionary<byte, int> ScheduleDurations { get; } = new Dictionary<byte, int>();
            internal Dictionary<byte, EventState> EventStates { get; } = new Dictionary<byte, EventState>();
            internal bool SynchronizationConfigured { get; set; }
            internal byte SyncSignal { get; set; }
            internal bool StimulationStarted { get; set; }
            internal bool StreamingObserved { get; set; }

            internal void Reset()
            {
                Cleared = false;
                ModuleQueried = false;
                ScheduleIds.Clear();
                ContactConfigurationIds.Clear();
                EventIds.Clear();
                EventContactConfigurationIds.Clear();
                EventRatioIds.Clear();
                EventScheduleIds.Clear();
                ScheduleDurations.Clear();
                EventStates.Clear();
                SynchronizationConfigured = false;
                SyncSignal = 0;
                StimulationStarted = false;
                StreamingObserved = false;
            }

            internal EventState GetEventState(byte eventId)
            {
                if (!EventStates.TryGetValue(eventId, out var state))
                {
                    state = new EventState();
                    EventStates[eventId] = state;
                }

                return state;
            }
        }

        private sealed class EventState
        {
            internal int PulseAmplitude { get; set; }
            internal int PulseWidth { get; set; }
            internal int InterPulseInterval { get; set; }
            internal byte LastMessageId { get; set; }

            internal WssStimulationObservation ToObservation(
                long sequenceNumber,
                byte target,
                int channel,
                byte messageId)
            {
                return new WssStimulationObservation(
                    sequenceNumber,
                    target,
                    channel,
                    messageId,
                    PulseAmplitude,
                    PulseWidth,
                    InterPulseInterval);
            }
        }
    }
}
