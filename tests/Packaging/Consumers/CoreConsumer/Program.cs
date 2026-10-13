using Wss.CoreModule;
using Wss.Transports;

var options = new WssClientOptions();
var target = WssTarget.Wss1;
var codec = new WssFrameCodec();
var payload = new byte[] { 0x20, 0x00 };
var wire = codec.Frame((byte)target, 0x02, payload);
var decoded = codec.Deframe(wire).Single();

if (decoded[0] != (byte)target || decoded[1] != 0x02 || decoded[2] != payload[0] || options == null)
{
    throw new InvalidOperationException("WSS frame codec smoke check failed.");
}

using var transport = new TestModeTransport(new TestModeTransportOptions
{
    Rng = new Random(0)
});
using var client = new WssClient(
    transport,
    new WssFrameCodec(),
    new WSSVersionHandler("J03"),
    new WssClientOptions { OwnsTransport = false });
await transport.ConnectAsync();
var result = await client.StartStim(WssTarget.Wss1);

if (result != "Start Acknowledged")
{
    throw new InvalidOperationException("TestModeTransport did not complete the deterministic core operation.");
}

var configDirectory = Path.Combine(Path.GetTempPath(), $"wss-core-consumer-{Guid.NewGuid():N}");
Directory.CreateDirectory(configDirectory);
try
{
    using var coreTransport = new TestModeTransport(new TestModeTransportOptions
    {
        Rng = new Random(0)
    });
    using var core = new WssStimulationCore(
        coreTransport,
        new WssStimulationCoreOptions
        {
            ConfigPath = configDirectory
        });

    if (core is not IAdvancedEventProgrammer)
    {
        throw new InvalidOperationException("Advanced event programming capability is unavailable.");
    }
}
finally
{
    Directory.Delete(configDirectory, recursive: true);
}

Console.WriteLine("Core artifact consumer passed.");
