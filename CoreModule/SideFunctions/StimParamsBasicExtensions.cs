// StimParamsBasicExtensions.cs
using System;
using Wss.CalibrationModule;
namespace Wss.CoreModule
{
    /// <summary>
    /// Convenience extension methods over <see cref="IStimParamsCore"/> that forward
    /// to the optional BASIC layer (<see cref="IBasicStimulation"/>).
    /// </summary>
    public static class StimParamsBasicExtensions
    {
        /// <summary>
        /// Forwards to <see cref="IBasicStimulation.UpdateWaveform(WaveformBuilder,int,WssTarget)"/>.
        /// </summary>
        /// <param name="s">Stimulation params core instance.</param>
        /// <param name="wf">Prepared waveform builder to upload.</param>
        /// <param name="eventId">Event slot to associate with the waveform.</param>
        /// <param name="t">Target device or broadcast.</param>
        /// <exception cref="NotSupportedException">Thrown when the BASIC capability is not available.</exception>
        public static void UpdateWaveform(this IStimParamsCore s, WaveformBuilder wf, int eventId, WssTarget t)
        {
            if (!s.TryGetBasic(out var b)) throw new NotSupportedException("Basic stimulation not available.");
            b.UpdateWaveform(wf, eventId, t);
        }

        /// <summary>
        /// Forwards to <see cref="IBasicStimulation.UpdateWaveform(int[],int,WssTarget)"/>.
        /// </summary>
        /// <param name="s">Stimulation params core instance.</param>
        /// <param name="waveform">Concatenated raw waveform definition.</param>
        /// <param name="eventId">Event slot to associate with the waveform.</param>
        /// <param name="t">Target device or broadcast.</param>
        /// <exception cref="NotSupportedException">Thrown when the BASIC capability is not available.</exception>
        public static void UpdateWaveform(this IStimParamsCore s, int[] waveform, int eventId, WssTarget t)
        {
            if (!s.TryGetBasic(out var b)) throw new NotSupportedException("Basic stimulation not available.");
            b.UpdateWaveform(waveform, eventId, t);
        }

        /// <summary>
        /// Forwards to <see cref="IBasicStimulation.UpdateEventShape"/>.
        /// </summary>
        /// <param name="s">Stimulation params core instance.</param>
        /// <param name="cathodicWaveform">Shape ID for the standard (cathodic) phase.</param>
        /// <param name="anodicWaveform">Shape ID for the recharge (anodic) phase.</param>
        /// <param name="eventId">Event slot to modify.</param>
        /// <param name="t">Target device or broadcast.</param>
        /// <exception cref="NotSupportedException">Thrown when the BASIC capability is not available.</exception>
        public static void UpdateEventShape(this IStimParamsCore s, int cathodicWaveform, int anodicWaveform, int eventId, WssTarget t)
        {
            if (!s.TryGetBasic(out var b)) throw new NotSupportedException("Basic stimulation not available.");
            b.UpdateEventShape(cathodicWaveform, anodicWaveform, eventId, t);
        }

        /// <summary>
        /// Forwards to <see cref="IBasicStimulation.LoadWaveform"/>.
        /// </summary>
        /// <param name="s">Stimulation params core instance.</param>
        /// <param name="fileName">Waveform JSON file name or path; "WF.json" suffix is enforced.</param>
        /// <param name="eventId">Event slot to target.</param>
        /// <exception cref="NotSupportedException">Thrown when the BASIC capability is not available.</exception>
        public static void LoadWaveform(this IStimParamsCore s, string fileName, int eventId)
        {
            if (!s.TryGetBasic(out var b)) throw new NotSupportedException("Basic stimulation not available.");
            b.LoadWaveform(fileName, eventId);
        }

        /// <summary>
        /// Forwards to <see cref="IBasicStimulation.WaveformSetup"/>.
        /// </summary>
        /// <param name="s">Stimulation params core instance.</param>
        /// <param name="wf">Waveform builder to upload.</param>
        /// <param name="eventId">Event slot to target.</param>
        /// <param name="t">Target device or broadcast.</param>
        /// <exception cref="NotSupportedException">Thrown when the BASIC capability is not available.</exception>
        public static void WaveformSetup(this IStimParamsCore s, WaveformBuilder wf, int eventId, WssTarget t)
        {
            if (!s.TryGetBasic(out var b)) throw new NotSupportedException("Basic stimulation not available.");
            b.WaveformSetup(wf, eventId, t);
        }

