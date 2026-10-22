# Installs Pulsar with its setup wizard, PulsarSetup-<version>.exe, on a disposable, elevated Windows (Windows Sandbox
# through prepare.ps1), as a user following docs/guides/install.md "Easy install" would, and checks what roadmap Phase
# 18's "Done when" asks that one machine can show:
# - without network: without internet and without the installers it would download, the setup says so (its exit code)
#   and changes nothing; a tampered installer is refused, changing nothing; then the install with the installers given
#   (/DOTNETINSTALLERS=, /POSTGRESINSTALLER=), checked again inside the sandbox;
# - with -Online: the install downloads the newest .NET 10 and PostgreSQL 18 itself and checks them.
# Then: the services, PostgreSQL listening on this machine only, no password on any process's command line, in the
# setup's and the installer's logs, the registry entry or the setup's folder, the sign-in page answering, the
# administrator signing in, this PC online as a node, the agent's setup served by the panel, PulsarAgentSetup.exe
# refusing a PC that runs the panel, an update with the same setup, and the removal from Apps (services, firewall rules
# and trust gone, data and PostgreSQL kept).
# Silent by default (the secrets in the setup's environment, as install.md's silent installs say); with -Wizard, the
# wizard is run by hand with the administrator password shown first. Every check is written to report\summary.txt as
# PASS/FAIL; the last line is "done", and the exit code is 1 on any FAIL.
# Never run it on a machine you use: it installs services and runtimes, and trusts certificates machine-wide.
param(
    [Parameter(Mandatory)] [string] $Bundle,
    [Parameter(Mandatory)] [string] $Setup,
    [Parameter(Mandatory)] [string] $AgentSetup,
    [Parameter(Mandatory)] [string] $Version,
    # The installers prepare.ps1 mapped (.NET's and EDB's), and EDB's in it.
    [string] $Installers,
    [string] $PostgresInstaller,
    # The sandbox has network: the setup downloads what it needs.
    [switch] $Online,
    [switch] $Wizard)
$ErrorActionPreference = 'Stop'
$report = New-Item -ItemType Directory -Force (Join-Path $Bundle 'report')
$summary = Join-Path $report 'summary.txt'
Set-Content $summary "setup check $(Get-Date -Format o) on $([Environment]::OSVersion.VersionString): PulsarSetup $Version, $(if ($Online) { 'online' } else { 'offline' })$(if ($Wizard) { ', wizard' })"
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

$appKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{8D0B5E7C-4A61-4F2B-9C3E-2B7D1A9F6E54}_is1'
$setupFolder = 'C:\Program Files\Pulsar\Setup'
$panelUrl = 'https://localhost:8443'
$adminPassword = 'sandbox administrator password'
$trustedSids = @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')

# Runs a setup (or its uninstaller) and waits for it: its exit code.
function Invoke-Setup([string] $exe, [string] $arguments) {
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -Wait -PassThru
    return $process.ExitCode
}

# Whether nothing of Pulsar is on this machine, as before the first install: no services, no entry in Apps, no setup
# folder, no PostgreSQL and no .NET 10 the setup would have installed. What is there, otherwise.
function Get-PulsarLeftovers {
    @(
        if (Get-Service PulsarPanel, PulsarAgent -ErrorAction SilentlyContinue) { 'services' }
        if (Test-Path $appKey) { 'entry in Apps' }
        if (Test-Path $setupFolder) { $setupFolder }
        if (Get-Service 'postgresql*' -ErrorAction SilentlyContinue) { 'a PostgreSQL service' }
        if (Get-ChildItem 'C:\Program Files\dotnet\shared\Microsoft.NETCore.App' -Directory -Filter '10.0.*' -ErrorAction SilentlyContinue) { '.NET 10' }
    )
}

# The newest installer log (Install-Pulsar.ps1's own) and its text.
function Get-InstallerLog {
    $log = Get-ChildItem 'C:\ProgramData\Pulsar\Setup\logs' -Filter 'install-*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $log) { return [pscustomobject]@{ Path = ''; Text = '' } }
    return [pscustomobject]@{ Path = $log.FullName; Text = (Get-Content -Raw -LiteralPath $log.FullName) }
}

# Whether a file holds $secret, in UTF-8 or UTF-16 (Inno Setup's files, logs).
function Test-FileHolds([string] $path, [string] $secret) {
    $bytes = [IO.File]::ReadAllBytes($path)
    $text = [Text.Encoding]::GetEncoding(28591).GetString($bytes)
    foreach ($encoding in [Text.Encoding]::UTF8, [Text.Encoding]::Unicode) {
        if ($text.Contains([Text.Encoding]::GetEncoding(28591).GetString($encoding.GetBytes($secret)))) { return $true }
    }
    return $false
}

