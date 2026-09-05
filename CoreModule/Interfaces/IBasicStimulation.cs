using System;

namespace Wss.CoreModule
{
    /// <summary>
    /// Basic stimulation operations that upload or select waveforms and manage
    /// board configuration. Calls are non-blocking; implementations may enqueue
    /// setup steps and return immediately.
    /// </summary>
    public interface IBasicStimulation : IDisposable
    {
        /// <summary>
        /// Sets event shape IDs directly for the given <paramref name="eventID"/>.
        /// Implementations should send setup edits with replies.
        /// </summary>
        /// <param name="cathodicWaveform">Shape ID for the standard phase.</param>
        /// <param name="anodicWaveform">Shape ID for the recharge phase.</param>
        /// <param name="eventID">Event slot to modify.</param>
        /// <param name="wssTarget">Target device or broadcast.</param>
        void UpdateEventShape(int cathodicWaveform, int anodicWaveform, int eventID, WssTarget wssTarget);

        /// <summary>
        /// Uploads a prepared waveform and assigns shapes for <paramref name="eventID"/>.
        /// </summary>
        /// <param name="waveform">Prepared <see cref="WaveformBuilder"/>.</param>
        /// <param name="eventID">Event slot to target.</param>
        /// <param name="wssTarget">Target device or broadcast.</param>
        void UpdateWaveform(WaveformBuilder waveform, int eventID, WssTarget wssTarget);

        /// <summary>
        /// Builds a custom waveform from raw points and schedules the upload
        /// for the specified <paramref name="eventID"/>.
        /// </summary>
        /// <param name="waveform">Concatenated waveform definition.</param>
        /// <param name="eventID">Event slot to target.</param>
        /// <param name="wssTarget">Target device or broadcast.</param>
        void UpdateWaveform(int[] waveform, int eventID, WssTarget wssTarget);

        /// <summary>
        /// Loads a waveform JSON file (*WF.json), builds it, and enqueues an upload.
        /// </summary>
        /// <param name="fileName">File name or path; "WF.json" suffix is enforced.</param>
        /// <param name="eventID">Event slot to target.</param>
        void LoadWaveform(string fileName, int eventID);

        /// <summary>
        /// Schedules the upload of custom waveform chunks and points
        /// <paramref name="eventID"/> at the uploaded shapes.
        /// </summary>
        /// <param name="wave">Waveform builder.</param>
        /// <param name="eventID">Event slot to target.</param>
        /// <param name="wssTarget">Target device or broadcast.</param>
        void WaveformSetup(WaveformBuilder wave, int eventID, WssTarget wssTarget);

        // setup & edits

        /// <summary>
        /// Saves board settings to non-volatile memory. Implementations should
        /// pause streaming if needed, perform the save, then resume.
        /// </summary>
        /// <param name="wssTarget">Target device or broadcast.</param>
        void Save(WssTarget wssTarget);

        /// <summary>
        /// Loads board settings from non-volatile memory. Implementations should
        /// pause streaming if needed, perform the load, then resume.
        /// </summary>
        /// <param name="wssTarget">Target device or broadcast.</param>
        void Load(WssTarget wssTarget);

        /// <summary>
        /// Requests configuration blocks from the device.
        /// </summary>
        /// <param name="command">Command group identifier.</param>
        /// <param name="id">Sub-id / selector.</param>
        /// <param name="wssTarget">Target device or broadcast.</param>
        void Request_Configs(int command, int id, WssTarget wssTarget);

        /// <summary>
        /// Updates inter-phase delay (IPD) for events 1&#x2013;3 via setup commands (with replies).
        /// Non-blocking; enqueues the setup edits and returns immediately.
        /// If currently streaming, the core pauses streaming, sends edits, and resumes when done.
        /// </summary>
        /// <param name="ipd">Inter-phase delay in microseconds. Internally clamped to 1&#x2013;1000 &#xB5;s.</param>
        /// <param name="wssTarget">Target device or broadcast.</param>
        void UpdateIPD(int ipd, WssTarget wssTarget = WssTarget.Broadcast);

        /// <summary>
        /// Updates inter-phase delay (IPD) for a specific <paramref name="eventID"/> via setup commands (with replies).
        /// Non-blocking; enqueues the setup edit and returns immediately.
        /// If currently streaming, the core pauses streaming, sends the edit, and resumes when done.
        /// </summary>
        /// <param name="ipd">Inter-phase delay in microseconds. Internally clamped to 1&#x2013;1000 &#xB5;s.</param>
        /// <param name="eventID">Event slot to target.</param>
        /// <param name="wssTarget">Target device or broadcast.</param>
        void UpdateIPD(int ipd, int eventID, WssTarget wssTarget = WssTarget.Broadcast);

