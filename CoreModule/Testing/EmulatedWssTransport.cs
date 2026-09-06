using System;
using System.Threading;
using System.Threading.Tasks;
using Wss.Transports;

namespace Wss.Testing
{
    /// <summary>
    /// Provides a deterministic in-memory byte transport backed by an emulated WSS device.
    /// </summary>
    /// <remarks>
    /// Replies are raised synchronously through <see cref="BytesReceived"/> during <see cref="SendAsync"/>.
    /// No serial port, socket, hardware, or background worker is used.
    /// </remarks>
    public sealed class EmulatedWssTransport : ITransport, IConformanceProvider
    {
        private readonly object _gate = new object();
        private readonly EmulatedWssDevice _device;
        private readonly WssConformance _conformance;
        private bool _connected;
        private bool _disposed;

        /// <summary>
        /// Creates the transport and its single associated device and conformance view.
        /// </summary>
        public EmulatedWssTransport()
        {
            _device = new EmulatedWssDevice();
            _conformance = new WssConformance(_device);
        }

        /// <inheritdoc/>
        public bool IsConnected
        {
            get
            {
                lock (_gate)
                {
                    return _connected;
                }
            }
        }

        /// <inheritdoc/>
        public IWssConformance Conformance => _conformance;

        /// <inheritdoc/>
        public event Action<byte[]> BytesReceived;

        /// <inheritdoc/>
        public Task ConnectAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                ct.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                _connected = true;
            }
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task DisconnectAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                ct.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                _connected = false;
            }
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task SendAsync(byte[] data, CancellationToken ct = default)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            ct.ThrowIfCancellationRequested();

            lock (_gate)
            {
                ct.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                if (!_connected) throw new InvalidOperationException("Transport is not connected.");

                var replies = _device.ProcessBytes(data);
                for (int i = 0; i < replies.Count; i++)
                    BytesReceived?.Invoke(replies[i]);
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _connected = false;
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(EmulatedWssTransport));
        }
    }
}
