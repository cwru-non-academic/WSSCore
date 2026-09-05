# WSSCoreInterface Event Programming API Update Summary

## Purpose

This update makes internally supported event and schedule programming capabilities available through the DLL-facing APIs while keeping the public surface organized by user intent.

The original issue was event ratio: the protocol client already supported updating it, and setup code used it internally, but consumers could not access it through the higher-level DLL interfaces. While fixing that gap, related event, contact, and schedule operations were grouped into clearer capability layers.

## Design principle

The API is divided into two levels:

1. **Basic stimulation**: edits the default/simple setup, especially the built-in/default events `1`, `2`, and `3`.
2. **Advanced event programming**: creates or manages custom event/schedule layouts beyond the default setup.

This avoids making normal users deal with schedule programming while still exposing the lower-level capabilities needed for custom workflows.

## Basic stimulation scope

Use the Basic API when the user is still working with the default setup and only needs to tune or reassign existing events.

Basic includes operations such as:

- Updating event ratio.
- Updating event delay.
- Enabling or disabling events.
- Updating IPD.
- Updating event shape or waveform.
- Creating or deleting contact configs.
- Assigning a contact config to one default event or all default events.

Contact config management is intentionally Basic because it can be useful without custom schedules. A user may want to create a new contact routing pattern and assign it to event `1`, `2`, or `3` while still using the default stimulation structure.

## Advanced event programming scope

The advanced interface is named:

```csharp
IAdvancedEventProgrammer
```

Use this interface when the user needs to program custom event/schedule behavior, especially anything beyond the default three-event setup.

Advanced workflows include:

- Creating or deleting events.
- Creating or deleting schedules.
- Adding events to schedules.
- Removing or moving events between schedules.
- Running multiple schedules with different durations/frequencies.
- Editing programmed event pulse widths.
- Editing programmed event amplitudes.
- Setting schedule state, schedule group, or group state.
- Triggering sync groups.

## Why live amplitude/PW/frequency controls are separate

Amplitude, pulse width, and timing/frequency were already available through live stimulation APIs. Those APIs are appropriate when the user wants to change streamed stimulation values for the default channels.

However, live stimulation controls are not the same as programmed event/schedule configuration.

Examples:

- Live amplitude changes streamed output values.
- Advanced event amplitude changes the stored event configuration.
- Live IPI changes live stimulation timing for existing channel behavior.
- Schedule duration changes a programmed device schedule.

Therefore, event-level pulse width, event-level amplitude, and schedule duration/frequency belong in `IAdvancedEventProgrammer` when they are part of custom programmed event/schedule layouts.

## When to use each layer

Use **Basic** when:

- The user is working with the default/simple setup.
- The user only needs to tune existing events.
- The user wants to enable/disable event execution.
- The user wants to modify contact routing for default events.
- The user does not need to create new schedules or events.

Use **IAdvancedEventProgrammer** when:

- The user needs more than the default three events.
- The user needs multiple schedules at different frequencies/durations.
- Different events must belong to different schedules.
- The user needs to create, delete, move, or reassign events/schedules.
- The user needs persistent event-level pulse width or amplitude configuration rather than live streamed values.

## Relationship to normal setup

The internal normal setup builds a minimum viable stimulation structure:

1. Clear device configuration.
2. Query module settings.
3. Create schedules.
4. Create contact configs.
5. Create events.
6. Set event ratios.
7. Add events to schedules.
8. Sync the group.
9. Start stimulation.

The public grouping maps those responsibilities as follows:

| Setup operation | Public grouping |
|---|---|
| Clear device configuration | Internal/admin only |
| Query module settings | Internal/readback only |
| Create schedule | Advanced |
| Create contact config | Basic |
| Create event | Advanced |
| Set event ratio | Basic |
| Add event to schedule | Advanced |
| Sync group | Advanced |
| Start stimulation | Existing lifecycle/basic API |

## Commands intentionally not exposed normally

Some low-level protocol commands remain outside the normal public interfaces because they are destructive, diagnostic, or too low-level for regular stimulation workflows:

- Clear device configuration.
- Device reset.
- Raw settings writes.
- Raw stream packets.
- Raw waveform chunk upload.
- Device log and diagnostic commands.

These should only be documented or exposed later if a separate admin/diagnostic API is intentionally designed.

## Documentation guidance

For downstream user documentation, emphasize workflow choice rather than listing every protocol method first:

- Use existing stimulation/params APIs for normal live stimulation control.
- Use `IBasicStimulation` for default event setup edits.
- Use `IAdvancedEventProgrammer` for custom event/schedule programming.

The advanced interface should not be presented as the default way to stimulate. It is for users who need custom programmed layouts.

## Example scenarios

### Change ratio or delay for default events

Use Basic. The user is still using events `1`, `2`, and `3` and only tuning setup behavior.

### Create a new contact config and apply it to event 2

Use Basic. The user is changing routing for an existing/default event, not creating a custom schedule system.

### Run two schedules at different frequencies

Use `IAdvancedEventProgrammer`. This requires schedule-level programming and event-to-schedule membership control.

### Change amplitude while stimulating

Use live stimulation/params APIs. This does not require advanced event programming unless the user specifically needs to change stored event configuration.

### Change amplitude for a custom programmed event

Use `IAdvancedEventProgrammer`. This is event-configuration programming, not live streaming control.
