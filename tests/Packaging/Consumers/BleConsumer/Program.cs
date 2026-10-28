using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Runtime.Versioning;
using Wss.Transports;
using Wss.Transports.Backends;

// Never discover/connect a peripheral or write stimulation payloads.
bool linuxLoadOnly = args.Contains("--linux-load-only");
string platform = linuxLoadOnly ? "linux" : BleBackendLoader.GetCurrentPlatform();
string backendName = platform == "windows" ? "WSS.Transport.BLE.Windows" : platform == "macos" ? "WSS.Transport.BLE.MacOS" : "WSS.Transport.BLE.Linux";
string directory = Path.Combine(AppContext.BaseDirectory, "backends", platform);
if (args.Contains("--register"))
{
    var assembly = Assembly.LoadFrom(Path.Combine(directory, backendName + ".dll"));
    string bootstrap = platform == "windows" ? "Wss.Transports.Backends.Windows.WindowsBleBackend" : platform == "macos" ? "Wss.Transports.Backends.MacOS.MacBleBackend" : "Wss.Transports.Backends.Linux.LinuxBleBackend";
    assembly.GetType(bootstrap, true)!.GetMethod("Register")!.Invoke(null, null);
    assembly.GetType(bootstrap, true)!.GetMethod("Register")!.Invoke(null, null);
}
var options = new BleNusTransportOptions { DeviceName = "universal-load-only" };
using var backend = BleBackendLoader.CreateBackendForPlatform(options, platform);
if (!linuxLoadOnly)
{
    using var facade = new BleNusTransport(options);
    using var byName = new BleNusTransport("universal-load-only");
    if (facade.IsConnected || byName.IsConnected) throw new InvalidOperationException("Construction must not connect hardware.");
    Console.WriteLine("Public BLE constructors without peripheral access: PASS");
}
foreach (var path in Directory.GetFiles(directory, "*.dll"))
{
    if (Path.GetFileName(path) == "WssBleWindows.dll") continue;
    var assembly = Assembly.LoadFrom(path);
    if (AssemblyLoadContext.GetLoadContext(assembly) != AssemblyLoadContext.Default)
        throw new InvalidOperationException("Backend dependencies must use ordinary shared loading: " + path);
    var framework = DistributedFramework(path);
    if (framework?.Contains(".NETCoreApp") == true)
        throw new InvalidOperationException("Platform-managed dependency is not portable: " + path + " " + framework);
    if (assembly.GetName().Name!.StartsWith("WSS") && framework != ".NETStandard,Version=v2.0")
        throw new InvalidOperationException("Nonportable WSS backend: " + path);
    // Host BCL facades can unify; backend/non-BCL assemblies must come from the package.
    if (!assembly.GetName().Name!.StartsWith("System.") && Path.GetFullPath(assembly.Location) != Path.GetFullPath(path))
        throw new InvalidOperationException("Dependency resolved outside universal backend tree: " + path);
    Console.WriteLine($"Dependency: {assembly.GetName().Name} ({framework ?? "no TFM attribute"})");
}
var shared = new[] { typeof(ITransport).Assembly, typeof(SerialPortTransport).Assembly, typeof(BleNusTransport).Assembly };
foreach (var assembly in shared)
{
    if (assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName != ".NETStandard,Version=v2.0")
        throw new InvalidOperationException("WSS assembly must target Standard 2.0: " + assembly.FullName);
    if (AssemblyLoadContext.GetLoadContext(assembly) != AssemblyLoadContext.Default ||
        AppDomain.CurrentDomain.GetAssemblies().Count(a => a.GetName().Name == assembly.GetName().Name) != 1)
        throw new InvalidOperationException("Duplicate/nonshared WSS identity: " + assembly.FullName);
    if (Directory.GetFiles(AppContext.BaseDirectory, assembly.GetName().Name + ".dll", SearchOption.AllDirectories).Length != 1)
        throw new InvalidOperationException("Duplicate staged WSS DLL: " + assembly.FullName);
}
if (typeof(BleNusTransport).Assembly.GetForwardedTypes().Length != 0 || typeof(SerialPortTransport).Assembly.GetName().Name != "WSS.Transport.Serial")
    throw new InvalidOperationException("Serial must be owned directly by its shared assembly without forwarding.");
if (typeof(BleNusTransport).Assembly.GetTypes().Any(t => t.Name == "SerialPortTransport" || t.Name == "SerialPortTransportOptions"))
    throw new InvalidOperationException("BLE still defines duplicate Serial types.");
using (var serial = new SerialPortTransport(new() { PortName = "never-open", AutoSelectPort = false }))
    if (serial.IsConnected) throw new InvalidOperationException("Serial construction must not open hardware.");
