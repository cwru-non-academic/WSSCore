using System.Reflection;
using System.Runtime.Loader;
using Wss.Transports;
using Wss.Transports.Backends;

_ = Assembly.Load("WSS.Transport.BLE");

var options = new BleNusTransportOptions { DeviceName = "WssConsumerSmokeDevice" };
using var backend = BleBackendLoader.CreateBackendForPlatform(options, "linux");

Assembly backendAssembly = AppDomain.CurrentDomain.GetAssemblies()
    .Single(assembly => assembly.GetName().Name == "WSS.Transport.BLE.Linux");
Assembly facadeAssembly = typeof(BleNusTransport).Assembly;
AssemblyLoadContext backendLoadContext = AssemblyLoadContext.GetLoadContext(backendAssembly)
    ?? throw new InvalidOperationException("Linux BLE backend has no load context.");
Assembly linuxBluetoothAssembly = backendLoadContext.LoadFromAssemblyName(new AssemblyName("Linux.Bluetooth"));
Assembly dbusAssembly = backendLoadContext.LoadFromAssemblyName(new AssemblyName("Tmds.DBus"));

if (backend.IsConnected || options.ServiceUuid == Guid.Empty ||
    ReferenceEquals(backendLoadContext, AssemblyLoadContext.Default) ||
    backendLoadContext.IsCollectible ||
    AssemblyLoadContext.GetLoadContext(facadeAssembly) != AssemblyLoadContext.Default ||
    backendLoadContext.Assemblies.Any(assembly => assembly.GetName().Name == "WSS.Transport.BLE") ||
    AssemblyLoadContext.GetLoadContext(linuxBluetoothAssembly) != backendLoadContext ||
    AssemblyLoadContext.GetLoadContext(dbusAssembly) != backendLoadContext)
{
    throw new InvalidOperationException("BLE transport smoke check failed.");
}

Console.WriteLine("BLE artifact consumer passed.");
