# WSSCore

WSSCore provides the shared WSS protocol and transport implementations for C# and
Unity applications. API and application documentation is available on
[GitHub Pages](https://cwru-non-academic.github.io/WSS_Documentation/).

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

## Dev propagation

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

The Core coordinator waits for all four human-reviewed dependency merges, requests
fresh normal CI at their committed dev SHAs, and authorizes all application routes
only after every required job passes. A durable intent is uploaded before any
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

See [Windows toolchain and ABI instructions](tools/WindowsBleBridge/README.md)
for direct MSBuild commands and alternate SDK selection. See
[tests/README.md](tests/README.md) for automated test commands and safety boundaries.

## One universal distribution

Build the complete release from the repository root on Windows:

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
both backend dependency trees, production Windows native code, and the Ports
runtime assets. Verify the checksum before extraction and preserve the complete
payload for redistribution. Do not mix independently sourced DLLs or download
separate platform packages. The [artifact guide](tools/ArtifactBuild/README.md)
lists the exact inventory, deployment rules, and validation commands.

## C# and Unity consumption

Reference `WSS_Core_Interface.dll`, `WSS.Transport.Serial.dll`, and
`WSS.Transport.BLE.dll` as needed from the same extracted artifact. Serial types
are defined directly in the Serial assembly; the BLE assembly does not forward them.
Rebuild internal libraries, applications, and Unity assembly references against the
final assembly set. Constructors/options and shared transport contracts are preserved.

The facade automatically loads the current platform backend from
`AppContext.BaseDirectory/backends/windows` or `backends/linux`. Deploy the full
backend/runtime trees without duplicate shared WSS DLLs in backend directories.
Hosts that preload backend assemblies can register explicitly:

```csharp
Wss.Transports.Backends.Windows.WindowsBleBackend.Register();
// On Linux:
Wss.Transports.Backends.Linux.LinuxBleBackend.Register();
```

For .NET applications use `System.IO.Ports` NuGet **8.0.0** to select the host
runtime implementation; do not overwrite it with the portable root net462 asset.
Keep the original complete artifact for redistribution. Unity plugin deployment
and native resolution are host-specific; configure the production native DLL for
Windows x86_64, preload/register if needed, and validate Editor/player loading.
See [consumer integration requirements](tools/ArtifactBuild/CSHARP-HANDOFF.md).

### Linux serial setup

- On Debian/Ubuntu install `libudev1`; Unity build hosts may also need `libudev-dev`.
- Grant access to the intended `/dev/tty*` device, commonly by adding the user to
  `dialout` with `sudo usermod -a -G dialout $USER`, then logging out and back in.
- Check device permissions with `ls -l /dev/ttyUSB0`. Missing access can cause
  `SerialPort.Open()` to throw `UnauthorizedAccessException`.
- Linux Unity/WSL hosts have the same OS dependency and device-access requirements.

## CI and releases

`.github/workflows/artifacts.yml` runs for PRs targeting `dev`, `main`, or
`net2_BLE`, manual dispatch, and pushed `v*` tags. `Core conformance` runs the
functional suite once on Linux. `Build and validate artifacts` builds its own inputs
on `windows-2022` and validates one universal ZIP without functional suites.
`Core consumer` and `Serial consumer` jobs run on Windows/Linux/macOS;
`BLE consumer` jobs combine source functional suites and packaged consumers on
Linux and Windows (`windows-2022` for isolated shim/production-native testing).
All consumers use the same verified, extracted ZIP. Serial smoke checks do not
exercise port I/O; classified Linux controller skips do not suppress dependencies.
See [CI Jobs and Test Responsibilities](tests/README.md#ci-jobs-and-test-responsibilities)
for the authoritative catalog and placement policy. Named logs, job summaries and
downloadable NUnit TRX artifacts expose actual execution and failures.

Only successful `v*` tag pushes publish after all component, packaging, and consumer
jobs pass. The release job downloads the already-tested
ZIP/checksum and verifies them without rebuilding or repackaging. Hyphenated tags
are prereleases. PRs, ordinary branch pushes, and manual validation do not publish.
Hardware diagnostics are opt-in and are not release-gate substitutes for automated tests.
# macOS BLE

The separate CoreBluetooth backend and universal native bridge are described in
[MacBleBridge](tools/MacBleBridge/README.md). The reviewed receiver migration and
Python-first candidate sequence are in [macOS BLE rollout](tools/ci/MACOS_BLE_ROLLOUT.md).
