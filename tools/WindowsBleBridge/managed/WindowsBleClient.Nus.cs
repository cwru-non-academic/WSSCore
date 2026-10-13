using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wss.Ble.Windows.Native.Interop;

namespace Wss.Ble.Windows.Native
{
    public sealed partial class WindowsBleClient
    {
        private readonly SemaphoreSlim _connectionOperations = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _sendOperations = new SemaphoreSlim(1, 1);
        private ulong _nextConnection;
        public bool IsConnected { get { lock (_state.Gate) return !_state.Disposed && _state.Connected; } }
        public uint MaxWriteLength { get { lock (_state.Gate) return _state.MaxWriteLength; } }
        public BleBridgeException? LastConnectionError { get { lock (_state.Gate) return _state.ConnectionError; } }

        public async Task<BleConnectionInfo> ConnectAsync(string deviceId,
            BleAddressType addressType = BleAddressType.Unspecified, BleNusOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            RequireNus();
            if (string.IsNullOrWhiteSpace(deviceId)) throw new ArgumentException("A device address or Windows ID is required.", nameof(deviceId));
            if ((uint)addressType > 2) throw new ArgumentOutOfRangeException(nameof(addressType));
            options = options ?? new BleNusOptions();
            var native = new NativeNusOptions { Size = 88, Timeout = TimeoutMilliseconds,
                AddressType = (uint)addressType, ServiceUuid = CanonicalUuid(options.ServiceUuid),
                RxUuid = CanonicalUuid(options.WriteCharacteristicUuid), TxUuid = CanonicalUuid(options.NotifyCharacteristicUuid) };
            var normalized = deviceId.Trim();
            if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) normalized = normalized.Substring(2);
            normalized = normalized.Replace(":", "").Replace("-", "");
            if (normalized.Length > 0 && normalized.Length <= 12 && ulong.TryParse(normalized, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var address))
            { native.Selector = 1; native.Address = address; if (address == 0) throw new ArgumentException("Zero BLE address.", nameof(deviceId)); }
            else native.Selector = 2;
            var idBytes = native.Selector == 2 ? new UTF8Encoding(false, true).GetBytes(deviceId) : Array.Empty<byte>();
            if (idBytes.Length > 65536 || deviceId.IndexOf('\0') >= 0) throw new ArgumentException("Invalid Windows ID.", nameof(deviceId));
            await _connectionOperations.WaitAsync(cancellationToken).ConfigureAwait(false);
            ulong connection = 0;
            try
            {
                lock (_state.Gate)
                {
                    ThrowIfUnavailable();
                    if (_state.ActiveConnection != 0) throw new BleBridgeException(BleStatus.InvalidState, "Disconnect the current connection first.");
                    connection = ++_nextConnection; _state.ActiveConnection = connection;
                    _state.ConnectionError = null; _state.Notifications.Clear(); _state.NotificationBytes = 0;
                    _state.WakeNotifications();
                }
                var pending = new PendingOperation { Kind = OperationKind.Connect, Session = connection };
                try
                {
                    await SubmitAsync(OperationKind.Connect, connection, cancellationToken, id =>
                    {
                        var pin = GCHandle.Alloc(idBytes, GCHandleType.Pinned);
                        try
                        {
                            native.DeviceId = idBytes.Length == 0 ? IntPtr.Zero : pin.AddrOfPinnedObject();
                            native.DeviceIdLength = (uint)idBytes.Length;
                            return NativeMethods.wss_ble_nus_connect(_context, id, connection, ref native);
                        }
                        finally { pin.Free(); }
                    }, pending).ConfigureAwait(false);
                    lock (_state.Gate)
                    {
                        ThrowIfUnavailable();
                        if (!_state.Connected || _state.ActiveConnection != connection) throw _state.ConnectionError ?? new BleBridgeException(BleStatus.InvalidState, "Connection lost during activation.");
                        return new BleConnectionInfo(connection, pending.Gatt.MaxWriteLength);
                    }
                }
                catch
                {
                    await CompensateDisconnect(connection).ConfigureAwait(false);
                    throw;
                }
            }
            finally { _connectionOperations.Release(); }
        }

