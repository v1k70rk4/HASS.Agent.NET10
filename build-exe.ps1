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
      -Sign         The same standalone .exe, signed, with the native libraries inside it that come unsigned
                    (libsodium) signed too. Handy to test a signed binary on this machine.
      -Tag          Release build for a GitHub tag: the Inno Setup installer (with its uninstaller) and the
                    win-x64 zip, every Windows asset of a release, all signed, with the exact file names the CI
                    workflow produces - so they can replace the CI's unsigned assets on the release. Warns when
                    the working tree is dirty or HEAD does not carry the tag of the version being built.

    Signing happens BEFORE the installer and the zip are assembled, and every produced file is re-read and
    rejected unless its signature is valid. A signing failure is a build failure: nothing unsigned is packaged
    as if it were signed, and nothing is uploaded.

    Which source a development build uses: the script fetches origin and builds what is waiting to be tested,
    not whatever this folder happens to have checked out. With one open pull request that is its branch; with
    none it is main; with several it asks (Enter takes the newest). Uncommitted changes or local-only commits
    in this folder add "this folder as it is" to the choice. -Branch names the source and skips the question.
    Anything other than this folder's own state is exported to a temp folder and built there, so nothing is
    checked out or changed here. -Tag always builds this folder as it is.

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
    asks when such a release exists. With -Upload, a missing `gh` or a failed upload is an error. Only a draft
    release (what CI makes from the tag) is touched: a release already published is left alone, unless
    -Replace is given too.

.PARAMETER Replace
    With -Tag: also replace the assets of a release that is already published. Its Sigstore bundles then no
    longer match; run the sign-release workflow for the tag again afterwards.

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

.PARAMETER Branch
    Development builds: the branch to build (e.g. main), or . for this folder as it is. The branch is
    taken from origin; a local branch of the same name is used instead when origin does not have it, or
    when it is ahead of origin (commits waiting for a test before they are pushed).
    Default: chosen as described above.

.PARAMETER Version
    Build with this version instead of the csproj's (e.g. 10.6.8-test.1, which the released 10.6.8 then
    outranks - handy for testing the update path against a real release). Not allowed with -Tag.

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
    [switch]$Replace,
    [string]$SignScript,
    [string]$Output = "artifacts\standalone",
    [switch]$Open,
    [switch]$Deploy,
    [switch]$NoDeploy,
    [string]$Branch,
    [string]$Version
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$projectPath = "src\HASS.Agent.NET10\HASS.Agent.NET10.csproj"
$appName = "HASS.Agent .NET10"
$hasGit = [bool](Get-Command git -ErrorAction SilentlyContinue)

if (-not (Test-Path $projectPath)) {
    throw "Project not found: $projectPath  (run this script from inside the repo)."
}
if ($Tag -and $Deploy) { throw "-Tag builds are the public release assets; they are not deployed to this machine." }
if ($Upload -and -not $Tag) { throw "-Upload goes with -Tag." }
if ($Version -and $Tag) { throw "-Version is for test builds; a -Tag build carries the csproj version." }
if ($Branch -and $Tag) { throw "-Branch is for development builds; a -Tag build is this folder at the tag." }

# The GitHub repository behind origin. Always named explicitly to gh: this checkout also has an "upstream"
# remote (the original HASS.Agent), and gh would otherwise pick that one.
function Get-OriginRepo {
    $originUrl = git remote get-url origin 2>$null
    if ($originUrl -match 'github\.com[:/](?<repo>[^/]+/[^/]+?)(\.git)?/?$') { return $Matches['repo'] }
    return $null
}

# Whether the GitHub release of a tag is a draft, published, or missing. Anything else gh says (no login, no
# network) is an error, never taken for one of those: the answer decides whether files may be replaced.
function Get-ReleaseState([string] $tag, [string] $repo) {
    $answer = gh release view $tag -R $repo --json isDraft --jq '.isDraft' 2>&1
    if ($LASTEXITCODE -eq 0) {
        switch ("$answer".Trim()) {
            'true' { return 'draft' }
            'false' { return 'published' }
        }
    }
    elseif ("$answer" -match 'release not found') { return 'missing' }
    throw "gh could not tell whether release $tag is a draft: $answer"
}

# --- Which source a development build uses (see the description) ------------------------------------------------
# Returns the folder to build from: this one, or an export of the chosen commit under %TEMP%.
function Select-Source {
    git fetch origin --prune --quiet 2>$null
    if ($LASTEXITCODE -ne 0) { Write-Host "WARNING: could not fetch origin - choosing from what is known locally." -ForegroundColor Yellow }

    $head = git rev-parse HEAD 2>$null
    $dirty = [bool](git status --porcelain 2>$null)
    $localOnly = -not (git branch -r --contains HEAD 2>$null)

    # Branches with something to test: open pull requests when gh can tell, otherwise every branch on origin
    # that is ahead of main (which also lists squash-merged branches nobody deleted).
    $open = @()
    $repo = Get-OriginRepo
    if ($repo -and (Get-Command gh -ErrorAction SilentlyContinue)) {
        $json = gh pr list -R $repo --state open --json number,title,headRefName 2>$null
        if ($LASTEXITCODE -eq 0 -and $json) {
            $open = @($json | ConvertFrom-Json | ForEach-Object {
                [pscustomobject]@{ Ref = "origin/$($_.headRefName)"; Label = "$($_.headRefName)  (#$($_.number) $($_.title))" }
            })
        }
    }
    if ($open.Count -eq 0) {
        $open = @(git for-each-ref --format='%(refname:short)' refs/remotes/origin 2>$null |
            Where-Object { $_ -notin 'origin', 'origin/HEAD', 'origin/main' } |
            Where-Object { [int](git rev-list --count "origin/main..$_" 2>$null) -gt 0 } |
            ForEach-Object { [pscustomobject]@{ Ref = $_; Label = ($_ -replace '^origin/', '') } })
    }
    $open = @($open | Where-Object { git rev-parse --verify --quiet "$($_.Ref)^{commit}" 2>$null } |
        Sort-Object { [long](git log -1 --format=%ct $_.Ref) } -Descending)

    $here = [pscustomobject]@{ Ref = '.'; Label = 'this folder as it is' + $(if ($dirty) { '  (uncommitted changes)' } elseif ($localOnly) { '  (commits that are not on origin)' }) }
    $choices = @($open) + [pscustomobject]@{ Ref = 'origin/main'; Label = 'main' }
    if ($dirty -or $localOnly) { $choices += $here }

    if ($Branch -eq '.') { $chosen = $here }
    elseif ($Branch) {
        $name = $Branch -replace '^origin/', ''
        $ref = "origin/$name"
        $local = "refs/heads/$name"
        git rev-parse --verify --quiet "$ref^{commit}" 2>$null | Out-Null
        $onOrigin = $LASTEXITCODE -eq 0
        git rev-parse --verify --quiet "$local^{commit}" 2>$null | Out-Null
        $isLocal = $LASTEXITCODE -eq 0
        if (-not $onOrigin -and -not $isLocal) { throw "No such branch on origin or locally: $Branch" }

        # Commits made here (or in a worktree of this repository) and waiting for a test before they are
        # pushed: the local branch is the one to build when origin does not have it, or is behind it.
        $ahead = 0
        if ($onOrigin -and $isLocal) {
            git merge-base --is-ancestor $ref $local 2>$null
            if ($LASTEXITCODE -eq 0) { $ahead = [int](git rev-list --count "$ref..$local" 2>$null) }
        }
        if (-not $onOrigin) { $chosen = [pscustomobject]@{ Ref = $local; Label = "$name  (local branch, not on origin)" } }
        elseif ($ahead -gt 0) { $chosen = [pscustomobject]@{ Ref = $local; Label = "$name  (local branch, $ahead commit(s) ahead of origin)" } }
        else { $chosen = [pscustomobject]@{ Ref = $ref; Label = $name } }
    }
    elseif ($open.Count -le 1 -and -not ($dirty -or $localOnly)) {
        # The usual case: the one branch waiting to be tested, or main when nothing is.
        $chosen = $choices[0]
    }
    else {
        Write-Host "Which source to build?" -ForegroundColor Cyan
        for ($i = 0; $i -lt $choices.Count; $i++) { Write-Host ("  {0}. {1}" -f ($i + 1), $choices[$i].Label) }
        $answer = Read-Host "Number [1]"
        $index = 0
        if ($answer -and (-not [int]::TryParse($answer, [ref]$index) -or $index -lt 1 -or $index -gt $choices.Count)) { throw "Not one of the choices: $answer" }
        $chosen = $choices[[Math]::Max($index, 1) - 1]
    }

    if ($chosen.Ref -eq '.') {
        Write-Host "Source: $($chosen.Label)" -ForegroundColor Cyan
        return $PSScriptRoot
    }

    $sha = git rev-parse --verify --quiet "$($chosen.Ref)^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $sha) { throw "Cannot resolve $($chosen.Ref) - fetch origin, or pass -Branch . to build this folder." }
    Write-Host "Source: $($chosen.Label)  @ $(git log -1 --format='%h  %s' $sha)" -ForegroundColor Cyan
    if ($sha -eq $head -and -not $dirty) { return $PSScriptRoot }   # already what this folder has

    # Exported, not checked out: this folder (and whatever is open in it) stays as it is. Keyed by commit,
    # so building the same commit again is incremental; exports of other commits are cleared out.
    $exportRoot = Join-Path ([IO.Path]::GetTempPath()) "hass-agent-build"
    $exportDir = Join-Path $exportRoot $sha.Substring(0, 12)
    if (-not (Test-Path (Join-Path $exportDir $projectPath))) {
        if (Test-Path $exportRoot) { Remove-Item $exportRoot -Recurse -Force -ErrorAction SilentlyContinue }
        New-Item -ItemType Directory -Force -Path $exportDir | Out-Null
        $archive = Join-Path $exportRoot "source.zip"
        git archive --format=zip -o $archive $sha
        if ($LASTEXITCODE -ne 0) { throw "git archive of $($chosen.Ref) failed (exit code $LASTEXITCODE)." }
        Expand-Archive -LiteralPath $archive -DestinationPath $exportDir -Force
        Remove-Item $archive -Force
    }
    # An export has no .git, so the build cannot read the commit for the version (10.9.1+<sha>).
    $script:exportedSha = $sha
    return $exportDir
}

