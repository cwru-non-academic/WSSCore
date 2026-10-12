using System.IO.Ports;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Wss.CoreModule;
using Wss.Transports;

var artifactDirectory = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
    .Single(attribute => attribute.Key == "WssArtifactDirectory").Value!;
foreach (var directory in new[] { artifactDirectory, AppContext.BaseDirectory })
{
    if (Directory.Exists(Path.Combine(directory, "backends")) ||
        Directory.GetFiles(directory, "WSS.Transport.BLE*.dll", SearchOption.AllDirectories).Length != 0)
    {
        throw new InvalidOperationException("Core+Serial compatibility check must not have BLE assemblies/backends available.");
    }
}

var coreOptions = new WssClientOptions();
var configurationOptions = new WssStimulationCoreOptions();
var codec = new WssFrameCodec();
var payload = new byte[] { 0x20, 0x00 };
var frame = codec.Frame((byte)WssTarget.Wss1, 0x02, payload);
var decoded = codec.Deframe(frame).Single();
if (decoded.Length != payload.Length + 3 || decoded[0] != (byte)WssTarget.Wss1 || decoded[1] != 0x02 ||
    !decoded.Skip(2).Take(payload.Length).SequenceEqual(payload) || coreOptions == null || configurationOptions == null)
{
    throw new InvalidOperationException("Core construction/codec compatibility check failed.");
}

if (typeof(WssClientOptions).Assembly.GetName().Name != "WSS_Core_Interface")
{
    throw new InvalidOperationException("Core candidate assembly was not loaded.");
}

var assembly = typeof(SerialPortTransport).Assembly;
if (assembly.GetName().Name != "WSS.Transport.Serial")
{
    throw new InvalidOperationException("Core+Serial candidate assembly was not loaded.");
}

var options = new SerialPortTransportOptions { PortName = "WssConsumerSmokePort" };
using (var transport = new SerialPortTransport(options))
{
    if (transport.IsConnected || options.PortName != "WssConsumerSmokePort" || options.AutoSelectPort ||
        options.Baud != 115200 || options.DataBits != 8 || options.Parity != Parity.None ||
        options.StopBits != StopBits.One || options.ReadTimeoutMs != 10)
    {
        throw new InvalidOperationException("Serial construction/state/options compatibility check failed.");
    }
}

if (AppDomain.CurrentDomain.GetAssemblies().Any(loaded =>
    loaded.GetName().Name!.StartsWith("WSS.Transport.BLE", StringComparison.Ordinal)))
{
    throw new InvalidOperationException("A BLE assembly was loaded by the Core+Serial compatibility check.");
}

var ports = typeof(SerialPort).Assembly;
using var loadedStream = File.OpenRead(ports.Location);
using var candidateStream = File.OpenRead(Path.Combine(artifactDirectory, "System.IO.Ports.dll"));
var usesCandidate = SHA256.HashData(loadedStream).SequenceEqual(SHA256.HashData(candidateStream));
var runtimeFramework = ports.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;
Console.WriteLine("Loaded System.IO.Ports identity: " + ports.FullName);
Console.WriteLine("Loaded System.IO.Ports path: " + ports.Location);
Console.WriteLine("Loaded System.IO.Ports framework: " + runtimeFramework);
Console.WriteLine("Uses candidate Ports bytes: " + usesCandidate);
Console.WriteLine("Supplied by net8 consumer NuGet runtime support: " +
    (!usesCandidate && runtimeFramework == ".NETCoreApp,Version=v8.0"));
Console.WriteLine("Core+Serial assembly/API compatibility check under the .NET host: PASS.");
Console.WriteLine("Packaged net462 Ports implementation in Unity: NOT VERIFIED; real Unity Serial I/O remains required.");
