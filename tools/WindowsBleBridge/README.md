# Windows BLE bridge

The production Windows stack has three separate layers:

- `WSS.Transport.BLE.Windows.dll`: implements the shared facade contract, discovery
  selection, notification delivery, and logical-send fragmentation.
- `managed/WSS.Transport.BLE.Windows.Native.csproj`: Standard 2.0 P/Invoke wrapper,
  callbacks, marshaling, and resource ownership.
- `native/WssBleWindows.vcxproj`: C++/WinRT `WssBleWindows.dll`, using Windows Bluetooth APIs.

The wrapper/native bridge transports raw bytes, not WSS protocol packets. The
production bridge is included in the [universal artifact](../ArtifactBuild/README.md).
The synthetic test shim is never shipped.

## Toolchain

Install Visual Studio 2022 Community or Build Tools with Desktop development with C++:

| Component | Installer identifier |
| --- | --- |
| Community C++ workload | `Microsoft.VisualStudio.Workload.NativeDesktop` |
| Build Tools C++ workload | `Microsoft.VisualStudio.Workload.VCTools` |
| MSVC v143 x64/x86 tools | `Microsoft.VisualStudio.Component.VC.Tools.x86.x64` |
| Windows SDK 10.0.22621.0 | `Microsoft.VisualStudio.Component.Windows11SDK.22621` |

Community can import [windows-build.vsconfig](windows-build.vsconfig); Build Tools
uses a different workload identifier. VS Code extensions do not install the compiler,
MSBuild, or SDK. Install .NET 8 for tests/diagnostics and .NET 9 for ArtifactBuild.
C++/WinRT **2.0.240405.15** is pinned in `native/packages.config`; restore needs NuGet.
C++17 and the static MSVC runtime are selected by the native projects. No C++/CLI,
managed WinRT projection, ARM64, x86, or UWP workload is required.

## Build from the repository root

```powershell
./tools/WindowsBleBridge/build.ps1 -Configuration Release -Architecture x64
```

The script locates VS2022 MSBuild, restores the bridge solution, builds production
native code and the separate shim, then builds/stages the managed diagnostic and test
projects. `-ProductionOnly` omits shim and consumer builds. The script never changes
machine/user execution policy. If scripts cannot run, use the direct commands below
in an **x64 Native Tools Command Prompt for VS 2022** (`cmd.exe`):

```bat
where cl
where msbuild
msbuild tools\WindowsBleBridge\WindowsBleBridge.sln /t:Restore /p:RestorePackagesConfig=true /p:Configuration=Release /p:Platform=x64 /p:WindowsTargetPlatformVersion=10.0.22621.0
msbuild tools\WindowsBleBridge\native\WssBleWindows.vcxproj /t:Build /m /p:Configuration=Release /p:Platform=x64 /p:WindowsTargetPlatformVersion=10.0.22621.0
msbuild tests\BLE\Windows\NativeTestShim\WssBleWindows.TestShim.vcxproj /t:Build /m /p:Configuration=Release /p:Platform=x64 /p:WindowsTargetPlatformVersion=10.0.22621.0
dotnet build tests\BLE\Windows\ScanConsumer\ScanConsumer.csproj -c Release
dotnet build tests\BLE\Windows\WSS.BLE.Windows.Tests\WSS.BLE.Windows.Tests.csproj -c Release
```

Stop at a failed restore/build. Use Visual Studio MSBuild, not `dotnet msbuild`,
for C++ projects. Build native DLLs before managed projects: the test project copies
only the shim; ScanConsumer copies only production code. Production output is
`tools/WindowsBleBridge/out/x64/Release/WssBleWindows.dll`; shim output is under
`tests/BLE/Windows/NativeTestShim/bin/x64/Release`.

An installed SDK >=10.0.19041.0 can be selected through `-WindowsSdkVersion` or
`/p:WindowsTargetPlatformVersion` in all native commands. Windows 10 build 19041 is
the intended minimum runtime and needs separate runtime validation. Only Windows
x64 processes are supported; an AnyCPU wrapper does not change that requirement.

## ABI 1.1 and ownership

`native/include/wss_ble_windows.h` is authoritative. Exports use `extern "C"`,
`__cdecl`, undecorated names, fixed-width fields, pack 8, and opaque context pointers.
The wrapper requires major 1, minor >=1, scan/NUS capabilities, and current layouts.
On x64 ABI info is 32 bytes, scan event 56, advertisement 48, NUS options/GATT event 88.

| Export | Contract |
| --- | --- |
| `wss_ble_get_abi_info` | Version, capabilities, pointer/layout sizes; caller sets info size |
| `wss_ble_create` | Owns callback/user state and creates a worker/context |
| `wss_ble_initialize` | Queued MTA initialization |
| `wss_ble_query_adapter` | Adapter/LE/radio state query |
| `wss_ble_scan_start` | Starts an active scan with an increasing nonzero session |
| `wss_ble_scan_stop` | Revokes/stops watcher; successful completion is a session callback fence |
| `wss_ble_cancel` | Cancellation request; an already committed completion can win |
| `wss_ble_destroy` | Synchronous close/cancel/revoke/join fence |
| `wss_ble_nus_connect` | Uncached selected-device/service/RX/TX discovery and CCCD enable |
| `wss_ble_nus_write` | Copies submission bytes and writes with response within current budget |
| `wss_ble_nus_disconnect` | Invalidates generation, releases resources, establishes local notification fence |

