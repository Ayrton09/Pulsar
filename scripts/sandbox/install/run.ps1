# Installs Pulsar from the release ZIP with Install-Pulsar.ps1 on a disposable, elevated Windows (Windows Sandbox
# through prepare.ps1, or a CI runner), as a user following docs/guides/install.md would: the release copied into
# C:\Program Files\PulsarSetup, which only administrators can change from the moment it exists (the installer refuses
# the bundle's own folder, which others can change),
# panel and agent on one PC, unattended (as the setup wizard runs it: secrets in the environment, progress lines, exit
# code), with the .NET runtimes installed from the bundle's installers (the ZIP's programs are framework-dependent and
# the sandbox has no network), and the database created in PostgreSQL 18 installed with EDB's installer from the
# bundle, or in a PostgreSQL service registered here from -PgBin, or in the machine's own (CI runners have one). Then
# checks what the installer left, backs up from the panel's folder, restores, updates and uninstalls.
# Every check is written to report\summary.txt as PASS/FAIL; the last line is "done", and the exit code is 1 on any FAIL.
# Never run it on a machine you use: it installs services and runtimes, and trusts certificates machine-wide.
param(
    [Parameter(Mandatory)] [string] $Bundle,
    # PostgreSQL binaries: registered here as a service, as PostgreSQL's installer does, when the bundle's installers
    # have no PostgreSQL installer. Without either, the machine's PostgreSQL service is used with -PostgresPassword.
    [string] $PgBin,
    [string] $PostgresPassword = 'sandbox-postgres-password',
    # The installers prepare.ps1 put in the bundle (.NET's, maybe EDB's), and the roots their signatures chain to.
    # Without them (a CI runner), the installer downloads what it needs.
    [string] $Installers = (Join-Path $Bundle 'installers'))
$ErrorActionPreference = 'Stop'
$report = New-Item -ItemType Directory -Force (Join-Path $Bundle 'report')
$summary = Join-Path $report 'summary.txt'
Set-Content $summary "install check $(Get-Date -Format o) on $([Environment]::OSVersion.VersionString)"
Start-Transcript (Join-Path $report 'transcript.txt') | Out-Null

