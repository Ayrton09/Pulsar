<#
.SYNOPSIS
    Backs up the Pulsar panel installed on this machine: its database, its keys and certificates, and its settings.

.DESCRIPTION
    Run it in an elevated PowerShell on the panel's machine, from the copy Install-Pulsar.ps1 keeps in the panel's folder
    (C:\Program Files\Pulsar\Panel): it refuses to run from a folder other accounts could change. It writes a folder
    pulsar-backup-<date> under -Destination with:
      pulsar.dump                PostgreSQL dump of the panel's database (pg_dump custom format)
      panel-data\                the panel's Data Protection keys, key ring certificate and HTTPS certificate
      appsettings.Production.json the panel's settings (database and certificate passwords)

    The database's secrets (game server tokens, two-factor keys, the panel's signing key) can only be read with the
    keys, so the three always go together. The folder holds secrets: keep it as private as the panel itself (it is
    readable only by SYSTEM and Administrators, so -Destination must be on a drive that keeps permissions: NTFS or
    ReFS, not FAT or exFAT) and store a copy away from this machine. Only SYSTEM, Administrators and TrustedInstaller
    may change -Destination or rename or replace a folder above it; one that does not exist yet is made here for
    SYSTEM and Administrators alone.

    Restore with: Install-Pulsar.ps1 -Mode Panel -RestoreFrom <the backup folder> (docs/guides/backup.md).
    Game servers' own backups stay on their nodes (C:\ProgramData\Pulsar\Agent\backups); copy them separately.

.EXAMPLE
    & 'C:\Program Files\Pulsar\Panel\Backup-Pulsar.ps1' -Destination D:\PulsarBackups
#>
param(
    [Parameter(Mandatory)] [string] $Destination,
    # Folder with pg_dump.exe, when PostgreSQL does not run as a service on this machine.
    [string] $PostgresBin,
    [string] $InstallRoot = (Join-Path $env:ProgramFiles 'Pulsar'),
    [string] $DataRoot = (Join-Path $env:ProgramData 'Pulsar')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script in an elevated PowerShell (right-click > Run as administrator).'
}

# This script runs elevated (a nightly task runs it as SYSTEM): whoever else could change it before it runs would get
# SYSTEM and the panel's secrets. It runs only where nobody but SYSTEM, Administrators and TrustedInstaller may own or
# modify it and its folder, or rename or replace a folder above it: the copy Install-Pulsar.ps1 keeps in the panel's
# folder (C:\Program Files\Pulsar\Panel), or one in a folder made like that. The same functions as Install-Pulsar.ps1's.
# The check lives in the script it protects: it stops a copy left where others can write, but not someone who already
# rewrote this script. The control is where the task runs it from: the panel's folder, which only administrators can change.
$TrustedWriters = @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
# Write data and append data (in a folder: create files and folders), write extended attributes, delete children, write
# attributes, delete, change permissions, take ownership, and the generic rights (all, write) that include them.
$ModifyRights = 0x2 -bor 0x4 -bor 0x10 -bor 0x40 -bor 0x100 -bor 0x10000 -bor 0x40000 -bor 0x80000 -bor 0x10000000 -bor 0x40000000
# On a folder above: what lets someone rename or replace it (delete children, delete, change permissions, take
# ownership, generic all). A drive's root cannot be renamed or deleted, so "delete" is harmless there.
$ReplaceRights = 0x40 -bor 0x10000 -bor 0x40000 -bor 0x80000 -bor 0x10000000

function Get-AccountName([Security.Principal.SecurityIdentifier] $sid) {
    try { return $sid.Translate([Security.Principal.NTAccount]).Value } catch { return $sid.Value }
}

