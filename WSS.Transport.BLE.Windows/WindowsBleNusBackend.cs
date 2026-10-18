using Wss.Ble.Windows.Native;
using Wss.CoreModule;

namespace Wss.Transports.Backends.Windows;

internal sealed class WindowsBleNusBackend : IBleNusBackend, IBleNativeStackProbe
{
    private readonly BleNusTransportOptions _options;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private WindowsBleClient? _client;
    private bool _initialized, _disposed;
    private Session? _session;

    private sealed class Session
    {
        internal readonly ulong Id;
        internal readonly CancellationTokenSource Stop = new();
        internal Session(ulong id) { Id = id; }
    }

    internal WindowsBleNusBackend(BleNusTransportOptions options, WindowsBleClient? initializedClient = null)
    {
        _options = options;
        _client = initializedClient;
        _initialized = initializedClient != null;
    }

    public bool IsConnected { get { lock (_gate) return !_disposed && _session != null && _client!.IsConnected; } }
    public event Action<byte[]>? BytesReceived;
    internal WindowsBleClient Client => _client ?? throw new InvalidOperationException("Client not initialized.");
    internal Task NotificationPump { get; private set; } = Task.CompletedTask;

    private BleNusOptions NusOptions => new()
    {
        ServiceUuid = _options.ServiceUuid,
        WriteCharacteristicUuid = _options.WriteCharacteristicUuid,
        NotifyCharacteristicUuid = _options.NotifyCharacteristicUuid
    };

    private async Task<WindowsBleClient> InitializeAsync(CancellationToken ct)
    {
        WindowsBleClient client;
        lock (_gate)
        {
            ThrowIfDisposed();
            client = _client ??= new WindowsBleClient();
        }
        if (!_initialized)
        {
            await client.InitializeAsync(ct).ConfigureAwait(false);
            _initialized = true;
        }
        return client;
    }