$sourceRoot = if (-not $Tag -and $hasGit) { @(Select-Source)[-1] } else { $PSScriptRoot }
$project = Join-Path $sourceRoot $projectPath

# --- Machine-specific settings (build.local.psd1, never committed) -------------------------------------------
$localFile = Join-Path $PSScriptRoot "build.local.psd1"
if (-not (Test-Path $localFile) -and $hasGit) {
    # In a git worktree the file lives in the main checkout; it is not committed, so it is not here.
    $commonDir = git rev-parse --path-format=absolute --git-common-dir 2>$null
    if ($commonDir) {
        $mainFile = Join-Path (Split-Path $commonDir -Parent) "build.local.psd1"
        if (Test-Path $mainFile) { $localFile = $mainFile }
    }
}
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
$appVersion = ($proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($appVersion)) { throw "Project version not found in $project." }
if ($Version) { $appVersion = $Version }   # the parameter; PowerShell names are case-insensitive, hence the different name
$tagName = "v$appVersion"

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
# The tag name comes from the project version, so a build of the wrong commit would replace the assets of whatever
# release carries that version (run from main before the release commit was merged, it would have overwritten the
# previous beta's files). A build that does not match is still made, for local use, but it is never uploaded:
# -Upload stops here, and the interactive offer to replace the assets is not made for it either.
$buildMatchesTag = $false
$mismatch = ''
if ($Tag) {
    if (-not $hasGit) {
        $mismatch = "no git on PATH, so there is no telling whether this build is the tagged commit."
    }
    elseif (git status --porcelain 2>$null) {
        $mismatch = "the working tree has uncommitted changes - these assets will not match any tag."
    }
    else {
        $headTags = @(git tag --points-at HEAD 2>$null)
        if ($headTags -contains $tagName) { $buildMatchesTag = $true; Write-Host "Release tag: $tagName" -ForegroundColor DarkGray }
        elseif ($headTags.Count -gt 0) { $mismatch = "HEAD is tagged $($headTags -join ', ') but the project version is $appVersion." }
        else { $mismatch = "HEAD carries no tag - tag the release ($tagName) first, so the signed assets match it." }
    }
    if (-not $buildMatchesTag) {
        if ($Upload) { throw "Not uploading: $mismatch" }
        Write-Host "WARNING: $mismatch" -ForegroundColor Yellow
    }

    # Before anything is built and signed: -Upload goes to a draft only (see -Replace).
    if ($Upload -and -not $Replace -and (Get-Command gh -ErrorAction SilentlyContinue) -and ($earlyRepo = Get-OriginRepo)) {
        if ((Get-ReleaseState $tagName $earlyRepo) -eq 'published') {
            throw "Release $tagName is already published. Check out the tag of the new release, or add -Replace to replace its assets."
        }
    }
}

# --- Build -----------------------------------------------------------------------------------------------------

$publishArgs = @(
    "publish", $project,
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishSingleFile=true"
)
if ($Version) { $publishArgs += "-p:Version=$Version" }
if ($script:exportedSha) { $publishArgs += "-p:SourceRevisionId=$($script:exportedSha)" }

if ($Tag) {
    # Same layout and flags as the CI workflow: the installer script's Source path and the zip contents match a
    # tag build. Recreated from scratch so nothing from an earlier local build ends up in the release.
    $Output = "artifacts\HASS.Agent.NET10\win-x64"
    if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
    Write-Host "Building $appName $appVersion  (release layout, win-x64)..." -ForegroundColor Cyan
}
else {
    $publishArgs += "-p:IncludeNativeLibrariesForSelfExtract=true"
    Write-Host "Building $appName $appVersion  (standalone, self-contained, win-x64)..." -ForegroundColor Cyan
}

