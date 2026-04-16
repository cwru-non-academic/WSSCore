# Constructor Changes

This file tracks constructor migration rules for this repo. It is intended for agents making future API cleanup changes.

## Current Pattern

When a public constructor has started to accumulate optional parameters or mixes required dependencies with behavioral/configuration knobs, convert it to a single constructor that accepts an options object.

Use this split:

- Required runtime dependencies remain direct constructor parameters.
- Behavioral/configuration values move into a `*Options` class.
- Remove old public constructors when we are intentionally making a breaking prerelease cleanup.

Do not add `Microsoft.Extensions.Options` or DI-specific patterns. Use plain POCO option classes to preserve Unity and `net48` friendliness.

## Implemented Changes

### 1. `SerialPortTransport`

Status: implemented

Old public constructors removed:

```csharp
SerialPortTransport(string portName, int baud = 115200, Parity parity = Parity.None,
    int dataBits = 8, StopBits stopBits = StopBits.One, int readTimeoutMs = 10)

SerialPortTransport(int baud = 115200, Parity parity = Parity.None,
    int dataBits = 8, StopBits stopBits = StopBits.One, int readTimeoutMs = 10)
```

New constructor:

```csharp
SerialPortTransport(SerialPortTransportOptions options)
```

Rules:

- `options` is required.
- Exactly one port selection mode must be used:
- `AutoSelectPort = true`, or
- `PortName` set to a non-empty value.
- Reject configurations that set both or neither.

Current options surface:

```csharp
PortName
AutoSelectPort
Baud
Parity
DataBits
StopBits
ReadTimeoutMs
```

### 2. `WssClient`

Status: implemented

Old public constructor removed:

```csharp
WssClient(ITransport transport, IFrameCodec codec, WSSVersionHandler versionHandler,
    byte sender = 0x00, bool ownsTransport = true)
```

New constructor:

```csharp
WssClient(ITransport transport, IFrameCodec codec, WSSVersionHandler versionHandler,
    WssClientOptions options)
```

Rules:

- `transport`, `codec`, `versionHandler`, and `options` are required.
- `options.ResponseTimeout` must be greater than zero.
- Internal request timeout should come from `WssClientOptions.ResponseTimeout`, not a hardcoded constant.

Current options surface:

```csharp
Sender
OwnsTransport
ResponseTimeout
```

### 3. `WssStimulationCore`

Status: implemented

Old public constructor removed:

```csharp
WssStimulationCore(ITransport transport, string jsonPath, int maxSetupTries = 5)
```

New constructor:

```csharp
WssStimulationCore(ITransport transport, WssStimulationCoreOptions options)
```

Rules:

- `transport` and `options` are required.
- `options.ConfigPath` must be non-empty.
- `options.MaxSetupTries` must be at least 1.
- The packet delay between setup commands remains hardcoded in the core because it is treated as a hardware/radio constraint, not consumer-configurable behavior.

Current options surface:

```csharp
ConfigPath
MaxSetupTries
DefaultIpi
DefaultAmp
DefaultSync
DefaultRatio
DefaultIpd
```

## How To Extend This Pattern

When converting another constructor in the future:

1. Create a new `*Options` class near the owning type.
2. Move non-dependency knobs into that options class.
3. Validate required options inside the constructor.
4. Remove the old public constructor if we still want a breaking cleanup.
5. Update in-repo call sites immediately.
6. Append a new section to this file documenting:
- old signature
- new signature
- validation rules
- intentional exceptions to the pattern

## Important Constraint

Keep the change constructor-focused. Do not automatically convert ordinary operational method parameters into options objects unless they represent reusable grouped configuration or request models.

## Request/Definition Changes

These are not constructor options. They are grouped request or definition models for public protocol APIs that had overload sprawl or ambiguous positional array arguments.

### 4. `CreateEventRequest`

Status: implemented

Old public overload set removed:

```csharp
CreateEvent(int eventId, int delayMs, int contactConfigId, ...)
CreateEvent(int eventId, int delayMs, int contactConfigId, int standardShapeId, int rechargeShapeId, ...)
CreateEvent(int eventId, int delayMs, int contactConfigId, int[] standardAmp, int[] rechargeAmp, int[] pw, ...)
CreateEvent(int eventId, int delayMs, int contactConfigId, int standardShapeId, int rechargeShapeId, int[] standardAmp, int[] rechargeAmp, int[] pw, ...)
```

New method:

```csharp
CreateEvent(CreateEventRequest request, WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
```

Supporting type:

```csharp
EventPulseWidths
```

Rules:

- `request` is required.
- `StandardShapeId` and `RechargeShapeId` must be provided together.
- `StandardAmplitudes` and `RechargeAmplitudes` must be provided together.
- `PulseWidths` is required when amplitude arrays are provided.
- Amplitude arrays are required when `PulseWidths` is provided.
- Supported shapes remain equivalent to the previous overload set:
- basic event
- event with shape IDs
- event with amplitudes and pulse widths
- event with shapes, amplitudes, and pulse widths

### 5. `ContactConfigDefinition`

Status: implemented

Old public method removed:

```csharp
CreateContactConfig(int contactId, int[] stimSetup, int[] rechargeSetup, int LEDs, ...)
```

New method:

```csharp
CreateContactConfig(ContactConfigDefinition definition, WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
```

Rules:

- `definition` is required.
- `StimSetup` and `RechargeSetup` must each contain exactly 4 elements.
- `Leds` is nullable and omitted when null.

### 6. `StreamChangeRequest`

Status: implemented

Old public method removed:

```csharp
StreamChange(int[] pa, int[] pw, int[] ipi, WssTarget target = WssTarget.Broadcast, CancellationToken ct = default)
```

New method:

```csharp
StreamChange(StreamChangeRequest request, WssTarget target = WssTarget.Broadcast, CancellationToken ct = default)
```

Rules:

- `request` is required.
- At most one of the three groups may be omitted.
- Each non-null group must still contain exactly 3 elements.

### 7. `ScheduleDefinition`

Status: implemented

Old public method removed:

```csharp
CreateSchedule(int scheduleId, int durationMs, int syncSignal, ...)
```

New method:

```csharp
CreateSchedule(ScheduleDefinition definition, WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
```

Rules:

- `definition` is required.
- `ScheduleId`, `DurationMs`, and `SyncSignal` keep the same protocol validation and encoding rules.

### 8. `TestModeTransportOptions`

Status: implemented

Old public constructor removed:

```csharp
TestModeTransport(TimeSpan? baseLatency = null, int jitterMs = 0, int maxInboundChunkSize = 0,
    double inboundDropProbability = 0.0, Random rng = null, byte[] fallbackPayload = null,
    Func<byte[], Task<byte[]>> autoResponderAsync = null)
```

New constructor:

```csharp
TestModeTransport(TestModeTransportOptions options)
```

Rules:

- `options` is required.
- `Rng` and `FallbackPayload` still get safe defaults when null.
