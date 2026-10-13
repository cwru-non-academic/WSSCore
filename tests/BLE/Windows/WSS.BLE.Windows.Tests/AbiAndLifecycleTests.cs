using System.Runtime.InteropServices;
using System.Text;
using NUnit.Framework;
using Wss.Ble.Windows.Native;
using Wss.Ble.Windows.Native.Interop;

namespace WSS.BLE.Windows.Tests;

[TestFixture]
public sealed class ManagedBoundaryTests
{
    private static AbiInfo ValidAbi() => new AbiInfo { Size = 32, Major = 1, Minor = 1, PointerSize = 8, EventSize = 56, AdvertisementSize = 48, Capabilities = 3 };

    [TestCase("major")]
    [TestCase("minor")]
    [TestCase("nus-capability")]
    [TestCase("pointer")]
    [TestCase("event")]
    [TestCase("advertisement")]
    [TestCase("capabilities")]
    public void IncompatibleAbiIsRejected(string mismatch)
    {
        var info = ValidAbi();
        switch (mismatch)
        {
            case "major": info.Major = 2; break;
            case "minor": info.Minor = 0; break;
            case "nus-capability": info.Capabilities = 1; break;
            case "pointer": info.PointerSize = 4; break;
            case "event": info.EventSize = 64; break;
            case "advertisement": info.AdvertisementSize = 56; break;
            default: info.Capabilities = 0; break;
        }
        var ex = Assert.Throws<BleBridgeException>(() => AbiValidation.Validate(info));
        Assert.That(ex!.Status, Is.EqualTo(BleStatus.UnsupportedAbi));
    }