        /// <summary>
        /// Forwards to <see cref="IBasicStimulation.Save"/>. Saves board settings to non-volatile memory.
        /// </summary>
        /// <param name="s">Stimulation params core instance.</param>
        /// <param name="t">Target device or broadcast.</param>
        /// <exception cref="NotSupportedException">Thrown when the BASIC capability is not available.</exception>
        public static void SaveBoard(this IStimParamsCore s, WssTarget t)
        {
            if (!s.TryGetBasic(out var b)) throw new NotSupportedException("Basic stimulation not available.");
            b.Save(t);
        }

        /// <summary>
        /// Forwards to <see cref="IBasicStimulation.Load"/>. Loads board settings from non-volatile memory.
        /// </summary>
        /// <param name="s">Stimulation params core instance.</param>
        /// <param name="t">Target device or broadcast.</param>
        /// <exception cref="NotSupportedException">Thrown when the BASIC capability is not available.</exception>
        public static void LoadBoard(this IStimParamsCore s, WssTarget t)
        {
            if (!s.TryGetBasic(out var b)) throw new NotSupportedException("Basic stimulation not available.");
            b.Load(t);
        }

        /// <summary>
        /// Forwards to <see cref="IBasicStimulation.Request_Configs"/>.
        /// </summary>
        /// <param name="s">Stimulation params core instance.</param>
        /// <param name="command">Command group identifier.</param>
        /// <param name="id">Sub-id / selector.</param>
        /// <param name="t">Target device or broadcast.</param>
        /// <exception cref="NotSupportedException">Thrown when the BASIC capability is not available.</exception>
        public static void RequestConfigs(this IStimParamsCore s, int command, int id, WssTarget t)
        {
            if (!s.TryGetBasic(out var b)) throw new NotSupportedException("Basic stimulation not available.");
            b.Request_Configs(command, id, t);
        }

        /// <summary>
        /// Forwards to <see cref="IBasicStimulation.UpdateEventRatio(int,WssTarget)"/> for events 1&#x2013;3.
        /// Valid ratios are exactly 1, 2, 4, or 8.
        /// </summary>
        /// <param name="s">Stimulation params core instance.</param>
        /// <param name="ratio">Event ratio to apply. Must be 1, 2, 4, or 8.</param>
        /// <param name="t">Target device or broadcast.</param>
        /// <exception cref="NotSupportedException">Thrown when the BASIC capability is not available.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="ratio"/> is not 1, 2, 4, or 8.</exception>
        public static void UpdateEventRatio(this IStimParamsCore s, int ratio, WssTarget t = WssTarget.Broadcast)
        {
            if (!s.TryGetBasic(out var b)) throw new NotSupportedException("Basic stimulation not available.");
            b.UpdateEventRatio(ratio, t);
        }

        /// <summary>
        /// Forwards to <see cref="IBasicStimulation.UpdateEventRatio(int,int,WssTarget)"/> for a specific event.
        /// Valid ratios are exactly 1, 2, 4, or 8.
        /// </summary>
        /// <param name="s">Stimulation params core instance.</param>
        /// <param name="ratio">Event ratio to apply. Must be 1, 2, 4, or 8.</param>
        /// <param name="eventId">Event slot to target.</param>
        /// <param name="t">Target device or broadcast.</param>
        /// <exception cref="NotSupportedException">Thrown when the BASIC capability is not available.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="ratio"/> is not 1, 2, 4, or 8.</exception>
        public static void UpdateEventRatio(this IStimParamsCore s, int ratio, int eventId, WssTarget t = WssTarget.Broadcast)
        {
            if (!s.TryGetBasic(out var b)) throw new NotSupportedException("Basic stimulation not available.");
            b.UpdateEventRatio(ratio, eventId, t);
        }

        // Optional "Try*" variants if you prefer no-throw:

