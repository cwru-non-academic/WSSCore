namespace Wss.Transports;

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
