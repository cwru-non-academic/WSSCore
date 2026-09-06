using System;
using System.Collections.Generic;
using System.Linq;
using Wss.CoreModule;

namespace Wss.Testing
{
    /// <summary>
    /// Provides read-only access to observations recorded by one emulated WSS device.
    /// </summary>
    public sealed class WssConformance : IWssConformance
    {
        private readonly EmulatedWssDevice _device;

        /// <summary>
        /// Creates a conformance view associated with an emulated device.
        /// </summary>
        /// <param name="device">Device whose observations are exposed.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="device"/> is null.</exception>
        public WssConformance(EmulatedWssDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
        }

        /// <inheritdoc/>
        public IReadOnlyList<WssMessageObservation> MessageHistory => _device.GetMessageHistorySnapshot();

        /// <inheritdoc/>
        public IReadOnlyList<WssProtocolError> ProtocolErrors => _device.GetProtocolErrorsSnapshot();

        /// <inheritdoc/>
        public IReadOnlyList<WssStimulationObservation> StimulationHistory => _device.GetStimulationHistorySnapshot();

        /// <inheritdoc/>
        public InitializationConformanceResult ValidateInitialization()
        {
            var transitions = _device.GetConfigurationTransitionsSnapshot();
            var protocolErrors = _device.GetProtocolErrorsSnapshot();
            var checks = new List<InitializationConformanceCheck>();

            foreach (var targetTransitions in transitions.GroupBy(item => item.Message.Target))
                ValidateTarget(targetTransitions.Key, targetTransitions.ToArray(), checks);

            return new InitializationConformanceResult(checks, protocolErrors);
        }

        /// <inheritdoc/>
        public WssStimulationBaseline CaptureStimulationBaseline()
            => _device.CaptureStimulationBaseline();

        /// <inheritdoc/>
        public StimulationConformanceResult ValidateStimulation(
            WssStimulationExpectation expectation,
            WssStimulationBaseline baseline)
        {
            if (expectation == null) throw new ArgumentNullException(nameof(expectation));
            if (baseline == null) throw new ArgumentNullException(nameof(baseline));

            var history = _device.GetStimulationHistorySnapshot();
            var candidate = history.LastOrDefault(item =>
                item.SequenceNumber > baseline.SequenceNumber &&
                item.Target == expectation.Target &&
                item.Channel == expectation.Channel &&
                item.MessageId == expectation.MessageId);
            if (candidate == null)
            {
                candidate = history.LastOrDefault(item =>
                    item.SequenceNumber > baseline.SequenceNumber &&
                    item.Channel == expectation.Channel &&
                    item.MessageId == expectation.MessageId);
            }
            if (candidate == null)
            {
                candidate = history.LastOrDefault(item =>
                    item.SequenceNumber > baseline.SequenceNumber &&
                    item.Target == expectation.Target &&
                    item.Channel == expectation.Channel);
            }

            var checks = new List<StimulationConformanceCheck>();
            if (candidate == null)
            {
                AddCheck(checks, "Observation", false, "post-baseline stream message", "none", expectation.Target, null);
            }
            else
            {
                AddCheck(checks, "Target", candidate.Target == expectation.Target,
                    $"0x{expectation.Target:X2}", $"0x{candidate.Target:X2}", expectation.Target, candidate.SequenceNumber);
                AddCheck(checks, "Channel", candidate.Channel == expectation.Channel,
                    expectation.Channel.ToString(), candidate.Channel.ToString(), expectation.Target, candidate.SequenceNumber);
                AddCheck(checks, "MessageId", candidate.MessageId == expectation.MessageId,
                    $"0x{expectation.MessageId:X2}", $"0x{candidate.MessageId:X2}", expectation.Target, candidate.SequenceNumber);
                AddCheck(checks, "PulseAmplitude", candidate.PulseAmplitude == expectation.PulseAmplitude,
                    expectation.PulseAmplitude.ToString(), candidate.PulseAmplitude.ToString(), expectation.Target, candidate.SequenceNumber);
                AddCheck(checks, "PulseWidth", candidate.PulseWidth == expectation.PulseWidth,
                    expectation.PulseWidth.ToString(), candidate.PulseWidth.ToString(), expectation.Target, candidate.SequenceNumber);
                AddCheck(checks, "IPI", candidate.InterPulseInterval == expectation.InterPulseInterval,
                    expectation.InterPulseInterval.ToString(), candidate.InterPulseInterval.ToString(), expectation.Target, candidate.SequenceNumber);

                if (expectation.RequireUnrelatedChannelsUnchanged)
                    AddUnrelatedChannelChecks(checks, history, baseline, expectation, candidate);
            }

            var failures = checks.Where(check => !check.Passed).Select(check => check.Details).ToList();
            failures.AddRange(_device.GetProtocolErrorsSnapshot()
                .Where(error => error.SequenceNumber > baseline.ProtocolErrorSequence)
                .Select(error => $"Protocol error #{error.SequenceNumber}: {error.Kind}: {error.Description}"));

            return new StimulationConformanceResult(expectation, candidate, checks, failures);
        }

