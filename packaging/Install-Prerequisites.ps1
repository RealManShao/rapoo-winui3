param(
    [string]$RequirementsPath = (Join-Path $PSScriptRoot 'requirements.json'),
    [string]$LogPath = (Join-Path $env:TEMP 'RapooControl-prerequisites.log'),
    [switch]$CheckOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$requirements = Get-Content -LiteralPath $RequirementsPath -Raw | ConvertFrom-Json

function Test-DotNet {
    $dotnet = Join-Path $env:ProgramW6432 'dotnet\dotnet.exe'
    if (!(Test-Path -LiteralPath $dotnet)) { return $false }
    $runtimes = & $dotnet --list-runtimes
    if ($LASTEXITCODE -ne 0) { return $false }
    return [bool]($runtimes | Where-Object { $_ -match '^Microsoft.NETCore.App 10\.0\.\d+ \[' })
}

function Test-VisualCpp {
    foreach ($key in @(
        'HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x64'
    )) {
        $runtime = Get-ItemProperty $key -ErrorAction SilentlyContinue
        if ($null -ne $runtime -and $runtime.Installed -eq 1 -and
            [version]($runtime.Version.TrimStart('v')) -ge [version]'14.44.35211.0') { return $true }
    }
    return $false
}

function Test-WindowsAppRuntime {
    # Probe with the same bootstrap API and minimum version as the app itself.
    # This also handles DDLM differences between Windows versions and newer runtimes.
    if (!('RapooInstaller.Bootstrap' -as [type])) {
        $dll = (Join-Path (Split-Path -Parent (Resolve-Path $RequirementsPath).Path) 'Microsoft.WindowsAppRuntime.Bootstrap.dll').Replace('"', '""')
        Add-Type -TypeDefinition @"
using System.Runtime.InteropServices;
namespace RapooInstaller {
    public static class Bootstrap {
        [DllImport(@"$dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int MddBootstrapInitialize2(uint majorMinor, string tag, ulong minVersion, uint options);
        [DllImport(@"$dll", ExactSpelling = true)]
        public static extern void MddBootstrapShutdown();
    }
}
"@
    }
    try {
        $result = [RapooInstaller.Bootstrap]::MddBootstrapInitialize2(
            [uint32]$requirements.ReleaseMajorMinor, '', [uint64]$requirements.RuntimeVersionUInt64, 0)
        if ($result -lt 0) { return $false }
        [RapooInstaller.Bootstrap]::MddBootstrapShutdown()
        return $true
    } catch { return $false }
}

function Install-MicrosoftRuntime {
    param([string]$Name, [string]$Url, [string]$Arguments, [bool]$Elevate)
    $file = Join-Path $script:downloadDirectory ($Name + '.exe')
    Write-Output "Downloading $Name from $Url"
    Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $file -TimeoutSec 600
    $signature = Get-AuthenticodeSignature -LiteralPath $file
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation(?:,|$)') {
        throw "Microsoft signature verification failed: $Name"
    }
    $start = @{ FilePath = $file; ArgumentList = $Arguments; Wait = $true; PassThru = $true; WindowStyle = 'Hidden' }
    if ($Elevate) { $start.Verb = 'RunAs' }
    $process = Start-Process @start
    Write-Output "$Name exit code: $($process.ExitCode)"
    if ($process.ExitCode -eq 3010) { $script:restartNeeded = $true }
    elseif ($process.ExitCode -ne 0) { throw "$Name installation failed (exit $($process.ExitCode))." }
}

if ($CheckOnly) {
    [pscustomobject]@{ DotNet = Test-DotNet; VisualCpp = Test-VisualCpp; WindowsAppRuntime = Test-WindowsAppRuntime } |
        ConvertTo-Json
    exit 0
}

$restartNeeded = $false
$downloadDirectory = Join-Path $env:TEMP ('RapooControl-dependencies-' + [guid]::NewGuid().ToString('N'))
$transcribing = $false
try {
    Start-Transcript -LiteralPath $LogPath -Force | Out-Null
    $transcribing = $true
    if (![Environment]::Is64BitProcess) { throw 'The prerequisite helper requires x64 Windows PowerShell.' }
    New-Item -ItemType Directory -Path $downloadDirectory | Out-Null
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

    if (!(Test-VisualCpp)) {
        Install-MicrosoftRuntime 'VC-redist-x64' 'https://aka.ms/vs/17/release/vc_redist.x64.exe' '/install /quiet /norestart' $true
        if (!$restartNeeded -and !(Test-VisualCpp)) { throw 'Visual C++ runtime verification failed.' }
    }
    if (!(Test-DotNet)) {
        $metadata = Invoke-RestMethod 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json' -TimeoutSec 60
        $release = $metadata.releases | Where-Object { $_.runtime.version -eq $metadata.'latest-runtime' } | Select-Object -First 1
        $asset = $release.runtime.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -like '*.exe' } | Select-Object -First 1
        if (!$asset -or $asset.url -notmatch '^https://(builds\.dotnet\.microsoft\.com|download\.visualstudio\.microsoft\.com)/') {
            throw 'No official .NET 10 x64 runtime installer was found.'
        }
        Install-MicrosoftRuntime 'dotnet-runtime-10-x64' $asset.url '/install /quiet /norestart' $true
        if (!$restartNeeded -and !(Test-DotNet)) { throw '.NET runtime verification failed.' }
    }
    if (!(Test-WindowsAppRuntime)) {
        # Run without elevation so MSIX registrations belong to the installing user.
        Install-MicrosoftRuntime 'WindowsAppRuntime-x64' $requirements.RuntimeUrl '--quiet' $false
        if (!$restartNeeded -and !(Test-WindowsAppRuntime)) { throw 'Windows App Runtime verification failed.' }
    }
    if ($restartNeeded) { exit 3010 }
    exit 0
} catch {
    Write-Output ($_ | Out-String)
    exit 1
} finally {
    if ($transcribing) { Stop-Transcript | Out-Null }
    # Resolve and bound the deletion to the uniquely named directory under TEMP.
    $cleanupPath = [IO.Path]::GetFullPath($downloadDirectory)
    $tempPath = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\')
    if ([IO.Directory]::GetParent($cleanupPath).FullName -eq $tempPath -and
        [IO.Path]::GetFileName($cleanupPath) -match '^RapooControl-dependencies-[0-9a-f]{32}$' -and
        (Test-Path -LiteralPath $cleanupPath)) {
        Remove-Item -LiteralPath $cleanupPath -Recurse -Force
    }
}
