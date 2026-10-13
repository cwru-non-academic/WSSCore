using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Wss.Ble.Windows.Native;
using Wss.Ble.Windows.Native.Interop;

namespace WSS.BLE.Windows.Tests;

[TestFixture, NonParallelizable]
public sealed class NativeDisconnectDeadlineTests
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate BleStatus Create(uint major, NativeMethods.Callback callback, IntPtr user, out IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate BleStatus Initialize(IntPtr context, ulong operation);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate BleStatus Disconnect(IntPtr context, ulong operation, ulong connection, uint timeout);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate BleStatus Destroy(IntPtr context);

    [Test]
    public void ExpiredQueuedDisconnectCannotSucceedWhenConnectionIsAlreadyAbsent()
    {
        // Load actual production code, not the shim copied beside this test assembly.
        var path = ProductionNativeLibrary.Path;
        Assert.That(File.Exists(path), Is.True, "Build the production native project first.");
        var module = ProductionNativeLibrary.Load();
        IntPtr context = IntPtr.Zero;
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var delivered = new AutoResetEvent(false);
        var completions = new ConcurrentQueue<NativeEvent>();
        var counts = new ConcurrentDictionary<ulong, int>();
        var errors = new ConcurrentQueue<string>();
        NativeMethods.Callback callback = (_, pointer) =>
        {
            try
            {
                var value = Marshal.PtrToStructure<NativeEvent>(pointer);
                if (value.Kind == 1 && value.OperationId == 1)
                {
                    // A bounded dispatcher barrier makes expiry-before-execution
                    // deterministic without a Bluetooth connection or CCCD request.
                    entered.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(5))) errors.Enqueue("Dispatcher barrier was not released.");
                }
                else
                {
                    counts.AddOrUpdate(value.OperationId, 1, (_, count) => count + 1);
                    completions.Enqueue(value); delivered.Set();
                }
            }
            catch (Exception ex) { errors.Enqueue(ex.ToString()); delivered.Set(); }
        };
        T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(module, name));
        NativeEvent Completion(ulong id)
        {
            var wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromSeconds(5))
            {
                while (completions.TryDequeue(out var value))
                {
                    Assert.That(value.OperationId, Is.EqualTo(id), "Unexpected or duplicate native completion.");
                    return value;
                }
                delivered.WaitOne(TimeSpan.FromMilliseconds(20));
            }
            Assert.Fail("Production disconnect did not complete: " + id); return default;
        }
        try
        {
            Assert.That(NativeLibrary.TryGetExport(module, "wss_test_mode", out _), Is.False, "This regression must execute production native logic.");
            Assert.That(Export<Create>("wss_ble_create")(1, callback, IntPtr.Zero, out context), Is.EqualTo(BleStatus.Success));
            Assert.That(Export<Initialize>("wss_ble_initialize")(context, 1), Is.EqualTo(BleStatus.Success));
            Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True, "Worker did not reach the dispatcher barrier.");

            const uint timeout = 5;
            var disconnect = Export<Disconnect>("wss_ble_nus_disconnect");
            Assert.That(disconnect(context, 2, 1, timeout), Is.EqualTo(BleStatus.Success));
            var elapsed = Stopwatch.StartNew();
            Assert.That(SpinWait.SpinUntil(() => elapsed.Elapsed >= TimeSpan.FromMilliseconds(timeout + 1), TimeSpan.FromSeconds(1)), Is.True);
            release.Set();
            var expired = Completion(2);
            Assert.Multiple(() =>
            {
                Assert.That(expired.Size, Is.EqualTo(88));
                Assert.That(expired.Kind, Is.EqualTo(5));
                Assert.That(expired.SessionId, Is.EqualTo(1));
                Assert.That(expired.Status, Is.EqualTo(BleStatus.TimedOut));
            });

            // Timeout must not strand the worker/context or duplicate the completion.
            Assert.That(disconnect(context, 3, 1, 10000), Is.EqualTo(BleStatus.Success));
            Assert.That(Completion(3).Status, Is.EqualTo(BleStatus.Success));
            Assert.That(counts[2], Is.EqualTo(1));
            Assert.That(errors, Is.Empty);
        }
        finally
        {
            release.Set();
            if (context != IntPtr.Zero) Assert.That(Export<Destroy>("wss_ble_destroy")(context), Is.EqualTo(BleStatus.Success));
            GC.KeepAlive(callback); NativeLibrary.Free(module);
        }
    }
}
