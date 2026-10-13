# Universal WSS artifact

Run from the repository root on Windows with .NET 9, VS 2022/MSVC v143,
Windows SDK 10.0.22621.0, and NuGet access:

```powershell
dotnet run --project tools/ArtifactBuild/ArtifactBuild.csproj -- build-all --destination "<new-directory>/WSS-Universal" --tag local
```

`build-all` builds the Windows adapter's managed graph, the Linux backend without
rebuilding shared projects, and the production native Release/x64 DLL. Its child
PowerShell process uses a process-only execution-policy bypass, never a machine/user
policy change. Direct MSBuild instructions are in the [bridge guide](../WindowsBleBridge/README.md).
`--no-build` reuses these source outputs after successful builds/tests; it does not
load a prior distribution. The destination must be new.

## Exact payload

```text
WSS-Universal/
  WSS_Core_Interface.dll
  WSS.Transport.Serial.dll
  WSS.Transport.BLE.dll
  Newtonsoft.Json.dll
  System.IO.Ports.dll
  System.Memory.dll
  System.Buffers.dll
  System.Numerics.Vectors.dll
  System.Runtime.CompilerServices.Unsafe.dll
  System.Security.Permissions.dll
  System.Security.AccessControl.dll
  System.Security.Principal.Windows.dll
  backends/windows/
    WSS.Transport.BLE.Windows.dll
    WSS.Transport.BLE.Windows.Native.dll
    WssBleWindows.dll
  backends/linux/
    WSS.Transport.BLE.Linux.dll
    Linux.Bluetooth.dll
    Tmds.DBus.dll
    Microsoft.Bcl.AsyncInterfaces.dll
    System.Reflection.Emit.dll
    System.Reflection.Emit.ILGeneration.dll
    System.Threading.Tasks.Extensions.dll
  runtimes/win/lib/net8.0/System.IO.Ports.dll
  runtimes/unix/lib/net8.0/System.IO.Ports.dll
  runtimes/linux-x64/native/libSystem.IO.Ports.Native.so
```

The 25-file contract is defined by `manifests/universal.json`. All six WSS managed
assemblies are Standard 2.0. Windows native code is production AMD64 ABI 1.1;
Linux serial native code is ELF64 x86-64. BlueZ/system D-Bus and OS device permissions
are prerequisites, not bundled dependencies.

Ports 8.0.0 has an intentional portable root net462 implementation plus Windows/Unix
net8 runtime implementations. Normal .NET applications use Ports 8.0.0 NuGet runtime
metadata and must not overwrite the selected runtime DLL with the portable alternate.
Linux.Bluetooth 5.67.1 and Tmds.DBus 0.20.0 use their Standard 2.0 assets. Shared
Security/Memory support dependencies remain required; they are not a WinRT backend.

Staging validates restored runtime assets and selected shared bytes. Validation checks
exact files, assembly metadata/frameworks, reference closure, native architectures,
and the exact production export set. It rejects WinRT payloads, shims, unexpected
files, duplicate shared identities, and incompatible dependency versions. Existing
Core unifications for Buffers, Vectors, and Unsafe are explicitly allowed.

## ZIP, checksums, and validation

The command writes `WSS-Universal-<TAG>.zip` and `SHA256SUMS.txt` beside the payload.
ZIP entries have stable ordinal ordering and timestamps; identical payload bytes
produce identical ZIP bytes. Native compilation reproducibility across toolchains
or source locations is not claimed.

```powershell
./tools/ArtifactBuild/extract-universal.ps1 -ReleaseAssets "<release-assets-directory>" -Destination "<new-extraction-directory>"
dotnet run --project tools/ArtifactBuild/ArtifactBuild.csproj -- validate --manifest tools/ArtifactBuild/manifests/universal.json --package "<extraction-directory>/WSS-Universal"
python tests/Packaging/verify_universal.py "<extraction-directory>/WSS-Universal"
```

Extraction requires exactly the ZIP and its checksum and verifies SHA256 before
unpacking. The package-contract script exercises rejection cases without hardware.
`stage`, `validate`, `describe`, and `references` remain useful manifest-driven
inspection utilities. See [test commands](../../tests/README.md) for artifact consumers.

## Deployment and selection

Preserve the complete original payload for redistribution, including both backends
and runtime trees regardless of build-host OS. Reference shared Core/Serial/BLE DLLs
copy-local and avoid duplicate copies in backend directories. Runnable .NET outputs
may omit the root alternate Ports implementation while retaining the complete original
artifact separately. Do not treat that runnable subset as the universal distribution.

Automatic selection probes `AppContext.BaseDirectory/backends/<platform>` using
ordinary shared assembly loading. Windows loads `WSS.Transport.BLE.Windows.dll`;
Linux loads `WSS.Transport.BLE.Linux.dll`. Explicit preloading/registration is supported:
`Wss.Transports.Backends.Windows.WindowsBleBackend.Register()` or
`Wss.Transports.Backends.Linux.LinuxBleBackend.Register()`.

Keep the production native DLL adjacent to its managed wrapper and ensure the host's
native resolver can find it. Unity deployment requires plugin importer configuration
and runtime validation, not a separate release download. Rebuild internal consumers;
no pre-release binary compatibility aliases are provided.

## CI/release gate

The existing artifact workflow runs Linux portable and Windows native/adapter tests,
packages once, and gives every consumer job the same ZIP. Core/Serial smoke checks
run on Windows/Linux/macOS; BLE checks run on Windows/Linux. Linux no-controller
classification skips only the native controller probe, never dependency checks.
Case-sensitive duplicate rejection is a Linux gate. Tag-gated publication reuses the
validated ZIP/checksum without rebuilding, restaging, or rezipping.
