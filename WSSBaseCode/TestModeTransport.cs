using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// In-memory transport for debugging and unit tests. No real I/O.
/// Features: artificial latency, jitter, inbound chunking, and drop simulation.
/// Default auto-responder validates checksum, flips sender/target, and echoes or replaces payload.
/// </summary>
public sealed class TestModeTransport : ITransport
{
    // --- configuration ---
    /// <summary>Artificial latency applied to inbound delivery.</summary>
    public TimeSpan BaseLatency { get; set; } = TimeSpan.Zero;

    /// <summary>Random jitter added to BaseLatency, in milliseconds.</summary>
    public int JitterMs { get; set; } = 0;

    /// <summary>If >0, splits inbound payloads into chunks of up to this size.</summary>
    public int MaxInboundChunkSize { get; set; } = 0;

    /// <summary>Probability [0..1] of randomly dropping an inbound chunk.</summary>
    public double InboundDropProbability { get; set; } = 0.0;

    /// <summary>Random generator used for jitter, chunk sizing, and drops.</summary>
    public Random Rng { get; set; } = new Random(1234);

    /// <summary>Payload used when incoming checksum is invalid.</summary>
    public byte[] FallbackPayload { get; set; } = new byte[] { 0xE1, 0xE2, 0xE3 };

    /// <summary>Optional override auto-responder. If null, the built-in default responder is used.</summary>
    public Func<byte[], Task<byte[]>> AutoResponderAsync { get; set; } = null;

    private volatile bool _connected;
    private volatile bool _disposed;
    private CancellationTokenSource _lifetimeCts;

    /// <inheritdoc/>
    public bool IsConnected => _connected;

    /// <inheritdoc/>
    public event Action<byte[]> BytesReceived;

    /// <summary>
    /// Initializes a new <see cref="TestModeTransport"/> with optional configuration.
    /// </summary>
    /// <param name="baseLatency">Artificial latency applied to inbound delivery (default 0).</param>
    /// <param name="jitterMs">Random jitter in milliseconds (default 0).</param>
    /// <param name="maxInboundChunkSize">If &gt;0, splits inbound payloads (default 0).</param>
    /// <param name="inboundDropProbability">Probability [0..1] of dropping an inbound chunk (default 0.0).</param>
    /// <param name="rng">Random generator to use (default new Random(1234)).</param>
    /// <param name="fallbackPayload">Payload used when checksum fails (default {0xE1,0xE2,0xE3}).</param>
    /// <param name="autoResponderAsync">Optional override auto-responder (default null = built-in).</param>
    public TestModeTransport(
        TimeSpan? baseLatency = null,
        int jitterMs = 0,
        int maxInboundChunkSize = 0,
        double inboundDropProbability = 0.0,
        Random rng = null,
        byte[] fallbackPayload = null,
        Func<byte[], Task<byte[]>> autoResponderAsync = null)
    {
        BaseLatency = baseLatency ?? TimeSpan.Zero;
        JitterMs = jitterMs;
        MaxInboundChunkSize = maxInboundChunkSize;
        InboundDropProbability = inboundDropProbability;
        Rng = rng ?? new Random(1234);
        FallbackPayload = fallbackPayload ?? new byte[] { 0xE1, 0xE2, 0xE3 };
        AutoResponderAsync = autoResponderAsync;
    }