        public async Task SendAsync(byte[] data, CancellationToken cancellationToken = default)
        {
            RequireNus();
            if (data == null) throw new ArgumentNullException(nameof(data));
            var snapshot = (byte[])data.Clone();
            ulong connection;
            lock (_state.Gate) { ThrowIfUnavailable(); connection = _state.ActiveConnection; }
            await _sendOperations.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (_state.Gate)
                {
                    ThrowIfUnavailable();
                    if (!_state.Connected || connection == 0 || connection != _state.ActiveConnection)
                        throw _state.ConnectionError ?? new BleBridgeException(BleStatus.InvalidState, "BLE is not connected.");
                }
                var accepted = false;
                try
                {
                    await SubmitAsync(OperationKind.Write, connection, cancellationToken,
                        id =>
                        {
                            var status = NativeMethods.wss_ble_nus_write(_context, id, connection, snapshot, (uint)snapshot.Length, TimeoutMilliseconds);
                            accepted = status == BleStatus.Success; return status;
                        }).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { if (accepted) await CompensateSendDisconnect(connection).ConfigureAwait(false); throw; }
                catch (TimeoutException) { if (accepted) await CompensateSendDisconnect(connection).ConfigureAwait(false); throw; }
            }
            finally { _sendOperations.Release(); }
        }

        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            RequireNus();
            await _connectionOperations.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ulong connection;
                lock (_state.Gate)
                {
                    ThrowIfUnavailable(); connection = _state.ActiveConnection;
                    if (connection == 0) return;
                    _state.Connected = false; _state.MaxWriteLength = 0;
                    _state.Notifications.Clear(); _state.NotificationBytes = 0;
                    _state.WakeNotifications();
                }
                try { await DisconnectCore(connection, cancellationToken).ConfigureAwait(false); }
                catch { await CompensateDisconnect(connection).ConfigureAwait(false); throw; }
            }
            finally { _connectionOperations.Release(); }
        }

        public IReadOnlyList<byte[]> DrainNotifications(int maxNotifications = 256) =>
            DrainNotificationsCore(null, maxNotifications);

        // The generation check and dequeue must be atomic: an old pump must never drain a newer stream.
        public IReadOnlyList<byte[]> DrainNotifications(ulong connectionId, int maxNotifications = 256)
        {
            if (connectionId == 0) throw new ArgumentOutOfRangeException(nameof(connectionId));
            return DrainNotificationsCore(connectionId, maxNotifications);
        }

        private IReadOnlyList<byte[]> DrainNotificationsCore(ulong? connectionId, int maxNotifications)
        {
            if (maxNotifications < 1) throw new ArgumentOutOfRangeException(nameof(maxNotifications));
            lock (_state.Gate)
            {
                ThrowIfUnavailable();
                if (connectionId.HasValue && _state.ActiveConnection != connectionId.Value) return Array.Empty<byte[]>();
                if (_state.ConnectionError?.Status == BleStatus.Overflow) throw _state.ConnectionError;
                var result = new List<byte[]>();
                while (result.Count < maxNotifications && _state.Notifications.Count != 0)
                { var bytes = _state.Notifications.Dequeue(); _state.NotificationBytes -= bytes.Length; result.Add(bytes); }
                return result;
            }
        }

        // A single consumer owns draining. Cancellation affects this wait, not the connection.
        public async Task<bool> WaitForNotificationsAsync(ulong connectionId,
            CancellationToken cancellationToken = default)
        {
            if (connectionId == 0) throw new ArgumentOutOfRangeException(nameof(connectionId));
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Task changed;
                lock (_state.Gate)
                {
                    ThrowIfUnavailable();
                    if (_state.ActiveConnection != connectionId) return false;
                    if (_state.ConnectionError?.Status == BleStatus.Overflow) throw _state.ConnectionError;
                    if (!_state.Connected) return false;
                    if (_state.Notifications.Count != 0) return true;
                    changed = _state.NotificationChanged.Task;
                }
                if (!cancellationToken.CanBeCanceled) await changed.ConfigureAwait(false);
                else
                {
                    var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
                    {
                        await Task.WhenAny(changed, cancelled.Task).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        await changed.ConfigureAwait(false);
                    }
                }
            }
        }
        private uint TimeoutMilliseconds => checked((uint)Math.Ceiling(OperationTimeout.TotalMilliseconds));
        private Task<NativeEvent> DisconnectCore(ulong connection, CancellationToken token) => SubmitAsync(
            OperationKind.Disconnect, connection, token, id => NativeMethods.wss_ble_nus_disconnect(_context, id, connection, TimeoutMilliseconds));
        private async Task CompensateDisconnect(ulong connection)
        {
            try { await DisconnectCore(connection, CancellationToken.None).ConfigureAwait(false); }
            catch { Dispose(); }
        }
        private async Task CompensateSendDisconnect(ulong connection)
        {
            await _connectionOperations.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                // An asynchronous send continuation can resume after a later disconnect
                // fence and reconnect. Never let its old-generation cleanup destroy the
                // newer connection. Lifecycle callers already own this gate themselves.
                lock (_state.Gate)
                    if (_state.Disposed || _state.ActiveConnection != connection) return;
                await CompensateDisconnect(connection).ConfigureAwait(false);
            }
            finally { _connectionOperations.Release(); }
        }
        private void RequireNus()
        {
            lock (_state.Gate) ThrowIfUnavailable();
        }
        internal static byte[] CanonicalUuid(Guid uuid)
        {
            if (uuid == Guid.Empty) throw new ArgumentException("NUS UUIDs must not be empty.");
            var b = uuid.ToByteArray();
            Array.Reverse(b, 0, 4); Array.Reverse(b, 4, 2); Array.Reverse(b, 6, 2); return b;
        }
    }
}
