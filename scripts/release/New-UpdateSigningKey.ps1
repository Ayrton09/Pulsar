# Makes the update signing key (docs/guides/releasing.md) with pulsar-update-signer, asking for its passphrase here,
# where PowerShell shows an asterisk for each character typed (the signer's own prompt shows nothing). The passphrase
# reaches the signer through its environment (PULSAR_UPDATE_KEY_PASSPHRASE), set through .NET for that one run and
# taken out at the end: never a command line, which other programs can read, nor a cmdlet's parameter, which PowerShell's
# module logging records.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\release\New-UpdateSigningKey.ps1 -Path <key file>
#
# Keep the key file and its passphrase safe, with a copy offline: a lost key cannot sign updates that installations take.
param([Parameter(Mandatory)] [string] $Path)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3

$Path = [IO.Path]::GetFullPath($Path)
if (Test-Path -LiteralPath $Path) { throw "$Path exists: a key is never written over another. Nothing was written." }
if (-not (Test-Path -LiteralPath (Split-Path -Parent $Path) -PathType Container)) { throw "The folder of $Path does not exist. Nothing was written." }

$first = Read-Host 'Passphrase for the new key (at least 12 characters)' -AsSecureString
$again = Read-Host 'Again' -AsSecureString
$passphrase = [Net.NetworkCredential]::new('', $first).Password
$repeated = [Net.NetworkCredential]::new('', $again).Password
try {
    if ($passphrase -cne $repeated) { throw 'The passphrases differ. Nothing was written.' }
    if ($passphrase.Length -lt 12) { throw 'The passphrase needs at least 12 characters. Nothing was written.' }
    [Environment]::SetEnvironmentVariable('PULSAR_UPDATE_KEY_PASSPHRASE', $passphrase, 'Process')
    & dotnet run --project (Join-Path $PSScriptRoot '..\..\tools\Pulsar.UpdateSigner') -c Release -- new-key $Path
    if ($LASTEXITCODE -ne 0) { throw "The signer failed (exit code $LASTEXITCODE)." }
}
finally {
    [Environment]::SetEnvironmentVariable('PULSAR_UPDATE_KEY_PASSPHRASE', $null, 'Process')
    $passphrase = $null
    $repeated = $null
    $first.Dispose()
    $again.Dispose()
}
