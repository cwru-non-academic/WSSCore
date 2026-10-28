using System.Text;
using Wss.CoreModule;

namespace Wss.Transports.Backends.MacOS;

/// <summary>Registers the packaged CoreBluetooth backend for hosts that explicitly bootstrap transports.</summary>
public static class MacBleBackend
{
    /// <summary>Registers the macOS backend without initializing Bluetooth or connecting a peripheral.</summary>
    public static void Register() => BleBackendLoader.RegisterFactory("macos", new MacBleNusBackendFactory());
    /// <summary>Loads the native ABI and creates/releases a context without initializing Bluetooth or requesting authorization.</summary>
    public static void VerifyNativeAbi() { using var native = new MacNative(); }
}
internal sealed class MacBleNusBackendFactory : IBleNusBackendFactory
{
    public IBleNusBackend Create(BleNusTransportOptions options) => new MacBleNusBackend(options);
}

internal sealed class MacBleNusBackend : IBleNusBackend, IBleNativeStackProbe
{
    private readonly BleNusTransportOptions _options;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1), _commands = new(1, 1), _sendGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<ulong, TaskCompletionSource<MacEvent>> _pending = new();
    private readonly Dictionary<string, BleCandidate> _discovered = new();
    private readonly Queue<(ulong Session, byte[] Data)> _notifications = new();
    private readonly SemaphoreSlim _notificationSignal = new(0);
    private IMacNative? _native;
    private ulong _operation, _nextSession, _session, _connecting, _scan;
    private uint _budget;
    private int _scanError;
    private bool _disposed;
    private CancellationTokenSource? _sessionStop;
    internal Task NotificationPump { get; }
    internal MacBleNusBackend(BleNusTransportOptions options, IMacNative? native = null)
    {
        _options = options; _native = native;
        if (_native != null) _native.Event += Receive;
        NotificationPump = Task.Run(PumpAsync);
    }
    public bool IsConnected { get { lock (_gate) return !_disposed && _session != 0 && _budget != 0; } }
    public event Action<byte[]>? BytesReceived;
    private IMacNative Native
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_native == null) { _native = new MacNative(); _native.Event += Receive; }
                return _native;
            }
        }
    }
    private void Receive(MacEvent e)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (e.Kind == 1 && _pending.TryGetValue(e.Operation, out var pending)) pending.TrySetResult(e);
            else if (e.Kind == 4 && e.Session == _scan && _scan != 0) _scanError = e.Status;
            else if (e.Kind == 3 && e.Session == _connecting && _connecting != 0)
            {
                if (_notifications.Count >= 256) { _connecting = 0; return; }
                _notifications.Enqueue((e.Session, e.Data));
            }
            else if (e.Kind == 2 && e.Session == _scan && _scan != 0)
            {
                var text = Encoding.UTF8.GetString(e.Data).Split(new[] { '\n' }, 3);
                if (text.Length != 3 || !Guid.TryParse(text[0], out _) || !int.TryParse(text[1], out var rssi)) return;
                if (_discovered.Count >= 256 && !_discovered.ContainsKey(text[0])) return;
                _discovered[text[0]] = new BleCandidate(text[0], text[2], rssi == 127 ? null : rssi);
            }
            else if (e.Session == _session && _session != 0)
            {
                if (e.Kind == 4 || (e.Kind == 3 && _notifications.Count >= 256))
                {
                    // Overflow/link loss terminates the session; partial streams are not silently accepted.
                    _budget = 0; _sessionStop?.Cancel(); _notifications.Clear();
                    _ = Task.Run(async () => { try { await CleanupSessionAsync(e.Session).ConfigureAwait(false); } catch (Exception ex) { Log.Error(ex, "macOS BLE link cleanup failed."); } });
                }
                else if (e.Kind == 3) { _notifications.Enqueue((e.Session, e.Data)); _notificationSignal.Release(); }
            }
        }
    }
    private async Task PumpAsync()
    {
        try
        {
            while (true)
            {
                await _notificationSignal.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                Action<byte[]>? handler; (ulong Session, byte[] Data) message;
                lock (_gate)
                {
                    if (_notifications.Count == 0) continue;
                    message = _notifications.Dequeue();
                    if (_disposed || message.Session != _session || _budget == 0) continue;
                    handler = BytesReceived;
                }
                try { handler?.Invoke(message.Data); }
                catch (Exception ex) { Log.Error(ex, "BLE BytesReceived handler failed."); }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }
    private async Task<MacEvent> CommandAsync(uint command, ulong session, CancellationToken token, string? device = null, byte[]? data = null)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await _commands.WaitAsync(linked.Token).ConfigureAwait(false);
        ulong id = 0;
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            var native = Native;
            var source = new TaskCompletionSource<MacEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                ThrowIfDisposed(); id = ++_operation; _pending.Add(id, source);
            }
            // Native submissions/fences must never run under the callback state lock.
            int status = native.Submit(command, id, session, device, _options.ServiceUuid.ToString(),
                _options.WriteCharacteristicUuid.ToString(), _options.NotifyCharacteristicUuid.ToString(), data ?? Array.Empty<byte>(), 10000);
            if (status != 0) throw Error(status);
            using (linked.Token.Register(() => { try { native.Cancel(id); } catch (ObjectDisposedException) { } }))
            {
                var e = await source.Task.ConfigureAwait(false);
                if (e.Status == 4) throw new OperationCanceledException(linked.Token);
                if (e.Status == 5) throw new TimeoutException("CoreBluetooth operation timed out; no payload is replayed.");
                if (e.Status != 0) throw Error(e.Status);
                linked.Token.ThrowIfCancellationRequested();
                return e;
            }
        }
        finally { lock (_gate) _pending.Remove(id); _commands.Release(); }
    }
    private static Exception Error(int status) => new InvalidOperationException("CoreBluetooth status " + status + ": " + (status == 8 ? "Bluetooth access denied; authorize the host application in macOS Privacy & Security." : status == 6 ? "Bluetooth adapter is unavailable or powered off." : "BLE operation failed."));
    async Task<BleNativeStackProbeResult> IBleNativeStackProbe.ProbeNativeStackAsync(CancellationToken token)
    {
        // ABI load-only verification is separate; this explicit probe may request OS authorization.
        await CommandAsync(1, 0, token).ConfigureAwait(false);
        return BleNativeStackProbeResult.StackAvailable;
    }
    public async Task<IReadOnlyList<BleCandidate>> DiscoverAsync(BleDiscoveryRequest request, CancellationToken token)
    {
        await _lifecycle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            if (IsConnected) throw new InvalidOperationException("Disconnect before discovering another BLE device.");
            await CommandAsync(1, 0, linked.Token).ConfigureAwait(false);
            if (!request.AutoSelectDevice && !string.IsNullOrWhiteSpace(request.DeviceId))
            {
                if (!Guid.TryParse(request.DeviceId, out _)) throw new ArgumentException("macOS DeviceId must be a CoreBluetooth peripheral UUID.");
                return new[] { new BleCandidate(request.DeviceId!, null) };
            }
            ulong scan;
            lock (_gate) { scan = _scan = ++_nextSession; _scanError = 0; _discovered.Clear(); }
            try
            {
                await CommandAsync(2, scan, linked.Token).ConfigureAwait(false);
                await Task.Delay(request.ScanTimeout ?? TimeSpan.FromSeconds(10), linked.Token).ConfigureAwait(false);
            }
            finally
            {
                try { await CommandAsync(3, scan, CancellationToken.None).ConfigureAwait(false); }
                finally { lock (_gate) _scan = 0; }
            }
            BleCandidate[] devices;
            lock (_gate) { if (_scanError != 0) throw Error(_scanError); devices = _discovered.Values.ToArray(); }
            if (!request.AutoSelectDevice) return devices.Where(d => string.Equals(d.Name, request.DeviceName, StringComparison.OrdinalIgnoreCase)).Take(1).ToArray();
            var compatible = new List<BleCandidate>();
            foreach (var device in devices)
            {
                linked.Token.ThrowIfCancellationRequested();
                try
                {
                    await CommandAsync(1, 0, linked.Token).ConfigureAwait(false);
                    await CommandAsync(4, ++_nextSession, linked.Token, device.Id).ConfigureAwait(false);
                    compatible.Add(device);
                }
                catch (InvalidOperationException) { /* Reject peers without the required writable RX and notifying TX. */ }
                catch (TimeoutException) { /* Failed connection was fenced by native. */ }
                finally { await CommandAsync(6, 0, CancellationToken.None).ConfigureAwait(false); }
            }
            return compatible;
        }
        finally { _lifecycle.Release(); }
    }
    public async Task ConnectAsync(BleCandidate candidate, CancellationToken token)
    {
        await _lifecycle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (IsConnected) return;
            InvalidateSession();
            if (_native != null) await CommandAsync(6, 0, CancellationToken.None).ConfigureAwait(false);
            await CommandAsync(1, 0, token).ConfigureAwait(false);
            ulong session = ++_nextSession;
            lock (_gate) _connecting = session;
            try
            {
                var result = await CommandAsync(4, session, token, candidate.Id).ConfigureAwait(false);
                lock (_gate)
                {
                    ThrowIfDisposed();
                    if (_connecting != session) throw new InvalidOperationException("BLE notification queue overflowed during connection.");
                    if (result.Budget == 0) throw new InvalidOperationException("CoreBluetooth returned no write-with-response budget.");
                    _session = session; _connecting = 0; _budget = result.Budget; _sessionStop = new CancellationTokenSource();
                    if (_notifications.Count != 0) _notificationSignal.Release(_notifications.Count);
                }
            }
            catch { InvalidateSession(); await CommandAsync(6, session, CancellationToken.None).ConfigureAwait(false); throw; }
        }
        finally { _lifecycle.Release(); }
    }
    public async Task SendAsync(byte[] data, CancellationToken token)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        var snapshot = (byte[])data.Clone();
        ulong session; CancellationToken stop;
        lock (_gate) { ThrowIfDisposed(); session = _session; if (session == 0) throw new InvalidOperationException("BLE transport is not connected."); stop = _sessionStop!.Token; }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stop, _lifetime.Token);
        await _sendGate.WaitAsync(linked.Token).ConfigureAwait(false);
        bool submitted = false;
        try
        {
            for (int offset = 0; offset < snapshot.Length;)
            {
                linked.Token.ThrowIfCancellationRequested();
                int count;
                lock (_gate) { if (_session != session) throw new OperationCanceledException(stop); count = (int)Math.Min((uint)(snapshot.Length - offset), _budget); }
                if (count == 0) throw new InvalidOperationException("No BLE write budget.");
                var chunk = new byte[count]; Buffer.BlockCopy(snapshot, offset, chunk, 0, count);
                submitted = true;
                var result = await CommandAsync(5, session, linked.Token, data:chunk).ConfigureAwait(false);
                lock (_gate) if (_session == session) _budget = result.Budget;
                offset += count;
            }
        }
        catch
        {
            if (submitted) await CleanupSessionAsync(session).ConfigureAwait(false);
            throw;
        }
        finally { _sendGate.Release(); }
    }
    private void InvalidateSession()
    {
        lock (_gate) { _session = 0; _connecting = 0; _budget = 0; _sessionStop?.Cancel(); _notifications.Clear(); }
    }
    private async Task CleanupSessionAsync(ulong session)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate) if (_disposed || _session != session) return;
            InvalidateSession();
            await CommandAsync(6, 0, CancellationToken.None).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }
    public async Task DisconnectAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        InvalidateSession();
        await _lifecycle.WaitAsync(token).ConfigureAwait(false);
        try { lock (_gate) if (_disposed || _native == null) return; await CommandAsync(6, 0, CancellationToken.None).ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
        // A receive handler may synchronously disconnect; never join the pump here.
    }
    public void Dispose()
    {
        IMacNative? native;
        lock (_gate) { if (_disposed) return; _disposed = true; native = _native; BytesReceived = null; }
        InvalidateSession(); _lifetime.Cancel();
        native?.Dispose(); // synchronous native callback fence, outside the managed state lock
        lock (_gate) foreach (var pending in _pending.Values) pending.TrySetException(new ObjectDisposedException(nameof(MacBleNusBackend)));
    }
    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(MacBleNusBackend)); }
}
