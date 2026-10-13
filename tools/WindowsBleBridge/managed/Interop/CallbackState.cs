using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Wss.Ble.Windows.Native.Interop
{
    internal enum OperationKind { Initialize, Query, Start, Stop, Connect, Write, Disconnect }
    internal sealed class PendingOperation
    {
        internal readonly TaskCompletionSource<NativeEvent> Source = new TaskCompletionSource<NativeEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        internal OperationKind Kind;
        internal ulong Session;
        internal NativeGattEvent Gatt;
    }

    // Does not refer to WindowsBleClient or its SafeHandle: GCHandle cannot root the owner.
    internal sealed class CallbackState
    {
        internal readonly object Gate = new object();
        internal readonly Dictionary<ulong, PendingOperation> Pending = new Dictionary<ulong, PendingOperation>();
        internal readonly Dictionary<ulong, BleScanResult> Results = new Dictionary<ulong, BleScanResult>();
        internal readonly Queue<BleScanResult> Queue = new Queue<BleScanResult>();
        internal bool Disposed;
        internal ulong ActiveSession, Dropped;
        internal BleBridgeException? ScanError, CallbackError;
        internal readonly Queue<byte[]> Notifications = new Queue<byte[]>();
        internal int NotificationBytes;
        internal ulong ActiveConnection;
        internal bool Connected;
        internal uint MaxWriteLength;
        internal BleBridgeException? ConnectionError;
        internal TaskCompletionSource<bool> NotificationChanged = NewNotificationSignal();
        private static TaskCompletionSource<bool> NewNotificationSignal() =>
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Called under Gate. Continuations never run in reverse P/Invoke.
        internal void WakeNotifications()
        {
            var previous = NotificationChanged;
            NotificationChanged = NewNotificationSignal();
            previous.TrySetResult(true);
        }
        internal long CallbackCount, DuplicateCompletions;
        internal int CallbackThreadId;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        internal static void Receive(IntPtr user, IntPtr nativeEvent)
        {
            CallbackState? state = null;
            try
            {
                state = (CallbackState?)GCHandle.FromIntPtr(user).Target;
                if (state == null) return;
                if (nativeEvent == IntPtr.Zero) throw new BleBridgeException(BleStatus.UnsupportedAbi, "Null callback event.");
                var value = Marshal.PtrToStructure<NativeEvent>(nativeEvent);
                if (value.Kind >= 5 && value.Kind <= 7)
                {
                    if (value.Size != 88) throw new BleBridgeException(BleStatus.UnsupportedAbi, "GATT event size mismatch.");
                    var gatt = Marshal.PtrToStructure<NativeGattEvent>(nativeEvent);
                    if (gatt.Reserved != 0 || gatt.DataLength > 1024 * 1024 || (gatt.DataLength != 0 && gatt.Data == IntPtr.Zero) ||
                        (value.Kind != 6 && (gatt.Data != IntPtr.Zero || gatt.DataLength != 0)))
                        throw new BleBridgeException(BleStatus.UnsupportedAbi, "Invalid GATT buffer.");
                    var bytes = new byte[gatt.DataLength];
                    if (bytes.Length != 0) Marshal.Copy(gatt.Data, bytes, 0, bytes.Length);
                    state.DeliverGatt(gatt, bytes);
                    return;
                }
                if (value.Size != Marshal.SizeOf<NativeEvent>()) throw new BleBridgeException(BleStatus.UnsupportedAbi, "Callback event size mismatch.");
                // Copy borrowed buffers before taking the state lock or returning to native.
                var result = value.Kind == 2 ? CopyAdvertisement(value.Advertisement) : null;
                state.Deliver(value, result);
            }
            catch (Exception ex)
            {
                // No managed exception may cross reverse P/Invoke.
                try
                {
                    if (state != null)
                    {
                        lock (state.Gate)
                        {
                            state.CallbackError = ex as BleBridgeException ?? new BleBridgeException(BleStatus.NativeFailure, "Invalid native callback: " + ex.Message);
                            state.WakeNotifications();
                            foreach (var pending in state.Pending.Values) pending.Source.TrySetException(state.CallbackError);
                            state.Pending.Clear();
                        }
                    }
                }
                catch { } // even error-reporting failures must not escape reverse P/Invoke
            }
        }

        internal static BleScanResult CopyAdvertisement(IntPtr pointer)
        {
            if (pointer == IntPtr.Zero) throw new BleBridgeException(BleStatus.UnsupportedAbi, "Null advertisement.");
            var value = Marshal.PtrToStructure<NativeAdvertisement>(pointer);
            if (value.Size != Marshal.SizeOf<NativeAdvertisement>() || value.NameLength > 65536 || value.UuidCount > 256 ||
                (value.NameLength != 0 && value.Name == IntPtr.Zero) || (value.UuidCount != 0 && value.Uuids == IntPtr.Zero) ||
                value.RssiValid > 1 || (uint)value.AddressType > 2 || value.Address > 0xFFFFFFFFFFFFUL)
                throw new BleBridgeException(BleStatus.UnsupportedAbi, "Invalid advertisement layout or lengths.");
            var nameBytes = new byte[value.NameLength];
            if (nameBytes.Length != 0) Marshal.Copy(value.Name, nameBytes, 0, nameBytes.Length);
            var bytes = new byte[checked((int)value.UuidCount * 16)];
            if (bytes.Length != 0) Marshal.Copy(value.Uuids, bytes, 0, bytes.Length);
            var uuids = new Guid[value.UuidCount];
            for (var i = 0; i < uuids.Length; ++i)
            {
                var offset = i * 16;
                uuids[i] = new Guid((int)((uint)bytes[offset] << 24 | (uint)bytes[offset + 1] << 16 | (uint)bytes[offset + 2] << 8 | bytes[offset + 3]),
                    (short)(bytes[offset + 4] << 8 | bytes[offset + 5]), (short)(bytes[offset + 6] << 8 | bytes[offset + 7]),
                    bytes[offset + 8], bytes[offset + 9], bytes[offset + 10], bytes[offset + 11], bytes[offset + 12], bytes[offset + 13], bytes[offset + 14], bytes[offset + 15]);
            }
            return new BleScanResult(value.Address, value.AddressType, nameBytes.Length == 0 ? null : Utf8.GetString(nameBytes),
                value.RssiValid == 1 ? (int?)value.Rssi : null, uuids);
        }

        internal void Deliver(NativeEvent value, BleScanResult? result)
        {
            lock (Gate)
            {
                if (Disposed) return;
                ++CallbackCount;
                CallbackThreadId = Environment.CurrentManagedThreadId;
                if (value.Kind == 1)
                {
                    if (!Pending.TryGetValue(value.OperationId, out var pending)) { ++DuplicateCompletions; return; }
                    Pending.Remove(value.OperationId);
                    // Only a proven native stop fence may make an active session restartable.
                    if (pending.Kind == OperationKind.Stop && value.Status == BleStatus.Success && pending.Session == ActiveSession)
                    { ActiveSession = 0; Queue.Clear(); }
                    if (value.Status == BleStatus.Success) pending.Source.TrySetResult(value);
                    else if (value.Status == BleStatus.Cancelled) pending.Source.TrySetCanceled();
                    else if (value.Status == BleStatus.TimedOut) pending.Source.TrySetException(new TimeoutException("Native BLE operation timed out."));
                    else pending.Source.TrySetException(new BleBridgeException(value.Status, "Native BLE operation failed: " + value.Status,
                        value.HResult, value.BluetoothError));
                }
                else if (value.SessionId != ActiveSession || ActiveSession == 0) return;
                else if (value.Kind == 2 && result != null && result.Address != 0)
                {
                    if (Results.TryGetValue(result.Address, out var old))
                        result = new BleScanResult(result.Address, result.AddressType == BleAddressType.Unspecified ? old.AddressType : result.AddressType,
                            string.IsNullOrWhiteSpace(result.Name) ? old.Name : result.Name, result.Rssi ?? old.Rssi,
                            old.ServiceUuids.Concat(result.ServiceUuids).Distinct().Take(256).ToArray());
                    else if (Results.Count >= 1024) { ++Dropped; return; }
                    Results[result.Address] = result;
                    if (Queue.Count >= 256) { Queue.Dequeue(); ++Dropped; }
                    Queue.Enqueue(result);
                }
                else if (value.Kind == 3)
                {
                    ScanError = new BleBridgeException(value.Status, "Advertisement watcher terminated unexpectedly.", value.HResult, value.BluetoothError);
                    ActiveSession = 0;
                    Queue.Clear();
                }
                else if (value.Kind == 4) Dropped += value.DroppedCount;
            }
        }

        internal void Close()
        {
            lock (Gate)
            {
                if (Disposed) return;
                Disposed = true; ActiveSession = 0; Queue.Clear(); Results.Clear();
                ActiveConnection = 0; Connected = false; MaxWriteLength = 0; Notifications.Clear(); NotificationBytes = 0;
                foreach (var pending in Pending.Values) pending.Source.TrySetException(new ObjectDisposedException(nameof(WindowsBleClient)));
                Pending.Clear();
                WakeNotifications();
            }
        }
        internal void DeliverGatt(NativeGattEvent value, byte[] bytes)
        {
            lock (Gate)
            {
                if (Disposed) return;
                ++CallbackCount; CallbackThreadId = Environment.CurrentManagedThreadId;
                var e = value.Base;
                if (e.Kind == 5)
                {
                    if (!Pending.TryGetValue(e.OperationId, out var pending)) { ++DuplicateCompletions; return; }
                    if (pending.Session != e.SessionId) throw new BleBridgeException(BleStatus.UnsupportedAbi, "GATT connection correlation mismatch.");
                    Pending.Remove(e.OperationId); pending.Gatt = value;
                    if (e.SessionId == ActiveConnection)
                    {
                        if (pending.Kind == OperationKind.Connect && e.Status == BleStatus.Success && !pending.Source.Task.IsCompleted)
                        { Connected = true; MaxWriteLength = value.MaxWriteLength; WakeNotifications(); }
                        if (pending.Kind == OperationKind.Write && Connected) MaxWriteLength = value.MaxWriteLength;
                        if (pending.Kind == OperationKind.Disconnect && e.Status == BleStatus.Success)
                        {
                            if ((value.GattStatus != 0 && value.GattStatus != uint.MaxValue) || e.HResult != 0)
                            {
                                var warning = value; warning.Base.Status = BleStatus.NativeFailure;
                                ConnectionError = GattError(warning, "CCCD cleanup failed; local disconnect fence established.");
                            }
                            ClearConnection();
                        }
                    }
                    if (e.Status == BleStatus.Success) pending.Source.TrySetResult(e);
                    else if (e.Status == BleStatus.Cancelled) pending.Source.TrySetCanceled();
                    else if (e.Status == BleStatus.TimedOut) pending.Source.TrySetException(new TimeoutException("Native GATT operation timed out."));
                    else pending.Source.TrySetException(GattError(value, "Native GATT operation failed."));
                }
                else if (e.SessionId != ActiveConnection || ActiveConnection == 0) return;
                else if (e.Kind == 7)
                {
                    ConnectionError = GattError(value, "BLE connection terminated."); ClearConnection();
                }
                else if (e.Kind == 6 && Connected && bytes.Length != 0)
                {
                    if (Notifications.Count >= 256 || NotificationBytes + bytes.Length > 1024 * 1024)
                    {
                        ConnectionError = new BleBridgeException(BleStatus.Overflow, "Notification queue overflow; disconnect before reconnecting.");
                        Connected = false; MaxWriteLength = 0; Notifications.Clear(); NotificationBytes = 0;
                        WakeNotifications();
                    }
                    else { Notifications.Enqueue(bytes); NotificationBytes += bytes.Length; WakeNotifications(); }
                }
            }
        }
        internal void ClearConnection()
        {
            Connected = false; ActiveConnection = 0; MaxWriteLength = 0; Notifications.Clear(); NotificationBytes = 0;
            WakeNotifications();
        }
        private static BleBridgeException GattError(NativeGattEvent e, string message) => new BleBridgeException(
            e.Base.Status, message + " Stage=" + e.Stage + ", GATT=" + e.GattStatus + ", ATT=" + e.ProtocolError,
            e.Base.HResult, e.Base.BluetoothError, e.Stage, e.GattStatus, e.ProtocolError);
    }
}
