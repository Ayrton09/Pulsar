# Builds a self-contained bundle for spike 2 and a Windows Sandbox configuration that runs it.
# Run on the host (no elevation needed):  .\spikes\IsolatedLaunch\prepare-bundle.ps1
# Then open artifacts\spike2-bundle\spike2.wsb (requires the "Windows Sandbox" optional feature).
# The report appears in artifacts\spike2-bundle\report when the sandbox finishes.
$ErrorActionPreference = 'Stop'

$spikes = Split-Path $PSScriptRoot -Parent
$repo = Split-Path $spikes -Parent
$bundle = Join-Path $repo 'artifacts\spike2-bundle'

if (Test-Path $bundle) { Remove-Item $bundle -Recurse -Force }
New-Item -ItemType Directory -Force $bundle | Out-Null

# Self-contained: a fresh sandbox or VM has no .NET runtime.
dotnet publish (Join-Path $spikes 'IsolatedLaunch') -c Release -r win-x64 --self-contained -o (Join-Path $bundle 'IsolatedLaunch')
if ($LASTEXITCODE -ne 0) { throw 'publish IsolatedLaunch failed' }
dotnet publish (Join-Path $spikes 'ConsoleProbe') -c Release -r win-x64 --self-contained -o (Join-Path $bundle 'ConsoleProbe')
if ($LASTEXITCODE -ne 0) { throw 'publish ConsoleProbe failed' }

Copy-Item (Join-Path $PSScriptRoot 'run-spike2.ps1') $bundle

$sandboxBundle = 'C:\Users\WDAGUtilityAccount\Desktop\spike2-bundle'
@"
<Configuration>
  <Networking>Disable</Networking>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$bundle</HostFolder>
      <SandboxFolder>$sandboxBundle</SandboxFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <LogonCommand>
    <Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File $sandboxBundle\run-spike2.ps1 -Bundle $sandboxBundle</Command>
  </LogonCommand>
</Configuration>
"@ | Set-Content -Encoding utf8 (Join-Path $bundle 'spike2.wsb')

Write-Host "Bundle ready: $bundle"
Write-Host "Open $(Join-Path $bundle 'spike2.wsb') and wait for report\spike2.log to show 'done'."
