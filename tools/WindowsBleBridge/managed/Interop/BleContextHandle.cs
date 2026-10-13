using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Wss.Ble.Windows.Native.Interop
{
    internal sealed class BleContextHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private GCHandle _callbackState;
        private BleContextHandle(GCHandle state) : base(true)
        {
            _callbackState = state;
        }

        internal static BleContextHandle Create(CallbackState state)
        {
            var root = GCHandle.Alloc(state);
            try
            {
                // Allocate the managed owner before native creates resources. After success,
                // adopting the returned pointer does not require another managed allocation.
                var owner = new BleContextHandle(root);
                var status = NativeMethods.wss_ble_create(1, NativeMethods.CallbackRoot, GCHandle.ToIntPtr(root), out var context);
                if (status != BleStatus.Success) throw new BleBridgeException(status, "Create native BLE context failed.");
                if (context == IntPtr.Zero) throw new BleBridgeException(BleStatus.NativeFailure, "Native create returned a null context.");
                owner.SetHandle(context);
                return owner;
            }
            catch { root.Free(); throw; }
        }

        protected override bool ReleaseHandle()
        {
            try
            {
                if (_callbackState.Target is CallbackState state) state.Close();
                // Native destroy joins its dispatcher: only THEN may the caller pointer be freed.
                if (NativeMethods.wss_ble_destroy(handle) != BleStatus.Success) return false;
                if (_callbackState.IsAllocated) _callbackState.Free();
                return true;
            }
            catch { return false; } // preserve callback root on failed quiescence, never free unsafely
        }
    }
}