# What lets anyone but the trusted accounts change one file or folder with these rights; nothing when nobody can.
function Find-UntrustedWriter([IO.FileSystemInfo] $item, [int] $rights) {
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return "$($item.FullName) is a link" }
    $acl = Get-Acl -LiteralPath $item.FullName
    # The owner can always change the permissions.
    $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier])
    if ($owner.Value -notin $TrustedWriters) { "$($item.FullName) is owned by $(Get-AccountName $owner)" }
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -ne 'Allow' -or $rule.IdentityReference.Value -in $TrustedWriters) { continue }
        # Inherit-only entries (CREATOR OWNER, say) apply to what is created inside it later, by whoever may create there,
        # which this check already looks at; inherited entries count like any other.
        if (([int]$rule.PropagationFlags -band [int][Security.AccessControl.PropagationFlags]::InheritOnly) -ne 0) { continue }
        if (([int]$rule.FileSystemRights -band $rights) -ne 0) { "$(Get-AccountName $rule.IdentityReference) can change $($item.FullName)" }
    }
}

# Who besides SYSTEM, Administrators and TrustedInstaller could change $path (a folder: everything in it), or rename or
# replace the folders above it.
function Find-UntrustedWriters([string] $path) {
    $item = Get-Item -LiteralPath $path -Force
    $folder = $item
    if ($item -is [IO.FileInfo]) {
        Find-UntrustedWriter $item $ModifyRights
        $folder = $item.Directory
        Find-UntrustedWriter $folder $ModifyRights
    }
    else {
        # Not through links (refused above).
        $pending = New-Object Collections.Generic.Stack[IO.FileSystemInfo]
        $pending.Push($item)
        while ($pending.Count -gt 0) {
            $current = $pending.Pop()
            Find-UntrustedWriter $current $ModifyRights
            if ($current -is [IO.DirectoryInfo] -and ($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
                foreach ($child in $current.GetFileSystemInfos()) { $pending.Push($child) }
            }
        }
    }
    Find-UntrustedAncestors $folder
}

# Who besides SYSTEM, Administrators and TrustedInstaller could rename or replace the folders above $folder.
function Find-UntrustedAncestors([IO.DirectoryInfo] $folder) {
    for ($ancestor = $folder.Parent; $null -ne $ancestor; $ancestor = $ancestor.Parent) {
        $rights = $ReplaceRights
        if ($null -eq $ancestor.Parent) { $rights = $ReplaceRights -band (-bnot 0x10000) }
        Find-UntrustedWriter $ancestor $rights
    }
}

# Who besides SYSTEM, Administrators and TrustedInstaller could change the destination (create, rename or replace what
# is in it) or rename or replace a folder above it: the backup's secrets are written there by this script, after
# pg_dump has run for a while, and a folder someone renamed away and replaced meanwhile would get them. For a
# destination that does not exist yet, the folders above it (New-PrivateFolder makes it). Permissions that cannot be
# read (a drive without them) count as anyone's.
function Find-UntrustedDestination([string] $path) {
    try {
        $folder = New-Object IO.DirectoryInfo $path
        if ($folder.Exists) { Find-UntrustedWriter $folder $ModifyRights }
        elseif ($null -eq $folder.Parent -or -not $folder.Parent.Exists) { return "$($folder.Parent) does not exist" }
        Find-UntrustedAncestors $folder
    }
    catch {
        "the permissions of $path or a folder above it cannot be read ($($_.Exception.Message))"
    }
}

# A new folder, SYSTEM's and Administrators' alone from the moment it exists: made first and restricted then, it would
# have the permissions of the folder above it meanwhile, and whoever opened it then would keep that access. One that
# already exists is refused: someone else may have made it.
function New-PrivateFolder([string] $path) {
    if (Test-Path -LiteralPath $path) { throw "$path already exists: someone else may have made it. Nothing was backed up." }
    $security = New-Object Security.AccessControl.DirectorySecurity
    $security.SetAccessRuleProtection($true, $false)
    foreach ($sid in 'S-1-5-18', 'S-1-5-32-544') {
        $security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier $sid), 'FullControl', 'ContainerInherit, ObjectInherit', 'None', 'Allow')))
    }
    $null = [IO.Directory]::CreateDirectory($path, $security)
}

