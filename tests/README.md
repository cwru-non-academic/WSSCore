# Test organization

Run commands from the repository root. Automated functional suites use NUnit on
.NET 8; Windows interop tests require Windows x64 and the native toolchain documented
in [the bridge guide](../tools/WindowsBleBridge/README.md). Artifact tooling uses .NET 9.

| Location | Purpose |
| --- | --- |
| `Core/WSS.Core.Tests` | Protocol framing, command/configuration behavior, initialization, runtime edits, stimulation conformance through emulated transports |
| `BLE/WSS.BLE.Tests` | Portable facade, backend registration/selection, shared dependency identities, Linux portability and cancellation helpers |
| `BLE/Windows/WSS.BLE.Windows.Tests` | Managed/native layouts and callbacks, Windows adapter, GATT lifecycle, notification handling, fragmentation, cancellation and cleanup |
| `BLE/Windows/NativeTestShim` | Synthetic C++ ABI implementation with deterministic worker/barrier/failure controls; test dependency only |
| `BLE/Windows/ScanConsumer` | Opt-in production-native lifecycle/scanning/NUS diagnostic, not a unit test suite |
| `Packaging/Consumers` | Independent Core/Serial/BLE applications referencing extracted artifact DLLs, not source projects |
| `Packaging/verify_universal.py` | Exact package/dependency/native contract rejection cases; no hardware |

## Automated functional suites

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

## Artifact boundary

Build and extract the [universal ZIP](../tools/ArtifactBuild/README.md), then pass
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

## Manual hardware diagnostics

ScanConsumer copies **production** native code. Its lifecycle-only mode needs no
peripheral; scanning requires a usable adapter and nearby advertiser. Explicit NUS
mode is restricted to a known non-actuating test peripheral. Writes require explicit
bytes and confirmation; no safe write target is inferred from an advertisement.
See [diagnostic commands and boundaries](../tools/WindowsBleBridge/README.md).
Skipped/empty scans do not establish reception or GATT success. Never run stimulation
against physical hardware as part of these automated checks.

## CI Jobs and Test Responsibilities

This is the living catalog for [Build universal WSS release](../.github/workflows/artifacts.yml).
The workflow is the authority for exact command flags and execution order; the
sequences below describe the same suites and boundaries. All commands run from
the repository root. CI installs .NET 8/9, uses Release for functional/native/BLE
builds, and uses locked restores for the functional suites and packaging inputs.

### Test categories

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

### Job catalog

Each row identifies an actual displayed job. Shared sequences and their pass/fail
boundaries below apply to every row referencing them, including its future placement.

| Exact display name | OS / runner | Purpose, projects/tools, and categories | Sequence / coverage boundary |
| --- | --- | --- | --- |
| `Core conformance` | Linux / `ubuntu-latest` | `Core/WSS.Core.Tests`: protocol, configuration, initialization, runtime edits and emulated transport conformance; functional | Core functional sequence |
| `Build and validate artifacts` | Windows / `windows-2022` | ArtifactBuild, production-only native build, extraction script, `Packaging/verify_universal.py`; packaging | Packaging sequence |
| `Core consumer (Windows)` | Windows / `windows-latest` | `Packaging/Consumers/CoreConsumer` plus ArtifactBuild; consumer + platform package validation | Core consumer sequence |
| `Core consumer (Linux)` | Linux / `ubuntu-latest` | CoreConsumer, ArtifactBuild, `Packaging/verify_universal.py`; consumer + Linux filesystem package rejection | Core consumer sequence + Linux rejection |
| `Core consumer (macOS)` | macOS / `macos-latest` | CoreConsumer plus ArtifactBuild; consumer + platform package validation | Core consumer sequence |
| `Serial consumer (Windows)` | Windows / `windows-latest` | `Packaging/Consumers/SerialConsumer`; artifact consumer | Serial consumer sequence |
| `Serial consumer (Linux)` | Linux / `ubuntu-latest` | SerialConsumer; artifact consumer | Serial consumer sequence |
| `Serial consumer (macOS)` | macOS / `macos-latest` | SerialConsumer; artifact consumer | Serial consumer sequence |
| `BLE consumer (Windows)` | Windows / `windows-2022` | `BLE/WSS.BLE.Tests`, `BLE/Windows/WSS.BLE.Windows.Tests`, isolated NativeTestShim, production native DLL, `Packaging/Consumers/BleConsumer`; functional + native + consumer | Windows BLE sequence |
| `BLE consumer (Linux)` | Linux / `ubuntu-latest` | `BLE/WSS.BLE.Tests` (including Linux backend compatibility), BleConsumer, BlueZ/system D-Bus; functional + consumer | Linux BLE sequence |

