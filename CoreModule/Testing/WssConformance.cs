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
        public InitializationConformanceResult ValidateInitialization()
        {
            var history = _device.GetMessageHistorySnapshot();
            var protocolErrors = _device.GetProtocolErrorsSnapshot();
            byte target = _device.Profile.InitializationTarget;
            var requiredChecks = BuildRequiredMessageChecks(history, target);
            var orderingChecks = BuildOrderingChecks(history, target);
            var failures = new List<string>();

            failures.AddRange(requiredChecks.Where(check => !check.Passed).Select(check => check.Details));
            failures.AddRange(orderingChecks.Where(check => !check.Passed).Select(check => check.Details));
            failures.AddRange(protocolErrors.Select(error =>
                $"Protocol error #{error.SequenceNumber}: {error.Kind}: {error.Description}"));

            return new InitializationConformanceResult(requiredChecks, orderingChecks, protocolErrors, failures);
        }

        private static IReadOnlyList<InitializationConformanceCheck> BuildRequiredMessageChecks(
            IReadOnlyList<WssMessageObservation> history,
            byte target)
        {
            var checks = new List<InitializationConformanceCheck>();
            AddExactCheck(checks, history, target, "ClearAll", "Clear(All)", WSSMessageIDs.Clear,
                item => IsData(item, WSSMessageIDs.Clear, 0x00));
            AddExactCheck(checks, history, target, "ModuleQuerySettings", "ModuleQuery(settings)", WSSMessageIDs.ModuleQuery,
                item => IsData(item, WSSMessageIDs.ModuleQuery, 0x01));
            AddIdSetCheck(checks, history, target, "CreateSchedules", "schedule", WSSMessageIDs.CreateSchedule,
                item => HasDataLength(item, WSSMessageIDs.CreateSchedule, 4), 2);
            AddIdSetCheck(checks, history, target, "CreateContactConfigurations", "contact configuration", WSSMessageIDs.CreateContactConfig,
                item => HasDataLength(item, WSSMessageIDs.CreateContactConfig, 4), 2);
            AddIdSetCheck(checks, history, target, "CreateEvents", "event", WSSMessageIDs.CreateEvent,
                item => HasDataLength(item, WSSMessageIDs.CreateEvent, 16), 2);
            AddEventContactCheck(checks, history, target);
            AddIdSetCheck(checks, history, target, "ConfigureEventRatios", "event ratio", WSSMessageIDs.EditEventConfig,
                item => HasDataLength(item, WSSMessageIDs.EditEventConfig, 3) && item.Payload[3] == 0x07, 2);
            AddAssignmentCheck(checks, history, target);
            AddExactCheck(checks, history, target, "ConfigureSynchronization", "SyncGroup", WSSMessageIDs.SyncGroup,
                item => HasDataLength(item, WSSMessageIDs.SyncGroup, 1));
            AddExactCheck(checks, history, target, "StartStimulation", "stimulation start", WSSMessageIDs.StimulationSwitch,
                item => IsData(item, WSSMessageIDs.StimulationSwitch, 0x03));

            int totalStreamCount = CountMessages(history, target, WSSMessageIDs.StreamChangeNoIPI);
            int streamCount = history.Count(item => item.Target == target &&
                HasDataLength(item, WSSMessageIDs.StreamChangeNoIPI, 9));
            bool streamPassed = streamCount > 0 && streamCount == totalStreamCount;
            checks.Add(new InitializationConformanceCheck(
                "ObserveStreaming",
                streamPassed,
                streamPassed
                    ? $"Target 0x{target:X2} received {streamCount} StreamChangeNoIPI message(s)."
                    : streamCount == 0
                        ? $"Target 0x{target:X2} did not receive a valid StreamChangeNoIPI after setup."
                        : $"Target 0x{target:X2} received {totalStreamCount - streamCount} malformed StreamChangeNoIPI message(s)."));

            return checks.ToArray();
        }

        private static IReadOnlyList<InitializationConformanceCheck> BuildOrderingChecks(
            IReadOnlyList<WssMessageObservation> history,
            byte target)
        {
            var checks = new List<InitializationConformanceCheck>();
            var clear = First(history, target, item => IsData(item, WSSMessageIDs.Clear, 0x00));
            var moduleQuery = First(history, target, item => IsData(item, WSSMessageIDs.ModuleQuery, 0x01));
            var firstCreation = First(history, target, IsSetupCreation);
            var firstEvent = First(history, target, item => HasDataLength(item, WSSMessageIDs.CreateEvent, 16));
            var sync = First(history, target, item => HasDataLength(item, WSSMessageIDs.SyncGroup, 1));
            var start = First(history, target, item => IsData(item, WSSMessageIDs.StimulationSwitch, 0x03));
            var stream = First(history, target, item => HasDataLength(item, WSSMessageIDs.StreamChangeNoIPI, 9));

            AddBeforeCheck(checks, "ClearBeforeModuleQuery", target, "Clear(All)", clear, "ModuleQuery", moduleQuery);
            AddBeforeCheck(checks, "ClearBeforeSetupCreation", target, "Clear(All)", clear, "setup creation", firstCreation);
            AddBeforeCheck(checks, "ModuleQueryBeforeEventCreation", target, "ModuleQuery", moduleQuery, "CreateEvent", firstEvent);

            for (byte id = 1; id <= 3; id++)
            {
                byte resourceId = id;
                var schedule = First(history, target,
                    item => HasId(item, WSSMessageIDs.CreateSchedule, 4, resourceId));
                var contact = First(history, target,
                    item => HasId(item, WSSMessageIDs.CreateContactConfig, 4, resourceId));
                var createEvent = First(history, target,
                    item => HasId(item, WSSMessageIDs.CreateEvent, 16, resourceId));
                var ratio = First(history, target,
                    item => HasId(item, WSSMessageIDs.EditEventConfig, 3, resourceId) && item.Payload[3] == 0x07);
                var assignment = First(history, target,
                    item => IsAssignment(item, resourceId, resourceId));

                AddBeforeCheck(checks, $"ContactBeforeEvent{id}", target,
                    $"CreateContactConfiguration {id}", contact, $"CreateEvent {id}", createEvent);
                AddBeforeCheck(checks, $"EventBeforeRatio{id}", target,
                    $"CreateEvent {id}", createEvent, $"EditEventRatio {id}", ratio);
                AddBeforeCheck(checks, $"ScheduleBeforeAssignment{id}", target,
                    $"CreateSchedule {id}", schedule, $"AddEventToSchedule {id}", assignment);
                AddBeforeCheck(checks, $"EventBeforeAssignment{id}", target,
                    $"CreateEvent {id}", createEvent, $"AddEventToSchedule {id}", assignment);
            }

            var lastSetupObject = Last(history, target, IsRequiredSetupObject);
            AddBeforeCheck(checks, "SetupObjectsBeforeSynchronization", target,
                "last required setup object", lastSetupObject, "SyncGroup", sync);
            AddBeforeCheck(checks, "SynchronizationBeforeStimulationStart", target,
                "SyncGroup", sync, "stimulation start", start);
            AddBeforeCheck(checks, "StimulationStartBeforeStreaming", target,
                "stimulation start", start, "StreamChangeNoIPI", stream);

            return checks.ToArray();
        }

        private static void AddExactCheck(
            ICollection<InitializationConformanceCheck> checks,
            IReadOnlyList<WssMessageObservation> history,
            byte target,
            string name,
            string operation,
            WSSMessageIDs messageId,
            Func<WssMessageObservation, bool> predicate)
        {
            int totalCount = CountMessages(history, target, messageId);
            int count = history.Count(item => item.Target == target && predicate(item));
            bool passed = totalCount == 1 && count == 1;
            string details = passed
                ? $"Target 0x{target:X2} received exactly one {operation}."
                : totalCount != count
                    ? $"Target 0x{target:X2} received {totalCount - count} malformed {operation} message(s)."
                    : count == 0
                        ? $"Target 0x{target:X2} did not receive {operation}."
                        : $"Target 0x{target:X2} received {operation} {count} times; expected exactly once.";
            checks.Add(new InitializationConformanceCheck(name, passed, details));
        }

        private static void AddIdSetCheck(
            ICollection<InitializationConformanceCheck> checks,
            IReadOnlyList<WssMessageObservation> history,
            byte target,
            string name,
            string operation,
            WSSMessageIDs messageId,
            Func<WssMessageObservation, bool> predicate,
            int idIndex)
        {
            int totalCount = CountMessages(history, target, messageId);
            var counts = history
                .Where(item => item.Target == target && predicate(item))
                .GroupBy(item => item.Payload[idIndex])
                .ToDictionary(group => group.Key, group => group.Count());
            var issues = new List<string>();
            for (byte id = 1; id <= 3; id++)
            {
                counts.TryGetValue(id, out int count);
                if (count == 0) issues.Add($"missing ID {id}");
                else if (count != 1) issues.Add($"ID {id} observed {count} times");
            }

            bool passed = issues.Count == 0 && counts.Count == 3;
            if (counts.Keys.Any(id => id < 1 || id > 3))
                issues.Add("unexpected IDs " + string.Join(", ", counts.Keys.Where(id => id < 1 || id > 3).Select(id => id.ToString())));
            int validCount = counts.Values.Sum();
            if (totalCount != validCount)
                issues.Add($"{totalCount - validCount} malformed message(s)");

            passed = passed && issues.Count == 0;
            checks.Add(new InitializationConformanceCheck(
                name,
                passed,
                passed
                    ? $"Target 0x{target:X2} received {operation} IDs 1, 2, and 3 exactly once."
                    : $"Target 0x{target:X2} {operation} requirements failed: {string.Join("; ", issues)}."));
        }

        private static void AddEventContactCheck(
            ICollection<InitializationConformanceCheck> checks,
            IReadOnlyList<WssMessageObservation> history,
            byte target)
        {
            var issues = new List<string>();
            for (byte id = 1; id <= 3; id++)
            {
                byte eventId = id;
                var matchingEvents = history.Where(item =>
                    item.Target == target && HasId(item, WSSMessageIDs.CreateEvent, 16, eventId)).ToArray();
                if (matchingEvents.Length == 1 && matchingEvents[0].Payload[4] != eventId)
                    issues.Add($"event {eventId} references contact configuration {matchingEvents[0].Payload[4]}");
            }

            bool passed = issues.Count == 0;
            checks.Add(new InitializationConformanceCheck(
                "EventsReferenceContactConfigurations",
                passed,
                passed
                    ? $"Target 0x{target:X2} events 1, 2, and 3 reference matching contact configurations."
                    : $"Target 0x{target:X2} event contact references failed: {string.Join("; ", issues)}."));
        }

        private static void AddAssignmentCheck(
            ICollection<InitializationConformanceCheck> checks,
            IReadOnlyList<WssMessageObservation> history,
            byte target)
        {
            var issues = new List<string>();
            int totalCount = CountMessages(history, target, WSSMessageIDs.AddEventToSchedule);
            var assignments = history.Where(item =>
                item.Target == target && HasDataLength(item, WSSMessageIDs.AddEventToSchedule, 2)).ToArray();
            for (byte id = 1; id <= 3; id++)
            {
                int count = assignments.Count(item => IsAssignment(item, id, id));
                if (count == 0) issues.Add($"missing event {id} to schedule {id}");
                else if (count != 1) issues.Add($"event {id} to schedule {id} observed {count} times");
            }

            int unexpected = assignments.Count(item => item.Payload[2] != item.Payload[3] || item.Payload[2] < 1 || item.Payload[2] > 3);
            if (unexpected > 0) issues.Add($"{unexpected} unexpected assignment(s)");
            if (totalCount != assignments.Length)
                issues.Add($"{totalCount - assignments.Length} malformed assignment message(s)");

            bool passed = issues.Count == 0 && assignments.Length == 3;
            checks.Add(new InitializationConformanceCheck(
                "AssignEventsToSchedules",
                passed,
                passed
                    ? $"Target 0x{target:X2} assigned events 1, 2, and 3 to matching schedules exactly once."
                    : $"Target 0x{target:X2} event assignment requirements failed: {string.Join("; ", issues)}."));
        }

        private static void AddBeforeCheck(
            ICollection<InitializationConformanceCheck> checks,
            string name,
            byte target,
            string earlierName,
            WssMessageObservation earlier,
            string laterName,
            WssMessageObservation later)
        {
            if (earlier == null || later == null)
            {
                checks.Add(new InitializationConformanceCheck(
                    name,
                    true,
                    $"Target 0x{target:X2}: ordering not evaluated because a required operation is missing."));
                return;
            }

            bool passed = earlier.SequenceNumber < later.SequenceNumber;
            checks.Add(new InitializationConformanceCheck(
                name,
                passed,
                passed
                    ? $"{earlierName} target 0x{target:X2} at #{earlier.SequenceNumber} preceded {laterName} at #{later.SequenceNumber}."
                    : $"{earlierName} target 0x{target:X2} observed at #{earlier.SequenceNumber}; {laterName} observed at #{later.SequenceNumber}. Expected {earlierName} before {laterName}."));
        }

        private static WssMessageObservation First(
            IEnumerable<WssMessageObservation> history,
            byte target,
            Func<WssMessageObservation, bool> predicate)
        {
            return history.FirstOrDefault(item => item.Target == target && predicate(item));
        }

        private static WssMessageObservation Last(
            IEnumerable<WssMessageObservation> history,
            byte target,
            Func<WssMessageObservation, bool> predicate)
        {
            return history.LastOrDefault(item => item.Target == target && predicate(item));
        }

        private static bool IsSetupCreation(WssMessageObservation item)
        {
            return HasDataLength(item, WSSMessageIDs.CreateSchedule, 4) ||
                   HasDataLength(item, WSSMessageIDs.CreateContactConfig, 4) ||
                   HasDataLength(item, WSSMessageIDs.CreateEvent, 16);
        }

        private static bool IsRequiredSetupObject(WssMessageObservation item)
        {
            return IsSetupCreation(item) ||
                   (HasDataLength(item, WSSMessageIDs.EditEventConfig, 3) && item.Payload[3] == 0x07) ||
                   HasDataLength(item, WSSMessageIDs.AddEventToSchedule, 2);
        }

        private static bool IsData(WssMessageObservation item, WSSMessageIDs messageId, byte value)
        {
            return HasDataLength(item, messageId, 1) && item.Payload[2] == value;
        }

        private static bool HasId(WssMessageObservation item, WSSMessageIDs messageId, int dataLength, byte id)
        {
            return HasDataLength(item, messageId, dataLength) && item.Payload[2] == id;
        }

        private static bool IsAssignment(WssMessageObservation item, byte eventId, byte scheduleId)
        {
            return HasDataLength(item, WSSMessageIDs.AddEventToSchedule, 2) &&
                   item.Payload[2] == eventId && item.Payload[3] == scheduleId;
        }

        private static bool HasDataLength(WssMessageObservation item, WSSMessageIDs messageId, int dataLength)
        {
            if (item.MessageId != (byte)messageId)
                return false;

            var payload = item.Payload;
            return payload.Length == dataLength + 2 && payload[1] == dataLength;
        }

        private static int CountMessages(
            IEnumerable<WssMessageObservation> history,
            byte target,
            WSSMessageIDs messageId)
        {
            return history.Count(item => item.Target == target && item.MessageId == (byte)messageId);
        }
    }
}