Console.WriteLine("Shared Core/Serial/BLE identities and direct Serial ownership: PASS");
if (linuxLoadOnly)
{
    Console.WriteLine("Linux dependency activation: PASS; Linux BlueZ/runtime operation NOT EXECUTED on Windows.");
    return;
}
string unusedBackend = platform == "windows" ? "WSS.Transport.BLE.Linux" : "WSS.Transport.BLE.Windows";
if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == unusedBackend || a.GetName().Name is "WinRT.Runtime" or "Microsoft.Windows.SDK.NET"))
    throw new InvalidOperationException("Unused/obsolete platform backend was activated.");
if (platform == "windows")
{
    var module = NativeLibrary.Load(Path.Combine(directory, "WssBleWindows.dll"));
    try
    {
        if (NativeLibrary.TryGetExport(module, "wss_test_mode", out _)) throw new InvalidOperationException("Test shim must not be distributed.");
        foreach (string export in new[] { "wss_ble_get_abi_info", "wss_ble_nus_connect", "wss_ble_nus_write", "wss_ble_nus_disconnect" })
            NativeLibrary.GetExport(module, export);
        var abi = Marshal.GetDelegateForFunctionPointer<GetAbiInfo>(NativeLibrary.GetExport(module, "wss_ble_get_abi_info"));
        var info = Marshal.AllocHGlobal(32);
        try
        {
            for (int i = 0; i < 32; i += 4) Marshal.WriteInt32(info, i, 0);
            Marshal.WriteInt32(info, 32);
            if (abi(info) != 0 || Marshal.ReadInt32(info, 4) != 1 || Marshal.ReadInt32(info, 8) != 1 ||
                Marshal.ReadInt32(info, 12) != 8 || Marshal.ReadInt32(info, 16) != 56 || Marshal.ReadInt32(info, 20) != 48 ||
                (Marshal.ReadInt32(info, 24) & 2) == 0 || Marshal.ReadInt32(info, 28) != 0)
                throw new InvalidOperationException("Production native ABI 1.1 layout/capability mismatch.");
            Console.WriteLine("Production native ABI 1.1/x64/NUS capability: PASS");
        }
        finally { Marshal.FreeHGlobal(info); }
    }
    finally { NativeLibrary.Free(module); }
}
if (platform == "linux" && Environment.GetEnvironmentVariable("WSS_BLE_SKIP_NATIVE_PROBE") == "1")
{
    Console.WriteLine("Linux dependency checks: PASS; controller probe explicitly skipped (not runtime success).");
    return;
}
if (platform == "macos")
{
    Assembly.LoadFrom(Path.Combine(directory, backendName + ".dll"))
        .GetType("Wss.Transports.Backends.MacOS.MacBleBackend", true)!.GetMethod("VerifyNativeAbi")!.Invoke(null, null);
    // Mandatory native load and ABI query. Hosted CI deliberately does not create
    // CBCentralManager or trigger a TCC prompt. Radio operation is not proved here.
    var module = NativeLibrary.Load(Path.Combine(directory, "libwss_macos_ble.dylib"));
    try
    {
        foreach (string export in new[] { "wss_mac_abi", "wss_mac_create", "wss_mac_destroy", "wss_mac_submit", "wss_mac_cancel" }) NativeLibrary.GetExport(module, export);
        var abi = Marshal.GetDelegateForFunctionPointer<MacAbi>(NativeLibrary.GetExport(module, "wss_mac_abi"));
        if (abi() != 0x10000) throw new InvalidOperationException("macOS ABI mismatch.");
    }
    finally { NativeLibrary.Free(module); }
    Console.WriteLine("macOS managed backend/dependency/native ABI loading: PASS. Bluetooth authorization, adapter and peripheral I/O: NOT EXECUTED.");
    return;
}
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
var probe = await ((IBleNativeStackProbe)backend).ProbeNativeStackAsync(timeout.Token);
Console.WriteLine($"{platform} native stack probe: {probe}");
Console.WriteLine("Universal consumer: PASS (no peripheral connection or writes)");

static string? DistributedFramework(string path)
{
    using var file = File.OpenRead(path);
    using var pe = new PEReader(file);
    var metadata = pe.GetMetadataReader();
    foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
    {
        var attribute = metadata.GetCustomAttribute(handle);
        if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
        var constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
        if (constructor.Parent.Kind != HandleKind.TypeReference) continue;
        var type = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
        if (metadata.GetString(type.Name) != nameof(TargetFrameworkAttribute)) continue;
        var blob = metadata.GetBlobReader(attribute.Value);
        if (blob.ReadUInt16() != 1) throw new InvalidOperationException("Invalid framework attribute: " + path);
        return blob.ReadSerializedString();
    }
    return null;
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int GetAbiInfo(IntPtr info);
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate uint MacAbi();
