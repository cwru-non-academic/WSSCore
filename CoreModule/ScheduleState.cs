namespace Wss.CoreModule
{
    /// <summary>
    /// Protocol schedule states for WSS device schedule control.
    /// </summary>
    /// <remarks>
    /// Numeric values are sent directly to the firmware and must match protocol definitions:
    /// 0 = ACTIVE, 1 = READY, 2 = SUSPEND.
    /// Only <see cref="Active"/>, <see cref="Ready"/>, and <see cref="Suspend"/> are valid;
    /// other integer casts will be rejected by <see cref="IAdvancedEventProgrammer.SetScheduleState"/>
    /// and <see cref="IAdvancedEventProgrammer.SetGroupState"/>.
    /// </remarks>
    public enum ScheduleState
    {
        /// <summary>Schedule is active and running (firmware value 0).</summary>
        Active = 0,

        /// <summary>Schedule has been configured and is ready to run but is not yet active (firmware value 1).</summary>
        Ready = 1,

        /// <summary>Schedule execution is suspended (firmware value 2).</summary>
        Suspend = 2
    }
}
