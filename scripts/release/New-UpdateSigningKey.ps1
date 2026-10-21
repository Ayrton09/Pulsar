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

# The signer is built first, before the passphrase exists anywhere in the environment, and with no build server left running
# (MSBuild's nodes and the compiler server stay resident after a build, and keep the environment they were started with): the
# variable is set only for the signer's own run, and taken out as soon as it ends.
$build = Join-Path ([IO.Path]::GetTempPath()) ('pulsar-new-key-' + [Guid]::NewGuid().ToString('N'))
$first = Read-Host 'Passphrase for the new key (at least 12 characters)' -AsSecureString
$again = Read-Host 'Again' -AsSecureString
$passphrase = [Net.NetworkCredential]::new('', $first).Password
$repeated = [Net.NetworkCredential]::new('', $again).Password
try {
    if ($passphrase -cne $repeated) { throw 'The passphrases differ. Nothing was written.' }
    if ($passphrase.Length -lt 12) { throw 'The passphrase needs at least 12 characters. Nothing was written.' }
    [Environment]::SetEnvironmentVariable('PULSAR_UPDATE_KEY_PASSPHRASE', $null, 'Process')
    dotnet publish (Join-Path $PSScriptRoot '..\..\tools\Pulsar.UpdateSigner') -c Release -r win-x64 --no-self-contained -p:DebugType=none -p:RestoreLockedMode=true -nodeReuse:false -p:UseSharedCompilation=false --disable-build-servers -o $build | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'The build of the signer failed. Nothing was written.' }
    $signer = Join-Path $build 'pulsar-update-signer.exe'
    if (-not (Test-Path -LiteralPath $signer -PathType Leaf)) { throw "The signer was built, but $signer is missing. Nothing was written." }
    [Environment]::SetEnvironmentVariable('PULSAR_UPDATE_KEY_PASSPHRASE', $passphrase, 'Process')
    & $signer new-key $Path
    if ($LASTEXITCODE -ne 0) { throw "The signer failed (exit code $LASTEXITCODE)." }
}
finally {
    [Environment]::SetEnvironmentVariable('PULSAR_UPDATE_KEY_PASSPHRASE', $null, 'Process')
    Remove-Item -LiteralPath $build -Recurse -Force -ErrorAction SilentlyContinue
    $passphrase = $null
    $repeated = $null
    $first.Dispose()
    $again.Dispose()
}
