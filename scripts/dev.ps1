[CmdletBinding()]
param(
    [string]$DataDirectory = "",
    [switch]$Show
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $root ".dotnet-home"
$env:NUGET_PACKAGES = Join-Path $root ".nuget\packages"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = "true"
$env:MSBuildEnableWorkloadResolver = "false"
if ($DataDirectory) { $env:PKWS_DATA_DIR = [IO.Path]::GetFullPath($DataDirectory) }

$arguments = @("run", "--project", (Join-Path $root "apps\desktop\Pkws.Desktop\Pkws.Desktop.csproj"), "--")
if ($Show) { $arguments += "--show" }
& dotnet @arguments
exit $LASTEXITCODE
