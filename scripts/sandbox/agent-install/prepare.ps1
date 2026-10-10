# Prepares a Windows Sandbox run that installs the agent the production way (elevated enroll, machine key, PulsarAgent
# service as SYSTEM) on a clean Windows, against a panel and a PostgreSQL that also run inside the sandbox.
# Run on the host, no elevation needed:
#   .\scripts\sandbox\agent-install\prepare.ps1 -PgHome <extracted PostgreSQL binaries>\pgsql
# Then open artifacts\agent-install-sandbox\agent-install.wsb and wait for report\summary.txt to end with "done".
# The sandbox has no network and is discarded when closed; nothing is installed on the host. With -RealGame it has
# network access, to install a real Counter-Strike: Source server with SteamCMD from Valve (about 2.5 GB).
param([Parameter(Mandatory)] [string] $PgHome, [switch] $RealGame)
$ErrorActionPreference = 'Stop'

$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..\..')
$bundle = Join-Path $repo 'artifacts\agent-install-sandbox'
$PgHome = (Resolve-Path $PgHome).Path
if (-not (Test-Path (Join-Path $PgHome 'bin\initdb.exe'))) { throw "$PgHome does not contain bin\initdb.exe" }

if (Test-Path $bundle) { Remove-Item $bundle -Recurse -Force }
New-Item -ItemType Directory -Force $bundle | Out-Null

# Self-contained: the sandbox has no .NET runtime.
dotnet publish (Join-Path $repo 'src\Pulsar.Api') -c Release -r win-x64 --self-contained -o (Join-Path $bundle 'panel')
if ($LASTEXITCODE -ne 0) { throw 'publish panel failed' }
dotnet publish (Join-Path $repo 'src\Pulsar.Agent') -c Release -r win-x64 --self-contained -o (Join-Path $bundle 'agent')
if ($LASTEXITCODE -ne 0) { throw 'publish agent failed' }
# The agent MSI (Phase 14): 1.0.0, a second build of 1.0.0 (a new product of the same version, which replaces it), and
# 1.0.1, which upgrades them while a game runs (and which 1.0.0 must not downgrade).
foreach ($build in @(@('1.0.0', 'msi'), @('1.0.0', 'msi\rebuild'), @('1.0.1', 'msi'))) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'scripts\release\Build-AgentMsi.ps1') -AgentDir (Join-Path $bundle 'agent') -Version $build[0] -Output (Join-Path $bundle $build[1])
    if ($LASTEXITCODE -ne 0) { throw 'agent MSI failed' }
}
# The test game server runs as a server account, which has no .NET runtime to find either.
dotnet publish (Join-Path $repo 'tests\Pulsar.TestGameServer') -c Release -r win-x64 --self-contained -o (Join-Path $bundle 'testgame')
if ($LASTEXITCODE -ne 0) { throw 'publish test game server failed' }

# The PostgreSQL binaries zip relies on the Visual C++ runtime, which a clean sandbox may lack.
$vcrt = New-Item -ItemType Directory -Force (Join-Path $bundle 'vcrt')
foreach ($dll in 'vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll', 'msvcp140_1.dll', 'msvcp140_2.dll', 'concrt140.dll') {
    Copy-Item (Join-Path $env:SystemRoot "System32\$dll") $vcrt
}

Copy-Item (Join-Path $PSScriptRoot 'run.ps1') $bundle
# The check makes sure the ZIP's installer leaves an agent from the MSI alone.
Copy-Item (Join-Path $repo 'scripts\install\Install-Pulsar.ps1') $bundle

$sandboxBundle = 'C:\Users\WDAGUtilityAccount\Desktop\agent-install'
@"
<Configuration>
  <Networking>$(if ($RealGame) { 'Enable' } else { 'Disable' })</Networking>
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
    <Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File $sandboxBundle\run.ps1 -Bundle $sandboxBundle$(if (-not $RealGame) { ' -SkipRealGame' })</Command>
  </LogonCommand>
</Configuration>
"@ | Set-Content -Encoding utf8 (Join-Path $bundle 'agent-install.wsb')

Write-Host "Bundle ready: $bundle"
Write-Host "Open $(Join-Path $bundle 'agent-install.wsb') and wait for report\summary.txt to end with 'done'."
