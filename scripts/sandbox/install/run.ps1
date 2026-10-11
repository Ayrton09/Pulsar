# Installs Pulsar from the release ZIP with Install-Pulsar.ps1 on a disposable, elevated Windows (Windows Sandbox
# through prepare.ps1, or a CI runner), as a user following docs/guides/install.md would: panel and agent on one PC,
# database created in the local PostgreSQL service. Then checks what the installer left, updates and uninstalls.
# Every check is written to report\summary.txt as PASS/FAIL; the last line is "done", and the exit code is 1 on any FAIL.
# Never run it on a machine you use: it installs services and trusts a certificate machine-wide.
param(
    [Parameter(Mandatory)] [string] $Bundle,
    # PostgreSQL binaries: registered here as a service, as PostgreSQL's installer does. Without it, the machine's
    # PostgreSQL service is used (CI runners have one) with -PostgresPassword.
    [string] $PgBin,
    [string] $PostgresPassword = 'sandbox-postgres-password')
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

$release = Join-Path $Bundle 'release'
$installer = Join-Path $release 'Install-Pulsar.ps1'
$panelUrl = 'https://localhost:8443'
$adminPassword = 'sandbox administrator password'
$panelData = 'C:\ProgramData\Pulsar\Panel'

try {
    # --- PostgreSQL as its installer leaves it: a service, a superuser password ---
    if ($PgBin) {
        $pgHome = 'C:\pgsql-local'
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
    Check 'postgresql service installed' ($null -ne (Get-Service 'postgresql*' -ErrorAction SilentlyContinue))

    # --- Install, as the guide says (here with the passwords given instead of typed) ---
    $installed = $true
    try {
        & $installer -PostgresPassword (ConvertTo-SecureString $PostgresPassword -AsPlainText -Force) `
            -AdminPassword (ConvertTo-SecureString $adminPassword -AsPlainText -Force) *>&1 | Tee-Object -FilePath (Join-Path $report 'install.txt')
    }
    catch {
        $installed = $false
        Add-Content (Join-Path $report 'install.txt') "FAILED: $_"
    }
    Check 'installer completed' $installed

    $panel = Get-CimInstance Win32_Service -Filter "Name='PulsarPanel'"
    Check 'panel service: own virtual account, automatic start, running' ($panel.StartName -eq 'NT SERVICE\PulsarPanel' -and $panel.StartMode -eq 'Auto' -and $panel.State -eq 'Running') "$($panel.StartName) $($panel.StartMode) $($panel.State)"
    $agent = Get-CimInstance Win32_Service -Filter "Name='PulsarAgent'"
    Check 'agent service: LocalSystem, running' ($agent.StartName -eq 'LocalSystem' -and $agent.State -eq 'Running') "$($agent.StartName) $($agent.State)"

    $configAcl = (Get-Acl 'C:\Program Files\Pulsar\Panel\appsettings.Production.json').Access | ForEach-Object { $_.IdentityReference.Value }
    Check "panel settings readable only by SYSTEM, Administrators and the panel's account" (-not ($configAcl | Where-Object { $_ -match 'Users|Everyone|Authenticated' })) ($configAcl -join ', ')
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
    $trusted = Get-Item "Cert:\LocalMachine\Root\$thumbprint" -ErrorAction SilentlyContinue
    Check "panel's certificate trusted by this machine, marked as the installer's" ($null -ne $trusted -and $trusted.FriendlyName -eq 'Pulsar panel (trusted by Install-Pulsar.ps1)') "$thumbprint $($trusted.FriendlyName)"
    $firewall = Get-NetFirewallRule -Name 'Pulsar-Panel-HTTPS' -ErrorAction SilentlyContinue
    Check 'firewall rule for the panel, on Domain and Private networks only' ($null -ne $firewall -and "$($firewall.Profile)" -eq 'Domain, Private') "$($firewall.Profile)"

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
    Check 'node online' (Wait-Until { @(Api GET '/api/v1/nodes' | Where-Object { $_.connected }).Count -eq 1 } 90)
    $node = @(Api GET '/api/v1/nodes')[0]
    Check 'node is this machine' ($node.name -eq $env:COMPUTERNAME) $node.name
    $ping = Api POST "/api/v1/nodes/$($node.id)/ping"
    Check 'signed ping accepted' ($ping.status -eq 'accepted')

    # --- Backup, then the panel is lost (service, program, data, database) and restored; the node lives on ---
    $backupRoot = 'C:\PulsarBackups'
    $backedUp = $true
    try { & (Join-Path $release 'Backup-Pulsar.ps1') -Destination $backupRoot *>&1 | Tee-Object -FilePath (Join-Path $report 'backup.txt') } catch { $backedUp = $false; Add-Content (Join-Path $report 'backup.txt') "FAILED: $_" }
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
            (& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $release 'Backup-Pulsar.ps1') -Destination 'P:\Backups' 2>&1 | ForEach-Object { "$_" }) -join ' '
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
    $pgBinDir = 'C:\pgsql-local\bin'
    if (-not $PgBin) {
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
    Check 'firewall rule removed' ($null -eq (Get-NetFirewallRule -Name 'Pulsar-Panel-HTTPS' -ErrorAction SilentlyContinue))
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
