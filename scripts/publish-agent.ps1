[CmdletBinding()]
param(
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root "artifacts\publish\win-x64\agent" }
$env:DOTNET_CLI_HOME = Join-Path $root ".dotnet-home"
$env:NUGET_PACKAGES = Join-Path $root ".nuget\packages"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = "true"
$env:MSBuildEnableWorkloadResolver = "false"
$project = Join-Path $root "apps\agent\Pkws.Agent.Cli\Pkws.Agent.Cli.csproj"

& dotnet restore $project -r win-x64 --disable-build-servers -m:1 -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& dotnet publish $project -c Release -r win-x64 --self-contained false --no-restore `
    --disable-build-servers -m:1 -p:NuGetAudit=false -o ([IO.Path]::GetFullPath($OutputDirectory))
exit $LASTEXITCODE
