# WSSCore

WSSCore provides the shared WSS protocol and transport implementations for C# and
Unity applications. API and application documentation is available on
[GitHub Pages](https://cwru-non-academic.github.io/WSS_Documentation/).

## Architecture

All six WSS managed libraries target **.NET Standard 2.0**:

| Project | Responsibility / dependencies |
| --- | --- |
| `WSS_Core_Interface.csproj` | Protocol, command handling, configuration, and shared transport interfaces |
| `WSS.Transport.Serial` | Serial transport; references Core and System.IO.Ports 8.0.0 |
| `WSS.Transport.BLE` | Portable BLE facade; references Core and Serial |
| `WSS.Transport.BLE.Windows` | Windows adapter implementing the common BLE contract; references facade, Core, and native wrapper |
| `tools/WindowsBleBridge/managed` | `WSS.Transport.BLE.Windows.Native.dll`: P/Invoke, callbacks, marshaling, resource ownership |
| `WSS.Transport.BLE.Linux` | BlueZ backend; references facade, Core, Linux.Bluetooth, and Tmds.DBus |

The Windows wrapper calls the separate production C++/WinRT **`WssBleWindows.dll`**.
Windows BLE requires a Windows x64 process. Linux BLE requires BlueZ and system
D-Bus. There are no macOS/Android BLE backends or Python bindings.

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
`net2_BLE`, manual dispatch, and pushed `v*` tags. Independent component gates run
Core functional tests once on Ubuntu, portable BLE on Linux/Windows, and Windows
shim/production-native BLE tests on `windows-2022`. A separate Windows packaging
job builds its own inputs and validates one universal ZIP without functional suites.
Dedicated Serial artifact-consumer jobs extract that ZIP on Linux, Windows, and
macOS; separate universal consumer jobs cover Core on all three and BLE on
Linux/Windows only. These Serial checks do not exercise hardware. Missing Linux
controllers are explicitly reported and do not suppress managed dependency checks.
See [the CI coverage table](tests/README.md#ci) for the individual gates.

Only successful `v*` tag pushes publish after all component, packaging, and consumer
jobs pass. The release job downloads the already-tested
ZIP/checksum and verifies them without rebuilding or repackaging. Hyphenated tags
are prereleases. PRs, ordinary branch pushes, and manual validation do not publish.
Hardware diagnostics are opt-in and are not release-gate substitutes for automated tests.
