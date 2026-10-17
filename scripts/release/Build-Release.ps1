# Builds the Windows release ZIP: the panel (with the web app) and the agent for win-x64, framework-dependent (the
# installer makes sure the machine has the .NET runtimes they need, downloaded from Microsoft and checked, and Microsoft
# Update keeps those patched), Install-Pulsar.ps1, the guides and the licenses, plus a SHA-256 checksum file; and the
# agent-only MSI next to it (Build-AgentMsi.ps1), for Intune, group policies and other silent installs, whose agent is
# self-contained (no .NET runtime needed on the target: a silent managed install has no step that would fetch one).
# With Inno Setup 7, also the setup wizards (scripts\release\setup): PulsarAgentSetup.exe, which goes into the panel's
# downloads folder (the panel serves it to new nodes, so the ZIP and PulsarSetup.exe carry it), and PulsarSetup.exe.
#   .\scripts\release\Build-Release.ps1 -Version 1.0.0
# Output: artifacts\release\pulsar-<version>-windows-x64.zip, pulsar-agent-<version>-windows-x64.msi,
# PulsarSetup-<version>.exe, PulsarAgentSetup-<version>.exe and a .sha256 of each
param(
    [string] $Version = '0.0.0-dev',
    [string] $Output = (Join-Path $PSScriptRoot '..\..\artifacts\release'),
    # The web app is already built into src\Pulsar.Api\wwwroot (CI builds and tests it in its own job).
    [switch] $SkipWebBuild,
    # Inno Setup 7's command-line compiler (ISCC.exe). Without it, the per-user and the machine-wide installs of Inno
    # Setup 7 are looked in.
    [string] $InnoSetupCompiler,
    # Fails when Inno Setup 7 is not found, instead of building the ZIP and the MSI without the setups.
    [switch] $RequireSetups,
    # The oldest PostgreSQL 18 release Install-Pulsar.ps1 installs (release.json's postgresMinimum): the current 18.x when
    # the release is built (postgresql.org's versions.json, or the bin\postgres.exe --version of an up-to-date PostgreSQL).
    # It refuses an older one, which a list of versions or an installer given offline could pass off as current.
    [ValidatePattern('^18\.\d{1,4}$')] [string] $PostgresMinimum = '18.6')
$ErrorActionPreference = 'Stop'

# Inno Setup's signer: ISCC.exe and the DLLs next to it (its compiler, ISCmplr.dll, and the rest) must carry a valid
# Authenticode signature, in the file itself, of this organization, before each run: whoever could swap one would put
# code of their own in every setup this builds.
$InnoSetupSigner = 'Pyrsys B.V.'

function Assert-InnoSetupSigned([string] $compiler) {
    $folder = Split-Path -Parent $compiler
    $files = @(Get-Item -LiteralPath $compiler) + @(Get-ChildItem -LiteralPath $folder -File -Filter '*.dll')
    if (-not (Test-Path -LiteralPath (Join-Path $folder 'ISCmplr.dll') -PathType Leaf)) { throw "$folder has no ISCmplr.dll: it is not an Inno Setup install." }
    foreach ($file in $files) {
        $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
        $subject = "$($signature.SignerCertificate.Subject)"
        # The subject decoded one attribute to a line, not matched as text: a name that merely contains "O=Pyrsys B.V."
        # (in a CN, say) comes out quoted or on a line of its own, never as the organization line.
        $lines = if ($signature.SignerCertificate) { @($signature.SignerCertificate.SubjectName.Decode([System.Security.Cryptography.X509Certificates.X500DistinguishedNameFlags]::UseNewLines).Split([char[]]("`r`n"), [StringSplitOptions]::RemoveEmptyEntries)) } else { @() }
        $organizations = @($lines | Where-Object { $_ -clike 'O=*' })
        if ("$($signature.Status)" -cne 'Valid' -or "$($signature.SignatureType)" -cne 'Authenticode' -or $lines.Count -eq 0 -or @($lines | Where-Object { $_.Contains('"') }).Count -ne 0 -or $organizations.Count -ne 1 -or $organizations[0] -cne "O=$InnoSetupSigner") {
            throw "$($file.FullName) is not validly signed by $InnoSetupSigner, Inno Setup's publisher ($($signature.Status), $subject): the setups are not built with it. Reinstall Inno Setup 7 from https://jrsoftware.org (in Program Files)."
        }
    }
}

