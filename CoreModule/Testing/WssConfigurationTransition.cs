using System.Collections.Generic;

namespace Wss.Testing
{
    internal sealed class WssConfigurationTransition
    {
        internal WssConfigurationTransition(
            WssMessageObservation message,
            WssConfigurationSnapshot before,
            WssConfigurationSnapshot after)
        {
            Message = message;
            Before = before;
            After = after;
        }

        internal WssMessageObservation Message { get; }
        internal WssConfigurationSnapshot Before { get; }
        internal WssConfigurationSnapshot After { get; }
    }

    internal sealed class WssConfigurationSnapshot
    {
        internal WssConfigurationSnapshot(
            bool contactsKnown,
            bool eventsKnown,
            bool schedulesKnown,
            bool assignmentsKnown,
            bool moduleQueried,
            bool stimulationStarted,
            bool streamingObserved,
            IEnumerable<byte> contacts,
            IEnumerable<byte> events,
            IEnumerable<byte> schedules,
            IDictionary<byte, byte> eventContacts,
            IDictionary<byte, byte> eventSchedules,
            IDictionary<byte, byte> scheduleSyncSignals,
            IEnumerable<byte> synchronizedSchedules,
            IEnumerable<byte> observedSyncSignals)
        {
            ContactsKnown = contactsKnown;
            EventsKnown = eventsKnown;
            SchedulesKnown = schedulesKnown;
            AssignmentsKnown = assignmentsKnown;
            ModuleQueried = moduleQueried;
            StimulationStarted = stimulationStarted;
            StreamingObserved = streamingObserved;
            Contacts = new HashSet<byte>(contacts);
            Events = new HashSet<byte>(events);
            Schedules = new HashSet<byte>(schedules);
            EventContacts = new Dictionary<byte, byte>(eventContacts);
            EventSchedules = new Dictionary<byte, byte>(eventSchedules);
            ScheduleSyncSignals = new Dictionary<byte, byte>(scheduleSyncSignals);
            SynchronizedSchedules = new HashSet<byte>(synchronizedSchedules);
            ObservedSyncSignals = new HashSet<byte>(observedSyncSignals);
        }

        internal bool ContactsKnown { get; }
        internal bool EventsKnown { get; }
        internal bool SchedulesKnown { get; }
        internal bool AssignmentsKnown { get; }
        internal bool ModuleQueried { get; }
        internal bool StimulationStarted { get; }
        internal bool StreamingObserved { get; }
        internal HashSet<byte> Contacts { get; }
        internal HashSet<byte> Events { get; }
        internal HashSet<byte> Schedules { get; }
        internal Dictionary<byte, byte> EventContacts { get; }
        internal Dictionary<byte, byte> EventSchedules { get; }
        internal Dictionary<byte, byte> ScheduleSyncSignals { get; }
        internal HashSet<byte> SynchronizedSchedules { get; }
        internal HashSet<byte> ObservedSyncSignals { get; }
    }
}
