using System.Reflection;
using System.Runtime.InteropServices;

namespace Wss.Transports.Backends;

internal static class BleBackendLoader
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, IBleNusBackendFactory> Factories = new();
    private static readonly HashSet<string> Activated = new();

    internal static void RegisterFactory(string platform, IBleNusBackendFactory factory)
    {
        GetBackendIdentity(platform);
        if (factory == null) throw new ArgumentNullException(nameof(factory));
        lock (Gate)
        {
            if (Factories.TryGetValue(platform, out var existing))
            {
                if (existing.GetType() == factory.GetType()) return;
                if (Activated.Contains(platform))
                    throw new InvalidOperationException($"The '{platform}' BLE backend is already activated; its factory cannot be replaced.");
            }
            Factories[platform] = factory;
        }
    }

    internal static IBleNusBackend CreateBackend(BleNusTransportOptions options) =>
        CreateBackendForPlatform(options, GetCurrentPlatform());

    internal static IBleNusBackend CreateBackendForPlatform(BleNusTransportOptions options, string platform)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        GetBackendIdentity(platform);
        lock (Gate)
        {
            if (!Factories.TryGetValue(platform, out var factory))
                Factories[platform] = factory = LoadFactory(platform);
            var backend = factory.Create(options);
            Activated.Add(platform);
            return backend;
        }
    }

    internal static string GetCurrentPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "linux";
        throw new PlatformNotSupportedException("BLE is supported only on Linux and Windows.");
    }

    private static IBleNusBackendFactory LoadFactory(string platform)
    {
        var (assemblyName, factoryName) = GetBackendIdentity(platform);
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "backends", platform, assemblyName + ".dll"));
        Assembly? assembly = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == assemblyName);
        if (assembly == null && !File.Exists(path))
            throw new FileNotFoundException($"The BLE backend for {platform} was not found at '{path}'. Deploy its dependencies or register the platform backend explicitly.", path);
        try
        {
            assembly = assembly ?? Assembly.LoadFrom(path);
            var type = assembly.GetType(factoryName, throwOnError: true)!;
            return Activator.CreateInstance(type, nonPublic: true) as IBleNusBackendFactory
                ?? throw new InvalidCastException($"Factory '{factoryName}' does not implement the shared BLE contract.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Unable to activate the {platform} BLE backend '{assemblyName}'. Check its compatible managed dependencies and shared WSS assemblies.", ex);
        }
    }

    private static (string AssemblyName, string FactoryName) GetBackendIdentity(string platform) => platform switch
    {
        "windows" => ("WSS.Transport.BLE.Windows", "Wss.Transports.Backends.Windows.WindowsBleNusBackendFactory"),
        "linux" => ("WSS.Transport.BLE.Linux", "Wss.Transports.Backends.Linux.LinuxBleNusBackendFactory"),
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, "Unknown BLE backend platform.")
    };
}
