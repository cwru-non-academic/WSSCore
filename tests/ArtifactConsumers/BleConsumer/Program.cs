using System.Reflection;
using Wss.Transports;

foreach (var assemblyName in new[] { "WSS.Transport.BLE", "InTheHand.BluetoothLE", "Linux.Bluetooth", "Tmds.DBus" })
{
    _ = Assembly.Load(assemblyName);
}

var options = new BleNusTransportOptions { DeviceName = "WssConsumerSmokeDevice" };
using var transport = new BleNusTransport(options);
if (transport.IsConnected || options.ServiceUuid == Guid.Empty)
{
    throw new InvalidOperationException("BLE transport smoke check failed.");
}

Console.WriteLine("BLE artifact consumer passed.");
