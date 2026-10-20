# Publishes a release built by Build-Release.ps1 (docs\guides\releasing.md) as a GitHub release of Ayrton09/Pulsar, with the
# GitHub CLI (gh, signed in as a maintainer: gh auth login; no token passes through this script or its command lines):
#   .\scripts\release\Publish-GitHubRelease.ps1 -Version 1.0.1
# It uploads the files the signed manifest names, their .sha256 files, pulsar-update.json and pulsar-update.json.sig as a
# normal release (not a draft, not a pre-release, the latest one): that is what installed panels and agents find as the
# newest update, so nothing is uploaded until everything checks out:
#  - gh is installed, and the repository is public (installations download releases without signing in; -AllowPrivate for a
#    private repository, which no installation can then update from);
#  - the folder holds the manifest and its signature for this -Version, and the signature verifies against the keys built
#    into the programs (pulsar-update-signer verify: a release signed by a key they do not trust would be refused by every
#    installation);
#  - every file the manifest names is there with the size and SHA-256 it says, and the checksum files agree;
#  - the release v<version> and its tag do not exist yet (a version is published once; a mistake gets a new version).
# The tag is made by GitHub at the commit being published (-Target; by default this checkout's HEAD, which must be pushed). The
# release workflow does not start on that tag (.github\workflows\release.yml is run by hand only): releases are built and
# update-signed on the maintainer's PC, where the key stays.
param(
    # The version being published, exactly major.minor.patch; the folder's manifest must be for it.
    [Parameter(Mandatory)] [ValidatePattern('^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\z')] [string] $Version,
    # Where Build-Release.ps1 put the release.
    [string] $Folder = (Join-Path $PSScriptRoot '..\..\artifacts\release'),
    # Release notes (a text file) for the GitHub release; without it, the manifest's notes.
    [string] $Notes,
    # The commit the release's tag is made at (a commit of the repository on GitHub); default: this checkout's HEAD.
    [string] $Target,
    # pulsar-update-signer.exe, built already; otherwise it is built from this checkout into a temporary folder.
    [string] $Signer,
    # Publishes although the repository is private. Installations download without signing in, so none could update.
    [switch] $AllowPrivate,
    # Runs every check and uploads nothing.
    [switch] $CheckOnly)
$ErrorActionPreference = 'Stop'

# The repository releases come from: the one UpdateRelease.Repository (src\Pulsar.Contracts\Updates.cs) names, and the only
# one installations take updates from.
$Repository = 'Ayrton09/Pulsar'
$UpdateManifestName = 'pulsar-update.json'
$UpdateSignatureName = 'pulsar-update.json.sig'

# Runs gh with $arguments (words and paths, no secret); its exit code and its output, standard error included.
function Invoke-Gh([string[]] $arguments) {
    $ErrorActionPreference = 'Continue'
    $output = (& gh @arguments 2>&1 | ForEach-Object { "$_" }) -join [Environment]::NewLine
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
}

function Test-GhInstalled { return $null -ne (Get-Command gh -CommandType Application -ErrorAction SilentlyContinue) }

# Runs the signer's verify on a manifest (its signature next to it): checked against the keys built into the programs, as every
# installation does. Throws why it does not.
function Assert-ManifestVerifies([string] $signer, [string] $manifest) {
    $ErrorActionPreference = 'Continue'
    $output = (& $signer verify $manifest 2>&1 | ForEach-Object { "$_" }) -join [Environment]::NewLine
    if ($LASTEXITCODE -ne 0) { throw "The signature of $manifest does not verify against the keys Pulsar trusts: $output" }
}

# pulsar-update-signer.exe: -Signer, or the one built from this checkout into $folder.
function Get-UpdateSigner([string] $path, [string] $folder) {
    if ($path) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "-Signer must name pulsar-update-signer.exe ($path)." }
        return (Resolve-Path -LiteralPath $path).Path
    }
    $repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
    dotnet publish (Join-Path $repo 'tools\Pulsar.UpdateSigner') -c Release -r win-x64 --no-self-contained -p:DebugType=none -p:RestoreLockedMode=true -o $folder | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'publish of the update signer failed' }
    return Join-Path $folder 'pulsar-update-signer.exe'
}

