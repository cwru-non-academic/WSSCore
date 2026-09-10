using Wss.Transports.Backends;

namespace Wss.Transports.Backends.Windows;

internal sealed class WindowsBleNusBackendFactory : IBleNusBackendFactory
{
    public IBleNusBackend Create(BleNusTransportOptions options) => new WindowsBleNusBackend(options);
}
