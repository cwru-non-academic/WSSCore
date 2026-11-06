# WSSCore

WSSCore provides the shared WSS protocol and transport implementations for C# and
Unity applications. API and application documentation is available on
[GitHub Pages](https://cwru-non-academic.github.io/WSS_Documentation/).

Start with [Build](#build), [One universal distribution](#one-universal-distribution),
and [C# and Unity consumption](#c-and-unity-consumption). The complete build,
platform, test and automation reference is retained below in expandable sections.

## Architecture

All seven WSS managed libraries target **.NET Standard 2.0**:

| Project | Responsibility / dependencies |
| --- | --- |
| `WSS_Core_Interface.csproj` | Protocol, command handling, configuration, and shared transport interfaces |
| `WSS.Transport.Serial` | Serial transport; references Core and System.IO.Ports 8.0.0 |
| `WSS.Transport.BLE` | Portable BLE facade; references Core and Serial |
| `WSS.Transport.BLE.Windows` | Windows adapter implementing the common BLE contract; references facade, Core, and native wrapper |
| `tools/WindowsBleBridge/managed` | `WSS.Transport.BLE.Windows.Native.dll`: P/Invoke, callbacks, marshaling, resource ownership |
| `WSS.Transport.BLE.Linux` | BlueZ backend; references facade, Core, Linux.Bluetooth, and Tmds.DBus |
| `WSS.Transport.BLE.MacOS` | CoreBluetooth backend with the universal arm64/x86_64 native bridge |

The Windows wrapper calls the separate production C++/WinRT **`WssBleWindows.dll`**.
Windows BLE requires a Windows x64 process. Linux BLE requires BlueZ and system
D-Bus. macOS BLE requires macOS 11 or later, the managed adapter and universal
`backends/macos/libwss_macos_ble.dylib`, and host-owned Bluetooth permissions.
There is no Android backend. Hosted native/ABI/loading tests do not prove a
physical Bluetooth connection or TCC authorization.

## Build

Install .NET 8 (test runtime) and .NET 9 (ArtifactBuild), with NuGet access.
Managed projects can be built without a native compiler:

```powershell
dotnet restore WSS_Core_Interface.sln --locked-mode
dotnet build WSS_Core_Interface.sln -c Release --no-restore
```

The root solution builds the six managed libraries and portable test projects.
Windows native builds additionally require VS 2022/MSVC v143 and Windows SDK
10.0.22621.0. The pinned C++/WinRT package is restored by Visual Studio MSBuild.
Build production native code, the isolated test shim, and Windows test/diagnostic
projects with:

```powershell
./tools/WindowsBleBridge/build.ps1 -Configuration Release -Architecture x64
```

See [Windows toolchain and ABI instructions](#windows-ble-reference)
for direct MSBuild commands and alternate SDK selection. See
[test reference](#tests-and-ci-reference) for automated test commands and safety boundaries.

## One universal distribution

First build `tools/MacBleBridge/build.sh` on a Mac and copy its universal dylib to
`tools/MacBleBridge/out/libwss_macos_ble.dylib` on the Windows build host. CI passes
that native input between jobs in the same run. Then build the complete release
from the repository root on Windows:

```powershell
dotnet run --project tools/ArtifactBuild/ArtifactBuild.csproj -- build-all
```

This produces `artifacts/universal/`, `artifacts/WSS-Universal-local.zip`, and
`artifacts/SHA256SUMS.txt`. The payload destination must be new. Use
`--destination <new-payload-directory> --tag <TAG>` to select a different output.

Every supported consumer uses the same release input:

```text
WSS-Universal-<TAG>.zip
SHA256SUMS.txt
```

The ZIP contains one `WSS-Universal/` directory: one shared Core/Serial/BLE set,
all three backend dependency trees, production Windows native code, and the Ports
runtime assets. Verify the checksum before extraction and preserve the complete
payload for redistribution. Do not mix independently sourced DLLs or download
separate platform packages. The [artifact guide](#universal-artifact-reference)
lists the exact inventory, deployment rules, and validation commands.

## C# and Unity consumption

Reference `WSS_Core_Interface.dll`, `WSS.Transport.Serial.dll`, and
`WSS.Transport.BLE.dll` as needed from the same extracted artifact. Serial types
are defined directly in the Serial assembly; the BLE assembly does not forward them.
Rebuild internal libraries, applications, and Unity assembly references against the
final assembly set. Constructors/options and shared transport contracts are preserved.

The facade automatically loads the current platform backend from
`AppContext.BaseDirectory/backends/windows`, `backends/linux`, or `backends/macos`. Deploy the full
backend/runtime trees without duplicate shared WSS DLLs in backend directories.
Hosts that preload backend assemblies can register explicitly:

```csharp
Wss.Transports.Backends.Windows.WindowsBleBackend.Register();
// On Linux:
Wss.Transports.Backends.Linux.LinuxBleBackend.Register();
// On macOS:
Wss.Transports.Backends.MacOS.MacBleBackend.Register();
```

For .NET applications use `System.IO.Ports` NuGet **8.0.0** to select the host
runtime implementation; do not overwrite it with the portable root net462 asset.
Keep the original complete artifact for redistribution. Unity plugin deployment
and native resolution are host-specific; configure the production native DLL for
Windows x86_64, preload/register if needed, and validate Editor/player loading.
See [consumer integration requirements](#consumer-integration-reference).

### Linux serial setup

- On Debian/Ubuntu install `libudev1`; Unity build hosts may also need `libudev-dev`.
- Grant access to the intended `/dev/tty*` device, commonly by adding the user to
  `dialout` with `sudo usermod -a -G dialout $USER`, then logging out and back in.
- Check device permissions with `ls -l /dev/ttyUSB0`. Missing access can cause
  `SerialPort.Open()` to throw `UnauthorizedAccessException`.
- Linux Unity/WSL hosts have the same OS dependency and device-access requirements.

## CI and releases

`.github/workflows/artifacts.yml` runs for PRs targeting `dev`, `main`, or
`net2_BLE`, manual dispatch, and ordinary dev pushes. `Core conformance` runs the
functional suite once on Linux. `Build and validate artifacts` builds its own inputs
on `windows-2022` and validates one universal ZIP without functional suites.
`Core consumer` and `Serial consumer` jobs run on Windows/Linux/macOS;
`BLE consumer` jobs combine source functional suites and packaged consumers on
Linux, Windows and both macOS architectures (`windows-2022` for isolated
shim/production-native testing).
The logical checks `BLE Consumer for Windows`, `BLE Consumer for Linux`, and
`BLE Consumer for Mac` require the corresponding legacy jobs to succeed in the
current run attempt. The Mac check reports arm64 and x86_64 separately and fails
on missing, duplicate, skipped, cancelled or unsuccessful results. Both Mac legs
assert their runner and native/.NET process architectures. Existing job names and
per-architecture logs/results remain available for pinned downstream validators;
the producer handoff and acceptance verifier additionally require all three
logical checks. These checks do not validate physical peripherals or host permissions.
All consumers use the same verified, extracted ZIP. Serial smoke checks do not
exercise port I/O; classified Linux controller skips do not suppress dependencies.
See [CI Jobs and Test Responsibilities](#tests-and-ci-reference)
for the authoritative catalog and placement policy. Named logs, job summaries and
downloadable NUnit TRX artifacts expose actual execution and failures.

The current dev workflow removes the legacy `v*` publisher. The separate receipt
coordinator is gated by `WSS_DEV_ACCEPTANCE_ENABLED`. Optional publication additionally
requires `WSS_DEV_RELEASE_ENABLED` and the reviewed historical-tag policy. It verifies
all nine required targets before publishing the original ZIP bytes, without rebuilding
or repackaging. Main/stable publication is disabled
in this dev policy. See the expandable release reference below for its current
contract and limitations. Hardware diagnostics remain opt-in.

## Universal artifact reference

<details>
<summary>Exact payload, validation and deployment</summary>

Prepare the universal macOS native input as described above, then run from the
repository root on Windows with .NET 9, VS 2022/MSVC v143,
Windows SDK 10.0.22621.0, and NuGet access:

```powershell
dotnet run --project tools/ArtifactBuild/ArtifactBuild.csproj -- build-all --destination "<new-directory>/WSS-Universal" --tag local
```

`build-all` builds the Windows adapter's managed graph, the Linux backend without
rebuilding shared projects, and the production native Release/x64 DLL. Its child
PowerShell process uses a process-only execution-policy bypass, never a machine/user
policy change. Direct MSBuild instructions are in the [bridge guide](#windows-ble-reference).
`--no-build` reuses these source outputs after successful builds/tests; it does not
load a prior distribution. The destination must be new.

### Exact payload

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
  backends/macos/
    WSS.Transport.BLE.MacOS.dll
    libwss_macos_ble.dylib
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

The 27-file contract is defined by `tools/ArtifactBuild/manifests/universal.json`. All seven WSS managed
assemblies are Standard 2.0. Windows native code is production AMD64 ABI 1.1;
the macOS bridge is universal arm64/x86_64 ABI 1.0; Linux serial native code is ELF64 x86-64. BlueZ/system D-Bus and OS device permissions
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

### ZIP, checksums, and validation

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
inspection utilities. See [test commands](#tests-and-ci-reference) for artifact consumers.

### Deployment and selection

Preserve the complete original payload for redistribution, including all three backends
and runtime trees regardless of build-host OS. Reference shared Core/Serial/BLE DLLs
copy-local and avoid duplicate copies in backend directories. Runnable .NET outputs
may omit the root alternate Ports implementation while retaining the complete original
artifact separately. Do not treat that runnable subset as the universal distribution.

Automatic selection probes `AppContext.BaseDirectory/backends/<platform>` using
ordinary shared assembly loading. Windows loads `WSS.Transport.BLE.Windows.dll`;
Linux loads `WSS.Transport.BLE.Linux.dll`; macOS loads `WSS.Transport.BLE.MacOS.dll`. Explicit preloading/registration is supported:
`Wss.Transports.Backends.Windows.WindowsBleBackend.Register()` or
`Wss.Transports.Backends.Linux.LinuxBleBackend.Register()`.

Keep the production native DLL adjacent to its managed wrapper and ensure the host's
native resolver can find it. Unity deployment requires plugin importer configuration
and runtime validation, not a separate release download. Rebuild internal consumers;
no pre-release binary compatibility aliases are provided.

### CI/release gate

`Build and validate artifacts` on `windows-2022` builds its own managed/Linux/backend
and production-native inputs, validates the payload and extracted archive, runs
packaging rejection checks, and uploads one ZIP/checksum; it runs no functional suites.
Independent `Core conformance` runs once on Linux. `Core consumer` and `Serial consumer`
jobs run on Windows/Linux/macOS; `BLE consumer` jobs combine source functional/native
tests and packaged consumers on Linux/Windows/macOS, keeping those project boundaries separate.
Every consumer downloads that same ZIP. Platform-wide package validation runs in the
Core consumer jobs; Linux case-sensitive rejection runs there once. BLE's classified
controller skip suppresses only the native probe, never managed dependency checks.
Serial smoke checks do not establish hardware communication. See the authoritative
[CI/test catalog](#tests-and-ci-reference) for commands,
coverage, placement rules and job-summary/TRX visibility.
Optional publication uses `dev-release.yml` after all nine required acceptance
receipts pass. It requires the explicit release switch and reviewed historical-tag
guard, and reuses the accepted ZIP/checksum without rebuilding, restaging, or rezipping.
PRs do not publish.

</details>

## Consumer integration reference

<details>
<summary>C# and Unity deployment requirements</summary>

Use **one** `WSS-Universal-<TAG>.zip` and `SHA256SUMS.txt`. Verify the checksum,
extract once, and use the nested `WSS-Universal/` directory as the artifact root.
The complete inventory and deployment contract are in [README.md](#universal-artifact-reference).

### C# implementation library and applications

1. Use one artifact root for project references and redistribution. If multiple
   existing artifact-directory properties remain, give them the same root and
   reject differing inputs; no separate platform downloads are required.
2. Reference `WSS_Core_Interface.dll`, `WSS.Transport.Serial.dll`, and
   `WSS.Transport.BLE.dll` from that root, copy-local. Serial types belong directly
   to `WSS.Transport.Serial`; do not resolve them through BLE type forwarding.
3. Replace Windows backend references with `WSS.Transport.BLE.Windows.dll`.
   For explicit registration use
   `Wss.Transports.Backends.Windows.WindowsBleBackend.Register()`.
   Automatic loading needs no registration call when the expected layout is deployed.
4. Keep `WSS.Transport.BLE.Windows.Native.dll` and production `WssBleWindows.dll`
   beside the Windows adapter. Preserve the Linux backend and all its dependencies,
   the macOS DLL/dylib pair, and all platform runtime trees in library/installer redistributable content.
5. Remove obsolete Windows adapters, managed WinRT/SDK projections, Drawing,
   Windows.Extensions, and SystemEvents dependencies from the WSS payload. Do not
   remove the shared dependencies listed in the universal manifest.
6. Use System.IO.Ports NuGet **8.0.0** for runnable .NET runtime selection. Keep
   `runtimes/{win|unix}/lib/net8.0/System.IO.Ports.dll` and the Linux-x64 native
   asset in redistributed content. Do not overwrite the host-selected Ports DLL
   with the root net462 alternate; preserve the complete original artifact separately.
7. Update copy-to-output/publish rules, required-file lists, loading tests, and
   workflows to this one input. Assert ordinary shared loading and one identity
   each for Core/Serial/BLE. Never load the native DLL as a managed assembly.
8. Rebuild the implementation library, applications, tests, and dependent binaries.
   Existing facade constructors/options and stimulation-layer logic need no rewrite.

### Unity

1. Replace WSS managed plugins and assembly-definition references with the final
   Core/Serial/BLE set and `WSS.Transport.BLE.Windows.dll`; retain the native wrapper
   and production native DLL. Remove obsolete adapter/WinRT assemblies and duplicate
   shared DLLs rather than overlaying new files on an old plugin set.
2. Update explicit Windows registration to
   `Wss.Transports.Backends.Windows.WindowsBleBackend.Register()` after preloading
   the adapter, or deploy the automatic loader's backend layout under the host base directory.
3. Reference Serial directly wherever its types are used. Rebuild scripts and
   dependent assemblies; removed aliases are not a binary compatibility contract.
4. Configure `WssBleWindows.dll` for Windows Editor/Standalone x86_64 only, not
   Any Platform. Ensure native resolution and the wrapper's required dependencies
   are available. Use a .NET Standard-compatible Unity API level.
5. Preserve the complete universal input for redistribution even when Unity importer
   settings select platform-specific plugins. No platform-specific ZIP is needed.

Validate Editor and player assembly/native loading, explicit registration where used,
and communication with an approved non-actuating test device. Prior simulator evidence
does not establish renamed-assembly compatibility. Mono success does not establish
IL2CPP/AOT support; callback preservation and stripping need separate validation.

Dispose owned BLE clients/transports before assembly reload and application shutdown;
an unloaded domain is not a substitute for callback fencing.

</details>

## Windows BLE reference

<details>
<summary>Toolchain, ABI, ownership and manual diagnostics</summary>

The production Windows stack has three separate layers:

- `WSS.Transport.BLE.Windows.dll`: implements the shared facade contract, discovery
  selection, notification delivery, and logical-send fragmentation.
- `tools/WindowsBleBridge/managed/WSS.Transport.BLE.Windows.Native.csproj`: Standard 2.0 P/Invoke wrapper,
  callbacks, marshaling, and resource ownership.
- `tools/WindowsBleBridge/native/WssBleWindows.vcxproj`: C++/WinRT `WssBleWindows.dll`, using Windows Bluetooth APIs.

The wrapper/native bridge transports raw bytes, not WSS protocol packets. The
production bridge is included in the [universal artifact](#universal-artifact-reference).
The synthetic test shim is never shipped.

### Toolchain

Install Visual Studio 2022 Community or Build Tools with Desktop development with C++:

| Component | Installer identifier |
| --- | --- |
| Community C++ workload | `Microsoft.VisualStudio.Workload.NativeDesktop` |
| Build Tools C++ workload | `Microsoft.VisualStudio.Workload.VCTools` |
| MSVC v143 x64/x86 tools | `Microsoft.VisualStudio.Component.VC.Tools.x86.x64` |
| Windows SDK 10.0.22621.0 | `Microsoft.VisualStudio.Component.Windows11SDK.22621` |

Community can import [windows-build.vsconfig](tools/WindowsBleBridge/windows-build.vsconfig); Build Tools
uses a different workload identifier. VS Code extensions do not install the compiler,
MSBuild, or SDK. Install .NET 8 for tests/diagnostics and .NET 9 for ArtifactBuild.
C++/WinRT **2.0.240405.15** is pinned in `tools/WindowsBleBridge/native/packages.config`; restore needs NuGet.
C++17 and the static MSVC runtime are selected by the native projects. No C++/CLI,
managed WinRT projection, ARM64, x86, or UWP workload is required.

### Build from the repository root

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

### ABI 1.1 and ownership

`tools/WindowsBleBridge/native/include/wss_ble_windows.h` is authoritative. Exports use `extern "C"`,
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

### Managed usage

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

### Automated tests and manual diagnostics

See [tests/README.md](#tests-and-ci-reference). Managed boundary/shim tests validate
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

### Unity deployment

Use the full universal input, not a bridge-only staging package. Configure production
native code for Windows Editor/Standalone x86_64; disable Any Platform/unsupported
targets. Preload/register the adapter or deploy the automatic loader layout and ensure
the native resolver finds `WssBleWindows.dll` beside its wrapper. Never import shim,
tests, or managed WinRT projections. Rebuild references and validate Editor/player
loading separately. See [consumer handoff](#consumer-integration-reference).
Mono validation does not establish IL2CPP/AOT support: reverse callback preservation,
stripping, and standalone native loading require a separate host-specific check.

</details>

## macOS BLE reference

<details>
<summary>Build, host permissions, lifecycle and coverage</summary>

The production Objective-C bridge uses CBCentralManager/CBPeripheral to scan for
the configured service, connect by CoreBluetooth UUID, discover the required
write-with-response RX and notify/indicate TX characteristics, enable notifications,
and acknowledge writes. It is not a mock backend. The `IBleNusBackend` adapter is
`WSS.Transport.BLE.MacOS`, targeting .NET Standard 2.0 like Core and the other backends.

### Build and distribution

Run `bash tools/MacBleBridge/build.sh` on a Mac with Xcode command-line tools,
Python 3 and the macOS SDK. The minimum native deployment target is macOS 11.0.
Clang builds x86_64 and arm64; lipo combines them and codesign applies an ad-hoc
signature. No paid Apple account, signing service or runner is needed for CI.
The resulting file is `tools/MacBleBridge/out/libwss_macos_ble.dylib`; generated binaries are never
committed. `tools/MacBleBridge/verify_native.py` checks both architectures and their exact public C
exports, loads the host slice, checks ABI 1.0, and creates/destroys a context without
initializing Bluetooth. The header documents borrowed data and callback fencing.

`Build macOS universal BLE bridge` uploads only that dylib. `Build and validate
artifacts` downloads the **same workflow run's** native artifact, builds the managed
adapter, checks exact closure/identities and Mach-O architecture headers, then
creates the single universal ZIP/checksum. All consumers download that ZIP; no
second archive is rebuilt for macOS. Public standard `macos-15` (arm64) and
`macos-15-intel` runners run mandatory packaged ABI/load tests on .NET 8 and 9.
Publication and routine propagation wait for both `BLE consumer (macOS arm64)`
and `BLE consumer (macOS x86_64)`. Existing Core conformance remains authoritative.

### Runtime and permissions

Deploy the complete universal tree, including:

```
backends/macos/WSS.Transport.BLE.MacOS.dll
backends/macos/libwss_macos_ble.dylib
```

The host must use a 64-bit runtime with a matching x86_64 or arm64 slice. .NET 8/9
packaged loading is covered by CI. Unity Mono, MATLAB's CLR hosting and other
runtime loaders need separate receiver validation before claiming compatibility.
Automatic facade selection uses `macos`; explicit hosts may call
`Wss.Transports.Backends.MacOS.MacBleBackend.Register()` before constructing a BLE
transport. Construction/registration alone does not open Bluetooth. A native probe
or connection initializes CoreBluetooth and can request user authorization.

The **host application** owns TCC permission, not the dylib. Supply an appropriate
`NSBluetoothAlwaysUsageDescription` in the application's Info.plist; sandboxed
applications also need the Bluetooth entitlement
`com.apple.security.device.bluetooth`. A CLI launched by Terminal, Python, MATLAB
or an editor may be attributed to that host by macOS; authorize the actual host in
System Settings → Privacy & Security → Bluetooth. Signed/hardened applications
must sign embedded code consistently with their own distribution requirements.
CI's ad-hoc signature does not establish notarization or a production signing policy.
Denied permission, an unavailable adapter or powered-off Bluetooth is an error,
not successful runtime evidence. DeviceId is the OS's peripheral UUID, not a MAC
address; scan by configured name to obtain an identifier on that Mac.

References: [Apple Core Bluetooth](https://developer.apple.com/documentation/corebluetooth),
[CBCentralManager](https://developer.apple.com/documentation/corebluetooth/cbcentralmanager),
[standard hosted runner labels](https://docs.github.com/en/actions/reference/runners/github-hosted-runners).

### Lifecycle and evidence limits

All CoreBluetooth state and delegate callbacks run on a private serial dispatch
queue. Operation IDs and connection/scan epochs are monotonic. Native inputs are
copied synchronously; managed callbacks copy notification data before returning.
User handlers run on a managed notification pump outside native and lifecycle
locks. Disconnect/destruction fence callbacks and stale queued delivery; a handler
may synchronously disconnect itself. One previously claimed user callback may
finish after disconnect. Dispose before unloading the host/domain.

Writes snapshot caller data, serialize complete messages and chunk to the current
write-with-response budget. A submitted failure/cancellation ends the connection;
payloads are never replayed. Cancellation and the 10-second operation deadline
abandon the manager epoch so late GATT callbacks cannot complete a new operation.
Discovery is service-filtered; auto-selection also validates both characteristics
before the facade ranks compatible candidates by RSSI. Discovery results and
notification queues are bounded at 256. Notification overflow ends the session.

Ordinary CI proves native compilation, both binary slices, exact exports, ABI
loading, managed construction/registration, and deterministic simulated adapter
contracts (chunk order/snapshots, cancellation without replay, stale notification
rejection, reentrant disconnect and disposal). It does **not** initialize a real
CBCentralManager, prove TCC behavior, discover/connect a physical peripheral, or
write stimulation data. The fake is confined to managed tests and is never packaged.
The CoreBluetooth delegate/GATT interaction still requires real Mac validation.

Before claiming physical BLE support validated, test on Intel and Apple Silicon
with an authorized host and a safe non-stimulating NUS test peripheral: scan/name
and UUID selection; missing/wrong characteristic rejection; notifications and write
acknowledgments; MTU budget changes; radio off/permission denial; cancellation,
link loss and reconnect; host shutdown/domain reload. Do not use stimulation
hardware as an unattended CI target.

</details>

## Tests and CI reference

<details>
<summary>Suite commands, job catalog and test placement</summary>

Run commands from the repository root. Automated functional suites use NUnit on
.NET 8; Windows interop tests require Windows x64 and the native toolchain documented
in [the bridge guide](#windows-ble-reference). Artifact tooling uses .NET 9.

| Location | Purpose |
| --- | --- |
| `tests/Core/WSS.Core.Tests` | Protocol framing, command/configuration behavior, initialization, runtime edits, stimulation conformance through emulated transports |
| `tests/BLE/WSS.BLE.Tests` | Portable facade, backend registration/selection, shared dependency identities, Linux portability and cancellation helpers |
| `tests/BLE/Windows/WSS.BLE.Windows.Tests` | Managed/native layouts and callbacks, Windows adapter, GATT lifecycle, notification handling, fragmentation, cancellation and cleanup |
| `tests/BLE/Windows/NativeTestShim` | Synthetic C++ ABI implementation with deterministic worker/barrier/failure controls; test dependency only |
| `tests/BLE/Windows/ScanConsumer` | Opt-in production-native lifecycle/scanning/NUS diagnostic, not a unit test suite |
| `tests/Packaging/Consumers` | Independent Core/Serial/BLE applications referencing extracted artifact DLLs, not source projects |
| `tests/Packaging/verify_universal.py` | Exact package/dependency/native contract rejection cases; no hardware |

### Automated functional suites

```powershell
dotnet restore WSS_Core_Interface.sln --locked-mode
dotnet build WSS_Core_Interface.sln -c Release --no-restore
dotnet test tests/Core/WSS.Core.Tests/WSS.Core.Tests.csproj -c Release --no-build --no-restore
dotnet test tests/BLE/WSS.BLE.Tests/WSS.BLE.Tests.csproj -c Release --no-build --no-restore
```

For Windows interop:

```powershell
dotnet restore tests/BLE/Windows/WSS.BLE.Windows.Tests/WSS.BLE.Windows.Tests.csproj --locked-mode
./tools/WindowsBleBridge/build.ps1 -Configuration Release -Architecture x64
dotnet test tests/BLE/Windows/WSS.BLE.Windows.Tests/WSS.BLE.Windows.Tests.csproj -c Release --no-build --no-restore
```

The Windows test output receives **only the shim**. Managed-boundary fixtures use
synthetic buffers; shim fixtures exercise real P/Invoke and native threads but not
Windows Bluetooth APIs. `ProductionNativeLifecycleTests` and
`NativeDisconnectDeadlineTests` explicitly load the production DLL, validate ABI,
and reject shim exports. The path is embedded by the build for the selected configuration;
`WSS_BLE_PRODUCTION_DLL` can explicitly override it. Missing required native files
are failures, not evidence of success. Keep production and shim files isolated.

Deterministic concurrency tests retain bounded waits, controllable operations,
generation fencing, notification ordering, and resource cleanup. Core protocol-device
profiles are current behavior coverage, independent of native software ABI versions.

### Artifact boundary

Build and extract the [universal ZIP](#universal-artifact-reference), then pass
the nested extracted directory as `WSS_ARTIFACT_DIR`:

```powershell
dotnet run --project tests/Packaging/Consumers/CoreConsumer/CoreConsumer.csproj -p:WSS_ARTIFACT_DIR="<extracted-root>/WSS-Universal"
dotnet run --project tests/Packaging/Consumers/SerialConsumer/SerialConsumer.csproj -p:WSS_ARTIFACT_DIR="<extracted-root>/WSS-Universal"
dotnet run --project tests/Packaging/Consumers/BleConsumer/BleConsumer.csproj -c Release -p:WSS_ARTIFACT_DIR="<extracted-root>/WSS-Universal"
dotnet run --project tests/Packaging/Consumers/BleConsumer/BleConsumer.csproj -c Release -p:ConsumerTargetFramework=net8.0 -p:WSS_ARTIFACT_DIR="<extracted-root>/WSS-Universal"
python tests/Packaging/verify_universal.py "<extracted-root>/WSS-Universal"
```

BLE options follow `--`: `--register` checks explicit registration;
`--linux-load-only` checks Linux managed activation even on Windows, **not Linux runtime**.
`WSS_BLE_SKIP_NATIVE_PROBE=1` skips only the Linux controller probe when explicitly
classified unavailable. Identity/dependency loading must still pass.

Consumers do not discover/connect peripherals, open Serial hardware, or write physical
stimulation. Core uses TestModeTransport. These applications protect the release
boundary; they do not replace functional suites. Package checks reject bad file sets,
identities, native architectures, exports, and Linux filesystem case collisions.

### Manual hardware diagnostics

ScanConsumer copies **production** native code. Its lifecycle-only mode needs no
peripheral; scanning requires a usable adapter and nearby advertiser. Explicit NUS
mode is restricted to a known non-actuating test peripheral. Writes require explicit
bytes and confirmation; no safe write target is inferred from an advertisement.
See [diagnostic commands and boundaries](#windows-ble-reference).
Skipped/empty scans do not establish reception or GATT success. Never run stimulation
against physical hardware as part of these automated checks.

### CI Jobs and Test Responsibilities

This is the living catalog for [Build universal WSS release](.github/workflows/artifacts.yml).
The workflow is the authority for exact command flags and execution order; the
sequences below describe the same suites and boundaries. All commands run from
the repository root. CI installs .NET 8/9, uses Release for functional/native/BLE
builds, and uses locked restores for the functional suites and packaging inputs.

#### Test categories

- **Functional tests:** internal behavior through source/project references,
  emulated transports, mocks, and synthetic native test shims. These do not use the ZIP.
- **Artifact consumer tests:** independent applications compiled against DLLs in
  the extracted universal distribution, with no WSS source-project references.
  These protect the public assembly/dependency boundary, not full hardware operation.
- **Packaging validation:** inventory, metadata, dependency closure, identities,
  native exports/architectures, consistency, checksum, extraction and rejection cases.
- **Manual hardware diagnostics:** existing component diagnostics requiring real
  devices for reception/communication. Explicitly opt-in, never ordinary automated
  CI unless separately authorized; see the manual diagnostics section above.

#### Job catalog

Each row identifies an actual displayed job. Shared sequences and their pass/fail
boundaries below apply to every row referencing them, including its future placement.

| Exact display name | OS / runner | Purpose, projects/tools, and categories | Sequence / coverage boundary |
| --- | --- | --- | --- |
| `Core conformance` | Linux / `ubuntu-latest` | `tests/Core/WSS.Core.Tests`: protocol, configuration, initialization, runtime edits and emulated transport conformance; functional | Core functional sequence |
| `Build and validate artifacts` | Windows / `windows-2022` | ArtifactBuild, production-only native build, extraction script, `tests/Packaging/verify_universal.py`; packaging | Packaging sequence |
| `Core consumer (Windows)` | Windows / `windows-latest` | `tests/Packaging/Consumers/CoreConsumer` plus ArtifactBuild; consumer + platform package validation | Core consumer sequence |
| `Core consumer (Linux)` | Linux / `ubuntu-latest` | CoreConsumer, ArtifactBuild, `tests/Packaging/verify_universal.py`; consumer + Linux filesystem package rejection | Core consumer sequence + Linux rejection |
| `Core consumer (macOS)` | macOS / `macos-latest` | CoreConsumer plus ArtifactBuild; consumer + platform package validation | Core consumer sequence |
| `Serial consumer (Windows)` | Windows / `windows-latest` | `tests/Packaging/Consumers/SerialConsumer`; artifact consumer | Serial consumer sequence |
| `Serial consumer (Linux)` | Linux / `ubuntu-latest` | SerialConsumer; artifact consumer | Serial consumer sequence |
| `Serial consumer (macOS)` | macOS / `macos-latest` | SerialConsumer; artifact consumer | Serial consumer sequence |
| `BLE consumer (Windows)` | Windows / `windows-2022` | `tests/BLE/WSS.BLE.Tests`, `tests/BLE/Windows/WSS.BLE.Windows.Tests`, isolated NativeTestShim, production native DLL, `tests/Packaging/Consumers/BleConsumer`; functional + native + consumer | Windows BLE sequence |
| `BLE consumer (Linux)` | Linux / `ubuntu-latest` | `tests/BLE/WSS.BLE.Tests` (including Linux backend compatibility), BleConsumer, BlueZ/system D-Bus; functional + consumer | Linux BLE sequence |
| `BLE consumer (macOS arm64)` / `BLE consumer (macOS x86_64)` | macOS / `macos-15`, `macos-15-intel` | Portable BLE/simulated lifecycle tests plus packaged native ABI/load and automatic/explicit backend loading on .NET 8/9 | No CoreBluetooth initialization, radio or peripheral I/O |
| `BLE Consumer for Windows` / `BLE Consumer for Linux` / `BLE Consumer for Mac` | Linux / `ubuntu-latest` (summary only) | Exact current-attempt worker results; Mac requires both native architecture jobs | No test re-execution; missing, duplicate, skipped or unsuccessful workers fail |
| `Build macOS universal BLE bridge` | macOS / `macos-15` | `tools/MacBleBridge/build.sh`, universal native input for this run | Build/exports/ABI validation, no Bluetooth initialization |
| `Dev release acceptance contracts` | Linux / `ubuntu-latest` | Node verifier contract tests | Receipt/policy rejection cases; no receiver dispatch or release |

##### Core functional sequence

`dotnet restore tests/Core/WSS.Core.Tests/WSS.Core.Tests.csproj --locked-mode`,
then `dotnet test` on that project with `-c Release --no-restore` and TRX output.
The full suite runs **once, on Linux**, without a case filter.

Passing establishes source Core conformance through emulated transports. It does
not establish platform DLL consumption, physical stimulation, or device firmware
compatibility. No hardware is required. Future related tests belong in the existing
`tests/Core/WSS.Core.Tests` project and this job.

##### Shared consumer preparation

Every Core, Serial and BLE consumer job downloads **the same**
`universal-release-assets` artifact, then runs:

```powershell
./tools/ArtifactBuild/extract-universal.ps1 -ReleaseAssets "$env:RUNNER_TEMP/release-input" -Destination "$env:RUNNER_TEMP/universal"
```

This checks the single ZIP/checksum asset contract and SHA256 before extraction.
In the following commands, `<package>` means
`<runner-temp>/universal/WSS-Universal`, never a source build output.

##### Core consumer sequence (all three platforms)

```powershell
dotnet run --project tools/ArtifactBuild/ArtifactBuild.csproj -- validate --manifest tools/ArtifactBuild/manifests/universal.json --package "<package>"
dotnet run --project tests/Packaging/Consumers/CoreConsumer/CoreConsumer.csproj -p:WSS_ARTIFACT_DIR="<package>" --nologo
```

Only `Core consumer (Linux)` additionally runs:
`python tests/Packaging/verify_universal.py "<package>"`. This retains rejection
checks on a case-sensitive filesystem. Platform-wide manifest/identity/dependency
and native checks run here once per downstream platform, not again in Serial/BLE jobs.

Passing establishes complete package identities/dependency closure and Core public
API consumption on that OS: frame codec smoke behavior, deterministic acknowledged
operation through TestModeTransport, Core construction and advanced event capability.
It does not establish full functional coverage, Serial/BLE operation, physical
stimulation or Unity runtime compatibility. No hardware is required. Future public
artifact checks belong in `tests/Packaging/Consumers/CoreConsumer`; universal
validation/rejection cases belong in `tests/Packaging/` and the platform gate here.

##### Serial consumer sequence (all three platforms)

```powershell
dotnet run --project tests/Packaging/Consumers/SerialConsumer/SerialConsumer.csproj -p:WSS_ARTIFACT_DIR="<package>" --nologo
```

Passing establishes the packaged Core+Serial API and dependency boundary on that OS.
Behavior checks cover disconnected construction, configured/default options,
unchanged port name, and disposal without opening a port; the consumer also checks
Core codec framing/deframing. Loading checks cover unique Core/Serial identities,
no BLE activation, Ports version 8 and byte equality with the packaged host runtime
(Windows or Unix), plus the required build-time dependency inventory.

It does **not** test port enumeration, opening, reads/writes, real device protocols,
or comprehensive Serial communication. Disposal is exercised by scope exit, not
an exhaustive resource-leak test. The redistributed net462 Ports implementation
under Unity is explicitly not verified. No hardware is required. Extend artifact
checks in `tests/Packaging/Consumers/SerialConsumer`; future justified functional
coverage belongs in `tests/Serial/` and the appropriate Serial platform job.

##### Windows BLE sequence

The source/native layer runs these existing projects without filtering cases:

```powershell
dotnet restore tests/BLE/Windows/WSS.BLE.Windows.Tests/WSS.BLE.Windows.Tests.csproj --locked-mode --nologo
./tools/WindowsBleBridge/build.ps1 -Configuration Release -Architecture x64
dotnet test tests/BLE/Windows/WSS.BLE.Windows.Tests/WSS.BLE.Windows.Tests.csproj -c Release --no-build --no-restore --nologo
dotnet build WSS.Transport.BLE.Linux/WSS.Transport.BLE.Linux.csproj -c Release --no-dependencies -p:RestoreLockedMode=true --nologo
dotnet test tests/BLE/WSS.BLE.Tests/WSS.BLE.Tests.csproj -c Release -p:RestoreLockedMode=true -p:BuildProjectReferences=false --nologo
```

CI adds distinct TRX filenames for the two NUnit runs. The Windows NUnit suite
includes bridge/adapter, synthetic shim, and production-native regression fixtures;
they remain one suite, not repeated filtered invocations. The shim is copied only
to the test output; production fixtures explicitly load the separately built DLL.
The corrected VS/MSVC v143 and Windows SDK discovery remains unchanged.

After shared ZIP preparation, the artifact layer builds BleConsumer with
`dotnet build ... -c Release -p:WSS_ARTIFACT_DIR="<package>"`, runs it on .NET 9
with `--no-build --no-restore` for automatic selection, runs it again with
`-- --register` for explicit Windows registration, then rebuilds/runs with
`-p:ConsumerTargetFramework=net8.0`. The exact project is
`tests/Packaging/Consumers/BleConsumer/BleConsumer.csproj`.

Passing establishes portable facade/registration/cancellation behavior, managed/native
layouts, callback/GATT lifecycle/cleanup behavior under shim control, production
ABI/lifecycle/deadline regressions, and packaged Windows backend/API/dependency/ABI
activation on .NET 8/9. It does not establish physical scanning, reception, connection,
or successful GATT writes. The toolchain/native Windows APIs are required; no physical
controller or peripheral is required. Future portable functionality belongs in
`tests/BLE/WSS.BLE.Tests`, Windows/native cases in `tests/BLE/Windows/`, and artifact
boundary cases in BleConsumer. Keep production and shim isolation intact.

##### Linux BLE sequence

Locked restore and `dotnet test -c Release --no-restore` run
`tests/BLE/WSS.BLE.Tests/WSS.BLE.Tests.csproj`, with TRX output. This includes
source Linux compatibility and behavior tests. After shared ZIP preparation, CI
installs BlueZ, checks the daemon and system D-Bus, and classifies `btmgmt info`.
The existing classification sets `WSS_BLE_SKIP_NATIVE_PROBE=1` only for the known
zero-controller/unavailable-management-interface conditions; other errors fail.

BleConsumer is built/run on .NET 9 with the same project and packaged-directory
flags as Windows, first for automatic loading, then with `-- --register` for explicit
Linux registration. No .NET 8 or Windows-native suite is run on Linux.

Passing establishes portable/Linux source behavior and packaged backend dependency,
shared identity, framework and registration/loading compatibility. If a controller
interface is available, the bounded native-stack probe must also succeed; otherwise
only that probe is explicitly skipped, **not** managed dependency checks. BlueZ and
system D-Bus are required; a controller/peripheral is not. Passing or a classified
skip does not establish BLE discovery, connection, notification reception or
stimulation. Future source tests belong in `tests/BLE/WSS.BLE.Tests`, and artifact
checks in BleConsumer; retain the safe classification in this Linux job.

##### Packaging sequence

```powershell
dotnet run --project tools/ArtifactBuild/ArtifactBuild.csproj -- build-all --destination "<runner-temp>/release-assets/WSS-Universal" --tag "<release-tag-or-ci>"
./tools/ArtifactBuild/extract-universal.ps1 -ReleaseAssets "$env:RUNNER_TEMP/release-assets" -Destination "$env:RUNNER_TEMP/package-verification"
dotnet run --project tools/ArtifactBuild/ArtifactBuild.csproj -- validate --manifest tools/ArtifactBuild/manifests/universal.json --package "<runner-temp>/package-verification/WSS-Universal"
python tests/Packaging/verify_universal.py "<runner-temp>/package-verification/WSS-Universal"
```

After receiving the same run's universal macOS native input, `build-all` builds
its own managed graph, Linux/macOS backends and production-only Windows native
DLL (no shim), validates closure, stages the unchanged manifest, and generates one
ZIP/checksum. The extracted archive is checked before upload. Passing establishes
the artifact inventory, assembly/dependency identities, reference closure, native
exports/architectures, checksum and archive contract; it does not establish component
functionality or hardware/Unity operation. Windows native build tools are required,
not hardware. Future ZIP rejection/validation tests belong in `tests/Packaging/`;
artifact tooling remains in `tools/ArtifactBuild/`. No component functional suites
run here.

#### Dependencies and publication

Core conformance and native macOS compilation run independently. Packaging waits
for the macOS native input and builds its remaining inputs
and does not wait on BLE. All component consumer jobs need packaging; each BLE job
contains both source functional tests and extracted-artifact consumers, without
changing the projects' reference boundaries. macOS has Core/Serial consumers and BLE consumers on arm64 and x86_64.
The separate `dev-release.yml` publisher requires every producer testing/packaging
job, all nine downstream acceptance receipts, `WSS_DEV_RELEASE_ENABLED=true` and
the reviewed historical-tag guard. It reuses the accepted ZIP/checksum with exact
asset, existing-tag and prerelease checks. There is no rebuilding, repackaging or
separate platform distribution. PRs do not publish. Manual verification observes
evidence only; publication remains behind its own explicit controls. Manual
diagnostics are not release-gate substitutes.

### Adding or Organizing Tests

| Test category | Location |
| --- | --- |
| Core functionality | `tests/Core/` |
| BLE functionality | `tests/BLE/` |
| Windows BLE/native behavior | `tests/BLE/Windows/` |
| Future Serial functional coverage | `tests/Serial/` if justified (no project exists as part of this organization) |
| Packaged DLL consumers | `tests/Packaging/Consumers/` |
| Universal ZIP validation | `tests/Packaging/` |
| Manual diagnostics | Existing component-specific diagnostic tools, such as `tests/BLE/Windows/ScanConsumer` |

1. Prefer existing projects and categories.
2. Choose placement by the behavior or failure the test detects, not its name.
3. More cases alone do not justify a new project.
4. A new project alone does not justify a new GitHub Actions job.
5. Add functional suites to the relevant existing component job when practical.
6. Keep consumers independent of WSS source-project references and pointed at the ZIP.
7. Keep physical hardware tests explicitly opt-in and separately authorized.

Before introducing a folder, project, or CI job, document the distinct behavior or
responsibility, why existing categories cannot accommodate it, and any platform,
fixture, dependency, isolation or hardware requirements warranting separation.
Name its executing CI job and explain how it avoids duplicating existing coverage.
**A new category needs a meaningful technical reason, not a different test name or
growing test count.** Do not create a Serial functional project without that justification.

### Seeing Detailed Test Results

Open **Actions → Build universal WSS release → workflow run**, then the exact job
and its named steps. Console logs retain consumer diagnostics and packaging errors;
these tools report through exit codes, not invented NUnit case counts.

Every executing job writes a short `GITHUB_STEP_SUMMARY` using the standard-library
`tools/ci/summarize.py`: purpose, actual step outcomes, available TRX pass/fail and
not-executed/skipped counts, classified Linux controller skips and coverage limits.
Summary generation and NUnit result uploads run with `always()`; ordinary commands
retain failure exit codes and are not marked `continue-on-error`. Missing TRX or
skipped steps are not evidence of passing tests. A job skipped because packaging or
its gate failed has no executed summary.

Download `core-conformance-results`, `ble-linux-results`, or `ble-windows-results`
from the run's **Artifacts** section when produced, including failed test runs.
TRX files contain individual NUnit cases and failure details; do not maintain a
parallel manual case list here. Artifact retention follows GitHub/repository defaults.
This README describes intended permanent coverage; summaries report actual execution.

</details>

## Automation reference

<details>
<summary>Maintainer propagation contracts and evidence identities</summary>

`tools/ci/dev-propagation.json` requires C#, Unity, Python and MATLAB integrations
to consume the same canonical dev-push ZIP. Before the first integration handoff,
Core freezes a separate `wss-dev-propagation-plan` artifact and checks receiver
workflow registration, staged fanout capabilities and MATLAB readiness. The
existing two-file universal artifact is unchanged.

Deploy `dev-propagation.yml` and `propagate-matlab.yml` on Core's default main
branch before producing this candidate. A dev-only workflow file does not register
a GitHub workflow. Deploy receiver hooks on both main and dev before merging the
producer change. Every integration must publish `.github/dev-propagation.json`:

```json
{
  "schema": "wss-dev-stage-receiver/v1",
  "core_workflow": ".github/workflows/dev-propagation.yml",
  "fanout_workflow": "dispatch-cli-promotion.yml",
  "staged_applications": true,
  "applications": ["cwru-non-academic/CLI_CSharp_WSS_Application"]
}
```

Use each integration's fanout workflow and application list from the Core policy
(Unity uses `dispatch-unity-promotion.yml`; Python includes CLI and ROS). For a
candidate whose producer has the staging policy, a merged integration must hold
its normal application fanout until the Core authorization arrives. Preserve the
existing behavior for older candidates. The fanout workflow must support
`workflow_dispatch` on main with `producer_run_id`, `barrier_run_id` and
`barrier_attempt`; its run name must be
`Resume application promotion / wss-<producer-run>`.
Before creating an application write token, run the reviewed Core entry point
`node tools/ci/dev-propagation.mjs verify-barrier <producer-run> <barrier-run> <attempt>`
using read-only access. Then invoke the existing approval, source, candidate and
application validators for the returned actual integration merge; never substitute
a dispatch or preparation commit for that merge.

For policies declaring `application_fanout: per-integration`, the Core coordinator
checks each family's human-reviewed dependency merge and fresh normal CI at its
actual committed dev SHA, then authorizes only that family's linked applications.
It does not wait for the other integrations to advance that family. Older frozen
policies retain their original all-integration barrier. Final acceptance still
requires all applicable nine targets. A durable intent is uploaded before any
dispatch. Existing pending/successful correlated runs are reused; a failed,
ambiguous, expired or uncertain attempt requires inspected recovery. No merge,
rollback, release or tag is performed. Manual `mode: verify` reads evidence only
and emits no dispatch intent or application authorization; `mode: reconcile` may
dispatch. Scheduled reconciliation runs at most hourly.

MATLAB additionally requires `baseline_ready` and `candidate_preflight_ready` true
in both receiver configs and explicitly reviewed repository variables
`WSS_MATLAB_PROPAGATION_ENABLED=true` for wrapper and CLI. Source preparation does
not enable these settings. The existing App must already support Actions access
to the required destinations; this change does not create credentials or alter
its installation. Finish these external prerequisites before merging Core dev.
Dev propagation is separate from the disabled beta/stable release policy.


The MATLAB native evidence validator is pinned in the dev policy to the reviewed
R2026b update-aware implementation. A receiver validator change requires a fresh
canonical dev push and frozen plan; do not overwrite an old plan or replay its
failed promotions with changed validation code. Merge the receiver fix on both
branches, then the reviewed Core baseline update before using the new candidate.
The policy also pins the promotion contract test that keeps its legacy bootstrap
fixture separate from the installed candidate's payload verification.
The merged-wrapper CI validator is pinned as well, including its handling of
GitHub's empty PR associations after a reviewed merge.

### Python and Unity handoffs

The default-branch completion handlers in
[`propagate-python.yml`](.github/workflows/propagate-python.yml) and
[`propagate-unity.yml`](.github/workflows/propagate-unity.yml) accept only a
successful canonical `artifacts.yml` push to dev. PRs, manual producers, tags,
failures and forks are ineligible. Each handler independently verifies the exact
producer commit/run, all seventeen required producer jobs (including both macOS
BLE consumers and all three BLE aggregate jobs), the unique unexpired artifact, archive checksum and immutable
commit/run/artifact/archive/SHA-256 tuple. The payload is neither rebuilt nor run
by the handoff preparation.

Preparation checks default-branch workflow registration and prior correlated
requests before obtaining a write token. One pending/successful request is reused;
failed, ambiguous or uncertain outcomes require inspection before retry. Each
integration runs paired candidate preflights before its restricted dependency PR.
Applications consume the actual human-reviewed integration merge, run their own
preflight and open gitlink-only dev PRs. Python's application routes include both
CLI and ROS. All normal committed-state CI and approval/projection validators
remain required. An identical payload/gitlink preserves existing provenance as a
verified no-op; it does not manufacture a dependency merge or release receipt.

The existing `WSS_GITHUB_APP_ID` / `WSS_GITHUB_APP_PRIVATE_KEY` configuration
supplies narrowly scoped tokens: read access for evidence, Actions-write only for
the actual dispatch destination, and target-only Contents/PR-write only after
validation. Missing workflows or App capability fail at that hop. Existing C#
access does not prove access to another destination. These scripts do not create
credentials, change installation grants, merge PRs or publish applications.

Python conformance embeds CPython 3.12/pythonnet 3.0.5 in the NUnit host and retains
the ten canonical cases/eighteen assertions through production wrapper/controller
paths and Core's emulator. Unity's ordinary .NET harness retains those cases but
does not establish Unity Editor, scene/UI or Player execution. Hosted package/native
loading does not establish a physical connection on any language route.

#### Evidence identities

A Git commit SHA identifies the exact source revision; a Git blob/tree ID freezes
reviewed validator content; a SHA-256 digest binds archive, policy or proof bytes.
They are different identities and are checked together with workflow/run/artifact
IDs. Operational values belong in the executable policies, candidate records and
Actions evidence, where validators use them. Runtime users need the artifact's
checksum, not a copied list of internal revisions. The CI summary links to the
README at its tested commit so later documentation changes cannot misdescribe an
older run. No operational pin is changed by this documentation consolidation.

</details>

## Dev release reference

<details>
<summary>Implemented receipt contract, reconciliation and publication gates</summary>

### Frozen plan

When `WSS_DEV_ACCEPTANCE_ENABLED=true`, the successful canonical `artifacts.yml`
**dev push** produces a separate
`wss-propagation-plan` Actions artifact. Do not add files to
`universal-release-assets`: its ZIP/checksum two-file contract is unchanged.

`plan.json` uses schema `wss-propagation-plan/v1` and contains `channel: "dev"`,
the existing six-field `candidate` tuple, a complete frozen `manifest`, its
`manifest_sha256`, `target_snapshots`, and `plan_id`. Digests use SHA-256 of UTF-8
JSON with recursively sorted object keys and no insignificant whitespace. `plan_id`
is the digest of the plan excluding `plan_id`. Snapshot every required target's
dev head and Git object IDs for its `trusted_paths`. Missing/unregistered receiver
tooling blocks plan creation; it is not an implicit exclusion.

The required target IDs are `csharp-wrapper`, `csharp-cli`, `unity-wrapper`,
`unity-gui`, `python-wrapper`, `python-cli`, `python-ros`, `matlab-wrapper` and
`matlab-cli`. All nine are mandatory and immutable for that plan; there are no
implicit exclusions. MATLAB retains its native conformance and packaged macOS
loading requirements.

### Acceptance receipts

Receivers use `.github/workflows/release-receipt.yml` and a trusted receipt writer
to publish a
single JSON file `receipt.json` in an artifact named
`wss-acceptance-<target_id>-<plan_id>-a<run_attempt>` after actual human merge and
successful normal committed-state CI. An explicit reconciliation dispatch may
recheck an existing dependency merge; it must not create a dependency PR or merge
one. Preparation-only or no-op dispositions do not produce acceptance receipts.
The receipt writer needs read access only. Receipt tooling must be registered on default/main and dev; missing tooling blocks
plan production.

The shared reconciliation entry point is `workflow_dispatch` with required string
inputs `producer_run_id` and `plan_id`. Its exact run-name is
`WSS acceptance <target_id> <plan_id>`. The writer downloads the producer's frozen
plan and rejects an input plan ID mismatch. Core may request this read-only receipt
once per target/plan. Existing pending/successful attempts are reused; a failed
attempt requires explicit inspected recovery, never a duplicate automatic retry.

Receipt envelope:

```json
{
  "schema": "wss-acceptance-receipt/v1",
  "plan_id": "<64 hex>",
  "channel": "dev",
  "target_id": "csharp-cli",
  "candidate": {"repository":"cwru-non-academic/WSSCore","commit":"<40 hex>","run_id":"1","artifact_id":"2","archive":"WSS-Universal-ci.zip","sha256":"<64 hex>"},
  "mode": "merged-pr",
  "merge": {"pr_number":"15","head_sha":"<40 hex>","sha":"<actual API merge SHA>"},
  "normal_ci": {"run_id":"3","attempt":1},
  "preflights": [{"repository":"cwru-non-academic/CLI_CSharp_WSS_Application","run_id":"4","source_sha":"<40 hex>","workflow":".github/workflows/wss-preflight.yml"}],
  "parents": {"csharp-wrapper":"<actual accepted wrapper merge SHA>"},
  "emitter": {"run_id":"5","attempt":1,"source_sha":"<same actual merge SHA>","workflow":".github/workflows/release-receipt.yml"},
  "coverage": ["canonical-conformance","packaged-macos-ble-load"]
}
```

The writer must first invoke existing receiver-specific approval/projection,
candidate checksum, paired preflight, actual merge, normal CI, source-scope and
payload/gitlink validators. JSON claims alone are not authority. Core independently
checks GitHub PR/run/artifact identity, exact current branch state, required jobs,
parent gitlinks, and the frozen receipt tooling Git IDs at the actual merge.
Each preflight source SHA must equal that repository's frozen target snapshot;
a successful run for another source cannot satisfy acceptance.
Each preflight listed in the receipt must additionally retain the normalized
`wss-candidate-provenance` artifact with exactly one `candidate.json` file containing
the six-field candidate tuple. C# already has this contract. Python/ROS and Unity
must preserve all existing richer/native/projection evidence and its validators;
the normalized artifact is an additional cross-language binding, not replacement
evidence. Do not activate until each registered preflight emits it after success.
The receipt emitter must be a successful same-repository `workflow_dispatch` run
at the actual accepted dev merge, with exact attempt and a unique unexpired
artifact. Fork/caller-nominated code is never an eligible emitter. Normal CI must
also be a fresh `workflow_dispatch` (or `push`) run at the actual accepted merge;
PR-head API identities cannot establish what synthetic merge checkout was tested.
Core reconciliation may request existing normal workflows before the receipt.

Preserve existing no-op provenance. A no-op must be explicitly identified and
anchored to a real prior reviewed merge plus fresh full candidate-byte/projection
equivalence and normal-state proof; it must not manufacture a metadata-only PR.
Do not emit a `merged-pr` receipt for a dispatch, open PR, synthetic fixture,
preflight-only run, skipped check or unverified no-op. Initial publisher fails
closed on no-op receipts until equivalence verification is implemented/tested.

### Reconciliation and publication

The nine required targets are declared in `tools/release/targets.dev.json`.
The executable manifest and verifier in the selected revision define the exact
target/job set. Never interpret a missing required target as an exclusion.

`dev-release.yml` and `tools/release/cli.mjs` reconcile a canonical dev producer
and its frozen plan. `WSS_DEV_ACCEPTANCE_ENABLED=true` enables plan creation and
receipt reconciliation independently of publication. This job has read-only contents
permission; its scoped Actions token requests only registered normal CI and receipt
workflows. Publication is a separate job that additionally requires
`WSS_DEV_RELEASE_ENABLED=true`, successful acceptance and historical-tag protection.
Neither setting is changed by a source merge. Register matching trusted main tooling
before producing the candidate. An uploaded intent
precedes each dispatch. Reconciliation requests only normal committed-state CI and
receipt workflows; it does not create dependency PRs, merge, rollback or rewrite
branches. Pending/successful correlated runs are reused. Failed, ambiguous, expired
or uncertain proof stops for inspection; an existing intent without a confirmed
run blocks automatic retries. HTTP dispatch success is not acceptance.

The staged coordinator alone requests integration committed-state CI. Acceptance
reconciliation requests application CI and receipt workflows; it never requests a
second integration run. Both use the same discovery of exact-head dev push or
manual runs created after the actual merge. Failed, ambiguous, foreign or uncertain
evidence blocks a request instead of being hidden by another successful run.

Cross-repository receipt, preflight and journal reads use a short-lived read-only
token from the existing WSS App. Its installation must already allow contents,
Actions and pull-request reads for Core and all nine targets. Source changes do not
grant that access. The optional publisher keeps Core's contents-write token
separate and recollects evidence through the read-only token.

Receipt failure, missing/expired evidence, stale source, wrong parent gitlinks or
missing required jobs block publication. Partial human merges remain in place;
fixes and reverts are new reviewed PRs. A matching existing release is reused only
when tag commit, prerelease state, exact asset inventory and downloaded digests all
match. Partial/mismatched releases block; assets are never overwritten or deleted.
The release retains original candidate identity, acceptance merges, proof digests
and plan/manifest IDs. ZIP bytes are downloaded and hashed again, never rebuilt.

### Historical tag guard

Removing today's tag trigger cannot change historical workflow definitions. A new
`v*` tag pointing at an old commit could execute its old publisher. The verifier
requires a live, reviewed repository ruleset: `target: tag`, `enforcement: active`,
source exactly this repository, include exactly `refs/tags/v*`, no exclusions or
bypass actors, and exactly creation/update/deletion prohibitions. The reviewed
`legacy_tag_guard` ID and policy SHA-256 in the manifest must match the full live
policy both when freezing a publication-ready plan and publishing. Verification-only
freezes may use an explicitly null ID and digest; this never authorizes publication.
Partially configured or changed policy, failed API reads and hidden bypass metadata
still block verification. Null configuration always blocks publication.

`node tools/release/cli.mjs inspect-tag-guard <ruleset-id>` is a read-only inspection
entry point. No setting, permission, rule, credential or release is changed by
inspection. The existing audit identity must be able to read the complete policy;
omitted bypass metadata cannot mean no bypasses. The accepted release record
retains ruleset ID and digest. Repository administrators' manual powers are outside
this Actions verifier.

### No-op limitations

The v1 publisher rejects no-op receipts. It cannot count a migration PR, empty PR,
preflight-only run, skipped check or fabricated merge as dependency acceptance.
`equivalence.mjs` and its tests cover inventory, actual file-byte and restricted
source-equivalence primitives; they do not implement a deployed v2 receipt route,
GitHub ancestry proof or end-to-end no-op acceptance. Existing installed provenance
must remain unchanged.

Before the first integration Actions-write token, the producer's read-only
projection classifier hashes the actual checksum-verified ZIP and compares its
reviewed projections with all four frozen integration heads. C#, Python and
MATLAB retain the universal relative layout; Unity uses its 20-file projection,
flat Windows backend names, preserved runtime paths and macOS subdirectory.
Committed projection hashes/importer metadata and provenance are checked against
immutable Git blobs; no receiver code is executed. Frozen main/dev heads are
rechecked after all reads. `wss-projection-classification/report.json` records
each result as `payload-change`, `equivalent-but-unsupported`, or `blocked`.
Only four `payload-change` results allow the existing reviewed promotion route.
One unsupported equality or missing/invalid/drifting target blocks the whole
handoff before writes. The report is diagnostic, never acceptance or a new
provenance record; all existing receiver/preflight/merge gates still apply.
Individual file hashes are compared here; aggregate projection digests, complete
approval validation, Unity importer policy, ABI/load checks and source contracts
remain the responsibility of the existing receiver validators. A `payload-change`
classification permits that validation route and does not assert it has passed.
The C# row also applies the receiver's installed-candidate ordering guard: the
same producer run must retain its exact six-field identity, a same-source new run
must move forward, and a changed Core source must descend from the installed
source. Missing, contradictory or divergent ancestry blocks the whole handoff.
The C# receiver retains its own final checks; this diagnostic cannot authorize a
stale candidate or replace an installed provenance record.

<details>
<summary>Historical identifiers retained pending documentation review</summary>

The former deployment notes identify the initial contract-only Core PR17 merge as
`4484d1c`; this is historical traceability, not the current verifier pin or a runtime
requirement. They also record a read-only 2026-10-29 audit of branch rulesets
`22505518` (dev) and `22505396` (main), with no tag rule at that time. Those numeric
values are GitHub ruleset IDs, not SHAs, and are not evidence of current policy.
Operational guard configuration and live inspection determine eligibility.

</details>

</details>
