using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Linux.Bluetooth;
using Linux.Bluetooth.Extensions;
using InTheHand.Bluetooth;
using Wss.CoreModule;
using IHBluetooth = InTheHand.Bluetooth.Bluetooth;
using IHBluetoothDevice = InTheHand.Bluetooth.BluetoothDevice;
using IHGattCharacteristic = InTheHand.Bluetooth.GattCharacteristic;
using IHGattCharacteristicProperties = InTheHand.Bluetooth.GattCharacteristicProperties;
using IHGattCharacteristicValueChangedEventArgs = InTheHand.Bluetooth.GattCharacteristicValueChangedEventArgs;
using IHGattService = InTheHand.Bluetooth.GattService;
using IHRemoteGattServer = InTheHand.Bluetooth.RemoteGattServer;
using LinuxDevice = Linux.Bluetooth.Device;
using LinuxGattCharacteristic = Linux.Bluetooth.GattCharacteristic;
using LinuxGattCharacteristicValueEventArgs = Linux.Bluetooth.GattCharacteristicValueEventArgs;

namespace HFI.Wss;

/// <summary>
/// BLE transport backed by the Nordic UART Service (NUS).
/// </summary>
/// <remarks>
/// This is a low-level byte transport only. Outbound messages are written with response
/// to the NUS RX characteristic, inbound messages arrive as notifications from the NUS
/// TX characteristic, and writes are serialized so only one GATT write is in flight at a time.
/// Device selection follows <see cref="BleNusTransportOptions.AutoSelectDevice"/>:
/// when enabled, the transport scans for compatible BLE devices and chooses the best
/// valid candidate; otherwise it requires an explicit <see cref="BleNusTransportOptions.DeviceId"/>
/// or <see cref="BleNusTransportOptions.DeviceName"/>.
/// </remarks>
public sealed class BleNusTransport : ITransport, IDisposable
{
    /// <summary>
    /// Gets the default Nordic UART Service UUID used to discover compatible BLE devices.
    /// </summary>
    public static readonly Guid DefaultServiceUuid = Guid.Parse("6E400001-B5A3-F393-E0A9-E50E24DCCA9E");

    /// <summary>
    /// Gets the default Nordic UART Service write characteristic UUID used for outbound bytes.
    /// </summary>
    public static readonly Guid DefaultWriteCharacteristicUuid = Guid.Parse("6E400002-B5A3-F393-E0A9-E50E24DCCA9E");

    /// <summary>
    /// Gets the default Nordic UART Service notification characteristic UUID used for inbound bytes.
    /// </summary>
    public static readonly Guid DefaultNotifyCharacteristicUuid = Guid.Parse("6E400003-B5A3-F393-E0A9-E50E24DCCA9E");

    private readonly BleNusTransportOptions _options;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly object _gate = new();

    private IHBluetoothDevice? _device;
    private IHRemoteGattServer? _gatt;
    private IHGattCharacteristic? _writeCharacteristic;
    private IHGattCharacteristic? _notifyCharacteristic;
    private LinuxDevice? _linuxDevice;
    private LinuxGattCharacteristic? _linuxWriteCharacteristic;
    private LinuxGattCharacteristic? _linuxNotifyCharacteristic;
    private bool _disposed;

    private enum BlePlatformKind
    {
        Linux,
        Windows,
        Generic
    }

