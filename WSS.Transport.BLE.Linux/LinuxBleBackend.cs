namespace Wss.Transports.Backends.Linux;

public static class LinuxBleBackend
{
    public static void Register() => BleBackendLoader.RegisterFactory("linux", new LinuxBleNusBackendFactory());
}
