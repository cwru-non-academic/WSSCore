using System.Globalization;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using Wss.CoreModule;

namespace Wss.Transports.Backends.Windows;

internal sealed class WindowsBleNusBackend : IBleNusBackend, IBleNativeStackProbe
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly BleNusTransportOptions _options;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly object _gate = new();

    private BluetoothLEDevice? _device;
    private GattDeviceService? _service;
    private GattCharacteristic? _writeCharacteristic;
    private GattCharacteristic? _notifyCharacteristic;
    private bool _disposed;

    internal WindowsBleNusBackend(BleNusTransportOptions options)
    {
        _options = options;
    }

    public bool IsConnected
    {
        get
        {
            lock (_gate)
            {
                return _device != null && _service != null &&
                    _writeCharacteristic != null && _notifyCharacteristic != null;
            }
        }
    }

    public event Action<byte[]>? BytesReceived;

    async Task<BleNativeStackProbeResult> IBleNativeStackProbe.ProbeNativeStackAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        (_, BluetoothError error) = await RunAdvertisementWatcherAsync(
            TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);

        return error switch
        {
            BluetoothError.Success => BleNativeStackProbeResult.StackAvailable,
            BluetoothError.RadioNotAvailable => BleNativeStackProbeResult.AdapterUnavailable,
            _ => throw CreateWatcherStoppedException(error)
        };
    }

    public async Task<IReadOnlyList<BleCandidate>> DiscoverAsync(
        BleDiscoveryRequest request,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        if (!request.AutoSelectDevice && !string.IsNullOrWhiteSpace(request.DeviceId))
        {
            BleCandidate? configuredCandidate = await TryResolveConfiguredIdAsync(
                request.DeviceId!, cancellationToken).ConfigureAwait(false);
            if (configuredCandidate != null)
            {
                return new[] { configuredCandidate };
            }
        }

        IReadOnlyList<AdvertisementCandidate> advertisements = await ScanAsync(
            request.ScanTimeout ?? DefaultTimeout, cancellationToken).ConfigureAwait(false);

        if (!request.AutoSelectDevice)
        {
            AdvertisementCandidate? match;
            if (!string.IsNullOrWhiteSpace(request.DeviceId))
            {
                if (!TryParseBluetoothAddress(request.DeviceId!, out ulong configuredAddress))
                {
                    return Array.Empty<BleCandidate>();
                }

                match = advertisements.FirstOrDefault(item => item.Address == configuredAddress);
            }
            else
            {
                match = advertisements.FirstOrDefault(item =>
                    string.Equals(item.Name, request.DeviceName, StringComparison.OrdinalIgnoreCase));
            }

            return match == null
                ? Array.Empty<BleCandidate>()
                : new[] { match.ToCandidate() };
        }

        var candidates = new List<BleCandidate>();
        foreach (AdvertisementCandidate advertisement in advertisements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!advertisement.ServiceUuids.Contains(request.ServiceUuid))
            {
                continue;
            }

            BleCandidate? candidate = await TryValidateCandidateAsync(
                advertisement, request, cancellationToken).ConfigureAwait(false);
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
        cancellationToken.ThrowIfCancellationRequested();

        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsConnected)
            {
                await ConnectCoreAsync(candidate, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private async Task ConnectCoreAsync(BleCandidate candidate, CancellationToken cancellationToken)
    {

        BluetoothLEDevice device = await CreateDeviceAsync(candidate, cancellationToken).ConfigureAwait(false);
        GattDeviceService? service = null;
        GattCharacteristic? notifyCharacteristic = null;
        bool handlerAttached = false;
        bool notificationsEnabled = false;

        try
        {
            service = await ResolveServiceAsync(device, _options.ServiceUuid, candidate.Name, cancellationToken)
                .ConfigureAwait(false);
            GattCharacteristic writeCharacteristic = await ResolveCharacteristicAsync(
                service,
                _options.WriteCharacteristicUuid,
                GattCharacteristicProperties.Write,
                "write",
                candidate.Name,
                cancellationToken).ConfigureAwait(false);
            notifyCharacteristic = await ResolveCharacteristicAsync(
                service,
                _options.NotifyCharacteristicUuid,
                GattCharacteristicProperties.Notify,
                "notify",
                candidate.Name,
                cancellationToken).ConfigureAwait(false);

            notifyCharacteristic.ValueChanged += OnCharacteristicValueChanged;
            handlerAttached = true;

            GattWriteResult notificationResult = await notifyCharacteristic
                .WriteClientCharacteristicConfigurationDescriptorWithResultAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify)
                .AsTask(cancellationToken).ConfigureAwait(false);
            EnsureSuccess(notificationResult.Status, "enable BLE notifications");
            notificationsEnabled = true;

            lock (_gate)
            {
                _device = device;
                _service = service;
                _writeCharacteristic = writeCharacteristic;
                _notifyCharacteristic = notifyCharacteristic;
            }

            device = null!;
            service = null;
            notifyCharacteristic = null;
        }
        catch
        {
            if (notificationsEnabled && notifyCharacteristic != null)
            {
                await DisableNotificationsBestEffortAsync(notifyCharacteristic, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (handlerAttached && notifyCharacteristic != null)
            {
                notifyCharacteristic.ValueChanged -= OnCharacteristicValueChanged;
            }

            service?.Dispose();
            device?.Dispose();
            throw;
        }
    }

    public async Task SendAsync(byte[] data, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            GattCharacteristic? writeCharacteristic;
            lock (_gate)
            {
                writeCharacteristic = _writeCharacteristic;
            }

            if (writeCharacteristic == null)
            {
                throw new InvalidOperationException("BLE transport is not connected.");
            }

            using var writer = new DataWriter();
            writer.WriteBytes(data);
            IBuffer buffer = writer.DetachBuffer();
            GattWriteResult result = await writeCharacteristic
                .WriteValueWithResultAsync(buffer, GattWriteOption.WriteWithResponse)
                .AsTask(cancellationToken).ConfigureAwait(false);
            EnsureSuccess(result.Status, "write BLE data");
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _sendGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                GattCharacteristic? notifyCharacteristic;
                lock (_gate)
                {
                    notifyCharacteristic = _notifyCharacteristic;
                }

                if (notifyCharacteristic != null)
                {
                    await DisableNotificationsBestEffortAsync(notifyCharacteristic, cancellationToken)
                        .ConfigureAwait(false);
                    notifyCharacteristic.ValueChanged -= OnCharacteristicValueChanged;
                }

                (BluetoothLEDevice? device, GattDeviceService? service, _) = TakeAndClearConnectionState();
                service?.Dispose();
                device?.Dispose();
            }
            finally
            {
                _sendGate.Release();
            }
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connectionGate.Dispose();
        _sendGate.Dispose();
    }

    private static async Task<IReadOnlyList<AdvertisementCandidate>> ScanAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        (IReadOnlyList<AdvertisementCandidate> candidates, BluetoothError error) =
            await RunAdvertisementWatcherAsync(timeout, cancellationToken).ConfigureAwait(false);

        if (error != BluetoothError.Success)
        {
            throw CreateWatcherStoppedException(error);
        }

        return candidates;
    }

    private static async Task<(IReadOnlyList<AdvertisementCandidate> Candidates, BluetoothError Error)>
        RunAdvertisementWatcherAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken)
    {
        var candidates = new Dictionary<ulong, AdvertisementCandidate>();
        var candidatesGate = new object();
        var stopped = new TaskCompletionSource<BluetoothError>(TaskCreationOptions.RunContinuationsAsynchronously);
        BluetoothError error = BluetoothError.Success;
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            if (args.IsAnonymous || args.BluetoothAddress == 0)
            {
                return;
            }

            lock (candidatesGate)
            {
                if (!candidates.TryGetValue(args.BluetoothAddress, out AdvertisementCandidate? candidate))
                {
                    candidate = new AdvertisementCandidate(args.BluetoothAddress, args.BluetoothAddressType);
                    candidates.Add(args.BluetoothAddress, candidate);
                }

                candidate.Update(args);
            }
        }

        void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args) =>
            stopped.TrySetResult(args.Error);

        watcher.Received += OnReceived;
        watcher.Stopped += OnStopped;
        bool started = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            watcher.Start();
            started = true;

            Task delay = Task.Delay(timeout, cancellationToken);
            Task completed = await Task.WhenAny(delay, stopped.Task).ConfigureAwait(false);
            if (completed == stopped.Task)
            {
                error = await stopped.Task.ConfigureAwait(false);
            }
            else
            {
                await delay.ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                if (started && watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
                {
                    watcher.Stop();
                }
            }
            finally
            {
                watcher.Received -= OnReceived;
                watcher.Stopped -= OnStopped;
            }
        }

        lock (candidatesGate)
        {
            return (candidates.Values.ToArray(), error);
        }
    }

    private static InvalidOperationException CreateWatcherStoppedException(BluetoothError error) =>
        new($"Windows BLE advertisement scanning stopped with error '{error}'.");

    private static async Task<BleCandidate?> TryResolveConfiguredIdAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        BluetoothLEDevice? device = null;
        try
        {
            if (TryParseBluetoothAddress(deviceId, out ulong address))
            {
                device = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
                    .AsTask(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                device = await BluetoothLEDevice.FromIdAsync(deviceId)
                    .AsTask(cancellationToken).ConfigureAwait(false);
            }

            return device == null
                ? null
                : CreateCandidate(device.BluetoothAddress, device.BluetoothAddressType, device.Name, rssi: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
        finally
        {
            device?.Dispose();
        }
    }

    private static async Task<BleCandidate?> TryValidateCandidateAsync(
        AdvertisementCandidate advertisement,
        BleDiscoveryRequest request,
        CancellationToken cancellationToken)
    {
        BluetoothLEDevice? device = null;
        GattDeviceService? service = null;
        try
        {
            device = await CreateDeviceAsync(advertisement.Address, advertisement.AddressType, cancellationToken)
                .ConfigureAwait(false);
            service = await TryResolveServiceAsync(device, request.ServiceUuid, cancellationToken).ConfigureAwait(false);
            if (service == null)
            {
                return null;
            }

            GattCharacteristic? writeCharacteristic = await TryResolveCharacteristicAsync(
                service,
                request.WriteCharacteristicUuid,
                GattCharacteristicProperties.Write,
                cancellationToken).ConfigureAwait(false);
            if (writeCharacteristic == null)
            {
                return null;
            }

            GattCharacteristic? notifyCharacteristic = await TryResolveCharacteristicAsync(
                service,
                request.NotifyCharacteristicUuid,
                GattCharacteristicProperties.Notify,
                cancellationToken).ConfigureAwait(false);
            if (notifyCharacteristic == null)
            {
                return null;
            }

            string? name = string.IsNullOrWhiteSpace(advertisement.Name) ? device.Name : advertisement.Name;
            return CreateCandidate(advertisement.Address, advertisement.AddressType, name, advertisement.Rssi);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
        finally
        {
            service?.Dispose();
            device?.Dispose();
        }
    }

    private static async Task<BluetoothLEDevice> CreateDeviceAsync(
        BleCandidate candidate,
        CancellationToken cancellationToken)
    {
        if (!TryParseBluetoothAddress(candidate.Id, out ulong address))
        {
            throw new InvalidOperationException($"Unable to parse Windows BLE address '{candidate.Id}'.");
        }

        BluetoothAddressType? addressType = ParseAddressType(candidate.AddressType);
        return await CreateDeviceAsync(address, addressType, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<BluetoothLEDevice> CreateDeviceAsync(
        ulong address,
        BluetoothAddressType? addressType,
        CancellationToken cancellationToken)
    {
        BluetoothLEDevice? device = addressType.HasValue && addressType != BluetoothAddressType.Unspecified
            ? await BluetoothLEDevice.FromBluetoothAddressAsync(address, addressType.Value)
                .AsTask(cancellationToken).ConfigureAwait(false)
            : await BluetoothLEDevice.FromBluetoothAddressAsync(address)
                .AsTask(cancellationToken).ConfigureAwait(false);

        return device ?? throw new InvalidOperationException(
            $"Unable to create Windows BLE device for address '{FormatBluetoothAddress(address)}'.");
    }

    private static async Task<GattDeviceService> ResolveServiceAsync(
        BluetoothLEDevice device,
        Guid serviceUuid,
        string? deviceName,
        CancellationToken cancellationToken)
    {
        GattDeviceServicesResult result = await device
            .GetGattServicesForUuidAsync(serviceUuid, BluetoothCacheMode.Uncached)
            .AsTask(cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result.Status, "discover BLE services");

        GattDeviceService? selected = TakeFirstServiceAndDisposeOthers(result);
        return selected ?? throw new InvalidOperationException(
            $"BLE device '{deviceName}' does not expose service '{serviceUuid}'.");
    }

    private static async Task<GattDeviceService?> TryResolveServiceAsync(
        BluetoothLEDevice device,
        Guid serviceUuid,
        CancellationToken cancellationToken)
    {
        GattDeviceServicesResult result = await device
            .GetGattServicesForUuidAsync(serviceUuid, BluetoothCacheMode.Uncached)
            .AsTask(cancellationToken).ConfigureAwait(false);
        if (result.Status != GattCommunicationStatus.Success)
        {
            return null;
        }

        return TakeFirstServiceAndDisposeOthers(result);
    }

    private static async Task<GattCharacteristic> ResolveCharacteristicAsync(
        GattDeviceService service,
        Guid characteristicUuid,
        GattCharacteristicProperties requiredProperty,
        string role,
        string? deviceName,
        CancellationToken cancellationToken)
    {
        GattCharacteristicsResult result = await service
            .GetCharacteristicsForUuidAsync(characteristicUuid, BluetoothCacheMode.Uncached)
            .AsTask(cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result.Status, $"discover BLE {role} characteristic");

        GattCharacteristic? characteristic = result.Characteristics.FirstOrDefault(item =>
            (item.CharacteristicProperties & requiredProperty) != 0);
        return characteristic ?? throw new InvalidOperationException(
            $"BLE device '{deviceName}' is missing {role} characteristic '{characteristicUuid}' with '{requiredProperty}' support.");
    }

    private static async Task<GattCharacteristic?> TryResolveCharacteristicAsync(
        GattDeviceService service,
        Guid characteristicUuid,
        GattCharacteristicProperties requiredProperty,
        CancellationToken cancellationToken)
    {
        GattCharacteristicsResult result = await service
            .GetCharacteristicsForUuidAsync(characteristicUuid, BluetoothCacheMode.Uncached)
            .AsTask(cancellationToken).ConfigureAwait(false);
        if (result.Status != GattCommunicationStatus.Success)
        {
            return null;
        }

        return result.Characteristics.FirstOrDefault(item =>
            (item.CharacteristicProperties & requiredProperty) != 0);
    }

    private static async Task DisableNotificationsBestEffortAsync(
        GattCharacteristic notifyCharacteristic,
        CancellationToken cancellationToken)
    {
        try
        {
            GattWriteResult result = await notifyCharacteristic
                .WriteClientCharacteristicConfigurationDescriptorWithResultAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None)
                .AsTask(cancellationToken).ConfigureAwait(false);
            if (result.Status != GattCommunicationStatus.Success)
            {
                Log.Error($"Failed to disable BLE notifications. GATT status: {result.Status}.");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to disable BLE notifications.");
        }
    }

    private void OnCharacteristicValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            using DataReader reader = DataReader.FromBuffer(args.CharacteristicValue);
            if (reader.UnconsumedBufferLength == 0)
            {
                return;
            }

            var value = new byte[checked((int)reader.UnconsumedBufferLength)];
            reader.ReadBytes(value);
            BytesReceived?.Invoke(value);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "BLE BytesReceived handler failed.");
        }
    }

    private (BluetoothLEDevice? Device, GattDeviceService? Service, GattCharacteristic? NotifyCharacteristic)
        TakeAndClearConnectionState()
    {
        lock (_gate)
        {
            var state = (_device, _service, _notifyCharacteristic);
            _device = null;
            _service = null;
            _writeCharacteristic = null;
            _notifyCharacteristic = null;
            return state;
        }
    }

    private static BleCandidate CreateCandidate(
        ulong address,
        BluetoothAddressType addressType,
        string? name,
        int? rssi) =>
        new(FormatBluetoothAddress(address), name, rssi, addressType.ToString());

    private static string FormatBluetoothAddress(ulong address) => address.ToString("X12", CultureInfo.InvariantCulture);

    private static bool TryParseBluetoothAddress(string value, out ulong address)
    {
        address = 0;
        string normalized = value.Trim();
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..];
        }

        normalized = normalized.Replace(":", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        return normalized.Length is > 0 and <= 12 &&
            ulong.TryParse(normalized, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out address);
    }

    private static GattDeviceService? TakeFirstServiceAndDisposeOthers(GattDeviceServicesResult result)
    {
        GattDeviceService? selected = result.Services.FirstOrDefault();
        foreach (GattDeviceService returnedService in result.Services)
        {
            if (!ReferenceEquals(returnedService, selected))
            {
                returnedService.Dispose();
            }
        }

        return selected;
    }

    private static BluetoothAddressType? ParseAddressType(string? value) =>
        Enum.TryParse(value, ignoreCase: true, out BluetoothAddressType addressType) ? addressType : null;

    private static void EnsureSuccess(GattCommunicationStatus status, string operation)
    {
        if (status != GattCommunicationStatus.Success)
        {
            throw new InvalidOperationException($"Unable to {operation}. GATT status: {status}.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class AdvertisementCandidate
    {
        internal AdvertisementCandidate(ulong address, BluetoothAddressType addressType)
        {
            Address = address;
            AddressType = addressType;
        }

        internal ulong Address { get; }

        internal BluetoothAddressType AddressType { get; private set; }

        internal string? Name { get; private set; }

        internal int? Rssi { get; private set; }

        internal HashSet<Guid> ServiceUuids { get; } = new();

        internal void Update(BluetoothLEAdvertisementReceivedEventArgs args)
        {
            if (args.BluetoothAddressType != BluetoothAddressType.Unspecified)
            {
                AddressType = args.BluetoothAddressType;
            }

            if (!string.IsNullOrWhiteSpace(args.Advertisement.LocalName))
            {
                Name = args.Advertisement.LocalName;
            }

            if (args.RawSignalStrengthInDBm != -127)
            {
                Rssi = args.RawSignalStrengthInDBm;
            }

            foreach (Guid serviceUuid in args.Advertisement.ServiceUuids)
            {
                ServiceUuids.Add(serviceUuid);
            }
        }

        internal BleCandidate ToCandidate() => CreateCandidate(Address, AddressType, Name, Rssi);
    }
}
