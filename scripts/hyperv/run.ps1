# Runs Pulsar's install checks on fresh Windows Server 2022 Hyper-V VMs with a virtual TPM, which Windows Sandbox and
# CI runners lack: the agent install check (scripts/sandbox/agent-install: with a ready TPM, the node key must be a TPM
# machine key and stay one after a rotation) and the release install check (scripts/sandbox/install). Each check gets
# its own VM (a differencing disk over a base disk, its own virtual TPM, no network), removed afterwards.
#
# 1. On the host, no elevation needed, build the bundles (PostgreSQL binaries: the zip from EDB, extracted; installers:
#    a folder with the .NET runtime installers and, optionally, EDB's PostgreSQL installer, as
#    scripts\sandbox\install\prepare.ps1 says: the VMs have no network, and the release's programs need .NET):
#      .\scripts\sandbox\agent-install\prepare.ps1 -PgHome <pgsql>
#      .\scripts\sandbox\install\prepare.ps1 -Installers <installers> -PgHome <pgsql>
#    The install bundle carries the installers; with EDB's among them, its check installs PostgreSQL with it and leaves
#    -PgHome's binaries unused.
# 2. As administrator, with Hyper-V on and the Windows Server 2022 evaluation ISO (Microsoft Evaluation Center):
#      powershell -ExecutionPolicy Bypass -File .\scripts\hyperv\run.ps1 -IsoPath <iso> -PgHome <pgsql> -UpdatePackage <msu>
# The ISO's Windows has no updates, and Pulsar's programs (.NET 10) do not start on it ("Your Windows doesn't fully
# support CET"): pass the latest cumulative update for Windows Server 2022 from the Microsoft Update Catalog, which is
# applied to a copy of the base disk (about 15 minutes, once). Without it the checks run on the ISO's Windows as it is.
# The first run builds the base disk from the ISO (about 10 minutes); later runs reuse it. Progress goes to
# artifacts\hyperv\host.log, results to artifacts\hyperv\summary.txt and each check's report to
# artifacts\hyperv\<check>\report (summary.txt as in the sandbox). Exit code 1 on any FAIL.
param(
    [Parameter(Mandatory)] [string] $IsoPath,
    [Parameter(Mandatory)] [string] $PgHome,
    # The base disk, the VMs and their disks (about 20 GB).
    [string] $VmRoot = 'D:\PulsarVM',
    [ValidateSet('agent-install', 'install')] [string[]] $Checks = @('agent-install', 'install'),
    # A Windows update package (.msu) applied to the Windows the checks run on.
    [string] $UpdatePackage,
    # Keeps each VM after its check, for a look inside with Hyper-V Manager (sign in as Administrator; the password is
    # in <VmRoot>\base\credential.xml, readable only by the user who ran this).
    [switch] $Keep)
$ErrorActionPreference = 'Stop'

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this as administrator (Hyper-V).' }
Import-Module Hyper-V

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$out = Join-Path $repo 'artifacts\hyperv'
New-Item -ItemType Directory -Force $out, $VmRoot | Out-Null
$IsoPath = (Resolve-Path $IsoPath).Path
$PgHome = (Resolve-Path $PgHome).Path
$baseDir = New-Item -ItemType Directory -Force (Join-Path $VmRoot 'base')
# The base disk carries the lab Administrator's password in its answer file: only administrators and SYSTEM open it.
$baseAccess = New-Object Security.AccessControl.DirectorySecurity
$baseAccess.SetAccessRuleProtection($true, $false)
foreach ($sidText in 'S-1-5-18', 'S-1-5-32-544') {
    $baseAccess.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier $sidText),
        'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
}
Set-Acl -LiteralPath $baseDir.FullName -AclObject $baseAccess
$baseDisk = Join-Path $baseDir 'server2022.vhdx'
$credentialFile = Join-Path $baseDir 'credential.xml'
$summary = Join-Path $out 'summary.txt'
Start-Transcript (Join-Path $out 'host.log') | Out-Null
Set-Content $summary "Hyper-V checks $(Get-Date -Format o)"

function Write-Step([string] $text) { Write-Host "$(Get-Date -Format 'HH:mm:ss') $text" }
function Add-Result([string] $line) { Add-Content $summary $line; Write-Step $line }

