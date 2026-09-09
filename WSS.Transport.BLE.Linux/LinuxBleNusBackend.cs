using Linux.Bluetooth;
using Linux.Bluetooth.Extensions;
using Wss.CoreModule;
using LinuxDevice = Linux.Bluetooth.Device;
using LinuxGattCharacteristic = Linux.Bluetooth.GattCharacteristic;
using LinuxGattCharacteristicValueEventArgs = Linux.Bluetooth.GattCharacteristicValueEventArgs;

namespace Wss.Transports.Backends.Linux;

internal sealed class LinuxBleNusBackend : IBleNusBackend
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly BleNusTransportOptions _options;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly object _gate = new();

    private LinuxDevice? _device;
    private LinuxGattCharacteristic? _writeCharacteristic;
    private LinuxGattCharacteristic? _notifyCharacteristic;
    private bool _disposed;

    internal LinuxBleNusBackend(BleNusTransportOptions options)
    {
        _options = options;
    }

    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                return _device != null && _writeCharacteristic != null && _notifyCharacteristic != null;
            }
        }
    }

    public event Action<byte[]>? BytesReceived;

    public async Task<IReadOnlyList<BleCandidate>> DiscoverAsync(
        BleDiscoveryRequest request,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        var adapter = await GetAdapterAsync(cancellationToken).ConfigureAwait(false);

        if (!request.AutoSelectDevice && !string.IsNullOrWhiteSpace(request.DeviceId))
        {
            LinuxDevice? configuredDevice = await FindDeviceByIdAsync(adapter, request.DeviceId!, cancellationToken).ConfigureAwait(false);
            if (configuredDevice != null)
            {
                return new[] { await CreateCandidateAsync(configuredDevice, cancellationToken).ConfigureAwait(false) };
            }
        }

        IReadOnlyList<LinuxDevice> devices = await ScanForDevicesAsync(adapter, request, cancellationToken).ConfigureAwait(false);

        if (!request.AutoSelectDevice)
        {
            LinuxDevice? configuredDevice = !string.IsNullOrWhiteSpace(request.DeviceId)
                ? await FindDeviceByIdAsync(devices, request.DeviceId!, cancellationToken).ConfigureAwait(false)
                : await FindDeviceByNameAsync(devices, request.DeviceName!, cancellationToken).ConfigureAwait(false);

            if (configuredDevice == null)
            {
                return Array.Empty<BleCandidate>();
            }

            return new[] { await CreateCandidateAsync(configuredDevice, cancellationToken).ConfigureAwait(false) };
        }

        var candidates = new List<BleCandidate>();
        foreach (LinuxDevice device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BleCandidate? candidate = await TryCreateValidatedCandidateAsync(device, cancellationToken).ConfigureAwait(false);
            if (candidate != null)
            {
                candidates.Add(candidate);
            }
        }

        return candidates;
    }

    public async Task ConnectAsync(BleCandidate candidate, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        LinuxDevice linuxDevice = await ResolveLinuxDeviceAsync(candidate.Id, cancellationToken).ConfigureAwait(false);
        TimeSpan timeout = _options.ScanTimeout ?? DefaultTimeout;

        await linuxDevice.ConnectAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        await linuxDevice.WaitForPropertyValueAsync("Connected", value: true, timeout).WaitAsync(cancellationToken).ConfigureAwait(false);
        await linuxDevice.WaitForPropertyValueAsync("ServicesResolved", value: true, timeout).WaitAsync(cancellationToken).ConfigureAwait(false);

        (LinuxGattCharacteristic writeCharacteristic, LinuxGattCharacteristic notifyCharacteristic) =
            await ResolveTransportCharacteristicsAsync(candidate.Name, linuxDevice, cancellationToken).ConfigureAwait(false);

        notifyCharacteristic.Value += OnCharacteristicValueChangedAsync;

        try
        {
            await notifyCharacteristic.StartNotifyAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            notifyCharacteristic.Value -= OnCharacteristicValueChangedAsync;
            await linuxDevice.DisconnectAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }

        lock (_gate)
        {
            _device = linuxDevice;
            _writeCharacteristic = writeCharacteristic;
            _notifyCharacteristic = notifyCharacteristic;
        }
    }

    public async Task SendAsync(byte[] data, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        LinuxGattCharacteristic? writeCharacteristic;
        lock (_gate)
        {
            writeCharacteristic = _writeCharacteristic;
        }

        if (writeCharacteristic == null)
        {
            throw new InvalidOperationException("BLE transport is not connected.");
        }

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await writeCharacteristic.WriteValueAsync(data, new Dictionary<string, object>
            {
                ["type"] = "request"
            }).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        var (device, notifyCharacteristic) = TakeAndClearConnectionState();

        if (notifyCharacteristic != null)
        {
            notifyCharacteristic.Value -= OnCharacteristicValueChangedAsync;

            try
            {
                await notifyCharacteristic.StopNotifyAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to stop BLE notifications.");
            }
        }

        if (device == null)
        {
            return;
        }

        try
        {
            await device.DisconnectAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to disconnect BLE transport.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sendGate.Dispose();
    }

    private static async Task<Adapter> GetAdapterAsync(CancellationToken cancellationToken)
    {
        Adapter? adapter = (await BlueZManager.GetAdaptersAsync().WaitAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault();
        return adapter ?? throw new InvalidOperationException("No Linux Bluetooth adapter is available.");
    }

    private static async Task<LinuxDevice?> FindDeviceByIdAsync(
        Adapter adapter,
        string deviceId,
        CancellationToken cancellationToken)
    {
        LinuxDevice? device = await adapter.GetDeviceAsync(deviceId).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (device != null)
        {
            return device;
        }

        IReadOnlyList<LinuxDevice> devices = await adapter.GetDevicesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        return await FindDeviceByIdAsync(devices, deviceId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<LinuxDevice?> FindDeviceByIdAsync(
        IEnumerable<LinuxDevice> devices,
        string deviceId,
        CancellationToken cancellationToken)
    {
        foreach (LinuxDevice device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var properties = await device.GetPropertiesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            if (string.Equals(properties.Address, deviceId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(device.ObjectPath.ToString(), deviceId, StringComparison.Ordinal))
            {
                return device;
            }
        }

        return null;
    }

    private static async Task<LinuxDevice?> FindDeviceByNameAsync(
        IEnumerable<LinuxDevice> devices,
        string deviceName,
        CancellationToken cancellationToken)
    {
        foreach (LinuxDevice device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var properties = await device.GetPropertiesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            string? name = properties.Name ?? properties.Alias;
            if (string.Equals(name, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                return device;
            }
        }

        return null;
    }

    private static async Task<IReadOnlyList<LinuxDevice>> ScanForDevicesAsync(
        Adapter adapter,
        BleDiscoveryRequest request,
        CancellationToken cancellationToken)
    {
        string[] supportedFilters = await adapter.GetDiscoveryFiltersAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        var filter = new Dictionary<string, object>();
        if (supportedFilters.Contains("Transport", StringComparer.OrdinalIgnoreCase))
        {
            filter["Transport"] = "le";
        }

        if (supportedFilters.Contains("UUIDs", StringComparer.OrdinalIgnoreCase))
        {
            filter["UUIDs"] = new[] { request.ServiceUuid.ToString() };
        }

        await adapter.SetDiscoveryFilterAsync(filter).WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await adapter.StartDiscoveryAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await Task.Delay(request.ScanTimeout ?? DefaultTimeout, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await adapter.StopDiscoveryAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return await adapter.GetDevicesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await adapter.SetDiscoveryFilterAsync(new Dictionary<string, object>()).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<BleCandidate> CreateCandidateAsync(
        LinuxDevice device,
        CancellationToken cancellationToken,
        int? rssi = null)
    {
        var properties = await device.GetPropertiesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        string id = properties.Address ?? device.ObjectPath.ToString();
        string? name = properties.Name ?? properties.Alias;
        int? candidateRssi = rssi ?? properties.Rssi;
        return new BleCandidate(id, name, candidateRssi, properties.AddressType);
    }

    private async Task<BleCandidate?> TryCreateValidatedCandidateAsync(
        LinuxDevice linuxDevice,
        CancellationToken cancellationToken)
    {
        TimeSpan timeout = _options.ScanTimeout ?? DefaultTimeout;

        try
        {
            await linuxDevice.ConnectAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            await linuxDevice.WaitForPropertyValueAsync("Connected", value: true, timeout).WaitAsync(cancellationToken).ConfigureAwait(false);
            await linuxDevice.WaitForPropertyValueAsync("ServicesResolved", value: true, timeout).WaitAsync(cancellationToken).ConfigureAwait(false);

            BleCandidate candidate = await CreateCandidateAsync(linuxDevice, cancellationToken).ConfigureAwait(false);
            await ResolveTransportCharacteristicsAsync(candidate.Name, linuxDevice, cancellationToken).ConfigureAwait(false);

            int rssi;
            try
            {
                rssi = await linuxDevice.GetRSSIAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                rssi = int.MinValue;
            }

            return candidate with { Rssi = rssi };
        }
        catch
        {
            return null;
        }
        finally
        {
            try
            {
                await linuxDevice.DisconnectAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private async Task<LinuxDevice> ResolveLinuxDeviceAsync(string deviceId, CancellationToken cancellationToken)
    {
        Adapter adapter = await GetAdapterAsync(cancellationToken).ConfigureAwait(false);
        LinuxDevice? linuxDevice = await FindDeviceByIdAsync(adapter, deviceId, cancellationToken).ConfigureAwait(false);
        return linuxDevice ?? throw new InvalidOperationException($"Unable to resolve Linux BLE device '{deviceId}'.");
    }

    private async Task<(LinuxGattCharacteristic WriteCharacteristic, LinuxGattCharacteristic NotifyCharacteristic)> ResolveTransportCharacteristicsAsync(
        string? deviceName,
        LinuxDevice linuxDevice,
        CancellationToken cancellationToken)
    {
        var service = await linuxDevice.GetServiceAsync(_options.ServiceUuid.ToString()).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (service == null)
        {
            throw new InvalidOperationException($"BLE device '{deviceName}' does not expose service '{_options.ServiceUuid}'.");
        }

        LinuxGattCharacteristic? writeCharacteristic = await service.GetCharacteristicAsync(_options.WriteCharacteristicUuid.ToString()).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (writeCharacteristic == null)
        {
            throw new InvalidOperationException($"BLE device '{deviceName}' is missing write characteristic '{_options.WriteCharacteristicUuid}'.");
        }

        LinuxGattCharacteristic? notifyCharacteristic = await service.GetCharacteristicAsync(_options.NotifyCharacteristicUuid.ToString()).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (notifyCharacteristic == null)
        {
            throw new InvalidOperationException($"BLE device '{deviceName}' is missing notify characteristic '{_options.NotifyCharacteristicUuid}'.");
        }

        return (writeCharacteristic, notifyCharacteristic);
    }

    private Task OnCharacteristicValueChangedAsync(
        LinuxGattCharacteristic characteristic,
        LinuxGattCharacteristicValueEventArgs args)
    {
        byte[] value = args.Value;
        if (value.Length == 0)
        {
            return Task.CompletedTask;
        }

        try
        {
            BytesReceived?.Invoke(value);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "BLE BytesReceived handler failed.");
        }

        return Task.CompletedTask;
    }

    private (LinuxDevice? Device, LinuxGattCharacteristic? NotifyCharacteristic) TakeAndClearConnectionState()
    {
        lock (_gate)
        {
            var state = (_device, _notifyCharacteristic);
            _device = null;
            _writeCharacteristic = null;
            _notifyCharacteristic = null;
            return state;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
