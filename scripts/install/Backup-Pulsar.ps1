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
    ReFS, not FAT or exFAT) and store a copy away from this machine.

    Restore with: Install-Pulsar.ps1 -Mode Panel -RestoreFrom <the backup folder> (docs/guides/backup.md).
    Game servers' own backups stay on their nodes (C:\ProgramData\Pulsar\Agent\backups); copy them separately.

.EXAMPLE
    & 'C:\Program Files\Pulsar\Panel\Backup-Pulsar.ps1' -Destination D:\Backups
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
    for ($ancestor = $folder.Parent; $null -ne $ancestor; $ancestor = $ancestor.Parent) {
        $rights = $ReplaceRights
        if ($null -eq $ancestor.Parent) { $rights = $ReplaceRights -band (-bnot 0x10000) }
        Find-UntrustedWriter $ancestor $rights
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

# pg_dump must be as new as the server: the newest PostgreSQL installed here.
if (-not $PostgresBin) {
    $newest = 0
    foreach ($service in Get-CimInstance Win32_Service -Filter "Name LIKE 'postgresql%'") {
        if ($service.PathName -match '^"?([^"]+\\bin)\\pg_ctl\.exe') {
            $bin = $Matches[1]
            $version = (& (Join-Path $bin 'pg_dump.exe') --version) -join ' '
            if ($version -match '(\d+)\.\d+' -and [int]$Matches[1] -gt $newest) {
                $newest = [int]$Matches[1]
                $PostgresBin = $bin
            }
        }
    }
    if (-not $PostgresBin) { throw 'pg_dump.exe was not found: pass the folder of PostgreSQL''s programs with -PostgresBin.' }
}

$target = Join-Path $Destination ("pulsar-backup-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force $Destination | Out-Null
# A folder made here, never one that already exists (made in advance by someone else, who would own it).
New-Item -ItemType Directory $target | Out-Null
# SYSTEM and Administrators only, before anything is written in it: the backup holds the panel's secrets. icacls.exe by
# full path (this runs elevated, and a program found through PATH could be anyone's); checked, as a native program's
# failure does not stop the script, and so is the result: a drive without permissions (FAT, exFAT, some network
# shares) cannot keep the backup private.
$icacls = Join-Path ([Environment]::SystemDirectory) 'icacls.exe'
$ErrorActionPreference = 'Continue'
$output = & $icacls $target /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' /grant:r '*S-1-5-32-544:(OI)(CI)F' 2>&1
$ErrorActionPreference = 'Stop'
$isPrivate = $false
if ($LASTEXITCODE -eq 0) {
    $acl = Get-Acl $target
    $allowed = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) | Where-Object { $_.AccessControlType -eq 'Allow' } |
        ForEach-Object { $_.IdentityReference.Value } | Select-Object -Unique)
    $isPrivate = $acl.AreAccessRulesProtected -and $allowed.Count -gt 0 -and @($allowed | Where-Object { $_ -notin 'S-1-5-18', 'S-1-5-32-544' }).Count -eq 0
}
if (-not $isPrivate) {
    Remove-Item $target -Recurse -Force
    throw "$Destination cannot keep the backup readable by SYSTEM and Administrators only (a drive without permissions, such as FAT or exFAT, or a share that ignores them): choose a folder on an NTFS or ReFS drive. $output"
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
$data = New-Item -ItemType Directory -Force (Join-Path $target 'panel-data')
foreach ($folder in 'keys', 'keyring', 'tls') {
    if (Test-Path (Join-Path $panelData $folder)) { Copy-Item (Join-Path $panelData $folder) $data -Recurse }
}
Copy-Item $config $target

Write-Host "Backup written to $target. Copy it somewhere safe, away from this machine." -ForegroundColor Green