# Whether a folder is as New-PrivateFolder makes it: not a link, owned by SYSTEM or Administrators, nothing inherited
# and nobody else allowed in. Made by someone else meanwhile, or on a drive that keeps no permissions (FAT, exFAT, a
# share that ignores them), it is not.
function Test-PrivateFolder([string] $path) {
    try {
        $item = Get-Item -LiteralPath $path -Force
        if ($item -isnot [IO.DirectoryInfo] -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return $false }
        $acl = Get-Acl -LiteralPath $path
        $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier])
        if ($null -eq $owner -or $owner.Value -notin 'S-1-5-18', 'S-1-5-32-544') { return $false }
        $allowed = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) | Where-Object { $_.AccessControlType -eq 'Allow' } |
            ForEach-Object { $_.IdentityReference.Value } | Select-Object -Unique)
        return $acl.AreAccessRulesProtected -and $allowed.Count -gt 0 -and @($allowed | Where-Object { $_ -notin 'S-1-5-18', 'S-1-5-32-544' }).Count -eq 0
    }
    catch {
        return $false
    }
}

$findings = @(Find-UntrustedWriters $PSCommandPath)
if ($findings.Count -gt 0) {
    $shown = @($findings | Select-Object -First 10)
    $more = ''
    if ($findings.Count -gt $shown.Count) { $more = "$([Environment]::NewLine)  ... and $($findings.Count - $shown.Count) more" }
    throw "This script runs as administrator (a nightly task runs it as SYSTEM), so it refuses to run from where other accounts could change it (a game server's account could rewrite it and take over the machine). Here, ${PSCommandPath}:$([Environment]::NewLine)  $($shown -join "$([Environment]::NewLine)  ")$more$([Environment]::NewLine)Run the copy the installer keeps in the panel's folder, $(Join-Path $InstallRoot 'Panel\Backup-Pulsar.ps1') (docs/guides/backup.md). Nothing was backed up."
}

$config = Join-Path $InstallRoot 'Panel\appsettings.Production.json'
$panelData = Join-Path $DataRoot 'Panel'
if (-not (Test-Path $config)) { throw "The panel is not installed here ($config not found)." }

# pg_dump runs as administrator here (as SYSTEM in the nightly task): one that other accounts could change, or that
# sits in a folder they could add files to, would run their code as it. Refused, as Install-Pulsar.ps1 refuses such a
# psql.exe, before it is run at all (its --version too).
function Assert-TrustedPgDump([string] $bin) {
    $pgDump = Join-Path $bin 'pg_dump.exe'
    $findings = @(Find-UntrustedWriters $pgDump)
    if ($findings.Count -gt 0) {
        $shown = @($findings | Select-Object -First 10)
        throw "This script runs PostgreSQL's pg_dump.exe as administrator (a nightly task runs it as SYSTEM), and $pgDump could be changed by other accounts:$([Environment]::NewLine)  $($shown -join "$([Environment]::NewLine)  ")$([Environment]::NewLine)Install PostgreSQL in a folder only administrators can change (its installer's default, in Program Files), or pass -PostgresBin with such a folder. Nothing was backed up."
    }
}

# pg_dump must be as new as the server: the newest PostgreSQL installed here.
if ($PostgresBin) {
    if (-not (Test-Path -LiteralPath (Join-Path $PostgresBin 'pg_dump.exe') -PathType Leaf)) { throw "$PostgresBin has no pg_dump.exe. Nothing was backed up." }
    Assert-TrustedPgDump $PostgresBin
}
else {
    $newest = 0
    foreach ($service in Get-CimInstance Win32_Service -Filter "Name LIKE 'postgresql%'") {
        if ($service.PathName -match '^"?([^"]+\\bin)\\pg_ctl\.exe') {
            $bin = $Matches[1]
            if (-not (Test-Path -LiteralPath (Join-Path $bin 'pg_dump.exe') -PathType Leaf)) { continue }
            Assert-TrustedPgDump $bin
            $version = (& (Join-Path $bin 'pg_dump.exe') --version) -join ' '
            if ($version -match '(\d+)\.\d+' -and [int]$Matches[1] -gt $newest) {
                $newest = [int]$Matches[1]
                $PostgresBin = $bin
            }
        }
    }
    if (-not $PostgresBin) { throw 'pg_dump.exe was not found: pass the folder of PostgreSQL''s programs with -PostgresBin.' }
}

