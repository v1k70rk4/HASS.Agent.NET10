#Requires -Version 7.0
<#
.SYNOPSIS
    HASS.Agent .NET10 build: a standalone .exe for development, or the signed release assets of a tag.
    Run it without parameters for the plain development build.

.DESCRIPTION
    Pick what to do:

      (nothing)     Standalone, self-contained, single-file .exe in artifacts\standalone - no .NET runtime is
                    needed on the target machine, the tray icon is embedded. Offers to replace this machine's
                    installed copy afterwards (-Deploy skips the question, -NoDeploy the whole step).
      -Sign         The same standalone .exe, signed. Handy to test a signed binary on this machine.
      -Tag          Release build for a GitHub tag: the Inno Setup installer (with its uninstaller) and the
                    win-x64 zip, every Windows asset of a release, all signed, with the exact file names the CI
                    workflow produces - so they can replace the CI's unsigned assets on the release. Warns when
                    the working tree is dirty or HEAD does not carry the tag of the version being built.

    Signing happens BEFORE the installer and the zip are assembled, and every produced file is re-read and
    rejected unless its signature is valid. A signing failure is a build failure: nothing unsigned is packaged
    as if it were signed, and nothing is uploaded.

    Nothing machine-specific lives in this script. The signing script - which knows the certificate and how to
    reach it - comes from build.local.psd1 next to this file, which is never committed. Precedence: command-line
    parameter, then build.local.psd1. Example:

        @{
            SignScript = 'C:\Claude\ssign\sign.ps1'      # invoked as: & <script> -Path <file>
        }

.PARAMETER Sign
    Standalone build, signed by the signing script.

.PARAMETER Tag
    Release build (installer + zip), signed by the signing script. Never deployed.

.PARAMETER Upload
    With -Tag: replace the assets on the GitHub release of this version without asking. Without it the script
    asks when such a release exists. With -Upload, a missing `gh` or a failed upload is an error.

.PARAMETER SignScript
    Signing script for -Sign and -Tag, invoked as `& $SignScript -Path <file>`. Default: SignScript in
    build.local.psd1.

.PARAMETER Output
    Output folder of the standalone .exe. Default: artifacts\standalone. -Tag ignores it and uses the CI layout
    under artifacts\ (the installer script reads from there).

.PARAMETER Open
    Opens Explorer with the produced .exe selected when finished.

.PARAMETER Deploy
    Replace the installed copy without asking. Needs an elevated shell. Not available with -Tag.

.PARAMETER NoDeploy
    Never ask about replacing the installed copy.

.EXAMPLE
    # Development build, then replace the installed copy (administrator PowerShell):
    .\build-exe.ps1 -Deploy
.EXAMPLE
    # Signed standalone build for testing:
    .\build-exe.ps1 -Sign
.EXAMPLE
    # Release assets for the tag that was just pushed, then replace the CI's assets on the release:
    .\build-exe.ps1 -Tag -Upload
