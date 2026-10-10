# Builds the agent-only MSI (Phase 14, docs/guides/install.md, "Many nodes at once") from a published agent folder,
# with Windows' own Installer API (msi.dll through its COM interface) and makecab: no third-party tool.
#   .\scripts\release\Build-AgentMsi.ps1 -AgentDir <published agent> -Version 1.0.0 -Output <folder>
# Output: <Output>\pulsar-agent-<version>-windows-x64.msi
#
# What the package does (run elevated, typically silent through Intune, a group policy or a script):
#   msiexec /i pulsar-agent-<version>-windows-x64.msi /qn PANELURL=https://panel:8443 TOKEN=<token> PANELKEY=<fingerprint>
#     [SERVERSROOT=D:\PulsarServers]
# - First install: copies the agent to Program Files\Pulsar\Agent and runs "PulsarAgent enroll", which pairs the
#   machine with the panel (a reusable token enrolls each machine as a new node) and installs and starts the service.
#   It is refused where an agent installed by Install-Pulsar.ps1 runs (the PulsarAgent service exists): both would own
#   the same folder and service.
# - Upgrade (a newer package over an older one): stops the service, replaces the program, starts it again. The node
#   keeps its enrollment, and game servers keep running in their host processes meanwhile.
# - Uninstall: runs "PulsarAgent unenroll" (game servers stop, the service, node key and configuration go), then
#   removes the program. If unenroll fails, the uninstall fails and leaves the program in place, so it can be retried.
#   Game server folders and the agent's logs (enroll.log and unenroll.log among them) stay.
# Run it with Windows PowerShell (powershell.exe), as Build-Release.ps1 does.
param(
    [Parameter(Mandatory)] [string] $AgentDir,
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $Output)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3

# Fixed for every version of the agent: Windows Installer recognizes older versions by it (major upgrades).
$UpgradeCode = '{6C3D5A0B-2E7F-4B1D-9A44-5F0C8E2B7D31}'
# Names the components' GUIDs (stable per file path across versions, as the component rules want).
$ComponentNamespace = [Guid]'8f1b3c2e-7a4d-4e5b-9c61-0d2f3a4b5c6d'
$ServiceName = 'PulsarAgent'