function Check([string] $name, [bool] $ok, [string] $detail = '') {
    Add-Content $summary ("{0} {1}{2}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $name, $(if ($detail) { " - $detail" } else { '' }))
}

function Wait-Until([scriptblock] $condition, [int] $seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        try { if (& $condition) { return $true } } catch { }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

# The release as the guide has it extracted: in a new folder only administrators can change. The installer and the
# backup script refuse any other, such as the bundle's own folder (a mapped folder in Windows Sandbox, C:\bundle in a VM).
$bundled = Join-Path $Bundle 'release'
$release = Join-Path $env:ProgramFiles 'PulsarSetup'
$installer = Join-Path $release 'Install-Pulsar.ps1'
$installedBackup = 'C:\Program Files\Pulsar\Panel\Backup-Pulsar.ps1'

# A script run in its own PowerShell, expected to refuse: its exit code and what it printed (Windows PowerShell wraps the
# error it prints at the console's width, anywhere in a word, so callers match it without whitespace).
function Invoke-Refused([string] $script, [string[]] $arguments = @()) {
    $ErrorActionPreference = 'Continue'
    $output = (& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script @arguments 2>&1 | ForEach-Object { "$_" }) -join ' '
    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
}
$panelUrl = 'https://localhost:8443'
$adminPassword = 'sandbox administrator password'
$panelData = 'C:\ProgramData\Pulsar\Panel'

# Sets the postgres superuser's password as docs/guides/install.md says (trust for 127.0.0.1 and ::1 a moment, then
# back), here with the password on psql's standard input instead of typed: the installer kept the one it chose nowhere.
function Set-PostgresPassword([string] $service, [string] $data, [string] $bin, [string] $password) {
    Stop-Service $service
    $hba = Join-Path $data 'pg_hba.conf'
    $copy = "$hba.sandbox"
    Copy-Item $hba $copy
    $trust = "host    postgres    postgres    127.0.0.1/32    trust`r`nhost    postgres    postgres    ::1/128         trust`r`n"
    [IO.File]::WriteAllText($hba, $trust + [IO.File]::ReadAllText($copy))
    Start-Service $service
    $ErrorActionPreference = 'Continue'
    "ALTER ROLE postgres PASSWORD '$password';" | & (Join-Path $bin 'psql.exe') -h localhost -U postgres -d postgres -v ON_ERROR_STOP=1 -q -f - *> (Join-Path $report 'pg-password.txt')
    $ok = $LASTEXITCODE -eq 0
    Copy-Item $copy $hba -Force
    Remove-Item $copy
    Restart-Service $service
    return $ok
}

$pgInstaller = $null
if ($Installers -and (Test-Path $Installers)) {
    $pgInstaller = Get-ChildItem $Installers -Filter 'postgresql-*-windows-x64.exe' -File | Select-Object -First 1
}
else { $Installers = $null }
$pgService = 'postgresql-x64-18'
$pgData = 'C:\Program Files\PostgreSQL\18\data'

try {
    # The roots the installers' signatures chain to: a sandbox without network has only those Windows ships with (the
    # machine is disposable; a real one gets them from Windows Update as it checks a signature).
    if ($Installers -and (Test-Path (Join-Path $Installers 'roots'))) {
        foreach ($root in Get-ChildItem (Join-Path $Installers 'roots') -Filter '*.cer') {
            $null = Import-Certificate -FilePath $root.FullName -CertStoreLocation 'Cert:\LocalMachine\Root'
        }
    }

    # --- PostgreSQL: none (the installer installs it), or as its installer leaves it: a service, a superuser password ---
    if ($pgInstaller) {
        if ($PgBin) { Add-Content $summary "INFO $($pgInstaller.Name) is in the bundle: the installer installs PostgreSQL with it, and -PgBin is not used" }
        Check 'no postgresql service before the install (the installer installs one)' ($null -eq (Get-Service 'postgresql*' -ErrorAction SilentlyContinue))
    }
    elseif ($PgBin) {
        # Where only administrators can change it: the installer runs its psql as administrator, and refuses one in a
        # folder others could change (C:\ lets every account change what it makes there).
        $pgHome = Join-Path $env:ProgramFiles 'PostgreSQL-sandbox'
        Copy-Item (Split-Path $PgBin) $pgHome -Recurse
        # The PostgreSQL binaries zip relies on the Visual C++ runtime, which a clean Windows may lack.
        Copy-Item (Join-Path $Bundle 'vcrt\*') (Join-Path $pgHome 'bin')
        $pwfile = Join-Path $env:TEMP 'pgpass.txt'
        Set-Content $pwfile $PostgresPassword -NoNewline
        & (Join-Path $pgHome 'bin\initdb.exe') -D C:\pgdata -U postgres -A scram-sha-256 --pwfile $pwfile -E UTF8 --no-locale *> (Join-Path $report 'initdb.txt')
        Remove-Item $pwfile
        if ($LASTEXITCODE -ne 0) { throw 'initdb failed' }
        $null = icacls.exe C:\pgdata /grant 'NT AUTHORITY\NetworkService:(OI)(CI)F'
        & (Join-Path $pgHome 'bin\pg_ctl.exe') register -N postgresql-x64-sandbox -U 'NT AUTHORITY\NetworkService' -D C:\pgdata -S auto *> (Join-Path $report 'pg-register.txt')
        Start-Service postgresql-x64-sandbox
    }
    if (-not $pgInstaller) { Check 'postgresql service installed' ($null -ne (Get-Service 'postgresql*' -ErrorAction SilentlyContinue)) }

    # --- The release where the guide puts it (step 2); from where other accounts can change it, refused ---
    $refused = Invoke-Refused (Join-Path $bundled 'Install-Pulsar.ps1')
    Check 'installer refuses to run from a folder other accounts can change (nothing installed)' ($refused.ExitCode -ne 0 -and ($refused.Output -replace '\s', '') -match 'Nothingwaschanged' -and $null -eq (Get-Service PulsarPanel, PulsarAgent -ErrorAction SilentlyContinue)) "exit $($refused.ExitCode); $($refused.Output -replace '\s+', ' ')"
    # In C:\Program Files, as the guide says: it takes that folder's permissions as it is made (made elsewhere and
    # restricted afterwards, it would be open to every account in between).
    New-Item -ItemType Directory $release | Out-Null
    Copy-Item (Join-Path $bundled '*') $release -Recurse
    # Write data, append data, write extended attributes, delete children, write attributes, delete, change permissions,
    # take ownership, generic all and generic write: nobody but SYSTEM, Administrators and TrustedInstaller, nor its owner.
    $writeRights = 0x2 -bor 0x4 -bor 0x10 -bor 0x40 -bor 0x100 -bor 0x10000 -bor 0x40000 -bor 0x80000 -bor 0x10000000 -bor 0x40000000
    $trustedSids = @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
    $releaseAcl = Get-Acl $release
    $releaseWriters = @($releaseAcl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) |
        Where-Object { $_.AccessControlType -eq 'Allow' -and ([int]$_.PropagationFlags -band 2) -eq 0 -and ([int]$_.FileSystemRights -band $writeRights) -ne 0 } |
        ForEach-Object { $_.IdentityReference.Value } | Where-Object { $_ -notin $trustedSids } | Select-Object -Unique)
    $releaseOwner = $releaseAcl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    Check 'release folder: only SYSTEM, Administrators and TrustedInstaller can change it' ($releaseWriters.Count -eq 0 -and $releaseOwner -in $trustedSids) "owner $releaseOwner; others: $($releaseWriters -join ', ')"

    # --- Install, as the setup wizard runs it: unattended, the passwords in its environment ---
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $installer, '-Unattended')
    if ($pgInstaller) { $arguments += @('-PostgresInstaller', $pgInstaller.FullName) }
    if ($Installers) { $arguments += @('-DotNetInstallers', $Installers) }
    # Every process's command line while it runs: no password may show in any.
    $commandLines = Join-Path $report 'command-lines.txt'
    $watcher = Start-Job -ArgumentList $commandLines {
        param($file)
        $seen = New-Object 'Collections.Generic.HashSet[string]'
        while ($true) {
            foreach ($line in @(Get-CimInstance Win32_Process | ForEach-Object { $_.CommandLine } | Where-Object { $_ })) {
                if ($seen.Add($line)) { Add-Content -LiteralPath $file -Value $line }
            }
            Start-Sleep -Milliseconds 200
        }
    }
    $env:PULSAR_ADMIN_PASSWORD = $adminPassword
    if (-not $pgInstaller) { $env:PULSAR_POSTGRES_PASSWORD = $PostgresPassword }
    $output = & {
        $ErrorActionPreference = 'Continue'
        @(& powershell.exe @arguments 2>&1 | ForEach-Object { "$_" })
    }
    $installExit = $LASTEXITCODE
    Remove-Item Env:PULSAR_ADMIN_PASSWORD, Env:PULSAR_POSTGRES_PASSWORD -ErrorAction SilentlyContinue
    Stop-Job $watcher
    Remove-Job $watcher -Force
    $output | Set-Content (Join-Path $report 'install.txt')
    Check 'installer completed (unattended: exit code 0, or 3010 for a restart .NET asks for)' ($installExit -in 0, 3010) "exit $installExit; $(@($output | Where-Object { $_ -like 'PULSAR-ERROR *' }) -join ' ')"
    $steps = @($output | Where-Object { $_ -match '^PULSAR-STEP \d+/\d+ ' } | ForEach-Object { ($_ -split ' ')[1] })
    Check 'installer: progress lines for each step of an all-in-one install, in order' (($steps -join ',') -eq '1/7,2/7,3/7,4/7,5/7,6/7,7/7') ($steps -join ',')
    Check 'installer: the panel''s address at the end' (@($output | Where-Object { $_ -ceq 'PULSAR-PANEL-URL https://localhost:8443/' }).Count -eq 1)
    $logLine = @($output | Where-Object { $_ -like 'PULSAR-LOG *' }) | Select-Object -First 1
    $log = if ($logLine) { $logLine.Substring('PULSAR-LOG '.Length) } else { '' }
    $logText = if ($log -and (Test-Path -LiteralPath $log)) { Get-Content -Raw -LiteralPath $log } else { '' }
    Check 'installer: a log in C:\ProgramData\Pulsar\Setup\logs, without the passwords' ($log -like 'C:\ProgramData\Pulsar\Setup\logs\install-*.log' -and $logText -match 'PULSAR-STEP 7/7' -and -not $logText.Contains($adminPassword) -and -not $logText.Contains($PostgresPassword)) $log
    $seenLines = if (Test-Path $commandLines) { Get-Content -Raw $commandLines } else { '' }
    Check 'installer: no password on any command line while it ran' ($seenLines -and -not $seenLines.Contains($adminPassword) -and -not $seenLines.Contains($PostgresPassword) -and $seenLines -notmatch '(?i)superpassword')
    # The random password EDB's installer got is unknown here: what its helpers were started with is in
    # report\command-lines.txt, for a person to read.
    $edbHelpers = @(($seenLines -split "`r?`n") | Where-Object { $_ -match '(?i)initcluster|cscript|initdb' })
    Add-Content $summary "INFO command lines of PostgreSQL's installer helpers seen: $($edbHelpers.Count) (report\command-lines.txt)"
    $setupLeft = @(Get-ChildItem 'C:\ProgramData\Pulsar\Setup' -Recurse -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'downloads-*' -or $_.Name -like '*options*' })
    Check 'installer: its downloads and PostgreSQL''s options file are gone' ($setupLeft.Count -eq 0) (($setupLeft | ForEach-Object FullName) -join ', ')
    $runtimes = @('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App' | Where-Object { @(Get-ChildItem "C:\Program Files\dotnet\shared\$_" -Directory -Filter '10.0.*' -ErrorAction SilentlyContinue).Count -gt 0 })
    Check 'installer: the .NET 10 runtime and ASP.NET Core runtime are installed' ($runtimes.Count -eq 2) ($runtimes -join ', ')
    if ($pgInstaller) {
        Check "installer: PostgreSQL 18 installed as $pgService, running" ((Get-Service $pgService -ErrorAction SilentlyContinue).Status -eq 'Running')
        $listening = @(Get-NetTCPConnection -LocalPort 5432 -State Listen -ErrorAction SilentlyContinue | ForEach-Object { $_.LocalAddress } | Select-Object -Unique)
        Check 'installer: PostgreSQL listens on this machine only' ($listening.Count -gt 0 -and @($listening | Where-Object { $_ -notin '127.0.0.1', '::1' }).Count -eq 0) ($listening -join ', ')
        Check 'installer: no pgAdmin and no Stack Builder' (-not (Test-Path 'C:\Program Files\PostgreSQL\18\pgAdmin 4') -and -not (Test-Path 'C:\Program Files\PostgreSQL\18\stackbuilder'))
        # Its superuser's password is kept nowhere: set one as the guide says, for the drop and restore below.
        Check "postgres password set as install.md says" (Set-PostgresPassword $pgService $pgData 'C:\Program Files\PostgreSQL\18\bin' $PostgresPassword) (Get-Content (Join-Path $report 'pg-password.txt') -Raw -ErrorAction SilentlyContinue)
    }

    $panel = Get-CimInstance Win32_Service -Filter "Name='PulsarPanel'"
    Check 'panel service: own virtual account, automatic start, running' ($panel.StartName -eq 'NT SERVICE\PulsarPanel' -and $panel.StartMode -eq 'Auto' -and $panel.State -eq 'Running') "$($panel.StartName) $($panel.StartMode) $($panel.State)"
    $agent = Get-CimInstance Win32_Service -Filter "Name='PulsarAgent'"
    Check 'agent service: LocalSystem, running' ($agent.StartName -eq 'LocalSystem' -and $agent.State -eq 'Running') "$($agent.StartName) $($agent.State)"

    # By SID against what is allowed (any other principal fails, whatever its name): SYSTEM, Administrators and the panel's
    # virtual account.
    $panelSid = (New-Object Security.Principal.NTAccount 'NT SERVICE\PulsarPanel').Translate([Security.Principal.SecurityIdentifier]).Value
    $configAcl = (Get-Acl 'C:\Program Files\Pulsar\Panel\appsettings.Production.json').GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) |
        ForEach-Object { $_.IdentityReference.Value } | Select-Object -Unique
    $configOthers = @($configAcl | Where-Object { $_ -notin @('S-1-5-18', 'S-1-5-32-544', $panelSid) })
    Check "panel settings readable only by SYSTEM, Administrators and the panel's account" ($configAcl.Count -gt 0 -and $configOthers.Count -eq 0) "others: $($configOthers -join ', '); all: $($configAcl -join ', ')"
    $settings = Get-Content 'C:\Program Files\Pulsar\Panel\appsettings.Production.json' -Raw | ConvertFrom-Json
    Check 'setup token removed from the settings' ($null -eq $settings.PSObject.Properties['InitialSetup'])
    # Written in a private folder next to it and moved into place: none left behind.
    Check 'panel settings written without a staging folder left' (-not (Test-Path 'C:\Program Files\Pulsar\Panel\.settings'))
    Check 'Data Protection keys written' ((Get-ChildItem (Join-Path $panelData 'keys') -Filter 'key-*.xml').Count -ge 1)
    $panelCertificate = New-Object Security.Cryptography.X509Certificates.X509Certificate2((Join-Path $panelData 'tls\panel.cer'))
    $thumbprint = $panelCertificate.Thumbprint
    $constraints = $panelCertificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.19' }
    $endEntity = $null -ne $constraints -and $constraints.Critical -and -not (New-Object Security.Cryptography.X509Certificates.X509BasicConstraintsExtension($constraints, $true)).CertificateAuthority
    Check "panel's certificate cannot issue certificates (Basic Constraints: critical, CA=false)" $endEntity
    $usage = $panelCertificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' }
    $uses = if ($usage) { @((New-Object Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension($usage, $usage.Critical)).EnhancedKeyUsages | ForEach-Object { $_.Value }) } else { @() }
    Check "panel's certificate is for a web server only (Extended Key Usage: server authentication)" (($uses -join ',') -eq '1.3.6.1.5.5.7.3.1') ($uses -join ', ')
    $trusted = Get-Item "Cert:\LocalMachine\Root\$thumbprint" -ErrorAction SilentlyContinue
    Check "panel's certificate trusted by this machine, marked as the installer's" ($null -ne $trusted -and $trusted.FriendlyName -eq 'Pulsar panel (trusted by Install-Pulsar.ps1)') "$thumbprint $($trusted.FriendlyName)"
    # Its addresses as IP addresses ([7] iPAddress, 4 or 16 bytes: browsers match an address only against those), and its
    # SHA-256 at the end of the installer's output for the setup's last page.
    $alternative = $panelCertificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.17' }
    $alternativeHex = if ($alternative) { [BitConverter]::ToString($alternative.RawData) } else { '' }
    $ipv6Loopback = '87-10-' + ((@(@(0) * 15) + @(1) | ForEach-Object { '{0:X2}' -f $_ }) -join '-')
    Check "panel's certificate lists 127.0.0.1 and ::1 as IP addresses" ($alternativeHex.Contains('87-04-7F-00-00-01') -and $alternativeHex.Contains($ipv6Loopback)) $alternativeHex
    $sha = [Security.Cryptography.SHA256]::Create()
    $panelHash = (($sha.ComputeHash($panelCertificate.RawData) | ForEach-Object { $_.ToString('x2') }) -join '')
    $sha.Dispose()
    Check "installer: the certificate's SHA-256 at the end (PULSAR-CERTIFICATE)" (@($output | Where-Object { $_ -ceq "PULSAR-CERTIFICATE $panelHash" }).Count -eq 1) $panelHash
    $firewall = Get-NetFirewallRule -Name 'Pulsar-Panel-HTTPS' -ErrorAction SilentlyContinue
    Check 'firewall rule for the panel, on Domain and Private networks only' ($null -ne $firewall -and "$($firewall.Profile)" -eq 'Domain, Private') "$($firewall.Profile)"
    $sftpFirewall = Get-NetFirewallRule -Name 'Pulsar-Panel-SFTP' -ErrorAction SilentlyContinue
    $sftpPort = if ($sftpFirewall) { "$(($sftpFirewall | Get-NetFirewallPortFilter).LocalPort)" } else { '' }
    Check 'firewall rule for SFTP (port 2022), on Domain and Private networks only' ($null -ne $sftpFirewall -and $sftpPort -eq '2022' -and "$($sftpFirewall.Profile)" -eq 'Domain, Private') "$sftpPort $($sftpFirewall.Profile)"

    # --- The panel as a browser sees it (the certificate is trusted now) ---
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $null = Invoke-WebRequest "$panelUrl/api/v1/auth/session" -SessionVariable web -UseBasicParsing
    function Api([string] $method, [string] $path, $body = $null) {
        $xsrf = ($web.Cookies.GetCookies($panelUrl) | Where-Object Name -eq '__Host-pulsar-xsrf').Value
        $json = if ($null -ne $body) { $body | ConvertTo-Json -Compress } else { $null }
        Invoke-RestMethod "$panelUrl$path" -Method $method -WebSession $web -Headers @{ 'X-XSRF-TOKEN' = $xsrf } -ContentType 'application/json' -Body $json
    }
    $login = Api POST '/api/v1/auth/login' @{ userName = 'admin'; password = $adminPassword }
    Check 'administrator signs in' ($login.user.userName -eq 'admin')
    $null = Invoke-RestMethod "$panelUrl/api/v1/auth/session" -WebSession $web
    $index = Invoke-WebRequest "$panelUrl/" -UseBasicParsing
    Check 'web app served' ($index.StatusCode -eq 200 -and $index.Content -match '<div id="root">')
    # The Account page's Panel certificate: the one this connection was served, described and downloaded.
    $described = Api GET '/api/v1/panel/certificate'
    $downloadedFile = Join-Path $report 'pulsar-panel.cer'
    Invoke-WebRequest "$panelUrl/api/v1/panel/certificate/download" -WebSession $web -UseBasicParsing -OutFile $downloadedFile
    $downloaded = [IO.File]::ReadAllBytes($downloadedFile)
    Check "the Account page's certificate is the panel's, one to trust, and so is its download" ($described.sha256 -ceq $panelHash -and $described.trustable -and
        [Convert]::ToBase64String($downloaded) -ceq [Convert]::ToBase64String($panelCertificate.RawData)) "$($described.sha256) $($described.trustable)"
    Check 'node online' (Wait-Until { @(Api GET '/api/v1/nodes' | Where-Object { $_.connected }).Count -eq 1 } 90)
    $node = @(Api GET '/api/v1/nodes')[0]
    Check 'node is this machine' ($node.name -eq $env:COMPUTERNAME) $node.name
    $ping = Api POST "/api/v1/nodes/$($node.id)/ping"
    Check 'signed ping accepted' ($ping.status -eq 'accepted')

    # --- Backup, then the panel is lost (service, program, data, database) and restored; the node lives on ---
    $backupRoot = 'C:\PulsarBackups'
    # The copy a nightly task runs as SYSTEM, where only administrators can change it; one where others can is refused.
    Check "backup script kept in the panel's folder" (Test-Path $installedBackup)
    $refused = Invoke-Refused (Join-Path $bundled 'Backup-Pulsar.ps1') @('-Destination', $backupRoot)
    Check 'backup script refuses to run from a folder other accounts can change (nothing written)' ($refused.ExitCode -ne 0 -and ($refused.Output -replace '\s', '') -match 'Nothingwasbackedup' -and -not (Test-Path $backupRoot)) "exit $($refused.ExitCode); $($refused.Output -replace '\s+', ' ')"
    $backedUp = $true
    try { & $installedBackup -Destination $backupRoot *>&1 | Tee-Object -FilePath (Join-Path $report 'backup.txt') } catch { $backedUp = $false; Add-Content (Join-Path $report 'backup.txt') "FAILED: $_" }
    $backup = Get-ChildItem $backupRoot -Directory -ErrorAction SilentlyContinue | Select-Object -First 1
    Check 'backup written (dump, keys, key ring, certificate, settings)' ($backedUp -and $backup -and (Test-Path (Join-Path $backup.FullName 'pulsar.dump')) -and (Test-Path (Join-Path $backup.FullName 'panel-data\keyring\keyring.pfx')) -and (Test-Path (Join-Path $backup.FullName 'panel-data\tls\panel.pfx')) -and (Test-Path (Join-Path $backup.FullName 'appsettings.Production.json')))
    $backupAcl = if ($backup) { Get-Acl $backup.FullName }
    $backupSids = @($backupAcl | ForEach-Object { $_.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) } | ForEach-Object { $_.IdentityReference.Value } | Select-Object -Unique | Sort-Object)
    Check 'backup folder: SYSTEM and Administrators only, protected' ($null -ne $backupAcl -and $backupAcl.AreAccessRulesProtected -and ($backupSids -join ',') -eq 'S-1-5-18,S-1-5-32-544') ($backupSids -join ', ')

    # A drive that keeps no permissions (FAT32, on a small virtual disk) cannot hold a backup: refused, nothing written.
    $fatDisk = Join-Path $env:TEMP 'pulsar-fat.vhd'
    $diskpart = Join-Path $env:TEMP 'pulsar-fat.txt'
    Set-Content $diskpart "create vdisk file=`"$fatDisk`" maximum=64 type=expandable`r`nselect vdisk file=`"$fatDisk`"`r`nattach vdisk`r`ncreate partition primary`r`nformat fs=fat32 quick`r`nassign letter=P"
    $null = & (Join-Path ([Environment]::SystemDirectory) 'diskpart.exe') /s $diskpart
    if (Test-Path 'P:\') {
        $refusedOutput = & {
            $ErrorActionPreference = 'Continue'
            (& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installedBackup -Destination 'P:\Backups' 2>&1 | ForEach-Object { "$_" }) -join ' '
        }
        $refusedCode = $LASTEXITCODE
        # What the backup left in its destination (Windows keeps files of its own in the volume's System Volume Information).
        $left = @(Get-ChildItem 'P:\Backups' -Recurse -Force -ErrorAction SilentlyContinue)
        Check 'backup refuses a drive without permissions (FAT32), leaving nothing there' ($refusedCode -ne 0 -and ($refusedOutput -replace '\s', '') -match 'NTFSorReFS' -and $left.Count -eq 0) "exit $refusedCode; left: $(($left | ForEach-Object { $_.FullName }) -join ', '); $($refusedOutput -replace '\s+', ' ')"
    }
    else {
        Add-Content $summary 'INFO backup on a FAT32 drive not checked: the virtual disk could not be made'
    }

    Stop-Service PulsarPanel
    $null = sc.exe delete PulsarPanel
    Start-Sleep -Seconds 2
    Remove-Item 'C:\Program Files\Pulsar\Panel', $panelData -Recurse -Force
    $env:PGPASSWORD = $PostgresPassword
    # The newest PostgreSQL service's programs, as the installer chose.
    $pgBinDir = Join-Path $env:ProgramFiles 'PostgreSQL-sandbox\bin'
    if ($pgInstaller -or -not $PgBin) {
        $pgBinDir = (Get-CimInstance Win32_Service -Filter "Name LIKE 'postgresql%' AND State = 'Running'" |
            ForEach-Object { if ($_.PathName -match '^"?([^"]+\\bin)\\pg_ctl\.exe') { $Matches[1] } } | Select-Object -First 1)
    }
    & (Join-Path $pgBinDir 'psql.exe') -h localhost -U postgres -d postgres -v ON_ERROR_STOP=1 -c 'DROP DATABASE pulsar WITH (FORCE)' -c 'DROP ROLE pulsar' *> (Join-Path $report 'drop.txt')
    Remove-Item Env:PGPASSWORD
    Check 'panel lost (database dropped)' ($LASTEXITCODE -eq 0)

    $restored = $true
    try {
        & $installer -Mode Panel -RestoreFrom $backup.FullName -PostgresPassword (ConvertTo-SecureString $PostgresPassword -AsPlainText -Force) *>&1 |
            Tee-Object -FilePath (Join-Path $report 'restore.txt')
    }
    catch { $restored = $false; Add-Content (Join-Path $report 'restore.txt') "FAILED: $_" }
    Check 'restore completed' $restored
    $signedIn = try {
        $null = Invoke-WebRequest "$panelUrl/api/v1/auth/session" -SessionVariable web -UseBasicParsing
        $relogin = Api POST '/api/v1/auth/login' @{ userName = 'admin'; password = $adminPassword }
        $null = Invoke-RestMethod "$panelUrl/api/v1/auth/session" -WebSession $web
        $relogin.user.userName -eq 'admin'
    } catch { $false }
    Check 'administrator signs in after the restore' $signedIn
    Check 'node reconnects to the restored panel' (Wait-Until { @(Api GET '/api/v1/nodes' | Where-Object { $_.connected }).Count -eq 1 } 120)
    # Signing needs the panel's key, which the restored key ring decrypts.
    Check 'signed ping accepted after the restore' ((Api POST "/api/v1/nodes/$($node.id)/ping").status -eq 'accepted')

    # --- Update in place: settings and data kept, both services back ---
    $updated = $true
    try { & $installer -Update *>&1 | Tee-Object -FilePath (Join-Path $report 'update.txt') } catch { $updated = $false; Add-Content (Join-Path $report 'update.txt') "FAILED: $_" }
    Check 'update completed' $updated
    Check 'panel ready after the update' (Wait-Until { (Invoke-WebRequest "$panelUrl/health/ready" -UseBasicParsing).StatusCode -eq 200 } 120)
    $null = Invoke-WebRequest "$panelUrl/api/v1/auth/session" -SessionVariable web -UseBasicParsing
    $null = Api POST '/api/v1/auth/login' @{ userName = 'admin'; password = $adminPassword }
    $null = Invoke-RestMethod "$panelUrl/api/v1/auth/session" -WebSession $web
    Check 'node online after the update' (Wait-Until { @(Api GET '/api/v1/nodes' | Where-Object { $_.connected }).Count -eq 1 } 120)

    $logs = Get-ChildItem (Join-Path $panelData 'logs') -Filter 'panel-*.log'
    Check 'panel log written' ($logs.Count -ge 1)
    $errors = @($logs | Get-Content | Where-Object { $_ -match '\] (ERR|FTL) ' })
    Check 'no errors in the panel log' ($errors.Count -eq 0) (($errors | Select-Object -First 3) -join ' | ')
    Copy-Item $logs.FullName $report
    Copy-Item 'C:\ProgramData\Pulsar\Agent\logs\*' $report -ErrorAction SilentlyContinue

    # --- Uninstall: services and programs gone, data and database kept ---
    $removed = $true
    try { & $installer -Uninstall *>&1 | Tee-Object -FilePath (Join-Path $report 'uninstall.txt') } catch { $removed = $false; Add-Content (Join-Path $report 'uninstall.txt') "FAILED: $_" }
    Check 'uninstall completed' $removed
    Check 'services removed' ($null -eq (Get-Service PulsarPanel, PulsarAgent -ErrorAction SilentlyContinue))
    Check 'programs removed' (-not (Test-Path 'C:\Program Files\Pulsar\Panel') -and -not (Test-Path 'C:\Program Files\Pulsar\Agent'))
    Check "certificate no longer trusted" (-not (Test-Path "Cert:\LocalMachine\Root\$thumbprint"))
    Check 'firewall rules removed' ($null -eq (Get-NetFirewallRule -Name 'Pulsar-Panel-HTTPS' -ErrorAction SilentlyContinue) -and $null -eq (Get-NetFirewallRule -Name 'Pulsar-Panel-SFTP' -ErrorAction SilentlyContinue))
    Check 'panel keys kept' ((Get-ChildItem (Join-Path $panelData 'keys') -Filter 'key-*.xml').Count -ge 1)
}
catch {
    Check 'unexpected error' $false "$_ $($_.ScriptStackTrace)"
}
finally {
    # For diagnosis: what Windows says about the services, and the panel's own log.
    $since = (Get-Date).AddHours(-2)
    Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Service Control Manager'; StartTime = $since } -ErrorAction SilentlyContinue |
        Where-Object Message -match 'Pulsar' | Format-List TimeCreated, Id, Message | Out-File (Join-Path $report 'events-system.txt')
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $since } -ErrorAction SilentlyContinue |
        Where-Object { $_.ProviderName -in '.NET Runtime', 'Application Error' } | Format-List TimeCreated, ProviderName, Message |
        Out-File (Join-Path $report 'events-application.txt')
    Copy-Item 'C:\ProgramData\Pulsar\Panel\logs\*' $report -ErrorAction SilentlyContinue
    Stop-Transcript | Out-Null
    Add-Content $summary 'done'
}

if (Select-String -Path $summary -Pattern '^FAIL' -Quiet) { exit 1 }