# --- Native libraries that come unsigned: signed before the build takes them --------------------------------------
# A trial publish in the release layout puts the native libraries next to the exe. Those without their maker's
# signature (libsodium, under the Sendspin player) are copied to artifacts\signed-native and signed there, and the
# real publish takes those copies (the project's UseSignedNativeLibraries target): next to the exe for -Tag, and
# inside the single-file exe for -Sign, which unpacks them at start. Microsoft's own keep their signature.
if ($signing) {
    $probeDir = Join-Path ([IO.Path]::GetTempPath()) "hass-agent-native-probe"
    $signedNativeDir = Join-Path $PSScriptRoot "artifacts\signed-native"
    foreach ($dir in @($probeDir, $signedNativeDir)) { if (Test-Path $dir) { Remove-Item $dir -Recurse -Force } }
    New-Item -ItemType Directory -Force -Path $signedNativeDir | Out-Null

    Write-Host "Looking for native libraries that come unsigned..." -ForegroundColor Cyan
    dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $probeDir --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Trial publish failed (dotnet publish exit code $LASTEXITCODE)." }

    foreach ($dll in Get-ChildItem -Path $probeDir -Filter *.dll -File -Recurse) {
        $status = (Get-AuthenticodeSignature -FilePath $dll.FullName).Status
        if ($status -eq 'Valid') { continue }
        # A signature that is there but broken is not papered over with ours.
        if ($status -ne 'NotSigned') { throw "Signature on $($dll.Name) is $status - refusing to sign over it." }
        $copy = Join-Path $signedNativeDir $dll.Name
        Copy-Item $dll.FullName $copy
        Write-Host "[sign] $($dll.Name) (came unsigned)" -ForegroundColor Cyan
        Invoke-SignScript $copy
        Assert-Signed $copy
    }

    Remove-Item $probeDir -Recurse -Force -ErrorAction SilentlyContinue
    $publishArgs += "-p:SignedNativeDir=$signedNativeDir"
}

