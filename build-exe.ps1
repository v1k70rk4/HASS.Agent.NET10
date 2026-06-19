#Requires -Version 7.0
<#
.SYNOPSIS
    Builds a standalone, single-file HASS.Agent .NET10 executable.

.DESCRIPTION
    Publishes a self-contained win-x64 single-file .exe — no .NET runtime is
    needed on the target machine. The tray icon is embedded, so the .exe runs
    on its own without any extra files next to it.

.PARAMETER Output
    Output directory for the published .exe. Default: artifacts\standalone

.PARAMETER Open
    Opens Explorer with the produced .exe selected when finished.

.EXAMPLE
    .\build-exe.ps1
    .\build-exe.ps1 -Open
    .\build-exe.ps1 -Output C:\temp\hassagent
#>
[CmdletBinding()]
param(
    [string]$Output = "artifacts\standalone",
    [switch]$Open
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$project = "src\HASS.Agent.NET10\HASS.Agent.NET10.csproj"

if (-not (Test-Path $project)) {
    Write-Error "Project not found: $project  (run this script from inside the repo)."
    exit 1
}

# Read the version from the csproj just for a friendly message.
[xml]$proj = Get-Content $project
$version = ($proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)

Write-Host "Building HASS.Agent .NET10 $version  (standalone, self-contained, win-x64)..." -ForegroundColor Cyan

dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $Output

if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed (dotnet publish exit code $LASTEXITCODE)."
    exit $LASTEXITCODE
}

$exe = Join-Path $Output "HASS.Agent.NET10.exe"
if (-not (Test-Path $exe)) {
    Write-Error "Build finished but the .exe was not found at $exe."
    exit 1
}

$fullPath = (Resolve-Path $exe).Path
$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)

Write-Host ""
Write-Host "Done ✓" -ForegroundColor Green
Write-Host "  $fullPath  ($sizeMb MB)" -ForegroundColor Green

if ($Open) {
    Start-Process explorer.exe "/select,`"$fullPath`""
}
