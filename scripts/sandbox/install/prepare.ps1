# Prepares a Windows Sandbox run of the release installer (Install-Pulsar.ps1) on a clean Windows: builds the release
# ZIP, extracts it as a user would, and maps PostgreSQL binaries that run.ps1 registers as a service.
# Run on the host, no elevation needed:
#   .\scripts\sandbox\install\prepare.ps1 -PgHome <extracted PostgreSQL binaries>\pgsql
# Then open artifacts\install-sandbox\install.wsb and wait for report\summary.txt to end with "done".
# The sandbox has no network and is discarded when closed; nothing is installed on the host.
param(
    [Parameter(Mandatory)] [string] $PgHome,
    # The web app is already built into src\Pulsar.Api\wwwroot.
    [switch] $SkipWebBuild)
$ErrorActionPreference = 'Stop'

$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..\..')
$bundle = Join-Path $repo 'artifacts\install-sandbox'
$PgHome = (Resolve-Path $PgHome).Path
if (-not (Test-Path (Join-Path $PgHome 'bin\initdb.exe'))) { throw "$PgHome does not contain bin\initdb.exe" }

if (Test-Path $bundle) { Remove-Item $bundle -Recurse -Force }
New-Item -ItemType Directory -Force $bundle | Out-Null

& (Join-Path $repo 'scripts\release\Build-Release.ps1') -Version '0.0.0-sandbox' -Output (Join-Path $bundle 'zip') -SkipWebBuild:$SkipWebBuild
Expand-Archive (Join-Path $bundle 'zip\pulsar-0.0.0-sandbox-windows-x64.zip') (Join-Path $bundle 'release')
Remove-Item (Join-Path $bundle 'zip') -Recurse -Force

$vcrt = New-Item -ItemType Directory -Force (Join-Path $bundle 'vcrt')
foreach ($dll in 'vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll', 'msvcp140_1.dll', 'msvcp140_2.dll', 'concrt140.dll') {
    Copy-Item (Join-Path $env:SystemRoot "System32\$dll") $vcrt
}
Copy-Item (Join-Path $PSScriptRoot 'run.ps1') $bundle

$sandboxBundle = 'C:\Users\WDAGUtilityAccount\Desktop\install'
@"
<Configuration>
  <Networking>Disable</Networking>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$bundle</HostFolder>
      <SandboxFolder>$sandboxBundle</SandboxFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
    <MappedFolder>
      <HostFolder>$PgHome</HostFolder>
      <SandboxFolder>C:\pgsql</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <LogonCommand>
    <Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File $sandboxBundle\run.ps1 -Bundle $sandboxBundle -PgBin C:\pgsql\bin</Command>
  </LogonCommand>
</Configuration>
"@ | Set-Content -Encoding utf8 (Join-Path $bundle 'install.wsb')

Write-Host "Bundle ready: $bundle"
Write-Host "Open $(Join-Path $bundle 'install.wsb') and wait for report\summary.txt to end with 'done'."
