using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Wss.Transports.Backends.MacOS;

internal sealed class MacEvent
{
    internal uint Kind, Budget;
    internal ulong Operation, Session;
    internal int Status;
    internal byte[] Data = Array.Empty<byte>();
}

// Fake implementations exercise the managed lifecycle without pretending to prove radio I/O.
internal interface IMacNative : IDisposable
{
    event Action<MacEvent>? Event;
    int Submit(uint command, ulong operation, ulong session, string? device, string? service,
        string? rx, string? tx, byte[] data, uint timeout);
    void Cancel(ulong operation);
}

internal sealed class MacNative : IMacNative
{
    private const string Library = "wss_macos_ble";
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Callback(IntPtr user, uint kind, ulong operation, ulong session,
        int status, uint budget, IntPtr data, uint length);
    private sealed class Handle : SafeHandleZeroOrMinusOneIsInvalid
    {
        // Keep the delegate alive through the synchronous native destroy fence.
        private readonly Callback _callback;
        internal Handle(Callback callback) : base(true) { _callback = callback; SetHandle(Create(callback, IntPtr.Zero)); }
        protected override bool ReleaseHandle() { Destroy(handle); GC.KeepAlive(_callback); return true; }
    }
    private readonly Handle _handle;
    public event Action<MacEvent>? Event;
    internal MacNative()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX) || IntPtr.Size != 8)
            throw new PlatformNotSupportedException("CoreBluetooth requires a 64-bit macOS process.");
        if (Abi() != 0x10000) throw new InvalidOperationException("Unsupported macOS BLE ABI; deploy the matching universal payload.");
        _handle = new Handle(Receive);
        if (_handle.IsInvalid) { _handle.Dispose(); throw new InvalidOperationException("Unable to create CoreBluetooth context."); }
    }
    private void Receive(IntPtr user, uint kind, ulong operation, ulong session, int status, uint budget, IntPtr data, uint length)
    {
        try
        {
            if (length > 1024 * 1024 || (length != 0 && data == IntPtr.Zero)) throw new InvalidOperationException("Invalid native BLE event.");
            var copy = new byte[length];
            if (length != 0) Marshal.Copy(data, copy, 0, checked((int)length));
            Event?.Invoke(new MacEvent { Kind = kind, Operation = operation, Session = session, Status = status, Budget = budget, Data = copy });
        }
        catch { /* Exceptions must never cross the C callback boundary. */ }
    }
    public int Submit(uint command, ulong operation, ulong session, string? device, string? service,
        string? rx, string? tx, byte[] data, uint timeout) =>
        SubmitNative(_handle, command, operation, session, Utf8(device), Utf8(service), Utf8(rx), Utf8(tx), data, (uint)data.Length, timeout);
    private static byte[]? Utf8(string? value) => value == null ? null : System.Text.Encoding.UTF8.GetBytes(value + "\0");
    public void Cancel(ulong operation) => CancelNative(_handle, operation);
    public void Dispose() { _handle.Dispose(); Event = null; }
    [DllImport(Library, EntryPoint="wss_mac_abi", CallingConvention=CallingConvention.Cdecl)] private static extern uint Abi();
    [DllImport(Library, EntryPoint="wss_mac_create", CallingConvention=CallingConvention.Cdecl)] private static extern IntPtr Create(Callback callback, IntPtr user);
    [DllImport(Library, EntryPoint="wss_mac_destroy", CallingConvention=CallingConvention.Cdecl)] private static extern void Destroy(IntPtr context);
    [DllImport(Library, EntryPoint="wss_mac_cancel", CallingConvention=CallingConvention.Cdecl)] private static extern void CancelNative(Handle context, ulong operation);
    [DllImport(Library, EntryPoint="wss_mac_submit", CallingConvention=CallingConvention.Cdecl)] private static extern int SubmitNative(Handle context,
        uint command, ulong operation, ulong session, byte[]? device,
        byte[]? service, byte[]? rx, byte[]? tx, byte[] data, uint length, uint timeout);
}