        private static void AddUnrelatedChannelChecks(
            ICollection<StimulationConformanceCheck> checks,
            IReadOnlyList<WssStimulationObservation> history,
            WssStimulationBaseline baseline,
            WssStimulationExpectation expectation,
            WssStimulationObservation candidate)
        {
            for (int channel = 1; channel <= 3; channel++)
            {
                if (channel == expectation.Channel)
                    continue;

                var observed = history.LastOrDefault(item =>
                    item.SequenceNumber == candidate.SequenceNumber &&
                    item.Target == expectation.Target &&
                    item.Channel == channel);
                if (!baseline.TryGetState(expectation.Target, channel, out var expected) || observed == null)
                {
                    AddCheck(checks, $"Channel{channel}Unchanged", false,
                        "baseline state", observed == null ? "no same-frame state" : "no baseline state",
                        expectation.Target, candidate.SequenceNumber);
                    continue;
                }

                AddCheck(checks, $"Channel{channel}.PulseAmplitude",
                    observed.PulseAmplitude == expected.PulseAmplitude,
                    expected.PulseAmplitude.ToString(), observed.PulseAmplitude.ToString(),
                    expectation.Target, candidate.SequenceNumber);
                AddCheck(checks, $"Channel{channel}.PulseWidth",
                    observed.PulseWidth == expected.PulseWidth,
                    expected.PulseWidth.ToString(), observed.PulseWidth.ToString(),
                    expectation.Target, candidate.SequenceNumber);
                AddCheck(checks, $"Channel{channel}.IPI",
                    observed.InterPulseInterval == expected.InterPulseInterval,
                    expected.InterPulseInterval.ToString(), observed.InterPulseInterval.ToString(),
                    expectation.Target, candidate.SequenceNumber);
            }
        }

        private static void AddCheck(
            ICollection<StimulationConformanceCheck> checks,
            string name,
            bool passed,
            string expected,
            string observed,
            byte target,
            long? sequenceNumber)
        {
            string details =
                $"{name}: {(passed ? "PASS" : "FAIL")}\n" +
                $"Target: 0x{target:X2}\n" +
                $"Expected: {expected}\n" +
                $"Observed: {observed}\n" +
                $"Observed sequence: {(sequenceNumber.HasValue ? "#" + sequenceNumber.Value : "none")}";
            checks.Add(new StimulationConformanceCheck(name, passed, expected, observed, details));
        }

        private void ValidateTarget(
            byte target,
            IReadOnlyList<WssConfigurationTransition> allTransitions,
            ICollection<InitializationConformanceCheck> checks)
        {
            int lastReset = -1;
            for (int i = 0; i < allTransitions.Count; i++)
            {
                if (allTransitions[i].Message.MessageId == (byte)WSSMessageIDs.Reset)
                    lastReset = i;
            }

            if (lastReset >= 0)
            {
                var reset = allTransitions[lastReset].Message;
                AddFinding(checks, "ResetDuringConfiguration", WssConformanceSeverity.Error, target,
                    reset.SequenceNumber, "Reset invalidated all configuration and lifecycle observations that preceded it.");
            }

            var transitions = allTransitions.Skip(lastReset + 1).ToArray();
            if (transitions.Length == 0)
                return;

            bool conformingStartObserved = false;
            foreach (var transition in transitions)
                ValidateTransition(target, transition, checks, ref conformingStartObserved);

            var finalState = transitions[transitions.Length - 1].After;
            if (!finalState.ContactsKnown || !finalState.EventsKnown || !finalState.SchedulesKnown)
            {
                AddFinding(checks, "UnknownBaseline", WssConformanceSeverity.Warning, target, null,
                    "Clear(All) was not observed in the current epoch; unobserved resources may predate this session.");
            }

            AddFinalStateFindings(target, finalState, checks);
        }

