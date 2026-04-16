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
- `WSS.Transport.Serial/WSS.Transport.Serial.csproj` remains the legacy serial transport package targeting `net48`.
- `WSS.Transport.BLE/WSS.Transport.BLE.csproj` is the new modern transport package targeting `net9.0`.

## Choosing a transport build
- Use `WSS.Transport.Serial` when you need the legacy `net48`/Unity-compatible serial-only build.
- Use `WSS.Transport.BLE` when your app targets `.NET 9` and needs BLE support.
- `WSS.Transport.BLE` also includes the existing serial transport implementation, so modern desktop apps can choose either `BleNusTransport` or `SerialPortTransport` from the same package.

## Building the transport projects
```bash
dotnet build "WSS.Transport.Serial/WSS.Transport.Serial.csproj" -c Release --nologo
dotnet build "WSS.Transport.BLE/WSS.Transport.BLE.csproj" -c Release --nologo
```

The `.NET 9` transport project restores BLE-specific dependencies such as `InTheHand.BluetoothLE` and `Linux.Bluetooth`. If you deploy the built DLLs directly instead of consuming them through NuGet, copy the resolved dependency assemblies alongside `WSS.Transport.BLE.dll`.