$offlineArguments = ''
if ($Installers) { $offlineArguments = " /DOTNETINSTALLERS=`"$Installers`" /POSTGRESINSTALLER=`"$PostgresInstaller`"" }
$silent = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART'

try {
    if (-not $Online -and $Installers -and (Test-Path (Join-Path $Bundle 'roots'))) {
        foreach ($root in Get-ChildItem (Join-Path $Bundle 'roots') -Filter '*.cer') {
            $null = Import-Certificate -FilePath $root.FullName -CertStoreLocation 'Cert:\LocalMachine\Root'
        }
    }
    $before = @(Get-PulsarLeftovers)
    Check 'a clean machine: no Pulsar, PostgreSQL or .NET 10 before the setup' ($before.Count -eq 0) ($before -join ', ')

    $env:PULSAR_ADMIN_PASSWORD = $adminPassword
    # --- Done when 4: without internet, the setup says so and changes nothing ---
    if (-not $Online) {
        $code = Invoke-Setup $Setup "$silent /LOG=`"$report\setup-offline.log`""
        $left = @(Get-PulsarLeftovers)
        $log = Get-InstallerLog
        Check 'offline, without the installers: the setup rolls back (exit code 5) and changes nothing' ($code -eq 5 -and $left.Count -eq 0) "exit $code; left: $($left -join ', ')"
        Check 'offline: the installer''s log says what it could not download' ($log.Text -match 'FAILED \(exit code 4\)' -and $log.Text -match 'could not be downloaded') $log.Path
    }

    # --- Done when 6: a tampered installer is refused, changing nothing ---
    if ($Installers) {
        $tampered = New-Item -ItemType Directory -Force 'C:\tampered'
        foreach ($file in Get-ChildItem $Installers -File | Where-Object { $_.Name -like 'dotnet-runtime-*' -or $_.Name -like 'aspnetcore-runtime-*' }) {
            Copy-Item $file.FullName $tampered
        }
        $victim = Get-ChildItem $tampered -Filter 'dotnet-runtime-*' | Select-Object -First 1
        $bytes = [IO.File]::ReadAllBytes($victim.FullName)
        $bytes[[int]($bytes.Length / 2)] = $bytes[[int]($bytes.Length / 2)] -bxor 0xFF
        [IO.File]::WriteAllBytes($victim.FullName, $bytes)
        $code = Invoke-Setup $Setup "$silent /LOG=`"$report\setup-tampered.log`" /DOTNETINSTALLERS=`"$($tampered.FullName)`" /POSTGRESINSTALLER=`"$PostgresInstaller`""
        $left = @(Get-PulsarLeftovers)
        $log = Get-InstallerLog
        Check 'a tampered .NET installer: refused (installer exit code 5), the setup rolls back (exit code 5) and changes nothing' ($code -eq 5 -and $left.Count -eq 0 -and $log.Text -match 'FAILED \(exit code 5\)') "exit $code; left: $($left -join ', '); $($log.Path)"
        Remove-Item $tampered -Recurse -Force
    }

    # --- An install that changes nothing rolls the setup back: a Cancel that asks nothing, or, very silent, the guard ---
    # file that cannot be put in place (Inno Setup 7 carries on after an exception or an Abort from AfterInstall).
    foreach ($mode in '/VERYSILENT', '/SILENT') {
        $name = $mode.TrimStart('/').ToLowerInvariant()
        $rollbackLog = Join-Path $report "setup-rollback-$name.log"
        $code = Invoke-Setup $Setup "$mode /SUPPRESSMSGBOXES /NORESTART /LOG=`"$rollbackLog`" /MODE=AllInOne /POSTGRESINSTALLER=`"C:\missing\postgresql-18.6-1-windows-x64.exe`""
        $left = @(Get-PulsarLeftovers)
        $text = if (Test-Path $rollbackLog) { Get-Content -Raw $rollbackLog } else { '' }
        $guard = $text -match 'Defaulting to Abort for suppressed message box'
        Check "an install that changed nothing ($mode): the setup rolls back (exit code 5), $(if ($mode -eq '/VERYSILENT') { 'through the guard file' } else { 'by its own Cancel' }), leaving nothing" (
            $code -eq 5 -and $left.Count -eq 0 -and -not (Test-Path 'C:\Program Files\Pulsar') -and $text -match 'Rolling back changes' -and $guard -eq ($mode -eq '/VERYSILENT')) "exit $code; left: $($left -join ', '); guard: $guard"
    }

    # --- Done when 1: the install, with the defaults ---
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
    if ($Wizard) {
        Remove-Item Env:PULSAR_ADMIN_PASSWORD
        Add-Type -AssemblyName System.Windows.Forms
        $null = [Windows.Forms.MessageBox]::Show("The setup wizard opens next. Keep the defaults (the panel and this PC as a node) and use this administrator password:`n`n$adminPassword`n`nAt the end, check that the Finished page shows https://localhost:8443/ to copy and opens no browser itself, open that address in the browser, then close the browser.", 'Pulsar setup check')
        $code = Invoke-Setup $Setup "/LOG=`"$report\setup.log`"$offlineArguments"
        Add-Content $summary 'INFO wizard: whether the Finished page showed the sign-in page to copy is for the person who ran it to say'
    }
    else {
        $code = Invoke-Setup $Setup "$silent /LOG=`"$report\setup.log`" /MODE=AllInOne$offlineArguments"
    }
    Remove-Item Env:PULSAR_ADMIN_PASSWORD -ErrorAction SilentlyContinue
    Stop-Job $watcher
    Remove-Job $watcher -Force
    Check 'setup completed (exit code 0)' ($code -eq 0) "exit $code"
    $log = Get-InstallerLog
    $steps = @(([regex]::Matches($log.Text, 'PULSAR-STEP (\d+/\d+) ') | ForEach-Object { $_.Groups[1].Value }))
    Check 'installer: every step of an all-in-one install, in order' (($steps -join ',') -eq '1/7,2/7,3/7,4/7,5/7,6/7,7/7') "$($steps -join ',') in $($log.Path)"
    if ($Online) {
        Check 'online: the installer downloaded the newest .NET 10 and PostgreSQL 18 (checked before they ran)' ($log.Text -match 'Downloading Microsoft\.NETCore\.App 10\.0\.\d+ from Microsoft' -and $log.Text -match 'Downloading Microsoft\.AspNetCore\.App 10\.0\.\d+ from Microsoft' -and $log.Text -match 'Downloading PostgreSQL 18\.\d+ from EDB') $log.Path
    }
    $seenLines = if (Test-Path $commandLines) { Get-Content -Raw $commandLines } else { '' }
    Check 'no password on any command line while the setup ran' ($seenLines -and -not $seenLines.Contains($adminPassword) -and $seenLines -notmatch '(?i)superpassword') 'report\command-lines.txt'
    $setupLine = @(($seenLines -split "`r?`n") | Where-Object { $_ -match 'Install-Pulsar\.ps1' } | Select-Object -First 1)
    Check 'the installer ran as 64-bit Windows PowerShell from System32, unattended, from the setup''s folder' ($setupLine.Count -eq 1 -and $setupLine[0] -match '(?i)\\System32\\WindowsPowerShell\\v1\.0\\powershell\.exe' -and $setupLine[0] -match '-Unattended' -and $setupLine[0] -match [regex]::Escape("$setupFolder\payload\Install-Pulsar.ps1")) "$setupLine"
    Check 'the installer''s log has no password' ($log.Text -and -not $log.Text.Contains($adminPassword)) $log.Path
    Check 'the setup''s own log has no password' ((Test-Path "$report\setup.log") -and -not (Test-FileHolds "$report\setup.log" $adminPassword))

    # The setup's entry in Apps and its folder.
    $entry = Get-ItemProperty $appKey -ErrorAction SilentlyContinue
    Check 'an entry in Apps: Pulsar, by Pulsar contributors, this version' ($null -ne $entry -and $entry.DisplayName -eq 'Pulsar' -and $entry.Publisher -eq 'Pulsar contributors' -and $entry.DisplayVersion -eq $Version) "$($entry.DisplayName) / $($entry.Publisher) / $($entry.DisplayVersion)"
    $values = if ($entry) { ($entry.PSObject.Properties | ForEach-Object { "$($_.Value)" }) -join "`n" } else { '' }
    Check 'the entry in Apps keeps no password and no wizard values' (-not $values.Contains($adminPassword) -and $values -notmatch 'Inno Setup CodeFile')
    $payload = @(Get-ChildItem (Join-Path $setupFolder 'payload') -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
    Check 'the setup''s folder keeps only Install-Pulsar.ps1 of the release (for the uninstaller)' (($payload -join ',') -eq 'Install-Pulsar.ps1') ($payload -join ',')
    $writeRights = 0x2 -bor 0x4 -bor 0x10 -bor 0x40 -bor 0x100 -bor 0x10000 -bor 0x40000 -bor 0x80000 -bor 0x10000000 -bor 0x40000000
    $writers = foreach ($item in @(Get-Item $setupFolder) + @(Get-ChildItem $setupFolder -Recurse -Force)) {
        $acl = Get-Acl -LiteralPath $item.FullName
        if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trustedSids) { "$($item.FullName) owner" }
        $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) |
            Where-Object { $_.AccessControlType -eq 'Allow' -and ([int]$_.PropagationFlags -band 2) -eq 0 -and ([int]$_.FileSystemRights -band $writeRights) -ne 0 -and $_.IdentityReference.Value -notin $trustedSids } |
            ForEach-Object { "$($item.FullName) $($_.IdentityReference.Value)" }
    }
    Check 'the setup''s folder: only SYSTEM, Administrators and TrustedInstaller can change it' (@($writers).Count -eq 0) (@($writers) -join ', ')
    $holding = @(Get-ChildItem $setupFolder -Recurse -File -ErrorAction SilentlyContinue | Where-Object { Test-FileHolds $_.FullName $adminPassword } | ForEach-Object { $_.FullName })
    Check 'no file in the setup''s folder holds the password (the uninstaller''s log included)' ($holding.Count -eq 0) ($holding -join ', ')

    # What the installer installed.
    $panel = Get-CimInstance Win32_Service -Filter "Name='PulsarPanel'"
    Check 'panel service: own virtual account, running' ($panel.StartName -eq 'NT SERVICE\PulsarPanel' -and $panel.State -eq 'Running') "$($panel.StartName) $($panel.State)"
    $agent = Get-CimInstance Win32_Service -Filter "Name='PulsarAgent'"
    Check 'agent service: LocalSystem, running' ($agent.StartName -eq 'LocalSystem' -and $agent.State -eq 'Running') "$($agent.StartName) $($agent.State)"
    Check 'PostgreSQL 18 installed as postgresql-x64-18, running' ((Get-Service 'postgresql-x64-18' -ErrorAction SilentlyContinue).Status -eq 'Running')
    $listening = @(Get-NetTCPConnection -LocalPort 5432 -State Listen -ErrorAction SilentlyContinue | ForEach-Object { $_.LocalAddress } | Select-Object -Unique)
    Check 'PostgreSQL listens on this machine only' ($listening.Count -gt 0 -and @($listening | Where-Object { $_ -notin '127.0.0.1', '::1' }).Count -eq 0) ($listening -join ', ')
    $runtimes = @('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App' | Where-Object { @(Get-ChildItem "C:\Program Files\dotnet\shared\$_" -Directory -Filter '10.0.*' -ErrorAction SilentlyContinue).Count -gt 0 })
    Check 'the .NET 10 runtime and ASP.NET Core runtime are installed' ($runtimes.Count -eq 2) ($runtimes -join ', ')
    $firewall = @('Pulsar-Panel-HTTPS', 'Pulsar-Panel-SFTP' | Where-Object { "$((Get-NetFirewallRule -Name $_ -ErrorAction SilentlyContinue).Profile)" -eq 'Domain, Private' })
    Check 'firewall rules for the panel and SFTP, on Domain and Private networks' ($firewall.Count -eq 2) ($firewall -join ', ')

    # --- Done when 1 and 2: the sign-in page, the administrator, this PC online ---
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $page = Invoke-WebRequest "$panelUrl/" -UseBasicParsing
    Check 'the sign-in page answers at https://localhost:8443/ (its certificate trusted here)' ($page.StatusCode -eq 200 -and $page.Content -match '<div id="root">')
    $null = Invoke-WebRequest "$panelUrl/api/v1/auth/session" -SessionVariable web -UseBasicParsing
    function Api([string] $method, [string] $path, $body = $null) {
        $xsrf = ($web.Cookies.GetCookies($panelUrl) | Where-Object Name -eq '__Host-pulsar-xsrf').Value
        $json = if ($null -ne $body) { $body | ConvertTo-Json -Compress } else { $null }
        Invoke-RestMethod "$panelUrl$path" -Method $method -WebSession $web -Headers @{ 'X-XSRF-TOKEN' = $xsrf } -ContentType 'application/json' -Body $json
    }
    $login = Api POST '/api/v1/auth/login' @{ userName = 'admin'; password = $adminPassword }
    Check 'the administrator signs in' ($login.user.userName -eq 'admin')
    $null = Invoke-RestMethod "$panelUrl/api/v1/auth/session" -WebSession $web
    Check 'this PC is an online node' (Wait-Until { @(Api GET '/api/v1/nodes' | Where-Object { $_.connected -and $_.name -eq $env:COMPUTERNAME }).Count -eq 1 } 120)
    $download = Join-Path $env:TEMP 'PulsarAgentSetup.exe'
    Invoke-WebRequest "$panelUrl/downloads/PulsarAgentSetup.exe" -OutFile $download -UseBasicParsing
    Check 'the panel serves the release''s PulsarAgentSetup.exe' ((Get-FileHash $download).Hash -eq (Get-FileHash $AgentSetup).Hash)
    Remove-Item $download

    # PulsarAgentSetup.exe refuses a PC that runs the panel, changing nothing.
    $code = Invoke-Setup $AgentSetup "$silent /LOG=`"$report\agent-setup-refused.log`""
    Check 'PulsarAgentSetup.exe refuses a PC that runs the panel' ($code -ne 0 -and (Get-Service PulsarAgent).Status -eq 'Running') "exit $code"

    # --- The same setup again: an update in place, asking nothing ---
    $code = Invoke-Setup $Setup "$silent /LOG=`"$report\setup-update.log`"$offlineArguments"
    $log = Get-InstallerLog
    Check 'the setup again updates in place (Install-Pulsar.ps1 -Update)' ($code -eq 0 -and $log.Text -match '-Update:True' -and $log.Text -match 'Pulsar is updated') "exit $code; $($log.Path)"
    Check 'the panel is ready after the update' (Wait-Until { (Invoke-WebRequest "$panelUrl/health/ready" -UseBasicParsing).StatusCode -eq 200 } 120)
    $null = Invoke-WebRequest "$panelUrl/api/v1/auth/session" -SessionVariable web -UseBasicParsing
    $null = Api POST '/api/v1/auth/login' @{ userName = 'admin'; password = $adminPassword }
    $null = Invoke-RestMethod "$panelUrl/api/v1/auth/session" -WebSession $web
    Check 'the node is online after the update' (Wait-Until { @(Api GET '/api/v1/nodes' | Where-Object { $_.connected }).Count -eq 1 } 120)
    Copy-Item 'C:\ProgramData\Pulsar\Setup\logs\*' $report -ErrorAction SilentlyContinue

    # --- Done when 5: removed from Apps; data and PostgreSQL kept ---
    $thumbprint = (New-Object Security.Cryptography.X509Certificates.X509Certificate2('C:\ProgramData\Pulsar\Panel\tls\panel.cer')).Thumbprint
    $uninstaller = (Get-ItemProperty $appKey).UninstallString.Trim('"')
    $code = Invoke-Setup $uninstaller '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART'
    Check 'the uninstaller completed' ($code -eq 0 -and (Wait-Until { -not (Test-Path $appKey) } 300)) "exit $code"
    Check 'services removed' ($null -eq (Get-Service PulsarPanel, PulsarAgent -ErrorAction SilentlyContinue))
    Check 'programs and the setup''s folder removed' (-not (Test-Path 'C:\Program Files\Pulsar\Panel') -and -not (Test-Path 'C:\Program Files\Pulsar\Agent') -and -not (Test-Path $setupFolder))
    Check 'firewall rules removed' ($null -eq (Get-NetFirewallRule -Name 'Pulsar-Panel-HTTPS', 'Pulsar-Panel-SFTP' -ErrorAction SilentlyContinue))
    Check 'the panel''s certificate is no longer trusted' (-not (Test-Path "Cert:\LocalMachine\Root\$thumbprint"))
    Check 'the data is kept (the panel''s keys)' ((Get-ChildItem 'C:\ProgramData\Pulsar\Panel\keys' -Filter 'key-*.xml' -ErrorAction SilentlyContinue).Count -ge 1)
    $postgresEntry = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue | Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -like 'PostgreSQL 18*' }
    Check 'PostgreSQL stays, as its own app in Apps' ($null -ne $postgresEntry -and (Get-Service 'postgresql-x64-18').Status -eq 'Running') "$($postgresEntry.DisplayName)"
}
catch {
    Check 'unexpected error' $false "$_ $($_.ScriptStackTrace)"
}
finally {
    Remove-Item Env:PULSAR_ADMIN_PASSWORD -ErrorAction SilentlyContinue
    Copy-Item 'C:\ProgramData\Pulsar\Setup\logs\*' $report -ErrorAction SilentlyContinue
    Copy-Item 'C:\ProgramData\Pulsar\Panel\logs\*' $report -ErrorAction SilentlyContinue
    Stop-Transcript | Out-Null
    Add-Content $summary 'done'
}

if (Select-String -Path $summary -Pattern '^FAIL' -Quiet) { exit 1 }
