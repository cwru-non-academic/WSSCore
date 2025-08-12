using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

public sealed class WssClient : IDisposable
{
    private readonly ITransport _transport;
    private readonly IFrameCodec _codec;
    private readonly byte _sender;

    public enum WssTarget : byte
    {
        Broadcast = 0x8F,
        Wss1 = 0x81,
        Wss2 = 0x82,
        Wss3 = 0x83
    }

    public enum WssMessageId : byte
    {
        Reset = 0x04,
        Echo = 0x07,
        RequestAnalog = 0x02,
        Clear = 0x40,
        RequestConfig = 0x41,
        CreateContactConfig = 0x42,
        DeleteContactConfig = 0x43,
        CreateEvent = 0x44,
        DeleteEvent = 0x45,
        AddEventToSchedule = 0x46,
        RemoveEventFromSchedule = 0x47,
        MoveEventToSchedule = 0x48,
        EditEventConfig = 0x49,
        CreateSchedule = 0x4A,
        DeleteSchedule = 0x4B,
        SyncGroup = 0x4C,
        ChangeGroupState = 0x4D,
        ChangeScheduleConfig = 0x4E,
        ResetSchedule = 0x4F,
        CostumeWaveform = 0x9D,
        StimulationSwitch = 0x0B,
        BoardCommands = 0x09,
        StreamChangeAll = 0x30,
        StreamChangeNoIPI = 0x31,
        StreamChangeNoPA = 0x33,
        StreamChangeNoPW = 0x32,
    }

    private readonly ConcurrentDictionary<(byte target, byte msgId), ConcurrentQueue<TaskCompletionSource<byte[]>>> _pending
    = new();

    public bool Started { get; private set; }

    public WssClient(ITransport transport, IFrameCodec codec, byte sender = 0x00)
    {
        _transport = transport;
        _codec = codec;
        _sender = sender;
        _transport.BytesReceived += OnBytes;
    }

    public Task ConnectAsync(CancellationToken ct = default) => _transport.ConnectAsync(ct);
    public Task DisconnectAsync(CancellationToken ct = default) => _transport.DisconnectAsync(ct);

    /// <summary>
    /// Builds payload [cmd][len][data...], frames it, sends it, and awaits one response
    /// for the (target, msgId) key.
    /// </summary>
    /// <param name="msgId">The WSS message ID to send.</param>
    /// <param name="target">Target device to send to.</param>
    /// <param name="ct">Cancellation token to cancel the send/wait.</param>
    /// <param name="dataBytes">Payload data bytes (excluding cmd/len).</param>
    /// <returns>Response as a string, after processing.</returns>
    private Task<string> SendCmdAsync(WssMessageId msgId, WssTarget target, CancellationToken ct, params byte[] dataBytes)
    {
        if (dataBytes == null) dataBytes = Array.Empty<byte>();
        if (dataBytes.Length > 255)
            throw new ArgumentException("Payload too long. Max 255 bytes for length field.", nameof(dataBytes));

        // Construct [cmd][len][data...]
        var payload = new byte[2 + dataBytes.Length];
        payload[0] = (byte)msgId;
        payload[1] = (byte)dataBytes.Length; // length = bytes after cmd+len
        if (dataBytes.Length > 0)
            Buffer.BlockCopy(dataBytes, 0, payload, 2, dataBytes.Length);

        var framed = _codec.Frame(_sender, (byte)target, payload);
        var key = ((byte)target, (byte)msgId);

        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        // FIFO queue for multiple pending requests with same key
        var queue = _pending.GetOrAdd(key, _ => new ConcurrentQueue<TaskCompletionSource<byte[]>>());
        queue.Enqueue(tcs);

        return SendAwaitOneAsync(tcs, framed, key, ct);
    }


    private async Task<string> SendAwaitOneAsync(
        TaskCompletionSource<byte[]> tcs,
        byte[] framed,
        (byte target, byte msgId) key,
        CancellationToken ct)
    {
        await _transport.SendAsync(framed, ct).ConfigureAwait(false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(2000));
        using (cts.Token.Register(() => tcs.TrySetCanceled()))
        {
            var frame = await tcs.Task.ConfigureAwait(false);
            return ProcessFrame(frame);
        }
    }

    // Fire-and-forget path: no pending entry, just send the frame.
    private Task SendFireAndForgetAsync(WssTarget target, byte[] payload, CancellationToken ct = default)
    {
        if (payload == null || payload.Length < 2)
            throw new ArgumentException("Payload must include [cmd][lenPlaceholder].", nameof(payload));

        payload[1] = (byte)(payload.Length - 2);            // fill length
        var framed = _codec.Frame(_sender, (byte)target, payload);
        return _transport.SendAsync(framed, ct);            // no waiter
    }

