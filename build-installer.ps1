param(
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')][string]$Version = '1.0.0',
    [string]$InnoCompilerPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (!$InnoCompilerPath) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $InnoCompilerPath = $command.Source }
    else {
        $candidates = @(
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
            "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
            (Join-Path $PSScriptRoot '.build-home\tools\Inno Setup 6\ISCC.exe')
        )
        $InnoCompilerPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
}
if (!$InnoCompilerPath -or !(Test-Path -LiteralPath $InnoCompilerPath)) {
    throw 'Install Inno Setup 6.3 or later from https://jrsoftware.org/isdl.php, or pass -InnoCompilerPath to ISCC.exe.'
}

# Use a fresh staging directory so a previous self-contained build cannot leak runtimes into the installer.
$staging = Join-Path $PSScriptRoot ('artifacts\installer-staging\' + [guid]::NewGuid().ToString('N'))
$project = Join-Path $PSScriptRoot 'src\Rapoo.App\Rapoo.App.csproj'
dotnet publish $project -c Release -r win-x64 -p:Platform=x64 --self-contained false `
    -p:WindowsAppSDKSelfContained=false -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false `
    "-p:Version=$Version" -p:BuildInParallel=false -m:1 -o $staging
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

$assets = Get-Content (Join-Path $PSScriptRoot 'src\Rapoo.App\obj\project.assets.json') -Raw | ConvertFrom-Json
$runtimeKey = $assets.libraries.PSObject.Properties.Name | Where-Object { $_ -like 'Microsoft.WindowsAppSDK.Runtime/*' } | Select-Object -First 1
if (!$runtimeKey) { throw 'Cannot determine the Windows App SDK runtime version.' }
$packageRoot = ($assets.packageFolders.PSObject.Properties.Name | Select-Object -First 1)
$library = $assets.libraries.PSObject.Properties[$runtimeKey].Value
$versionHeader = Get-Content (Join-Path $packageRoot ($library.path + '/include/WindowsAppSDK-VersionInfo.cs')) -Raw
$sdkVersion = $runtimeKey.Split('/')[1]
if ($sdkVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Only stable Windows App SDK versions are supported by this installer.' }
$sdkParts = $sdkVersion.Split('.')
$releaseMajorMinor = [Convert]::ToUInt32([regex]::Match($versionHeader, 'MajorMinor = 0x([0-9a-fA-F]+)').Groups[1].Value, 16)
$runtimeUInt64 = [Convert]::ToUInt64([regex]::Match($versionHeader, 'UInt64 = 0x([0-9a-fA-F]+)').Groups[1].Value, 16)
$runtimeVersion = [regex]::Match($versionHeader, 'DotQuadString = "([^"]+)"').Groups[1].Value
$frameworkFamily = [regex]::Match($versionHeader, 'class Framework\s*\{[\s\S]*?PackageFamilyName = "([^"]+)"').Groups[1].Value
$mainFamily = [regex]::Match($versionHeader, 'class Main\s*\{[\s\S]*?PackageFamilyName = "([^"]+)"').Groups[1].Value
if (!$runtimeVersion -or !$frameworkFamily -or !$mainFamily) { throw 'Cannot read runtime package identities from the SDK.' }
@{
    RuntimeMinVersion = $runtimeVersion
    ReleaseMajorMinor = $releaseMajorMinor
    RuntimeVersionUInt64 = $runtimeUInt64
    FrameworkFamily = $frameworkFamily
    FrameworkName = $frameworkFamily.Split('_')[0]
    MainFamily = $mainFamily
    MainName = $mainFamily.Split('_')[0]
    RuntimeUrl = "https://aka.ms/windowsappsdk/$($sdkParts[0]).$($sdkParts[1])/$sdkVersion/windowsappruntimeinstall-x64.exe"
} | ConvertTo-Json | Set-Content (Join-Path $staging 'requirements.json') -Encoding UTF8

foreach ($required in @('Rapoo.App.exe', 'Rapoo.App.pri', 'Rapoo.App.runtimeconfig.json', 'Microsoft.WindowsAppRuntime.Bootstrap.dll')) {
    if (!(Test-Path (Join-Path $staging $required))) { throw "Publish output is missing $required." }
}
foreach ($forbidden in @('coreclr.dll', 'hostfxr.dll', 'Microsoft.ui.xaml.dll')) {
    if (Test-Path (Join-Path $staging $forbidden)) { throw "Unexpected bundled runtime: $forbidden" }
}

& $InnoCompilerPath "/DPublishDir=$staging" "/DAppVersion=$Version" (Join-Path $PSScriptRoot 'packaging\RapooControl.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed ($LASTEXITCODE)." }
$output = Join-Path $PSScriptRoot "artifacts\installer\RapooControl-$Version-Setup-x64.exe"
Write-Host "Installer: $output"
Write-Host ('Size: {0:N2} MiB' -f ((Get-Item -LiteralPath $output).Length / 1MB))
