using System;

namespace Wss.CoreModule
{
    /// <summary>
    /// Advanced event and schedule programming interface for custom WSS configuration.
    /// All calls are non-blocking; implementations enqueue device commands and return immediately.
    /// </summary>
    /// <remarks>
    /// Each method returns as soon as the commands are enqueued in the setup runner.
    /// Protocol or device failures that occur while executing the queued commands are
    /// logged or surfaced by the setup runner; they are not returned synchronously to
    /// the caller.
    /// </remarks>
    public interface IAdvancedEventProgrammer
    {
        /// <summary>
        /// Creates a new event with the given configuration. Enqueues the command and returns immediately.
        /// </summary>
        /// <remarks>
        /// All numeric IDs in <paramref name="request"/> are protocol byte values (0&#x2013;255).
        /// When amplitude arrays are supplied they must each contain exactly four raw protocol
        /// amplitude values (0&#x2013;255 per element). Pulse-width fields in
        /// <see cref="CreateEventRequest.PulseWidths"/> are in microseconds and share the same
        /// range as <see cref="UpdateEventPulseWidths"/>.
        /// </remarks>
        /// <param name="request">Event creation request. Must not be null.</param>
        /// <param name="wssTarget">Target device or broadcast.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null.</exception>
        void CreateEvent(CreateEventRequest request, WssTarget wssTarget = WssTarget.Broadcast);

        /// <summary>Deletes the event with the specified ID. Enqueues the command and returns immediately.</summary>
        /// <param name="eventID">Event slot to delete.</param>
        /// <param name="wssTarget">Target device or broadcast.</param>
        void DeleteEvent(int eventID, WssTarget wssTarget = WssTarget.Broadcast);

        /// <summary>
        /// Updates the pulse widths for the specified event. Enqueues the command and returns immediately.
        /// Standard and recharge pulse widths are in microseconds (0&#x2013;5400 &#xB5;s).
        /// Inter-phase delay is in microseconds (0&#x2013;1000 &#xB5;s).
        /// </summary>
        /// <param name="eventID">Event slot to modify.</param>
        /// <param name="standardPw">Standard phase pulse width in microseconds (0&#x2013;5400).</param>
        /// <param name="rechargePw">Recharge phase pulse width in microseconds (0&#x2013;5400).</param>
        /// <param name="ipd">Inter-phase delay in microseconds (0&#x2013;1000).</param>
        /// <param name="wssTarget">Target device or broadcast.</param>
        void UpdateEventPulseWidths(int eventID, int standardPw, int rechargePw, int ipd, WssTarget wssTarget = WssTarget.Broadcast);

        /// <summary>
        /// Updates the standard and recharge amplitudes for the specified event.
        /// Enqueues the command and returns immediately.
        /// Each array must contain exactly four raw protocol amplitude values (0&#x2013;255 per element).
        /// </summary>
        /// <param name="eventID">Event slot to modify.</param>
        /// <param name="standardAmplitudes">Standard phase amplitude values; must be exactly 4 elements, each 0&#x2013;255.</param>
        /// <param name="rechargeAmplitudes">Recharge phase amplitude values; must be exactly 4 elements, each 0&#x2013;255.</param>
        /// <param name="wssTarget">Target device or broadcast.</param>
        void UpdateEventAmplitudes(int eventID, int[] standardAmplitudes, int[] rechargeAmplitudes, WssTarget wssTarget = WssTarget.Broadcast);

        /// <summary>
        /// Creates a new schedule with the given definition. Enqueues the command and returns immediately.
        /// The <see cref="ScheduleDefinition.DurationMs"/> field is in milliseconds; <c>CreateSchedule</c>
        /// supports a wider duration range than <see cref="UpdateScheduleDuration"/>, which is limited to 0&#x2013;255 ms.
        /// </summary>
        /// <param name="definition">Schedule definition. Must not be null.</param>
        /// <param name="wssTarget">Target device or broadcast.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="definition"/> is null.</exception>
        void CreateSchedule(ScheduleDefinition definition, WssTarget wssTarget = WssTarget.Broadcast);

        /// <summary>Deletes the schedule with the specified ID. Enqueues the command and returns immediately.</summary>
        /// <param name="scheduleID">Schedule slot to delete.</param>
        /// <param name="wssTarget">Target device or broadcast.</param>
        void DeleteSchedule(int scheduleID, WssTarget wssTarget = WssTarget.Broadcast);

