using System;
using System.Runtime.InteropServices;

namespace Wss.Ble.Windows.Native.Interop
{
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct AbiInfo
    {
        internal uint Size, Major, Minor, PointerSize, EventSize, AdvertisementSize, Capabilities, Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct NativeEvent
    {
        internal uint Size, Kind;
        internal ulong OperationId, SessionId;
        internal BleStatus Status;
        internal int HResult;
        internal uint BluetoothError;
        internal BleAdapterState Adapter;
        internal IntPtr Advertisement;
        internal ulong DroppedCount;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct NativeAdvertisement
    {
        internal uint Size;
        internal BleAddressType AddressType;
        internal ulong Address;
        internal int Rssi;
        internal uint RssiValid;
        internal IntPtr Name;
        internal uint NameLength, UuidCount;
        internal IntPtr Uuids;
    }

    internal static class AbiValidation
    {
        internal static void Validate(AbiInfo info)
        {
            if (IntPtr.Size != 8)
                throw new PlatformNotSupportedException("The Windows BLE bridge supports x64 processes only.");
            if (info.Size != Marshal.SizeOf<AbiInfo>() || info.Major != 1 || info.Minor < 1 || info.PointerSize != 8 ||
                info.EventSize != Marshal.SizeOf<NativeEvent>() || info.AdvertisementSize != Marshal.SizeOf<NativeAdvertisement>() ||
                (info.Capabilities & 3) != 3 || Marshal.SizeOf<NativeEvent>() != 56 || Marshal.SizeOf<NativeAdvertisement>() != 48 ||
                Marshal.OffsetOf<NativeEvent>(nameof(NativeEvent.Advertisement)).ToInt32() != 40 ||
                Marshal.OffsetOf<NativeAdvertisement>(nameof(NativeAdvertisement.Name)).ToInt32() != 24)
                throw new BleBridgeException(BleStatus.UnsupportedAbi, "Native BLE ABI version, capabilities, or layouts are incompatible.");
        }
    }
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct NativeNusOptions
    {
        internal uint Size, Selector, AddressType, Timeout;
        internal ulong Address;
        internal IntPtr DeviceId;
        internal uint DeviceIdLength, Reserved;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] ServiceUuid;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] RxUuid;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] TxUuid;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct NativeGattEvent
    {
        internal NativeEvent Base;
        internal uint Stage, GattStatus, ProtocolError, MaxWriteLength;
        internal IntPtr Data;
        internal uint DataLength, Reserved;
    }
}
