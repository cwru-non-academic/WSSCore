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

    // Builds payload (sets length), frames, sends, awaits one response for (target,msgId)
    private Task<string> SendCmdAsync(byte msgId, WssTarget target, CancellationToken ct, params byte[] payload)
    {
        if (payload == null || payload.Length < 2)
            throw new ArgumentException("Payload must include [cmd][lenPlaceholder].", nameof(payload));

        payload[1] = (byte)(payload.Length - 2);

        var framed = _codec.Frame(_sender, (byte)target, payload);
        var key = ((byte)target, msgId);

        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Get or create the FIFO queue for this (target,msgId)
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
            var cmd  = data.ElementAtOrDefault(5);
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
                _    => "Unknown"
            };
            return $"Error: {text} in Command: {cmd:x}";
        }

        if (data.Length >= 5 && data[2] == 0x0B)
        {
            if (data[4] == 0x01) { Started = true;  return "Start Acknowledged"; }
            if (data[4] == 0x00) { Started = false; return "Stop Acknowledged";  }
        }

        return BitConverter.ToString(data).Replace("-", " ").ToLowerInvariant();
    }

    public void Dispose()
    {
        _transport.BytesReceived -= OnBytes;
        _transport.Dispose();
    }

    #region stimulation_base_methods
    public Task<string> StartStim(WssTarget target = WssTarget.Broadcast, CancellationToken ct = default)
    => SendCmdAsync(0x0B, target, ct, 0x0B, 0x00, 0x03);

    public Task<string> StopStim(WssTarget target = WssTarget.Broadcast, CancellationToken ct = default)
    => SendCmdAsync(0x0B, target, ct, 0x0B, 0x00, 0x04);


    #endregion
}