# --- Base disk: Windows Server 2022 Standard (Desktop Experience) applied from the ISO, set up unattended on first boot
function New-Password {
    # No XML-special characters: it goes into unattend.xml.
    $sets = 'ABCDEFGHJKLMNPQRSTUVWXYZ', 'abcdefghijkmnopqrstuvwxyz', '23456789', '-_.!#%+='
    $bytes = [byte[]]::new(24)
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $chars = for ($i = 0; $i -lt $bytes.Length; $i++) { $set = $sets[$i % $sets.Count]; $set[$bytes[$i] % $set.Length] }
    -join $chars
}

function New-BaseDisk {
    Write-Step "Building the base disk from $IsoPath"
    $password = New-Password
    $partial = Join-Path $baseDir 'server2022.partial.vhdx'
    if (Test-Path $partial) { Dismount-VHD $partial -ErrorAction SilentlyContinue; Remove-Item $partial -Force }
    $iso = Mount-DiskImage -ImagePath $IsoPath -PassThru
    try {
        $wim = "$(($iso | Get-Volume).DriveLetter):\sources\install.wim"
        $image = Get-WindowsImage -ImagePath $wim | Where-Object ImageName -match 'Standard.*Desktop Experience' | Select-Object -First 1
        if (-not $image) { throw "$wim has no Windows Server Standard (Desktop Experience) image." }
        Write-Step "Image: $($image.ImageName) (index $($image.ImageIndex))"

        $null = New-VHD -Path $partial -SizeBytes 80GB -Dynamic
        $disk = Mount-VHD -Path $partial -Passthru | Get-Disk
        try {
            Initialize-Disk -Number $disk.Number -PartitionStyle GPT
            # The EFI system partition is made as a data partition first: Windows does not format or letter an ESP.
            $system = New-Partition -DiskNumber $disk.Number -Size 260MB -AssignDriveLetter
            $null = Format-Volume -Partition $system -FileSystem FAT32 -NewFileSystemLabel System -Confirm:$false
            $null = New-Partition -DiskNumber $disk.Number -Size 16MB -GptType '{e3c9e316-0b5c-4db8-817d-f92df00215ae}'
            $windows = New-Partition -DiskNumber $disk.Number -UseMaximumSize -AssignDriveLetter
            $null = Format-Volume -Partition $windows -FileSystem NTFS -NewFileSystemLabel Windows -Confirm:$false
            $s = (Get-Partition -DiskNumber $disk.Number -PartitionNumber $system.PartitionNumber).DriveLetter
            $w = (Get-Partition -DiskNumber $disk.Number -PartitionNumber $windows.PartitionNumber).DriveLetter

            Write-Step 'Applying the image (several minutes)'
            $null = Expand-WindowsImage -ImagePath $wim -Index $image.ImageIndex -ApplyPath "${w}:\"
            & "${w}:\Windows\System32\bcdboot.exe" "${w}:\Windows" /s "${s}:" /f UEFI
            if ($LASTEXITCODE -ne 0) { throw 'bcdboot failed' }
            Set-Partition -DiskNumber $disk.Number -PartitionNumber $system.PartitionNumber -GptType '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}'

            # First boot: no questions; the built-in Administrator gets the password (PowerShell Direct signs in with it).
            $component = 'processorArchitecture="amd64" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS"'
            New-Item -ItemType Directory -Force "${w}:\Windows\Panther" | Out-Null
            @"
<?xml version="1.0" encoding="utf-8"?>
<unattend xmlns="urn:schemas-microsoft-com:unattend">
  <settings pass="specialize">
    <component name="Microsoft-Windows-Shell-Setup" $component>
      <ComputerName>PULSAR-VM</ComputerName>
      <TimeZone>UTC</TimeZone>
    </component>
  </settings>
  <settings pass="oobeSystem">
    <component name="Microsoft-Windows-International-Core" $component>
      <InputLocale>en-US</InputLocale>
      <SystemLocale>en-US</SystemLocale>
      <UILanguage>en-US</UILanguage>
      <UserLocale>en-US</UserLocale>
    </component>
    <component name="Microsoft-Windows-Shell-Setup" $component>
      <OOBE>
        <HideEULAPage>true</HideEULAPage>
        <SkipMachineOOBE>true</SkipMachineOOBE>
        <SkipUserOOBE>true</SkipUserOOBE>
      </OOBE>
      <UserAccounts>
        <AdministratorPassword>
          <Value>$password</Value>
          <PlainText>true</PlainText>
        </AdministratorPassword>
      </UserAccounts>
    </component>
  </settings>
</unattend>
"@ | Set-Content -Encoding UTF8 "${w}:\Windows\Panther\unattend.xml"

            # The answer file holds the Administrator's password in clear text. Windows runs SetupComplete.cmd (as SYSTEM,
            # before anyone can sign in) once the first boot's setup has used it: it deletes both copies of the answer
            # file Setup keeps, and itself, so no running VM (nor a checkpoint or export of one) keeps the password. The
            # base disk is never started, so its own copy stays: it is the lab's, in <VmRoot>\base, which only
            # administrators and SYSTEM can open (restricted above).
            New-Item -ItemType Directory -Force "${w}:\Windows\Setup\Scripts" | Out-Null
            "@echo off`r`ndel /f /q `"%SystemRoot%\Panther\unattend.xml`" `"%SystemRoot%\Panther\Unattend\unattend.xml`" 2>nul`r`ndel /f /q `"%~f0`"`r`n" |
                Set-Content -Encoding Ascii -NoNewline "${w}:\Windows\Setup\Scripts\SetupComplete.cmd"
        }
        finally { Dismount-VHD -Path $partial }
    }
    finally { Dismount-DiskImage -ImagePath $IsoPath | Out-Null }

    # Encrypted for this user on this machine (DPAPI).
    [pscredential]::new('Administrator', (ConvertTo-SecureString $password -AsPlainText -Force)) | Export-Clixml $credentialFile
    Move-Item $partial $baseDisk
    Write-Step "Base disk ready: $baseDisk"
}

