[CmdletBinding()]
param(
    [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root "artifacts\publish\win-x64" }
$env:DOTNET_CLI_HOME = Join-Path $root ".dotnet-home"
$env:NUGET_PACKAGES = Join-Path $root ".nuget\packages"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = "true"
$env:MSBuildEnableWorkloadResolver = "false"

& dotnet restore (Join-Path $root "apps\desktop\Pkws.Desktop\Pkws.Desktop.csproj") `
    -r win-x64 --disable-build-servers -m:1 -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& dotnet restore (Join-Path $root "apps\agent\Pkws.Agent.Cli\Pkws.Agent.Cli.csproj") `
    -r win-x64 --disable-build-servers -m:1 -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& dotnet publish (Join-Path $root "apps\desktop\Pkws.Desktop\Pkws.Desktop.csproj") `
    -c Release -r win-x64 --self-contained false --no-restore `
    --disable-build-servers -m:1 -o ([IO.Path]::GetFullPath($OutputDirectory))
$publishExitCode = $LASTEXITCODE
if ($publishExitCode -eq 0) {
    New-Item -ItemType Directory -Force -Path (Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) "doc") | Out-Null
    & dotnet publish (Join-Path $root "apps\agent\Pkws.Agent.Cli\Pkws.Agent.Cli.csproj") `
        -c Release -r win-x64 --self-contained false --no-restore `
        --disable-build-servers -m:1 -p:NuGetAudit=false `
        -o (Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) "agent")
    $publishExitCode = $LASTEXITCODE
}
exit $publishExitCode