# The exe of an earlier build must not survive: publish skips the single-file bundle when the one in the
# output folder looks newer than its inputs, which it does after a build of another source (or version).
$staleExe = Join-Path $Output "HASS.Agent.NET10.exe"
if (Test-Path $staleExe) { Remove-Item $staleExe -Force }

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

    # Every library next to the exe is signed by now (its maker's, or ours from the step before the build):
    # checked before the installer and the zip take them, so nothing unsigned goes into Program Files.
    if ($Tag) {
        foreach ($dll in Get-ChildItem -Path $Output -Filter *.dll -File -Recurse) {
            $status = (Get-AuthenticodeSignature -FilePath $dll.FullName).Status
            if ($status -ne 'Valid') { throw "$($dll.FullName) is $status in the release layout - every library there must be signed." }
        }
    }
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
    & $iscc "installer\HASS.Agent.NET10.iss" "/DMyAppVersion=$appVersion" "/DSignSetup" $signCommand
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed (exit code $LASTEXITCODE)." }

    $installer = (Resolve-Path "artifacts\installer\HASS.Agent.NET10-Setup-$appVersion.exe").Path
    Assert-Signed $installer

    Write-Host ""
    Write-Host "Packaging the zip..." -ForegroundColor Cyan
    $packageDir = "artifacts\package"
    New-Item -ItemType Directory -Force -Path $packageDir | Out-Null
    $zip = Join-Path $packageDir "HASS.Agent.NET10-win-x64-$appVersion.zip"
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
        # The product version: the file version of an exe can only be four numbers (10.9.1.0).
        Version = if ($item.Extension -eq '.exe') { ($item.VersionInfo.ProductVersion -split '\+')[0] } else { $appVersion }
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
        Write-Host "No gh on PATH - to replace the release's assets: gh release upload $tagName <files> --clobber -R <owner/repo>" -ForegroundColor DarkGray
    }
    else {
        # Always name the repository, or gh would look for, or upload to, the release of the wrong project.
        $repo = Get-OriginRepo
        if (-not $repo) { throw "Cannot tell the GitHub repository from the origin remote: $(git remote get-url origin 2>$null)" }

        # "release not found" is the one failure that means "nothing to replace"; anything else (auth, network)
        # must not pass for a missing release.
        $view = gh release view $tagName -R $repo --json tagName 2>&1
        if ($LASTEXITCODE -eq 0) {
            Write-Host "GitHub release $tagName exists on $repo." -ForegroundColor Cyan
            $doUpload = $Upload
            $isPublished = (Get-ReleaseState $tagName $repo) -eq 'published'
            if ($isPublished -and -not $Replace) {
                # A published release is what people and the updater already download, with Sigstore bundles
                # made for its files. Replacing them by accident (an old checkout still on an earlier tag)
                # happened once; it takes -Replace now.
                Write-Host "Release $tagName is already published; its assets are left as they are (add -Replace to replace them)." -ForegroundColor Yellow
                if ($Upload) { throw "Release $tagName is already published. Check out the tag of the new release, or add -Replace." }
                $doUpload = $false
            }
            elseif (-not $buildMatchesTag) {
                # Not offered either: the same guard as -Upload's, for the same reason.
                Write-Host "Its assets are not replaced with this build: $mismatch" -ForegroundColor Yellow
                $doUpload = $false
            }
            elseif (-not $doUpload) {
                $answer = Read-Host "Replace its assets with these signed files? [y/N]"
                $doUpload = $answer -match '^(y|yes|i|igen)$'
            }
            if ($doUpload) {
                gh release upload $tagName @releaseFiles --clobber -R $repo
                if ($LASTEXITCODE -ne 0) { throw "gh release upload failed (exit code $LASTEXITCODE)." }
                Write-Host "Release assets replaced on $tagName" -ForegroundColor Green

                # What is on the release must be what was just signed here, by hash.
                function Assert-SignedAssets([string] $when) {
                    $published = gh release view $tagName -R $repo --json assets --jq '.assets[] | "\(.name) \(.digest)"' 2>$null
                    foreach ($file in $releaseFiles) {
                        $name = Split-Path $file -Leaf
                        $expected = "sha256:" + (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
                        if (-not ($published | Where-Object { $_ -eq "$name $expected" })) {
                            throw "Release $tagName does not carry the signed $name $when (expected $expected). Check the release before anyone downloads it."
                        }
                    }
                }

                # CI creates the release as a draft, so that nothing unsigned is ever public.
                # With the signed files in place, and only then (an upload by CI still running could
                # have put its own files back), it goes live; a stable tag becomes "latest".
                $isDraft = (gh release view $tagName -R $repo --json isDraft --jq '.isDraft' 2>$null) -eq 'true'
                if ($isDraft) {
                    Assert-SignedAssets "before publishing; it is still a draft"
                    $publishArgs = @('release', 'edit', $tagName, '--draft=false', '-R', $repo)
                    if ($tagName -notmatch '-') { $publishArgs += '--latest' }
                    gh @publishArgs
                    if ($LASTEXITCODE -ne 0) { throw "gh release edit failed (exit code $LASTEXITCODE) - the release is still a draft." }
                    Write-Host "Release $tagName published" -ForegroundColor Green
                }

                # Again after publishing, because that is the state people download.
                Assert-SignedAssets "after publishing"
                Write-Host "Release assets verified against the signed files" -ForegroundColor Green
            }
            else {
                Write-Host "Release left untouched." -ForegroundColor DarkGray
            }
        }
        elseif ("$view" -match 'release not found') {
            $hint = "No GitHub release $tagName yet - push the tag, let CI create the (draft) release, then re-run with -Tag -Upload to replace its assets and publish it."
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
}

function Wait-AgentExit([int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Process -Name $processName -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 300
    }
    return -not (Get-Process -Name $processName -ErrorAction SilentlyContinue)
}

# "Stopped" is not "gone": the service reports stopped a moment before its process exits, and
# the exe stays locked until it does. A fixed sleep here used to lose that race now and then -
# and left the machine with the service uninstalled and the old exe in place.
if (-not (Wait-AgentExit 20)) {
    Write-Host "  Still running - forcing it closed." -ForegroundColor Yellow
    Stop-Process -Name $processName -Force -ErrorAction SilentlyContinue
    [void](Wait-AgentExit 10)
}

# Puts the service back with whatever exe is installed at that moment. Returns whether it
# ended up running, so the caller decides what a service that stayed down means.
function Restore-Service {
    if (-not $serviceWasRunning) { return $true }
    Write-Host "  Putting the service back..." -ForegroundColor Yellow
    [void](Invoke-Agent $installedExe "--install-service --quiet")
    Start-Sleep -Seconds 2
    $restored = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -eq $restored) {
        Write-Warning "The service did not come back - install it from the app's Service page."
        return $false
    }
    if ($restored.Status -ne 'Running') {
        [void](Invoke-Agent $installedExe "--start-service --quiet")
        try { $restored.WaitForStatus('Running', [TimeSpan]::FromSeconds(15)) } catch { }
        $restored.Refresh()
    }
    if ($restored.Status -ne 'Running') {
        Write-Warning "The service is installed but $($restored.Status), not Running."
        return $false
    }
    Write-Host "  Service status: $($restored.Status)" -ForegroundColor Green
    return $true
}

# The new build is staged next to the installed exe first, and the old exe is kept until the
# service runs again: a copy that fails half way, or a build whose service will not start,
# ends with the previous build back in place rather than with a broken install.
$stagedExe = "$installedExe.new"
$backupExe = "$installedExe.bak"

Write-Host "  Copying the new build in..." -ForegroundColor Yellow
Copy-Item -LiteralPath $fullPath -Destination $stagedExe -Force -ErrorAction Stop

$swapped = $false
$swapError = $null
foreach ($attempt in 1..10) {
    try {
        [System.IO.File]::Move($installedExe, $backupExe, $true)
        try {
            [System.IO.File]::Move($stagedExe, $installedExe)
        }
        catch {
            [System.IO.File]::Move($backupExe, $installedExe)
            throw
        }
        $swapped = $true
        break
    }
    catch {
        # Usually the virus scanner still holding the exe the process just let go of.
        $swapError = $_.Exception.Message
        Start-Sleep -Seconds 1
    }
}

if (-not $swapped) {
    # The old exe is untouched, so the service it belongs to goes back exactly as it was.
    if (Test-Path -LiteralPath $stagedExe) { [System.IO.File]::Delete($stagedExe) }
    [void](Restore-Service)
    throw "Could not replace $installedExe after 10 attempts - the old build is still installed. Last error: $swapError"
}

if (-not (Restore-Service)) {
    Write-Host "  The service does not run with the new build - putting the previous build back..." -ForegroundColor Yellow
    [void](Invoke-Agent $installedExe "--stop-service --quiet")
    [void](Invoke-Agent $installedExe "--uninstall-service --quiet")
    [void](Wait-AgentExit 20)
    [System.IO.File]::Move($backupExe, $installedExe, $true)
    [void](Restore-Service)
    throw "The service did not start with the new build; the previous build was restored."
}

if (Test-Path -LiteralPath $backupExe) { [System.IO.File]::Delete($backupExe) }

Write-Host ""
Write-Host "Installed copy replaced" -ForegroundColor Green
if ($trayWasRunning) {
    # Not restarted on purpose: this shell is elevated, and the tray app started
    # from here would inherit that and run as administrator.
    Write-Host "  The tray app was closed - start it yourself so it runs unelevated:" -ForegroundColor Yellow
    Write-Host "    $installedExe" -ForegroundColor Yellow
}

# SIG # Begin signature block
# MIIodQYJKoZIhvcNAQcCoIIoZjCCKGICAQExDzANBglghkgBZQMEAgEFADB5Bgor
# BgEEAYI3AgEEoGswaTA0BgorBgEEAYI3AgEeMCYCAwEAAAQQH8w7YFlLCE63JNLG
# KX7zUQIBAAIBAAIBAAIBAAIBADAxMA0GCWCGSAFlAwQCAQUABCBVN1xIQMcC6ryT
# JcvQZXUoosFqt4+8NNREGbdfrUX0eKCCDR8wggZeMIIERqADAgECAhBNmC3Mcig7
# 2XedD7d/I7DIMA0GCSqGSIb3DQEBCwUAMFYxCzAJBgNVBAYTAlBMMSEwHwYDVQQK
# ExhBc3NlY28gRGF0YSBTeXN0ZW1zIFMuQS4xJDAiBgNVBAMTG0NlcnR1bSBDb2Rl
# IFNpZ25pbmcgMjAyMSBDQTAeFw0yNjA5MTYwODE1NDRaFw0yNzA5MTYwODE1NDNa
# MIGDMQswCQYDVQQGEwJIVTERMA8GA1UECAwIQnVkYXBlc3QxETAPBgNVBAcMCEJ1
# ZGFwZXN0MR4wHAYDVQQKDBVPcGVuIFNvdXJjZSBEZXZlbG9wZXIxLjAsBgNVBAMM
# JU9wZW4gU291cmNlIERldmVsb3BlciBWaWt0b3IgUsOpdsOpc3owggGiMA0GCSqG
# SIb3DQEBAQUAA4IBjwAwggGKAoIBgQCHgWCQuCnYig77lld16eLYhqH48+uJM8v4
# TvDcZSF3MB2R9O3clCWgVIVQ8bTh0DAZ55Inpjh6ccJtcyKr0uI8ZZb5kDqdx7wQ
# OmKC+4Wi0cdHHlLQitn+sPrl/PFgXQbBea9E2gm10cOBDkGUaRvIH2P7nw90R7+j
# Mog60xqyLEm9l+LdWZxtqdk+b+nuEKuu3HWlMi3bV9/f2G71in0lskDL1bHQJYqS
# +pNU5WeBBM2mO6Qg6G0d8uzllh0pvRg211Hkts8IyQhyto4vhrdcbQ/Nodl52++p
# C+q6g9dvsGmCoxCUvooTFj1a5nOXHRLUWAJGP0ZQbrHVKVxg+ifxfIIa6nZOrkIT
# cKOeiN6qqkIKW2VK29arz9ck0pkKd9thvpEzT9A4EwOFUNK2d8Fqarcc5+RaEBVq
# npI5jzSPfWQuwBKwGkyr5x/z1/vIWfbtroE4rNb6b5p8JGhR8XtytPIzqC3AkqhC
# Ch6C2EhquQJt1Nhywrqzt+b7EY/PKm0CAwEAAaOCAXgwggF0MAwGA1UdEwEB/wQC
# MAAwPQYDVR0fBDYwNDAyoDCgLoYsaHR0cDovL2Njc2NhMjAyMS5jcmwuY2VydHVt
# LnBsL2Njc2NhMjAyMS5jcmwwcwYIKwYBBQUHAQEEZzBlMCwGCCsGAQUFBzABhiBo
# dHRwOi8vY2NzY2EyMDIxLm9jc3AtY2VydHVtLmNvbTA1BggrBgEFBQcwAoYpaHR0
# cDovL3JlcG9zaXRvcnkuY2VydHVtLnBsL2Njc2NhMjAyMS5jZXIwHwYDVR0jBBgw
# FoAU3XRdTADbe5+gdMqxbvc8wDLAcM0wHQYDVR0OBBYEFHvFGXj34BX8e5Gtig/I
# qvU3WzF7MEsGA1UdIAREMEIwCAYGZ4EMAQQBMDYGCyqEaAGG9ncCBQEEMCcwJQYI
# KwYBBQUHAgEWGWh0dHBzOi8vd3d3LmNlcnR1bS5wbC9DUFMwEwYDVR0lBAwwCgYI
# KwYBBQUHAwMwDgYDVR0PAQH/BAQDAgeAMA0GCSqGSIb3DQEBCwUAA4ICAQBT6+sj
# irDn4Csvy8n1Kipy7lO6wXUsebHq+ukpO9bMqjhHDjTGol3lxMZbEZbTAoYH7ay8
# LZHwck9Iu8/yMkzrYmdGD+YuPUHnKa5O7aFqoNvyBddHITln61y7BdF/164RWFP6
# kmrFjxrtChQc+kv4dAJW+uiAoL36yyBsFnv1sYB5dYt70aElQOBQ4rv4CSbgy/KN
# d1DqYQ5Q8JcMZgSF95nyKVgGVxekdop/Qw0/HYsoY6CZ6u59S9Upwh7eZ2ZGz9bz
# nRBvJ1kVv/Q/TNrVJjvRL+Pcicb8UvJwaf3uT1qWglROGcX+3uwgGLDotrSWT9Xi
# TV+jDbyQfOQKmXW/6HXWqC/z1+nvO6Z257EP03TeaE3UPu3sqORnorkbMCpY0D4j
# +neh4glK2B7mJmS+hpf/8lVw55++2FvGomeB43ETzRD7tzd9ODYifxcR0QIcPBdd
# XF2GulF9UsXZmqLIsgSGZtWVA/aiFhgZtflrCrIYTCTH82CO8Hdtq+cWag+U+07H
# IaR12TLeeviJeBQaaKdF8MI+pWj90jsHdaYGN72F6ViNKZJgREU/Qlw8v7ppmz2f
# p7L44qHw3PwRrrvkwROmUnvE2M+DLBGZSmrzMPp2Jy+rpIbgr/X3f3+FQms/UuNi
# dojgO0Qf1aGyq/SE9tCMjzmZCfGNzYqUKefq+jCCBrkwggShoAMCAQICEQCZo4AK
# JlU7ZavcboSms+o5MA0GCSqGSIb3DQEBDAUAMIGAMQswCQYDVQQGEwJQTDEiMCAG
# A1UEChMZVW5pemV0byBUZWNobm9sb2dpZXMgUy5BLjEnMCUGA1UECxMeQ2VydHVt
# IENlcnRpZmljYXRpb24gQXV0aG9yaXR5MSQwIgYDVQQDExtDZXJ0dW0gVHJ1c3Rl
# ZCBOZXR3b3JrIENBIDIwHhcNMjEwNTE5MDUzMjE4WhcNMzYwNTE4MDUzMjE4WjBW
# MQswCQYDVQQGEwJQTDEhMB8GA1UEChMYQXNzZWNvIERhdGEgU3lzdGVtcyBTLkEu
# MSQwIgYDVQQDExtDZXJ0dW0gQ29kZSBTaWduaW5nIDIwMjEgQ0EwggIiMA0GCSqG
# SIb3DQEBAQUAA4ICDwAwggIKAoICAQCdI88EMCM7wUYs5zNzPmNdenW6vlxNur3r
# Lfi+5OZ+U3iZIB+AspO+CC/bj+taJUbMbFP1gQBJUzDUCPx7BNLgid1TyztVLn52
# NKgxxu8gpyTr6EjWyGzKU/gnIu+bHAse1LCitX3CaOE13rbuHbtrxF2tPU8f253Q
# gX6eO8yTbGps1Mg+yda3DcTsOYOhSYNCJiL+5wnjZ9weoGRtvFgMHtJg6i671OPX
# IciiHO4Lwo2p9xh/tnj+JmCQEn5QU0NxzrOiRna4kjFaA9ZcwSaG7WAxeC/xoZSx
# F1oK1UPZtKVt+yrsGKqWONoK6f5EmBOAVEK2y4ATDSkb34UD7JA32f+Rm0wsr5aj
# zftDhA5mBipVZDjHpwzv8bTKzCDUSUuUmPo1govD0RwFcTtMXcfJtm1i+P2UNXad
# PyYVKRxKQATHN3imsfBiNRdN5kiVVeqP55piqgxOkyt+HkwIA4gbmSc3hD8ke66t
# 9MjlcNg73rZZlrLHsAIV/nJ0mmgSjBI/TthoGJDydekOQ2tQD2Dup/+sKQptalDl
# ui59SerVSJg8gAeV7N/ia4mrGoiez+SqV3olVfxyLFt3o/OQOnBmjhKUANoKLYlK
# mUpKEFI0PfoT8Q1W/y6s9LTI6ekbi0igEbFUIBE8KDUGfIwnisEkBw5KcBZ3XwnH
# mfznwlKo8QIDAQABo4IBVTCCAVEwDwYDVR0TAQH/BAUwAwEB/zAdBgNVHQ4EFgQU
# 3XRdTADbe5+gdMqxbvc8wDLAcM0wHwYDVR0jBBgwFoAUtqFUOQLDoD+Oirz61Pgc
# ptE6Dv0wDgYDVR0PAQH/BAQDAgEGMBMGA1UdJQQMMAoGCCsGAQUFBwMDMDAGA1Ud
# HwQpMCcwJaAjoCGGH2h0dHA6Ly9jcmwuY2VydHVtLnBsL2N0bmNhMi5jcmwwbAYI
# KwYBBQUHAQEEYDBeMCgGCCsGAQUFBzABhhxodHRwOi8vc3ViY2Eub2NzcC1jZXJ0
# dW0uY29tMDIGCCsGAQUFBzAChiZodHRwOi8vcmVwb3NpdG9yeS5jZXJ0dW0ucGwv
# Y3RuY2EyLmNlcjA5BgNVHSAEMjAwMC4GBFUdIAAwJjAkBggrBgEFBQcCARYYaHR0
# cDovL3d3dy5jZXJ0dW0ucGwvQ1BTMA0GCSqGSIb3DQEBDAUAA4ICAQB1iFgP5Y9Q
# KJpTnxDsQ/z0O23JmoZifZdEOEmQvo/79PQg9nLF/GJe6ZiUBEyDBHMtFRK0mXj3
# Qv3gL0sYXe+PPMfwmreJHvgFGWQ7XwnfMh2YIpBrkvJnjwh8gIlNlUl4KENTK5DL
# qsYPEtRQCw7R6p4s2EtWyDDr/M58iY2UBEqfUU/ujR9NuPyKk0bEcEi62JGxauFY
# zZ/yld13fHaZskIoq2XazjaD0pQkcQiIueL0HKiohS6XgZuUtCKA7S6CHttZEsOb
# QJ1j2s0urIDdqF7xaXFVaTHKtAuMfwi0jXtF3JJphrJfc+FFILgCbX/uYBPBlbBI
# P4Ht4xxk2GmfzMn7oxPITpigQFJFWuzTMUUgdRHTxaTSKRJ/6Uh7ki/pFjf9sUAS
# WgxT69QF9Ki4JF5nBIujxZ2sOU9e1HSCJwOfK07t5nnzbs1LbHuAIGJsRJiQ6HX/
# DW1XFOlXY1rc9HufFhWU+7Uk+hFkJsfzqBz3pRO+5aI6u5abI4Qws4YaeJH7H7M8
# X/YNoaArZbV4Ql+jarKsE0+8XvC4DJB+IVcvC9Ydqahi09mjQse4fxfef0L7E3hh
# o2O3bLDM6v60rIRUCi2fJT2/IRU5ohgyTch4GuYWefSBsp5NPJh4QRTP9DC3gc5Q
# EKtbrTY0Ka87Web7/zScvLmvQBm8JDFpDjGCGqwwghqoAgEBMGowVjELMAkGA1UE
# BhMCUEwxITAfBgNVBAoTGEFzc2VjbyBEYXRhIFN5c3RlbXMgUy5BLjEkMCIGA1UE
# AxMbQ2VydHVtIENvZGUgU2lnbmluZyAyMDIxIENBAhBNmC3Mcig72XedD7d/I7DI
# MA0GCWCGSAFlAwQCAQUAoHwwEAYKKwYBBAGCNwIBDDECMAAwGQYJKoZIhvcNAQkD
# MQwGCisGAQQBgjcCAQQwHAYKKwYBBAGCNwIBCzEOMAwGCisGAQQBgjcCARUwLwYJ
# KoZIhvcNAQkEMSIEIOP5QAbQDNHfRePw/os4gx4o02eC1FXBFuosTuD/6vqYMA0G
# CSqGSIb3DQEBAQUABIIBgCF1Zr99gwysRiFh7udmviXMQHQY1Mqh4fPWHmYDgTyE
# 8q7zqmXIDlMSwBiaqnOJcAu/SVfq+H6Vvce7DTRzHF2oIrfxbhQrjIbLy6aMKlx9
# VW1NAQSXh2+nWmigsW4yH13ZSkteHpiB3f4lHnDQZEOySNHytHdtLzRWLDOqiV7S
# 2sxSC8tIK/a2tGeT6Zs8oH5kdP57OZoFp5IKk4x4i7YbhBf55nyMujoIAcBkxq8J
# dZeasIi7nEXcIhZLqikP5LBWxpL9Al73I5Dh1DBWY7FsN5MVK8S+gkCHWgej1n9l
# S6uTCZpIxv5LSThJoJFzJDrVtoaahAdGlnPfoCogFwTaF8P7NcrNEgS3akseMajM
# fmR8TbrHAtTjMQYYNU507ctZQ3j//4E1d25AFGbm9wqGr2HgPQEWzsvWYDm9hz1Z
# i7RP+X6VTDfLVtcVpT0bnIAhVaBaGrjhlLEozPOlfD2k1o30/L27p6sMdGpKCe6K
# BVYWeq33OOSdh8uYGgW5JaGCGBUwghgRBgorBgEEAYI3AwMBMYIYATCCF/0GCSqG
# SIb3DQEHAqCCF+4wghfqAgEDMQ0wCwYJYIZIAWUDBAICMIHOBgsqhkiG9w0BCRAB
# BKCBvgSBuzCBuAIBAQYLKoRoAYb2dwIFAQswMTANBglghkgBZQMEAgEFAAQgcjOw
# iALWQ/YR2so/gHzsdlISx0bxjOYwecNq5/mnyDACBwqofHOOG4cYDzIwMjYxMDEx
# MTgzNzIyWjADAgEBoFSkUjBQMQswCQYDVQQGEwJQTDEhMB8GA1UECgwYQXNzZWNv
# IERhdGEgU3lzdGVtcyBTLkEuMR4wHAYDVQQDDBVDZXJ0dW0gVGltZXN0YW1wIDIw
# MjagghMQMIIGgjCCBGqgAwIBAgIQKPB3wRw2vf5fdDJHcCcuAzANBgkqhkiG9w0B
# AQwFADBWMQswCQYDVQQGEwJQTDEhMB8GA1UEChMYQXNzZWNvIERhdGEgU3lzdGVt
# cyBTLkEuMSQwIgYDVQQDExtDZXJ0dW0gVGltZXN0YW1waW5nIDIwMjEgQ0EwHhcN
# MjYwMzExMDczNDU0WhcNMzYwMjI3MDczNDU0WjBQMQswCQYDVQQGEwJQTDEhMB8G
# A1UECgwYQXNzZWNvIERhdGEgU3lzdGVtcyBTLkEuMR4wHAYDVQQDDBVDZXJ0dW0g
# VGltZXN0YW1wIDIwMjYwggIiMA0GCSqGSIb3DQEBAQUAA4ICDwAwggIKAoICAQC4
# vc3wcPsSNdoYthfbjUATTkk5EyItd37odw4lsmyOd/dmwXycOrTu8w7O1Kj/63o1
# X+MRU1N1zTj7LjhZD+UjxbJ1sJ91SS/jYTYbLro72LELWkJ7zaNcJOOq3k4MLNS/
# DhRHSmsYMqk+MzkMfNZi8DWUqlzVPUa2/aACViReDMnc/gtXm7tA9Pr4DN7agEPE
# 1NtN36oCgj6iHR5aGhah/XskfvLZ6ZstoZ5zefWTrZrLh3kcH+YkzoZYX9Hf6qqr
# izkiBj4BlS9R4+VwVKMdjhO7UEa/pf8cTWy+jrsATuAGthG7sWIN+3KV7+Ytv5Uy
# +DQEgf3bn/VuH4wN0aPhj/mwueJ9jmIjNwqvM/dno8fBeR1jtnuP++XgveQfbTMP
# 3qu9dPRg8ltvtrreQZ9KPDFyUxEZMsrLVm6nJ1JKJgPe6n4gwsL6VrjjHX0zpLF3
# vUvsQ8TiWgm4sk4r4D7pcXE/rX0E0Vwa+VMLT73PiDZ2nzhIpc1RwNdbb/qR3Mpa
# 8sQXNPHFIoX/7bapGUtG5sd1cUGsvLqIWk6dP/xAgPb2BludUinSTCYV7ysiHQS8
# vFmDXIU7fMkSE7/QHcYPSahro4fk5z9W1t2kIxbZRJe0s4DXiPDlQPwiQFllLkU9
# wLvU6DqR8v35a65J4TeIXhMO/+iCzL+B0rI9GGeKLQIDAQABo4IBUDCCAUwwdQYI
# KwYBBQUHAQEEaTBnMDsGCCsGAQUFBzAChi9odHRwOi8vc3ViY2EucmVwb3NpdG9y
# eS5jZXJ0dW0ucGwvY3RzY2EyMDIxLmNlcjAoBggrBgEFBQcwAYYcaHR0cDovL3N1
# YmNhLm9jc3AtY2VydHVtLmNvbTAfBgNVHSMEGDAWgBS+VAIvv0Bsc0POrAklTp5D
# RBru4DAMBgNVHRMBAf8EAjAAMDkGA1UdHwQyMDAwLqAsoCqGKGh0dHA6Ly9zdWJj
# YS5jcmwuY2VydHVtLnBsL2N0c2NhMjAyMS5jcmwwFgYDVR0lAQH/BAwwCgYIKwYB
# BQUHAwgwDgYDVR0PAQH/BAQDAgeAMCIGA1UdIAQbMBkwCAYGZ4EMAQQCMA0GCyqE
# aAGG9ncCBQELMB0GA1UdDgQWBBQjOWooq5oSp6Sn8h/5RZq/jf6CAzANBgkqhkiG
# 9w0BAQwFAAOCAgEAZv6Tm6wL3k6mBTwkrYcTVcAR01uE37gUGNihgnTJR+gC41T2
# H9lwETRjE10nEdCUNv3Ni8S6aBx4UcMRX93fd9w4RUteua2I+oj0kCpuNa3PQDKU
# wozHZmotWivaPO9KTs08y0YVhVSMZvuUH/reFec9eLT6FLAFnX0Sg9Wc7833uQB5
# RPa8TmXBkTolC8bNxHX+2SfEWSNI8w/Y7o9qBF9l0uwVSrjQitVtIdZo2qb9NvMq
# qPFLWYef9SG+PGMqfPAQ2EoYeT0hlFLkubWF1X3HbiDwXtz3TU2EWHU+VTcZMC4I
# zMyHK/+kh9n3oxSQ45wsY71y7mqxKyYPyHSPoV3RiAQ1Thr9c65BeJLaELxWSKeQ
# yAGIdBsMlIXPO6qp+VGmE49vosogQ0jYe2hgq5LgJ6oO0Ie364V/kITBnifUqS6Z
# xu+qjfOYS8o72EeoEWIi04/BuH8Y6leNNP27j8NKwaPciWcqv/HBnQjizzmH/aqX
# n03K2SnjvaYPlCR8fFAAW2k6os0cZvLFEhMnHHAghYSIrdwCP98BrkjgA3ogOu+j
# XEO/nbD9OGL873Te6U2TcoOu9lqQQyXYNHiLR5eMqJrzXiDlodarOEKjlukyPDnL
# IuLkg0y6K8S1A5wU8CkM86cXdWFHLRIEYd9kdCfRZBZHhK7iVPeEZYxa9bgwgga5
# MIIEoaADAgECAhEA5/9pxzs1zkuRJth0fGilhzANBgkqhkiG9w0BAQwFADCBgDEL
# MAkGA1UEBhMCUEwxIjAgBgNVBAoTGVVuaXpldG8gVGVjaG5vbG9naWVzIFMuQS4x
# JzAlBgNVBAsTHkNlcnR1bSBDZXJ0aWZpY2F0aW9uIEF1dGhvcml0eTEkMCIGA1UE
# AxMbQ2VydHVtIFRydXN0ZWQgTmV0d29yayBDQSAyMB4XDTIxMDUxOTA1MzIwN1oX
# DTM2MDUxODA1MzIwN1owVjELMAkGA1UEBhMCUEwxITAfBgNVBAoTGEFzc2VjbyBE
# YXRhIFN5c3RlbXMgUy5BLjEkMCIGA1UEAxMbQ2VydHVtIFRpbWVzdGFtcGluZyAy
# MDIxIENBMIICIjANBgkqhkiG9w0BAQEFAAOCAg8AMIICCgKCAgEA6RIfBDXtuV16
# xaaVQb6KZX9Od9FtJXXTZo7b+GEof3+3g0ChWiKnO7R4+6MfrvLyLCWZa6GpFHjE
# t4t0/GiUQvnkLOBRdBqr5DOvlmTvJJs2X8ZmWgWJjC7PBZLYBWAs8sJl3kNXxBMX
# 5XntjqWx1ZOuuXl0R4x+zGGSMzZ45dpvB8vLpQfZkfMC/1tL9KYyjU+htLH68dZJ
# PtzhqLBVG+8ljZ1ZFilOKksS79epCeqFSeAUm2eMTGpOiS3gfLM6yvb8Bg6bxg5y
# glDGC9zbr4sB9ceIGRtCQF1N8dqTgM/dSViiUgJkcv5dLNJeWxGCqJYPgzKlYZTg
# DXfGIeZpEFmjBLwURP5ABsyKoFocMzdjrCiFbTvJn+bD1kq78qZUgAQGGtd6zGJ8
# 8H4NPJ5Y2R4IargiWAmv8RyvWnHr/VA+2PrrK9eXe5q7M88YRdSTq9TKbqdnITUg
# Zcjjm4ZUjteq8K331a4P0s2in0p3UubMEYa/G5w6jSWPUzchGLwWKYBfeSu6dIOC
# 4LkeAPvmdZxSB1lWOb9HzVWZoM8Q/blaP4LWt6JxjkI9yQsYGMdCqwl7uMnPUIlc
# ExS1mzXRxUowQref/EPaS7kYVaHHQrp4XB7nTEtQhkP0Z9Puz/n8zIFnUSnxDof4
# Yy650PAXSYmK2TcbyDoTNmmt8xAxzcMCAwEAAaOCAVUwggFRMA8GA1UdEwEB/wQF
# MAMBAf8wHQYDVR0OBBYEFL5UAi+/QGxzQ86sCSVOnkNEGu7gMB8GA1UdIwQYMBaA
# FLahVDkCw6A/joq8+tT4HKbROg79MA4GA1UdDwEB/wQEAwIBBjATBgNVHSUEDDAK
# BggrBgEFBQcDCDAwBgNVHR8EKTAnMCWgI6Ahhh9odHRwOi8vY3JsLmNlcnR1bS5w
# bC9jdG5jYTIuY3JsMGwGCCsGAQUFBwEBBGAwXjAoBggrBgEFBQcwAYYcaHR0cDov
# L3N1YmNhLm9jc3AtY2VydHVtLmNvbTAyBggrBgEFBQcwAoYmaHR0cDovL3JlcG9z
# aXRvcnkuY2VydHVtLnBsL2N0bmNhMi5jZXIwOQYDVR0gBDIwMDAuBgRVHSAAMCYw
# JAYIKwYBBQUHAgEWGGh0dHA6Ly93d3cuY2VydHVtLnBsL0NQUzANBgkqhkiG9w0B
# AQwFAAOCAgEAuJNZd8lMFf2UBwigp3qgLPBBk58BFCS3Q6aJDf3TISoytK0eal/J
# yCB88aUEd0wMNiEcNVMbK9j5Yht2whaknUE1G32k6uld7wcxHmw67vUBY6pSp8Qh
# dodY4SzRRaZWzyYlviUpyU4dXyhKhHSncYJfa1U75cXxCe3sTp9uTBm3f8Bj8Lkp
# jMUSVTtMJ6oEu5JqCYzRfc6nnoRUgwz/GVZFoOBGdrSEtDN7mZgcka/tS5MI47fA
# LVvN5lZ2U8k7Dm/hTX8CWOw0uBZloZEW4HB0Xra3qE4qzzq/6M8gyoU/DE0k3+i7
# bYOrOk/7tPJg1sOhytOGUQ30PbG++0FfJioDuOFhj99b151SqFlSaRQYz74y/P2X
# JP+cF19oqozmi0rRTkfyEJIvhIZ+M5XIFZttmVQgTxfpfJwMFFEoQrSrklOxpmSy
# gppsUDJEoliC05vBLVQ+gMZyYaKvBJ4YxBMlKH5ZHkRdloRYlUDplk8GUa+OCMVh
# pDSQurU6K1ua5dmZftnvSSz2H96UrQDzA6DyiI1V3ejVtvn2azVAXg6NnjmuRZ+w
# a7Pxy0H3+V4K4rOTHlG3VYA6xfLsTunCz72T6Ot4+tkrDYOeaU1pPX1CBfYj6EW2
# +ELq46GP8KCNUQDirWLU4nOmgCat7vN0SD6RlwUiSsMeCiQDmZwgwrUwggXJMIIE
# saADAgECAhAbtY8lKt8jAEkoya49fu0nMA0GCSqGSIb3DQEBDAUAMH4xCzAJBgNV
# BAYTAlBMMSIwIAYDVQQKExlVbml6ZXRvIFRlY2hub2xvZ2llcyBTLkEuMScwJQYD
# VQQLEx5DZXJ0dW0gQ2VydGlmaWNhdGlvbiBBdXRob3JpdHkxIjAgBgNVBAMTGUNl
# cnR1bSBUcnVzdGVkIE5ldHdvcmsgQ0EwHhcNMjEwNTMxMDY0MzA2WhcNMjkwOTE3
# MDY0MzA2WjCBgDELMAkGA1UEBhMCUEwxIjAgBgNVBAoTGVVuaXpldG8gVGVjaG5v
# bG9naWVzIFMuQS4xJzAlBgNVBAsTHkNlcnR1bSBDZXJ0aWZpY2F0aW9uIEF1dGhv
# cml0eTEkMCIGA1UEAxMbQ2VydHVtIFRydXN0ZWQgTmV0d29yayBDQSAyMIICIjAN
# BgkqhkiG9w0BAQEFAAOCAg8AMIICCgKCAgEAvfl4+ObVgAxknYYblmRnPyI6HnUB
# fe/7XGeMycxca6mR5rlC5SBLm9qbe7mZXdmbgEvXhEArJ9PoujC7Pgkap0mV7ytA
# JMKXx6fumyXvqAoAl4Vaqp3cKcniNQfrcE1K1sGzVrihQTib0fsxf4/gX+GxPw+O
# Fklg1waNGPmqJhCrKtPQ0WeNG0a+RzDVLnLRxWPa52N5RH5LYySJhi40PylMUosq
# p8DikSiJucBb+R3Z5yet/5oCl8HGUJKbAiy9qbk0WQq/hEr/3/6zn+vZnuCYI+ym
# a3cWKtvMrTscpIfcRnNeGWJoRVfkkIJCu0LW8GHgwaM9ZqNd9BjuiMmNF0UpmTJ1
# AjHuKSbIawLmtWJFfzcVWiNoidQ+3k4nsPBADLxNF8tNorMe0AZa3faTz1d1mfX6
# hhpneLO/lv403L3nUlbls+V1e9dBkQXcXWnjlQ1DufyDljmVe2yAWk8TcsbXfSl6
# RLpSpCrVQUYJIP4ioLZbMI28iQzV13D4h1L92u+sUS4Hs07+0AnacO+Y+lbmbdu1
# V0vc5SwlFcieLnhO+NqcnoYsylfzGuXIkosagpZ6w7xQEmnYDlpGizrrJvojybaw
# gb5CAKT41v4wLsfSRvbljnX98sy50IdbzAYQYLuDNbdeZ95H7JlI8aShFf6tjGKO
# OVVPORa5sWOd/7cCAwEAAaOCAT4wggE6MA8GA1UdEwEB/wQFMAMBAf8wHQYDVR0O
# BBYEFLahVDkCw6A/joq8+tT4HKbROg79MB8GA1UdIwQYMBaAFAh2zcsH/yT2xc3t
# u5C84oQ3RnX3MA4GA1UdDwEB/wQEAwIBBjAvBgNVHR8EKDAmMCSgIqAghh5odHRw
# Oi8vY3JsLmNlcnR1bS5wbC9jdG5jYS5jcmwwawYIKwYBBQUHAQEEXzBdMCgGCCsG
# AQUFBzABhhxodHRwOi8vc3ViY2Eub2NzcC1jZXJ0dW0uY29tMDEGCCsGAQUFBzAC
# hiVodHRwOi8vcmVwb3NpdG9yeS5jZXJ0dW0ucGwvY3RuY2EuY2VyMDkGA1UdIAQy
# MDAwLgYEVR0gADAmMCQGCCsGAQUFBwIBFhhodHRwOi8vd3d3LmNlcnR1bS5wbC9D
# UFMwDQYJKoZIhvcNAQEMBQADggEBAFHCoVgWIhCL/IYx1MIy01z4S6Ivaj5N+KsI
# Hu3V6PrnCA3st8YeDrJ1BXqxC/rXdGoABh+kzqrya33YEcARCNQOTWHFOqj6seHj
# mOriY/1B9ZN9DbxdkjuRmmW60F9MvkyNaAMQFtXx0ASKhTP5N+dbLiZpQjy6zbzU
# eulNndrnQ/tjUoCFBMQllVXwfqefAcVbKPjgzoZwpic7Ofs4LphTZSJ1Ldf23SIi
# kZbr3WjtP6MZl9M7JYjsNhI9qX7OAo0FmpKnJ25FspxihjcNpDOO16hO0EoXQ0zF
# 8ads0h5YbBRRfopUofbvn3l6XYGaFpAP4bvxSgD5+d2+7arszgoxggPvMIID6wIB
# ATBqMFYxCzAJBgNVBAYTAlBMMSEwHwYDVQQKExhBc3NlY28gRGF0YSBTeXN0ZW1z
# IFMuQS4xJDAiBgNVBAMTG0NlcnR1bSBUaW1lc3RhbXBpbmcgMjAyMSBDQQIQKPB3
# wRw2vf5fdDJHcCcuAzANBglghkgBZQMEAgIFAKCCAVYwGgYJKoZIhvcNAQkDMQ0G
# CyqGSIb3DQEJEAEEMBwGCSqGSIb3DQEJBTEPFw0yNjEwMTExODM3MjJaMDcGCyqG
# SIb3DQEJEAIvMSgwJjAkMCIEIIW+kOEK0kONfMkotq9IsJqyCBd87PiwEmxY05EF
# JcQ8MD8GCSqGSIb3DQEJBDEyBDBy5DY90dYdVkK0TMdXHxsL4r5q0J7183xdJ7DU
# u/fDdtxBvO3B6Sq4NZrlSzUkTgUwgZ8GCyqGSIb3DQEJEAIMMYGPMIGMMIGJMIGG
# BBRXFGhBDKha80JO+RZKUTYQ9NONmDBuMFqkWDBWMQswCQYDVQQGEwJQTDEhMB8G
# A1UEChMYQXNzZWNvIERhdGEgU3lzdGVtcyBTLkEuMSQwIgYDVQQDExtDZXJ0dW0g
# VGltZXN0YW1waW5nIDIwMjEgQ0ECECjwd8EcNr3+X3QyR3AnLgMwDQYJKoZIhvcN
# AQEBBQAEggIAVtxfsEh2toE9nofwM5E6AcglO0anZsdnJk2u4W/2o5bJJMOc3hIo
# +9IwAW8gzs4BatJrsgVrLZ3M3dQP0YRSpVP6dAnvec9R0jr8gwh44slDpfczMiXE
# Unha2TPnibOgxpGy0PZbk+z3hzrdudj3b+0dmv0iPU361b64QjS1s18/xSJhJIcM
# /qbXWJg9yD+cPs3FuATF9U5OsmZ2jzcgPiUHKI3x/K3xyUoGo/blh2LqERs5DWci
# 3lpGqluXQZsBNMCipCvAmdR+aYMWeYBj9yL+KOr1y63u39wQiqVf6E7wTPvLxL0L
# Kl4qD8Hv2e+ILgna913Yn2X3bKd1lLQfX0rmV/f+gS5nfeeV0sXqqo0+vOQRDqbf
# vuxCnoYtQHe9oBPRj63mZeG94gaolDgs4Bxgc/xSjqXb/zYB3r7pIToUTnQZ5AMk
# OPcVg0ZBe3nrAdCQmJQBPcTlIGVnAI3eg9A4hkTIQXx7B1pCR25oKER6Jv1MwRdP
# C3+HH8cL2bksMb003VlWT5qcj4e+yg3ZRn1PxYp7EmsyDDv1d+Pn1Jhm/3oTbyIY
# IYlzkz9iH0lFsywVv+8/qryHvBiOaxaLTtmi//xt5750O5YVpU61Ae/pxxUiXeW2
# lHaA2Q/PnHeJK/Yrdv3uKdThWR3GaTC6r4gp73sLrd+4XCK4e33q2pc=
# SIG # End signature block