$numeric = ($Version -split '[-+]')[0]
if ($numeric -notmatch '^\d+\.\d+\.\d+$') { throw "The MSI version must start with major.minor.patch, not '$Version'." }
$AgentDir = (Resolve-Path $AgentDir).Path
if (-not (Test-Path (Join-Path $AgentDir 'PulsarAgent.exe'))) { throw "$AgentDir has no PulsarAgent.exe." }
New-Item -ItemType Directory -Force $Output | Out-Null
$Output = (Resolve-Path $Output).Path
$msi = Join-Path $Output "pulsar-agent-$Version-windows-x64.msi"
# makecab reads its directive file in the system's ANSI code page (paths included), not in UTF-8.
$ansi = [Text.Encoding]::GetEncoding([int](Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Nls\CodePage').ACP)
$work = Join-Path ([IO.Path]::GetTempPath()) ("pulsar-msi-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $work | Out-Null
$installer = $null
$database = $null
$summary = $null
$built = $false

# A name-based GUID (RFC 4122 version 5, SHA-1) for a component.
function New-NameGuid([string] $name) {
    $namespace = $ComponentNamespace.ToByteArray()
    # Guid.ToByteArray is little-endian in its first three fields; RFC 4122 hashes them big-endian.
    [Array]::Reverse($namespace, 0, 4); [Array]::Reverse($namespace, 4, 2); [Array]::Reverse($namespace, 6, 2)
    $sha1 = [Security.Cryptography.SHA1]::Create()
    try { $hash = $sha1.ComputeHash([byte[]]($namespace + [Text.Encoding]::UTF8.GetBytes($name.ToUpperInvariant()))) }
    finally { $sha1.Dispose() }
    $bytes = $hash[0..15]
    $bytes[6] = ($bytes[6] -band 0x0F) -bor 0x50
    $bytes[8] = ($bytes[8] -band 0x3F) -bor 0x80
    [Array]::Reverse($bytes, 0, 4); [Array]::Reverse($bytes, 4, 2); [Array]::Reverse($bytes, 6, 2)
    return '{' + ([Guid]::new([byte[]]$bytes)).ToString().ToUpperInvariant() + '}'
}

try {
    # --- The files: one component each, keyed by their path, in a cabinet embedded in the package ----------------
    $directories = [ordered]@{}   # relative folder -> Directory key
    $directories[''] = 'INSTALLDIR'
    $files = @()
    $index = 0
    foreach ($file in Get-ChildItem $AgentDir -Recurse -File | Sort-Object FullName) {
        $relative = $file.FullName.Substring($AgentDir.Length).TrimStart('\')
        $folder = Split-Path $relative -Parent
        $parts = if ($folder) { $folder.Split('\') } else { @() }
        $path = ''
        foreach ($part in $parts) {
            $parent = $path
            $path = if ($path) { "$path\$part" } else { $part }
            if (-not $directories.Contains($path)) {
                $parentKey = if ($parent) { $directories[$parent].Key } else { 'INSTALLDIR' }
                $directories[$path] = @{ Key = "D$($directories.Count)"; Parent = $parentKey; Name = $part }
            }
        }
        $index++
        $key = "F$index"
        $extension = ($file.Extension.TrimStart('.') + '').ToUpperInvariant()
        $short = "F{0:D7}" -f $index
        if ($extension) { $short += '.' + $extension.Substring(0, [Math]::Min(3, $extension.Length)) }
        $directoryKey = if ($folder) { $directories[$folder].Key } else { 'INSTALLDIR' }
        # Binaries carry their version: Windows Installer compares it with an installed file's (an unversioned entry
        # loses to any installed binary, and an upgrade would then leave the files out).
        # (Not $version: PowerShell's names ignore case, and that would overwrite -Version.)
        $fileVersion = $null
        $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($file.FullName)
        if ($null -ne $info.FileVersion) {
            $fileVersion = '{0}.{1}.{2}.{3}' -f $info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart, $info.FilePrivatePart
        }
        $files += [pscustomobject]@{
            Key = $key; Component = "C$index"; Guid = (New-NameGuid $relative); Directory = $directoryKey; Version = $fileVersion
            FileName = "$short|$($file.Name)"; Size = $file.Length; Source = $file.FullName; Sequence = $index; Relative = $relative
        }
    }

    $exe = $files | Where-Object Relative -eq 'PulsarAgent.exe'

    # makecab names each file in the cabinet by its File key, in sequence order.
    $ddf = Join-Path $work 'agent.ddf'
    $lines = @('.OPTION EXPLICIT', '.Set CabinetNameTemplate=agent.cab', ".Set DiskDirectoryTemplate=$work", '.Set Cabinet=on',
        '.Set Compress=on', '.Set CompressionType=LZX', '.Set MaxDiskSize=0', '.Set CabinetFileCountThreshold=0',
        '.Set FolderFileCountThreshold=0', '.Set FolderSizeThreshold=0', '.Set MaxCabinetSize=0', '.Set MaxDiskFileCount=0',
        ".Set InfFileName=$work\agent.inf", ".Set RptFileName=$work\agent.rpt")
    $lines += $files | ForEach-Object { "`"$($_.Source)`" $($_.Key)" }
    foreach ($line in $lines) {
        if ($ansi.GetString($ansi.GetBytes($line)) -ne $line) {
            throw "makecab cannot read this path in the system's code page ($($ansi.WebName)): $line. Build from folders (and a TEMP) whose names it can represent."
        }
    }
    # GetBytes writes no byte order mark (with the UTF-8 code page either).
    [IO.File]::WriteAllBytes($ddf, $ansi.GetBytes(($lines -join "`r`n") + "`r`n"))
    $cab = Join-Path $work 'agent.cab'
    $made = & makecab.exe /F $ddf 2>&1
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $cab)) { throw "makecab failed: $made" }

    # --- The database ----------------------------------------------------------------------------------------------
    if (Test-Path $msi) { Remove-Item $msi }
    $installer = New-Object -ComObject WindowsInstaller.Installer
    # Arguments are cast: PowerShell may hand COM a wrapped value, which it refuses (DISP_E_TYPEMISMATCH).
    $database = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @([string]$msi, [int]3)) # msiOpenDatabaseModeCreate

    function Invoke-Sql([string] $sql, [object[]] $values = @()) {
        $view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database, @([string]$sql))
        $record = $null
        try {
            if ($values.Count -gt 0) {
                $record = $installer.GetType().InvokeMember('CreateRecord', 'InvokeMethod', $null, $installer, @([int]$values.Count))
                for ($i = 0; $i -lt $values.Count; $i++) {
                    $value = $values[$i]
                    if ($null -eq $value) { continue }
                    $field = [int]($i + 1)
                    if ($value -is [int]) { $record.GetType().InvokeMember('IntegerData', 'SetProperty', $null, $record, @($field, [int]$value)) }
                    elseif ($value -is [IO.FileInfo]) { $record.GetType().InvokeMember('SetStream', 'InvokeMethod', $null, $record, @($field, [string]$value.FullName)) }
                    else { $record.GetType().InvokeMember('StringData', 'SetProperty', $null, $record, @($field, [string]$value)) }
                }
                $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, @($record))
            }
            else {
                $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
            }
        }
        finally {
            # An open view keeps the package open (and locked) until the process ends.
            $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null)
            [void][Runtime.InteropServices.Marshal]::ReleaseComObject($view)
            if ($null -ne $record) { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($record) }
        }
    }

    function Insert([string] $table, [string[]] $columns, [object[]] $values) {
        $names = ($columns | ForEach-Object { "``$_``" }) -join ', '
        $marks = (@('?') * $columns.Count) -join ', '
        Invoke-Sql "INSERT INTO ``$table`` ($names) VALUES ($marks)" $values
    }

    # The schema of each table used (Windows Installer SDK).
    Invoke-Sql 'CREATE TABLE `Property` (`Property` CHAR(72) NOT NULL, `Value` LONGCHAR NOT NULL LOCALIZABLE PRIMARY KEY `Property`)'
    Invoke-Sql 'CREATE TABLE `Directory` (`Directory` CHAR(72) NOT NULL, `Directory_Parent` CHAR(72), `DefaultDir` CHAR(255) NOT NULL LOCALIZABLE PRIMARY KEY `Directory`)'
    Invoke-Sql 'CREATE TABLE `Component` (`Component` CHAR(72) NOT NULL, `ComponentId` CHAR(38), `Directory_` CHAR(72) NOT NULL, `Attributes` SHORT NOT NULL, `Condition` CHAR(255), `KeyPath` CHAR(72) PRIMARY KEY `Component`)'
    Invoke-Sql 'CREATE TABLE `File` (`File` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL, `FileName` CHAR(255) NOT NULL LOCALIZABLE, `FileSize` LONG NOT NULL, `Version` CHAR(72), `Language` CHAR(20), `Attributes` SHORT, `Sequence` SHORT NOT NULL PRIMARY KEY `File`)'
    Invoke-Sql 'CREATE TABLE `Media` (`DiskId` SHORT NOT NULL, `LastSequence` SHORT NOT NULL, `DiskPrompt` CHAR(64) LOCALIZABLE, `Cabinet` CHAR(255), `VolumeLabel` CHAR(32), `Source` CHAR(72) PRIMARY KEY `DiskId`)'
    Invoke-Sql 'CREATE TABLE `Feature` (`Feature` CHAR(38) NOT NULL, `Feature_Parent` CHAR(38), `Title` CHAR(64) LOCALIZABLE, `Description` CHAR(255) LOCALIZABLE, `Display` SHORT, `Level` SHORT NOT NULL, `Directory_` CHAR(72), `Attributes` SHORT NOT NULL PRIMARY KEY `Feature`)'
    Invoke-Sql 'CREATE TABLE `FeatureComponents` (`Feature_` CHAR(38) NOT NULL, `Component_` CHAR(72) NOT NULL PRIMARY KEY `Feature_`, `Component_`)'
    Invoke-Sql 'CREATE TABLE `CustomAction` (`Action` CHAR(72) NOT NULL, `Type` SHORT NOT NULL, `Source` CHAR(72), `Target` CHAR(255), `ExtendedType` LONG PRIMARY KEY `Action`)'
    Invoke-Sql 'CREATE TABLE `InstallExecuteSequence` (`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` SHORT PRIMARY KEY `Action`)'
    Invoke-Sql 'CREATE TABLE `InstallUISequence` (`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` SHORT PRIMARY KEY `Action`)'
    Invoke-Sql 'CREATE TABLE `Upgrade` (`UpgradeCode` CHAR(38) NOT NULL, `VersionMin` CHAR(20), `VersionMax` CHAR(20), `Language` CHAR(255), `Attributes` LONG NOT NULL, `Remove` CHAR(255), `ActionProperty` CHAR(72) NOT NULL PRIMARY KEY `UpgradeCode`, `VersionMin`, `VersionMax`, `Language`, `Attributes`)'
    Invoke-Sql 'CREATE TABLE `LaunchCondition` (`Condition` CHAR(255) NOT NULL, `Description` CHAR(255) NOT NULL LOCALIZABLE PRIMARY KEY `Condition`)'
    Invoke-Sql 'CREATE TABLE `ServiceControl` (`ServiceControl` CHAR(72) NOT NULL, `Name` CHAR(255) NOT NULL LOCALIZABLE, `Event` SHORT NOT NULL, `Arguments` CHAR(255) LOCALIZABLE, `Wait` SHORT, `Component_` CHAR(72) NOT NULL PRIMARY KEY `ServiceControl`)'
    Invoke-Sql 'CREATE TABLE `AppSearch` (`Property` CHAR(72) NOT NULL, `Signature_` CHAR(72) NOT NULL PRIMARY KEY `Property`, `Signature_`)'
    Invoke-Sql 'CREATE TABLE `RegLocator` (`Signature_` CHAR(72) NOT NULL, `Root` SHORT NOT NULL, `Key` CHAR(255) NOT NULL, `Name` CHAR(255), `Type` SHORT PRIMARY KEY `Signature_`)'
    # Empty, but AppSearch looks every signature up in it first (error 2228 without it).
    Invoke-Sql 'CREATE TABLE `Signature` (`Signature` CHAR(72) NOT NULL, `FileName` CHAR(255) NOT NULL, `MinVersion` CHAR(20), `MaxVersion` CHAR(20), `MinSize` LONG, `MaxSize` LONG, `MinDate` LONG, `MaxDate` LONG, `Languages` CHAR(255) PRIMARY KEY `Signature`)'

    $properties = [ordered]@{
        ProductCode = '{' + [Guid]::NewGuid().ToString().ToUpperInvariant() + '}'  # each build is a new product (major upgrades)
        ProductName = 'Pulsar Agent'
        ProductVersion = $numeric
        ProductLanguage = '1033'
        Manufacturer = 'Pulsar'
        UpgradeCode = $UpgradeCode
        ALLUSERS = '1'
        ARPNOMODIFY = '1'
        ARPCOMMENTS = 'The Pulsar agent: runs game servers on this machine for a Pulsar panel.'
        ARPURLINFOABOUT = 'https://github.com/Ayrton09/Pulsar'
        # Public properties a managed (elevated) install passes on; the token never reaches the log.
        SecureCustomProperties = 'PANELURL;TOKEN;PANELKEY;SERVERSROOT;OLDERFOUND;NEWERFOUND'
        MsiHiddenProperties = 'TOKEN'
        # Every file of the package is copied, whatever version is installed: an upgrade removes the older version first
        # (RemoveExistingProducts early), and the agent's folder holds nothing but its own files.
        REINSTALLMODE = 'amus'
        # Extra arguments for "enroll" when SERVERSROOT is given (set below by a type 51 action).
        SERVERSROOTARGUMENT = ' '
    }
    foreach ($name in $properties.Keys) { Insert 'Property' @('Property', 'Value') @($name, $properties[$name]) }

    Insert 'Directory' @('Directory', 'Directory_Parent', 'DefaultDir') @('TARGETDIR', $null, 'SourceDir')
    Insert 'Directory' @('Directory', 'Directory_Parent', 'DefaultDir') @('ProgramFiles64Folder', 'TARGETDIR', '.')
    Insert 'Directory' @('Directory', 'Directory_Parent', 'DefaultDir') @('PULSARDIR', 'ProgramFiles64Folder', 'Pulsar')
    Insert 'Directory' @('Directory', 'Directory_Parent', 'DefaultDir') @('INSTALLDIR', 'PULSARDIR', 'Agent')
    $folderIndex = 0
    foreach ($entry in $directories.GetEnumerator()) {
        if ($entry.Key -eq '') { continue }
        $folderIndex++
        $value = $entry.Value
        Insert 'Directory' @('Directory', 'Directory_Parent', 'DefaultDir') @($value.Key, $value.Parent, ("DIR{0:D5}|{1}" -f $folderIndex, $value.Name))
    }

    Insert 'Feature' @('Feature', 'Feature_Parent', 'Title', 'Description', 'Display', 'Level', 'Directory_', 'Attributes') `
        @('Agent', $null, 'Pulsar Agent', 'The agent and its service.', 1, 1, 'INSTALLDIR', 0)
    foreach ($file in $files) {
        # 256: a 64-bit component.
        Insert 'Component' @('Component', 'ComponentId', 'Directory_', 'Attributes', 'Condition', 'KeyPath') @($file.Component, $file.Guid, $file.Directory, 256, $null, $file.Key)
        $language = if ($file.Version) { '0' } else { $null }
        Insert 'File' @('File', 'Component_', 'FileName', 'FileSize', 'Version', 'Language', 'Attributes', 'Sequence') @($file.Key, $file.Component, $file.FileName, [int]$file.Size, $file.Version, $language, 0, $file.Sequence)
        Insert 'FeatureComponents' @('Feature_', 'Component_') @('Agent', $file.Component)
    }

    Insert 'Media' @('DiskId', 'LastSequence', 'DiskPrompt', 'Cabinet', 'VolumeLabel', 'Source') @(1, $files.Count, $null, '#agent.cab', $null, $null)

    # Older versions are removed first (inclusive: a rebuild of the same version replaces it too); a newer one blocks.
    Insert 'Upgrade' @('UpgradeCode', 'VersionMin', 'VersionMax', 'Language', 'Attributes', 'Remove', 'ActionProperty') @($UpgradeCode, '0.0.0', $numeric, $null, (256 + 512), $null, 'OLDERFOUND')
    Insert 'Upgrade' @('UpgradeCode', 'VersionMin', 'VersionMax', 'Language', 'Attributes', 'Remove', 'ActionProperty') @($UpgradeCode, $numeric, $null, $null, 2, $null, 'NEWERFOUND')

    # Registry values the launch conditions read (2: HKLM; 2 + 16: the raw value, from the 64-bit view).
    #   - The Windows build: msiexec reports VersionNT 603 (Windows 8.1) on Windows 10 and 11 as well, so VersionNT
    #     cannot tell them apart. 14393 is Windows 10 version 1607 and Windows Server 2016.
    #   - The PulsarAgent service, when no older package is installed: then Install-Pulsar.ps1 installed it, and the
    #     package would take over its folder and service (and its uninstall would remove the agent from under it).
    Insert 'AppSearch' @('Property', 'Signature_') @('PulsarWindowsBuild', 'WindowsBuildNumber')
    Insert 'RegLocator' @('Signature_', 'Root', 'Key', 'Name', 'Type') @('WindowsBuildNumber', 2, 'SOFTWARE\Microsoft\Windows NT\CurrentVersion', 'CurrentBuildNumber', (2 + 16))
    Insert 'AppSearch' @('Property', 'Signature_') @('PulsarAgentService', 'AgentServiceImage')
    Insert 'RegLocator' @('Signature_', 'Root', 'Key', 'Name', 'Type') @('AgentServiceImage', 2, "SYSTEM\CurrentControlSet\Services\$ServiceName", 'ImagePath', (2 + 16))

    Insert 'LaunchCondition' @('Condition', 'Description') @('NOT NEWERFOUND', 'A newer version of the Pulsar Agent is installed.')
    Insert 'LaunchCondition' @('Condition', 'Description') @('Privileged', 'The Pulsar Agent installs for the whole machine: run the installation as administrator.')
    Insert 'LaunchCondition' @('Condition', 'Description') @('VersionNT64 AND PulsarWindowsBuild >= 14393',
        'The Pulsar Agent runs on 64-bit Windows 10 version 1607, Windows Server 2016 and later.')
    # These two leave a newer installed version to NOT NEWERFOUND, so a downgrade is refused for what it is.
    Insert 'LaunchCondition' @('Condition', 'Description') @('Installed OR OLDERFOUND OR NEWERFOUND OR NOT PulsarAgentService',
        'An agent installed with Install-Pulsar.ps1 runs on this machine (the PulsarAgent service exists). Remove it first with Install-Pulsar.ps1 -Uninstall, from the release ZIP, then install this package.')
    Insert 'LaunchCondition' @('Condition', 'Description') @('Installed OR OLDERFOUND OR NEWERFOUND OR (PANELURL AND TOKEN AND PANELKEY)',
        'Install it with the panel''s values: msiexec /i <package> /qn PANELURL=<panel URL> TOKEN=<enrollment token> PANELKEY=<panel key fingerprint> (the panel shows them under Nodes).')

    # Custom actions: the agent's own commands, run as SYSTEM after the files are in place (deferred, no impersonation).
    #   18 = an executable installed by the package; +1024 deferred; +2048 no impersonation; +8192 target not logged;
    #   +64 exit code ignored. 51 = sets a property.
    # A SERVERSROOT ending in a backslash escapes the closing quote (D:\PulsarServers\" reaches the agent as
    # D:\PulsarServers", which the agent trims), and the argument runs to the end of the line: it stays last.
    Insert 'CustomAction' @('Action', 'Type', 'Source', 'Target', 'ExtendedType') @('SetServersRootArgument', 51, 'SERVERSROOTARGUMENT', ' --servers-root "[SERVERSROOT]"', $null)
    # What the agent's commands print goes to logs next to the agent's (a custom action has no console).
    $logs = '[CommonAppDataFolder]Pulsar\Agent\logs'
    Insert 'CustomAction' @('Action', 'Type', 'Source', 'Target', 'ExtendedType') @('EnrollAgent', (18 + 1024 + 2048 + 8192), $exe.Key,
        "enroll --panel `"[PANELURL]`" --token `"[TOKEN]`" --panel-key `"[PANELKEY]`" --log-file `"$logs\enroll.log`"[SERVERSROOTARGUMENT]", $null)
    # +256: runs only if the installation fails after enrolling, so a rolled-back install leaves no enrollment behind.
    Insert 'CustomAction' @('Action', 'Type', 'Source', 'Target', 'ExtendedType') @('UnenrollOnRollback', (18 + 256 + 1024 + 2048 + 64), $exe.Key, "unenroll --log-file `"$logs\unenroll.log`"", $null)
    # Not +64: a failed unenroll fails the uninstall, which then keeps the program to retry with. Ignored, the program
    # would go and leave a service pointing at a missing file, the node key and the configuration behind.
    Insert 'CustomAction' @('Action', 'Type', 'Source', 'Target', 'ExtendedType') @('UnenrollAgent', (18 + 1024 + 2048), $exe.Key, "unenroll --log-file `"$logs\unenroll.log`"", $null)

    # The service is created by "enroll" (it checks its executable's permissions first); the package only stops it
    # before replacing the program and starts it again after (2 + 1 on install, 32 on uninstall).
    Insert 'ServiceControl' @('ServiceControl', 'Name', 'Event', 'Arguments', 'Wait', 'Component_') @('AgentService', $ServiceName, (1 + 2 + 32), $null, 1, $exe.Component)

    $firstInstall = 'NOT Installed AND NOT OLDERFOUND'
    $execute = @(
        @('FindRelatedProducts', $null, 25), @('AppSearch', $null, 50), @('LaunchConditions', $null, 100),
        @('SetServersRootArgument', 'SERVERSROOT', 110),
        @('ValidateProductID', $null, 700), @('CostInitialize', $null, 800), @('FileCost', $null, 900), @('CostFinalize', $null, 1000),
        @('InstallValidate', $null, 1400), @('InstallInitialize', $null, 1500),
        # Uninstalls the older version first; its uninstall leaves the enrollment alone (UPGRADINGPRODUCTCODE is set).
        @('RemoveExistingProducts', $null, 1510),
        @('ProcessComponents', $null, 1600),
        @('UnenrollAgent', 'REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE', 1700),
        @('UnpublishFeatures', $null, 1800), @('StopServices', $null, 1900), @('RemoveFiles', $null, 3500),
        @('InstallFiles', $null, 4000),
        @('UnenrollOnRollback', $firstInstall, 4090),
        @('EnrollAgent', $firstInstall, 4100),
        @('StartServices', 'NOT REMOVE="ALL"', 5900),
        @('RegisterUser', $null, 6000), @('RegisterProduct', $null, 6100), @('PublishFeatures', $null, 6300), @('PublishProduct', $null, 6400),
        @('InstallFinalize', $null, 6600))
    foreach ($row in $execute) { Insert 'InstallExecuteSequence' @('Action', 'Condition', 'Sequence') $row }
    $ui = @(@('FindRelatedProducts', $null, 25), @('AppSearch', $null, 50), @('LaunchConditions', $null, 100), @('ValidateProductID', $null, 700),
        @('CostInitialize', $null, 800), @('FileCost', $null, 900), @('CostFinalize', $null, 1000), @('ExecuteAction', $null, 1300))
    foreach ($row in $ui) { Insert 'InstallUISequence' @('Action', 'Condition', 'Sequence') $row }

    Invoke-Sql 'INSERT INTO `_Streams` (`Name`, `Data`) VALUES (?, ?)' @('agent.cab', (Get-Item $cab))

    # Summary information: x64 package for Windows Installer 5.0, compressed files, a new package code.
    $summary = $database.GetType().InvokeMember('SummaryInformation', 'GetProperty', $null, $database, @([int]20))
    $set = { param([int]$id, $value) $summary.GetType().InvokeMember('Property', 'SetProperty', $null, $summary, @($id, $value)) }
    & $set 2 'Installation Database'
    & $set 3 "Pulsar Agent $Version"
    & $set 4 'Pulsar'
    & $set 5 'Installer, Pulsar, agent'
    & $set 6 'The Pulsar agent for Windows (x64).'
    & $set 7 'x64;1033'
    & $set 9 ('{' + [Guid]::NewGuid().ToString().ToUpperInvariant() + '}')
    & $set 12 ([DateTime]::Now)
    & $set 13 ([DateTime]::Now)
    & $set 14 ([int]500)
    & $set 15 ([int]2)
    & $set 18 'Pulsar Build-AgentMsi.ps1'
    & $set 19 ([int]2)
    $summary.GetType().InvokeMember('Persist', 'InvokeMethod', $null, $summary, $null)
    $database.GetType().InvokeMember('Commit', 'InvokeMethod', $null, $database, $null)
    $built = $true
}
catch {
    # Windows Installer's automation errors only name the call ("OpenView,Sql"); its last error record says why.
    $failure = $_
    $detail = $null
    if ($null -ne $installer) {
        try {
            $last = $installer.GetType().InvokeMember('LastErrorRecord', 'GetProperty', $null, $installer, $null)
            if ($null -ne $last) {
                $detail = $last.GetType().InvokeMember('FormatText', 'InvokeMethod', $null, $last, $null)
                [void][Runtime.InteropServices.Marshal]::ReleaseComObject($last)
            }
        }
        catch { }
    }
    if ($detail) { throw "$($failure.Exception.Message) Windows Installer error record: $detail" }
    throw $failure
}
finally {
    # Released before the package is deleted: the database holds it open.
    foreach ($object in $summary, $database, $installer) {
        if ($null -ne $object) { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($object) }
    }
    # A package not built to the end is never left behind for a release to pick up.
    if (-not $built -and (Test-Path $msi)) { Remove-Item $msi -Force -ErrorAction SilentlyContinue }
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host "Agent MSI: $msi ($($files.Count) files)"