# Inno Setup 7's ISCC.exe (-InnoSetupCompiler, or an install of it: Program Files first, where only administrators can
# change it, then a per-user install), signed by its publisher; or $null (with a warning) when there is none.
function Find-InnoSetupCompiler {
    $candidates = @($InnoSetupCompiler)
    if (-not $InnoSetupCompiler) {
        $candidates = @(@($env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:LOCALAPPDATA) | Where-Object { $_ } | ForEach-Object {
                if ($_ -eq $env:LOCALAPPDATA) { Join-Path $_ 'Programs\Inno Setup 7\ISCC.exe' } else { Join-Path $_ 'Inno Setup 7\ISCC.exe' }
            })
    }
    foreach ($candidate in $candidates) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        Assert-InnoSetupSigned $candidate
        $engine = & {
            $ErrorActionPreference = 'Continue'
            (& $candidate --version 2>&1 | ForEach-Object { "$_" }) -join ' '
        }
        # The scripts use Inno Setup 7 (SetupArchitecture, 64-bit setups).
        if ($engine -notmatch '^\s*(\d+)\.\d+' -or [int]$Matches[1] -lt 7) { throw "$candidate is not Inno Setup 7 or later ($engine)." }
        return (Resolve-Path -LiteralPath $candidate).Path
    }
    $message = "Inno Setup 7 was not found ($($candidates -join ', ')): the setups (PulsarSetup.exe, PulsarAgentSetup.exe) are not built."
    if ($RequireSetups -or $InnoSetupCompiler) { throw $message }
    Write-Warning $message
    return $null
}

# Compiles one of scripts\release\setup's setups from the staged release into $Output; its path. A warning fails the
# build like an error.
function Build-Setup([string] $compiler, [string] $script, [string] $release, [string] $numeric) {
    $arguments = @('--quiet', '--messages-jsonl', "/DVersion=$Version", "/DNumericVersion=$numeric.0", "/DRelease=$release", "/O$Output", (Join-Path $PSScriptRoot "setup\$script"))
    # Checked again right before it runs: the web build (npm ci) ran code from the registry since it was found.
    Assert-InnoSetupSigned $compiler
    $lines = & {
        $ErrorActionPreference = 'Continue'
        @(& $compiler @arguments 2>&1 | ForEach-Object { "$_" })
    }
    $code = $LASTEXITCODE
    $messages = @($lines | Where-Object { $_ -match '^\s*\{' } | ForEach-Object { $_ | ConvertFrom-Json })
    $other = @($lines | Where-Object { $_ -notmatch '^\s*\{' -and $_.Trim() })
    foreach ($message in $messages) { Write-Host "${script}: $($message.severity): $($message.message)" }
    if ($code -ne 0 -or $messages.Count -gt 0) {
        throw "$script did not compile cleanly (ISCC exit code $code, $($messages.Count) message(s)).$([Environment]::NewLine)$($other -join [Environment]::NewLine)"
    }
    $name = [IO.Path]::GetFileNameWithoutExtension($script)
    $setup = Join-Path $Output "$name-$Version.exe"
    if (-not (Test-Path -LiteralPath $setup)) { throw "$script compiled, but $setup is missing." }
    return $setup
}

