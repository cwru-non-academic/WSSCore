using System.Runtime.InteropServices;
using System.Text;
using NUnit.Framework;
using Wss.Ble.Windows.Native;
using Wss.Ble.Windows.Native.Interop;

namespace WSS.BLE.Windows.Tests;

[TestFixture, NonParallelizable]
public sealed class GattShimTests
{
    private const string Dll = "WssBleWindows.dll";
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] private static extern void wss_test_mode(int mode);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] private static extern BleStatus wss_test_barrier(BleContextHandle context, ulong id);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] private static extern BleStatus wss_test_nus_emit(BleContextHandle context, ulong conn, byte[] bytes, uint length, uint kind, BleStatus status);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] private static extern BleStatus wss_test_nus_release(BleContextHandle context);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] private static extern uint wss_test_nus_observe(BleContextHandle context, uint field, uint index, byte[] bytes, uint capacity);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] private static extern BleStatus wss_test_nus_budget(BleContextHandle context, uint budget);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] private static extern void wss_test_counters(out ulong made, out ulong freed, out ulong callbacks, out ulong alive);
    [OneTimeSetUp]
    public void RequireShim()
    {
        Assert.That(OperatingSystem.IsWindows(), Is.True, "GATT interop verification requires Windows.");
        var path = Path.Combine(TestContext.CurrentContext.TestDirectory, Dll);
        Assert.That(File.Exists(path), Is.True, "Build the native shim first; missing shim is not a pass.");
        var module = NativeLibrary.Load(path);
        try { Assert.That(NativeLibrary.TryGetExport(module, "wss_test_nus_emit", out _), Is.True); }
        finally { NativeLibrary.Free(module); }
    }
    [SetUp] public void Reset() => wss_test_mode(0);
    [TearDown] public void NoLeaks() { wss_test_counters(out _, out _, out _, out var alive); Assert.That(alive, Is.Zero); }

    [TestCase("0xAA:BB:CC:DD:EE:FF", BleAddressType.Random, 1u)]
    [TestCase("aa-bb-cc-dd-ee-ff", BleAddressType.Public, 1u)]
    [TestCase("BluetoothLE#safe-é", BleAddressType.Unspecified, 2u)]
    public async Task SelectionAndDefaultUuidRolesCrossAbiUnchanged(string device, BleAddressType type, uint selector)
    {
        using var client = await Client();
        var info = await client.ConnectAsync(device, type);
        await Barrier(client);
        var bytes = new byte[88]; Assert.That(Observe(client, 3, bytes: bytes), Is.EqualTo(88));
        Assert.Multiple(() =>
        {
            Assert.That(client.IsConnected, Is.True);
            Assert.That(info.ConnectionId, Is.EqualTo(1));
            Assert.That(info.MaxWriteLength, Is.EqualTo(20));
            Assert.That(BitConverter.ToUInt32(bytes, 4), Is.EqualTo(selector));
            Assert.That(BitConverter.ToUInt32(bytes, 8), Is.EqualTo((uint)type));
            Assert.That(bytes[40..56], Is.EqualTo(Convert.FromHexString("6E400001B5A3F393E0A9E50E24DCCA9E")));
            Assert.That(bytes[56..72], Is.EqualTo(Convert.FromHexString("6E400002B5A3F393E0A9E50E24DCCA9E")));
            Assert.That(bytes[72..88], Is.EqualTo(Convert.FromHexString("6E400003B5A3F393E0A9E50E24DCCA9E")));
        });
        if (selector == 1) Assert.That(BitConverter.ToUInt64(bytes, 16), Is.EqualTo(0xAABBCCDDEEFFUL));
        else { var text = new byte[256]; var count = Observe(client, 4, bytes: text); Assert.That(Encoding.UTF8.GetString(text, 0, (int)count), Is.EqualTo(device)); }
        await client.DisconnectAsync(); await Barrier(client);
        Assert.That(client.IsConnected, Is.False); Assert.That(Observe(client, 0), Is.Zero);
    }

    [Test]
    public async Task CustomUuidsAreSnapshottedBeforeLifecycleWait()
    {
        using var client = await Client();
        var options = new BleNusOptions { ServiceUuid = Guid.NewGuid(), WriteCharacteristicUuid = Guid.NewGuid(), NotifyCharacteristicUuid = Guid.NewGuid() };
        var expected = WindowsBleClient.CanonicalUuid(options.ServiceUuid).Concat(WindowsBleClient.CanonicalUuid(options.WriteCharacteristicUuid)).Concat(WindowsBleClient.CanonicalUuid(options.NotifyCharacteristicUuid)).ToArray();
        var connecting = client.ConnectAsync("AABBCCDDEEFF", options: options);
        options.ServiceUuid = options.WriteCharacteristicUuid = options.NotifyCharacteristicUuid = Guid.Empty;
        await connecting; await Barrier(client);
        var bytes = new byte[88]; Observe(client, 3, bytes: bytes);
        Assert.That(bytes[40..88], Is.EqualTo(expected));
        Assert.ThrowsAsync<ArgumentException>(async () => await client.ConnectAsync("AABBCCDDEEFF", options: options));
    }

    [TestCase(20, 2u)] // missing service
    [TestCase(21, 3u)] // missing RX
    [TestCase(22, 4u)] // missing TX
    [TestCase(23, 3u)] // RX only WriteWithoutResponse
    [TestCase(24, 4u)] // TX only Indicate
    [TestCase(25, 5u)] // CCCD rejected
    public async Task PartialConnectionFailureRetainsDiagnosticsAndCleansResources(int mode, uint stage)
    {
        wss_test_mode(mode);
        using var client = await Client();
        var error = Assert.ThrowsAsync<BleBridgeException>(async () => await client.ConnectAsync("AABBCCDDEEFF"));
        Assert.Multiple(() =>
        {
            Assert.That(error!.Status, Is.EqualTo(BleStatus.NativeFailure));
            Assert.That(error.GattStage, Is.EqualTo(stage));
            Assert.That(error.NativeHResult, Is.EqualTo(unchecked((int)0x80004005)));
            Assert.That(client.IsConnected, Is.False);
            Assert.That(Observe(client, 0), Is.Zero);
        });
        if (mode == 25) { Assert.That(error!.GattStatus, Is.EqualTo(2)); Assert.That(error.ProtocolError, Is.EqualTo(3)); }
        Assert.That(client.State.Pending, Is.Empty);
    }

    [Test]
    public async Task NotificationsAreBinaryOrderedCopiesAndGenerationFencedAcrossReconnect()
    {
        using var client = await Client(); var first = await client.ConnectAsync("AABBCCDDEEFF");
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Emit(client, first.ConnectionId, new byte[] { 0, 255, 128, 1 }); Emit(client, first.ConnectionId, new byte[] { 2, 0 });
        await Barrier(client);
        var values = client.DrainNotifications();
        Assert.That(values, Is.EqualTo(new[] { new byte[] { 0, 255, 128, 1 }, new byte[] { 2, 0 } }));
        await client.DisconnectAsync(); Emit(client, first.ConnectionId, new byte[] { 9 }); await Barrier(client);
        Assert.That(client.DrainNotifications(), Is.Empty);
        var second = await client.ConnectAsync("AABBCCDDEEFF");
        Assert.That(second.ConnectionId, Is.GreaterThan(first.ConnectionId));
        Emit(client, first.ConnectionId, new byte[] { 8 }); Emit(client, second.ConnectionId, new byte[] { 7 }); await Barrier(client);
        Assert.That(client.DrainNotifications(), Is.EqualTo(new[] { new byte[] { 7 } }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ManagedNotificationOverflowIsVisibleAndRequiresCleanup(bool bytesLimit)
    {
        using var client = await Client(); var info = await client.ConnectAsync("AABBCCDDEEFF");
        if (bytesLimit) { Emit(client, info.ConnectionId, new byte[700000]); Emit(client, info.ConnectionId, new byte[400000]); }
        else for (var i = 0; i < 257; ++i) Emit(client, info.ConnectionId, new byte[] { (byte)i });
        await Barrier(client);
        Assert.That(client.IsConnected, Is.False);
        Assert.That(client.LastConnectionError!.Status, Is.EqualTo(BleStatus.Overflow));
        Assert.Throws<BleBridgeException>(() => client.DrainNotifications());
        Assert.ThrowsAsync<BleBridgeException>(async () => await client.SendAsync(new byte[] { 1 }));
        await client.DisconnectAsync(); await client.ConnectAsync("AABBCCDDEEFF");
        Assert.That(client.IsConnected, Is.True);
    }

    [TestCase(BleStatus.NativeFailure)]
    [TestCase(BleStatus.Overflow)]
    public async Task NativeTerminationClearsStateAndAllowsFreshConnection(BleStatus status)
    {
        using var client = await Client(); var info = await client.ConnectAsync("AABBCCDDEEFF");
        Emit(client, info.ConnectionId, new byte[] { 1 }); Terminate(client, info.ConnectionId, status); await Barrier(client);
        Assert.That(client.IsConnected, Is.False); Assert.That(client.MaxWriteLength, Is.Zero);
        Assert.That(client.LastConnectionError!.Status, Is.EqualTo(status));
        Assert.That(client.State.Notifications, Is.Empty);
        await client.DisconnectAsync(); var next = await client.ConnectAsync("AABBCCDDEEFF"); Assert.That(next.ConnectionId, Is.GreaterThan(info.ConnectionId));
    }

    [Test]
    public async Task SendsAreSerializedSnapshotsAndBudgetIsCheckedAtEachWrite()
    {
        wss_test_mode(28);
        using var client = await Client(); await client.ConnectAsync("AABBCCDDEEFF");
        var first = client.SendAsync(new byte[] { 1 }); await Barrier(client);
        var bytes = new byte[] { 0, 255, 128 }; var second = client.SendAsync(bytes); Array.Fill(bytes, (byte)9);
        await Barrier(client); Assert.That(Observe(client, 1), Is.EqualTo(1), "Second write must not reach native while first is unresolved.");
        wss_test_nus_release(client.Context); await first;
        var end = DateTime.UtcNow.AddSeconds(5);
        do { await Barrier(client); if (Observe(client, 1) == 2) break; await Task.Delay(1); } while (DateTime.UtcNow < end);
        Assert.That(Observe(client, 1), Is.EqualTo(2));
        var copy = new byte[20]; Assert.That(Observe(client, 2, 1, copy), Is.EqualTo(3)); Assert.That(copy[..3], Is.EqualTo(new byte[] { 0, 255, 128 }));
        wss_test_nus_release(client.Context); await second;
        wss_test_nus_budget(client.Context, 4); await Barrier(client);
        var boundary = client.SendAsync(new byte[4]); await Barrier(client); wss_test_nus_release(client.Context); await boundary;
        Assert.That(client.MaxWriteLength, Is.EqualTo(4));
        var error = Assert.ThrowsAsync<BleBridgeException>(async () => await client.SendAsync(new byte[5])); Assert.That(error!.Status, Is.EqualTo(BleStatus.InvalidArgument));
        var empty = client.SendAsync(Array.Empty<byte>()); await Barrier(client); wss_test_nus_release(client.Context); await empty;
        Assert.That(Observe(client, 2, 3), Is.Zero, "Empty writes still reach native.");
    }

    [Test]
    public async Task WriteFailureRetainsAttDiagnosticsWithoutReplay()
    {
        wss_test_mode(26); using var client = await Client(); await client.ConnectAsync("AABBCCDDEEFF");
        var error = Assert.ThrowsAsync<BleBridgeException>(async () => await client.SendAsync(new byte[] { 1, 2 }));
        Assert.That(error!.GattStage, Is.EqualTo(6)); Assert.That(error.GattStatus, Is.EqualTo(2)); Assert.That(error.ProtocolError, Is.EqualTo(0x0d));
        Assert.That(Observe(client, 1), Is.EqualTo(1)); Assert.That(client.IsConnected, Is.True);
    }

    [Test]
    public async Task CancellationBeforeWriteSubmissionLeavesConnectionUsable()
    {
        using var client = await Client(); await client.ConnectAsync("AABBCCDDEEFF");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.ThrowsAsync<TaskCanceledException>(async () => await client.SendAsync(new byte[] { 1 }, cancelled.Token));
        Assert.That(client.IsConnected, Is.True); Assert.That(Observe(client, 1), Is.Zero);
        await client.SendAsync(new byte[] { 2 }); Assert.That(Observe(client, 1), Is.EqualTo(1));
    }

    [Test]
    public async Task DisconnectTimeoutStillEstablishesCleanupFence()
    {
        wss_test_mode(29); using var client = await Client(TimeSpan.FromMilliseconds(500)); await client.ConnectAsync("AABBCCDDEEFF");
        Assert.ThrowsAsync<TimeoutException>(async () => await client.DisconnectAsync());
        await Barrier(client); Assert.That(client.IsConnected, Is.False); Assert.That(Observe(client, 0), Is.Zero);
        Assert.That(client.State.Pending, Is.Empty);
        await client.ConnectAsync("AABBCCDDEEFF");
    }

    [Test]
    public async Task CccdDisableFailureIsBestEffortButDiagnosticIsRetained()
    {
        wss_test_mode(32); using var client = await Client(); await client.ConnectAsync("AABBCCDDEEFF");
        await client.DisconnectAsync();
        Assert.That(client.IsConnected, Is.False); Assert.That(Observe(client, 0), Is.Zero);
        Assert.That(client.LastConnectionError!.GattStage, Is.EqualTo(7));
        Assert.That(client.LastConnectionError.GattStatus, Is.EqualTo(2)); Assert.That(client.LastConnectionError.ProtocolError, Is.EqualTo(3));
        await client.ConnectAsync("AABBCCDDEEFF"); Assert.That(client.LastConnectionError, Is.Null);
    }

    [Test]
    public async Task LinkLossDuringConnectionActivationCannotPublishReadiness()
    {
        wss_test_mode(27); using var client = await Client();
        var connect = client.ConnectAsync("AABBCCDDEEFF"); await Barrier(client);
        Terminate(client, 1, BleStatus.NativeFailure);
        Assert.ThrowsAsync<TaskCanceledException>(async () => await connect);
        await Barrier(client); Assert.That(client.IsConnected, Is.False); Assert.That(Observe(client, 0), Is.Zero);
        Assert.That(client.LastConnectionError!.Status, Is.EqualTo(BleStatus.NativeFailure));
    }

    [TestCase(27, false)]
    [TestCase(27, true)]
    [TestCase(28, false)]
    [TestCase(28, true)]
    public async Task CancelAndTimeoutFenceHeldConnectOrWrite(int mode, bool timeout)
    {
        wss_test_mode(mode); using var client = await Client(TimeSpan.FromMilliseconds(500));
        if (mode == 28) await client.ConnectAsync("AABBCCDDEEFF");
        using var cts = new CancellationTokenSource();
        var task = mode == 27 ? (Task)client.ConnectAsync("AABBCCDDEEFF", cancellationToken: cts.Token) : client.SendAsync(new byte[] { 3 }, cts.Token);
        await Barrier(client);
        if (!timeout) cts.Cancel();
        if (timeout) Assert.ThrowsAsync<TimeoutException>(async () => await task);
        else Assert.ThrowsAsync<TaskCanceledException>(async () => await task);
        await Barrier(client); Assert.That(client.IsConnected, Is.False); Assert.That(Observe(client, 0), Is.Zero); Assert.That(client.State.Pending, Is.Empty);
    }

    [Test]
    public async Task CancelledSubmittedWriteBlocksReconnectUntilRetiredWriteIsTerminal()
    {
        wss_test_mode(30); using var client = await Client(); await client.ConnectAsync("AABBCCDDEEFF");
        using var cts = new CancellationTokenSource(); var send = client.SendAsync(new byte[] { 1 }, cts.Token); await Barrier(client); cts.Cancel();
        Assert.ThrowsAsync<TaskCanceledException>(async () => await send);
        var error = Assert.ThrowsAsync<BleBridgeException>(async () => await client.ConnectAsync("AABBCCDDEEFF")); Assert.That(error!.Status, Is.EqualTo(BleStatus.Busy));
        Assert.That(Observe(client, 1), Is.EqualTo(1));
        wss_test_nus_release(client.Context); await Barrier(client); await client.ConnectAsync("AABBCCDDEEFF");
        Assert.That(client.IsConnected, Is.True); Assert.That(Observe(client, 1), Is.EqualTo(1));
    }

    [Test]
    public async Task DelayedSendCompensationCannotDisposeANewerGeneration()
    {
        using var client = await Client(); var first = await client.ConnectAsync("AABBCCDDEEFF");
        await client.DisconnectAsync(); var next = await client.ConnectAsync("AABBCCDDEEFF");
        // Deterministically place the old send's asynchronous cleanup continuation
        // after the newer lifecycle fence, rather than depending on thread-pool timing.
        var cleanup = typeof(WindowsBleClient).GetMethod("CompensateSendDisconnect", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await ((Task)cleanup.Invoke(client, new object[] { first.ConnectionId })!).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(client.IsConnected, Is.True); Assert.That(client.State.ActiveConnection, Is.EqualTo(next.ConnectionId));
        await client.SendAsync(new byte[] { 1 }); Assert.That(Observe(client, 1), Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DisconnectOrLinkLossDuringWriteTerminatesPendingSend(bool linkLoss)
    {
        wss_test_mode(28); using var client = await Client(); var info = await client.ConnectAsync("AABBCCDDEEFF");
        var send = client.SendAsync(new byte[] { 1 }); await Barrier(client);
        if (linkLoss) { Terminate(client, info.ConnectionId, BleStatus.NativeFailure); await Barrier(client); }
        else await client.DisconnectAsync();
        Assert.ThrowsAsync<TaskCanceledException>(async () => await send);
        await Barrier(client); Assert.That(client.IsConnected, Is.False); Assert.That(Observe(client, 0), Is.Zero);
        await client.ConnectAsync("AABBCCDDEEFF"); Assert.That(client.IsConnected, Is.True);
    }

    [TestCase(27)]
    [TestCase(28)]
    [TestCase(29)]
    public async Task DisposalFencesCallbacksWithPendingGattOperation(int mode)
    {
        wss_test_mode(mode); var client = await Client();
        try
        {
            if (mode != 27) await client.ConnectAsync("AABBCCDDEEFF");
            Task task = mode == 27 ? client.ConnectAsync("AABBCCDDEEFF") : mode == 28 ? client.SendAsync(new byte[] { 1 }) : client.DisconnectAsync();
            await Barrier(client); client.Dispose(); client.Dispose();
            Assert.ThrowsAsync<ObjectDisposedException>(async () => await task);
            wss_test_counters(out _, out _, out var first, out var alive); Assert.That(alive, Is.Zero);
            wss_test_counters(out _, out _, out var second, out _); Assert.That(second, Is.EqualTo(first));
            Assert.That(client.State.Pending, Is.Empty); Assert.That(client.IsConnected, Is.False);
        }
        finally { client.Dispose(); }
    }

    private static async Task<WindowsBleClient> Client(TimeSpan? timeout = null) { var client = new WindowsBleClient(timeout); await client.InitializeAsync(); return client; }
    private static uint Observe(WindowsBleClient client, uint field, uint index = 0, byte[]? bytes = null)
    { bytes ??= new byte[1]; return wss_test_nus_observe(client.Context, field, index, bytes, (uint)bytes.Length); }
    private static void Emit(WindowsBleClient client, ulong conn, byte[] bytes) => Assert.That(wss_test_nus_emit(client.Context, conn, bytes, (uint)bytes.Length, 6, BleStatus.Success), Is.EqualTo(BleStatus.Success));
    private static void Terminate(WindowsBleClient client, ulong conn, BleStatus status) => Assert.That(wss_test_nus_emit(client.Context, conn, Array.Empty<byte>(), 0, 7, status), Is.EqualTo(BleStatus.Success));
    private static async Task Barrier(WindowsBleClient client)
    {
        const ulong id = 0xFFFF000000000001;
        var pending = new PendingOperation();
        lock (client.State.Gate) { client.State.Pending.Add(id, pending); Assert.That(wss_test_barrier(client.Context, id), Is.EqualTo(BleStatus.Success)); }
        await pending.Source.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}

[TestFixture]
public sealed class GattBoundaryTests
{
    [Test]
    public void ExtendedLayoutsAndOffsetsMatchWithoutChangingScanEnvelope()
    {
        Assert.That(Marshal.SizeOf<NativeNusOptions>(), Is.EqualTo(88)); Assert.That(Marshal.SizeOf<NativeGattEvent>(), Is.EqualTo(88));
        Assert.That(Marshal.OffsetOf<NativeNusOptions>(nameof(NativeNusOptions.ServiceUuid)).ToInt32(), Is.EqualTo(40));
        Assert.That(Marshal.OffsetOf<NativeGattEvent>(nameof(NativeGattEvent.Data)).ToInt32(), Is.EqualTo(72));
        Assert.That(Marshal.SizeOf<NativeEvent>(), Is.EqualTo(56));
    }
    [Test]
    public void LateConnectSuccessAfterLocalCancellationDoesNotPublishConnection()
    {
        var state = new CallbackState { ActiveConnection = 1 };
        var pending = new PendingOperation { Kind = OperationKind.Connect, Session = 1 }; pending.Source.TrySetCanceled(); state.Pending.Add(1, pending);
        state.DeliverGatt(new NativeGattEvent { Base = new NativeEvent { Kind = 5, Size = 88, OperationId = 1, SessionId = 1 }, MaxWriteLength = 20 }, Array.Empty<byte>());
        Assert.That(state.Connected, Is.False); Assert.That(state.Pending, Is.Empty);
        Assert.That(pending.Source.Task.IsCanceled, Is.True);
    }
    [TestCase(0u, 1u)]
    [TestCase(88u, 1048577u)]
    public void MalformedGattCallbacksFailPendingTasksWithoutEscaping(uint size, uint length)
    {
        var state = new CallbackState(); var pending = new PendingOperation(); state.Pending.Add(1, pending);
        var root = GCHandle.Alloc(state); var pointer = Marshal.AllocHGlobal(88);
        try
        {
            Marshal.StructureToPtr(new NativeGattEvent { Base = new NativeEvent { Size = size, Kind = 6 }, DataLength = length }, pointer, false);
            NativeMethods.CallbackRoot(GCHandle.ToIntPtr(root), pointer);
            Assert.That(state.CallbackError, Is.Not.Null); Assert.That(pending.Source.Task.IsFaulted, Is.True);
        }
        finally { Marshal.FreeHGlobal(pointer); root.Free(); }
    }
}
