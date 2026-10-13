param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateSet('x64')][string]$Architecture = 'x64',
    [string]$WindowsSdkVersion = '10.0.22621.0',
    [switch]$ProductionOnly
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$repo = [System.IO.Path]::GetFullPath((Join-Path $root '..\..'))
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
function Invoke-Checked([string]$tool, [string[]]$arguments) {
    & $tool @arguments
    if ($LASTEXITCODE -ne 0) { throw "$tool failed with exit code $LASTEXITCODE" }
}
if (!(Test-Path -LiteralPath $root)) { throw 'Bridge source directory is missing.' }
# Managed projects can also be built directly without the native toolchain.
if (!(Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio Installer/vswhere is missing. Install VS 2022 Build Tools with Desktop C++ and a Windows SDK.' }
$installation = & $vswhere -latest -version '[17.0,18.0)' -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$installation) { throw 'MSVC v143 C++ build tools are not installed. Install Desktop development with C++ and Windows SDK; no native DLL was built.' }
$msbuild = Join-Path $installation 'MSBuild\Current\Bin\MSBuild.exe'
if (!(Test-Path -LiteralPath $msbuild)) { throw "MSBuild was not found at $msbuild" }
$sdkPath = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\Include\$WindowsSdkVersion"
if (!(Test-Path -LiteralPath $sdkPath)) { throw "Windows SDK $WindowsSdkVersion is missing. Install it or pass -WindowsSdkVersion with an installed SDK (minimum 10.0.19041.0)." }
if ([version]$WindowsSdkVersion -lt [version]'10.0.19041.0') { throw 'Windows SDK 10.0.19041.0 or newer is required.' }
$properties = @("/p:Configuration=$Configuration", "/p:Platform=$Architecture", "/p:WindowsTargetPlatformVersion=$WindowsSdkVersion")
Invoke-Checked $msbuild (@((Join-Path $root 'WindowsBleBridge.sln'), '/t:Restore', '/p:RestorePackagesConfig=true') + $properties)
Invoke-Checked $msbuild (@((Join-Path $root 'native\WssBleWindows.vcxproj'), '/t:Build', '/m') + $properties)
if ($ProductionOnly) { return }
Invoke-Checked $msbuild (@((Join-Path $repo 'tests\BLE\Windows\NativeTestShim\WssBleWindows.TestShim.vcxproj'), '/t:Build', '/m') + $properties)
# Rebuild only the two consumers to stage their DISTINCT native libraries.
Invoke-Checked 'dotnet' @('build', (Join-Path $repo 'tests\BLE\Windows\ScanConsumer\ScanConsumer.csproj'), '-c', $Configuration)
Invoke-Checked 'dotnet' @('build', (Join-Path $repo 'tests\BLE\Windows\WSS.BLE.Windows.Tests\WSS.BLE.Windows.Tests.csproj'), '-c', $Configuration)
'Bridge and isolated test shim built. Run tests and the production scan consumer separately.'
