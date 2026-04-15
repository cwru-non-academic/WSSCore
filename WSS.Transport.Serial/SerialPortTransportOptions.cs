using System.IO.Ports;

namespace Wss.CoreModule
{
    /// <summary>
    /// Holds connection settings for <see cref="SerialPortTransport"/>.
    /// </summary>
    /// <remarks>
    /// Exactly one port selection mode must be used: either set <see cref="AutoSelectPort"/> to true,
    /// or provide a non-empty <see cref="PortName"/>.
    /// </remarks>
    public sealed class SerialPortTransportOptions
    {
        /// <summary>
        /// Gets or sets the explicit port name.
        /// Use values such as <c>COM5</c> on Windows or <c>/dev/ttyUSB0</c> on Linux/macOS.
        /// This must be provided when <see cref="AutoSelectPort"/> is false.
        /// </summary>
        public string PortName { get; set; }

        /// <summary>
        /// Gets or sets whether the transport should automatically select the best available serial port.
        /// When true, <see cref="PortName"/> must be left empty.
        /// </summary>
        public bool AutoSelectPort { get; set; }

        /// <summary>
        /// Gets or sets the baud rate. The default is 115200.
        /// </summary>
        public int Baud { get; set; } = 115200;

        /// <summary>
        /// Gets or sets the parity mode. The default is <see cref="Parity.None"/>.
        /// </summary>
        public Parity Parity { get; set; } = Parity.None;

        /// <summary>
        /// Gets or sets the number of data bits. The default is 8.
        /// </summary>
        public int DataBits { get; set; } = 8;

        /// <summary>
        /// Gets or sets the stop-bit configuration. The default is <see cref="StopBits.One"/>.
        /// </summary>
        public StopBits StopBits { get; set; } = StopBits.One;

        /// <summary>
        /// Gets or sets the synchronous serial read timeout, in milliseconds.
        /// The default is 10.
        /// </summary>
        public int ReadTimeoutMs { get; set; } = 10;
    }
}