        private void ValidateTransition(
            byte target,
            WssConfigurationTransition transition,
            ICollection<InitializationConformanceCheck> checks,
            ref bool conformingStartObserved)
        {
            var message = transition.Message;
            var payload = message.Payload;
            var before = transition.Before;

            switch ((WSSMessageIDs)message.MessageId)
            {
                case WSSMessageIDs.Clear:
                    conformingStartObserved = false;
                    if (HasData(payload, 1) && payload[2] == 0x00)
                    {
                        AddFinding(checks, "KnownCleanBaseline", WssConformanceSeverity.Info, target,
                            message.SequenceNumber, "Clear(All) established a known empty resource baseline.");
                    }
                    break;

                case WSSMessageIDs.CreateEvent:
                    if (!IsCreateEventPayload(payload))
                        break;
                    byte eventId = payload[2];
                    byte contactId = payload[4];
                    CheckResource(checks, "EventContactExists", "CreateEvent", "ContactConfig", contactId,
                        before.ContactsKnown, before.Contacts.Contains(contactId), target, message.SequenceNumber);
                    if (_device.Profile.SupportsModuleQuery && IsKnownFreshState(before) && !before.ModuleQueried)
                    {
                        AddFinding(checks, "ModuleQueryBeforeEventCreation", WssConformanceSeverity.Error,
                            target, message.SequenceNumber,
                            $"CreateEvent {eventId} requires ModuleQuery(settings) first on ModuleQuery-capable firmware after Clear(All).");
                    }
                    break;

                case WSSMessageIDs.AddEventToSchedule:
                    if (!HasData(payload, 2))
                        break;
                    CheckResource(checks, "AssignmentEventExists", "AddEventToSchedule", "Event", payload[2],
                        before.EventsKnown, before.Events.Contains(payload[2]), target, message.SequenceNumber);
                    CheckResource(checks, "AssignmentScheduleExists", "AddEventToSchedule", "Schedule", payload[3],
                        before.SchedulesKnown, before.Schedules.Contains(payload[3]), target, message.SequenceNumber);
                    break;

                case WSSMessageIDs.EditEventConfig:
                    if (payload.Length < 4)
                        break;
                    CheckResource(checks, "EditedEventExists", "EditEventConfig", "Event", payload[2],
                        before.EventsKnown, before.Events.Contains(payload[2]), target, message.SequenceNumber);
                    if (HasData(payload, 3) && payload[3] == 0x01)
                    {
                        CheckResource(checks, "EditedContactExists", "EditEventContactConfig", "ContactConfig", payload[4],
                            before.ContactsKnown, before.Contacts.Contains(payload[4]), target, message.SequenceNumber);
                    }
                    break;

                case WSSMessageIDs.SyncGroup:
                    if (!HasData(payload, 1))
                        break;
                    bool matchingSchedule = before.ScheduleSyncSignals.Any(item =>
                        item.Value == payload[2] && before.Schedules.Contains(item.Key));
                    if (matchingSchedule)
                    {
                        AddFinding(checks, "SynchronizationMatchesSchedule", WssConformanceSeverity.Info,
                            target, message.SequenceNumber,
                            $"SyncGroup({payload[2]}) matches at least one known schedule.");
                    }
                    else
                    {
                        var severity = before.SchedulesKnown
                            ? WssConformanceSeverity.Error
                            : WssConformanceSeverity.NotVerifiable;
                        AddFinding(checks, "SynchronizationMatchesSchedule", severity, target,
                            message.SequenceNumber,
                            $"SyncGroup({payload[2]}) has no observed matching schedule; schedule state is " +
                            (before.SchedulesKnown ? "known." : "pre-existing or unknown."));
                    }
                    break;

                case WSSMessageIDs.StimulationSwitch:
                    if (HasData(payload, 1) && payload[2] == 0x03)
                        conformingStartObserved = ValidateStart(target, message.SequenceNumber, before, checks);
                    else if (HasData(payload, 1) && payload[2] == 0x04)
                        conformingStartObserved = false;
                    break;

                case WSSMessageIDs.StreamChangeAll:
                case WSSMessageIDs.StreamChangeNoIPI:
                case WSSMessageIDs.StreamChangeNoPW:
                case WSSMessageIDs.StreamChangeNoPA:
                    if (HasData(payload, 9) && !conformingStartObserved)
                    {
                        AddFinding(checks, "StimulationStartedBeforeStreaming", WssConformanceSeverity.Error,
                            target, message.SequenceNumber, "A stimulation stream message was observed before StartStim.");
                    }
                    break;

                case WSSMessageIDs.DeleteContactConfig:
                    if (!HasData(payload, 1))
                        break;
                    byte deletedContact = payload[2];
                    CheckResource(checks, "DeletedContactExists", "DeleteContactConfig", "ContactConfig", deletedContact,
                        before.ContactsKnown, before.Contacts.Contains(deletedContact), target, message.SequenceNumber);
                    foreach (var reference in before.EventContacts.Where(item =>
                        item.Value == deletedContact && before.Events.Contains(item.Key)))
                    {
                        AddFinding(checks, "DeleteReferencedContact", WssConformanceSeverity.Error, target,
                            message.SequenceNumber,
                            $"DeleteContactConfig {deletedContact} would leave Event {reference.Key} referencing the deleted contact configuration.");
                    }
                    break;

                case WSSMessageIDs.DeleteEvent:
                    if (HasData(payload, 1))
                        CheckResource(checks, "DeletedEventExists", "DeleteEvent", "Event", payload[2],
                            before.EventsKnown, before.Events.Contains(payload[2]), target, message.SequenceNumber);
                    break;

                case WSSMessageIDs.RemoveEventFromSchedule:
                    if (HasData(payload, 1))
                    {
                        CheckResource(checks, "UnassignedEventExists", "DeleteEventFromSchedule", "Event", payload[2],
                            before.EventsKnown, before.Events.Contains(payload[2]), target, message.SequenceNumber);
                    }
                    break;

                case WSSMessageIDs.MoveEventToSchedule:
                    if (!HasData(payload, 3))
                        break;
                    CheckResource(checks, "MovedEventExists", "MoveEventToSchedule", "Event", payload[2],
                        before.EventsKnown, before.Events.Contains(payload[2]), target, message.SequenceNumber);
                    CheckResource(checks, "MoveDestinationScheduleExists", "MoveEventToSchedule", "Schedule", payload[3],
                        before.SchedulesKnown, before.Schedules.Contains(payload[3]), target, message.SequenceNumber);
                    break;

                case WSSMessageIDs.DeleteSchedule:
                    if (HasData(payload, 1))
                        CheckResource(checks, "DeletedScheduleExists", "DeleteSchedule", "Schedule", payload[2],
                            before.SchedulesKnown, before.Schedules.Contains(payload[2]), target, message.SequenceNumber);
                    break;

                case WSSMessageIDs.ChangeScheduleConfig:
                    if (HasData(payload, 3))
                        CheckResource(checks, "EditedScheduleExists", "ChangeScheduleConfig", "Schedule", payload[3],
                            before.SchedulesKnown, before.Schedules.Contains(payload[3]), target, message.SequenceNumber);
                    break;
            }
        }