        /// <summary>
        /// Updates the event ratio for events 1&#x2013;3. Non-blocking; enqueues the edits and returns immediately.
        /// Valid ratios are exactly 1, 2, 4, or 8.
        /// </summary>
        /// <param name="ratio">Event ratio to apply. Must be 1, 2, 4, or 8.</param>
        /// <param name="wssTarget">Target device or broadcast.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="ratio"/> is not 1, 2, 4, or 8.</exception>
        void UpdateEventRatio(int ratio, WssTarget wssTarget = WssTarget.Broadcast);

        /// <summary>
        /// Updates the event ratio for a specific <paramref name="eventID"/>. Non-blocking; enqueues the edit and returns immediately.
        /// Valid ratios are exactly 1, 2, 4, or 8.
        /// </summary>
        /// <param name="ratio">Event ratio to apply. Must be 1, 2, 4, or 8.</param>
        /// <param name="eventID">Event slot to target.</param>
        /// <param name="wssTarget">Target device or broadcast.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="ratio"/> is not 1, 2, 4, or 8.</exception>
        void UpdateEventRatio(int ratio, int eventID, WssTarget wssTarget = WssTarget.Broadcast);

        /// <summary>
        /// Sets the start delay for the specified event. Non-blocking; enqueues the edit and returns immediately.
        /// Valid range is 0&#x2013;255 ms.
        /// </summary>
        /// <param name="delayMs">Delay in milliseconds (0&#x2013;255).</param>
        /// <param name="eventID">Event slot to modify.</param>
        /// <param name="wssTarget">Target device or broadcast.</param>
        void UpdateEventDelay(int delayMs, int eventID, WssTarget wssTarget = WssTarget.Broadcast);

        /// <summary>
        /// Sets the start delay for events 1&#x2013;3. Non-blocking; enqueues the edits and returns immediately.
        /// Valid range is 0&#x2013;255 ms.
        /// </summary>
        /// <param name="delayMs">Delay in milliseconds (0&#x2013;255).</param>
        /// <param name="wssTarget">Target device or broadcast.</param>
        void UpdateEventDelay(int delayMs, WssTarget wssTarget = WssTarget.Broadcast);

        /// <summary>
        /// Enables or disables execution of the specified event. Non-blocking; enqueues the edit and returns immediately.
        /// This controls whether the event runs during stimulation, not the live stream amplitude.
        /// </summary>
        /// <param name="eventID">Event slot to modify.</param>
        /// <param name="enabled"><c>true</c> to enable the event, <c>false</c> to disable it.</param>
        /// <param name="wssTarget">Target device or broadcast.</param>
        void SetEventEnabled(int eventID, bool enabled, WssTarget wssTarget = WssTarget.Broadcast);

        /// <summary>
        /// Enables or disables execution of events 1&#x2013;3. Non-blocking; enqueues the edits and returns immediately.
        /// This controls whether events run during stimulation, not the live stream amplitudes.
        /// </summary>
        /// <param name="enabled"><c>true</c> to enable the events, <c>false</c> to disable them.</param>
        /// <param name="wssTarget">Target device or broadcast.</param>
        void SetEventEnabled(bool enabled, WssTarget wssTarget = WssTarget.Broadcast);

        /// <summary>
        /// Creates a contact configuration on the device. Non-blocking; enqueues the command and returns immediately.
        /// The <see cref="ContactConfigDefinition.StimSetup"/> and <see cref="ContactConfigDefinition.RechargeSetup"/>
        /// arrays must each contain exactly four contact-role values: 0 (unused), 1 (source), or 2 (sink).
        /// </summary>
        /// <param name="definition">Contact configuration to create. Must not be null.</param>
        /// <param name="wssTarget">Target device or broadcast.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="definition"/> is null.</exception>
        void CreateContactConfig(ContactConfigDefinition definition, WssTarget wssTarget = WssTarget.Broadcast);

        /// <summary>
        /// Deletes the contact configuration with the specified ID. Non-blocking; enqueues the command.
        /// </summary>
        /// <param name="contactConfigID">Contact configuration ID to delete.</param>
        /// <param name="wssTarget">Target device or broadcast.</param>
        void DeleteContactConfig(int contactConfigID, WssTarget wssTarget = WssTarget.Broadcast);

        /// <summary>
        /// Assigns a contact configuration to the specified event. Non-blocking; enqueues the edit and returns immediately.
        /// </summary>
        /// <param name="eventID">Event slot to modify.</param>
        /// <param name="contactConfigID">Protocol contact configuration ID to assign (0&#x2013;255).</param>
        /// <param name="wssTarget">Target device or broadcast.</param>
        void UpdateEventContactConfig(int eventID, int contactConfigID, WssTarget wssTarget = WssTarget.Broadcast);

        /// <summary>
        /// Assigns a contact configuration to events 1&#x2013;3. Non-blocking; enqueues the edits and returns immediately.
        /// </summary>
        /// <param name="contactConfigID">Protocol contact configuration ID to assign (0&#x2013;255).</param>
        /// <param name="wssTarget">Target device or broadcast.</param>
        void UpdateEventContactConfig(int contactConfigID, WssTarget wssTarget = WssTarget.Broadcast);
    }
}
