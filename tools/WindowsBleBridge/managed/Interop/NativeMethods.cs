using System;
using System.Runtime.InteropServices;

namespace Wss.Ble.Windows.Native.Interop
{
    internal static class NativeMethods
    {
        private const string Library = "WssBleWindows.dll";
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void Callback(IntPtr user, IntPtr nativeEvent);
        // A static, process-rooted delegate; native never retains a managed instance delegate.
        internal static readonly Callback CallbackRoot = CallbackState.Receive;

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern BleStatus wss_ble_get_abi_info(ref AbiInfo info);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern BleStatus wss_ble_create(uint major, Callback callback, IntPtr user, out IntPtr context);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern BleStatus wss_ble_initialize(BleContextHandle context, ulong id);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern BleStatus wss_ble_query_adapter(BleContextHandle context, ulong id);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern BleStatus wss_ble_scan_start(BleContextHandle context, ulong id, ulong session);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern BleStatus wss_ble_scan_stop(BleContextHandle context, ulong id, ulong session);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern BleStatus wss_ble_cancel(BleContextHandle context, ulong id);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern BleStatus wss_ble_destroy(IntPtr context);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern BleStatus wss_ble_nus_connect(BleContextHandle context, ulong id, ulong connection, ref NativeNusOptions options);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern BleStatus wss_ble_nus_write(BleContextHandle context, ulong id, ulong connection, byte[] bytes, uint length, uint timeout);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        internal static extern BleStatus wss_ble_nus_disconnect(BleContextHandle context, ulong id, ulong connection, uint timeout);
    }
}
