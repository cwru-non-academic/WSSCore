@ -1,6 +1,54 @@
# WSSInterfacingCode
C# class that has all the methods to communicate with the WSS and software specific classes that implements some of the functionalities necessary for that program.

All documentation about this API and its implementations can be found in [GitHub Pages](https://cwru-non-academic.github.io/WSS_Documentation/).

## Linux serial setup
- Install the native dependency that backs `System.IO.Ports`. On Debian/Ubuntu-based distros run `sudo apt install libudev1` (Unity build hosts may also require `libudev-dev`). Most other distros ship the equivalent package by default.
- Ensure your user can access `/dev/tty*` devices. Add yourself to the `dialout` (or distro-specific) group via `sudo usermod -a -G dialout $USER`, then log out and back in so the new group takes effect.
- You can confirm permissions with `ls -l /dev/ttyUSB0`; the owner or group should include your account after the step above. Without this, `SerialPort.Open()` will throw `UnauthorizedAccessException`.
- Unity on Linux/WSL uses the same transport class, so the same `libudev` dependency and group membership apply when running in the editor or player.

## Standalone C# apps
When building a separate .NET console/worker app that references the compiled `WSS_Core_Interface.dll`, ensure the project targets a modern framework (e.g., `net8.0`) and explicitly references the dependencies that live outside the base runtime. The sample below mirrors a working setup:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="System.Security.Permissions" Version="7.0.0" />
    <PackageReference Include="System.IO.Ports" Version="7.0.0" />
  </ItemGroup>

  <ItemGroup>
    <Reference Include="WSS_Core_Interface">
      <HintPath>..\lib\WSS_Core_Interface.dll</HintPath>
    </Reference>
    <Reference Include="Newtonsoft.Json">
      <HintPath>..\lib\Newtonsoft.Json.dll</HintPath>
    </Reference>
    <Reference Include="System.Buffers">
      <HintPath>..\lib\System.Buffers.dll</HintPath>
    </Reference>
    <Reference Include="System.Memory">
      <HintPath>..\lib\System.Memory.dll</HintPath>
    </Reference>
    <Reference Include="System.Numerics.Vectors">
      <HintPath>..\lib\System.Numerics.Vectors.dll</HintPath>
    </Reference>
    <Reference Include="System.Runtime.CompilerServices.Unsafe">
      <HintPath>..\lib\System.Runtime.CompilerServices.Unsafe.dll</HintPath>
    </Reference>
  </ItemGroup>
</Project>
```

Copy the listed DLLs from this repository’s `bin` output into your app’s `lib` folder (or use NuGet where possible) so the runtime can resolve them when your executable starts.

## Transport project layout
- `WSS_Core_Interface.csproj` remains the transport-agnostic core library targeting `netstandard2.0`.
- `WSS.Transport.Serial/WSS.Transport.Serial.csproj` targets `netstandard2.0` for the Core+Serial distribution.
- `WSS.Transport.BLE/WSS.Transport.BLE.csproj` is the new modern transport package targeting `net9.0`.

## Choosing a transport build
- Use the **Core+Serial distribution** for the serial-only build. It contains separate `WSS_Core_Interface.dll` and `WSS.Transport.Serial.dll` assemblies, both targeting `.NET Standard 2.0`.
- Unity 2022.3 Editor compilation and communication with a mock WSS stimulator over real Serial I/O have passed using these assemblies. Physical-stimulator testing and a built Windows standalone player remain unverified.
- The distribution stages `System.IO.Ports` 8.0.0's `lib/net462` implementation after checking its identity against Serial's compile asset and emitted reference. The `net8` consumer is an assembly/API compatibility check using its own NuGet runtime support, separate from the Unity validation above.
- The reproducible legacy `net48` source/build baseline is commit `97fd8ddfa115dc906ebe8a06afb0425227c874ea`. The modified branch no longer builds the legacy Serial assembly. Existing published releases remain unchanged.
- Use `WSS.Transport.BLE` when your app targets `.NET 9` and needs BLE support.
- `WSS.Transport.BLE` also includes the existing serial transport implementation, so modern desktop apps can choose either `BleNusTransport` or `SerialPortTransport` from the same package.
- Core+Serial and BLE unified are independent deployment choices with different assemblies and dependency selections. Do not blindly combine their ZIP contents in one DLL folder. BLE remains a `.NET 9` distribution; Unity BLE compatibility has not been established.

## Building the transport projects
```bash
dotnet build "WSS.Transport.Serial/WSS.Transport.Serial.csproj" -c Release --nologo
dotnet build "WSS.Transport.BLE/WSS.Transport.BLE.csproj" -c Release --nologo
```

The unified `.NET 9` BLE artifact includes the direct Windows WinRT and Linux BlueZ backends with their platform-local dependencies under `backends/windows` and `backends/linux`.

### Building all release artifacts

From the repository root, rebuild, stage, and validate all supported release artifacts with:

```bash
dotnet run --project tools/ArtifactBuild/ArtifactBuild.csproj -- build-all
```

The command produces the manifest-defined distributions in:

```text
artifacts/core-serial/
artifacts/ble-unified/
```

Rerunning the command safely replaces only these two generated artifact directories before staging and validating them again. CI uploads them as GitHub Actions workflow artifacts named `core-serial` and `ble-unified`; successful tagged releases package those same tested files as ZIP assets.

## CI, testing, and release workflow

GitHub Actions builds and validates the distributable WSS artifacts. Normal feature-branch pushes do not run the full workflow.

The workflow runs automatically when:

- A pull request targets `dev` or `main`.
- A Git tag beginning with `v` is pushed, such as `v0.3.0-rc.4` or `v0.3.0`.
- The workflow is manually started with `workflow_dispatch`.

A normal push or merge to `main` does not create a version tag and does not create a release. Git tags are created explicitly when a particular commit is ready to be tested as a release candidate or release.

### Pull request validation

Before merging changes into `main`, open a pull request targeting `main`.

The CI workflow builds, stages, validates, and uploads the Core+Serial and unchanged BLE distributions, then runs external consumer smoke tests against the downloaded artifact DLLs rather than against the WSS source projects.

The current consumer matrix is:

| Artifact | Windows | Linux | macOS |
| --- | --- | --- | --- |
| Core+Serial (Core and Serial consumers) | Yes | Yes | Yes |
| BLE unified | Yes | Yes | — |

A pull request should not be merged until its required artifact and consumer jobs pass.

### Creating a release candidate

After the desired commit has passed pull-request validation and is on the branch/commit that should be released, create a new prerelease tag.

For example:

```bash
git checkout main
git pull
git log -1 --oneline --decorate

git tag -a v0.3.0-rc.4 -m "v0.3.0-rc.4"
git push origin v0.3.0-rc.4
```

Always use a new release-candidate number for a new commit. Do not move or reuse a release-candidate tag that has already been pushed.

Pushing the `v*` tag automatically runs the artifact workflow against the exact tagged commit.

If Core conformance, artifact build/validation, and all Core, Serial, and BLE consumer jobs succeed, the release job publishes the already-tested artifacts from the same workflow run without rebuilding or restaging WSS. Only `v*` tag pushes can publish; pull requests and manual workflow runs cannot.

New releases provide one Core+Serial ZIP instead of separate Core and Serial downloads. Historical releases retain their existing `WSS-Core-<TAG>.zip` and `WSS-Serial-<TAG>.zip` assets and remain available unchanged.

Release assets are:

```text
WSS-Core-Serial-<TAG>.zip
WSS-BLE-Unified-<TAG>.zip
SHA256SUMS.txt
```

For example:

```text
WSS-Core-Serial-v0.3.0-rc.4.zip
WSS-BLE-Unified-v0.3.0-rc.4.zip
SHA256SUMS.txt
```

Tags containing a hyphen, such as:

```text
v0.3.0-rc.4
```

are published as GitHub prereleases.

A stable tag such as:

```text
v0.3.0
```

is published as a normal GitHub release.

### Release principle

The release pipeline follows this sequence:

```text
source/tagged commit
        ↓
clean build
        ↓
stage exact distributable files
        ↓
validate staged artifacts
        ↓
upload workflow artifacts
        ↓
consumer verification against downloaded workflow artifacts
        ↓
release job downloads the same tested artifacts
        ↓
ZIP + SHA-256 checksums
        ↓
GitHub Release
```

The release job must not rebuild or restage WSS. This ensures the files published in a GitHub Release are the same files that passed the artifact consumer tests.
