# WSSCore

WSSCore implements the shared WSS protocol, configuration, Core/parameter/model
layers, and Serial, BLE and simulated transports. Most application developers
should start with an integration library:

- [C#](https://github.com/cwru-non-academic/CSharp_WSS_Integration_Lib#readme)
- [Python](https://github.com/cwru-non-academic/Python_WSS_Integration_Lib#readme)
- [Unity](https://github.com/cwru-non-academic/Unity_WSS_Integration_Lib#readme)
- [MATLAB](https://github.com/cwru-non-academic/Matlab_WSS_Integration_Lib#readme)

Each README explains host setup and links to its applications. The
[shared documentation](https://cwru-non-academic.github.io/WSS_Documentation/)
covers architecture, lifecycle, configuration, platform support and the API.

## Use a released distribution

Download the universal ZIP and its matching `SHA256SUMS.txt` from
[WSSCore releases](https://github.com/cwru-non-academic/WSSCore/releases).
Verify the archive checksum before extracting it. Use the complete nested
`WSS-Universal/` directory, including backend and runtime subdirectories.

Keep one coherent release together; do not mix individual DLLs from other
releases. Integration repositories already carry their pinned distribution.
For a direct .NET host, reference `WSS_Core_Interface.dll` and the required
`WSS.Transport.Serial.dll` / `WSS.Transport.BLE.dll` from that same distribution.
Use System.IO.Ports NuGet 8.0.0 for the .NET host's runtime selection.
See [consumer integration requirements](tools/ArtifactBuild/CSHARP-HANDOFF.md).

Serial and BLE are supported on Windows, Linux and macOS. Windows BLE requires
an x64 process; Linux BLE requires BlueZ and system D-Bus. Device permissions
and Bluetooth access are configured in the host operating system.

## Build from source

Install .NET 8 for the functional test runtime and .NET 9 for artifact tooling.
The first restore needs NuGet access. From a source checkout:

```bash
git clone https://github.com/cwru-non-academic/WSSCore.git
cd WSSCore
dotnet restore WSS_Core_Interface.sln --locked-mode
dotnet build WSS_Core_Interface.sln -c Release --no-restore
```

The managed projects target .NET Standard 2.0. Building the complete Windows
native distribution additionally requires Visual Studio 2022/MSVC v143 and
Windows SDK 10.0.22621.0. See the [Windows bridge guide](tools/WindowsBleBridge/README.md)
and [artifact build guide](tools/ArtifactBuild/README.md) for native builds,
the exact package inventory and deployment rules.

## First checks without hardware

These functional suites use emulators and synthetic inputs:

```bash
dotnet test tests/Core/WSS.Core.Tests/WSS.Core.Tests.csproj -c Release --no-build --no-restore
dotnet test tests/BLE/WSS.BLE.Tests/WSS.BLE.Tests.csproj -c Release --no-build --no-restore
```

For a minimal independent consumer of an extracted distribution:

```bash
dotnet run --project tests/Packaging/Consumers/CoreConsumer/CoreConsumer.csproj -p:WSS_ARTIFACT_DIR="/absolute/path/to/WSS-Universal"
```

This consumer uses TestModeTransport and does not open hardware. For an
interactive simulated application, follow the
[C# CLI README](https://github.com/cwru-non-academic/CLI_CSharp_WSS_Application#readme).

## Maintainer references

- [Tests and CI responsibilities](tests/README.md)
- [Universal artifact build and deployment](tools/ArtifactBuild/README.md)
- [Windows native toolchain and ABI](tools/WindowsBleBridge/README.md)
- [Shared CI and dependency guides](https://cwru-non-academic.github.io/WSS_Documentation/apiDocs/ci.html)

Automated software tests and package validation are separate from physical device
testing. Hardware diagnostics are opt-in; see the test guide before using them.
