using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Wss.Ble.Windows.Native;
using Wss.Ble.Windows.Native.Interop;

namespace WSS.BLE.Windows.Tests;

[TestFixture, NonParallelizable]
public sealed class ProductionNativeLifecycleTests
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate BleStatus Create(uint major, NativeMethods.Callback callback, IntPtr user, out IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate BleStatus Submit(IntPtr context, ulong operation);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate BleStatus Destroy(IntPtr context);

    [Test]
    public void CurrentAbiInitializesQueriesAndFencesCallbacksAtDestruction()
    {
        var module = ProductionNativeLibrary.Load();
        IntPtr context = IntPtr.Zero;
        var events = new ConcurrentQueue<NativeEvent>();
        var errors = new ConcurrentQueue<Exception>();
        using var signal = new AutoResetEvent(false);
        NativeMethods.Callback callback = (_, pointer) =>
        {
            try { events.Enqueue(Marshal.PtrToStructure<NativeEvent>(pointer)); }
            catch (Exception ex) { errors.Enqueue(ex); }
            finally { signal.Set(); }
        };
        void Complete(ulong operation)
        {
            Assert.That(signal.WaitOne(TimeSpan.FromSeconds(5)), Is.True, "Missing production completion.");
            Assert.That(errors, Is.Empty);
            Assert.That(events.TryDequeue(out var value), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(value.OperationId, Is.EqualTo(operation));
                Assert.That(value.Size, Is.EqualTo(56));
                Assert.That(value.Status, Is.EqualTo(BleStatus.Success));
                Assert.That(events, Is.Empty, "Duplicate completion.");
            });
        }
        try
        {
            Assert.That(ProductionNativeLibrary.Export<Create>(module, "wss_ble_create")(1, callback, IntPtr.Zero, out context), Is.EqualTo(BleStatus.Success));
            Assert.That(ProductionNativeLibrary.Export<Submit>(module, "wss_ble_initialize")(context, 1), Is.EqualTo(BleStatus.Success));
            Complete(1);
            Assert.That(ProductionNativeLibrary.Export<Submit>(module, "wss_ble_query_adapter")(context, 2), Is.EqualTo(BleStatus.Success));
            Complete(2);
            Assert.That(ProductionNativeLibrary.Export<Destroy>(module, "wss_ble_destroy")(context), Is.EqualTo(BleStatus.Success));
            context = IntPtr.Zero;
            Assert.That(events, Is.Empty, "Destroy must fence callback delivery.");
            Assert.That(errors, Is.Empty);
        }
        finally
        {
            if (context != IntPtr.Zero) ProductionNativeLibrary.Export<Destroy>(module, "wss_ble_destroy")(context);
            GC.KeepAlive(callback);
            NativeLibrary.Free(module);
        }
    }
}