    private void OnBytes(byte[] chunk)
    {
        foreach (var frame in _codec.Deframe(chunk))
        {
            if (frame == null || frame.Length < 3) continue;

            var key = ((byte)frame[1], (byte)frame[2]);

            if (_pending.TryGetValue(key, out var q))
            {
                // Pop until we find a waiter we can complete (skip any that timed out/canceled)
                while (q.TryDequeue(out var waiter))
                {
                    if (waiter.TrySetResult(frame))
                        break; // matched the oldest pending request
                    // else it was canceled already; keep dequeuing
                }

                // Optional: if queue is empty now, prune the dictionary entry
                if (q.IsEmpty)
                    _pending.TryRemove(key, out _);
            }
            else
            {
                // TODO: unsolicited/late frame; consider logging or metrics
            }
        }
    }

    private string ProcessFrame(byte[] data)
    {
        if (data.Length >= 3 && data[2] == 0x05) // error
        {
            var code = data.ElementAtOrDefault(4);
            var cmd = data.ElementAtOrDefault(5);
            var text = code switch
            {
                0x00 => "No Error",
                0x01 => "Comms Error",
                0x02 => "Wrong Receiver",
                0x03 => "Checksum Error",
                0x04 => "Command Error",
                0x05 => "Parameters Error",
                0x06 => "No Setup",
                0x07 => "Incompatible",
                0x0B => "No Schedule",
                0x0C => "No Event",
                0x0D => "No Memory",
                0x0E => "Not Event",
                0x0F => "Delay Too Long",
                0x10 => "Wrong Schedule",
                0x11 => "Duration Too Short",
                0x12 => "Fault",
                0x15 => "Delay Too Short",
                0x16 => "Event Exists",
                0x17 => "Schedule Exists",
                0x18 => "No Config",
                0x19 => "Bad State",
                0x1A => "Not Shape",
                0x20 => "Wrong Address",
                0x30 => "Stream Params",
                0x31 => "Stream Address",
                0x81 => "Output Invalid",
                _ => "Unknown"
            };
            return $"Error: {text} in Command: {cmd:x}";
        }

        if (data.Length >= 5 && data[2] == 0x0B)
        {
            if (data[4] == 0x01) { Started = true; return "Start Acknowledged"; }
            if (data[4] == 0x00) { Started = false; return "Stop Acknowledged"; }
        }

        return BitConverter.ToString(data).Replace("-", " ").ToLowerInvariant();
    }

    public void Dispose()
    {
        _transport.BytesReceived -= OnBytes;
        _transport.Dispose();
    }

    // Helper used to extract a byte from an int, ensuring it's within 0-255 range.
    // Throws ArgumentOutOfRangeException if not.
    private static byte ToByteValidated(int v, string paramName = "value")
    {
        if (v < 0 || v > 255) throw new ArgumentOutOfRangeException(paramName, "Value must be between 0 and 255.");
        return (byte)v;
    }

    #region stimulation_base_methods
    public Task<string> StartStim(WssTarget target = WssTarget.Broadcast, CancellationToken ct = default)
    => SendCmdAsync(WssMessageId.StimulationSwitch, target, ct, 0x03);

    public Task<string> StopStim(WssTarget target = WssTarget.Broadcast, CancellationToken ct = default)
    => SendCmdAsync(WssMessageId.StimulationSwitch, target, ct, 0x04);

