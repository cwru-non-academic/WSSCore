using System.Collections.Concurrent;
using NUnit.Framework;
using Wss.Transports;
using Wss.Transports.Backends;
using Wss.Transports.Backends.MacOS;

namespace WSS.BLE.Tests;

[TestFixture]
public class MacBleContractTests
{
    private const string Device = "11111111-2222-3333-4444-555555555555";
    private sealed class Fake : IMacNative
    {
        public event Action<MacEvent>? Event;
        internal readonly ConcurrentQueue<byte[]> Writes = new();
        internal readonly TaskCompletionSource<ulong> WriteStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool HoldWrite, Disposed;
        internal ulong Session, PendingWrite;
        internal uint Budget = 3;
        public int Submit(uint command, ulong operation, ulong session, string? device, string? service, string? rx, string? tx, byte[] data, uint timeout)
        {
            if (Disposed) throw new ObjectDisposedException(nameof(Fake));
            if (command == 4) Session = session;
            if (command == 5)
            {
                Writes.Enqueue((byte[])data.Clone()); PendingWrite = operation; WriteStarted.TrySetResult(operation);
                if (HoldWrite) return 0;
            }
            if (command == 6) Session = 0;
            Emit(new MacEvent { Kind = 1, Operation = operation, Session = session, Budget = Budget });
            return 0;
        }
        public void Cancel(ulong operation) => Emit(new MacEvent { Kind = 1, Operation = operation, Status = 4 });
        internal void Emit(MacEvent e) => Event?.Invoke(e);
        public void Dispose() { Disposed = true; Event = null; }
    }
    private static MacBleNusBackend Backend(Fake fake) => new(new BleNusTransportOptions { DeviceId = Device }, fake);
    private static async Task Connect(MacBleNusBackend backend) => await backend.ConnectAsync(new BleCandidate(Device, "fake"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));

    [Test]
    public async Task SendsSnapshotInBudgetChunksWithoutInterleaving()
    {
        var fake = new Fake { HoldWrite = true };
        using var backend = Backend(fake); await Connect(backend);
        byte[] data = { 1, 2, 3, 4, 5 };
        var first = backend.SendAsync(data, CancellationToken.None);
        await fake.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        data[3] = 99;
        var second = backend.SendAsync(new byte[] { 6, 7 }, CancellationToken.None);
        fake.HoldWrite = false;
        fake.Emit(new MacEvent { Kind = 1, Operation = fake.PendingWrite, Budget = 3 });
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.That(fake.Writes.ToArray(), Is.EqualTo(new[] { new byte[] { 1, 2, 3 }, new byte[] { 4, 5 }, new byte[] { 6, 7 } }));
    }
    [Test]
    public async Task SubmittedCancellationFencesQueuedSendAndDoesNotReplay()
    {
        var fake = new Fake { HoldWrite = true };
        using var backend = Backend(fake); await Connect(backend);
        using var cancel = new CancellationTokenSource();
        var first = backend.SendAsync(new byte[] { 1, 2, 3, 4 }, cancel.Token);
        await fake.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var queued = backend.SendAsync(new byte[] { 9 }, CancellationToken.None);
        cancel.Cancel();
        Assert.ThrowsAsync<OperationCanceledException>(async () => await first.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.ThrowsAsync<OperationCanceledException>(async () => await queued.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.That(backend.IsConnected, Is.False);
        Assert.That(fake.Writes.ToArray(), Is.EqualTo(new[] { new byte[] { 1, 2, 3 } }));
    }
    [Test]
    public async Task ReconnectRejectsOldNotificationsAndHandlerCanDisconnect()
    {
        var fake = new Fake(); using var backend = Backend(fake); await Connect(backend);
        var stale = fake.Session;
        await backend.DisconnectAsync(CancellationToken.None);
        await Connect(backend);
        var delivered = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.BytesReceived += bytes => { backend.DisconnectAsync(CancellationToken.None).GetAwaiter().GetResult(); delivered.TrySetResult(bytes); };
        fake.Emit(new MacEvent { Kind = 3, Session = stale, Data = new byte[] { 99 } });
        fake.Emit(new MacEvent { Kind = 3, Session = fake.Session, Data = new byte[] { 7, 8 } });
        Assert.That(await delivered.Task.WaitAsync(TimeSpan.FromSeconds(3)), Is.EqualTo(new byte[] { 7, 8 }));
        Assert.That(backend.IsConnected, Is.False);
    }
    [Test]
    public async Task DisposeTerminatesPendingOperationAndNativeCallbackOwner()
    {
        var fake = new Fake { HoldWrite = true }; var backend = Backend(fake); await Connect(backend);
        var send = backend.SendAsync(new byte[] { 1 }, CancellationToken.None);
        await fake.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        backend.Dispose();
        try { await send.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Fail("Pending send survived disposal"); }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { }
        await backend.NotificationPump.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.That(fake.Disposed, Is.True);
        Assert.That(backend.IsConnected, Is.False);
    }
    [Test]
    public void MacFactoryActivatesWithoutTouchingNativeOrHardware()
    {
        Assert.That(typeof(MacBleBackend).Assembly.GetName().Name, Is.EqualTo("WSS.Transport.BLE.MacOS"));
        using var backend = BleBackendLoader.CreateBackendForPlatform(new BleNusTransportOptions { DeviceId = Device }, "macos");
        Assert.That(backend.GetType().Assembly.GetName().Name, Is.EqualTo("WSS.Transport.BLE.MacOS"));
        Assert.That(backend.IsConnected, Is.False);
    }
}
