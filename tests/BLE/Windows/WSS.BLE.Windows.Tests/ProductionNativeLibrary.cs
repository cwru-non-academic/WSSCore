using System.Reflection;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Wss.Ble.Windows.Native;
using Wss.Ble.Windows.Native.Interop;

namespace WSS.BLE.Windows.Tests;

internal static class ProductionNativeLibrary
{
    internal static string Path => System.IO.Path.GetFullPath(
        Environment.GetEnvironmentVariable("WSS_BLE_PRODUCTION_DLL") ??
        typeof(ProductionNativeLibrary).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "ProductionNativePath").Value!);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate BleStatus GetInfo(ref AbiInfo info);

    internal static IntPtr Load()
    {
        Assert.That(File.Exists(Path), Is.True, "Build the production native DLL first: " + Path);
        var module = NativeLibrary.Load(Path);
        try
        {
            Assert.That(NativeLibrary.TryGetExport(module, "wss_test_mode", out _), Is.False, "Production tests must never load the shim.");
            var info = new AbiInfo { Size = (uint)Marshal.SizeOf<AbiInfo>() };
            Assert.That(Export<GetInfo>(module, "wss_ble_get_abi_info")(ref info), Is.EqualTo(BleStatus.Success));
            AbiValidation.Validate(info);
            return module;
        }
        catch { NativeLibrary.Free(module); throw; }
    }

    internal static T Export<T>(IntPtr module, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(module, name));
}