        private static bool ValidateStart(
            byte target,
            long sequenceNumber,
            WssConfigurationSnapshot state,
            ICollection<InitializationConformanceCheck> checks)
        {
            bool runnable = state.EventSchedules.Any(assignment =>
                state.Events.Contains(assignment.Key) &&
                state.Schedules.Contains(assignment.Value) &&
                state.EventContacts.TryGetValue(assignment.Key, out byte contactId) &&
                state.Contacts.Contains(contactId) &&
                state.SynchronizedSchedules.Contains(assignment.Value));

            if (runnable)
            {
                AddFinding(checks, "RunnableConfigurationBeforeStart", WssConformanceSeverity.Info,
                    target, sequenceNumber, "StartStim has at least one synchronized runnable configuration chain.");
                return true;
            }

            bool known = state.ContactsKnown && state.EventsKnown && state.SchedulesKnown && state.AssignmentsKnown;
            AddFinding(checks, "RunnableConfigurationBeforeStart",
                known ? WssConformanceSeverity.Error : WssConformanceSeverity.NotVerifiable,
                target, sequenceNumber,
                known
                    ? "StartStim has no complete ContactConfig -> Event -> Schedule assignment -> synchronized schedule chain."
                    : "A runnable chain cannot be verified because one or more resource categories may predate this session.");
            return false;
        }