    /// <summary>
    /// Creates a BLE transport from a fully specified options object.
    /// </summary>
    /// <param name="options">BLE transport configuration including device selection and required UUIDs.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when the supplied options are invalid.</exception>
    public BleNusTransport(BleNusTransportOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    /// <summary>
    /// Creates a BLE transport that connects to a device with the specified advertised name.
    /// </summary>
    /// <param name="deviceName">Exact BLE device name to scan for.</param>
    public BleNusTransport(string deviceName)
        : this(new BleNusTransportOptions { DeviceName = deviceName })
    {
    }

    /// <summary>
    /// Gets whether the transport currently has an active BLE session and both required GATT characteristics.
    /// </summary>
    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                if (GetPlatformKind() == BlePlatformKind.Linux)
                {
                    return _linuxDevice != null && _linuxWriteCharacteristic != null && _linuxNotifyCharacteristic != null;
                }

                return _gatt != null && _writeCharacteristic != null && _notifyCharacteristic != null;
            }
        }
    }

    /// <summary>
    /// Raised when raw bytes are received from the BLE notification characteristic.
    /// </summary>
    /// <remarks>
    /// Handlers may be invoked on a background thread supplied by the underlying BLE stack.
    /// Marshal to the required context before touching thread-affine state.
    /// </remarks>
    public event Action<byte[]>? BytesReceived;

    /// <summary>
    /// Asynchronously scans for and connects to the configured BLE device.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the BLE session and required characteristics are ready.</returns>
    /// <exception cref="ObjectDisposedException">Thrown when the transport has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Thrown when no compatible BLE device can be resolved or the required service and characteristics are unavailable.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        if (IsConnected)
        {
            return;
        }

        if (_options.AutoSelectDevice)
        {
            IHBluetoothDevice? device = await SelectAutoDeviceAsync(ct).ConfigureAwait(false);
            if (device == null)
            {
                throw new InvalidOperationException("Unable to find a compatible BLE device exposing the required service and characteristics.");
            }

            await ConnectDeviceAsync(device, ct).ConfigureAwait(false);
            return;
        }

        IHBluetoothDevice configuredDevice = await ResolveConfiguredDeviceAsync(ct).ConfigureAwait(false);
        await ConnectDeviceAsync(configuredDevice, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Asynchronously disconnects the active BLE session and stops inbound notifications.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when platform-specific BLE resources have been released.</returns>
    /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        switch (GetPlatformKind())
        {
            case BlePlatformKind.Linux:
                await DisconnectLinuxAsync(ct).ConfigureAwait(false);
                return;

            case BlePlatformKind.Windows:
                await DisconnectWindowsAsync(ct).ConfigureAwait(false);
                return;

            default:
                await DisconnectGenericAsync(ct).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>
    /// Sends a block of raw bytes over the BLE write characteristic.
    /// </summary>
    /// <param name="data">Raw payload bytes to write.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the write has been handed off to the platform BLE stack.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="data"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">Thrown when the transport has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the transport is not connected.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
    /// <remarks>
    /// Writes are serialized so only one GATT write is in flight at a time.
    /// </remarks>
    public async Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        ArgumentNullException.ThrowIfNull(data);

        if (GetPlatformKind() == BlePlatformKind.Linux)
        {
            LinuxGattCharacteristic? linuxWriteCharacteristic;
            lock (_gate)
            {
                linuxWriteCharacteristic = _linuxWriteCharacteristic;
            }

            if (linuxWriteCharacteristic == null)
            {
                throw new InvalidOperationException("BLE transport is not connected.");
            }

            await _sendGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await linuxWriteCharacteristic.WriteValueAsync(data, new Dictionary<string, object>
                {
                    ["type"] = "request"
                }).WaitAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _sendGate.Release();
            }

            return;
        }

        IHGattCharacteristic? writeCharacteristic;
        lock (_gate)
        {
            writeCharacteristic = _writeCharacteristic;
        }

        if (writeCharacteristic == null)
        {
            throw new InvalidOperationException("BLE transport is not connected.");
        }

        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (writeCharacteristic.Properties.HasFlag(IHGattCharacteristicProperties.WriteWithoutResponse))
            {
                await writeCharacteristic.WriteValueWithoutResponseAsync(data).WaitAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await writeCharacteristic.WriteValueWithResponseAsync(data).WaitAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>
    /// Disconnects the transport and releases managed resources.
    /// </summary>
    /// <remarks>
    /// This method performs a best-effort synchronous disconnect and logs disconnect failures instead of rethrowing them.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            DisconnectAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error disposing BLE transport.");
        }

        _sendGate.Dispose();
    }

    private async Task<IHBluetoothDevice> ResolveConfiguredDeviceAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_options.DeviceId))
        {
            IHBluetoothDevice? device = await IHBluetoothDevice.FromIdAsync(_options.DeviceId!).WaitAsync(ct).ConfigureAwait(false);
            return device ?? throw new InvalidOperationException($"Unable to find BLE device with id '{_options.DeviceId}'.");
        }

        IHBluetoothDevice? namedDevice = await ScanForNamedDeviceAsync(ct).ConfigureAwait(false);
        return namedDevice ?? throw new InvalidOperationException($"Unable to find BLE device matching '{_options.DeviceName}'.");
    }

    private async Task<IHBluetoothDevice?> ScanForNamedDeviceAsync(CancellationToken ct)
    {
        var filter = new BluetoothLEScanFilter
        {
            Name = _options.DeviceName!
        };
        filter.Services.Add(_options.ServiceUuid);

        IReadOnlyCollection<IHBluetoothDevice> devices = await ScanForDevicesAsync(filter, ct).ConfigureAwait(false);
        return devices.FirstOrDefault(device => string.Equals(device.Name, _options.DeviceName, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<IHBluetoothDevice?> SelectAutoDeviceAsync(CancellationToken ct)
    {
        var filter = new BluetoothLEScanFilter();
        filter.Services.Add(_options.ServiceUuid);

        IReadOnlyCollection<IHBluetoothDevice> devices = await ScanForDevicesAsync(filter, ct).ConfigureAwait(false);
        var candidates = new List<BleAutoSelectionCandidate>();

        foreach (IHBluetoothDevice device in devices)
        {
            ct.ThrowIfCancellationRequested();

            BleAutoSelectionCandidate? candidate = GetPlatformKind() switch
            {
                BlePlatformKind.Linux => await TryCreateLinuxAutoSelectionCandidateAsync(device, ct).ConfigureAwait(false),
                BlePlatformKind.Windows => await TryCreateWindowsAutoSelectionCandidateAsync(device, ct).ConfigureAwait(false),
                _ => await TryCreateGenericAutoSelectionCandidateAsync(device, ct).ConfigureAwait(false)
            };

            if (candidate != null)
            {
                candidates.Add(candidate);
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.Rssi)
            .Select(candidate => candidate.Device)
            .FirstOrDefault();
    }

    private async Task<IReadOnlyCollection<IHBluetoothDevice>> ScanForDevicesAsync(BluetoothLEScanFilter filter, CancellationToken ct)
    {
        var options = new RequestDeviceOptions
        {
            AcceptAllDevices = false,
            Timeout = _options.ScanTimeout
        };
        options.Filters.Add(filter);

        return await IHBluetooth.ScanForDevicesAsync(options, ct).WaitAsync(ct).ConfigureAwait(false);
    }

    private void OnCharacteristicValueChanged(object? sender, IHGattCharacteristicValueChangedEventArgs args)
    {
        if (args.Error != null)
        {
            Log.Error(args.Error, "BLE notification error.");
            return;
        }

        byte[]? value = args.Value;
        if (value == null || value.Length == 0)
        {
            return;
        }

        try
        {
            BytesReceived?.Invoke(value);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "BLE BytesReceived handler failed.");
        }
    }

    private void OnGattServerDisconnected(object? sender, EventArgs args)
    {
        lock (_gate)
        {
            ClearConnectionStateUnsafe();
        }
    }

    private async Task ConnectDeviceAsync(IHBluetoothDevice device, CancellationToken ct)
    {
        switch (GetPlatformKind())
        {
            case BlePlatformKind.Linux:
                await ConnectLinuxDeviceAsync(device, ct).ConfigureAwait(false);
                return;

            case BlePlatformKind.Windows:
                await ConnectWindowsDeviceAsync(device, ct).ConfigureAwait(false);
                return;

            default:
                await ConnectGenericDeviceAsync(device, ct).ConfigureAwait(false);
                return;
        }
    }

    private async Task ConnectWindowsDeviceAsync(IHBluetoothDevice device, CancellationToken ct)
    {
        await ConnectGenericDeviceAsync(device, ct).ConfigureAwait(false);
    }

    private async Task ConnectGenericDeviceAsync(IHBluetoothDevice device, CancellationToken ct)
    {
        IHRemoteGattServer gatt = device.Gatt ?? throw new InvalidOperationException("Selected BLE device does not expose a GATT server.");

        await gatt.ConnectAsync().WaitAsync(ct).ConfigureAwait(false);
        (IHGattCharacteristic writeCharacteristic, IHGattCharacteristic notifyCharacteristic) =
            await ResolveTransportCharacteristicsAsync(device, gatt, ct).ConfigureAwait(false);

        notifyCharacteristic.CharacteristicValueChanged += OnCharacteristicValueChanged;

        try
        {
            await notifyCharacteristic.StartNotificationsAsync().WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            notifyCharacteristic.CharacteristicValueChanged -= OnCharacteristicValueChanged;
            gatt.Disconnect();
            throw;
        }

        device.GattServerDisconnected += OnGattServerDisconnected;

        lock (_gate)
        {
            _device = device;
            _gatt = gatt;
            _writeCharacteristic = writeCharacteristic;
            _notifyCharacteristic = notifyCharacteristic;
        }
    }

    private async Task DisconnectWindowsAsync(CancellationToken ct)
    {
        await DisconnectGenericAsync(ct).ConfigureAwait(false);
    }

    private async Task DisconnectGenericAsync(CancellationToken ct)
    {
        var (device, gatt, notifyCharacteristic) = TakeAndClearConnectionState();

        if (device != null)
        {
            device.GattServerDisconnected -= OnGattServerDisconnected;
        }

        if (notifyCharacteristic != null)
        {
            notifyCharacteristic.CharacteristicValueChanged -= OnCharacteristicValueChanged;

            try
            {
                await notifyCharacteristic.StopNotificationsAsync().WaitAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to stop BLE notifications.");
            }
        }

        try
        {
            gatt?.Disconnect();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to disconnect BLE transport.");
        }
    }

    private async Task ConnectLinuxDeviceAsync(IHBluetoothDevice device, CancellationToken ct)
    {
        LinuxDevice linuxDevice = await ResolveLinuxDeviceAsync(device.Id, ct).ConfigureAwait(false);
        TimeSpan timeout = _options.ScanTimeout ?? TimeSpan.FromSeconds(10);

        await linuxDevice.ConnectAsync().WaitAsync(ct).ConfigureAwait(false);
        await linuxDevice.WaitForPropertyValueAsync("Connected", value: true, timeout).WaitAsync(ct).ConfigureAwait(false);
        await linuxDevice.WaitForPropertyValueAsync("ServicesResolved", value: true, timeout).WaitAsync(ct).ConfigureAwait(false);

        (LinuxGattCharacteristic writeCharacteristic, LinuxGattCharacteristic notifyCharacteristic) =
            await ResolveLinuxTransportCharacteristicsAsync(device, linuxDevice, ct).ConfigureAwait(false);

        notifyCharacteristic.Value += OnLinuxCharacteristicValueChangedAsync;

        try
        {
            await notifyCharacteristic.StartNotifyAsync().WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            notifyCharacteristic.Value -= OnLinuxCharacteristicValueChangedAsync;
            await linuxDevice.DisconnectAsync().WaitAsync(ct).ConfigureAwait(false);
            throw;
        }

        lock (_gate)
        {
            _device = device;
            _linuxDevice = linuxDevice;
            _linuxWriteCharacteristic = writeCharacteristic;
            _linuxNotifyCharacteristic = notifyCharacteristic;
        }
    }

    private async Task<LinuxDevice> ResolveLinuxDeviceAsync(string deviceId, CancellationToken ct)
    {
        var adapter = (await BlueZManager.GetAdaptersAsync().WaitAsync(ct).ConfigureAwait(false)).FirstOrDefault();
        if (adapter == null)
        {
            throw new InvalidOperationException("No Linux Bluetooth adapter is available.");
        }

        LinuxDevice? linuxDevice = await adapter.GetDeviceAsync(deviceId).WaitAsync(ct).ConfigureAwait(false);
        return linuxDevice ?? throw new InvalidOperationException($"Unable to resolve Linux BLE device '{deviceId}'.");
    }

    private async Task<(LinuxGattCharacteristic WriteCharacteristic, LinuxGattCharacteristic NotifyCharacteristic)> ResolveLinuxTransportCharacteristicsAsync(
        IHBluetoothDevice device,
        LinuxDevice linuxDevice,
        CancellationToken ct)
    {
        var service = await linuxDevice.GetServiceAsync(_options.ServiceUuid.ToString()).WaitAsync(ct).ConfigureAwait(false);
        if (service == null)
        {
            throw new InvalidOperationException($"BLE device '{device.Name}' does not expose service '{_options.ServiceUuid}'.");
        }

        LinuxGattCharacteristic? writeCharacteristic = await service.GetCharacteristicAsync(_options.WriteCharacteristicUuid.ToString()).WaitAsync(ct).ConfigureAwait(false);
        if (writeCharacteristic == null)
        {
            throw new InvalidOperationException($"BLE device '{device.Name}' is missing write characteristic '{_options.WriteCharacteristicUuid}'.");
        }

        LinuxGattCharacteristic? notifyCharacteristic = await service.GetCharacteristicAsync(_options.NotifyCharacteristicUuid.ToString()).WaitAsync(ct).ConfigureAwait(false);
        if (notifyCharacteristic == null)
        {
            throw new InvalidOperationException($"BLE device '{device.Name}' is missing notify characteristic '{_options.NotifyCharacteristicUuid}'.");
        }

        return (writeCharacteristic, notifyCharacteristic);
    }

    private Task OnLinuxCharacteristicValueChangedAsync(LinuxGattCharacteristic characteristic, LinuxGattCharacteristicValueEventArgs args)
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

    private async Task DisconnectLinuxAsync(CancellationToken ct)
    {
        var (device, notifyCharacteristic) = TakeAndClearLinuxConnectionState();

        if (notifyCharacteristic != null)
        {
            notifyCharacteristic.Value -= OnLinuxCharacteristicValueChangedAsync;

            try
            {
                await notifyCharacteristic.StopNotifyAsync().WaitAsync(ct).ConfigureAwait(false);
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
            await device.DisconnectAsync().WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to disconnect BLE transport.");
        }
    }

    private async Task<(IHGattCharacteristic WriteCharacteristic, IHGattCharacteristic NotifyCharacteristic)> ResolveTransportCharacteristicsAsync(
        IHBluetoothDevice device,
        IHRemoteGattServer gatt,
        CancellationToken ct)
    {
        IHGattService? service = await gatt.GetPrimaryServiceAsync(_options.ServiceUuid).WaitAsync(ct).ConfigureAwait(false);
        if (service == null)
        {
            throw new InvalidOperationException($"BLE device '{device.Name}' does not expose service '{_options.ServiceUuid}'.");
        }

        IHGattCharacteristic? writeCharacteristic = await service.GetCharacteristicAsync(_options.WriteCharacteristicUuid).WaitAsync(ct).ConfigureAwait(false);
        if (writeCharacteristic == null)
        {
            throw new InvalidOperationException($"BLE device '{device.Name}' is missing write characteristic '{_options.WriteCharacteristicUuid}'.");
        }

        IHGattCharacteristic? notifyCharacteristic = await service.GetCharacteristicAsync(_options.NotifyCharacteristicUuid).WaitAsync(ct).ConfigureAwait(false);
        if (notifyCharacteristic == null)
        {
            throw new InvalidOperationException($"BLE device '{device.Name}' is missing notify characteristic '{_options.NotifyCharacteristicUuid}'.");
        }

        if (!writeCharacteristic.Properties.HasFlag(IHGattCharacteristicProperties.Write) &&
            !writeCharacteristic.Properties.HasFlag(IHGattCharacteristicProperties.WriteWithoutResponse))
        {
            throw new InvalidOperationException("BLE write characteristic does not support writes.");
        }

        if (!notifyCharacteristic.Properties.HasFlag(IHGattCharacteristicProperties.Notify) &&
            !notifyCharacteristic.Properties.HasFlag(IHGattCharacteristicProperties.Indicate))
        {
            throw new InvalidOperationException("BLE notify characteristic does not support notifications or indications.");
        }

        return (writeCharacteristic, notifyCharacteristic);
    }

    private async Task<BleAutoSelectionCandidate?> TryCreateWindowsAutoSelectionCandidateAsync(IHBluetoothDevice device, CancellationToken ct)
    {
        return await TryCreateGenericAutoSelectionCandidateAsync(device, ct).ConfigureAwait(false);
    }

    private async Task<BleAutoSelectionCandidate?> TryCreateGenericAutoSelectionCandidateAsync(IHBluetoothDevice device, CancellationToken ct)
    {
        IHRemoteGattServer? gatt = device.Gatt;
        if (gatt == null)
        {
            return null;
        }

        try
        {
            await gatt.ConnectAsync().WaitAsync(ct).ConfigureAwait(false);
            await ResolveTransportCharacteristicsAsync(device, gatt, ct).ConfigureAwait(false);

            int rssi;
            try
            {
                rssi = await gatt.ReadRssi().WaitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                rssi = int.MinValue;
            }

            return new BleAutoSelectionCandidate(device, rssi);
        }
        catch
        {
            return null;
        }
        finally
        {
            try
            {
                gatt.Disconnect();
            }
            catch
            {
            }
        }
    }

    private async Task<BleAutoSelectionCandidate?> TryCreateLinuxAutoSelectionCandidateAsync(IHBluetoothDevice device, CancellationToken ct)
    {
        LinuxDevice linuxDevice = await ResolveLinuxDeviceAsync(device.Id, ct).ConfigureAwait(false);
        TimeSpan timeout = _options.ScanTimeout ?? TimeSpan.FromSeconds(10);

        try
        {
            await linuxDevice.ConnectAsync().WaitAsync(ct).ConfigureAwait(false);
            await linuxDevice.WaitForPropertyValueAsync("Connected", value: true, timeout).WaitAsync(ct).ConfigureAwait(false);
            await linuxDevice.WaitForPropertyValueAsync("ServicesResolved", value: true, timeout).WaitAsync(ct).ConfigureAwait(false);
            await ResolveLinuxTransportCharacteristicsAsync(device, linuxDevice, ct).ConfigureAwait(false);

            int rssi;
            try
            {
                rssi = await linuxDevice.GetRSSIAsync().WaitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                rssi = int.MinValue;
            }

            return new BleAutoSelectionCandidate(device, rssi);
        }
        catch
        {
            return null;
        }
        finally
        {
            try
            {
                await linuxDevice.DisconnectAsync().WaitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private (IHBluetoothDevice? Device, IHRemoteGattServer? Gatt, IHGattCharacteristic? NotifyCharacteristic) TakeAndClearConnectionState()
    {
        lock (_gate)
        {
            var state = (_device, _gatt, _notifyCharacteristic);
            ClearConnectionStateUnsafe();
            return state;
        }
    }

    private (LinuxDevice? Device, LinuxGattCharacteristic? NotifyCharacteristic) TakeAndClearLinuxConnectionState()
    {
        lock (_gate)
        {
            var state = (_linuxDevice, _linuxNotifyCharacteristic);
            ClearConnectionStateUnsafe();
            return state;
        }
    }

    private void ClearConnectionStateUnsafe()
    {
        _device = null;
        _gatt = null;
        _writeCharacteristic = null;
        _notifyCharacteristic = null;
        _linuxDevice = null;
        _linuxWriteCharacteristic = null;
        _linuxNotifyCharacteristic = null;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static BlePlatformKind GetPlatformKind()
    {
        if (OperatingSystem.IsLinux())
        {
            return BlePlatformKind.Linux;
        }

        if (OperatingSystem.IsWindows())
        {
            return BlePlatformKind.Windows;
        }

        return BlePlatformKind.Generic;
    }

    /// <summary>
    /// Creates a default options object that resolves a BLE device by advertised name.
    /// </summary>
    /// <param name="deviceName">Exact BLE device name to scan for.</param>
    /// <returns>A new options object with <see cref="BleNusTransportOptions.DeviceName"/> set.</returns>
    public static BleNusTransportOptions CreateDefaultOptions(string deviceName) => new() { DeviceName = deviceName };

    private sealed record BleAutoSelectionCandidate(IHBluetoothDevice Device, int Rssi);
}

/// <summary>
/// Configuration for <see cref="BleNusTransport"/>.
/// </summary>
/// <remarks>
/// When <see cref="AutoSelectDevice"/> is <see langword="false"/>, callers must provide either
/// <see cref="DeviceId"/> or <see cref="DeviceName"/>. Scan-based operations honor <see cref="ScanTimeout"/>
/// when a timeout is configured.
/// </remarks>
public sealed class BleNusTransportOptions
{
    /// <summary>
    /// Explicit BLE device identifier to connect to when <see cref="AutoSelectDevice"/> is disabled.
    /// When provided, it takes precedence over <see cref="DeviceName"/>.
    /// </summary>
    public string? DeviceId { get; init; }

    /// <summary>
    /// Exact BLE device name to scan for when <see cref="AutoSelectDevice"/> is disabled and
    /// <see cref="DeviceId"/> is not provided.
    /// </summary>
    public string? DeviceName { get; init; }

    /// <summary>
    /// Required BLE service UUID. Auto-select only considers devices that expose this service.
    /// </summary>
    public Guid ServiceUuid { get; init; } = BleNusTransport.DefaultServiceUuid;

    /// <summary>
    /// Required BLE write characteristic UUID. Auto-select rejects devices that do not expose it
    /// with write-with-response support.
    /// </summary>
    public Guid WriteCharacteristicUuid { get; init; } = BleNusTransport.DefaultWriteCharacteristicUuid;

    /// <summary>
    /// Required BLE notification characteristic UUID. Auto-select rejects devices that do not expose it
    /// with notify or indicate support.
    /// </summary>
    public Guid NotifyCharacteristicUuid { get; init; } = BleNusTransport.DefaultNotifyCharacteristicUuid;

    /// <summary>
    /// Maximum scan duration used for configured-name lookup and auto-selection.
    /// </summary>
    public TimeSpan? ScanTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// When true, scans for compatible BLE devices and auto-selects the best valid candidate.
    /// Auto-selection only accepts devices that expose the configured service and both required
    /// characteristics with the expected properties, then prefers stronger RSSI.
    /// When false, the caller must provide <see cref="DeviceId"/> or <see cref="DeviceName"/>.
    /// </summary>
    public bool AutoSelectDevice { get; init; }

    internal void Validate()
    {
        if (!AutoSelectDevice && string.IsNullOrWhiteSpace(DeviceId) && string.IsNullOrWhiteSpace(DeviceName))
        {
            throw new ArgumentException("A BLE device id or device name must be provided when AutoSelectDevice is disabled.");
        }

        if (ServiceUuid == Guid.Empty)
        {
            throw new ArgumentException("BLE service UUID must be provided.", nameof(ServiceUuid));
        }

        if (WriteCharacteristicUuid == Guid.Empty)
        {
            throw new ArgumentException("BLE write characteristic UUID must be provided.", nameof(WriteCharacteristicUuid));
        }

        if (NotifyCharacteristicUuid == Guid.Empty)
        {
            throw new ArgumentException("BLE notify characteristic UUID must be provided.", nameof(NotifyCharacteristicUuid));
        }
    }
}