# The base disk with an update applied offline (before its first boot, as the base disk is never started).
function New-UpdatedBaseDisk([string] $target) {
    $partial = $target -replace '\.vhdx$', '.partial.vhdx'
    if (Test-Path $partial) { Dismount-VHD $partial -ErrorAction SilentlyContinue; Remove-Item $partial -Force }
    Write-Step "Applying $UpdatePackage to a copy of the base disk (about 15 minutes)"
    Copy-Item $baseDisk $partial
    $disk = Mount-VHD -Path $partial -Passthru | Get-Disk
    try {
        $windows = Get-Partition -DiskNumber $disk.Number | Sort-Object Size -Descending | Select-Object -First 1
        if (-not $windows.DriveLetter) {
            Add-PartitionAccessPath -DiskNumber $disk.Number -PartitionNumber $windows.PartitionNumber -AssignDriveLetter
            $windows = Get-Partition -DiskNumber $disk.Number -PartitionNumber $windows.PartitionNumber
        }
        # An .msu carries the servicing stack update (SSU-*.cab) that the cumulative update needs: offline, the
        # servicing stack goes in first (applying the .msu as one fails with 0x800f0823).
        $expanded = Join-Path $VmRoot 'update-expanded'
        if (Test-Path $expanded) { Remove-Item $expanded -Recurse -Force }
        New-Item -ItemType Directory -Force $expanded | Out-Null
        $null = expand.exe "-F:*" $UpdatePackage $expanded
        if ($LASTEXITCODE -ne 0) { throw "Could not expand $UpdatePackage" }
        $cabs = @(Get-ChildItem $expanded -Filter 'SSU-*.cab') + @(Get-ChildItem $expanded -Filter '*.cab' | Where-Object { $_.Name -notlike 'SSU-*' -and $_.Name -ne 'WSUSSCAN.cab' })
        foreach ($cab in $cabs) {
            Write-Step "Applying $($cab.Name)"
            $null = Add-WindowsPackage -Path "$($windows.DriveLetter):\" -PackagePath $cab.FullName
        }
        Remove-Item $expanded -Recurse -Force
    }
    finally { Dismount-VHD -Path $partial }
    Move-Item $partial $target
    Write-Step "Updated base disk ready: $target"
}

# --- One VM per check: generation 2, Secure Boot, a virtual TPM, no network (PowerShell Direct needs none) ---
function Remove-CheckVm([string] $name) {
    $vm = Get-VM -Name $name -ErrorAction SilentlyContinue
    if ($vm) {
        if ($vm.State -ne 'Off') { Stop-VM -VM $vm -TurnOff -Force }
        Remove-VM -VM $vm -Force
    }
    Remove-Item (Join-Path $VmRoot "$name.vhdx") -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $VmRoot $name) -Recurse -Force -ErrorAction SilentlyContinue
}

