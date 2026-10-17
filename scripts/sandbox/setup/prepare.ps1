# Prepares a Windows Sandbox run of the setup wizard, PulsarSetup-<version>.exe (Phase 18, docs/guides/install.md
# "Easy install"), on a clean Windows: run.ps1 installs Pulsar with it (silently, or through the wizard with -Wizard),
# checks what roadmap Phase 18's "Done when" asks that one machine can show, updates it with the same setup, and
# removes it from Apps. Build the setups first (Build-Release.ps1 with Inno Setup 7); nothing is built here.
#
# Two ways to run it:
# - Without network (the default): the sandbox gets the installers the setup would download, downloaded on the host
#   beforehand into a folder of their own (-Installers), and passes them to the setup (/DOTNETINSTALLERS=,
#   /POSTGRESINSTALLER=), which checks them again inside the sandbox, as offline installs do:
#     - dotnet-runtime-10.0.<n>-win-x64.exe and aspnetcore-runtime-10.0.<n>-win-x64.exe, the Windows x64 installers of
#       the .NET Runtime and the ASP.NET Core Runtime (https://dotnet.microsoft.com/download/dotnet/10.0);
#     - EDB's postgresql-18.<minor>-<build>-windows-x64.exe (https://www.enterprisedb.com/downloads/postgres-postgresql-downloads).
#   The roots their signatures chain to on this host go in too (a sandbox without network has only those Windows ships
#   with). This run also checks that, without internet and without those installers, the setup says so and changes
#   nothing, and that a tampered installer is refused.
# - -Networking: the sandbox has the host's network, and the setup downloads the newest .NET 10 and PostgreSQL 18
#   itself and checks them: the real test of an easy install. -Installers is optional then (only for the tampered
#   installer check).
#
# Run on the host, no elevation needed:
#   .\scripts\sandbox\setup\prepare.ps1 -Installers <folder> [-Release artifacts\release] [-Wizard]
#   .\scripts\sandbox\setup\prepare.ps1 -Networking [-Installers <folder>] [-Wizard]
# Then open artifacts\setup-sandbox\setup.wsb and wait for report\summary.txt to end with "done". With -Wizard, the
# sandbox opens the wizard: click through it with the defaults and the administrator password run.ps1 shows, check
# that its Finished page shows the panel's sign-in page (https://localhost:8443/) to copy, open it in the browser,
# and close the browser; the checks then run.
# The sandbox is discarded when closed; nothing is installed on the host.
param(
    # The installers downloaded beforehand (see above): required without -Networking.
    [string] $Installers,
    # Where the setups are (Build-Release.ps1's output).
    [string] $Release = (Join-Path $PSScriptRoot '..\..\..\artifacts\release'),
    # The sandbox gets the network, and the setup downloads what it needs.
    [switch] $Networking,
    # The wizard is run by hand instead of silently.
    [switch] $Wizard)
$ErrorActionPreference = 'Stop'

$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..\..')
$bundle = Join-Path $repo 'artifacts\setup-sandbox'
$Release = (Resolve-Path $Release).Path
$setups = @(Get-ChildItem $Release -Filter 'PulsarSetup-*.exe' -File)
if ($setups.Count -ne 1) { throw "$Release must hold one PulsarSetup-<version>.exe (it has $($setups.Count)): build the release with Inno Setup 7 first." }
$setup = $setups[0]
$version = $setup.BaseName.Substring('PulsarSetup-'.Length)
$agentSetup = Join-Path $Release "PulsarAgentSetup-$version.exe"
foreach ($file in $setup.FullName, $agentSetup) {
    if (-not (Test-Path -LiteralPath "$file.sha256")) { throw "$file.sha256 is missing." }
    $expected = ((Get-Content -LiteralPath "$file.sha256" -Raw) -split '\s+')[0]
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $expected) { throw "$file does not match its .sha256 file." }
}

$pgInstaller = $null
if ($Installers) {
    $Installers = (Resolve-Path $Installers).Path
    foreach ($pattern in 'dotnet-runtime-10.0.*-win-x64.exe', 'aspnetcore-runtime-10.0.*-win-x64.exe', 'postgresql-*-windows-x64.exe') {
        if (-not (Get-ChildItem $Installers -Filter $pattern -File)) { throw "$Installers has no $pattern." }
    }
    $pgInstaller = Get-ChildItem $Installers -Filter 'postgresql-*-windows-x64.exe' -File | Sort-Object Name | Select-Object -Last 1
}
elseif (-not $Networking) { throw 'Give -Installers (the installers the setup would download), or -Networking.' }

if (Test-Path $bundle) { Remove-Item $bundle -Recurse -Force }
New-Item -ItemType Directory -Force $bundle | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'run.ps1') $bundle

# The roots the installers' signatures (and their timestamps) chain to, for a sandbox without network.
if ($Installers -and -not $Networking) {
    $roots = New-Item -ItemType Directory -Force (Join-Path $bundle 'roots')
    foreach ($file in Get-ChildItem $Installers -File | Where-Object { $_.Name -like 'dotnet-runtime-*' -or $_.Name -like 'aspnetcore-runtime-*' -or $_.Name -like 'postgresql-*' }) {
        $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
        if ($signature.Status -ne 'Valid') { Write-Warning "$($file.Name) is not validly signed here ($($signature.Status)): the run will refuse it." }
        foreach ($signer in @($signature.SignerCertificate, $signature.TimeStamperCertificate) | Where-Object { $_ }) {
            $chain = New-Object Security.Cryptography.X509Certificates.X509Chain
            try {
                $null = $chain.Build($signer)
                $root = $chain.ChainElements[$chain.ChainElements.Count - 1].Certificate
                [IO.File]::WriteAllBytes((Join-Path $roots "$($root.Thumbprint).cer"), $root.RawData)
            }
            finally { $chain.Dispose() }
        }
    }
}

$sandboxBundle = 'C:\Users\WDAGUtilityAccount\Desktop\setup'
$arguments = "-Bundle $sandboxBundle -Setup C:\release\$($setup.Name) -AgentSetup C:\release\PulsarAgentSetup-$version.exe -Version $version"
$installersFolder = ''
if ($Installers) {
    $installersFolder = @"

    <MappedFolder>
      <HostFolder>$Installers</HostFolder>
      <SandboxFolder>C:\installers</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
"@
    $arguments += " -Installers C:\installers -PostgresInstaller C:\installers\$($pgInstaller.Name)"
}
if ($Networking) { $arguments += ' -Online' }
if ($Wizard) { $arguments += ' -Wizard' }
$network = if ($Networking) { 'Default' } else { 'Disable' }
@"
<Configuration>
  <Networking>$network</Networking>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$bundle</HostFolder>
      <SandboxFolder>$sandboxBundle</SandboxFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
    <MappedFolder>
      <HostFolder>$Release</HostFolder>
      <SandboxFolder>C:\release</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>$installersFolder
  </MappedFolders>
  <LogonCommand>
    <Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File $sandboxBundle\run.ps1 $arguments</Command>
  </LogonCommand>
</Configuration>
"@ | Set-Content -Encoding utf8 (Join-Path $bundle 'setup.wsb')

Write-Host "Bundle ready: $bundle (PulsarSetup $version, network $(if ($Networking) { 'on: the setup downloads .NET and PostgreSQL' } else { 'off: the installers from ' + $Installers }))"
Write-Host "Open $(Join-Path $bundle 'setup.wsb') and wait for report\summary.txt to end with 'done'."
