using System;
using System.Collections.Generic;
using System.Globalization;

namespace Wss.Ble.Windows.Native
{
    public sealed class BleScanResult
    {
        public ulong Address { get; }
        public string Id => Address.ToString("X12", CultureInfo.InvariantCulture);
        public BleAddressType AddressType { get; }
        public string? Name { get; }
        public int? Rssi { get; }
        public IReadOnlyList<Guid> ServiceUuids { get; }
        internal BleScanResult(ulong address, BleAddressType type, string? name, int? rssi, Guid[] uuids)
        {
            Address = address; AddressType = type; Name = name; Rssi = rssi;
            ServiceUuids = Array.AsReadOnly(uuids);
        }
    }
}
