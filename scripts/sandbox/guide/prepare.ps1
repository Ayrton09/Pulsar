# Prepares a Windows Sandbox in which a person installs Pulsar by following docs/guides/install.md and nothing else,
# on a clean Windows 11 with internet access: the release ZIP and its checksum are on the sandbox's desktop, and
# everything else is downloaded inside it, as on a real machine (the .NET runtimes and PostgreSQL by the installer,
# which checks them; the game by the agent).
# Run on the host, no elevation needed:
#   .\scripts\sandbox\guide\prepare.ps1
# Then open artifacts\guide-sandbox\guide.wsb. The sandbox is discarded when closed; nothing is installed on the host.
param(
    # The web app is already built into src\Pulsar.Api\wwwroot.
    [switch] $SkipWebBuild)
$ErrorActionPreference = 'Stop'

$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..\..')
$bundle = Join-Path $repo 'artifacts\guide-sandbox'
if (Test-Path $bundle) { Remove-Item $bundle -Recurse -Force }
New-Item -ItemType Directory -Force $bundle | Out-Null

& (Join-Path $repo 'scripts\release\Build-Release.ps1') -Version '0.0.0-guide' -Output (Join-Path $bundle 'release') -SkipWebBuild:$SkipWebBuild
Get-ChildItem (Join-Path $bundle 'release') -Directory | Remove-Item -Recurse -Force

@"
<Configuration>
  <Networking>Enable</Networking>
  <MemoryInMB>8192</MemoryInMB>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$(Join-Path $bundle 'release')</HostFolder>
      <SandboxFolder>C:\Users\WDAGUtilityAccount\Desktop\Pulsar download</SandboxFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
  </MappedFolders>
</Configuration>
"@ | Set-Content -Encoding utf8 (Join-Path $bundle 'guide.wsb')

Write-Host "Open $(Join-Path $bundle 'guide.wsb'). On its desktop, 'Pulsar download' has the release ZIP and its checksum,"
Write-Host 'as if downloaded from the Releases page. Follow docs/guides/install.md from step 1.'
