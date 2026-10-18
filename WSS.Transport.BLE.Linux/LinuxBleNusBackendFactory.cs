using Wss.Transports.Backends;

namespace Wss.Transports.Backends.Linux;

internal sealed class LinuxBleNusBackendFactory : IBleNusBackendFactory
{
    public IBleNusBackend Create(BleNusTransportOptions options) => new LinuxBleNusBackend(options);
}