        /// <summary>
        /// Attempts to forward to <see cref="IBasicStimulation.UpdateWaveform(WaveformBuilder,int,WssTarget)"/> without throwing.
        /// </summary>
        /// <param name="s">Stimulation params core instance.</param>
        /// <param name="wf">Waveform builder to upload.</param>
        /// <param name="eventId">Event slot to associate with the waveform.</param>
        /// <param name="t">Target device or broadcast.</param>
        /// <returns>
        /// <c>true</c> if the BASIC capability is available and the enqueue call did not throw;
        /// <c>false</c> if BASIC is unavailable or an exception occurred.
        /// A <c>true</c> return does not confirm that the device accepted or completed the command.
        /// </returns>
        public static bool TryUpdateWaveform(this IStimParamsCore s, WaveformBuilder wf, int eventId, WssTarget t)
            => s.TryGetBasic(out var b) && TryCall(() => b.UpdateWaveform(wf, eventId, t));

        /// <summary>
        /// Attempts to forward to <see cref="IBasicStimulation.UpdateEventRatio(int,WssTarget)"/> for events 1&#x2013;3 without throwing.
        /// </summary>
        /// <param name="s">Stimulation params core instance.</param>
        /// <param name="ratio">Event ratio to apply. Must be 1, 2, 4, or 8.</param>
        /// <param name="t">Target device or broadcast.</param>
        /// <returns>
        /// <c>true</c> if the BASIC capability is available and the enqueue call did not throw;
        /// <c>false</c> if BASIC is unavailable or an exception occurred (e.g. invalid ratio).
        /// A <c>true</c> return does not confirm that the device accepted or completed the command.
        /// </returns>
        public static bool TryUpdateEventRatio(this IStimParamsCore s, int ratio, WssTarget t = WssTarget.Broadcast)
            => s.TryGetBasic(out var b) && TryCall(() => b.UpdateEventRatio(ratio, t));

        /// <summary>
        /// Attempts to forward to <see cref="IBasicStimulation.UpdateEventRatio(int,int,WssTarget)"/> without throwing.
        /// </summary>
        /// <param name="s">Stimulation params core instance.</param>
        /// <param name="ratio">Event ratio to apply. Must be 1, 2, 4, or 8.</param>
        /// <param name="eventId">Event slot to target.</param>
        /// <param name="t">Target device or broadcast.</param>
        /// <returns>
        /// <c>true</c> if the BASIC capability is available and the enqueue call did not throw;
        /// <c>false</c> if BASIC is unavailable or an exception occurred (e.g. invalid ratio).
        /// A <c>true</c> return does not confirm that the device accepted or completed the command.
        /// </returns>
        public static bool TryUpdateEventRatio(this IStimParamsCore s, int ratio, int eventId, WssTarget t = WssTarget.Broadcast)
            => s.TryGetBasic(out var b) && TryCall(() => b.UpdateEventRatio(ratio, eventId, t));

        // ---- New IBasicStimulation forwarding methods ----

        /// <summary>
        /// Forwards to <see cref="IBasicStimulation.UpdateEventDelay(int,int,WssTarget)"/>.
        /// Valid delay range is 0&#x2013;255 ms.
        /// </summary>
        /// <param name="s">Stimulation params core instance.</param>
        /// <param name="delayMs">Delay in milliseconds (0&#x2013;255).</param>
        /// <param name="eventId">Event slot to target.</param>
        /// <param name="t">Target device or broadcast.</param>
        /// <exception cref="NotSupportedException">Thrown when the BASIC capability is not available.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="delayMs"/> is outside 0&#x2013;255.</exception>
        public static void UpdateEventDelay(this IStimParamsCore s, int delayMs, int eventId, WssTarget t = WssTarget.Broadcast)
        {
            if (!s.TryGetBasic(out var b)) throw new NotSupportedException("Basic stimulation not available.");
            b.UpdateEventDelay(delayMs, eventId, t);
        }

        /// <summary>
        /// Forwards to <see cref="IBasicStimulation.UpdateEventDelay(int,WssTarget)"/> for events 1&#x2013;3.
        /// Valid delay range is 0&#x2013;255 ms.
        /// </summary>
        /// <param name="s">Stimulation params core instance.</param>
        /// <param name="delayMs">Delay in milliseconds (0&#x2013;255).</param>
        /// <param name="t">Target device or broadcast.</param>
        /// <exception cref="NotSupportedException">Thrown when the BASIC capability is not available.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="delayMs"/> is outside 0&#x2013;255.</exception>
        public static void UpdateEventDelay(this IStimParamsCore s, int delayMs, WssTarget t = WssTarget.Broadcast)
        {
            if (!s.TryGetBasic(out var b)) throw new NotSupportedException("Basic stimulation not available.");
            b.UpdateEventDelay(delayMs, t);
        }

