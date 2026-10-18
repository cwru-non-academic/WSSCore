using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NUnit.Framework;
using Wss.Transports;
using Wss.Transports.Backends;

namespace WSS.BLE.Tests;

[TestFixture, NonParallelizable]
public sealed class FacadeTests
{
    [SetUp, TearDown]
    public void ClearRegistry()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        ((Dictionary<string, IBleNusBackendFactory>)typeof(BleBackendLoader).GetField("Factories", flags)!.GetValue(null)!).Clear();
        ((HashSet<string>)typeof(BleBackendLoader).GetField("Activated", flags)!.GetValue(null)!).Clear();
    }

    [Test]
    public void ConstructorsAndEquivalentRegistrationKeepPublicApi()
    {
        string platform = BleBackendLoader.GetCurrentPlatform();
        Assert.That(platform, Is.EqualTo(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows" : "linux"));
        BleBackendLoader.RegisterFactory(platform, new Factory());
        using var byName = new BleNusTransport("device");
        using var byOptions = new BleNusTransport(BleNusTransport.CreateDefaultOptions("device"));
        BleBackendLoader.RegisterFactory(platform, new Factory());
        Assert.Throws<InvalidOperationException>(() => BleBackendLoader.RegisterFactory(platform, new OtherFactory()));
        Assert.That(byName.IsConnected, Is.False);
        Assert.Throws<ArgumentNullException>(() => new BleNusTransport((BleNusTransportOptions)null!));
        Assert.Throws<ArgumentException>(() => new BleNusTransport(new BleNusTransportOptions()));
        Assert.Throws<ArgumentNullException>(() => byOptions.SendAsync(null!));
    }

    [Test]
    public void MissingBackendReportsDeploymentAndRegistrationRequirements()
    {
        var ex = Assert.Throws<FileNotFoundException>(() => BleBackendLoader.CreateBackendForPlatform(new() { DeviceName = "test" }, "windows"));
        Assert.That(ex!.FileName, Does.EndWith("WSS.Transport.BLE.Windows.dll"));
        Assert.That(ex.Message, Does.Contain("register"));
        Assert.Throws<ArgumentOutOfRangeException>(() => BleBackendLoader.CreateBackendForPlatform(new(), "android"));
    }

    [TestCase(true, "strong")]
    [TestCase(false, "first")]
    public async Task FacadeRanksOnlyAutoSelectionAndForwardsRawBytes(bool autoSelect, string expected)
    {
        using var backend = new Backend();
        using var facade = new BleNusTransport(new() { DeviceName = "configured", AutoSelectDevice = autoSelect }, backend);
        await facade.ConnectAsync();
        Assert.That(backend.Selected!.Id, Is.EqualTo(expected));
        Assert.That(backend.Request!.AutoSelectDevice, Is.EqualTo(autoSelect));
        Assert.That(backend.Request.ServiceUuid, Is.EqualTo(BleNusTransport.DefaultServiceUuid));
        var data = new byte[] { 0, 128, 192, 219, 255 };
        byte[]? received = null;
        facade.BytesReceived += b => received = b;
        await facade.SendAsync(data);
        backend.Receive(data);
        Assert.That(backend.Sent, Is.SameAs(data));
        Assert.That(received, Is.SameAs(data));
        await facade.DisconnectAsync();
        Assert.That(facade.IsConnected, Is.False);
        facade.Dispose();
        Assert.Throws<ObjectDisposedException>(() => facade.SendAsync(data));
    }

    [Test]
    public void CoreAndSerialHaveSingleSharedIdentityWithoutForwarders()
    {
        var ble = typeof(BleNusTransport).Assembly;
        Assert.That(ble.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName, Is.EqualTo(".NETStandard,Version=v2.0"));
        Assert.That(ble.GetTypes().Any(t => t.Name == "SerialPortTransport" || t.Name == "SerialPortTransportOptions"), Is.False);
        Assert.That(ble.GetForwardedTypes(), Is.Empty);
        Assert.That(typeof(SerialPortTransport).Assembly.GetName().Name, Is.EqualTo("WSS.Transport.Serial"));
        Assert.That(typeof(SerialPortTransport).GetInterfaces(), Does.Contain(typeof(ITransport)));
        Assert.That(typeof(ITransport).Assembly.GetName().Name, Is.EqualTo("WSS_Core_Interface"));
        Assert.That(ble.GetReferencedAssemblies().Any(a => a.Name == "System.Runtime.Loader"), Is.False);
    }

    private class Factory : IBleNusBackendFactory { public IBleNusBackend Create(BleNusTransportOptions options) => new Backend(); }
    private sealed class OtherFactory : Factory { }
    private sealed class Backend : IBleNusBackend
    {
        public bool IsConnected { get; private set; }
        public event Action<byte[]>? BytesReceived;
        internal BleDiscoveryRequest? Request;
        internal BleCandidate? Selected;
        internal byte[]? Sent;
        public Task<IReadOnlyList<BleCandidate>> DiscoverAsync(BleDiscoveryRequest request, CancellationToken ct)
        {
            Request = request;
            return Task.FromResult<IReadOnlyList<BleCandidate>>(new[] { new BleCandidate("first", null), new BleCandidate("strong", null, -20), new BleCandidate("weak", null, -80) });
        }
        public Task ConnectAsync(BleCandidate candidate, CancellationToken ct) { Selected = candidate; IsConnected = true; return Task.CompletedTask; }
        public Task SendAsync(byte[] bytes, CancellationToken ct) { Sent = bytes; return Task.CompletedTask; }
        public Task DisconnectAsync(CancellationToken ct) { IsConnected = false; return Task.CompletedTask; }
        public void Receive(byte[] bytes) => BytesReceived?.Invoke(bytes);
        public void Dispose() { IsConnected = false; }
    }
}
