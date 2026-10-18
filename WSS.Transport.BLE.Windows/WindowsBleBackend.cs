namespace Wss.Transports.Backends.Windows;

public static class WindowsBleBackend
{
    public static void Register() => BleBackendLoader.RegisterFactory("windows", new WindowsBleNusBackendFactory());
}

internal sealed class WindowsBleNusBackendFactory : IBleNusBackendFactory
{
    public IBleNusBackend Create(BleNusTransportOptions options) => new WindowsBleNusBackend(options);
}
