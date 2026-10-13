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

## CI

The [artifact workflow](../.github/workflows/artifacts.yml) separates these gates:

| Job | Coverage/responsibility |
| --- | --- |
| `Test — Core` | All 69 Core NUnit cases, once on Ubuntu; platform-independent functional coverage |
| `Test — BLE (Linux)` | Portable BLE NUnit suite, including Linux compatibility/behavior checks; no controller required |
| `Test — BLE (Windows)` | Portable BLE plus all Windows shim/production-native NUnit cases on `windows-2022`; native files remain isolated |
| `Test — Serial (Linux/Windows/macOS)` | Separate platform jobs running SerialConsumer against the extracted ZIP, preserving behavior/dependency-loading checks; not comprehensive hardware Serial tests |
| `Package — Universal ZIP (Windows)` | Independently builds managed/Linux/production-native inputs on `windows-2022`, validates manifest/exports/dependencies and archive integrity, uploads one ZIP/checksum; no functional suites |
| `Test — Universal ZIP Consumer (Linux/Windows/macOS)` | CoreConsumer on all three platforms; BleConsumer on Linux/Windows with automatic and explicit registration, plus Windows .NET 8 alongside .NET 9; no SerialConsumer |
| `Publish — Universal ZIP` | Requires every component, packaging, and consumer gate; publishes only on successful `v*` tag pushes, reusing the tested ZIP/checksum without rebuilding |

All Serial and universal consumer jobs download, verify, and extract the same ZIP.
Linux universal consumers retain BlueZ dependency checks, narrowly classified and
reported controller skips, and case-sensitive package rejection. macOS has no BLE
gate. Serial jobs leave room for future platform-specific functional tests.
ScanConsumer/peripheral diagnostics are not automatic CI gates; PRs never publish.
