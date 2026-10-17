# Prepares a Windows Sandbox run of the release installer (Install-Pulsar.ps1) on a clean Windows: builds the release
# ZIP, extracts it (run.ps1 copies it into a folder only administrators can change, as the install guide says, since the
# installer refuses the mapped folder), and puts in the bundle the installers Install-Pulsar.ps1 would download: the
# sandbox has no network, and the ZIP's programs are framework-dependent. Download them on the host first, into a
# folder of their own (Install-Pulsar.ps1 checks them again inside the sandbox, as offline installs do):
#   - dotnet-runtime-10.0.<n>-win-x64.exe and aspnetcore-runtime-10.0.<n>-win-x64.exe, the Windows x64 installers of the
#     .NET Runtime and the ASP.NET Core Runtime (https://dotnet.microsoft.com/download/dotnet/10.0);
#   - optionally EDB's postgresql-18.<minor>-<build>-windows-x64.exe
#     (https://www.enterprisedb.com/downloads/postgres-postgresql-downloads): the run then has the installer install
#     PostgreSQL with it, as on a machine without one. Without it, -PgHome maps PostgreSQL binaries that run.ps1
#     registers as a service, as on a machine with a PostgreSQL installed already.
# The root certificates the installers' signatures (and their timestamps) chain to on this host go into the bundle too:
# a sandbox without network has only the roots Windows ships with, and run.ps1 adds them to the disposable machine's
# store so that the signatures can be checked there.
# Run on the host, no elevation needed:
#   .\scripts\sandbox\install\prepare.ps1 -Installers <folder> [-PgHome <extracted PostgreSQL binaries>\pgsql]
# Then open artifacts\install-sandbox\install.wsb and wait for report\summary.txt to end with "done".
# The sandbox has no network and is discarded when closed; nothing is installed on the host.
param(
    # The installers downloaded beforehand (see above).
    [Parameter(Mandatory)] [string] $Installers,
    # PostgreSQL binaries (the zip from EDB, extracted), when -Installers has no PostgreSQL installer.
    [string] $PgHome,
    # The web app is already built into src\Pulsar.Api\wwwroot.
    [switch] $SkipWebBuild)
$ErrorActionPreference = 'Stop'

$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..\..')
$bundle = Join-Path $repo 'artifacts\install-sandbox'
$Installers = (Resolve-Path $Installers).Path
foreach ($pattern in 'dotnet-runtime-10.0.*-win-x64.exe', 'aspnetcore-runtime-10.0.*-win-x64.exe') {
    if (-not (Get-ChildItem $Installers -Filter $pattern -File)) { throw "$Installers has no $pattern." }
}
$pgInstaller = Get-ChildItem $Installers -Filter 'postgresql-*-windows-x64.exe' -File | Select-Object -First 1
if (-not $pgInstaller -and -not $PgHome) { throw "Give -PgHome, or put EDB's postgresql-18.<minor>-<build>-windows-x64.exe in $Installers." }
if ($PgHome) {
    $PgHome = (Resolve-Path $PgHome).Path
    if (-not (Test-Path (Join-Path $PgHome 'bin\initdb.exe'))) { throw "$PgHome does not contain bin\initdb.exe" }
}

if (Test-Path $bundle) { Remove-Item $bundle -Recurse -Force }
New-Item -ItemType Directory -Force $bundle | Out-Null

& (Join-Path $repo 'scripts\release\Build-Release.ps1') -Version '0.0.0-sandbox' -Output (Join-Path $bundle 'zip') -SkipWebBuild:$SkipWebBuild
Expand-Archive (Join-Path $bundle 'zip\pulsar-0.0.0-sandbox-windows-x64.zip') (Join-Path $bundle 'release')
Remove-Item (Join-Path $bundle 'zip') -Recurse -Force

$installersCopy = New-Item -ItemType Directory -Force (Join-Path $bundle 'installers')
$roots = New-Item -ItemType Directory -Force (Join-Path $installersCopy 'roots')
foreach ($file in Get-ChildItem $Installers -File | Where-Object { $_.Name -like 'dotnet-runtime-*' -or $_.Name -like 'aspnetcore-runtime-*' -or $_.Name -like 'postgresql-*' }) {
    Copy-Item $file.FullName $installersCopy
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

$vcrt = New-Item -ItemType Directory -Force (Join-Path $bundle 'vcrt')
foreach ($dll in 'vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll', 'msvcp140_1.dll', 'msvcp140_2.dll', 'concrt140.dll') {
    Copy-Item (Join-Path $env:SystemRoot "System32\$dll") $vcrt
}
Copy-Item (Join-Path $PSScriptRoot 'run.ps1') $bundle

$sandboxBundle = 'C:\Users\WDAGUtilityAccount\Desktop\install'
# PostgreSQL's binaries only when there is no installer of it to test: run.ps1 prefers the installer.
$pgFolder = ''
$pgArgument = ''
if ($PgHome -and -not $pgInstaller) {
    $pgFolder = @"

    <MappedFolder>
      <HostFolder>$PgHome</HostFolder>
      <SandboxFolder>C:\pgsql</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
"@
    $pgArgument = ' -PgBin C:\pgsql\bin'
}
@"
<Configuration>
  <Networking>Disable</Networking>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$bundle</HostFolder>
      <SandboxFolder>$sandboxBundle</SandboxFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>$pgFolder
  </MappedFolders>
  <LogonCommand>
    <Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File $sandboxBundle\run.ps1 -Bundle $sandboxBundle$pgArgument</Command>
  </LogonCommand>
</Configuration>
"@ | Set-Content -Encoding utf8 (Join-Path $bundle 'install.wsb')

Write-Host "Bundle ready: $bundle"
Write-Host "Open $(Join-Path $bundle 'install.wsb') and wait for report\summary.txt to end with 'done'."