        /// <summary>
        /// Forwards to <see cref="IBasicStimulation.SetEventEnabled(int,bool,WssTarget)"/>.
        /// Enables or disables execution of the specified event.
        /// </summary>
        /// <param name="s">Stimulation params core instance.</param>
        /// <param name="eventId">Event slot to target.</param>
        /// <param name="enabled"><c>true</c> to enable the event, <c>false</c> to disable it.</param>
        /// <param name="t">Target device or broadcast.</param>
        /// <exception cref="NotSupportedException">Thrown when the BASIC capability is not available.</exception>
        public static void SetEventEnabled(this IStimParamsCore s, int eventId, bool enabled, WssTarget t = WssTarget.Broadcast)
        {
            if (!s.TryGetBasic(out var b)) throw new NotSupportedException("Basic stimulation not available.");
            b.SetEventEnabled(eventId, enabled, t);
        }

        /// <summary>
        /// Forwards to <see cref="IBasicStimulation.SetEventEnabled(bool,WssTarget)"/> for events 1&#x2013;3.
        /// Enables or disables execution of events 1&#x2013;3.
        /// </summary>
        /// <param name="s">Stimulation params core instance.</param>
        /// <param name="enabled"><c>true</c> to enable the events, <c>false</c> to disable them.</param>
        /// <param name="t">Target device or broadcast.</param>
        /// <exception cref="NotSupportedException">Thrown when the BASIC capability is not available.</exception>
        public static void SetEventEnabled(this IStimParamsCore s, bool enabled, WssTarget t = WssTarget.Broadcast)
        {
            if (!s.TryGetBasic(out var b)) throw new NotSupportedException("Basic stimulation not available.");
            b.SetEventEnabled(enabled, t);
        }

        /// <summary>
        /// Forwards to <see cref="IBasicStimulation.CreateContactConfig"/>.
        /// The definition arrays must each contain exactly four contact-role values (0 unused, 1 source, 2 sink).
        /// </summary>
        /// <param name="s">Stimulation params core instance.</param>
        /// <param name="definition">Contact configuration to create. Must not be null.</param>
        /// <param name="t">Target device or broadcast.</param>
        /// <exception cref="NotSupportedException">Thrown when the BASIC capability is not available.</exception>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="definition"/> is null.</exception>
        /// <exception cref="ArgumentException">Thrown when a setup array is null or does not contain exactly four elements.</exception>
        public static void CreateContactConfig(this IStimParamsCore s, ContactConfigDefinition definition, WssTarget t = WssTarget.Broadcast)
        {
            if (!s.TryGetBasic(out var b)) throw new NotSupportedException("Basic stimulation not available.");
            b.CreateContactConfig(definition, t);
        }

        /// <summary>
        /// Forwards to <see cref="IBasicStimulation.DeleteContactConfig"/>.
        /// </summary>
        /// <param name="s">Stimulation params core instance.</param>
        /// <param name="contactConfigID">Protocol contact configuration ID to delete (0&#x2013;255).</param>
        /// <param name="t">Target device or broadcast.</param>
        /// <exception cref="NotSupportedException">Thrown when the BASIC capability is not available.</exception>
        public static void DeleteContactConfig(this IStimParamsCore s, int contactConfigID, WssTarget t = WssTarget.Broadcast)
        {
            if (!s.TryGetBasic(out var b)) throw new NotSupportedException("Basic stimulation not available.");
            b.DeleteContactConfig(contactConfigID, t);
        }

        /// <summary>
        /// Forwards to <see cref="IBasicStimulation.UpdateEventContactConfig(int,int,WssTarget)"/>.
        /// Assigns a protocol contact configuration to the specified event.
        /// </summary>
        /// <param name="s">Stimulation params core instance.</param>
        /// <param name="eventId">Event slot to modify.</param>
        /// <param name="contactConfigID">Protocol contact configuration ID to assign (0&#x2013;255).</param>
        /// <param name="t">Target device or broadcast.</param>
        /// <exception cref="NotSupportedException">Thrown when the BASIC capability is not available.</exception>
        public static void UpdateEventContactConfig(this IStimParamsCore s, int eventId, int contactConfigID, WssTarget t = WssTarget.Broadcast)
        {
            if (!s.TryGetBasic(out var b)) throw new NotSupportedException("Basic stimulation not available.");
            b.UpdateEventContactConfig(eventId, contactConfigID, t);
        }