    public Task<string> Reset(WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
    => SendCmdAsync(WssMessageId.Reset, target, ct, 0x04, 0x00);

    /// <summary>
    /// Requests specific data blocks data1 and data2 from the WSS device.
    /// Untested, and reading from WSS devices is not yet implemented.
    /// </summary>
    public Task<string> Echo(int echoData1, int echoData2, WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
    {
        // Validate and convert ints into bytes
        var b1 = ToByteValidated(echoData1, nameof(echoData1));
        var b2 = ToByteValidated(echoData2, nameof(echoData2));

        return SendCmdAsync(WssMessageId.Echo, target, ct, b1, b2);
    }

    /// <summary>
    /// Requests specific battery and impednace data from the WSS device.
    /// Untested, and reading from WSS devices is not yet implemented.
    /// </summary>
    public Task<string> RequestAnalog(WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
    => SendCmdAsync(WssMessageId.RequestAnalog, target, ct, 0x01);

    /// <summary>
    /// Requests different configuration depending on the configIndex.
    /// </summary>
    /// <param name="configIndex">Clear events(1), schedules(2), contacts(3), all(0).</param>
    public Task<string> Clear(int configIndex, WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
    {
        // Validate and convert ints into bytes
        var b1 = ToByteValidated(configIndex, nameof(configIndex));

        return SendCmdAsync(WssMessageId.Echo, target, ct, b1);
    }

    /// <summary>
    /// Sends a request to the WSS target for a specific configuration type.
    /// This is a general-purpose request that can target output configs, events,
    /// schedules, and their various sub-configurations.
    /// </summary>
    /// <param name="command">
    /// Integer code that selects which configuration to request:
    /// 0 = Output Configuration List
    /// 1 = Output Configuration details
    /// 2 = Event List
    /// 3 = Basic Event configuration
    /// 4 = Event output configuration
    /// 5 = Event stimulation configuration
    /// 6 = Event shape configuration
    /// 7 = Schedule basic configuration
    /// 8 = Schedule listing
    /// </param>
    /// <param name="id">ID associated with the requested configuration (0–255).</param>
    /// <param name="target">Target WSS device to query (default: Wss1).</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>Processed response string from the WSS target.</returns>
    public Task<string> RequestConfigs(int command, int id, WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
    {
        // Map the command code to the two selector bytes used in the WSS protocol
        var selectors = command switch
        {
            0 => (0x00, 0x00), // Output Configuration List
            1 => (0x00, 0x01), // Output Configuration details
            2 => (0x01, 0x00), // Event List
            3 => (0x01, 0x01), // Basic Event configuration
            4 => (0x01, 0x02), // Event output configuration
            5 => (0x01, 0x03), // Event stimulation configuration
            6 => (0x01, 0x04), // Event shape configuration
            7 => (0x02, 0x00), // Schedule basic configuration
            8 => (0x02, 0x01), // Schedule listing
            _ => (0x00, 0x00), // Default: Output Configuration List
        };

        // Convert id safely to a byte with range validation
        byte validatedId = ToByteValidated(id, nameof(id));

        // SendCmdAsync will build [cmd][len][selectors][id] and handle framing/queue/await
        return SendCmdAsync(WssMessageId.RequestConfig, target, ct, (byte)selectors.Item1, (byte)selectors.Item2, validatedId);
    }

    /// <summary>
    /// Creates a contact configuration for the stimulator.
    /// Defines the sources and sinks for stimulation and recharge phases.
    /// </summary>
    /// <param name="contactId">
    /// ID for the contact configuration (0–255).
    /// </param>
    /// <param name="stimSetup">
    /// Array of 4 integers (0 = not used, 1 = source, 2 = sink) representing
    /// the stimulation phase for each output on the stimulator.  
    /// Index 0 = closest to switch, index 3 = farthest from switch.
    /// </param>
    /// <param name="rechargeSetup">
    /// Array of 4 integers (0 = not used, 1 = source, 2 = sink) representing
    /// the recharge phase for each output on the stimulator.  
    /// Index 0 = closest to switch, index 3 = farthest from switch.
    /// </param>
    /// <param name="target">The WSS target device to send the configuration to.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>Processed response string from the WSS target.</returns>
    public Task<string> CreateContactConfig(int contactId, int[] stimSetup, int[] rechargeSetup, WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
    {
        if (stimSetup == null || stimSetup.Length != 4)
            throw new ArgumentException("Stim setup must be an array of exactly 4 integers.", nameof(stimSetup));

        if (rechargeSetup == null || rechargeSetup.Length != 4)
            throw new ArgumentException("Recharge setup must be an array of exactly 4 integers.", nameof(rechargeSetup));

        // Convert ID safely
        byte validatedId = ToByteValidated(contactId, nameof(contactId));

        // Reverse order so index 0 (closest to switch) becomes index 3 internally
        // made so the order matches other methods in which array index 0 is closest to the switch
        var stimReversed = stimSetup.Reverse().ToArray();
        var rechargeReversed = rechargeSetup.Reverse().ToArray();

        // Encode stim and recharge setups into single bytes (bit-packed 2 bits per output)
        byte stimByte = EncodeContactSetup(stimReversed);
        byte rechargeByte = EncodeContactSetup(rechargeReversed);

        return SendCmdAsync(WssMessageId.CreateContactConfig, target, ct, validatedId, stimByte, rechargeByte);
    }

    /// <summary>
    /// Encodes a contact configuration array into a single byte.
    /// </summary>
    /// <param name="setup">
    /// Array of 4 integers (0 = not used, 1 = source, 2 = sink),
    /// index 0 = farthest from switch, index 3 = closest to switch.
    /// </param>
    /// <returns>
    /// Encoded byte: 2 bits per contact, 00 = not used, 10 = source, 11 = sink.
    /// </returns>
    private static byte EncodeContactSetup(int[] setup)
    {
        if (setup.Length != 4)
            throw new ArgumentException("Setup must have exactly 4 elements.");

        byte result = 0;
        for (int i = 0; i < 4; i++)
        {
            int value = setup[i] switch
            {
                0 => 0b00,
                1 => 0b10,
                2 => 0b11,
                _ => throw new ArgumentOutOfRangeException(nameof(setup), "Values must be 0, 1, or 2.")
            };
            result |= (byte)(value << ((3 - i) * 2));
        }
        return result;
    }

    /// <summary>
    /// Deletes an existing contact configuration on the WSS device by its ID.
    /// </summary>
    /// <param name="contactId">The contact configuration ID to delete (0–255).</param>
    /// <param name="target">The WSS target device to send the request to (default: Wss1).</param>
    /// <param name="ct">Optional cancellation token to cancel the operation.</param>
    /// <returns>Processed response string from the WSS target.</returns>
    public Task<string> DeleteContactConfig(int contactId, WssTarget target = WssTarget.Wss1, CancellationToken ct = default)
    {
        // Convert ID safely to a byte (throws if out of range)
        byte validatedId = ToByteValidated(contactId, nameof(contactId));
        return SendCmdAsync(WssMessageId.DeleteContactConfig, target, ct, validatedId);
    }

    

    #endregion
}
