using System.Reflection;
using System.Runtime.Loader;
using Wss.Transports;
using Wss.Transports.Backends;

const string WindowsPlatform = "Windows";
const string LinuxPlatform = "Linux";

string platform;
string backendAssemblyName;
string backendDirectory;
string[] dependencyAssemblyNames;

if (OperatingSystem.IsWindows())
{
    platform = WindowsPlatform;
    backendAssemblyName = "WSS.Transport.BLE.Windows";
    backendDirectory = "windows";
    dependencyAssemblyNames = ["Microsoft.Windows.SDK.NET", "WinRT.Runtime"];
}
else if (OperatingSystem.IsLinux())
{
    platform = LinuxPlatform;
    backendAssemblyName = "WSS.Transport.BLE.Linux";
    backendDirectory = "linux";
    dependencyAssemblyNames = ["Linux.Bluetooth", "Tmds.DBus"];
}
else
{
    throw new PlatformNotSupportedException("The BLE artifact smoke supports only Windows and Linux.");
}

Console.WriteLine($"Platform: {platform}");
Console.WriteLine();

Assembly facadeAssembly = Assembly.Load("WSS.Transport.BLE");
if (facadeAssembly != typeof(BleNusTransport).Assembly ||
    AssemblyLoadContext.GetLoadContext(facadeAssembly) != AssemblyLoadContext.Default)
{
    throw new InvalidOperationException("The BLE facade did not load in the default assembly load context.");
}

Console.WriteLine("Facade load: PASS");

var options = new BleNusTransportOptions { DeviceName = "WssConsumerSmokeDevice" };
using IBleNusBackend backend = BleBackendLoader.CreateBackend(options);

Assembly backendAssembly = AppDomain.CurrentDomain.GetAssemblies()
    .Single(assembly => assembly.GetName().Name == backendAssemblyName);
AssemblyLoadContext backendLoadContext = AssemblyLoadContext.GetLoadContext(backendAssembly)
    ?? throw new InvalidOperationException($"The {platform} BLE backend has no load context.");

string expectedBackendDirectory = Path.GetFullPath(
    Path.Combine(AppContext.BaseDirectory, "backends", backendDirectory));
string actualBackendDirectory = Path.GetFullPath(Path.GetDirectoryName(backendAssembly.Location)!);

if (backend.IsConnected ||
    options.ServiceUuid == Guid.Empty ||
    !string.Equals(actualBackendDirectory, expectedBackendDirectory, StringComparison.OrdinalIgnoreCase) ||
    ReferenceEquals(backendLoadContext, AssemblyLoadContext.Default) ||
    backendLoadContext.IsCollectible ||
    backendLoadContext.Assemblies.Any(assembly => assembly.GetName().Name == "WSS.Transport.BLE"))
{
    throw new InvalidOperationException($"The {platform} BLE backend activation check failed.");
}

Console.WriteLine($"{platform} backend load: PASS");

foreach (string dependencyAssemblyName in dependencyAssemblyNames)
{
    Assembly dependencyAssembly = backendLoadContext.LoadFromAssemblyName(new AssemblyName(dependencyAssemblyName));
    string expectedDependencyPath = Path.Combine(expectedBackendDirectory, dependencyAssemblyName + ".dll");

    if (AssemblyLoadContext.GetLoadContext(dependencyAssembly) != backendLoadContext ||
        !string.Equals(
            Path.GetFullPath(dependencyAssembly.Location),
            Path.GetFullPath(expectedDependencyPath),
            StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            $"Dependency '{dependencyAssemblyName}' did not resolve from the {platform} backend tree.");
    }

    Console.WriteLine($"{dependencyAssemblyName} resolution: PASS");
}

if (backend is not IBleNativeStackProbe nativeStackProbe)
{
    throw new InvalidCastException($"The {platform} BLE backend does not implement the internal native-stack diagnostic contract.");
}

if (platform == LinuxPlatform &&
    string.Equals(Environment.GetEnvironmentVariable("WSS_BLE_SKIP_NATIVE_PROBE"), "1", StringComparison.Ordinal))
{
    Console.WriteLine("Bluetooth adapter/management interface: NOT AVAILABLE (acceptable)");
    Console.WriteLine("Native BLE hardware probe: SKIPPED - no controller");
    return;
}

using var probeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
BleNativeStackProbeResult probeResult = await nativeStackProbe.ProbeNativeStackAsync(probeTimeout.Token);

Console.WriteLine(platform == WindowsPlatform
    ? "Native WinRT BLE API touch: PASS"
    : "BlueZ/D-Bus API touch: PASS");

Console.WriteLine(probeResult switch
{
    BleNativeStackProbeResult.StackAvailable => "Bluetooth adapter: AVAILABLE",
    BleNativeStackProbeResult.AdapterUnavailable => "Bluetooth adapter: NOT AVAILABLE (acceptable)",
    _ => throw new InvalidOperationException($"Unknown native-stack probe result '{probeResult}'.")
});