        /// <summary>
        /// Forwards to <see cref="IBasicStimulation.UpdateEventContactConfig(int,WssTarget)"/> for events 1&#x2013;3.
        /// Assigns a protocol contact configuration to events 1&#x2013;3.
        /// </summary>
        /// <param name="s">Stimulation params core instance.</param>
        /// <param name="contactConfigID">Protocol contact configuration ID to assign (0&#x2013;255).</param>
        /// <param name="t">Target device or broadcast.</param>
        /// <exception cref="NotSupportedException">Thrown when the BASIC capability is not available.</exception>
        public static void UpdateEventContactConfig(this IStimParamsCore s, int contactConfigID, WssTarget t = WssTarget.Broadcast)
        {
            if (!s.TryGetBasic(out var b)) throw new NotSupportedException("Basic stimulation not available.");
            b.UpdateEventContactConfig(contactConfigID, t);
        }

        // ---- Try* variants ----

        /// <summary>
        /// Attempts to forward to <see cref="IBasicStimulation.UpdateEventDelay(int,int,WssTarget)"/> without throwing.
        /// </summary>
        /// <param name="s">Stimulation params core instance.</param>
        /// <param name="delayMs">Delay in milliseconds (0&#x2013;255).</param>
        /// <param name="eventId">Event slot to target.</param>
        /// <param name="t">Target device or broadcast.</param>
        /// <returns>
        /// <c>true</c> if the BASIC capability is available and the enqueue call did not throw;
        /// <c>false</c> if BASIC is unavailable or an exception occurred (e.g. out-of-range delay).
        /// A <c>true</c> return does not confirm that the device accepted or completed the command.
        /// </returns>
        public static bool TryUpdateEventDelay(this IStimParamsCore s, int delayMs, int eventId, WssTarget t = WssTarget.Broadcast)
            => s.TryGetBasic(out var b) && TryCall(() => b.UpdateEventDelay(delayMs, eventId, t));

        /// <summary>
        /// Attempts to forward to <see cref="IBasicStimulation.UpdateEventDelay(int,WssTarget)"/> for events 1&#x2013;3 without throwing.
        /// </summary>
        /// <param name="s">Stimulation params core instance.</param>
        /// <param name="delayMs">Delay in milliseconds (0&#x2013;255).</param>
        /// <param name="t">Target device or broadcast.</param>
        /// <returns>
        /// <c>true</c> if the BASIC capability is available and the enqueue call did not throw;
        /// <c>false</c> if BASIC is unavailable or an exception occurred (e.g. out-of-range delay).
        /// A <c>true</c> return does not confirm that the device accepted or completed the command.
        /// </returns>
        public static bool TryUpdateEventDelay(this IStimParamsCore s, int delayMs, WssTarget t = WssTarget.Broadcast)
            => s.TryGetBasic(out var b) && TryCall(() => b.UpdateEventDelay(delayMs, t));

        /// <summary>
        /// Attempts to forward to <see cref="IBasicStimulation.SetEventEnabled(int,bool,WssTarget)"/> without throwing.
        /// </summary>
        /// <param name="s">Stimulation params core instance.</param>
        /// <param name="eventId">Event slot to target.</param>
        /// <param name="enabled"><c>true</c> to enable the event, <c>false</c> to disable it.</param>
        /// <param name="t">Target device or broadcast.</param>
        /// <returns>
        /// <c>true</c> if the BASIC capability is available and the enqueue call did not throw;
        /// <c>false</c> if BASIC is unavailable or an exception occurred.
        /// A <c>true</c> return does not confirm that the device accepted or completed the command.
        /// </returns>
        public static bool TrySetEventEnabled(this IStimParamsCore s, int eventId, bool enabled, WssTarget t = WssTarget.Broadcast)
            => s.TryGetBasic(out var b) && TryCall(() => b.SetEventEnabled(eventId, enabled, t));