# The checks on the release folder: returns the manifest and the files to upload (full paths, the manifest's files first), or
# throws what is wrong. The signature is verified before the manifest is read at all.
function Test-ReleaseFolder([string] $folder, [string] $version, [string] $signer) {
    $manifestPath = Join-Path $folder $UpdateManifestName
    foreach ($required in $manifestPath, "$manifestPath.sig") {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "$required is missing: build the release with Build-Release.ps1 -Version $version -UpdateSigningKey <key>." }
    }
    Assert-ManifestVerifies $signer $manifestPath
    $manifest = [IO.File]::ReadAllText($manifestPath, (New-Object Text.UTF8Encoding($false, $true))) | ConvertFrom-Json
    if ($manifest.version -cne $version) { throw "The manifest is for version $($manifest.version), not $version." }
    $upload = @()
    foreach ($file in @($manifest.files)) {
        $path = Join-Path $folder $file.name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "$($file.name), named by the manifest, is not in $folder." }
        $size = (Get-Item -LiteralPath $path).Length
        if ($size -ne $file.size) { throw "$($file.name) is $size bytes; the manifest says $($file.size)." }
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -cne $file.sha256) { throw "$($file.name) has SHA-256 $hash; the manifest says $($file.sha256)." }
        $upload += $path
        $checksum = "$path.sha256"
        if (Test-Path -LiteralPath $checksum -PathType Leaf) {
            $line = (Get-Content -LiteralPath $checksum -TotalCount 1)
            if ($line -cnotmatch '^([0-9a-f]{64})  (.+)$' -or $Matches[1] -cne $hash -or $Matches[2] -cne $file.name) { throw "$checksum does not say what the manifest says about $($file.name) (SHA-256 $hash)." }
            $upload += $checksum
        }
    }
    return [pscustomobject]@{ Manifest = $manifest; Files = @($upload + $manifestPath + "$manifestPath.sig") }
}

# What must be true on GitHub before the upload: the repository is public (or -AllowPrivate says it may not be) and neither the
# release nor its tag exist. Throws what is not.
function Assert-GitHubReady([string] $repository, [string] $tag, [bool] $allowPrivate) {
    if (-not (Test-GhInstalled)) { throw 'The GitHub CLI (gh) is not installed or not on the path: https://cli.github.com, then gh auth login.' }
    $visibility = Invoke-Gh @('repo', 'view', $repository, '--json', 'visibility')
    if ($visibility.ExitCode -ne 0) { throw "gh could not read $repository (sign in with gh auth login): $($visibility.Output)" }
    $state = ($visibility.Output | ConvertFrom-Json).visibility
    if ($state -cne 'PUBLIC' -and -not $allowPrivate) {
        throw "$repository is $state. Installations download releases without signing in, so none of them could update from it: make the repository public, or give -AllowPrivate to publish anyway."
    }
    $release = Invoke-Gh @('release', 'view', $tag, '--repo', $repository)
    if ($release.ExitCode -eq 0) { throw "The release $tag exists already. A version is published once: fix the problem in a new version." }
    if ($release.Output -notmatch 'release not found') { throw "gh could not look for the release $tag ($($release.Output))." }
    $reference = Invoke-Gh @('api', "repos/$repository/git/ref/tags/$tag")
    if ($reference.ExitCode -eq 0) { throw "The tag $tag exists already in $repository." }
    if ($reference.Output -notmatch 'Not Found') { throw "gh could not look for the tag $tag ($($reference.Output))." }
}

$tag = "v$Version"
$Folder = (Resolve-Path -LiteralPath $Folder).Path
$work = Join-Path ([IO.Path]::GetTempPath()) ('pulsar-publish-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory $work
try {
    $signerPath = Get-UpdateSigner $Signer (Join-Path $work 'update-signer')
    $release = Test-ReleaseFolder $Folder $Version $signerPath
    Assert-GitHubReady $Repository $tag ([bool]$AllowPrivate)

    # The notes: the given file, or the manifest's, under a title.
    $notesFile = $Notes
    if ($Notes) {
        if (-not (Test-Path -LiteralPath $Notes -PathType Leaf)) { throw "-Notes must name an existing text file ($Notes)." }
    }
    else {
        $notesFile = Join-Path $work 'notes.md'
        $text = "Pulsar $Version"
        if ($release.Manifest.notes) { $text += "$([Environment]::NewLine)$([Environment]::NewLine)$($release.Manifest.notes)" }
        [IO.File]::WriteAllText($notesFile, $text, (New-Object Text.UTF8Encoding $false))
    }

    if (-not $Target) {
        $head = (& git -C $PSScriptRoot rev-parse HEAD 2>$null)
        if ($LASTEXITCODE -ne 0 -or "$head" -cnotmatch '^[0-9a-f]{40}\z') { throw 'The commit of the release could not be found: give -Target (a commit of the repository on GitHub).' }
        $Target = "$head"
    }
    elseif ($Target -cnotmatch '^[0-9A-Za-z._/-]{1,100}\z') { throw "-Target is not a commit or a branch name ($Target)." }

    Write-Host "Publishing $tag of $Repository at $Target with $($release.Files.Count) file(s):"
    foreach ($file in $release.Files) { Write-Host "  $(Split-Path $file -Leaf)" }
    if ($CheckOnly) {
        Write-Host 'Everything checks out (-CheckOnly: nothing was uploaded).'
        return
    }
    $created = Invoke-Gh (@('release', 'create', $tag) + $release.Files + @('--repo', $Repository, '--title', "Pulsar $Version", '--notes-file', $notesFile, '--target', $Target, '--latest'))
    if ($created.ExitCode -ne 0) { throw "gh release create failed: $($created.Output)" }
    Write-Host $created.Output
    Write-Host "Published $tag."
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
