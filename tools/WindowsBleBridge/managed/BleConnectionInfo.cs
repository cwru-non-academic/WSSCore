namespace Wss.Ble.Windows.Native
{
    public sealed class BleConnectionInfo
    {
        public ulong ConnectionId { get; }
        public uint MaxWriteLength { get; }
        internal BleConnectionInfo(ulong connectionId, uint maxWriteLength)
        { ConnectionId = connectionId; MaxWriteLength = maxWriteLength; }
    }
}
