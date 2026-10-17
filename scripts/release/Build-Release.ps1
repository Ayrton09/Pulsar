# Builds the Windows release ZIP: the panel (with the web app) and the agent for win-x64, framework-dependent (the
# installer makes sure the machine has the .NET runtimes they need, downloaded from Microsoft and checked, and Microsoft
# Update keeps those patched), Install-Pulsar.ps1, the guides and the licenses, plus a SHA-256 checksum file; and the
# agent-only MSI next to it (Build-AgentMsi.ps1), for Intune, group policies and other silent installs, whose agent is
# self-contained (no .NET runtime needed on the target: a silent managed install has no step that would fetch one).
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
$msiAgent = Join-Path $Output "$name-msi-agent"
if (Test-Path $msiAgent) { Remove-Item $msiAgent -Recurse -Force }

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
$common = @('-c', 'Release', '-r', 'win-x64', "-p:Version=$Version", '-p:DebugType=none', '-p:RestoreLockedMode=true')
# The ZIP's programs need the shared .NET runtimes, at least the newest patch this SDK knows (TargetLatestRuntimePatch
# writes it into their runtimeconfig.json, so they refuse to start on an older one).
$zipOptions = $common + @('--no-self-contained', '-p:TargetLatestRuntimePatch=true')
dotnet publish (Join-Path $repo 'src\Pulsar.Api') @zipOptions -o (Join-Path $staging 'panel')
if ($LASTEXITCODE -ne 0) { throw 'publish panel failed' }
dotnet publish (Join-Path $repo 'src\Pulsar.Agent') @zipOptions -o (Join-Path $staging 'agent')
if ($LASTEXITCODE -ne 0) { throw 'publish agent failed' }
# The MSI's agent carries its own runtime: Windows Installer runs it as is, with nothing to install .NET first.
dotnet publish (Join-Path $repo 'src\Pulsar.Agent') @common --self-contained -o $msiAgent
if ($LASTEXITCODE -ne 0) { throw 'publish agent for the MSI failed' }

# Development settings never ship.
Get-ChildItem $staging, $msiAgent -Recurse -Filter 'appsettings.Development.json' | Remove-Item

# The runtimes the ZIP's programs were built for, which Install-Pulsar.ps1 makes sure of (at least these versions).
$runtimes = [ordered]@{}
foreach ($config in (Join-Path $staging 'panel\Pulsar.Api.runtimeconfig.json'), (Join-Path $staging 'agent\PulsarAgent.runtimeconfig.json')) {
    $options = (Get-Content -Raw $config | ConvertFrom-Json).runtimeOptions
    $frameworks = @()
    if ($options.PSObject.Properties['framework']) { $frameworks += $options.framework }
    if ($options.PSObject.Properties['frameworks']) { $frameworks += @($options.frameworks) }
    if ($frameworks.Count -eq 0) { throw "$config names no shared framework: the program came out self-contained." }
    foreach ($framework in $frameworks) {
        if (-not $runtimes.Contains($framework.name) -or [version]$runtimes[$framework.name] -lt [version]$framework.version) {
            $runtimes[$framework.name] = $framework.version
        }
    }
}
[ordered]@{ version = $Version; runtimes = $runtimes } | ConvertTo-Json | Set-Content (Join-Path $staging 'release.json') -Encoding ASCII

# In Windows PowerShell, whatever runs this script: the MSI is written through the Installer's COM interface, checked there.
# By full path, like the other Windows programs the release and install scripts run.
& (Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe') -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Build-AgentMsi.ps1') -AgentDir $msiAgent -Version $Version -Output $Output
if ($LASTEXITCODE -ne 0) { throw 'agent MSI failed' }
Remove-Item $msiAgent -Recurse -Force
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
This folder must be one only administrators can change (docs\install.md, step 2 shows how to make it): the installer
refuses to run from any other. It downloads and checks what this machine lacks (the .NET $(($runtimes.Keys | ForEach-Object { "$_ $($runtimes[$_])" }) -join ' and ')
runtimes, and PostgreSQL 18 for a panel without one); docs\install.md says how, and how to install offline.
Pulsar is licensed under the GNU AGPL v3 (LICENSE); game manifests and schemas under MIT.
"@ | Set-Content (Join-Path $staging 'README.txt') -Encoding UTF8

$zip = Join-Path $Output "$name.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $name.zip" | Set-Content "$zip.sha256" -Encoding ASCII
Write-Host "Release: $zip"
Write-Host "SHA-256: $hash"
Write-Host "Agent MSI: $msi (SHA-256 $msiHash)"
