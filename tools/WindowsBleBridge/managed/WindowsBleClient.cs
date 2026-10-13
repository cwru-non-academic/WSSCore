using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Wss.Ble.Windows.Native.Interop;

namespace Wss.Ble.Windows.Native
{
    // Thread-safe raw BLE discovery/NUS. No user code is invoked from native callbacks.
    // Drain queues on the application's chosen thread; dispose before domain reload.
    public sealed partial class WindowsBleClient : IDisposable
    {
        private readonly CallbackState _state = new CallbackState();
        private readonly BleContextHandle _context;
        private readonly SemaphoreSlim _operations = new SemaphoreSlim(1, 1);
        private ulong _nextOperation, _nextSession, _scanSession;
        public TimeSpan OperationTimeout { get; }
        public string AbiVersion { get; }
        public BleBridgeException? LastScanError { get { lock (_state.Gate) return _state.ScanError; } }
        public ulong DroppedAdvertisements { get { lock (_state.Gate) return _state.Dropped; } }
        internal CallbackState State => _state;
        internal BleContextHandle Context => _context;

        public WindowsBleClient(TimeSpan? operationTimeout = null)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || IntPtr.Size != 8)
                throw new PlatformNotSupportedException("Windows BLE native bridge requires a Windows x64 process.");
            OperationTimeout = operationTimeout ?? TimeSpan.FromSeconds(10);
            if (OperationTimeout <= TimeSpan.Zero || OperationTimeout.TotalMilliseconds > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(operationTimeout));
            var info = new AbiInfo { Size = (uint)Marshal.SizeOf<AbiInfo>() };
            var status = NativeMethods.wss_ble_get_abi_info(ref info);
            if (status != BleStatus.Success) throw new BleBridgeException(status, "Native ABI query failed.");
            AbiValidation.Validate(info);
            AbiVersion = info.Major + "." + info.Minor;
            _context = BleContextHandle.Create(_state);
        }

        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await SubmitAsync(OperationKind.Initialize, 0, cancellationToken).ConfigureAwait(false); }
            finally { _operations.Release(); }
        }

        public async Task<BleAdapterState> QueryAdapterAsync(CancellationToken cancellationToken = default)
        {
            await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { return (await SubmitAsync(OperationKind.Query, 0, cancellationToken).ConfigureAwait(false)).Adapter; }
            finally { _operations.Release(); }
        }

        public async Task StartScanAsync(CancellationToken cancellationToken = default)
        {
            await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (_state.Gate)
                {
                    ThrowIfUnavailable();
                    if (_state.ActiveSession != 0) throw new BleBridgeException(BleStatus.InvalidState, "A scan is already active.");
                    _scanSession = ++_nextSession;
                    _state.ActiveSession = _scanSession;
                    _state.Results.Clear(); _state.Queue.Clear(); _state.ScanError = null; _state.Dropped = 0;
                }
                try { await SubmitAsync(OperationKind.Start, _scanSession, cancellationToken).ConfigureAwait(false); }
                catch
                {
                    // Cancellation can race a successful native Start. Always request its stop.
                    try { await SubmitAsync(OperationKind.Stop, _scanSession, CancellationToken.None).ConfigureAwait(false); }
                    catch { Dispose(); } // failed compensation cannot leave an unowned watcher
                    throw;
                }
            }
            finally { _operations.Release(); }
        }

        public async Task StopScanAsync(CancellationToken cancellationToken = default)
        {
            await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (_state.Gate) { ThrowIfUnavailable(); if (_scanSession == 0) return; }
                try { await SubmitAsync(OperationKind.Stop, _scanSession, cancellationToken).ConfigureAwait(false); }
                catch
                {
                    // A cancellation/rejection is not a native stop fence. Retry without caller
                    // cancellation before releasing the lifecycle gate; destroy if cleanup fails.
                    try { await SubmitAsync(OperationKind.Stop, _scanSession, CancellationToken.None).ConfigureAwait(false); }
                    catch { Dispose(); }
                    throw;
                }
            }
            finally { _operations.Release(); }
        }

        public IReadOnlyList<BleScanResult> DrainDiscovery(int maxResults = 256)
        {
            if (maxResults < 1) throw new ArgumentOutOfRangeException(nameof(maxResults));
            lock (_state.Gate)
            {
                ThrowIfUnavailable();
                var results = new List<BleScanResult>();
                while (results.Count < maxResults && _state.Queue.Count != 0) results.Add(_state.Queue.Dequeue());
                return results;
            }
        }

        public IReadOnlyList<BleScanResult> GetDiscoveredDevices()
        {
            lock (_state.Gate) { ThrowIfUnavailable(); return _state.Results.Values.ToArray(); }
        }

        private async Task<NativeEvent> SubmitAsync(OperationKind kind, ulong session, CancellationToken token,
            Func<ulong, BleStatus>? submit = null, PendingOperation? operation = null)
        {
            token.ThrowIfCancellationRequested();
            var pending = operation ?? new PendingOperation { Kind = kind, Session = session };
            ulong id;
            lock (_state.Gate)
            {
                ThrowIfUnavailable();
                id = ++_nextOperation;
                _state.Pending.Add(id, pending);
                try
                {
                    BleStatus status;
                    if (submit != null) status = submit(id);
                    else switch (kind)
                    {
                        case OperationKind.Initialize: status = NativeMethods.wss_ble_initialize(_context, id); break;
                        case OperationKind.Query: status = NativeMethods.wss_ble_query_adapter(_context, id); break;
                        case OperationKind.Start: status = NativeMethods.wss_ble_scan_start(_context, id, session); break;
                        default: status = NativeMethods.wss_ble_scan_stop(_context, id, session); break;
                    }
                    if (status != BleStatus.Success) throw new BleBridgeException(status, "Native command rejected: " + status);
                }
                catch { _state.Pending.Remove(id); throw; }
            }
            using (token.Register(() => RequestCancel(id, false)))
            using (var timer = new Timer(_ => RequestCancel(id, true), null, OperationTimeout, Timeout.InfiniteTimeSpan))
                return await pending.Source.Task.ConfigureAwait(false);
        }

        private void RequestCancel(ulong id, bool timedOut)
        {
            lock (_state.Gate)
            {
                if (_state.Disposed || !_state.Pending.TryGetValue(id, out var pending) || pending.Source.Task.IsCompleted) return;
                // Local completion is bounded even if the OS operation does not respond to Cancel.
                if (timedOut) pending.Source.TrySetException(new TimeoutException("Windows BLE operation exceeded " + OperationTimeout + "."));
                else pending.Source.TrySetCanceled();
                try
                {
                    var status = NativeMethods.wss_ble_cancel(_context, id);
                    if (status != BleStatus.Success)
                    {
                        var error = new BleBridgeException(status, "Native cancellation request failed.");
                        if (pending.Kind >= OperationKind.Connect) _state.ConnectionError = error; else _state.ScanError = error;
                    }
                }
                catch (Exception ex)
                {
                    var error = new BleBridgeException(BleStatus.NativeFailure, "Native cancellation failed: " + ex.Message);
                    if (pending.Kind >= OperationKind.Connect) _state.ConnectionError = error; else _state.ScanError = error;
                }
            }
        }

        private void ThrowIfUnavailable()
        {
            if (_state.Disposed) throw new ObjectDisposedException(nameof(WindowsBleClient));
            if (_state.CallbackError != null) throw _state.CallbackError;
        }

        public void Dispose()
        {
            _state.Close();
            _context.Dispose();
            // Do not dispose the semaphore while asynchronous callers may still release it.
        }
    }
}
