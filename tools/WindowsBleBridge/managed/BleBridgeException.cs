using System;

namespace Wss.Ble.Windows.Native
{
    public enum BleStatus
    {
        Success = 0, InvalidArgument = 1, InvalidState = 2, UnsupportedAbi = 3,
        Cancelled = 4, TimedOut = 5, AdapterUnavailable = 6, BluetoothDisabled = 7,
        AccessDenied = 8, NativeFailure = 9, Busy = 10, Overflow = 11, Closing = 12
    }
    public enum BleAdapterState { Unknown = 0, Absent = 1, NoLowEnergySupport = 2, Ready = 3, Off = 4 }
    public enum BleAddressType { Unspecified = 0, Public = 1, Random = 2 }

    public sealed class BleBridgeException : Exception
    {
        public BleStatus Status { get; }
        public int NativeHResult { get; }
        public uint BluetoothError { get; }
        public uint GattStage { get; }
        public uint GattStatus { get; }
        public uint ProtocolError { get; }
        public BleBridgeException(BleStatus status, string message, int nativeHResult = 0, uint bluetoothError = 0)
            : this(status, message, nativeHResult, bluetoothError, 0, uint.MaxValue, uint.MaxValue) { }

        public BleBridgeException(BleStatus status, string message, int nativeHResult, uint bluetoothError,
            uint gattStage, uint gattStatus, uint protocolError)
            : base(message)
        {
            Status = status;
            NativeHResult = nativeHResult;
            BluetoothError = bluetoothError;
            GattStage = gattStage; GattStatus = gattStatus; ProtocolError = protocolError;
        }
    }
}
