using System;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Serial-port implementation of <see cref="ITransport"/> for WSS communications.
/// Wraps <see cref="SerialPort"/> and exposes an event-driven receive API that works well in Unity/.NET Standard 2.0.
/// </summary>
/// <remarks>
/// <para><b>Threading:</b> <see cref="BytesReceived"/> is raised from a background task. If your app (e.g., Unity)
/// requires main-thread processing, marshal the callback to the main thread before touching UI/GameObjects.</para>
/// <para><b>Chunking:</b> Serial I/O does not preserve message boundaries. Each chunk may contain partial frames or
/// multiple frames. Always pass chunks to a frame codec/deframer.</para>
/// <para><b>Timeouts:</b> The read loop polls <see cref="SerialPort.BytesToRead"/> and catches <see cref="TimeoutException"/>s,
/// which are normal with short read timeouts.</para>
/// </remarks>
public sealed class SerialPortTransport : ITransport
{
    private readonly SerialPort _port;
    private CancellationTokenSource _cts;
    private Task _readLoop;

    /// <summary>
    /// Creates a serial transport bound to the given port name and parameters.
    /// </summary>
    /// <param name="portName">Port name (e.g., <c>"COM5"</c> on Windows, <c>"/dev/ttyUSB0"</c> on Linux/macOS).</param>
    /// <param name="baud">Baud rate. Default is 115200.</param>
    /// <param name="parity">Parity setting. Default is <see cref="Parity.None"/>.</param>
    /// <param name="dataBits">Data bits. Default is 8.</param>
    /// <param name="stopBits">Stop bits. Default is <see cref="StopBits.One"/>.</param>
    /// <param name="readTimeoutMs">
    /// Synchronous read timeout in milliseconds (used by <see cref="SerialPort.Read(byte[], int, int)"/>).
    /// Typical value is small (e.g., 10ms). Timeouts are caught and ignored in the read loop.
    /// </param>
    public SerialPortTransport(string portName, int baud = 115200, Parity parity = Parity.None,
                               int dataBits = 8, StopBits stopBits = StopBits.One, int readTimeoutMs = 10)
    {
        _port = new SerialPort(portName, baud, parity, dataBits, stopBits) { ReadTimeout = readTimeoutMs };
    }

    /// <summary>
    /// Gets whether the underlying serial port is currently open.
    /// </summary>
    public bool IsConnected => _port.IsOpen;

    /// <summary>
    /// Raised when raw bytes are received from the serial port.
    /// Each invocation may contain any number of bytes and may include partial or multiple protocol frames.
    /// </summary>
    /// <remarks>
    /// This event is typically raised on a background thread by the internal read loop.
    /// Consumers should copy the buffer if it needs to be retained beyond the callback.
    /// </remarks>
    public event Action<byte[]> BytesReceived;

    /// <summary>
    /// Opens the serial port and starts a background read loop that forwards incoming data to <see cref="BytesReceived"/>.
    /// </summary>
    /// <param name="ct">Optional cancellation token to abort the connect/start operation.</param>
    /// <returns>A completed task once the port is open and the read loop has been scheduled.</returns>
    /// <exception cref="UnauthorizedAccessException">Access to the port is denied.</exception>
    /// <exception cref="IOException">The specified port could not be found or opened.</exception>
    /// <exception cref="OperationCanceledException">If <paramref name="ct"/> is canceled before opening.</exception>
    public Task ConnectAsync(CancellationToken ct = default)
    {
        _port.Open();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _readLoop = Task.Run(ReadLoopAsync, _cts.Token);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the background read loop and closes the serial port.
    /// </summary>
    /// <param name="ct">Optional cancellation token to abort a long-running disconnect.</param>
    /// <returns>A task that completes after the read loop has exited and the port is closed.</returns>
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        try
        {
            _cts?.Cancel();
            if (_readLoop != null)
                await _readLoop.ConfigureAwait(false); // ignore cancellations below
        }
        catch
        {
            // ignored
        }

        if (_port.IsOpen) _port.Close();
    }

    /// <summary>
    /// Sends the specified bytes over the serial port.
    /// </summary>
    /// <param name="data">Data to write. The buffer is written synchronously to the port.</param>
    /// <param name="ct">
    /// Optional cancellation token. For synchronous serial writes, cancellation is only observed before the write begins.
    /// </param>
    /// <returns>A completed task once the bytes are written and the base stream is flushed.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the port is not open.</exception>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="data"/> is null.</exception>
    public Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        if (!_port.IsOpen) throw new InvalidOperationException("Transport is not connected.");

        _port.Write(data, 0, data.Length);
        _port.BaseStream.Flush();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Background task that polls the serial port and raises <see cref="BytesReceived"/> when data arrives.
    /// </summary>
    /// <remarks>
    /// The loop polls <see cref="SerialPort.BytesToRead"/> and performs short delays (2ms) when idle
    /// to reduce CPU usage. It exits on cancellation or unexpected exceptions.
    /// </remarks>
    private async Task ReadLoopAsync()
    {
        var token = _cts.Token;
        var buf = new byte[256];

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (_port.BytesToRead > 0)
                {
                    var read = _port.Read(buf, 0, buf.Length);
                    if (read > 0)
                    {
                        var chunk = new byte[read];
                        Buffer.BlockCopy(buf, 0, chunk, 0, read);
                        BytesReceived?.Invoke(chunk);
                    }
                }
                else
                {
                    await Task.Delay(2, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { /* normal during shutdown */ }
            catch (TimeoutException) { /* normal with short read timeouts */ }
            catch (Exception)
            {
                // TODO(YourName, yyyy-mm-dd): Consider surfacing an OnError event or logging this exception.
                break;
            }
        }
    }

    /// <summary>
    /// Disposes the serial port and stops the read loop.
    /// </summary>
    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { /* ignored */ }
        _port?.Dispose();
    }
}
