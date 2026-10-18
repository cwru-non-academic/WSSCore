namespace Wss.Transports.Backends;

internal interface IBleNusBackend : IDisposable
{
    bool IsConnected { get; }

    event Action<byte[]>? BytesReceived;

    Task<IReadOnlyList<BleCandidate>> DiscoverAsync(
        BleDiscoveryRequest request,
        CancellationToken cancellationToken);

    Task ConnectAsync(BleCandidate candidate, CancellationToken cancellationToken);

    Task SendAsync(byte[] data, CancellationToken cancellationToken);

    Task DisconnectAsync(CancellationToken cancellationToken);
}

internal interface IBleNusBackendFactory
{
    IBleNusBackend Create(BleNusTransportOptions options);
}

internal interface IBleNativeStackProbe
{
    Task<BleNativeStackProbeResult> ProbeNativeStackAsync(CancellationToken cancellationToken);
}

internal enum BleNativeStackProbeResult
{
    StackAvailable,
    AdapterUnavailable
}

internal sealed record BleCandidate(
    string Id,
    string? Name,
    int? Rssi = null,
    string? AddressType = null);

internal sealed record BleDiscoveryRequest(
    string? DeviceId,
    string? DeviceName,
    Guid ServiceUuid,
    Guid WriteCharacteristicUuid,
    Guid NotifyCharacteristicUuid,
    TimeSpan? ScanTimeout,
    bool AutoSelectDevice);