Accepted `Success` means queued, not completed. Each accepted operation has one
completion unless destruction suppresses it; rejected submissions have none.
IDs must be unique while outstanding; operation slots are capped at 128.
Callback data and pointed-to buffers are borrowed only until callback return.
Copy immediately; never free borrowed native pointers. UUIDs use canonical network
order, not Windows GUID memory order. Root callback/user state until destroy returns;
never destroy from a reverse callback. SafeHandle guarantees exactly-once release.

Native owns an MTA worker; callbacks do not run with internal locks held. Shutdown
revokes handlers, cancels/closes asynchronous work, disables dispatch, and joins the
worker without waiting for Windows to honor cancellation. Managed callbacks contain
exceptions, copy validated buffers, and complete tasks asynchronously; they do not
invoke application events. Cancellation cannot prove bytes were not already delivered.

NUS RX requires Write and TX requires Notify. One connection is owned per context;
scanning is independent. Connection generations fence stale notifications/cleanup.
Native deadlines are positive finite milliseconds <=INT32_MAX. Write cancellation/
timeout tears down the generation without replay; retired nonterminal writes can
make reconnect Busy. Disconnect CCCD cleanup is best-effort, with timeout/diagnostics
retained while local resource fencing is established.

Notification queues are bounded at 256 values/1 MiB. Overflow faults rather than
silently replacing bytes. There is no automatic reconnect or payload replay.
Adapter presence alone does not establish radio availability, permission, or reception.

## Managed usage

Applications normally use the BLE facade and
`Wss.Transports.Backends.Windows.WindowsBleBackend.Register()` only if preloading.
For raw bridge diagnostics, `Wss.Ble.Windows.Native.WindowsBleClient` exposes
initialization/adapter query, scan start/stop, discovery snapshots, NUS connect/send/
disconnect, notification drains, and explicit disposal. Initialize before connecting.
Use nonempty configured service/write/notify UUIDs; defaults are the standard NUS UUIDs.
Inputs are snapshotted before asynchronous waiting.

The wrapper's `MaxWriteLength` is current ATT MTU minus three, not peripheral attribute
capacity. Raw wrapper writes above it are rejected; the Windows transport adapter
fragments logical sends and serializes them. Inspect `LastScanError`,
`DroppedAdvertisements`, and `LastConnectionError`. Drain raw queues on the chosen
application thread and dispose before host assembly reload.

## Automated tests and manual diagnostics

See [tests/README.md](../../tests/README.md). Managed boundary/shim tests validate
layouts, copying, callback lifetime, GATT lifecycle, cancellation, overflow, and
adapter fragmentation. Production-native fixtures load the actual DLL by explicit
build metadata path and reject test exports; they cover initialization/query/destruction
and disconnect deadline handling without connecting a peripheral. Missing required
DLLs fail these fixtures. Synthetic success is not real GATT hardware verification.

```powershell
dotnet test tests/BLE/Windows/WSS.BLE.Windows.Tests/WSS.BLE.Windows.Tests.csproj -c Release --no-build --no-restore
dotnet run --project tests/BLE/Windows/ScanConsumer/ScanConsumer.csproj -c Release --no-build -- --lifecycle-only --iterations 100
dotnet run --project tests/BLE/Windows/ScanConsumer/ScanConsumer.csproj -c Release --no-build -- --scan-seconds 10 --iterations 5
```

ScanConsumer is opt-in. Scanning needs a usable LE adapter; reception needs a nearby
advertiser. Empty/skipped scans do not prove reception. NUS diagnostics must use an
explicitly selected **non-actuating** development peripheral, never an arbitrary
stimulator. Receive-only example:

```powershell
dotnet run --project tests/BLE/Windows/ScanConsumer/ScanConsumer.csproj -c Release --no-build -- --nus-device AABBCCDDEEFF --address-type Public --nus-seconds 10
```

`--write-hex` sends explicitly supplied bytes and requires `NON-ACTUATING`
confirmation. `--expect-notification-hex` requires an exact notification match.
Do not reuse example bytes on unknown firmware or send WSS stimulation commands.
Hardware unavailability leaves physical scanning/GATT validation outstanding.

## Unity deployment

Use the full universal input, not a bridge-only staging package. Configure production
native code for Windows Editor/Standalone x86_64; disable Any Platform/unsupported
targets. Preload/register the adapter or deploy the automatic loader layout and ensure
the native resolver finds `WssBleWindows.dll` beside its wrapper. Never import shim,
tests, or managed WinRT projections. Rebuild references and validate Editor/player
loading separately. See [consumer handoff](../ArtifactBuild/CSHARP-HANDOFF.md).
Mono validation does not establish IL2CPP/AOT support: reverse callback preservation,
stripping, and standalone native loading require a separate host-specific check.
