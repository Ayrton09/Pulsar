<#
.SYNOPSIS
    Backs up the Pulsar panel installed on this machine: its database, its keys and certificates, and its settings.

.DESCRIPTION
    Run it in an elevated PowerShell on the panel's machine. It writes a folder pulsar-backup-<date> under -Destination
    with:
      pulsar.dump                PostgreSQL dump of the panel's database (pg_dump custom format)
      panel-data\                the panel's Data Protection keys, key ring certificate and HTTPS certificate
      appsettings.Production.json the panel's settings (database and certificate passwords)

    The database's secrets (game server tokens, two-factor keys, the panel's signing key) can only be read with the
    keys, so the three always go together. The folder holds secrets: keep it as private as the panel itself (it is
    readable only by SYSTEM and Administrators) and store a copy away from this machine.

    Restore with: Install-Pulsar.ps1 -Mode Panel -RestoreFrom <the backup folder> (docs/guides/backup.md).
    Game servers' own backups stay on their nodes (C:\ProgramData\Pulsar\Agent\backups); copy them separately.

.EXAMPLE
    .\Backup-Pulsar.ps1 -Destination D:\Backups
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

$config = Join-Path $InstallRoot 'Panel\appsettings.Production.json'
$panelData = Join-Path $DataRoot 'Panel'
if (-not (Test-Path $config)) { throw "The panel is not installed here ($config not found)." }

if (-not $PostgresBin) {
    foreach ($service in Get-CimInstance Win32_Service -Filter "Name LIKE 'postgresql%'") {
        if ($service.PathName -match '^"?([^"]+\\bin)\\pg_ctl\.exe') { $PostgresBin = $Matches[1] }
    }
    if (-not $PostgresBin) { throw 'pg_dump.exe was not found: pass the folder of PostgreSQL''s programs with -PostgresBin.' }
}

$target = Join-Path $Destination ("pulsar-backup-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force $target | Out-Null
# SYSTEM and Administrators only: the backup holds the panel's secrets.
$null = icacls.exe $target /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' /grant:r '*S-1-5-32-544:(OI)(CI)F'

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
