# Prepares a Windows Sandbox run that starts a real game server the way the agent service does: as SYSTEM in session 0,
# with the server's own local account, restricted token and window station, inside a Job (Pulsar.Agent.Windows.Tests,
# RealGameTests). Today: Counter-Strike: Source (srcds.exe). Run on the host, no elevation needed:
#   .\scripts\sandbox\real-game\prepare.ps1 -GameDir <an installed CS:S dedicated server, the folder with srcds.exe>
# Then open artifacts\real-game-sandbox\real-game.wsb and wait for report\summary.txt to end with "done".
# The game folder is mapped read-only and copied inside the sandbox; nothing is installed on the host.
param([Parameter(Mandatory)] [string] $GameDir)
$ErrorActionPreference = 'Stop'

$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..\..')
$bundle = Join-Path $repo 'artifacts\real-game-sandbox'
$GameDir = (Resolve-Path $GameDir).Path
if (-not (Test-Path (Join-Path $GameDir 'srcds.exe'))) { throw "$GameDir does not contain srcds.exe" }

if (Test-Path $bundle) { Remove-Item $bundle -Recurse -Force }
New-Item -ItemType Directory -Force $bundle | Out-Null

# Self-contained: the sandbox has no .NET runtime.
dotnet publish (Join-Path $repo 'tests\Pulsar.Agent.Windows.Tests') -c Release -r win-x64 --self-contained -o (Join-Path $bundle 'tests')
if ($LASTEXITCODE -ne 0) { throw 'publish tests failed' }
Copy-Item (Join-Path $repo 'scripts\ci\Invoke-AsSystem.ps1') $bundle
Copy-Item (Join-Path $PSScriptRoot 'run.ps1') $bundle

$sandboxBundle = 'C:\Users\WDAGUtilityAccount\Desktop\real-game'
# Networking stays on (NAT, isolated from the host's files): the game server talks to Steam as it would on a node.
@"
<Configuration>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$bundle</HostFolder>
      <SandboxFolder>$sandboxBundle</SandboxFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
    <MappedFolder>
      <HostFolder>$GameDir</HostFolder>
      <SandboxFolder>C:\game</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <LogonCommand>
    <Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File $sandboxBundle\run.ps1 -Bundle $sandboxBundle</Command>
  </LogonCommand>
</Configuration>
"@ | Set-Content -Encoding utf8 (Join-Path $bundle 'real-game.wsb')

Write-Host "Bundle ready: $bundle"
Write-Host "Open $(Join-Path $bundle 'real-game.wsb') and wait for report\summary.txt to end with 'done'."
