using System.Reflection;
using System.Runtime.Loader;

namespace Wss.Transports.Backends;

internal static class BleBackendLoader
{
    private const string LinuxAssemblyName = "WSS.Transport.BLE.Linux";
    private const string LinuxFactoryTypeName = "Wss.Transports.Backends.Linux.LinuxBleNusBackendFactory";
    private const string WindowsAssemblyName = "WSS.Transport.BLE.Windows";
    private const string WindowsFactoryTypeName = "Wss.Transports.Backends.Windows.WindowsBleNusBackendFactory";

    private static readonly object Gate = new();
    private static IBleNusBackendFactory? _backendFactory;
    private static BleBackendLoadContext? _loadContext;
    private static string? _loadedPlatform;

    internal static IBleNusBackend CreateBackend(BleNusTransportOptions options) =>
        CreateBackendForPlatform(options, GetCurrentPlatform());

    internal static IBleNusBackend CreateBackendForPlatform(BleNusTransportOptions options, string platform)
    {
        lock (Gate)
        {
            if (_loadedPlatform == null)
            {
                _loadedPlatform = platform;
            }
            else if (!string.Equals(_loadedPlatform, platform, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The '{_loadedPlatform}' BLE backend is already loaded in this process; the backend load context cannot be switched to '{platform}'.");
            }

            _backendFactory ??= LoadFactory(platform);
            return _backendFactory.Create(options);
        }
    }

    private static IBleNusBackendFactory LoadFactory(string platform)
    {
        (string platformDirectory, string assemblyName, string factoryTypeName) = GetBackendIdentity(platform);
        string backendPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "backends",
            platformDirectory,
            assemblyName + ".dll"));

        if (!File.Exists(backendPath))
        {
            throw new FileNotFoundException(
                $"The BLE backend for {platformDirectory} was not found at '{backendPath}'.",
                backendPath);
        }

        _loadContext ??= new BleBackendLoadContext(backendPath, typeof(IBleNusBackend).Assembly);
        Assembly backendAssembly = _loadContext.LoadFromAssemblyPath(backendPath);
        Type factoryType = backendAssembly.GetType(factoryTypeName, throwOnError: true)!;
        object factory = Activator.CreateInstance(factoryType, nonPublic: true)
            ?? throw new InvalidOperationException($"Unable to create BLE backend factory '{factoryTypeName}'.");

        return factory as IBleNusBackendFactory
            ?? throw new InvalidCastException(
                $"BLE backend factory '{factoryTypeName}' does not implement the facade backend contract.");
    }

    private static string GetCurrentPlatform()
    {
        if (OperatingSystem.IsLinux())
        {
            return "linux";
        }

        if (OperatingSystem.IsWindows())
        {
            return "windows";
        }

        throw new PlatformNotSupportedException("BLE is supported only on Linux and Windows.");
    }

    private static (string PlatformDirectory, string AssemblyName, string FactoryTypeName) GetBackendIdentity(string platform) =>
        platform switch
        {
            "linux" => ("linux", LinuxAssemblyName, LinuxFactoryTypeName),
            "windows" => ("windows", WindowsAssemblyName, WindowsFactoryTypeName),
            _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, "Unknown BLE backend platform.")
        };

    private sealed class BleBackendLoadContext : AssemblyLoadContext
    {
        private readonly string _backendDirectory;
        private readonly AssemblyDependencyResolver _resolver;
        private readonly Assembly _sharedFacadeAssembly;

        internal BleBackendLoadContext(string backendPath, Assembly sharedFacadeAssembly)
            : base("WSS.Transport.BLE.Backend", isCollectible: false)
        {
            _backendDirectory = Path.GetDirectoryName(backendPath)!;
            _resolver = new AssemblyDependencyResolver(backendPath);
            _sharedFacadeAssembly = sharedFacadeAssembly;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (AssemblyName.ReferenceMatchesDefinition(assemblyName, _sharedFacadeAssembly.GetName()))
            {
                return _sharedFacadeAssembly;
            }

            string? assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
            if (assemblyPath == null && assemblyName.Name != null)
            {
                string adjacentPath = Path.Combine(_backendDirectory, assemblyName.Name + ".dll");
                if (File.Exists(adjacentPath))
                {
                    assemblyPath = adjacentPath;
                }
            }

            return assemblyPath == null ? null : LoadFromAssemblyPath(assemblyPath);
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            string? libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return libraryPath == null ? nint.Zero : LoadUnmanagedDllFromPath(libraryPath);
        }
    }
}