        private static void AddFinalStateFindings(
            byte target,
            WssConfigurationSnapshot state,
            ICollection<InitializationConformanceCheck> checks)
        {
            foreach (byte eventId in state.Events)
            {
                if (!state.EventSchedules.ContainsKey(eventId))
                {
                    AddFinding(checks, "UnassignedEvent", WssConformanceSeverity.Warning, target, null,
                        $"Event {eventId} exists but is not assigned to any schedule.");
                }

                if (state.EventContacts.TryGetValue(eventId, out byte contactId) &&
                    state.ContactsKnown && !state.Contacts.Contains(contactId))
                {
                    AddFinding(checks, "DanglingEventContact", WssConformanceSeverity.Error, target, null,
                        $"Event {eventId} references missing ContactConfig {contactId}.");
                }
            }

            var usedContacts = new HashSet<byte>(state.EventContacts
                .Where(item => state.Events.Contains(item.Key))
                .Select(item => item.Value));
            foreach (byte contactId in state.Contacts.Where(id => !usedContacts.Contains(id)))
            {
                AddFinding(checks, "UnusedContactConfig", WssConformanceSeverity.Warning, target, null,
                    $"ContactConfig {contactId} exists but is not referenced by any event.");
            }

            var usedSchedules = new HashSet<byte>(state.EventSchedules
                .Where(item => state.Events.Contains(item.Key))
                .Select(item => item.Value));
            foreach (byte scheduleId in state.Schedules.Where(id => !usedSchedules.Contains(id)))
            {
                AddFinding(checks, "EmptySchedule", WssConformanceSeverity.Warning, target, null,
                    $"Schedule {scheduleId} exists but has no assigned event.");
            }
        }

        private static void CheckResource(
            ICollection<InitializationConformanceCheck> checks,
            string name,
            string operation,
            string resourceType,
            byte resourceId,
            bool categoryKnown,
            bool exists,
            byte target,
            long sequenceNumber)
        {
            if (exists)
                return;

            AddFinding(checks, name,
                categoryKnown ? WssConformanceSeverity.Error : WssConformanceSeverity.NotVerifiable,
                target,
                sequenceNumber,
                categoryKnown
                    ? $"{operation} references missing {resourceType} {resourceId}."
                    : $"{operation} references {resourceType} {resourceId}, whose existence cannot be verified from this session.");
        }

        private static bool IsKnownFreshState(WssConfigurationSnapshot state)
            => state.ContactsKnown && state.EventsKnown && state.SchedulesKnown && state.AssignmentsKnown;

        private static bool IsCreateEventPayload(byte[] payload)
        {
            int dataLength = payload.Length >= 2 ? payload[1] : -1;
            return HasData(payload, dataLength) &&
                   (dataLength == 3 || dataLength == 5 || dataLength == 14 ||
                    dataLength == 16 || dataLength == 17 || dataLength == 19);
        }

        private static bool HasData(byte[] payload, int dataLength)
            => payload != null && payload.Length == dataLength + 2 && payload.Length >= 2 && payload[1] == dataLength;

        private static void AddFinding(
            ICollection<InitializationConformanceCheck> checks,
            string name,
            WssConformanceSeverity severity,
            byte target,
            long? sequenceNumber,
            string message)
        {
            string details = $"{severity.ToString().ToUpperInvariant()}: {message}\nTarget: 0x{target:X2}";
            if (sequenceNumber.HasValue)
                details += $"\nObserved sequence: #{sequenceNumber.Value}";
            checks.Add(new InitializationConformanceCheck(name, severity, details, target, sequenceNumber));
        }
    }
}