    [Test]
    public void NativeLayoutsAndCallingConventionsMatchDeclaredAbi()
    {
        AbiValidation.Validate(ValidAbi());
        Assert.That(Marshal.SizeOf<AbiInfo>(), Is.EqualTo(32));
        Assert.That(Marshal.SizeOf<NativeEvent>(), Is.EqualTo(56));
        Assert.That(Marshal.SizeOf<NativeAdvertisement>(), Is.EqualTo(48));
        Assert.That(typeof(NativeMethods.Callback).GetCustomAttributes(typeof(UnmanagedFunctionPointerAttribute), false)
            .Cast<UnmanagedFunctionPointerAttribute>().Single().CallingConvention, Is.EqualTo(CallingConvention.Cdecl));
        foreach (var method in typeof(NativeMethods).GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic))
        {
            var import = method.GetCustomAttributes(typeof(DllImportAttribute), false).Cast<DllImportAttribute>().SingleOrDefault();
            if (import != null)
            {
                Assert.That(import.CallingConvention, Is.EqualTo(CallingConvention.Cdecl), method.Name);
                Assert.That(import.ExactSpelling, Is.True, method.Name);
            }
        }
    }

    [Test]
    public void BorrowedUtf8AndCanonicalUuidBuffersAreCopiedBeforeReturn()
    {
        BleScanResult copy;
        using (var payload = new Payload()) copy = CallbackState.CopyAdvertisement(payload.Pointer);
        Assert.Multiple(() =>
        {
            Assert.That(copy.Name, Is.EqualTo("WSS-é-刺激"));
            Assert.That(copy.Id, Is.EqualTo("AABBCCDDEEFF"));
            Assert.That(copy.AddressType, Is.EqualTo(BleAddressType.Random));
            Assert.That(copy.Rssi, Is.EqualTo(-42));
            Assert.That(copy.ServiceUuids, Is.EqualTo(new[] { Guid.Parse("6E400001-B5A3-F393-E0A9-E50E24DCCA9E") }));
        });
        using var unknownRssi = new Payload(validRssi: false);
        Assert.That(CallbackState.CopyAdvertisement(unknownRssi.Pointer).Rssi, Is.Null);
    }

    [Test]
    public async Task ReverseCallbackCompletesExactlyOnceWithoutInlineUserContinuation()
    {
        var state = new CallbackState();
        var pending = new PendingOperation();
        state.Pending.Add(1, pending);
        var continuationThread = pending.Source.Task.ContinueWith(_ => Environment.CurrentManagedThreadId,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var callbackThread = 0;
        var thread = new Thread(() =>
        {
            callbackThread = Environment.CurrentManagedThreadId;
            Invoke(state, Completion(1));
            Invoke(state, Completion(1, BleStatus.AccessDenied));
        });
        thread.Start();
        Assert.That(thread.Join(TimeSpan.FromSeconds(5)), Is.True);
        var completed = await pending.Source.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(completed.Status, Is.EqualTo(BleStatus.Success));
        Assert.That(await continuationThread.WaitAsync(TimeSpan.FromSeconds(5)), Is.Not.EqualTo(callbackThread));
        Assert.That(state.Pending, Is.Empty);
        Assert.That(state.DuplicateCompletions, Is.EqualTo(1));
    }

    [Test]
    public void CancellationTimeoutAndFailureAreDistinctTerminalResults()
    {
        foreach (var status in new[] { BleStatus.Cancelled, BleStatus.TimedOut, BleStatus.AccessDenied })
        {
            var state = new CallbackState();
            var pending = new PendingOperation();
            state.Pending.Add(1, pending);
            var completion = Completion(1, status); completion.HResult = unchecked((int)0x80070005);
            Invoke(state, completion);
            if (status == BleStatus.Cancelled) Assert.That(pending.Source.Task.IsCanceled, Is.True);
            else if (status == BleStatus.TimedOut) Assert.That(pending.Source.Task.Exception!.InnerException, Is.TypeOf<TimeoutException>());
            else
            {
                var error = (BleBridgeException)pending.Source.Task.Exception!.InnerException!;
                Assert.That(error.Status, Is.EqualTo(BleStatus.AccessDenied));
                Assert.That(error.NativeHResult, Is.EqualTo(unchecked((int)0x80070005)));
            }
            Assert.That(state.Pending, Is.Empty);
        }
    }

    [Test]
    public void StopAndDisposalFenceQueuedAndStaleResults()
    {
        var state = new CallbackState { ActiveSession = 7 };
        using var payload = new Payload();
        var advertisement = new NativeEvent { Size = 56, Kind = 2, SessionId = 7, Advertisement = payload.Pointer };
        Invoke(state, advertisement);
        Assert.That(state.Queue.Count, Is.EqualTo(1));
        var stopped = new PendingOperation { Kind = OperationKind.Stop, Session = 7 };
        state.Pending.Add(2, stopped);
        Invoke(state, Completion(2));
        Invoke(state, advertisement);
        Assert.That(stopped.Source.Task.IsCompletedSuccessfully, Is.True);
        Assert.That(state.Queue, Is.Empty);
        var pending = new PendingOperation(); state.Pending.Add(3, pending);
        state.Close(); state.Close();
        var count = state.CallbackCount;
        Invoke(state, advertisement);
        Invoke(state, Completion(3));
        Assert.That(state.CallbackCount, Is.EqualTo(count));
        Assert.That(pending.Source.Task.Exception!.InnerException, Is.TypeOf<ObjectDisposedException>());
        Assert.That(state.Results, Is.Empty);
    }

    [Test]
    public void CancelledStopDoesNotForgetAnUnfencedSession()
    {
        var state = new CallbackState { ActiveSession = 7 };
        var stopped = new PendingOperation { Kind = OperationKind.Stop, Session = 7 };
        state.Pending.Add(1, stopped);
        Invoke(state, Completion(1, BleStatus.Cancelled));
        Assert.That(stopped.Source.Task.IsCanceled, Is.True);
        Assert.That(state.ActiveSession, Is.EqualTo(7), "A cancellation is not a proven native stop fence.");
        var retry = new PendingOperation { Kind = OperationKind.Stop, Session = 7 };
        state.Pending.Add(2, retry);
        Invoke(state, Completion(2));
        Assert.That(state.ActiveSession, Is.Zero);
    }

    [Test]
    public void RepeatedAdvertisementsMergeAndResultQueueIsBounded()
    {
        var state = new CallbackState { ActiveSession = 1 };
        var firstUuid = Guid.Parse("6E400001-B5A3-F393-E0A9-E50E24DCCA9E");
        var secondUuid = Guid.Parse("6E400002-B5A3-F393-E0A9-E50E24DCCA9E");
        var evt = new NativeEvent { Size = 56, Kind = 2, SessionId = 1 };
        state.Deliver(evt, new BleScanResult(1, BleAddressType.Random, "device", -42, new[] { firstUuid }));
        state.Deliver(evt, new BleScanResult(1, BleAddressType.Unspecified, " ", null, new[] { secondUuid, firstUuid }));
        Assert.That(state.Results.Count, Is.EqualTo(1));
        var merged = state.Results[1];
        Assert.That(merged.Name, Is.EqualTo("device"));
        Assert.That(merged.Rssi, Is.EqualTo(-42));
        Assert.That(merged.AddressType, Is.EqualTo(BleAddressType.Random));
        Assert.That(merged.ServiceUuids, Is.EquivalentTo(new[] { firstUuid, secondUuid }));
        for (ulong i = 2; i <= 1100; ++i)
            state.Deliver(evt, new BleScanResult(i, BleAddressType.Public, null, null, Array.Empty<Guid>()));
        Assert.That(state.Results.Count, Is.EqualTo(1024));
        Assert.That(state.Queue.Count, Is.EqualTo(256));
        Assert.That(state.Dropped, Is.GreaterThan(0));
    }

    [Test]
    public void MalformedCallbackIsContainedAndFailsPendingOperation()
    {
        var state = new CallbackState();
        var pending = new PendingOperation(); state.Pending.Add(1, pending);
        Invoke(state, new NativeEvent { Size = 1, Kind = 1, OperationId = 1 });
        Assert.That(state.CallbackError!.Status, Is.EqualTo(BleStatus.UnsupportedAbi));
        Assert.That(pending.Source.Task.Exception!.InnerException, Is.TypeOf<BleBridgeException>());
    }

    private static NativeEvent Completion(ulong id, BleStatus status = BleStatus.Success) => new NativeEvent { Size = 56, Kind = 1, OperationId = id, Status = status };
    private static void Invoke(CallbackState state, NativeEvent value)
    {
        var root = GCHandle.Alloc(state);
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeEvent>());
        try { Marshal.StructureToPtr(value, pointer, false); NativeMethods.CallbackRoot(GCHandle.ToIntPtr(root), pointer); }
        finally { Marshal.FreeHGlobal(pointer); root.Free(); }
    }

    private sealed class Payload : IDisposable
    {
        private readonly IntPtr _name, _uuid;
        internal IntPtr Pointer { get; }
        internal Payload(bool validRssi = true)
        {
            var name = Encoding.UTF8.GetBytes("WSS-é-刺激");
            var uuid = Convert.FromHexString("6E400001B5A3F393E0A9E50E24DCCA9E");
            _name = Marshal.AllocHGlobal(name.Length); _uuid = Marshal.AllocHGlobal(uuid.Length);
            Marshal.Copy(name, 0, _name, name.Length); Marshal.Copy(uuid, 0, _uuid, uuid.Length);
            Pointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeAdvertisement>());
            Marshal.StructureToPtr(new NativeAdvertisement { Size = 48, Address = 0xAABBCCDDEEFF, AddressType = BleAddressType.Random,
                Rssi = -42, RssiValid = validRssi ? 1u : 0u, Name = _name, NameLength = (uint)name.Length, Uuids = _uuid, UuidCount = 1 }, Pointer, false);
        }
        public void Dispose()
        {
            Marshal.Copy(Enumerable.Repeat((byte)0xDD, 16).ToArray(), 0, _uuid, 16);
            Marshal.FreeHGlobal(_name); Marshal.FreeHGlobal(_uuid); Marshal.FreeHGlobal(Pointer);
        }
    }
}