function Write-Checksum([string] $path) {
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $(Split-Path $path -Leaf)" | Set-Content "$path.sha256" -Encoding ASCII
    return $hash
}

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$name = "pulsar-$Version-windows-x64"
# The setups' Windows version resource takes numbers only.
$numeric = ($Version -split '[-+]')[0]
if ($numeric -notmatch '^\d+\.\d+\.\d+$') { throw "The version must start with major.minor.patch, not '$Version'." }
New-Item -ItemType Directory -Force $Output | Out-Null
$Output = (Resolve-Path $Output).Path
# Before the long builds: whether the setups can be built.
$iscc = Find-InnoSetupCompiler
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
[ordered]@{ version = $Version; runtimes = $runtimes; postgresMinimum = $PostgresMinimum } | ConvertTo-Json | Set-Content (Join-Path $staging 'release.json') -Encoding ASCII

# In Windows PowerShell, whatever runs this script: the MSI is written through the Installer's COM interface, checked there.
# By full path, like the other Windows programs the release and install scripts run.
& (Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe') -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Build-AgentMsi.ps1') -AgentDir $msiAgent -Version $Version -Output $Output
if ($LASTEXITCODE -ne 0) { throw 'agent MSI failed' }
Remove-Item $msiAgent -Recurse -Force
$msi = Join-Path $Output "pulsar-agent-$Version-windows-x64.msi"
$msiHash = Write-Checksum $msi

Copy-Item (Join-Path $repo 'scripts\install\*.ps1') $staging
Copy-Item (Join-Path $repo 'LICENSE') $staging
New-Item -ItemType Directory -Force (Join-Path $staging 'docs') | Out-Null
Copy-Item (Join-Path $repo 'docs\guides\*.md') (Join-Path $staging 'docs')
$easy = ''
if ($iscc) { $easy = "$([Environment]::NewLine)The easy way: PulsarSetup-$Version.exe, published next to this ZIP (docs\install.md, `"Easy install`").$([Environment]::NewLine)" }
@"
Pulsar $Version for Windows (x64)
$easy
Install with this ZIP: open PowerShell as administrator in this folder and run
    powershell -ExecutionPolicy Bypass -File .\Install-Pulsar.ps1
This folder must be one only administrators can change (docs\install.md, step 2 shows how to make it): the installer
refuses to run from any other. It downloads and checks what this machine lacks (the .NET $(($runtimes.Keys | ForEach-Object { "$_ $($runtimes[$_])" }) -join ' and ')
runtimes, and PostgreSQL 18 for a panel without one); docs\install.md says how, and how to install offline.
Pulsar is licensed under the GNU AGPL v3 (LICENSE); game manifests and schemas under MIT.
"@ | Set-Content (Join-Path $staging 'README.txt') -Encoding UTF8

# The agent's setup first: in the panel's downloads folder, the panel serves it at /downloads/PulsarAgentSetup.exe
# (Install-Pulsar.ps1 copies the folder along with the panel, and keeps it on updates), so the ZIP and PulsarSetup.exe
# carry it there.
$agentSetup = $null
if ($iscc) {
    $agentSetup = Build-Setup $iscc 'PulsarAgentSetup.iss' $staging $numeric
    $downloads = New-Item -ItemType Directory -Force (Join-Path $staging 'panel\downloads')
    Copy-Item -LiteralPath $agentSetup -Destination (Join-Path $downloads.FullName 'PulsarAgentSetup.exe')
}

$zip = Join-Path $Output "$name.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip -CompressionLevel Optimal
$hash = Write-Checksum $zip
Write-Host "Release: $zip"
Write-Host "SHA-256: $hash"
Write-Host "Agent MSI: $msi (SHA-256 $msiHash)"

if ($iscc) {
    $setup = Build-Setup $iscc 'PulsarSetup.iss' $staging $numeric
    foreach ($built in $setup, $agentSetup) {
        $builtHash = Write-Checksum $built
        Write-Host "Setup: $built ($([Math]::Round((Get-Item -LiteralPath $built).Length / 1MB, 1)) MB, SHA-256 $builtHash)"
    }
}