# The destination: one that only SYSTEM, Administrators and TrustedInstaller can change, rename or replace (made here,
# for SYSTEM and Administrators alone, when it does not exist yet), on a drive that keeps permissions.
$Destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Destination)
function Assert-TrustedDestination {
    $findings = @(Find-UntrustedDestination $Destination)
    if ($findings.Count -gt 0) {
        $shown = @($findings | Select-Object -First 10)
        throw "The backup holds the panel's secrets, so it is written only where nobody but SYSTEM, Administrators and TrustedInstaller can change the destination or rename or replace a folder above it (someone who could would get the keys). Here:$([Environment]::NewLine)  $($shown -join "$([Environment]::NewLine)  ")$([Environment]::NewLine)Give -Destination a folder that does not exist yet, on an NTFS or ReFS drive, in a folder only administrators can change or directly on the drive (D:\PulsarBackups, say): this script makes it for SYSTEM and Administrators only (docs/guides/backup.md). Nothing was backed up."
    }
}
Assert-TrustedDestination
if (-not (Test-Path -LiteralPath $Destination)) {
    New-PrivateFolder $Destination
    # Made by someone else in the meantime, it is theirs: refused.
    Assert-TrustedDestination
}

# A folder made here, never one that already exists, for SYSTEM and Administrators from the start: the backup holds
# the panel's secrets. Checked, as a drive without permissions (FAT, exFAT, some network shares) takes the folder and
# drops its permissions.
$target = Join-Path $Destination ("pulsar-backup-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-PrivateFolder $target
if (-not (Test-PrivateFolder $target)) {
    Remove-Item -LiteralPath $target -Recurse -Force
    throw "$Destination cannot keep the backup readable by SYSTEM and Administrators only: the folder made there did not keep the permissions and owner it was made with (a drive without permissions, such as FAT or exFAT, or a share that ignores them). Choose a folder on an NTFS or ReFS drive. Nothing was backed up."
}

Write-Host '==> Dumping the database' -ForegroundColor Cyan
$settings = Get-Content $config -Raw | ConvertFrom-Json
$parts = @{}
foreach ($part in $settings.ConnectionStrings.Pulsar.Split(';')) {
    if ($part -match '^\s*([^=]+?)\s*=\s*(.*)$') { $parts[$Matches[1].ToLowerInvariant()] = $Matches[2] }
}
$port = '5432'
if ($parts.ContainsKey('port')) { $port = $parts['port'] }
$env:PGPASSWORD = $parts['password']
try {
    $ErrorActionPreference = 'Continue'
    $output = & (Join-Path $PostgresBin 'pg_dump.exe') -h $parts['host'] -p $port -U $parts['username'] -d $parts['database'] -Fc -f (Join-Path $target 'pulsar.dump') 2>&1
    $ErrorActionPreference = 'Stop'
    if ($LASTEXITCODE -ne 0) { throw "pg_dump failed: $output" }
}
finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
}

Write-Host '==> Copying the keys, certificates and settings' -ForegroundColor Cyan
# Checked again before the secrets go in: the backup folder and where it is must still be as they were made.
Assert-TrustedDestination
if (-not (Test-PrivateFolder $target)) { throw "$target changed while the database was dumped: the keys, certificates and settings were not copied." }
# Not -Force: a panel-data folder already there is not this script's, and is refused.
$data = New-Item -ItemType Directory (Join-Path $target 'panel-data')
foreach ($folder in 'keys', 'keyring', 'tls') {
    if (Test-Path (Join-Path $panelData $folder)) { Copy-Item (Join-Path $panelData $folder) $data -Recurse }
}
Copy-Item $config $target

Write-Host "Backup written to $target. Copy it somewhere safe, away from this machine." -ForegroundColor Green