[TestFixture, NonParallelizable]
public sealed class NativeShimTests
{
    private const string Dll = "WssBleWindows.dll";
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern void wss_test_mode(int mode);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern BleStatus wss_test_emit(BleContextHandle context, uint variant);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern BleStatus wss_test_barrier(BleContextHandle context, ulong id);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern void wss_test_counters(out ulong made, out ulong freed, out ulong callbacks, out ulong alive);

    [OneTimeSetUp]
    public void RequireIsolatedShim()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(Path.Combine(TestContext.CurrentContext.TestDirectory, Dll)))
            Assert.Ignore("Native test shim not built: requires MSVC/Windows SDK. Managed-only tests are not native validation.");
        var module = NativeLibrary.Load(Path.Combine(TestContext.CurrentContext.TestDirectory, Dll));
        Assert.That(NativeLibrary.TryGetExport(module, "wss_test_mode", out _), Is.True, "Production DLL must never be used in the shim fixture.");
        NativeLibrary.Free(module);
    }

    [SetUp] public void ResetScenario() => wss_test_mode(0);
    [TearDown] public void CheckNoLeaks()
    {
        wss_test_counters(out _, out _, out _, out var alive);
        Assert.That(alive, Is.Zero, "Every test must destroy its native contexts.");
    }

    [TestCase(1)]
    [TestCase(2)]
    public void NativeReportedAbiMismatchRejectsCreation(int mode)
    {
        wss_test_mode(mode);
        var error = Assert.Throws<BleBridgeException>(() => new WindowsBleClient());
        Assert.That(error!.Status, Is.EqualTo(BleStatus.UnsupportedAbi));
    }

    [Test]
    public async Task NativeThreadPayloadAndDelegateSurviveGcAndBorrowedMemoryRelease()
    {
        using var client = new WindowsBleClient();
        await client.InitializeAsync();
        await client.StartScanAsync();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var submittingThread = Environment.CurrentManagedThreadId;
        Assert.That(wss_test_emit(client.Context, 0), Is.EqualTo(BleStatus.Success));
        Assert.That(wss_test_emit(client.Context, 1), Is.EqualTo(BleStatus.Success));
        await Barrier(client);
        Assert.That(client.State.CallbackThreadId, Is.Not.EqualTo(submittingThread), "Delivery must originate on the native worker, not the P/Invoke caller.");
        var result = client.GetDiscoveredDevices().Single();
        Assert.That(result.Name, Is.EqualTo("WSS-é-刺激"));
        Assert.That(result.Id, Is.EqualTo("AABBCCDDEEFF"));
        Assert.That(result.AddressType, Is.EqualTo(BleAddressType.Random));
        Assert.That(result.Rssi, Is.EqualTo(-42));
        Assert.That(result.ServiceUuids, Is.EquivalentTo(new[] {
            Guid.Parse("6E400001-B5A3-F393-E0A9-E50E24DCCA9E"), Guid.Parse("6E400002-B5A3-F393-E0A9-E50E24DCCA9E") }));
        Assert.That(client.DrainDiscovery().Count, Is.EqualTo(2));
        await client.StopScanAsync();
        var callbacks = client.State.CallbackCount;
        wss_test_emit(client.Context, 0);
        await Barrier(client);
        Assert.That(client.State.CallbackCount, Is.EqualTo(callbacks + 1), "Only barrier, no advertisement, follows stop.");
        Assert.That(client.DrainDiscovery(), Is.Empty);
    }

    [Test]
    public async Task DuplicateNativeCompletionDoesNotChangeTerminalResult()
    {
        wss_test_mode(6);
        using var client = new WindowsBleClient();
        await client.InitializeAsync();
        await Barrier(client);
        Assert.That(client.State.DuplicateCompletions, Is.EqualTo(1));
        Assert.That(client.State.Pending, Is.Empty);
    }

    [Test]
    public async Task CancelledStopRetriesCleanupBeforeAnotherScan()
    {
        wss_test_mode(7);
        using var client = new WindowsBleClient();
        await client.InitializeAsync(); await client.StartScanAsync();
        Assert.ThrowsAsync<TaskCanceledException>(async () => await client.StopScanAsync());
        Assert.That(client.State.ActiveSession, Is.Zero);
        await client.StartScanAsync(); // old watcher must be stopped, not merely hidden
        await client.StopScanAsync();
    }

    [Test]
    public void NativeFailureMapsStatusAndHresult()
    {
        wss_test_mode(3);
        using var client = new WindowsBleClient();
        var error = Assert.ThrowsAsync<BleBridgeException>(async () => await client.InitializeAsync());
        Assert.That(error!.Status, Is.EqualTo(BleStatus.AccessDenied));
        Assert.That(error.NativeHResult, Is.EqualTo(unchecked((int)0x80070005)));
    }

    [Test]
    public async Task CancellationAndTimeoutTerminateHeldNativeOperation()
    {
        wss_test_mode(4);
        using var client = new WindowsBleClient(TimeSpan.FromMilliseconds(500));
        await client.InitializeAsync();
        using var cancel = new CancellationTokenSource();
        var query = client.QueryAdapterAsync(cancel.Token);
        await Barrier(client); // ensures the held query is established before cancellation
        cancel.Cancel();
        Assert.ThrowsAsync<TaskCanceledException>(async () => await query);
        await Barrier(client); // ensures terminal native cancellation processed
        Assert.That(client.State.Pending, Is.Empty);
        Assert.ThrowsAsync<TimeoutException>(async () => await client.QueryAdapterAsync());
        await Barrier(client);
        Assert.That(client.State.Pending, Is.Empty);
    }

    [Test]
    public async Task DisposeDuringOutstandingWorkQuiescesNativeCallbacks()
    {
        wss_test_mode(4);
        var client = new WindowsBleClient();
        try
        {
            await client.InitializeAsync();
            var query = client.QueryAdapterAsync();
            await Barrier(client);
            client.Dispose(); client.Dispose();
            Assert.ThrowsAsync<ObjectDisposedException>(async () => await query);
            wss_test_counters(out _, out _, out var first, out var alive);
            Assert.That(alive, Is.Zero);
            // No native worker remains that could produce subsequent callbacks.
            wss_test_counters(out _, out _, out var second, out _);
            Assert.That(second, Is.EqualTo(first));
            Assert.Throws<ObjectDisposedException>(() => client.DrainDiscovery());
        }
        finally { client.Dispose(); }
    }

    [Test]
    public async Task RepeatedInitializeScanStopAndDestroyReleaseEveryContext()
    {
        wss_test_counters(out var beforeCreated, out var beforeDestroyed, out _, out _);
        for (var i = 0; i < 100; ++i)
        {
            using var client = new WindowsBleClient();
            await client.InitializeAsync(); await client.InitializeAsync();
            Assert.That(await client.QueryAdapterAsync(), Is.EqualTo(BleAdapterState.Ready));
            await client.StartScanAsync(); await client.StopScanAsync(); await client.StopScanAsync();
        }
        wss_test_counters(out var created, out var destroyed, out _, out var alive);
        Assert.That(created - beforeCreated, Is.EqualTo(100));
        Assert.That(destroyed - beforeDestroyed, Is.EqualTo(100));
        Assert.That(alive, Is.Zero);
    }

    private static async Task Barrier(WindowsBleClient client)
    {
        const ulong id = 0xFFFF000000000000;
        var pending = new PendingOperation { Kind = OperationKind.Query };
        lock (client.State.Gate)
        {
            client.State.Pending.Add(id, pending);
            Assert.That(wss_test_barrier(client.Context, id), Is.EqualTo(BleStatus.Success));
        }
        await pending.Source.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