#### Core functional sequence

`dotnet restore tests/Core/WSS.Core.Tests/WSS.Core.Tests.csproj --locked-mode`,
then `dotnet test` on that project with `-c Release --no-restore` and TRX output.
The full suite runs **once, on Linux**, without a case filter.

Passing establishes source Core conformance through emulated transports. It does
not establish platform DLL consumption, physical stimulation, or device firmware
compatibility. No hardware is required. Future related tests belong in the existing
`tests/Core/WSS.Core.Tests` project and this job.

#### Shared consumer preparation

Every Core, Serial and BLE consumer job downloads **the same**
`universal-release-assets` artifact, then runs:

```powershell
./tools/ArtifactBuild/extract-universal.ps1 -ReleaseAssets "$env:RUNNER_TEMP/release-input" -Destination "$env:RUNNER_TEMP/universal"
```

This checks the single ZIP/checksum asset contract and SHA256 before extraction.
In the following commands, `<package>` means
`<runner-temp>/universal/WSS-Universal`, never a source build output.

#### Core consumer sequence (all three platforms)

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

#### Serial consumer sequence (all three platforms)

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

#### Windows BLE sequence

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

#### Linux BLE sequence

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

#### Packaging sequence

```powershell
dotnet run --project tools/ArtifactBuild/ArtifactBuild.csproj -- build-all --destination "<runner-temp>/release-assets/WSS-Universal" --tag "<release-tag-or-ci>"
./tools/ArtifactBuild/extract-universal.ps1 -ReleaseAssets "$env:RUNNER_TEMP/release-assets" -Destination "$env:RUNNER_TEMP/package-verification"
dotnet run --project tools/ArtifactBuild/ArtifactBuild.csproj -- validate --manifest tools/ArtifactBuild/manifests/universal.json --package "<runner-temp>/package-verification/WSS-Universal"
python tests/Packaging/verify_universal.py "<runner-temp>/package-verification/WSS-Universal"
```

`build-all` builds its own managed graph, Linux backend and production-only native
DLL (no shim), validates closure, stages the unchanged manifest, and generates one
ZIP/checksum. The extracted archive is checked before upload. Passing establishes
the artifact inventory, assembly/dependency identities, reference closure, native
exports/architectures, checksum and archive contract; it does not establish component
functionality or hardware/Unity operation. Windows native build tools are required,
not hardware. Future ZIP rejection/validation tests belong in `tests/Packaging/`;
artifact tooling remains in `tools/ArtifactBuild/`. No component functional suites
run here.

### Dependencies and publication

Core conformance and packaging run independently. Packaging builds its own inputs
and does not wait on BLE. All component consumer jobs need packaging; each BLE job
contains both source functional tests and extracted-artifact consumers, without
changing the projects' reference boundaries. macOS has Core/Serial consumers only.
`Publish — Universal ZIP` requires every testing/packaging job, including every matrix
entry. Only successful pushed `v*` tags publish the already-tested ZIP/checksum;
verification, exact asset count, existing-tag safety and prerelease rules remain in
place. There is no rebuilding, repackaging, or separate platform distribution. PRs
and manual dispatch never publish. Manual diagnostics are not release-gate substitutes.

## Adding or Organizing Tests

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

## Seeing Detailed Test Results

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