    /// <inheritdoc/>
    /// <summary>
    /// Connects the transport. No real I/O occurs, only state change.
    /// </summary>
    public Task ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_connected) return Task.CompletedTask;
        _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _connected = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <summary>
    /// Disconnects the transport and cancels any pending operations.
    /// </summary>
    public Task DisconnectAsync(CancellationToken ct = default)
    {
        if (!_connected) return Task.CompletedTask;
        _connected = false;
        try { _lifetimeCts?.Cancel(); } catch { }
        _lifetimeCts?.Dispose();
        _lifetimeCts = null;
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <summary>
    /// Sends bytes into the transport. The default auto-responder will generate a reply
    /// based on checksum validation and invoke <see cref="BytesReceived"/>.
    /// </summary>
    /// <param name="data">Outbound bytes to simulate sending.</param>
    /// <param name="ct">Optional cancellation token.</param>
    public Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (!_connected) throw new InvalidOperationException("Transport is not connected.");
        if (data == null) throw new ArgumentNullException(nameof(data));
        ct.ThrowIfCancellationRequested();

        var token = _lifetimeCts?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                var responder = AutoResponderAsync ?? DefaultResponderAsync;
                var reply = await responder(data).ConfigureAwait(false);
                if (reply is { Length: > 0 })
                    await EmitInboundAsync(reply, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch { }
        }, token);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Injects inbound bytes as if received from a physical device. They are subject
    /// to latency, jitter, chunking, and drop settings before being delivered.
    /// </summary>
    /// <param name="payload">Raw inbound payload.</param>
    /// <param name="ct">Optional cancellation token.</param>
    public Task EnqueueIncoming(byte[] payload, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (!_connected) throw new InvalidOperationException("Transport is not connected.");
        if (payload == null) throw new ArgumentNullException(nameof(payload));
        return EmitInboundAsync(payload, ct);
    }

    /// <inheritdoc/>
    /// <summary>
    /// Disposes the transport, cancels background operations, and releases resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _lifetimeCts?.Cancel(); } catch { }
        _lifetimeCts?.Dispose();
    }

    // --- private default auto-responder ---
    private Task<byte[]> DefaultResponderAsync(byte[] req)
    {
        if (req == null || req.Length < 4) return Task.FromResult(req);

        // Suppress responses for streaming commands (IDs 0x30–0x33 at payload[0])
        if (IsFireAndForget(req))
            return Task.FromResult(Array.Empty<byte>());

        int endIdx = req.Length - 1;
        int cksIdx = req.Length - 2;
        byte endMarker = req[endIdx];

        byte inCks = req[cksIdx];
        byte recomputed = ComputeChecksum(req.AsSpan(0, cksIdx));
        bool ok = inCks == recomputed;

        if (ok)
        {
            var resp = new byte[req.Length];
            Buffer.BlockCopy(req, 0, resp, 0, req.Length);
            (resp[0], resp[1]) = (resp[1], resp[0]);
            resp[cksIdx] = ComputeChecksum(resp.AsSpan(0, cksIdx));
            resp[endIdx] = endMarker;
            return Task.FromResult(resp);
        }
        else
        {
            int payloadLen = FallbackPayload?.Length ?? 0;
            var resp = new byte[2 + payloadLen + 2];
            resp[0] = req.Length >= 2 ? req[1] : (byte)0;
            resp[1] = req.Length >= 1 ? req[0] : (byte)0;
            if (payloadLen > 0) Buffer.BlockCopy(FallbackPayload, 0, resp, 2, payloadLen);
            int respCksIdx = resp.Length - 2;
            int respEndIdx = resp.Length - 1;
            resp[respCksIdx] = ComputeChecksum(resp.AsSpan(0, respCksIdx));
            resp[respEndIdx] = endMarker;
            return Task.FromResult(resp);
        }
    }


    // --- helpers ---
    private byte ComputeChecksum(ReadOnlySpan<byte> frameWithoutCksAndEnd)
    {
        byte x = 0;
        for (int i = 0; i < frameWithoutCksAndEnd.Length; i++) x ^= frameWithoutCksAndEnd[i];
        return x;
    }

    private static bool IsFireAndForget(byte[] frame)
    {
        // Frame must be at least [S][T][payload0][...][CKS][END]
        if (frame == null || frame.Length < 5) return false;
        byte id = frame[2]; // first byte of payload
        return id >= 0x30 && id <= 0x33;
    }

    private async Task EmitInboundAsync(byte[] bytes, CancellationToken ct)
    {
        if (!_connected) return;

        var delay = BaseLatency;
        if (JitterMs > 0) delay += TimeSpan.FromMilliseconds(Rng.Next(0, JitterMs + 1));
        if (delay > TimeSpan.Zero) await Task.Delay(delay, ct).ConfigureAwait(false);

        if (MaxInboundChunkSize > 0 && bytes.Length > MaxInboundChunkSize)
        {
            int off = 0;
            while (off < bytes.Length && !_disposed && _connected)
            {
                ct.ThrowIfCancellationRequested();
                int remain = bytes.Length - off;
                int size = Math.Min(MaxInboundChunkSize, remain);
                size = Rng.Next(1, size + 1);

                if (!(InboundDropProbability > 0 && Rng.NextDouble() < InboundDropProbability))
                {
                    var chunk = new byte[size];
                    Buffer.BlockCopy(bytes, off, chunk, 0, size);
                    BytesReceived?.Invoke(chunk);
                }

                off += size;

                if (MaxInboundChunkSize > 1)
                {
                    int d = JitterMs > 0 ? Math.Min(3, JitterMs) : 0;
                    if (d > 0) await Task.Delay(Rng.Next(0, d + 1), ct).ConfigureAwait(false);
                }
            }
        }
        else
        {
            if (!(InboundDropProbability > 0 && Rng.NextDouble() < InboundDropProbability))
            {
                var copy = new byte[bytes.Length];
                Buffer.BlockCopy(bytes, 0, copy, 0, bytes.Length);
                BytesReceived?.Invoke(copy);
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DebugTransport));
    }
}
