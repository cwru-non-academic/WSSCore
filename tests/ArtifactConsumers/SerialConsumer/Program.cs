using Wss.Transports;

var assembly = typeof(SerialPortTransport).Assembly;
if (assembly.GetName().Name != "WSS.Transport.Serial")
{
    throw new InvalidOperationException("Standalone serial artifact was not loaded.");
}

var options = new SerialPortTransportOptions { PortName = "WssConsumerSmokePort" };
using var transport = new SerialPortTransport(options);
if (transport.IsConnected)
{
    throw new InvalidOperationException("A newly constructed serial transport must not be connected.");
}

Console.WriteLine("Serial artifact consumer passed.");
