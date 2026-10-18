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

`Build and validate artifacts` on `windows-2022` builds its own managed/Linux/backend
and production-native inputs, validates the payload and extracted archive, runs
packaging rejection checks, and uploads one ZIP/checksum; it runs no functional suites.
Independent `Core conformance` runs once on Linux. `Core consumer` and `Serial consumer`
jobs run on Windows/Linux/macOS; `BLE consumer` jobs combine source functional/native
tests and packaged consumers on Linux/Windows, keeping those project boundaries separate.
Every consumer downloads that same ZIP. Platform-wide package validation runs in the
Core consumer jobs; Linux case-sensitive rejection runs there once. BLE's classified
controller skip suppresses only the native probe, never managed dependency checks.
Serial smoke checks do not establish hardware communication. See the authoritative
[CI/test catalog](../../tests/README.md#ci-jobs-and-test-responsibilities) for commands,
coverage, placement rules and job-summary/TRX visibility.
`Publish — Universal ZIP` requires all testing/packaging jobs and reuses the validated
ZIP/checksum without rebuilding, restaging, or rezipping. PRs do not publish.

## Automatic dev propagation

Ordinary `dev` pushes run this same producer and all existing tests/consumers. The gated
`Start routine downstream validation` job reuses its uploaded `universal-release-assets`
artifact and checksum (no second build, release or tag) and automatically dispatches the
existing Integration `promote-wss.yml` on `dev` with the exact commit, producer run ID,
artifact ID and `WSS-Universal-ci.zip` SHA-256. Integration waits for this entire producer
run to complete successfully before issuing downstream preflight dispatch credentials.
PR synthetic merge SHAs, manual producer runs and tag pushes never enter this routine path.

Integration automatically validates the same candidate in Integration and CLI preflights,
constructs/checks schema-2 approval, and opens/reuses a lib-only Integration PR. Reviewed
Integration merge starts the existing CLI gitlink-only PR cascade; both PRs retain normal
committed-state CI and human merges. Identical complete payload bytes produce a verified
no-op rather than a metadata-only PR. Correlation is `wss-<run-id>-<artifact-id>` throughout.

Deploy the Integration automation first. The existing `WSS_GITHUB_APP_ID` variable and
`WSS_GITHUB_APP_PRIVATE_KEY` secret must be available in Core; its App token requests only
Integration Actions write. Missing credentials/permissions or failed handoff fail clearly.
Nothing in this path authorizes an auto-merge or protection bypass.

For the controlled acceptance run, `WSS_Core_Interface.csproj` adds only
`AssemblyMetadata("WssPropagationAcceptance", "automatic-universal-v1")`, changing a
packaged assembly without changing public APIs or stimulation behavior. Publish/merge only
with authorization, and observe the automatic push path rather than dispatching workflows
manually. A local build/safety check is not a hosted end-to-end acceptance pass; record the
actual producer/preflight runs, artifact/hash and both PRs after authorized execution.