function New-CheckVm([string] $name) {
    Remove-CheckVm $name
    $disk = Join-Path $VmRoot "$name.vhdx"
    $null = New-VHD -Path $disk -ParentPath $checkBase -Differencing
    $vm = New-VM -Name $name -Generation 2 -MemoryStartupBytes 6GB -VHDPath $disk -Path $VmRoot
    Set-VM -VM $vm -ProcessorCount 4 -StaticMemory -AutomaticCheckpointsEnabled $false
    Get-VMNetworkAdapter -VM $vm | Remove-VMNetworkAdapter
    Set-VMFirmware -VM $vm -EnableSecureBoot On -SecureBootTemplate MicrosoftWindows
    Set-VMKeyProtector -VM $vm -NewLocalKeyProtector
    Enable-VMTPM -VM $vm
    Start-VM -VM $vm
    $vm
}

function Wait-Session([string] $name, [pscredential] $credential) {
    $deadline = (Get-Date).AddMinutes(20)
    while ((Get-Date) -lt $deadline) {
        try { return New-PSSession -VMName $name -Credential $credential -ErrorAction Stop }
        catch { Start-Sleep -Seconds 10 }
    }
    throw "$name did not accept PowerShell Direct within 20 minutes (first boot and setup)."
}

function New-Zip([string] $source, [string] $zip, [string[]] $only = @()) {
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    if (Test-Path $zip) { Remove-Item $zip -Force }
    $archive = [IO.Compression.ZipFile]::Open($zip, 'Create')
    try {
        $root = (Resolve-Path $source).Path.TrimEnd('\') + '\'
        foreach ($file in Get-ChildItem $source -Recurse -File) {
            $relative = $file.FullName.Substring($root.Length)
            $top = $relative.Split('\')[0]
            if ($top -eq 'report' -or ($only.Count -gt 0 -and $top -notin $only)) { continue }
            $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $relative, 'Fastest')
        }
    }
    finally { $archive.Dispose() }
}

function Invoke-Check([string] $check, [pscredential] $credential) {
    $bundle = Join-Path $repo "artifacts\$check-sandbox"
    if (-not (Test-Path (Join-Path $bundle 'run.ps1'))) { throw "$bundle is missing: run scripts\sandbox\$check\prepare.ps1 first." }
    $name = "pulsar-check-$check"
    $result = Join-Path $out $check
    if (Test-Path $result) { Remove-Item $result -Recurse -Force }
    New-Item -ItemType Directory -Force $result | Out-Null

    Write-Step "${check}: packing the bundle and PostgreSQL"
    New-Zip $bundle (Join-Path $out 'bundle.zip')
    New-Zip $PgHome (Join-Path $out 'pgsql.zip') -only 'bin', 'lib', 'share'

    Write-Step "${check}: starting VM $name"
    $null = New-CheckVm $name
    $session = $null
    try {
        $session = Wait-Session $name $credential
        # The account signs in before Windows setup has quite finished.
        $setupDone = $false
        $deadline = (Get-Date).AddMinutes(10)
        do {
            $setupDone = Invoke-Command -Session $session {
                $setup = Get-ItemProperty 'HKLM:\SYSTEM\Setup'
                $setup.SystemSetupInProgress -eq 0 -and $setup.OOBEInProgress -eq 0
            }
            if (-not $setupDone) { Start-Sleep -Seconds 10 }
        } while (-not $setupDone -and (Get-Date) -lt $deadline)
        if (-not $setupDone) { throw 'Windows setup did not finish in the VM within 10 minutes.' }
        $os =Invoke-Command -Session $session { (Get-CimInstance Win32_OperatingSystem).Caption + ' ' + [Environment]::OSVersion.Version }
        Write-Step "${check}: signed in to $os"

        # Windows provisions the TPM itself shortly after setup.
        $tpm = $null
        $deadline = (Get-Date).AddMinutes(5)
        do {
            $tpm = Invoke-Command -Session $session { Get-Tpm | Select-Object TpmPresent, TpmReady, ManufacturerIdTxt, ManufacturerVersion }
            if ($tpm.TpmReady) { break }
            Start-Sleep -Seconds 10
        } while ((Get-Date) -lt $deadline)
        Add-Result "INFO ${check}: $os; TPM present: $($tpm.TpmPresent), ready: $($tpm.TpmReady), $($tpm.ManufacturerIdTxt) $($tpm.ManufacturerVersion)"
        # Without it the agent would fall back to software keys and the check would prove nothing about the TPM.
        if (-not $tpm.TpmReady) { Add-Result "FAIL ${check}: no ready TPM in the VM" }

        Write-Step "${check}: copying the bundle into the VM"
        Copy-Item (Join-Path $out 'bundle.zip'), (Join-Path $out 'pgsql.zip') -Destination 'C:\' -ToSession $session
        Invoke-Command -Session $session {
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            [IO.Compression.ZipFile]::ExtractToDirectory('C:\bundle.zip', 'C:\bundle')
            [IO.Compression.ZipFile]::ExtractToDirectory('C:\pgsql.zip', 'C:\pgsql')
            Remove-Item 'C:\bundle.zip', 'C:\pgsql.zip'
        }

        Write-Step "${check}: running the check in the VM"
        $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'C:\bundle\run.ps1', '-Bundle', 'C:\bundle', '-PgBin', 'C:\pgsql\bin')
        if ($check -eq 'agent-install') { $arguments += '-SkipRealGame' }
        # Output to files, not through a pipe: the check leaves processes running (the agent install check's panel and
        # PostgreSQL), which would inherit the pipe and keep this call waiting after the check ends.
        $exit = Invoke-Command -Session $session {
            param($arguments)
            $process = Start-Process powershell.exe -ArgumentList $arguments -PassThru -WindowStyle Hidden `
                -RedirectStandardOutput 'C:\bundle\console.txt' -RedirectStandardError 'C:\bundle\console-error.txt'
            $null = $process.Handle # keeps the exit code readable after the process ends
            $process.WaitForExit()
            # The agent install check's panel is still running and writing report\panel*.txt, which a copy out of the VM
            # cannot read while it is open: end it (the VM is thrown away anyway).
            Get-Process -Name 'Pulsar.Api' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
            Get-Process -Name 'Pulsar.Api' -ErrorAction SilentlyContinue | Wait-Process -Timeout 30 -ErrorAction SilentlyContinue
            $process.ExitCode
        } -ArgumentList (, $arguments)

        # A file still held open in the VM must not keep the rest of the report (summary.txt above all) from coming out.
        Copy-Item 'C:\bundle\report' -Destination $result -Recurse -FromSession $session -ErrorAction Continue
        Copy-Item 'C:\bundle\console.txt', 'C:\bundle\console-error.txt' -Destination $result -FromSession $session -ErrorAction SilentlyContinue
        $lines = Get-Content (Join-Path $result 'report\summary.txt')
        $pass = @($lines | Where-Object { $_ -like 'PASS *' }).Count
        $fail = @($lines | Where-Object { $_ -like 'FAIL *' })
        foreach ($line in $lines | Where-Object { $_ -like 'INFO *' -or $_ -like 'FAIL *' -or $_ -match 'TPM' }) { Add-Result "  $line" }
        Add-Result ("{0} {1}: {2} passed, {3} failed (exit {4})" -f $(if ($fail.Count -eq 0 -and $exit -eq 0) { 'PASS' } else { 'FAIL' }), $check, $pass, $fail.Count, $exit)
    }
    catch {
        Add-Result "FAIL ${check}: $_"
    }
    finally {
        if ($session) { Remove-PSSession $session }
        Remove-Item (Join-Path $out 'bundle.zip'), (Join-Path $out 'pgsql.zip') -ErrorAction SilentlyContinue
        if ($Keep) { Write-Step "${check}: VM $name kept" } else { Remove-CheckVm $name }
    }
}

try {
    if (-not (Test-Path $baseDisk) -or -not (Test-Path $credentialFile)) { New-BaseDisk }
    $checkBase = $baseDisk
    if ($UpdatePackage) {
        $UpdatePackage = (Resolve-Path $UpdatePackage).Path
        # windows10.0-kb5122882-x64_<sha1>.msu -> server2022-kb5122882.vhdx
        $kb = [IO.Path]::GetFileNameWithoutExtension($UpdatePackage) -split '[-_]' | Where-Object { $_ -like 'kb*' } | Select-Object -First 1
        if (-not $kb) { $kb = [IO.Path]::GetFileNameWithoutExtension($UpdatePackage) }
        $checkBase = Join-Path $baseDir "server2022-$kb.vhdx"
        if (-not (Test-Path $checkBase)) { New-UpdatedBaseDisk $checkBase }
        Add-Result "INFO update applied: $([IO.Path]::GetFileName($UpdatePackage))"
    }
    $credential = Import-Clixml $credentialFile
    foreach ($check in $Checks) { Invoke-Check $check $credential }
}
catch {
    Add-Result "FAIL unexpected: $_ $($_.ScriptStackTrace)"
}
finally {
    Add-Content $summary 'done'
    Stop-Transcript | Out-Null
}
if (Select-String -Path $summary -Pattern '^FAIL' -Quiet) { exit 1 }
