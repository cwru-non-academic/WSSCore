using System;

namespace Wss.Ble.Windows.Native
{
    public sealed class BleNusOptions
    {
        public Guid ServiceUuid { get; set; } = new Guid("6E400001-B5A3-F393-E0A9-E50E24DCCA9E");
        public Guid WriteCharacteristicUuid { get; set; } = new Guid("6E400002-B5A3-F393-E0A9-E50E24DCCA9E");
        public Guid NotifyCharacteristicUuid { get; set; } = new Guid("6E400003-B5A3-F393-E0A9-E50E24DCCA9E");
    }
}
