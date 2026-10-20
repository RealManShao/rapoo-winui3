param([switch]$Publish, [switch]$SelfContained)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\Rapoo.App\Rapoo.App.csproj'
if ($Publish) {
    $includeRuntime = $SelfContained.IsPresent.ToString().ToLowerInvariant()
    dotnet publish $project -c Release -p:Platform=x64 "-p:SelfContained=$includeRuntime" -p:BuildInParallel=false -m:1 -o (Join-Path $PSScriptRoot 'artifacts\RapooControl')
} else {
    dotnet build $project -c Debug -p:Platform=x64 -p:BuildInParallel=false -m:1
}
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