        /// <summary>
        /// Updates the duration of the specified schedule. Enqueues the command and returns immediately.
        /// This edit command supports durations of 0&#x2013;255 ms only; recreate the schedule to use a wider range.
        /// </summary>
        /// <param name="scheduleID">Schedule slot to modify.</param>
        /// <param name="durationMs">Duration in milliseconds (0&#x2013;255).</param>
        /// <param name="wssTarget">Target device or broadcast.</param>
        void UpdateScheduleDuration(int scheduleID, int durationMs, WssTarget wssTarget = WssTarget.Broadcast);

        /// <summary>Adds the specified event to a schedule. Enqueues the command and returns immediately.</summary>
        /// <param name="eventID">Event to add.</param>
        /// <param name="scheduleID">Target schedule slot.</param>
        /// <param name="wssTarget">Target device or broadcast.</param>
        void AddEventToSchedule(int eventID, int scheduleID, WssTarget wssTarget = WssTarget.Broadcast);

        /// <summary>Removes the specified event from its current schedule. Enqueues the command and returns immediately.</summary>
        /// <param name="eventID">Event to remove.</param>
        /// <param name="wssTarget">Target device or broadcast.</param>
        void RemoveEventFromSchedule(int eventID, WssTarget wssTarget = WssTarget.Broadcast);

        /// <summary>
        /// Moves the specified event to a different schedule with a new start delay.
        /// Enqueues the command and returns immediately.
        /// Valid delay range is 0&#x2013;255 ms.
        /// </summary>
        /// <param name="eventID">Event to move.</param>
        /// <param name="scheduleID">Target schedule slot.</param>
        /// <param name="delayMs">Start delay within the schedule in milliseconds (0&#x2013;255).</param>
        /// <param name="wssTarget">Target device or broadcast.</param>
        void MoveEventToSchedule(int eventID, int scheduleID, int delayMs, WssTarget wssTarget = WssTarget.Broadcast);

        /// <summary>Assigns a schedule to a sync group signal. Enqueues the command and returns immediately.</summary>
        /// <param name="scheduleID">Schedule to assign.</param>
        /// <param name="syncSignal">Sync signal identifier.</param>
        /// <param name="wssTarget">Target device or broadcast.</param>
        void SetScheduleGroup(int scheduleID, int syncSignal, WssTarget wssTarget = WssTarget.Broadcast);

        /// <summary>
        /// Sets the state of a schedule. Valid states are <see cref="ScheduleState.Active"/>,
        /// <see cref="ScheduleState.Ready"/>, and <see cref="ScheduleState.Suspend"/>.
        /// Enqueues the command and returns immediately.
        /// </summary>
        /// <param name="scheduleID">Schedule slot to modify.</param>
        /// <param name="state">Target schedule state.</param>
        /// <param name="wssTarget">Target device or broadcast.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="state"/> is not <see cref="ScheduleState.Active"/>,
        /// <see cref="ScheduleState.Ready"/>, or <see cref="ScheduleState.Suspend"/>.
        /// </exception>
        void SetScheduleState(int scheduleID, ScheduleState state, WssTarget wssTarget = WssTarget.Broadcast);

        /// <summary>
        /// Sets the state of all schedules in a sync group. Valid states are
        /// <see cref="ScheduleState.Active"/>, <see cref="ScheduleState.Ready"/>, and <see cref="ScheduleState.Suspend"/>.
        /// Enqueues the command and returns immediately.
        /// </summary>
        /// <param name="syncSignal">Sync signal identifier.</param>
        /// <param name="state">Target schedule state.</param>
        /// <param name="wssTarget">Target device or broadcast.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="state"/> is not <see cref="ScheduleState.Active"/>,
        /// <see cref="ScheduleState.Ready"/>, or <see cref="ScheduleState.Suspend"/>.
        /// </exception>
        void SetGroupState(int syncSignal, ScheduleState state, WssTarget wssTarget = WssTarget.Broadcast);

        /// <summary>Synchronizes all schedules in the specified sync group. Enqueues the command and returns immediately.</summary>
        /// <param name="syncSignal">Sync signal identifier.</param>
        /// <param name="wssTarget">Target device or broadcast.</param>
        void SyncGroup(int syncSignal, WssTarget wssTarget = WssTarget.Broadcast);
    }
}
