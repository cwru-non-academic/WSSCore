using Wss.CoreModule;
using Wss.Transports.Backends;

namespace Wss.Transports;

/// <summary>
/// BLE transport backed by the Nordic UART Service (NUS).
/// </summary>
/// <remarks>
/// This is a low-level byte transport only. Outbound messages are written with response
/// to the NUS RX characteristic, inbound messages arrive as notifications from the NUS
/// TX characteristic, and writes are serialized so only one GATT write is in flight at a time.
/// Device selection follows <see cref="BleNusTransportOptions.AutoSelectDevice"/>:
/// when enabled, the transport discovers compatible BLE devices and chooses the strongest
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
    private readonly IBleNusBackend _backend;
    private bool _disposed;

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
        _backend = BleBackendLoader.CreateBackend(_options);
    }

    /// <summary>
    /// Creates a BLE transport that connects to a device with the specified advertised name.
    /// </summary>
    /// <param name="deviceName">Exact BLE device name to discover.</param>
    public BleNusTransport(string deviceName)
        : this(new BleNusTransportOptions { DeviceName = deviceName })
    {
    }

    /// <summary>
    /// Gets whether the transport currently has an active BLE session and both required GATT characteristics.
    /// </summary>
    public bool IsConnected => _backend.IsConnected;

    /// <summary>
    /// Raised when raw bytes are received from the BLE notification characteristic.
    /// </summary>
    /// <remarks>
    /// Handlers may be invoked on a background thread supplied by the underlying BLE stack.
    /// Marshal to the required context before touching thread-affine state.
    /// </remarks>
    public event Action<byte[]>? BytesReceived
    {
        add => _backend.BytesReceived += value;
        remove => _backend.BytesReceived -= value;
    }

    /// <summary>
    /// Asynchronously discovers and connects to the configured BLE device.
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

        var request = new BleDiscoveryRequest(
            _options.DeviceId,
            _options.DeviceName,
            _options.ServiceUuid,
            _options.WriteCharacteristicUuid,
            _options.NotifyCharacteristicUuid,
            _options.ScanTimeout,
            _options.AutoSelectDevice);

        IReadOnlyList<BleCandidate> candidates = await _backend.DiscoverAsync(request, ct).ConfigureAwait(false);
        BleCandidate? candidate = _options.AutoSelectDevice
            ? candidates.OrderByDescending(item => item.Rssi ?? int.MinValue).FirstOrDefault()
            : candidates.FirstOrDefault();

        if (candidate == null)
        {
            if (_options.AutoSelectDevice)
            {
                throw new InvalidOperationException("Unable to find a compatible BLE device exposing the required service and characteristics.");
            }

            if (!string.IsNullOrWhiteSpace(_options.DeviceId))
            {
                throw new InvalidOperationException($"Unable to find BLE device with id '{_options.DeviceId}'.");
            }

            throw new InvalidOperationException($"Unable to find BLE device matching '{_options.DeviceName}'.");
        }

        await _backend.ConnectAsync(candidate, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Asynchronously disconnects the active BLE session and stops inbound notifications.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when platform-specific BLE resources have been released.</returns>
    public Task DisconnectAsync(CancellationToken ct = default) => _backend.DisconnectAsync(ct);

    /// <summary>
    /// Sends a block of raw bytes over the BLE write characteristic.
    /// </summary>
    /// <param name="data">Raw payload bytes to write.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the platform BLE stack completes the write.</returns>
    public Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(data);
        return _backend.SendAsync(data, ct);
    }

    /// <summary>
    /// Disconnects the transport and releases managed resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _backend.DisconnectAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error disposing BLE transport.");
        }

        _backend.Dispose();
    }

    /// <summary>
    /// Creates a default options object that resolves a BLE device by advertised name.
    /// </summary>
    /// <param name="deviceName">Exact BLE device name to discover.</param>
    /// <returns>A new options object with <see cref="BleNusTransportOptions.DeviceName"/> set.</returns>
    public static BleNusTransportOptions CreateDefaultOptions(string deviceName) => new() { DeviceName = deviceName };

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
