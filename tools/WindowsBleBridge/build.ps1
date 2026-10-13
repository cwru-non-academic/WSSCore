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
$sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
function Assert-BuildFile([string]$path) {
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required build file is missing: $path" }
}
try {
    if (!(Test-Path -LiteralPath $vswhere -PathType Leaf)) { throw 'Visual Studio Installer/vswhere is missing. Install VS 2022 Build Tools with Desktop C++ and a Windows SDK.' }
    $discovery = & $vswhere -latest -version '[17.0,18.0)' -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -format json
    if ($LASTEXITCODE -ne 0) { throw "vswhere discovery failed with exit code $LASTEXITCODE." }
    $instances = ($discovery -join "`n") | ConvertFrom-Json
    $instances = @($instances)
    if (!$instances.Count) { throw 'No VS 2022 installation matched [17.0,18.0) with Microsoft.VisualStudio.Component.VC.Tools.x86.x64; no native DLL was built.' }
    $selected = $instances[0]
    $installation = ([string]$selected.installationPath).Trim()
    if (!$installation -or ![System.IO.Path]::IsPathRooted($installation)) { throw 'vswhere returned an invalid installation path.' }
    $installation = [System.IO.Path]::GetFullPath($installation)
    if (!(Test-Path -LiteralPath $installation -PathType Container)) { throw "Visual Studio installation directory is missing: $installation" }
    $msbuild = Join-Path $installation 'MSBuild\Current\Bin\MSBuild.exe'
    Assert-BuildFile $msbuild
    $versionFile = Join-Path $installation 'VC\Auxiliary\Build\Microsoft.VCToolsVersion.default.txt'
    Assert-BuildFile $versionFile
    $msvcVersion = (Get-Content -LiteralPath $versionFile -Raw).Trim()
    if ($msvcVersion -notmatch '^14\.\d+\.\d+(\.\d+)?$') { throw "Invalid MSVC version in ${versionFile}: $msvcVersion" }
    $msvcPath = Join-Path $installation "VC\Tools\MSVC\$msvcVersion"
    foreach ($tool in @('cl.exe', 'link.exe')) {
        Assert-BuildFile (Join-Path $msvcPath "bin\Hostx64\x64\$tool")
    }
    $targetsPath = Join-Path $installation 'MSBuild\Microsoft\VC\v170'
    foreach ($target in @('Microsoft.Cpp.Default.props', 'Microsoft.Cpp.props', 'Microsoft.Cpp.targets', 'Platforms\x64\PlatformToolsets\v143\Toolset.props')) {
        Assert-BuildFile (Join-Path $targetsPath $target)
    }
    if ([version]$WindowsSdkVersion -lt [version]'10.0.19041.0') { throw 'Windows SDK 10.0.19041.0 or newer is required.' }
    foreach ($header in @('um\Windows.h', 'shared\sdkddkver.h', 'winrt\windows.foundation.h', 'ucrt\stdio.h')) {
        Assert-BuildFile (Join-Path $sdkRoot "Include\$WindowsSdkVersion\$header")
    }
    foreach ($library in @('um\x64\windowsapp.lib', 'um\x64\runtimeobject.lib', 'ucrt\x64\ucrt.lib')) {
        Assert-BuildFile (Join-Path $sdkRoot "Lib\$WindowsSdkVersion\$library")
    }
    Write-Host "Visual Studio $($selected.installationVersion): $installation"
    Write-Host "MSVC $msvcVersion (x64): $msvcPath"
    Write-Host "MSBuild $((Get-Item -LiteralPath $msbuild).VersionInfo.FileVersion): $msbuild"
    Write-Host "Windows SDK ${WindowsSdkVersion}: $sdkRoot"
} catch {
    # Diagnostics are best-effort and must not replace the original preflight error.
    try {
        Write-Host "Toolchain discovery failed. Runner image: $env:ImageOS / $env:ImageVersion"
        if (Test-Path -LiteralPath $vswhere -PathType Leaf) {
            Write-Host 'Available Visual Studio installations (all versions/products):'
            & $vswhere -all -products '*' -format json | Out-Host
            Write-Host "vswhere inventory exit code: $LASTEXITCODE"
            Write-Host 'Installations with Microsoft.VisualStudio.Component.VC.Tools.x86.x64 (all versions):'
            & $vswhere -all -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -format json | Out-Host
            Write-Host "vswhere component inventory exit code: $LASTEXITCODE"
        }
        foreach ($part in @('Include', 'Lib')) {
            $path = Join-Path $sdkRoot $part
            Write-Host "Available Windows SDK versions under ${path}:"
            if (Test-Path -LiteralPath $path -PathType Container) {
                Get-ChildItem -LiteralPath $path -Directory | ForEach-Object { Write-Host $_.Name }
            } else { Write-Host '(directory missing)' }
        }
    } catch { Write-Host "Could not complete toolchain diagnostics: $($_.Exception.Message)" }
    throw
}
$properties = @("/p:Configuration=$Configuration", "/p:Platform=$Architecture", "/p:WindowsTargetPlatformVersion=$WindowsSdkVersion")
Invoke-Checked $msbuild (@((Join-Path $root 'WindowsBleBridge.sln'), '/t:Restore', '/p:RestorePackagesConfig=true') + $properties)
Invoke-Checked $msbuild (@((Join-Path $root 'native\WssBleWindows.vcxproj'), '/t:Build', '/m') + $properties)
if ($ProductionOnly) { return }
Invoke-Checked $msbuild (@((Join-Path $repo 'tests\BLE\Windows\NativeTestShim\WssBleWindows.TestShim.vcxproj'), '/t:Build', '/m') + $properties)
# Rebuild only the two consumers to stage their DISTINCT native libraries.
Invoke-Checked 'dotnet' @('build', (Join-Path $repo 'tests\BLE\Windows\ScanConsumer\ScanConsumer.csproj'), '-c', $Configuration)
Invoke-Checked 'dotnet' @('build', (Join-Path $repo 'tests\BLE\Windows\WSS.BLE.Windows.Tests\WSS.BLE.Windows.Tests.csproj'), '-c', $Configuration)
'Bridge and isolated test shim built. Run tests and the production scan consumer separately.'
