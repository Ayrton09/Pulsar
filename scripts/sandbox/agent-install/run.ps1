# Installs the agent the production way on a disposable, elevated Windows (Windows Sandbox through prepare.ps1, or a CI
# runner): PostgreSQL + panel on 127.0.0.1, then elevated enroll, machine key, PulsarAgent service as SYSTEM, and a
# test game server running as its own Windows account. Every check is written to report\summary.txt as PASS/FAIL; the
# last line is "done", and the exit code is 1 on any FAIL.
# When the machine has a ready TPM, the node key must be in it.
# Never run it on a machine you use: it installs a service and creates a machine key (removed again at the end).
param(
    [Parameter(Mandatory)] [string] $Bundle,
    # PostgreSQL binaries (initdb, pg_ctl, createdb); the sandbox maps them here.
    [string] $PgBin = 'C:\pgsql\bin')
$ErrorActionPreference = 'Stop'
$report = New-Item -ItemType Directory -Force (Join-Path $Bundle 'report')
$summary = Join-Path $report 'summary.txt'
Set-Content $summary "agent install check $(Get-Date -Format o) on $([Environment]::OSVersion.VersionString)"
Start-Transcript (Join-Path $report 'transcript.txt') | Out-Null

function Check([string] $name, [bool] $ok, [string] $detail = '') {
    Add-Content $summary ("{0} {1}{2}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $name, $(if ($detail) { " - $detail" } else { '' }))
}

function Invoke-Native([string] $exe) {
    # Windows PowerShell turns a native command's stderr into terminating errors under ErrorActionPreference=Stop;
    # here stderr is part of the output being checked. $LASTEXITCODE stays set for the caller.
    $ErrorActionPreference = 'Continue'
    (& $exe @args 2>&1 | ForEach-Object { "$_" }) -join [Environment]::NewLine
}

function Wait-Until([scriptblock] $condition, [int] $seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        try { if (& $condition) { return $true } } catch { }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

# HTTPS, as in production: a certificate made here and trusted machine-wide (the machine is disposable).
$panel = 'https://localhost:5291'
$setupToken = 'sandbox-setup-token-not-a-secret-0123456789'
$admin = @{ userName = 'sandbox-admin'; password = 'sandbox admin password' }
$agentSource = Join-Path $Bundle 'agent'
$installDir = 'C:\Program Files\Pulsar\Agent'
# Pulsar's test game server, where only administrators can change it (the agent copies it into server folders).
$testGameDir = 'C:\Program Files\Pulsar\TestGame'
$agentExe = Join-Path $installDir 'PulsarAgent.exe'
$dataDir = 'C:\ProgramData\Pulsar\Agent'

try {
    # --- Machine facts that decide what to expect ---
    $tpm = Get-Tpm
    Add-Content $summary "INFO TPM present: $($tpm.TpmPresent), ready: $($tpm.TpmReady)"

    # --- PostgreSQL (trust authentication on loopback: the machine is disposable) ---
    $env:PATH = "$(Join-Path $Bundle 'vcrt');$PgBin;$env:PATH"
    & initdb.exe -D C:\pgdata -U postgres -A trust -E UTF8 --no-locale *> (Join-Path $report 'initdb.txt')
    if ($LASTEXITCODE -ne 0) { throw 'initdb failed' }
    # pg_ctl's server inherits handles; through cmd with output to NUL the call returns. Not -Wait: in Windows
    # PowerShell it also waits for child processes, and the server keeps running.
    (Start-Process cmd.exe -ArgumentList '/d', '/c', 'pg_ctl -D C:\pgdata -l C:\pgdata\server.log -w start > NUL 2>&1 < NUL' -PassThru -WindowStyle Hidden).WaitForExit()
    & createdb.exe -h 127.0.0.1 -U postgres pulsar
    Check 'postgresql' ($LASTEXITCODE -eq 0)

    # --- TLS certificate for localhost, trusted by the whole machine (the agent service runs as SYSTEM) ---
    $certificate = New-SelfSignedCertificate -DnsName 'localhost' -CertStoreLocation 'Cert:\LocalMachine\My' -KeyExportPolicy Exportable -NotAfter (Get-Date).AddDays(2)
    $pfx = Join-Path $env:TEMP 'pulsar-panel.pfx'
    $null = Export-PfxCertificate -Cert $certificate -FilePath $pfx -Password (ConvertTo-SecureString 'agent-install' -AsPlainText -Force)
    $cer = Join-Path $env:TEMP 'pulsar-panel.cer'
    $null = Export-Certificate -Cert $certificate -FilePath $cer
    $null = Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\LocalMachine\Root'
    # Windows PowerShell may default to TLS 1.0, which the panel refuses.
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

    # --- Panel ---
    $env:ConnectionStrings__Pulsar = 'Host=127.0.0.1;Port=5432;Database=pulsar;Username=postgres'
    $env:InitialSetup__Token = $setupToken
    $env:Kestrel__Certificates__Default__Path = $pfx
    $env:Kestrel__Certificates__Default__Password = 'agent-install'
    $env:Pulsar__EnableTestGame = 'true'
    $panelDir = Join-Path $Bundle 'panel'
    Start-Process (Join-Path $panelDir 'Pulsar.Api.exe') -ArgumentList '--contentRoot', "`"$panelDir`"", '--urls', $panel `
        -RedirectStandardOutput (Join-Path $report 'panel.txt') -RedirectStandardError (Join-Path $report 'panel-error.txt') -WindowStyle Hidden
    Check 'panel ready' (Wait-Until { (Invoke-WebRequest "$panel/health/ready" -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200 } 90)

    $null = Invoke-WebRequest "$panel/api/v1/auth/session" -SessionVariable web -UseBasicParsing
    function Api([string] $method, [string] $path, $body = $null) {
        # Over HTTPS the panel uses __Host- cookie names.
        $xsrf = ($web.Cookies.GetCookies($panel) | Where-Object Name -in '__Host-pulsar-xsrf', 'pulsar-xsrf').Value
        $json = if ($null -ne $body) { $body | ConvertTo-Json -Compress } else { $null }
        Invoke-RestMethod "$panel$path" -Method $method -WebSession $web -Headers @{ 'X-XSRF-TOKEN' = $xsrf } -ContentType 'application/json' -Body $json
    }
    $null = Api POST '/api/v1/auth/setup' @{ token = $setupToken; userName = $admin.userName; password = $admin.password; displayName = 'Sandbox' }
    $null = Invoke-RestMethod "$panel/api/v1/auth/session" -WebSession $web
    $created = Api POST '/api/v1/nodes' @{ name = 'sandbox-node' }
    $nodeId = $created.node.id
    $token = $created.enrollment.token
    $panelKey = $created.enrollment.panelKeyFingerprint

    # --- The service must not be installed from a folder other accounts can write (here: Downloads) ---
    $unsafeDir = Join-Path $env:USERPROFILE 'Downloads\PulsarAgent'
    New-Item -ItemType Directory -Force (Split-Path $unsafeDir) | Out-Null
    Copy-Item $agentSource $unsafeDir -Recurse
    $refused = Invoke-Native (Join-Path $unsafeDir 'PulsarAgent.exe') enroll --panel $panel --token $token --panel-key $panelKey
    Check 'enroll refuses a user-writable folder' ($LASTEXITCODE -eq 1 -and $refused -match 'other accounts could replace it') ($refused.Trim() -replace '\s+', ' ')

    # --- Production install from Program Files ---
    New-Item -ItemType Directory -Force (Split-Path $installDir) | Out-Null
    Copy-Item $agentSource $installDir -Recurse
    Copy-Item (Join-Path $Bundle 'testgame') $testGameDir -Recurse
    # The service reads appsettings.Production.json next to its executable: this agent may install the test game.
    @{ Pulsar = @{ TestGameDirectory = $testGameDir } } | ConvertTo-Json | Set-Content (Join-Path $installDir 'appsettings.Production.json')
    # The service binary check reads these ACLs; keep them in the report for diagnosis.
    foreach ($path in 'C:\', 'C:\Program Files', (Split-Path $installDir), $installDir) {
        Add-Content (Join-Path $report 'acls.txt') (Invoke-Native icacls.exe $path)
    }
    $enrolled = Invoke-Native $agentExe enroll --panel $panel --token $token --panel-key $panelKey --servers-root C:\PulsarServers
    Set-Content (Join-Path $report 'enroll.txt') $enrolled
    Check 'enroll from Program Files (token was not consumed by the refusal)' ($LASTEXITCODE -eq 0) ($enrolled.Trim() -replace '\s+', ' ')

    $service = Get-CimInstance Win32_Service -Filter "Name='PulsarAgent'"
    Check 'service runs as LocalSystem, automatic start' ($service.StartName -eq 'LocalSystem' -and $service.StartMode -eq 'Auto') "$($service.StartName) $($service.StartMode) $($service.PathName)"
    # sc.exe output is localized: check the delays it prints and the non-crash flag in the registry instead.
    $failure = & sc.exe qfailure PulsarAgent | Out-String
    $nonCrash = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\PulsarAgent').FailureActionsOnNonCrashFailures
    Check 'service restarts on failure (5 s, 15 s, 60 s; also on error exits)' ($failure -match '= 5000 ' -and $failure -match '= 15000 ' -and $failure -match '= 60000 ' -and $nonCrash -eq 1) ($failure.Trim() -replace '\s+', ' ')
    Check 'service running' (Wait-Until { (Get-Service PulsarAgent).Status -eq 'Running' } 30)

    Check 'node online in the panel' (Wait-Until { (Api GET "/api/v1/nodes/$nodeId").node.connected } 60)
    $details = Api GET "/api/v1/nodes/$nodeId"
    Check 'inventory reported' ($details.node.inventory.hostname -eq $env:COMPUTERNAME -and $details.node.inventory.serversRoot -eq 'C:\PulsarServers') "$($details.node.inventory.osDescription) $($details.node.inventory.osBuild)"
    $credential = $details.credentials | Select-Object -First 1
    if ($tpm.TpmPresent -and $tpm.TpmReady) {
        Check 'machine key in the TPM' ($credential.keyStorage -eq 'tpm') "key storage: $($credential.keyStorage)"
    }
    else {
        Add-Content $summary "INFO key storage: $($credential.keyStorage) (no ready TPM on this machine)"
    }

    $ping = Api POST "/api/v1/nodes/$nodeId/ping"
    Check 'signed ping accepted by the service' ($ping.status -eq 'accepted') "$([math]::Round($ping.roundTripMilliseconds, 1)) ms"

    # --- What enroll left on disk and in the key store ---
    $acl = Get-Acl $dataDir
    $principals = ($acl.Access | ForEach-Object { $_.IdentityReference.Value }) | Sort-Object -Unique
    $sids = $principals | ForEach-Object { (New-Object Security.Principal.NTAccount $_).Translate([Security.Principal.SecurityIdentifier]).Value }
    Check 'state directory: SYSTEM + Administrators only, protected' ($acl.AreAccessRulesProtected -and (@($sids | Sort-Object) -join ',') -eq 'S-1-5-18,S-1-5-32-544') "owner $($acl.Owner); $($principals -join ', ')"

    $config = Get-Content (Join-Path $dataDir 'config\agent.json') -Raw | ConvertFrom-Json
    Check 'machine key in config' ($config.key.scope -eq 'machine' -and -not $config.development) "$($config.key.name) $($config.key.storage)"
    Check 'agent talks to the panel over HTTPS' ($config.panel -eq "$panel/") $config.panel
    $provider = if ($config.key.storage -eq 'tpm') { [Security.Cryptography.CngProvider]::new('Microsoft Platform Crypto Provider') } else { [Security.Cryptography.CngProvider]::MicrosoftSoftwareKeyStorageProvider }
    $key = [Security.Cryptography.CngKey]::Open($config.key.name, $provider, [Security.Cryptography.CngKeyOpenOptions]::MachineKey)
    $descriptor = New-Object Security.AccessControl.RawSecurityDescriptor -ArgumentList ($key.GetProperty('Security Descr', [Security.Cryptography.CngPropertyOptions]4).GetValue()), 0
    $sddl = $descriptor.GetSddlForm([Security.AccessControl.AccessControlSections]::Access)
    Check 'key is non-exportable' ($key.ExportPolicy -eq [Security.Cryptography.CngExportPolicies]::None) "$($key.ExportPolicy)"
    # The key store stores "generic all" as its specific rights mask, so only the principals are compared.
    Check 'key DACL: protected, SYSTEM + Administrators only' ($sddl -match '^D:P' -and $sddl -match '\(A;;[^;]+;;;SY\)' -and $sddl -match '\(A;;[^;]+;;;BA\)' -and ($sddl -split '\(A;').Count -eq 3) $sddl
    $key.Dispose()

    $status = Invoke-Native $agentExe status --check
    Set-Content (Join-Path $report 'status.txt') $status
    Check 'status --check' ($LASTEXITCODE -eq 0 -and $status -match 'Check:\s+OK' -and $status -match 'Service:\s+running') ($status.Trim() -replace '\s+', ' ')
    Check 'panel shows the same key fingerprint' ($status -match [regex]::Escape($credential.fingerprint)) $credential.fingerprint

    # --- Restart: the service comes back on its own ---
    Restart-Service PulsarAgent
    Check 'online again after a service restart' (Wait-Until { (Api GET "/api/v1/nodes/$nodeId").node.connected } 60)

    # --- Key rotation, as SYSTEM with machine keys: a new key replaces the old one, which is deleted ---
    $rotate = Api POST "/api/v1/nodes/$nodeId/credentials/rotate"
    Check 'rotation accepted by the service' ($rotate.status -eq 'accepted') "$($rotate.status)"
    $rotated = Wait-Until {
        $credentials = (Api GET "/api/v1/nodes/$nodeId").credentials
        ($credentials | Where-Object id -eq $credential.id).status -eq 'superseded' -and @($credentials | Where-Object status -eq 'active').Count -eq 1
    } 60
    Check 'old credential superseded, new one active' $rotated
    $newCredential = (Api GET "/api/v1/nodes/$nodeId").credentials | Where-Object status -eq 'active'
    Check 'online with the new key' (Wait-Until { (Api GET "/api/v1/nodes/$nodeId").node.connected -and (Api POST "/api/v1/nodes/$nodeId/ping").status -eq 'accepted' } 60)
    $rotatedConfig = Get-Content (Join-Path $dataDir 'config\agent.json') -Raw | ConvertFrom-Json
    $oldKeyGone = -not [Security.Cryptography.CngKey]::Exists($config.key.name, $provider, [Security.Cryptography.CngKeyOpenOptions]::MachineKey)
    $newProvider = if ($rotatedConfig.key.storage -eq 'tpm') { [Security.Cryptography.CngProvider]::new('Microsoft Platform Crypto Provider') } else { [Security.Cryptography.CngProvider]::MicrosoftSoftwareKeyStorageProvider }
    $newKeyThere = [Security.Cryptography.CngKey]::Exists($rotatedConfig.key.name, $newProvider, [Security.Cryptography.CngKeyOpenOptions]::MachineKey)
    Check 'machine key replaced and the old one deleted' ($rotatedConfig.credentialId -eq $newCredential.id -and $rotatedConfig.key.scope -eq 'machine' -and $oldKeyGone -and $newKeyThere -and $null -eq $rotatedConfig.pendingRotation) "$($rotatedConfig.key.name) $($rotatedConfig.key.storage)"
    if ($tpm.TpmPresent -and $tpm.TpmReady) {
        Check 'rotated key in the TPM' ($newCredential.keyStorage -eq 'tpm') "key storage: $($newCredential.keyStorage)"
    }
    $credential = $newCredential
    $config = $rotatedConfig
    $provider = $newProvider

    # --- A game server runs as its own Windows account (docs/architecture/06) ---
    # Windows PowerShell hands a JSON array over as one object: foreach enumerates its elements.
    $template = $null
    foreach ($candidate in (Api GET '/api/v1/templates')) { if ($candidate.isTestGame) { $template = $candidate } }
    $server = (Api POST '/api/v1/servers' @{ nodeId = $nodeId; templateId = $template.id; name = 'sandbox-server'; description = $null }).server
    $serverId = $server.id
    $account = "pulsar_$($server.shortId)"
    $serverDir = Join-Path 'C:\PulsarServers' $server.shortId
    function Server { (Api GET "/api/v1/servers/$serverId").server }
    Check 'server provisioned and installed' (Wait-Until { $s = Server; $s.installState -eq 'installed' -and $null -eq $s.activeOperation } 60) $server.shortId
    $user = Get-LocalUser $account -ErrorAction SilentlyContinue
    Check 'server account created' ($null -ne $user) $account
    $rightsFile = Join-Path $env:TEMP 'pulsar-rights.inf'
    $null = Invoke-Native secedit.exe /export /areas USER_RIGHTS /cfg $rightsFile
    $sid = $user.SID.Value
    $rightsText = Get-Content $rightsFile -Raw
    Copy-Item $rightsFile (Join-Path $report 'user-rights.inf')
    # secedit lists an account by SID (*S-1-5-...) or, when it can resolve it, by name.
    $holder = "(\*$sid|$account)(,|\s*$)"
    $batch = $rightsText -match "(?m)^SeBatchLogonRight = .*$holder"
    $denied = @('SeDenyInteractiveLogonRight', 'SeDenyRemoteInteractiveLogonRight', 'SeDenyNetworkLogonRight') | Where-Object { $rightsText -match "(?m)^$_ = .*$holder" }
    Check 'server account: batch logon only' ($batch -and @($denied).Count -eq 3) "batch=$batch denied=$($denied -join ',')"
    $serverAcl = Get-Acl $serverDir
    $serverPrincipals = @($serverAcl.Access | ForEach-Object { $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value }) | Sort-Object -Unique
    $expected = @('S-1-3-4', 'S-1-5-18', 'S-1-5-32-544', $sid) | Sort-Object
    Check 'server folder: SYSTEM, Administrators and its account only' ($serverAcl.AreAccessRulesProtected -and (@($serverPrincipals) -join ',') -eq ($expected -join ',')) ($serverPrincipals -join ', ')

    $null = Api POST "/api/v1/servers/$serverId/start"
    Check 'server online' (Wait-Until { (Server).runtimeState -eq 'running' } 60)
    $game = @(Get-CimInstance Win32_Process -Filter "Name = 'Pulsar.TestGameServer.exe'")
    $owner = if ($game.Count -eq 1) { Invoke-CimMethod -InputObject $game[0] -MethodName GetOwner } else { $null }
    Check 'server process runs as its own account' ($game.Count -eq 1 -and $owner.User -eq $account) "$($owner.Domain)\$($owner.User), session $($game[0].SessionId), $($game[0].CommandLine)"

    # An orderly service stop stops the server, and the server starts again with the service.
    $before = $game[0].ProcessId
    Restart-Service PulsarAgent
    $resumed = Wait-Until {
        $now = @(Get-CimInstance Win32_Process -Filter "Name = 'Pulsar.TestGameServer.exe'")
        (Server).runtimeState -eq 'running' -and $now.Count -eq 1 -and $now[0].ProcessId -ne $before
    } 90
    Check 'server resumes after a service restart' $resumed "reason: $((Server).lastStateReason)"

    $null = Api POST "/api/v1/servers/$serverId/kill"
    Check 'kill stops the server and leaves no process' (Wait-Until { (Server).runtimeState -eq 'offline' -and @(Get-CimInstance Win32_Process -Filter "Name = 'Pulsar.TestGameServer.exe'").Count -eq 0 } 30)

    $null = Api DELETE "/api/v1/servers/$serverId"
    $gone = Wait-Until { try { $null = Server; $false } catch { $_.Exception.Response.StatusCode -eq [Net.HttpStatusCode]::NotFound } } 60
    Check 'delete removes the server, its folder and its account' ($gone -and -not (Test-Path $serverDir) -and $null -eq (Get-LocalUser $account -ErrorAction SilentlyContinue)) $account

    # --- Revocation cuts the service off ---
    $credentialId = $credential.id
    $null = Api POST "/api/v1/nodes/$nodeId/credentials/$credentialId/revoke" @{ reason = 'sandbox' }
    Check 'offline after revocation' (Wait-Until { -not (Api GET "/api/v1/nodes/$nodeId").node.connected } 15)
    $log = Get-ChildItem (Join-Path $dataDir 'logs') -Filter 'agent-*.log' | Sort-Object LastWriteTime | Select-Object -Last 1
    Check 'the service logs the refusal' (Wait-Until { (Get-Content $log.FullName -Raw) -match 'The panel refused this node' } 20)
    Copy-Item $log.FullName (Join-Path $report 'agent.log')

    # --- Nothing unexpected in the logs (the refusal after revocation is the one expected error) ---
    $agentErrors = @(Select-String -Path $log.FullName -Pattern '\[(ERR|FTL)\]' | Where-Object { $_.Line -notmatch 'The panel refused this node' })
    Check 'agent log: no unexpected errors' ($agentErrors.Count -eq 0) (($agentErrors | ForEach-Object Line) -join ' | ')
    $panelErrors = @(Select-String -Path (Join-Path $report 'panel.txt') -Pattern ' (ERR|FTL)\]')
    Check 'panel log: no errors' ($panelErrors.Count -eq 0) (($panelErrors | ForEach-Object Line) -join ' | ')

    # --- Unenroll removes the service, the key and the configuration ---
    $removed = Invoke-Native $agentExe unenroll
    $serviceGone = $null -eq (Get-Service PulsarAgent -ErrorAction SilentlyContinue)
    $keyGone = -not [Security.Cryptography.CngKey]::Exists($config.key.name, $provider, [Security.Cryptography.CngKeyOpenOptions]::MachineKey)
    Check 'unenroll removes service, key and configuration' ($LASTEXITCODE -eq 0 -and $serviceGone -and $keyGone -and -not (Test-Path (Join-Path $dataDir 'config\agent.json'))) ($removed.Trim() -replace '\s+', ' ')
}
catch {
    Check 'script' $false ($_ | Out-String).Trim()
}
finally {
    Stop-Transcript | Out-Null
    Add-Content $summary 'done'
}

exit $(if (Select-String -Path $summary -Pattern '^FAIL ' -Quiet) { 1 } else { 0 })