#>
[CmdletBinding()]
param(
    [switch]$Sign,
    [switch]$Tag,
    [switch]$Upload,
    [string]$SignScript,
    [string]$Output = "artifacts\standalone",
    [switch]$Open,
    [switch]$Deploy,
    [switch]$NoDeploy
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$project = "src\HASS.Agent.NET10\HASS.Agent.NET10.csproj"
$appName = "HASS.Agent .NET10"

if (-not (Test-Path $project)) {
    throw "Project not found: $project  (run this script from inside the repo)."
}
if ($Tag -and $Deploy) { throw "-Tag builds are the public release assets; they are not deployed to this machine." }
if ($Upload -and -not $Tag) { throw "-Upload goes with -Tag." }

# --- Machine-specific settings (build.local.psd1, never committed) -------------------------------------------
$localFile = Join-Path $PSScriptRoot "build.local.psd1"
$localSettings = if (Test-Path $localFile) { Import-PowerShellDataFile $localFile } else { @{} }
if (-not $SignScript -and $localSettings.ContainsKey('SignScript')) { $SignScript = [string]$localSettings['SignScript'] }

$signing = $Sign -or $Tag
if ($signing) {
    if (-not $SignScript) { throw "No signing script configured: set SignScript in $localFile, or pass -SignScript." }
    # Checked up front: finding out after a full build would waste it.
    if (-not (Test-Path $SignScript)) { throw "Sign script not found: $SignScript" }
    $SignScript = (Resolve-Path $SignScript).Path
}

# Version from the csproj: for the messages, the release file names and the tag check.
[xml]$proj = Get-Content $project
$version = ($proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($version)) { throw "Project version not found in $project." }
$tagName = "v$version"

# --- Helpers ---------------------------------------------------------------------------------------------------

function Invoke-SignScript([string]$File) {
    & $SignScript -Path $File
    if ($LASTEXITCODE) { throw "Signing $File failed (exit $LASTEXITCODE)." }
}

# Trust nothing a tool merely reported: re-read the file and insist on a valid, timestamped signature.
function Assert-Signed([string]$File) {
    $sig = Get-AuthenticodeSignature -FilePath $File
    if ($sig.Status -ne 'Valid') { throw "Signature on $File is not valid: $($sig.Status) - $($sig.StatusMessage)" }
    if (-not $sig.TimeStamperCertificate) { throw "Signature on $File has no timestamp - refusing it." }
}

function Get-SignerName([string]$File) {
    $sig = Get-AuthenticodeSignature -FilePath $File
    if (-not $sig.SignerCertificate) { return '' }
    return $sig.SignerCertificate.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
}

# --- A release build has to match its tag: the signed assets replace the CI's assets of exactly that commit. ---
if ($Tag -and (Get-Command git -ErrorAction SilentlyContinue)) {
    if (git status --porcelain 2>$null) {
        Write-Host "WARNING: the working tree has uncommitted changes - these assets will not match any tag." -ForegroundColor Yellow
    }
    $headTags = @(git tag --points-at HEAD 2>$null)
    if ($headTags -contains $tagName) { Write-Host "Release tag: $tagName" -ForegroundColor DarkGray }
    elseif ($headTags.Count -gt 0) { Write-Host "WARNING: HEAD is tagged $($headTags -join ', ') but the project version is $version." -ForegroundColor Yellow }
    else { Write-Host "WARNING: HEAD carries no tag - tag the release ($tagName) first, so the signed assets match it." -ForegroundColor Yellow }
}

# --- Build -----------------------------------------------------------------------------------------------------

$publishArgs = @(
    "publish", $project,
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishSingleFile=true"
)

if ($Tag) {
    # Same layout and flags as the CI workflow: the installer script's Source path and the zip contents match a
    # tag build. Recreated from scratch so nothing from an earlier local build ends up in the release.
    $Output = "artifacts\HASS.Agent.NET10\win-x64"
    if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
    Write-Host "Building $appName $version  (release layout, win-x64)..." -ForegroundColor Cyan
}
else {
    $publishArgs += "-p:IncludeNativeLibrariesForSelfExtract=true"
    Write-Host "Building $appName $version  (standalone, self-contained, win-x64)..." -ForegroundColor Cyan
}

dotnet @publishArgs -o $Output
if ($LASTEXITCODE -ne 0) { throw "Build failed (dotnet publish exit code $LASTEXITCODE)." }

$exe = Join-Path $Output "HASS.Agent.NET10.exe"
if (-not (Test-Path $exe)) { throw "Build finished but the .exe was not found at $exe." }
$fullPath = (Resolve-Path $exe).Path

Write-Host ""
Write-Host "Built ✓  $fullPath  ($([math]::Round((Get-Item $exe).Length / 1MB, 1)) MB)" -ForegroundColor Green

# --- Sign the executable -----------------------------------------------------------------------------------------
if ($signing) {
    Write-Host ""
    Write-Host "[sign] using $SignScript" -ForegroundColor Cyan
    Invoke-SignScript $fullPath
    Assert-Signed $fullPath
}

# --- Release assets: signed installer (+ uninstaller) and the zip, CI file names ----------------------------------
$releaseFiles = @()
if ($Tag) {
    $iscc = @(
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe")
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw "Inno Setup 6 compiler (ISCC.exe) not found." }

    Write-Host ""
    Write-Host "Building the installer..." -ForegroundColor Cyan
    # /DSignSetup turns on the SignTool directive in the .iss; /Srelease=... is the command Inno runs for Setup.exe
    # and the uninstaller: the same signing script through pwsh. $f arrives quoted, $q is a literal quote - kept in
    # single quotes so PowerShell leaves them alone.
    $pwsh = (Get-Command pwsh.exe).Source
    $signCommand = '/Srelease=$q' + $pwsh + '$q -NoProfile -ExecutionPolicy Bypass -File $q' + $SignScript + '$q -Path $f'
    & $iscc "installer\HASS.Agent.NET10.iss" "/DMyAppVersion=$version" "/DSignSetup" $signCommand
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed (exit code $LASTEXITCODE)." }

    $installer = (Resolve-Path "artifacts\installer\HASS.Agent.NET10-Setup-$version.exe").Path
    Assert-Signed $installer

    Write-Host ""
    Write-Host "Packaging the zip..." -ForegroundColor Cyan
    $packageDir = "artifacts\package"
    New-Item -ItemType Directory -Force -Path $packageDir | Out-Null
    $zip = Join-Path $packageDir "HASS.Agent.NET10-win-x64-$version.zip"
    Compress-Archive -Path "$Output\*" -DestinationPath $zip -Force
    $zip = (Resolve-Path $zip).Path

    $releaseFiles = @($installer, $zip)
}

if ($Open) { Start-Process explorer.exe "/select,`"$fullPath`"" }

# --- Summary: only what this run produced. The signer column shows which certificate a hash belongs to. ---------
Write-Host ""
$rows = foreach ($file in @($fullPath) + $releaseFiles) {
    $item = Get-Item $file
    [pscustomobject]@{
        File    = $item.Name
        Version = if ($item.Extension -eq '.exe') { $item.VersionInfo.FileVersion } else { $version }
        Signed  = if ($item.Extension -eq '.exe') { (Get-AuthenticodeSignature $file).Status } else { '-' }
        Signer  = if ($item.Extension -eq '.exe') { Get-SignerName $file } else { '' }
        MB      = [math]::Round($item.Length / 1MB, 1)
        SHA256  = (Get-FileHash $file -Algorithm SHA256).Hash
    }
}
$rows | Format-Table -AutoSize

# --- Replace the assets on the GitHub release of this version ----------------------------------------------------
if ($Tag) {
    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if (-not $gh) {
        if ($Upload) { throw "-Upload needs the GitHub CLI (gh) on PATH." }
        Write-Host "No gh on PATH - to replace the release's assets: gh release upload $tagName <files> --clobber" -ForegroundColor DarkGray
    }
    else {
        # "release not found" is the one failure that means "nothing to replace"; anything else (auth, network)
        # must not pass for a missing release.
        $view = gh release view $tagName --json tagName 2>&1
        if ($LASTEXITCODE -eq 0) {
            Write-Host "GitHub release $tagName exists." -ForegroundColor Cyan
            $doUpload = $Upload
            if (-not $doUpload) {
                $answer = Read-Host "Replace its assets with these signed files? [y/N]"
                $doUpload = $answer -match '^(y|yes|i|igen)$'
            }
            if ($doUpload) {
                gh release upload $tagName @releaseFiles --clobber
                if ($LASTEXITCODE -ne 0) { throw "gh release upload failed (exit code $LASTEXITCODE)." }
                Write-Host "Release assets replaced on $tagName" -ForegroundColor Green
            }
            else {
                Write-Host "Release left untouched." -ForegroundColor DarkGray
            }
        }
        elseif ("$view" -match 'release not found') {
            $hint = "No GitHub release $tagName yet - push the tag, let CI create the release, then re-run with -Tag to replace its assets."
            if ($Upload) { throw $hint }
            Write-Host $hint -ForegroundColor Yellow
        }
        else {
            throw "gh could not read release ${tagName}: $view"
        }
    }
    # A release build ends here; a failed `gh release view` above must not leak out as this script's exit code.
    exit 0
}

# --- Optionally replace the installed copy, so a build can be tested for real without the installer. -------------

$installDir = Join-Path $env:ProgramFiles "HASS.Agent .NET10"
$installedExe = Join-Path $installDir "HASS.Agent.NET10.exe"
$serviceName = "HASS.Agent.NET10.Service"
$processName = "HASS.Agent.NET10"

if ($NoDeploy) { return }

if (-not (Test-Path $installedExe)) {
    Write-Host "No installed copy found at $installDir - nothing to replace." -ForegroundColor DarkGray
    return
}

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
    throw "Replacing the installed copy needs an elevated shell (Program Files and the service). Re-run as administrator."
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
    throw "Could not replace $installedExe : $($_.Exception.Message)"
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