        /// <summary>
        /// Attempts to forward to <see cref="IBasicStimulation.SetEventEnabled(bool,WssTarget)"/> for events 1&#x2013;3 without throwing.
        /// </summary>
        /// <param name="s">Stimulation params core instance.</param>
        /// <param name="enabled"><c>true</c> to enable the events, <c>false</c> to disable them.</param>
        /// <param name="t">Target device or broadcast.</param>
        /// <returns>
        /// <c>true</c> if the BASIC capability is available and the enqueue call did not throw;
        /// <c>false</c> if BASIC is unavailable or an exception occurred.
        /// A <c>true</c> return does not confirm that the device accepted or completed the command.
        /// </returns>
        public static bool TrySetEventEnabled(this IStimParamsCore s, bool enabled, WssTarget t = WssTarget.Broadcast)
            => s.TryGetBasic(out var b) && TryCall(() => b.SetEventEnabled(enabled, t));

        /// <summary>
        /// Attempts to forward to <see cref="IBasicStimulation.CreateContactConfig"/> without throwing.
        /// </summary>
        /// <param name="s">Stimulation params core instance.</param>
        /// <param name="definition">Contact configuration to create.</param>
        /// <param name="t">Target device or broadcast.</param>
        /// <returns>
        /// <c>true</c> if the BASIC capability is available and the enqueue call did not throw;
        /// <c>false</c> if BASIC is unavailable or an exception occurred (e.g. null definition, invalid arrays).
        /// A <c>true</c> return does not confirm that the device accepted or completed the command.
        /// </returns>
        public static bool TryCreateContactConfig(this IStimParamsCore s, ContactConfigDefinition definition, WssTarget t = WssTarget.Broadcast)
            => s.TryGetBasic(out var b) && TryCall(() => b.CreateContactConfig(definition, t));

        /// <summary>
        /// Attempts to forward to <see cref="IBasicStimulation.DeleteContactConfig"/> without throwing.
        /// </summary>
        /// <param name="s">Stimulation params core instance.</param>
        /// <param name="contactConfigID">Protocol contact configuration ID to delete (0&#x2013;255).</param>
        /// <param name="t">Target device or broadcast.</param>
        /// <returns>
        /// <c>true</c> if the BASIC capability is available and the enqueue call did not throw;
        /// <c>false</c> if BASIC is unavailable or an exception occurred.
        /// A <c>true</c> return does not confirm that the device accepted or completed the command.
        /// </returns>
        public static bool TryDeleteContactConfig(this IStimParamsCore s, int contactConfigID, WssTarget t = WssTarget.Broadcast)
            => s.TryGetBasic(out var b) && TryCall(() => b.DeleteContactConfig(contactConfigID, t));

        /// <summary>
        /// Attempts to forward to <see cref="IBasicStimulation.UpdateEventContactConfig(int,int,WssTarget)"/> without throwing.
        /// </summary>
        /// <param name="s">Stimulation params core instance.</param>
        /// <param name="eventId">Event slot to target.</param>
        /// <param name="contactConfigID">Protocol contact configuration ID to assign (0&#x2013;255).</param>
        /// <param name="t">Target device or broadcast.</param>
        /// <returns>
        /// <c>true</c> if the BASIC capability is available and the enqueue call did not throw;
        /// <c>false</c> if BASIC is unavailable or an exception occurred.
        /// A <c>true</c> return does not confirm that the device accepted or completed the command.
        /// </returns>
        public static bool TryUpdateEventContactConfig(this IStimParamsCore s, int eventId, int contactConfigID, WssTarget t = WssTarget.Broadcast)
            => s.TryGetBasic(out var b) && TryCall(() => b.UpdateEventContactConfig(eventId, contactConfigID, t));

        /// <summary>
        /// Attempts to forward to <see cref="IBasicStimulation.UpdateEventContactConfig(int,WssTarget)"/> for events 1&#x2013;3 without throwing.
        /// </summary>
        /// <param name="s">Stimulation params core instance.</param>
        /// <param name="contactConfigID">Protocol contact configuration ID to assign (0&#x2013;255).</param>
        /// <param name="t">Target device or broadcast.</param>
        /// <returns>
        /// <c>true</c> if the BASIC capability is available and the enqueue call did not throw;
        /// <c>false</c> if BASIC is unavailable or an exception occurred.
        /// A <c>true</c> return does not confirm that the device accepted or completed the command.
        /// </returns>
        public static bool TryUpdateEventContactConfig(this IStimParamsCore s, int contactConfigID, WssTarget t = WssTarget.Broadcast)
            => s.TryGetBasic(out var b) && TryCall(() => b.UpdateEventContactConfig(contactConfigID, t));

        private static bool TryCall(Action a) { try { a(); return true; } catch { return false; } }
    }
}
