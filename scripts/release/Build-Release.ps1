# Builds the Windows release ZIP: the panel (with the web app) and the agent, self-contained for win-x64 (no .NET
# runtime needed on the target), Install-Pulsar.ps1, the guides and the licenses, plus a SHA-256 checksum file; and the
# agent-only MSI next to it (Build-AgentMsi.ps1), for Intune, group policies and other silent installs.
#   .\scripts\release\Build-Release.ps1 -Version 1.0.0
# Output: artifacts\release\pulsar-<version>-windows-x64.zip, pulsar-agent-<version>-windows-x64.msi and a .sha256 of each
param(
    [string] $Version = '0.0.0-dev',
    [string] $Output = (Join-Path $PSScriptRoot '..\..\artifacts\release'),
    # The web app is already built into src\Pulsar.Api\wwwroot (CI builds and tests it in its own job).
    [switch] $SkipWebBuild)
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$name = "pulsar-$Version-windows-x64"
New-Item -ItemType Directory -Force $Output | Out-Null
$Output = (Resolve-Path $Output).Path
$staging = Join-Path $Output $name
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }

if (-not $SkipWebBuild) {
    Push-Location (Join-Path $repo 'src\Pulsar.Web')
    try {
        npm ci
        if ($LASTEXITCODE -ne 0) { throw 'npm ci failed' }
        npm run build
        if ($LASTEXITCODE -ne 0) { throw 'web build failed' }
    }
    finally { Pop-Location }
}

if (-not (Test-Path (Join-Path $repo 'src\Pulsar.Api\wwwroot\index.html'))) { throw 'The web app is not built (src\Pulsar.Api\wwwroot).' }

# Locked restore: the release gets exactly the dependencies (and content hashes) in the lock files.
$common = @('-c', 'Release', '-r', 'win-x64', '--self-contained', "-p:Version=$Version", '-p:DebugType=none', '-p:RestoreLockedMode=true')
dotnet publish (Join-Path $repo 'src\Pulsar.Api') @common -o (Join-Path $staging 'panel')
if ($LASTEXITCODE -ne 0) { throw 'publish panel failed' }
dotnet publish (Join-Path $repo 'src\Pulsar.Agent') @common -o (Join-Path $staging 'agent')
if ($LASTEXITCODE -ne 0) { throw 'publish agent failed' }

# Development settings never ship.
Get-ChildItem $staging -Recurse -Filter 'appsettings.Development.json' | Remove-Item

# In Windows PowerShell, whatever runs this script: the MSI is written through the Installer's COM interface, checked there.
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Build-AgentMsi.ps1') -AgentDir (Join-Path $staging 'agent') -Version $Version -Output $Output
if ($LASTEXITCODE -ne 0) { throw 'agent MSI failed' }
$msi = Join-Path $Output "pulsar-agent-$Version-windows-x64.msi"
$msiHash = (Get-FileHash $msi -Algorithm SHA256).Hash.ToLowerInvariant()
"$msiHash  $(Split-Path $msi -Leaf)" | Set-Content "$msi.sha256" -Encoding ASCII

Copy-Item (Join-Path $repo 'scripts\install\*.ps1') $staging
Copy-Item (Join-Path $repo 'LICENSE') $staging
New-Item -ItemType Directory -Force (Join-Path $staging 'docs') | Out-Null
Copy-Item (Join-Path $repo 'docs\guides\*.md') (Join-Path $staging 'docs')
@"
Pulsar $Version for Windows (x64)

Install: open PowerShell as administrator in this folder and run
    powershell -ExecutionPolicy Bypass -File .\Install-Pulsar.ps1
The guide is docs\install.md. Pulsar is licensed under the GNU AGPL v3 (LICENSE); game manifests and schemas under MIT.
"@ | Set-Content (Join-Path $staging 'README.txt') -Encoding UTF8

$zip = Join-Path $Output "$name.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $name.zip" | Set-Content "$zip.sha256" -Encoding ASCII
Write-Host "Release: $zip"
Write-Host "SHA-256: $hash"
Write-Host "Agent MSI: $msi (SHA-256 $msiHash)"
