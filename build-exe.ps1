#Requires -Version 7.0
<#
.SYNOPSIS
    Builds HASS.Agent .NET10 — a standalone .exe for testing, or the signed
    release assets (installer + zip).

.DESCRIPTION
    Default: publishes a self-contained win-x64 single-file .exe — no .NET
    runtime is needed on the target machine. The tray icon is embedded, so the
    .exe runs on its own without any extra files next to it.

    -Sign signs the built .exe with the Certum SimplySign cloud certificate
    through `ssign` (https://github.com/Le-Syl21/ssign): you type the current
    code from the SimplySign mobile app, nothing secret is stored anywhere.

    -Release builds exactly what CI publishes — the Inno Setup installer and
    the win-x64 zip, same file names — but signed (implies -Sign): the .exe,
    the installer and its uninstaller all carry the signature. Every signed
    file is checked with Microsoft's signtool before the script reports success.

.PARAMETER Output
    Output directory for the standalone .exe. Default: artifacts\standalone
    (ignored with -Release, which uses the CI layout under artifacts\).

.PARAMETER Open
    Opens Explorer with the produced .exe selected when finished.

.PARAMETER Deploy
    Replace the installed copy without asking. Needs an elevated shell.

.PARAMETER NoDeploy
    Build only; never ask about replacing the installed copy.

.PARAMETER Sign
    Sign the .exe with the Certum certificate (asks for the SimplySign code).

.PARAMETER Release
    Build the signed installer and zip (implies -Sign).

.PARAMETER Upload
    With -Release: replace the assets on the GitHub release for this version
    without asking. Without it the script asks when such a release exists.

.PARAMETER SsignPath
    Path to ssign.exe. Default: $env:SSIGN_EXE, else C:\Claude\ssign\ssign.exe

.PARAMETER CertumEmail
    Certum account e-mail. Default: $env:CERTUM_EMAIL, else asked.

.EXAMPLE
    .\build-exe.ps1
    .\build-exe.ps1 -Sign -Deploy
    .\build-exe.ps1 -Release
    .\build-exe.ps1 -Release -Upload -NoDeploy
#>
[CmdletBinding()]
param(
    [string]$Output = "artifacts\standalone",
    [switch]$Open,
    [switch]$Deploy,
    [switch]$NoDeploy,
    [switch]$Sign,
    [switch]$Release,
    [switch]$Upload,
    [string]$SsignPath = $(if ($env:SSIGN_EXE) { $env:SSIGN_EXE } else { "C:\Claude\ssign\ssign.exe" }),
    [string]$CertumEmail = $env:CERTUM_EMAIL
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if ($Release) { $Sign = $true }

$project = "src\HASS.Agent.NET10\HASS.Agent.NET10.csproj"
$appName = "HASS.Agent .NET10"
$repoUrl = "https://github.com/v1k70rk4/HASS.Agent.NET10"

if (-not (Test-Path $project)) {
    Write-Error "Project not found: $project  (run this script from inside the repo)."
    exit 1
}

# Read the version from the csproj: for the message, and for the release file names.
[xml]$proj = Get-Content $project
$version = ($proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)

# ---------------------------------------------------------------------------
# Signing helpers
# ---------------------------------------------------------------------------

function Find-SignTool {
    $kits = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (-not (Test-Path $kits)) { return $null }
    Get-ChildItem -Path $kits -Directory |
        Sort-Object { [version]($_.Name -replace '[^\d.]', '') } -Descending |
        ForEach-Object { Join-Path $_.FullName "x64\signtool.exe" } |
        Where-Object { Test-Path $_ } |
        Select-Object -First 1
}

function Assert-Signed([string]$SignTool, [string]$File) {
    # Microsoft's own verifier is the judge, not the signing tool: full chain,
    # timestamp, and the Authenticode policy Windows itself applies.
    & $SignTool verify /pa /q $File 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        & $SignTool verify /pa /v $File
        Write-Error "signtool could not verify the signature on $File"
        exit 1
    }
    Write-Host "  Verified: $File" -ForegroundColor Green
}

function Invoke-Ssign([string]$File) {
    # -n/-u put the product name and URL into the signature block (shown in the
    # file's Digital Signatures tab and in the UAC prompt).
    & $SsignPath -n $appName -u $repoUrl $File
    if ($LASTEXITCODE -ne 0) {
        Write-Error "ssign failed on $File (exit code $LASTEXITCODE). A wrong or expired SimplySign code is the usual cause - run the script again with a fresh one."
        exit 1
    }
}

$signTool = $null
if ($Sign) {
    if (-not (Test-Path $SsignPath)) {
        Write-Error "ssign.exe not found at $SsignPath - download it from https://github.com/Le-Syl21/ssign/releases or pass -SsignPath / set SSIGN_EXE."
        exit 1
    }
    $signTool = Find-SignTool
    if (-not $signTool) {
        Write-Error "signtool.exe not found under the Windows Kits - install the Windows SDK (Signing Tools) to verify signatures."
        exit 1
    }
    if ([string]::IsNullOrWhiteSpace($CertumEmail)) {
        $CertumEmail = Read-Host "Certum account e-mail"
    }
    # ssign reads the credentials from the environment, so nothing lands in the
    # command line or the shell history. The code is single-use and expires in
    # 30 seconds; ssign caches the cloud session for ~20 minutes, so the
    # installer and uninstaller signed later by Inno Setup reuse this login.
    $env:CERTUM_EMAIL = $CertumEmail
    $env:CERTUM_TOKEN = Read-Host "SimplySign code (6 digits, from the mobile app)" -MaskInput
    if ($env:CERTUM_TOKEN -notmatch '^\d{6}$') {
        Remove-Item Env:CERTUM_TOKEN
        Write-Error "That is not a 6-digit code."
        exit 1
    }
}

try {

# ---------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------

$publishArgs = @(
    "publish", $project,
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishSingleFile=true"
)

if ($Release) {
    # Same layout and flags as the CI workflow, so the installer script's
    # Source path and the zip contents match what a tag build would produce.
    $Output = "artifacts\HASS.Agent.NET10\win-x64"
    Write-Host "Building $appName $version  (release layout, win-x64)..." -ForegroundColor Cyan
}
else {
    $publishArgs += "-p:IncludeNativeLibrariesForSelfExtract=true"
    Write-Host "Building $appName $version  (standalone, self-contained, win-x64)..." -ForegroundColor Cyan
}

dotnet @publishArgs -o $Output

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
Write-Host "Built ✓" -ForegroundColor Green
Write-Host "  $fullPath  ($sizeMb MB)" -ForegroundColor Green

# ---------------------------------------------------------------------------
# Sign the executable
# ---------------------------------------------------------------------------

if ($Sign) {
    Write-Host ""
    Write-Host "Signing the executable..." -ForegroundColor Cyan
    Invoke-Ssign $fullPath
    Assert-Signed $signTool $fullPath
}

# ---------------------------------------------------------------------------
# Release assets: signed installer (+ uninstaller) and the zip, CI file names
# ---------------------------------------------------------------------------

$releaseFiles = @()
if ($Release) {
    $iscc = @(
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe")
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) {
        Write-Error "Inno Setup 6 compiler (ISCC.exe) not found."
        exit 1
    }

    Write-Host ""
    Write-Host "Building the installer..." -ForegroundColor Cyan
    # /DSignSetup turns on the SignTool directive in the .iss; /Sssign=... is the
    # command Inno runs for Setup.exe and the uninstaller. $f arrives quoted, $q
    # is a literal quote - single-quoted here so PowerShell leaves them alone.
    $signCommand = '/Sssign=$q' + $SsignPath + '$q -n $q' + $appName + '$q -u $q' + $repoUrl + '$q $f'
    & $iscc "installer\HASS.Agent.NET10.iss" "/DMyAppVersion=$version" "/DSignSetup" $signCommand
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Inno Setup failed (exit code $LASTEXITCODE)."
        exit 1
    }

    $installer = (Resolve-Path "artifacts\installer\HASS.Agent.NET10-Setup-$version.exe").Path
    Assert-Signed $signTool $installer

    Write-Host ""
    Write-Host "Packaging the zip..." -ForegroundColor Cyan
    $packageDir = "artifacts\package"
    New-Item -ItemType Directory -Force -Path $packageDir | Out-Null
    $zip = Join-Path $packageDir "HASS.Agent.NET10-win-x64-$version.zip"
    Compress-Archive -Path "$Output\*" -DestinationPath $zip -Force
    $zip = (Resolve-Path $zip).Path

    $releaseFiles = @($installer, $zip)
    Write-Host ""
    Write-Host "Release assets ✓" -ForegroundColor Green
    foreach ($file in $releaseFiles) {
        Write-Host "  $file  ($([math]::Round((Get-Item $file).Length / 1MB, 1)) MB)" -ForegroundColor Green
    }
}

}
finally {
    if ($Sign) { Remove-Item Env:CERTUM_TOKEN -ErrorAction SilentlyContinue }
}

if ($Open) {
    Start-Process explorer.exe "/select,`"$fullPath`""
}

# ---------------------------------------------------------------------------
# Optionally replace the assets on the GitHub release for this version
# ---------------------------------------------------------------------------

if ($Release -and (Get-Command gh -ErrorAction SilentlyContinue)) {
    $tag = "v$version"
    gh release view $tag --json tagName 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Host ""
        Write-Host "GitHub release $tag exists." -ForegroundColor Cyan
        $doUpload = $Upload
        if (-not $doUpload) {
            $answer = Read-Host "Replace its assets with these signed files? [y/N]"
            $doUpload = $answer -match '^(y|yes|i|igen)$'
        }
        if ($doUpload) {
            gh release upload $tag @releaseFiles --clobber
            if ($LASTEXITCODE -ne 0) {
                Write-Error "gh release upload failed (exit code $LASTEXITCODE)."
                exit 1
            }
            Write-Host "Release assets replaced on $tag" -ForegroundColor Green
        }
        else {
            Write-Host "Release left untouched." -ForegroundColor DarkGray
        }
    }
    else {
        Write-Host ""
        Write-Host "No GitHub release $tag yet - push the tag first, then re-run with -Release to replace the CI assets." -ForegroundColor DarkGray
    }
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
