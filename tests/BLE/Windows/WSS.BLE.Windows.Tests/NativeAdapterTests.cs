using System.Diagnostics;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Wss.Ble.Windows.Native;
using Wss.Ble.Windows.Native.Interop;
using Wss.Transports;
using Wss.Transports.Backends;
using Wss.Transports.Backends.Windows;

namespace WSS.BLE.Windows.Tests;

internal static class AdapterShim
{
    private const string Dll = "WssBleWindows.dll";
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern void wss_test_mode(int mode);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern BleStatus wss_test_nus_emit(BleContextHandle context, ulong conn, byte[] bytes, uint length, uint kind, BleStatus status);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern BleStatus wss_test_nus_release(BleContextHandle context);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern BleStatus wss_test_nus_budget(BleContextHandle context, uint budget);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] private static extern BleStatus wss_test_barrier(BleContextHandle context, ulong id);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] private static extern uint wss_test_nus_observe(BleContextHandle context, uint field, uint index, byte[] bytes, uint capacity);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)] internal static extern void wss_test_counters(out ulong made, out ulong freed, out ulong callbacks, out ulong alive);

    internal static async Task Barrier(WindowsBleClient client)
    {
        const ulong id = 0xFFFF000000000002;
        var pending = new PendingOperation();
        lock (client.State.Gate)
        {
            client.State.Pending.Add(id, pending);
            Assert.That(wss_test_barrier(client.Context, id), Is.EqualTo(BleStatus.Success));
        }
        await pending.Source.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
    internal static uint Observe(WindowsBleClient client, uint field, uint index = 0, byte[]? bytes = null)
    {
        bytes ??= new byte[1];
        return wss_test_nus_observe(client.Context, field, index, bytes, (uint)bytes.Length);
    }
    internal static byte[] Write(WindowsBleClient client, uint index)
    {
        var buffer = new byte[1024];
        int length = checked((int)Observe(client, 2, index, buffer));
        return buffer[..length];
    }
    internal static void Emit(WindowsBleClient client, ulong generation, byte[] bytes, uint kind = 6, BleStatus status = BleStatus.Success) =>
        Assert.That(wss_test_nus_emit(client.Context, generation, bytes, (uint)bytes.Length, kind, status), Is.EqualTo(BleStatus.Success));
    internal static async Task WaitWrites(WindowsBleClient client, uint expected)
    {
        var clock = Stopwatch.StartNew();
        do { await Barrier(client); if (Observe(client, 1) >= expected) return; await Task.Yield(); }
        while (clock.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Fail($"Expected {expected} writes, observed {Observe(client, 1)}.");
    }
    internal static async Task<WindowsBleClient> Client()
    {
        var client = new WindowsBleClient();
        await client.InitializeAsync();
        return client;
    }
}

[TestFixture, NonParallelizable]
public sealed class NotificationReadinessTests
{
    [SetUp] public void Reset() => AdapterShim.wss_test_mode(0);
    [TearDown] public void NoLeaks() { AdapterShim.wss_test_counters(out _, out _, out _, out var alive); Assert.That(alive, Is.Zero); }

    [Test]
    public async Task BeforeAndAfterArrivalWaitsHaveNoLostWakeupAndCancellationIsLocal()
    {
        using var client = await AdapterShim.Client();
        var info = await client.ConnectAsync("AABBCCDDEEFF");
        using var cancelled = new CancellationTokenSource();
        var first = client.WaitForNotificationsAsync(info.ConnectionId, cancelled.Token);
        var second = client.WaitForNotificationsAsync(info.ConnectionId);
        cancelled.Cancel();
        Assert.ThrowsAsync<OperationCanceledException>(async () => await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.That(second.IsCompleted, Is.False);
        AdapterShim.Emit(client, info.ConnectionId, new byte[] { 0, 255 });
        Assert.That(await second.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
        Assert.That(await client.WaitForNotificationsAsync(info.ConnectionId), Is.True);
        Assert.That(client.DrainNotifications().Single(), Is.EqualTo(new byte[] { 0, 255 }));
        // Re-arm after a drain, then publish: no fixed delay or Update call.
        var rearmed = client.WaitForNotificationsAsync(info.ConnectionId);
        AdapterShim.Emit(client, info.ConnectionId, new byte[] { 128 });
        Assert.That(await rearmed.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
        Assert.That(client.IsConnected, Is.True);
    }

    [TestCase("disconnect")]
    [TestCase("linkloss")]
    [TestCase("dispose")]
    public async Task TerminalStateWakesIdleWaiter(string terminal)
    {
        using var client = await AdapterShim.Client();
        var info = await client.ConnectAsync("AABBCCDDEEFF");
        var wait = client.WaitForNotificationsAsync(info.ConnectionId);
        if (terminal == "disconnect") await client.DisconnectAsync();
        else if (terminal == "linkloss") AdapterShim.Emit(client, info.ConnectionId, Array.Empty<byte>(), 7, BleStatus.NativeFailure);
        else client.Dispose();
        if (terminal == "dispose") Assert.ThrowsAsync<ObjectDisposedException>(async () => await wait.WaitAsync(TimeSpan.FromSeconds(5)));
        else Assert.That(await wait.WaitAsync(TimeSpan.FromSeconds(5)), Is.False);
        if (terminal == "disconnect")
        {
            var next = await client.ConnectAsync("AABBCCDDEEFF");
            Assert.That(await client.WaitForNotificationsAsync(info.ConnectionId), Is.False);
            Assert.That(next.ConnectionId, Is.GreaterThan(info.ConnectionId));
        }
    }

    [Test]
    public async Task ReadyOldPumpCannotDrainNewGenerationAfterReconnect()
    {
        using var client = await AdapterShim.Client();
        var first = await client.ConnectAsync("AABBCCDDEEFF");
        var ready = client.WaitForNotificationsAsync(first.ConnectionId);
        AdapterShim.Emit(client, first.ConnectionId, new byte[] { 1 });
        Assert.That(await ready.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
        // Deterministically defer the old pump's drain until after a new generation queues data.
        await client.DisconnectAsync();
        var second = await client.ConnectAsync("AABBCCDDEEFF");
        AdapterShim.Emit(client, second.ConnectionId, new byte[] { 7, 0, 255 });
        await AdapterShim.Barrier(client);
        Assert.That(client.DrainNotifications(first.ConnectionId), Is.Empty);
        Assert.That(client.DrainNotifications(second.ConnectionId).Single(), Is.EqualTo(new byte[] { 7, 0, 255 }));
    }

    [Test]
    public async Task OverflowAndMalformedCallbackWakeWithDiagnostics()
    {
        using var client = await AdapterShim.Client();
        var info = await client.ConnectAsync("AABBCCDDEEFF");
        for (int i = 0; i < 257; ++i) AdapterShim.Emit(client, info.ConnectionId, new byte[] { 1 });
        await AdapterShim.Barrier(client);
        var error = Assert.ThrowsAsync<BleBridgeException>(async () => await client.WaitForNotificationsAsync(info.ConnectionId));
        Assert.That(error!.Status, Is.EqualTo(BleStatus.Overflow));
        await client.DisconnectAsync();
        info = await client.ConnectAsync("AABBCCDDEEFF");
        var wait = client.WaitForNotificationsAsync(info.ConnectionId);
        var root = GCHandle.Alloc(client.State);
        var pointer = Marshal.AllocHGlobal(88);
        try
        {
            Marshal.StructureToPtr(new NativeGattEvent { Base = new NativeEvent { Size = 0, Kind = 6 } }, pointer, false);
            NativeMethods.CallbackRoot(GCHandle.ToIntPtr(root), pointer);
            Assert.ThrowsAsync<BleBridgeException>(async () => await wait.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { Marshal.FreeHGlobal(pointer); root.Free(); }
    }
}

[TestFixture, NonParallelizable]
public sealed class NativeAdapterTests
{
    [SetUp] public void Reset() => AdapterShim.wss_test_mode(0);
    [TearDown] public void NoLeaks() { AdapterShim.wss_test_counters(out _, out _, out _, out var alive); Assert.That(alive, Is.Zero); }
    private static BleCandidate Device => new("0xAA:BB:CC:DD:EE:FF", "safe", -42, "Random");
    private static BleDiscoveryRequest Request(bool auto, string? id = null, string? name = "WSS-é-刺激") => new(id, name,
        BleNusTransport.DefaultServiceUuid, BleNusTransport.DefaultWriteCharacteristicUuid,
        BleNusTransport.DefaultNotifyCharacteristicUuid, TimeSpan.Zero, auto);

    [TestCase(false)]
    [TestCase(true)]
    public async Task ConfiguredConnectionMapsAddressAndDefaultOrCustomUuidRoles(bool custom)
    {
        var options = new BleNusTransportOptions { DeviceName = "safe", ServiceUuid = custom ? Guid.NewGuid() : BleNusTransport.DefaultServiceUuid,
            WriteCharacteristicUuid = custom ? Guid.NewGuid() : BleNusTransport.DefaultWriteCharacteristicUuid,
            NotifyCharacteristicUuid = custom ? Guid.NewGuid() : BleNusTransport.DefaultNotifyCharacteristicUuid };
        using var backend = new WindowsBleNusBackend(options);
        var found = await backend.DiscoverAsync(Request(false, "BluetoothLE#safe-é"), default);
        Assert.That(found.Single().Id, Is.EqualTo("BluetoothLE#safe-é"));
        await backend.ConnectAsync(Device, default);
        await AdapterShim.Barrier(backend.Client);
        var bytes = new byte[88]; AdapterShim.Observe(backend.Client, 3, bytes: bytes);
        Assert.That(BitConverter.ToUInt32(bytes, 4), Is.EqualTo(1));
        Assert.That(BitConverter.ToUInt32(bytes, 8), Is.EqualTo((uint)BleAddressType.Random));
        Assert.That(BitConverter.ToUInt64(bytes, 16), Is.EqualTo(0xAABBCCDDEEFFUL));
        Assert.That(bytes[40..88], Is.EqualTo(WindowsBleClient.CanonicalUuid(options.ServiceUuid)
            .Concat(WindowsBleClient.CanonicalUuid(options.WriteCharacteristicUuid)).Concat(WindowsBleClient.CanonicalUuid(options.NotifyCharacteristicUuid)).ToArray()));
        await backend.DisconnectAsync(default);
        await backend.ConnectAsync(found.Single(), default);
        await AdapterShim.Barrier(backend.Client);
        AdapterShim.Observe(backend.Client, 3, bytes: bytes);
        Assert.That(BitConverter.ToUInt32(bytes, 4), Is.EqualTo(2));
    }

    [Test]
    public void DiscoveryNativeFailureRetainsInvalidOperationCategoryAndDiagnostic()
    {
        AdapterShim.wss_test_mode(3);
        using var backend = new WindowsBleNusBackend(new() { DeviceName = "safe" });
        var error = Assert.ThrowsAsync<InvalidOperationException>(async () => await backend.DiscoverAsync(Request(false), default));
        Assert.That(error!.InnerException, Is.TypeOf<BleBridgeException>());
        Assert.That(((BleBridgeException)error.InnerException!).Status, Is.EqualTo(BleStatus.AccessDenied));
    }

    [TestCase(false, 40, 1)]
    [TestCase(true, 40, 1)]
    [TestCase(true, 41, 0)]
    public async Task DiscoveryValidatesAutoCandidatesWithoutWritesOrDelivery(bool auto, int mode, int count)
    {
        AdapterShim.wss_test_mode(mode);
        using var backend = new WindowsBleNusBackend(new() { DeviceName = "safe" });
        int deliveries = 0; backend.BytesReceived += _ => ++deliveries;
        var candidates = await backend.DiscoverAsync(Request(auto), default);
        await AdapterShim.Barrier(backend.Client);
        Assert.That(candidates.Count, Is.EqualTo(count));
        Assert.That(backend.IsConnected, Is.False);
        Assert.That(AdapterShim.Observe(backend.Client, 0), Is.Zero);
        Assert.That(AdapterShim.Observe(backend.Client, 1), Is.Zero);
        Assert.That(deliveries, Is.Zero);
        if (count > 0)
        {
            Assert.That(candidates[0].Id, Is.EqualTo("AABBCCDDEEFF"));
            Assert.That(candidates[0].Rssi, Is.EqualTo(-42));
            Assert.That(candidates[0].AddressType, Is.EqualTo("Random"));
        }
    }

    [Test]
    public async Task NotificationPumpPreservesOrderBinaryBytesAndReconnectGeneration()
    {
        using var backend = new WindowsBleNusBackend(new() { DeviceName = "safe" });
        await backend.ConnectAsync(Device, default);
        ulong old = backend.Client.State.ActiveConnection;
        var received = new List<byte[]>();
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int callbackThread = 0;
        backend.BytesReceived += bytes => { received.Add(bytes); callbackThread = Environment.CurrentManagedThreadId; if (received.Count == 2) done.TrySetResult(true); };
        AdapterShim.Emit(backend.Client, old, new byte[] { 0, 128, 255 });
        AdapterShim.Emit(backend.Client, old, new byte[] { 192, 219 });
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(received, Is.EqualTo(new[] { new byte[] { 0, 128, 255 }, new byte[] { 192, 219 } }));
        Assert.That(callbackThread, Is.Not.EqualTo(backend.Client.State.CallbackThreadId));
        var oldPump = backend.NotificationPump;
        await backend.DisconnectAsync(default);
        await backend.ConnectAsync(Device, default);
        await oldPump.WaitAsync(TimeSpan.FromSeconds(5));
        var next = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.BytesReceived += b => next.TrySetResult(b);
        AdapterShim.Emit(backend.Client, old, new byte[] { 9 });
        AdapterShim.Emit(backend.Client, backend.Client.State.ActiveConnection, new byte[] { 7 });
        Assert.That(await next.Task.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(new byte[] { 7 }));
        Assert.That(received.Count, Is.EqualTo(3));
    }

    [Test]
    public async Task LogicalSendSnapshotFragmentationAndSerializationPreserveWholeByteStream()
    {
        AdapterShim.wss_test_mode(28);
        using var backend = new WindowsBleNusBackend(new() { DeviceName = "safe" });
        await backend.ConnectAsync(Device, default);
        var payload = Enumerable.Range(0, 45).Select(i => (byte)i).ToArray();
        var expected = payload.ToArray();
        var first = backend.SendAsync(payload, default);
        await AdapterShim.WaitWrites(backend.Client, 1);
        var second = backend.SendAsync(new byte[] { 255, 128, 0 }, default);
        Array.Fill(payload, (byte)99);
        await AdapterShim.Barrier(backend.Client);
        Assert.That(AdapterShim.Observe(backend.Client, 1), Is.EqualTo(1));
        for (uint i = 1; i <= 4; ++i)
        {
            await AdapterShim.WaitWrites(backend.Client, i);
            AdapterShim.wss_test_nus_release(backend.Client.Context);
        }
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        await AdapterShim.Barrier(backend.Client);
        Assert.That(Enumerable.Range(0, 4).SelectMany(i => AdapterShim.Write(backend.Client, (uint)i)), Is.EqualTo(expected.Concat(new byte[] { 255, 128, 0 })));
        Assert.That(Enumerable.Range(0, 4).Select(i => AdapterShim.Write(backend.Client, (uint)i).Length), Is.EqualTo(new[] { 20, 20, 5, 3 }));
        var empty = backend.SendAsync(Array.Empty<byte>(), default);
        await AdapterShim.WaitWrites(backend.Client, 5); AdapterShim.wss_test_nus_release(backend.Client.Context);
        await empty.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(AdapterShim.Write(backend.Client, 4), Is.Empty);
    }

    [Test]
    public async Task BudgetIsRecheckedBetweenChunks()
    {
        AdapterShim.wss_test_mode(28);
        using var backend = new WindowsBleNusBackend(new() { DeviceName = "safe" });
        await backend.ConnectAsync(Device, default);
        var send = backend.SendAsync(new byte[29], default);
        await AdapterShim.WaitWrites(backend.Client, 1);
        AdapterShim.wss_test_nus_budget(backend.Client.Context, 4);
        for (uint i = 1; i <= 4; ++i) { await AdapterShim.WaitWrites(backend.Client, i); AdapterShim.wss_test_nus_release(backend.Client.Context); }
        await send.WaitAsync(TimeSpan.FromSeconds(5));
        await AdapterShim.Barrier(backend.Client);
        Assert.That(Enumerable.Range(0, 4).Select(i => AdapterShim.Write(backend.Client, (uint)i).Length), Is.EqualTo(new[] { 20, 4, 4, 1 }));
    }

    [Test]
    public async Task BudgetLossAfterSuccessfulPrefixDisconnectsWithoutSendingTheRemainder()
    {
        AdapterShim.wss_test_mode(28);
        using var backend = new WindowsBleNusBackend(new() { DeviceName = "safe" });
        await backend.ConnectAsync(Device, default);
        var send = backend.SendAsync(new byte[45], default);
        await AdapterShim.WaitWrites(backend.Client, 1);
        AdapterShim.wss_test_nus_budget(backend.Client.Context, 0);
        AdapterShim.wss_test_nus_release(backend.Client.Context);
        Assert.ThrowsAsync<InvalidOperationException>(async () => await send.WaitAsync(TimeSpan.FromSeconds(5)));
        await AdapterShim.Barrier(backend.Client);
        Assert.That(AdapterShim.Observe(backend.Client, 1), Is.EqualTo(1));
        Assert.That(AdapterShim.Write(backend.Client, 0).Length, Is.EqualTo(20));
        Assert.That(AdapterShim.Observe(backend.Client, 0), Is.Zero);
        Assert.That(backend.IsConnected, Is.False);
    }

    [Test]
    public async Task LinkLossWakesIdleAdapterPumpAndAllowsFreshReconnect()
    {
        using var backend = new WindowsBleNusBackend(new() { DeviceName = "safe" });
        await backend.ConnectAsync(Device, default);
        var pump = backend.NotificationPump;
        AdapterShim.Emit(backend.Client, backend.Client.State.ActiveConnection, Array.Empty<byte>(), 7, BleStatus.NativeFailure);
        await pump.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(backend.IsConnected, Is.False);
        await backend.ConnectAsync(Device, default);
        Assert.That(backend.IsConnected, Is.True);
        await backend.SendAsync(new byte[] { 7 }, default);
        await AdapterShim.Barrier(backend.Client);
        Assert.That(AdapterShim.Write(backend.Client, 0), Is.EqualTo(new byte[] { 7 }));
    }

    [Test]
    public async Task SubmittedCancellationCleansUpAndQueuedOldSendCannotCrossReconnect()
    {
        AdapterShim.wss_test_mode(28);
        using var backend = new WindowsBleNusBackend(new() { DeviceName = "safe" });
        await backend.ConnectAsync(Device, default);
        using var cts = new CancellationTokenSource();
        var first = backend.SendAsync(new byte[45], cts.Token);
        await AdapterShim.WaitWrites(backend.Client, 1);
        var queued = backend.SendAsync(new byte[] { 99 }, default);
        cts.Cancel();
        Assert.CatchAsync<OperationCanceledException>(async () => await first.WaitAsync(TimeSpan.FromSeconds(5)));
        var stale = Assert.CatchAsync(async () => await queued.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.That(stale is OperationCanceledException || stale is InvalidOperationException, Is.True,
            "A queued old-generation send must be cancelled or rejected as stale, never submitted.");
        await AdapterShim.Barrier(backend.Client);
        Assert.That(backend.IsConnected, Is.False);
        Assert.That(AdapterShim.Observe(backend.Client, 0), Is.Zero);
        Assert.That(AdapterShim.Observe(backend.Client, 1), Is.EqualTo(1));
        await backend.ConnectAsync(Device, default);
        var fresh = backend.SendAsync(new byte[] { 7 }, default);
        await AdapterShim.WaitWrites(backend.Client, 2); AdapterShim.wss_test_nus_release(backend.Client.Context);
        await fresh.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(AdapterShim.Write(backend.Client, 1), Is.EqualTo(new byte[] { 7 }));
    }

    [Test]
    public async Task PreSubmissionCancellationDoesNotDisconnectAndWriteFailureDoesNotReplay()
    {
        using (var backend = new WindowsBleNusBackend(new() { DeviceName = "safe" }))
        {
            await backend.ConnectAsync(Device, default);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Assert.CatchAsync<OperationCanceledException>(async () => await backend.SendAsync(new byte[45], cancelled.Token));
            Assert.That(backend.IsConnected, Is.True);
            Assert.That(AdapterShim.Observe(backend.Client, 1), Is.Zero);
        }
        AdapterShim.wss_test_mode(26);
        using var failed = new WindowsBleNusBackend(new() { DeviceName = "safe" });
        await failed.ConnectAsync(Device, default);
        var error = Assert.ThrowsAsync<InvalidOperationException>(async () => await failed.SendAsync(new byte[45], default));
        Assert.That(error!.InnerException, Is.TypeOf<BleBridgeException>());
        await AdapterShim.Barrier(failed.Client);
        Assert.That(AdapterShim.Observe(failed.Client, 1), Is.EqualTo(1));
        Assert.That(AdapterShim.Observe(failed.Client, 0), Is.Zero);
        Assert.That(failed.IsConnected, Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ReceiveHandlerCanSynchronouslyDisconnectOrDisposeFacade(bool dispose)
    {
        var backend = new WindowsBleNusBackend(new() { DeviceName = "safe" });
        using var facade = new BleNusTransport(new() { DeviceName = "safe" }, backend);
        await backend.ConnectAsync(Device, default);
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        facade.BytesReceived += _ =>
        {
            try { if (dispose) facade.Dispose(); else facade.DisconnectAsync().GetAwaiter().GetResult(); done.TrySetResult(true); }
            catch (Exception ex) { done.TrySetException(ex); }
        };
        var pump = backend.NotificationPump;
        AdapterShim.Emit(backend.Client, backend.Client.State.ActiveConnection, new byte[] { 1 });
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await pump.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(facade.IsConnected, Is.False);
    }

    [Test]
    public async Task SlowSubscriberOverflowDisconnectsRatherThanSilentlyLosingBytes()
    {
        using var backend = new WindowsBleNusBackend(new() { DeviceName = "safe" });
        await backend.ConnectAsync(Device, default);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        backend.BytesReceived += _ => { entered.TrySetResult(true); Assert.That(release.Wait(TimeSpan.FromSeconds(5)), Is.True); };
        ulong generation = backend.Client.State.ActiveConnection;
        AdapterShim.Emit(backend.Client, generation, new byte[] { 0 });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            for (int i = 0; i < 257; ++i) AdapterShim.Emit(backend.Client, generation, new byte[] { 1 });
            await AdapterShim.Barrier(backend.Client);
            Assert.That(backend.Client.LastConnectionError!.Status, Is.EqualTo(BleStatus.Overflow));
        }
        finally { release.Set(); }
        await backend.NotificationPump.WaitAsync(TimeSpan.FromSeconds(5));
        await AdapterShim.Barrier(backend.Client);
        Assert.That(backend.IsConnected, Is.False);
        Assert.That(AdapterShim.Observe(backend.Client, 0), Is.Zero);
        await backend.ConnectAsync(Device, default);
        Assert.That(backend.IsConnected, Is.True);
    }
}