    async Task<BleNativeStackProbeResult> IBleNativeStackProbe.ProbeNativeStackAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            var client = await InitializeAsync(linked.Token).ConfigureAwait(false);
            var state = await client.QueryAdapterAsync(linked.Token).ConfigureAwait(false);
            if (state == BleAdapterState.Unknown) throw new InvalidOperationException("Windows BLE adapter state is unknown.");
            return state == BleAdapterState.Ready ? BleNativeStackProbeResult.StackAvailable : BleNativeStackProbeResult.AdapterUnavailable;
        }
        finally { _lifecycle.Release(); }
    }

    public async Task<IReadOnlyList<BleCandidate>> DiscoverAsync(BleDiscoveryRequest request, CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            var ct = linked.Token;
            var client = await InitializeAsync(ct).ConfigureAwait(false);
            if (IsConnected) throw new InvalidOperationException("Disconnect before discovering another BLE device.");
            if (!request.AutoSelectDevice && !string.IsNullOrWhiteSpace(request.DeviceId))
                return new[] { new BleCandidate(request.DeviceId!, null) };

            await client.StartScanAsync(ct).ConfigureAwait(false);
            try { await Task.Delay(request.ScanTimeout ?? TimeSpan.FromSeconds(10), ct).ConfigureAwait(false); }
            finally { await client.StopScanAsync(CancellationToken.None).ConfigureAwait(false); }
            if (client.LastScanError != null) throw new InvalidOperationException("Windows BLE scanning failed.", client.LastScanError);
            var devices = client.GetDiscoveredDevices();
            if (!request.AutoSelectDevice)
            {
                var match = devices.FirstOrDefault(d => string.Equals(d.Name, request.DeviceName, StringComparison.OrdinalIgnoreCase));
                return match == null ? Array.Empty<BleCandidate>() : new[] { Candidate(match) };
            }

            var candidates = new List<BleCandidate>();
            foreach (var device in devices)
            {
                ct.ThrowIfCancellationRequested();
                if (!device.ServiceUuids.Contains(request.ServiceUuid)) continue;
                try
                {
                    await client.ConnectAsync(device.Id, device.AddressType, new BleNusOptions
                    {
                        ServiceUuid = request.ServiceUuid, WriteCharacteristicUuid = request.WriteCharacteristicUuid,
                        NotifyCharacteristicUuid = request.NotifyCharacteristicUuid
                    }, ct).ConfigureAwait(false);
                    candidates.Add(Candidate(device));
                }
                catch (BleBridgeException ex) when (ex.Status == BleStatus.NativeFailure) { /* incompatible candidate */ }
                catch (TimeoutException) { /* unreachable candidate; wrapper has already fenced its failed connection */ }
                finally { await client.DisconnectAsync(CancellationToken.None).ConfigureAwait(false); }
            }
            return candidates;
        }
        catch (BleBridgeException ex) { throw new InvalidOperationException("Unable to discover Windows BLE devices.", ex); }
        finally { _lifecycle.Release(); }
    }

    private static BleCandidate Candidate(BleScanResult device) => new(device.Id, device.Name, device.Rssi, device.AddressType.ToString());
    private static BleAddressType AddressType(string? value) =>
        Enum.TryParse(value, true, out BleAddressType type) ? type : BleAddressType.Unspecified;

    public async Task ConnectAsync(BleCandidate candidate, CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            var client = await InitializeAsync(linked.Token).ConfigureAwait(false);
            if (IsConnected) return;
            InvalidateSession();
            await client.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            BleConnectionInfo info;
            try { info = await client.ConnectAsync(candidate.Id, AddressType(candidate.AddressType), NusOptions, linked.Token).ConfigureAwait(false); }
            catch (BleBridgeException ex) { throw new InvalidOperationException($"Unable to connect BLE device '{candidate.Id}' with the required NUS characteristics.", ex); }
            Session session;
            lock (_gate)
            {
                ThrowIfDisposed();
                _session = session = new Session(info.ConnectionId);
                NotificationPump = Task.Run(() => PumpAsync(client, session));
            }
        }
        finally { _lifecycle.Release(); }
    }

    private async Task PumpAsync(WindowsBleClient client, Session session)
    {
        try
        {
            while (await client.WaitForNotificationsAsync(session.Id, session.Stop.Token).ConfigureAwait(false))
            {
                foreach (var bytes in client.DrainNotifications(session.Id))
                {
                    Action<byte[]>? handler;
                    lock (_gate)
                    {
                        if (_disposed || !ReferenceEquals(_session, session)) return;
                        if (!client.IsConnected) break;
                        handler = BytesReceived; // claimed delivery; user code runs outside every lifecycle/state lock
                    }
                    try { handler?.Invoke(bytes); }
                    catch (Exception ex) { Log.Error(ex, "BLE BytesReceived handler failed."); }
                }
            }
        }
        catch (OperationCanceledException) when (session.Stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_disposed) { }
        catch (Exception ex)
        {
            Log.Error(ex, "Windows BLE notification stream failed.");
            await CleanupSessionAsync(session).ConfigureAwait(false);
        }
        finally
        {
            bool ended;
            lock (_gate) { ended = ReferenceEquals(_session, session); if (ended) _session = null; }
            if (ended) session.Stop.Cancel();
        }
    }

    public async Task SendAsync(byte[] data, CancellationToken cancellationToken)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        var snapshot = (byte[])data.Clone();
        Session session;
        WindowsBleClient client;
        lock (_gate)
        {
            ThrowIfDisposed();
            session = _session ?? throw new InvalidOperationException("BLE transport is not connected.");
            client = _client!;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Stop.Token, _lifetime.Token);
        await _sendGate.WaitAsync(linked.Token).ConfigureAwait(false);
        bool submitted = false;
        try
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (!ReferenceEquals(_session, session)) throw new InvalidOperationException("Queued BLE send belongs to a stale connection.");
            }
            if (snapshot.Length == 0)
            {
                linked.Token.ThrowIfCancellationRequested();
                submitted = true;
                await client.SendAsync(snapshot, linked.Token).ConfigureAwait(false);
            }
            for (int offset = 0; offset < snapshot.Length;)
            {
                linked.Token.ThrowIfCancellationRequested();
                uint budget = client.MaxWriteLength;
                if (!client.IsConnected || budget == 0) throw new InvalidOperationException("BLE transport has no active write budget.");
                int count = (int)Math.Min((uint)(snapshot.Length - offset), budget);
                var chunk = new byte[count];
                Buffer.BlockCopy(snapshot, offset, chunk, 0, count);
                submitted = true; // conservative: once handed to the wrapper, never assume no bytes reached the peripheral
                await client.SendAsync(chunk, linked.Token).ConfigureAwait(false);
                offset += count;
            }
        }
        catch (Exception ex)
        {
            if (submitted) await CleanupSessionAsync(session).ConfigureAwait(false);
            if (ex is BleBridgeException) throw new InvalidOperationException("Unable to write BLE data; no payload is replayed.", ex);
            throw;
        }
        finally { _sendGate.Release(); }
    }

    private void InvalidateSession()
    {
        Session? old;
        lock (_gate) { old = _session; _session = null; }
        old?.Stop.Cancel();
    }

    private async Task CleanupSessionAsync(Session session)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate) if (_disposed || !ReferenceEquals(_session, session)) return;
            InvalidateSession();
            try { await Client.DisconnectAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { Log.Error(ex, "BLE cleanup failed; releasing native context."); Dispose(); }
        }
        finally { _lifecycle.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            InvalidateSession();
            WindowsBleClient? client;
            lock (_gate) { if (_disposed) return; client = _client; }
            if (client != null) await client.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            // Never join NotificationPump here: a receive handler can synchronously disconnect itself.
        }
        finally { _lifecycle.Release(); }
    }

    public void Dispose()
    {
        WindowsBleClient? client;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            client = _client;
            BytesReceived = null;
        }
        InvalidateSession();
        _lifetime.Cancel();
        client?.Dispose();
        // Outstanding operations may still release gates. Do not dispose them underneath callers.
    }

    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(WindowsBleNusBackend)); }
}
