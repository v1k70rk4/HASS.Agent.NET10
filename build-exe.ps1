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

.PARAMETER Deploy
    Replace the installed copy without asking. Needs an elevated shell.

.PARAMETER NoDeploy
    Build only; never ask about replacing the installed copy.

.EXAMPLE
    .\build-exe.ps1
    .\build-exe.ps1 -Open
    .\build-exe.ps1 -Deploy
    .\build-exe.ps1 -Output C:\temp\hassagent -NoDeploy
#>
[CmdletBinding()]
param(
    [string]$Output = "artifacts\standalone",
    [switch]$Open,
    [switch]$Deploy,
    [switch]$NoDeploy
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

# ---------------------------------------------------------------------------
# Optionally replace the installed copy, so a build can be tested for real
# without going through the installer.
# ---------------------------------------------------------------------------

$installDir = Join-Path $env:ProgramFiles "HASS.Agent .NET10"
$installedExe = Join-Path $installDir "HASS.Agent.NET10.exe"
$serviceName = "HASS.Agent.NET10.Service"
$processName = "HASS.Agent.NET10"

if ($NoDeploy) { return }

if (-not (Test-Path $installedExe)) {
    Write-Host ""
    Write-Host "No installed copy found at $installDir - nothing to replace." -ForegroundColor DarkGray
    return
}

Write-Host ""
Write-Host "Installed copy found: $installedExe" -ForegroundColor Cyan

if (-not $Deploy) {
    $answer = Read-Host "Replace it with this build? [y/N]"
    if ($answer -notmatch '^(y|yes|i|igen)$') {
        Write-Host "Left the installed copy untouched." -ForegroundColor DarkGray
        return
    }
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Error "Replacing the installed copy needs an elevated shell (Program Files and the service). Re-run as administrator."
    exit 1
}

function Invoke-Agent([string]$Exe, [string]$Arguments) {
    # The agent's own switches do the service work, exactly like the installer does.
    $p = Start-Process -FilePath $Exe -ArgumentList $Arguments -Wait -PassThru -WindowStyle Hidden
    return $p.ExitCode
}

# Only a *running* service is taken down and put back: one that is installed but
# deliberately stopped is left exactly as it is.
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
$serviceWasRunning = ($null -ne $service) -and ($service.Status -eq 'Running')

if ($serviceWasRunning) {
    Write-Host "  Service is running - stopping and uninstalling..." -ForegroundColor Yellow
    [void](Invoke-Agent $installedExe "--stop-service --quiet")
    [void](Invoke-Agent $installedExe "--uninstall-service --quiet")
    Start-Sleep -Seconds 2
}
elseif ($null -ne $service) {
    Write-Host "  Service is installed but not running - leaving it alone." -ForegroundColor DarkGray
}
else {
    Write-Host "  Service is not installed." -ForegroundColor DarkGray
}

$trayWasRunning = $null -ne (Get-Process -Name $processName -ErrorAction SilentlyContinue)
if ($trayWasRunning) {
    Write-Host "  Tray app is running - closing it..." -ForegroundColor Yellow
    [void](Invoke-Agent $installedExe "--exit --quiet")
    Start-Sleep -Seconds 2
    if (Get-Process -Name $processName -ErrorAction SilentlyContinue) {
        Write-Host "  Still running - forcing it closed." -ForegroundColor Yellow
        Stop-Process -Name $processName -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 1
    }
}

Write-Host "  Copying the new build in..." -ForegroundColor Yellow
try {
    Copy-Item -LiteralPath $fullPath -Destination $installedExe -Force
}
catch {
    Write-Error "Could not replace $installedExe : $($_.Exception.Message)"
    exit 1
}

if ($serviceWasRunning) {
    Write-Host "  Reinstalling and starting the service..." -ForegroundColor Yellow
    [void](Invoke-Agent $installedExe "--install-service --quiet")
    Start-Sleep -Seconds 2
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -eq $service) {
        Write-Warning "The service did not come back - install it from the app's Service page."
    }
    else {
        if ($service.Status -ne 'Running') {
            [void](Invoke-Agent $installedExe "--start-service --quiet")
            Start-Sleep -Seconds 1
            $service.Refresh()
        }
        Write-Host "  Service status: $($service.Status)" -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "Installed copy replaced" -ForegroundColor Green
if ($trayWasRunning) {
    # Not restarted on purpose: this shell is elevated, and the tray app started
    # from here would inherit that and run as administrator.
    Write-Host "  The tray app was closed - start it yourself so it runs unelevated:" -ForegroundColor Yellow
    Write-Host "    $installedExe" -ForegroundColor Yellow
}
