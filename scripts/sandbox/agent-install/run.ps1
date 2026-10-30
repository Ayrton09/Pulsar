# Installs the agent the production way on a disposable, elevated Windows (Windows Sandbox through prepare.ps1, or a CI
# runner): PostgreSQL + panel on 127.0.0.1, then elevated enroll, machine key, PulsarAgent service as SYSTEM, and a
# test game server running as its own Windows account (backed up while it runs, from a volume snapshot, and restored
# clean), then a real Counter-Strike: Source server installed by SteamCMD
# from Valve (about 2.5 GB), started and stopped over RCON as its account, with managed firewall rules. Then the agent
# MSI: refused next to that agent, a refused token, install, a same-version rebuild, an upgrade with a game running, a
# refused downgrade, Install-Pulsar.ps1 leaving it alone, uninstall. Last, Install-Pulsar.ps1 -Mode Agent trusting the
# panel's certificate (one that can issue certificates refused) and -Uninstall removing that trust. The panel also has
# an agents' port (Phase 16): the service moves there with its machine key as TLS client certificate, and from then on
# the panel requires it, so everything after (rotation, games, backups, the MSI) runs over it. Every check is
# written to report\summary.txt as PASS/FAIL; the last line is "done", and the exit code is 1 on any FAIL.
# When the machine has a ready TPM, the node key must be in it.
# Never run it on a machine you use: it installs a service and creates a machine key (removed again at the end).
param(
    [Parameter(Mandatory)] [string] $Bundle,
    # PostgreSQL binaries (initdb, pg_ctl, createdb); the sandbox maps them here.
    [string] $PgBin = 'C:\pgsql\bin',
    # Skips the real game (SteamCMD download) on a machine without internet access.
    [switch] $SkipRealGame)
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

# What a failed request says about why the server refused it: "tls <status>" when the TLS handshake failed (what a server
# that requires a client certificate does to a client with none: Windows PowerShell reports a send, receive or secure
# channel failure, PowerShell 7 an authentication or IO exception), "http 401"/"http 403" for an explicit refusal, and
# "other: <message>" for anything else (no listener, a timeout, an unresolved name, a 404): that proves nothing.
function Get-Refusal([Exception] $exception) {
    $chain = @()
    for ($e = $exception; $null -ne $e; $e = $e.InnerException) { $chain += $e }
    $web = $chain | Where-Object { $_ -is [Net.WebException] } | Select-Object -First 1
    if ($web -and $web.Response -and ([int]$web.Response.StatusCode) -in 401, 403) { return "http $([int]$web.Response.StatusCode)" }
    if ($web -and "$($web.Status)" -in 'SecureChannelFailure', 'SendFailure', 'ReceiveFailure', 'ConnectionClosed') { return "tls $($web.Status)" }
    if (-not $web -and ($chain | Where-Object { $_ -is [Security.Authentication.AuthenticationException] -or $_ -is [IO.IOException] })) { return 'tls handshake failed' }
    return "other: $($exception.Message)"
}

# HTTPS, as in production: a certificate made here and trusted machine-wide (the machine is disposable).
$panel = 'https://localhost:5291'
# The agents' port: agents present their node key as client certificate there (docs/architecture/03).
$agentsUrl = 'https://localhost:5292'
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

    # --- TLS certificate for localhost, trusted by the whole machine (the agent service runs as SYSTEM); like the
    # installer's own, it cannot issue certificates and is for a web server only (server authentication) ---
    $certificate = New-SelfSignedCertificate -DnsName 'localhost' -CertStoreLocation 'Cert:\LocalMachine\My' -KeyExportPolicy Exportable -NotAfter (Get-Date).AddDays(2) `
        -TextExtension @('2.5.29.19={critical}{text}ca=0', '2.5.29.37={text}1.3.6.1.5.5.7.3.1')
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
    # Both endpoints from configuration, as the installer writes them (configured endpoints replace --urls).
    $env:Kestrel__Endpoints__Main__Url = $panel
    $env:Kestrel__Endpoints__Agents__Url = $agentsUrl
    $env:Pulsar__EnableTestGame = 'true'
    $panelDir = Join-Path $Bundle 'panel'
    Start-Process (Join-Path $panelDir 'Pulsar.Api.exe') -ArgumentList '--contentRoot', "`"$panelDir`"" `
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
    # The service reads appsettings.Production.json next to its executable: this agent may install the test game, and
    # it manages the firewall rules of its servers (off by default; the machine's administrator decides).
    @{ Pulsar = @{ TestGameDirectory = $testGameDir; ManageFirewall = $true } } | ConvertTo-Json | Set-Content (Join-Path $installDir 'appsettings.Production.json')
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
    # The agent may have moved to the agents' address already (checked below).
    Check 'agent talks to the panel over HTTPS' ($config.panel -in "$panel/", "$agentsUrl/") $config.panel
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

    # --- Client certificates (Phase 16): told the agents' address, the service signs in there with its machine key in
    # the TLS handshake (SChannel, TPM included) and keeps the main address as its fallback ---
    $moved = Wait-Until {
        $now = Get-Content (Join-Path $dataDir 'config\agent.json') -Raw | ConvertFrom-Json
        $now.panel -eq "$agentsUrl/" -and $now.fallbackPanel -eq "$panel/" -and (Api GET "/api/v1/nodes/$nodeId").node.connectedWithCertificate -eq $true
    } 90
    Check "agent moved to the agents' address, its machine key as client certificate" $moved
    Check "signed ping over the agents' address" ((Api POST "/api/v1/nodes/$nodeId/ping").status -eq 'accepted')
    # Refused for lack of a certificate: the TLS handshake fails (the endpoint requires one), or the answer is an explicit
    # 401/403. Any other failure (nothing listens, a timeout, a name that does not resolve) or an answer says nothing.
    $bare = try { $null = Invoke-WebRequest "$agentsUrl/agent/v1/panel-identity" -UseBasicParsing -TimeoutSec 10; 'answered' } catch { Get-Refusal $_.Exception }
    Check "the agents' port turns away a client without certificate" ($bare -match '^(tls|http) ') "$bare"
    $required = Api PUT '/api/v1/agent-transport' @{ requireClientCertificates = $true }
    Check 'certificates required, every node has one' ($required.requireClientCertificates -and $required.inEffect -and $required.nodesWithoutCertificateCount -eq 0) "$($required.agentsUrl)"
    # The browsers' port never asks for a certificate, not even now that agents must have one: it answers without any.
    $browserReady = try { (Invoke-WebRequest "$panel/health/ready" -UseBasicParsing -TimeoutSec 10).StatusCode } catch { Get-Refusal $_.Exception }
    Check "the browsers' port answers without a client certificate" ($browserReady -eq 200) "$browserReady"
    Check 'still online with certificates required' (Wait-Until { (Api GET "/api/v1/nodes/$nodeId").node.connected -and (Api POST "/api/v1/nodes/$nodeId/ping").status -eq 'accepted' } 30)
    $agentsStatus = Invoke-Native $agentExe status --check
    Check "status --check through the agents' address" ($LASTEXITCODE -eq 0 -and $agentsStatus -match 'Check:\s+OK') ($agentsStatus.Trim() -replace '\s+', ' ')

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

    # The outbound block of a server's account (docs/architecture/06, "Windows Firewall"): TCP to the agents' port and the
    # main one it falls back to, at loopback and this machine's own addresses (the panel runs here), for that account only.
    function Test-PanelBlock([string] $shortId, [string] $accountName) {
        $rule = Get-NetFirewallRule -DisplayName "Pulsar $shortId panel block" -ErrorAction SilentlyContinue
        $accountSid = (Get-LocalUser $accountName).SID.Value
        $own = @(Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' })[0].IPAddress
        $ports = if ($rule) { @(($rule | Get-NetFirewallPortFilter).RemotePort) } else { @() }
        $protocol = if ($rule) { "$(($rule | Get-NetFirewallPortFilter).Protocol)" } else { '' }
        $addresses = if ($rule) { @(($rule | Get-NetFirewallAddressFilter).RemoteAddress) } else { @() }
        $users = if ($rule) { "$(($rule | Get-NetFirewallSecurityFilter).LocalUser)" } else { '' }
        $ok = $null -ne $rule -and @($rule).Count -eq 1 -and "$($rule.Direction)" -eq 'Outbound' -and "$($rule.Action)" -eq 'Block' -and "$($rule.Enabled)" -eq 'True' `
            -and $protocol -eq 'TCP' -and '5291' -in $ports -and '5292' -in $ports -and @($addresses -match '^127\.0\.0\.0/(8|255\.0\.0\.0)$').Count -eq 1 `
            -and (-not $own -or @($addresses -match "^$([regex]::Escape($own))(/32|/255\.255\.255\.255)?$").Count -eq 1) -and $users -eq "D:(A;;CC;;;$accountSid)"
        [pscustomobject]@{ Ok = $ok; Detail = "$($rule.Direction) $($rule.Action) $protocol; ports $($ports -join ','); addresses $($addresses -join ','); users $users; own $own" }
    }

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
    $block = Test-PanelBlock $server.shortId $account
    Check "firewall keeps the server's account from the panel's ports" $block.Ok $block.Detail

    # --- The server's own host process keeps it running while the agent restarts (docs/architecture/06, "Server hosts") ---
    $before = $game[0].ProcessId
    $hostProcess = @(Get-CimInstance Win32_Process -Filter "Name = 'PulsarAgent.exe'" | Where-Object { $_.CommandLine -match ' host ' })
    $hostOwner = if ($hostProcess.Count -eq 1) { Invoke-CimMethod -InputObject $hostProcess[0] -MethodName GetOwner } else { $null }
    Check 'server runs under its own host process (SYSTEM, from a copy of the program)' ($hostProcess.Count -eq 1 -and $hostOwner.User -eq 'SYSTEM' -and $hostProcess[0].ExecutablePath -like "$dataDir\host-programs\*") "$($hostOwner.User) $($hostProcess[0].ExecutablePath)"
    function Same-Game { $now = @(Get-CimInstance Win32_Process -Filter "Name = 'Pulsar.TestGameServer.exe'"); $now.Count -eq 1 -and $now[0].ProcessId -eq $before }
    function Back-Online { (Api GET "/api/v1/nodes/$nodeId").node.connected -and (Server).runtimeState -eq 'running' }

    # The block is the account's, not the start's: one deleted by hand is made again when the agent takes the game over.
    Remove-NetFirewallRule -DisplayName "Pulsar $($server.shortId) panel block" -ErrorAction SilentlyContinue
    Restart-Service PulsarAgent
    Check 'server keeps running through a service restart (same process)' (Wait-Until { (Back-Online) -and (Same-Game) } 90) "reason: $((Server).lastStateReason)"
    $retaken = Wait-Until { (Test-PanelBlock $server.shortId $account).Ok } 30
    Check 'the panel block is made again for a game taken over after a restart' $retaken (Test-PanelBlock $server.shortId $account).Detail

    # The agent crashes: Windows restarts the service (5 s) and it takes the game over again.
    $agentProcess = (Get-CimInstance Win32_Service -Filter "Name='PulsarAgent'").ProcessId
    Stop-Process -Id $agentProcess -Force
    Check 'server keeps running through an agent crash (same process)' (Wait-Until { (Get-CimInstance Win32_Service -Filter "Name='PulsarAgent'").ProcessId -notin 0, $agentProcess -and (Back-Online) -and (Same-Game) } 120)

    # Asked to, the service stops its servers with it; they start again with it.
    $stopped = Invoke-Native $agentExe service stop --stop-servers
    Check 'service stop --stop-servers stops the game' ($LASTEXITCODE -eq 0 -and @(Get-CimInstance Win32_Process -Filter "Name = 'Pulsar.TestGameServer.exe'").Count -eq 0) ($stopped.Trim() -replace '\s+', ' ')
    Start-Service PulsarAgent
    $resumed = Wait-Until {
        $now = @(Get-CimInstance Win32_Process -Filter "Name = 'Pulsar.TestGameServer.exe'")
        (Server).runtimeState -eq 'running' -and $now.Count -eq 1 -and $now[0].ProcessId -ne $before
    } 90
    Check 'server resumes when the service starts again' $resumed "reason: $((Server).lastStateReason)"

    # --- A backup of the running game reads a snapshot of its volume, after the game saved (docs/architecture/06,
    # "Backup snapshots"): a file the game holds open with no sharing is in it, which a live copy leaves out ---
    $null = Api POST "/api/v1/servers/$serverId/console" @{ command = 'lock held.dat' }
    Check 'the game holds a file open' (Wait-Until { Test-Path (Join-Path $serverDir 'data\held.dat') } 30)
    Start-Sleep -Seconds 1
    $made = Api POST "/api/v1/servers/$serverId/backups" @{ name = 'while running' }
    function Backup-Of($id) { foreach ($b in (Api GET "/api/v1/servers/$serverId/backups").backups) { if ($b.id -eq $id) { return $b } } }
    $completed = Wait-Until { (Backup-Of $made.id).status -in 'completed', 'failed' } 180
    $running = Backup-Of $made.id
    Check 'backup of the running game: from a snapshot, after the game saved' ($completed -and $running.status -eq 'completed' -and $running.copyMode -eq 'snapshot' -and $running.gameSave -eq 'confirmed' -and $running.skippedFiles -eq 0) "$($running.status) $($running.copyMode) $($running.snapshotError) save=$($running.gameSave) skipped=$($running.skippedFiles) $($running.errorMessage)"
    $archive = Join-Path $dataDir "backups\$($server.shortId)\$($made.id -replace '-', '').zip"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $entries = @()
    if (Test-Path $archive) {
        $zip = [IO.Compression.ZipFile]::OpenRead($archive)
        try { $entries = @($zip.Entries | ForEach-Object FullName) } finally { $zip.Dispose() }
    }
    Check 'the backup has the file the game held open, and the world it saved' ('data/held.dat' -in $entries -and 'data/world.dat' -in $entries) (($entries | Sort-Object) -join ', ')
    $shadows = @(Get-CimInstance Win32_ShadowCopy -ErrorAction SilentlyContinue)
    Check 'no snapshot left after the backup' ($shadows.Count -eq 0) "$($shadows.Count) left"
    Check 'no paused save left (the game was told to save again)' (-not (Test-Path (Join-Path $dataDir 'state\paused-saves\*.json')))
    $agentLog = Get-ChildItem (Join-Path $dataDir 'logs') -Filter 'agent-*.log' | Sort-Object LastWriteTime | Select-Object -Last 1
    Check 'agent log: snapshot taken and deleted' ((Get-Content $agentLog.FullName -Raw) -match 'Snapshot of .+ taken: \\\\\?\\GLOBALROOT\\Device\\HarddiskVolumeShadowCopy' -and (Get-Content $agentLog.FullName -Raw) -match 'Snapshot \S+ deleted')

    # A clean restore, the server stopped: the file made after the backup goes, the backup's files come back.
    $null = Api POST "/api/v1/servers/$serverId/stop"
    Check 'server stopped for the restore' (Wait-Until { (Server).runtimeState -eq 'offline' } 30)
    Set-Content (Join-Path $serverDir 'data\later.dat') 'made after the backup'
    Remove-Item (Join-Path $serverDir 'data\world.dat')
    $null = Api POST "/api/v1/servers/$serverId/backups/$($made.id)/restore?clean=true"
    $restored = Wait-Until { $s = Server; $null -eq $s.activeOperation -and (Test-Path (Join-Path $serverDir 'data\world.dat')) } 120
    Check 'clean restore as the server account: newer file deleted, backed-up files back' ($restored -and -not (Test-Path (Join-Path $serverDir 'data\later.dat')) -and (Test-Path (Join-Path $serverDir 'data\held.dat'))) "later.dat left: $(Test-Path (Join-Path $serverDir 'data\later.dat'))"
    $null = Api POST "/api/v1/servers/$serverId/start"
    Check 'server online again after the restore' (Wait-Until { (Server).runtimeState -eq 'running' } 60)

    $null = Api POST "/api/v1/servers/$serverId/kill"
    Check 'kill stops the server and leaves no process' (Wait-Until { (Server).runtimeState -eq 'offline' -and @(Get-CimInstance Win32_Process -Filter "Name = 'Pulsar.TestGameServer.exe'").Count -eq 0 } 30)

    $null = Api DELETE "/api/v1/servers/$serverId"
    $gone = Wait-Until { try { $null = Server; $false } catch { $_.Exception.Response.StatusCode -eq [Net.HttpStatusCode]::NotFound } } 60
    Check 'delete removes the server, its folder and its account' ($gone -and -not (Test-Path $serverDir) -and $null -eq (Get-LocalUser $account -ErrorAction SilentlyContinue)) $account

    # --- A real Source game: SteamCMD as the server's account, RCON readiness and stop, firewall (docs/architecture/04, 06) ---
    if (-not $SkipRealGame) {
        $null = Api POST "/api/v1/nodes/$nodeId/allocations" @{ ipAddress = '0.0.0.0'; ports = '27015-27020'; notes = 'sandbox' }
        $css = $null
        foreach ($candidate in (Api GET '/api/v1/templates')) { if ($candidate.key -eq 'css') { $css = $candidate } }
        $game = (Api POST '/api/v1/servers' @{
            nodeId = $nodeId; templateId = $css.id; name = 'sandbox-css'; description = $null
            variables = @{ hostname = 'Sandbox CS:S'; map = 'cs_office'; maxPlayers = '12'; password = 'sandbox-join' }
        })
        $gameId = $game.server.id
        $gameAccount = "pulsar_$($game.server.shortId)"
        $gameDir = Join-Path 'C:\PulsarServers' $game.server.shortId
        function GameServer { Api GET "/api/v1/servers/$gameId" }
        Check 'css: game port from the node allocations' (($game.ports | Where-Object name -eq 'game').port -eq 27015) "$(($game.ports | ForEach-Object { "$($_.name)=$($_.port)" }) -join ' ')"

        # Progress shows while SteamCMD downloads; then the install completes.
        $stages = New-Object System.Collections.Generic.List[string]
        $installed = Wait-Until {
            $g = GameServer
            if ($g.server.activeOperation) { $stages.Add("$($g.server.activeOperation.type):$($g.server.activeOperation.stage):$($g.server.activeOperation.percent)") }
            $g.server.installState -eq 'installed' -and $null -eq $g.server.activeOperation
        } 1800
        $seen = @($stages | Select-Object -Unique)
        Check 'css: installed by SteamCMD' $installed "$((GameServer).recentOperations[0].status) $((GameServer).recentOperations[0].errorCode) $((GameServer).recentOperations[0].errorMessage)"
        Check 'css: live install progress' (@($seen | Where-Object { $_ -match ':downloading:\d' }).Count -gt 0) (($seen | Select-Object -First 12) -join ' | ')
        $steamOwner = (Get-Acl (Join-Path $gameDir 'srcds.exe')).Owner
        Check 'css: game files written by the server account (SteamCMD ran as it)' ($steamOwner -like "*$gameAccount") $steamOwner
        $signature = Get-AuthenticodeSignature (Join-Path $dataDir 'steamcmd\steamcmd.exe')
        # The organization (O) compared whole, as the agent does (SteamCmdBootstrapper.VerifyValveSignature): "Valve" or "Valve Corp.",
        # not any name that merely contains "O=Valve" ("O=Valve Software Fake", "OU=O=Valve...").
        $subject = "$($signature.SignerCertificate.Subject)"
        $organization = if ($subject -match '(?:^|,\s*)O=(?:"((?:[^"]|"")*)"|([^,]*))') { if ($Matches[1]) { $Matches[1] -replace '""', '"' } else { $Matches[2].Trim() } } else { $null }
        Check 'css: SteamCMD bootstrapper signed by Valve' ($signature.Status -eq 'Valid' -and $organization -in @('Valve', 'Valve Corp.')) "O=$organization; $subject"

        $null = Api POST "/api/v1/servers/$gameId/start"
        Check 'css: ready over RCON' (Wait-Until { (GameServer).server.runtimeState -eq 'running' } 300) "reason: $((GameServer).server.lastStateReason)"
        $srcds = @(Get-CimInstance Win32_Process -Filter "Name = 'srcds.exe'")
        $srcdsOwner = if ($srcds.Count -eq 1) { Invoke-CimMethod -InputObject $srcds[0] -MethodName GetOwner } else { $null }
        Check 'css: srcds runs as its account with the configured values' ($srcds.Count -eq 1 -and $srcdsOwner.User -eq $gameAccount -and $srcds[0].CommandLine -match '\+map cs_office' -and $srcds[0].CommandLine -match '\+maxplayers 12') "$($srcdsOwner.User): $($srcds[0].CommandLine -replace '\+rcon_password \S+', '+rcon_password ***')"
        $cfg = Get-Content (Join-Path $gameDir 'cstrike\cfg\server.cfg') -Raw
        Check 'css: server.cfg written before the start' ($cfg -match 'hostname "Sandbox CS:S"' -and $cfg -match 'sv_password "sandbox-join"') (($cfg -split "`n" | Where-Object { $_ -match '^(hostname|sv_password)' }) -join ' | ')
        $udpRule = Get-NetFirewallRule -DisplayName "Pulsar $($game.server.shortId) game udp" -ErrorAction SilentlyContinue
        $tcpRule = Get-NetFirewallRule -DisplayName "Pulsar $($game.server.shortId) game tcp" -ErrorAction SilentlyContinue
        $udpPort = if ($udpRule) { ($udpRule | Get-NetFirewallPortFilter).LocalPort } else { '' }
        $udpApp = if ($udpRule) { ($udpRule | Get-NetFirewallApplicationFilter).Program } else { '' }
        Check 'css: firewall opens the game port over UDP only, for srcds' ($udpPort -eq '27015' -and $udpApp -like '*\srcds.exe' -and $null -eq $tcpRule) "udp $udpPort $udpApp; tcp rule: $($null -ne $tcpRule)"
        # Its account may not reach the panel: TCP to the agents' port and the main one it falls back to, at loopback and
        # this machine's own addresses (the panel runs here), for that account only.
        $block = Test-PanelBlock $game.server.shortId $gameAccount
        Check "css: firewall keeps its account from the panel's ports" $block.Ok $block.Detail

        $null = Api POST "/api/v1/servers/$gameId/stop"
        $stopped = Wait-Until { (GameServer).server.runtimeState -eq 'offline' -and @(Get-CimInstance Win32_Process -Filter "Name = 'srcds.exe'").Count -eq 0 } 60
        # "requested": it quit on the RCON command, neither timed out nor killed. srcds' own exit code on the way out is
        # reported, not judged: on Windows Server 2025 it ends with 0xC0000409 (a fast fail while shutting down).
        Check 'css: stopped over RCON (no timeout, no process left)' ($stopped -and (GameServer).server.lastStateReason -eq 'requested') "reason $((GameServer).server.lastStateReason), exit $((GameServer).server.lastExitCode)"

        $null = Api DELETE "/api/v1/servers/$gameId"
        $gameGone = Wait-Until { try { $null = GameServer; $false } catch { $_.Exception.Response.StatusCode -eq [Net.HttpStatusCode]::NotFound } } 120
        $rulesLeft = @(Get-NetFirewallRule -DisplayName "Pulsar $($game.server.shortId) *" -ErrorAction SilentlyContinue).Count
        # Windows PowerShell hands a JSON array over as one object: foreach enumerates its elements.
        $assigned = 0
        foreach ($allocation in (Api GET "/api/v1/nodes/$nodeId/allocations")) { if ($allocation.serverId) { $assigned++ } }
        Check 'css: delete removes folder, account, firewall rules (the panel block too) and frees the ports' ($gameGone -and -not (Test-Path $gameDir) -and $null -eq (Get-LocalUser $gameAccount -ErrorAction SilentlyContinue) -and $rulesLeft -eq 0 -and $assigned -eq 0) "rules left: $rulesLeft, ports still assigned: $assigned"
    }

    # --- The panel block holds where the panel runs on the node, as here: a TCP connection to 127.0.0.1 from an account
    # with such a rule is refused (docs/architecture/06, "Windows Firewall"). A throwaway account with a known password,
    # since a server's has none; connected first without the rule, so a refusal is the rule's ---
    $probeUser = 'fwprobe'
    $probePassword = ConvertTo-SecureString ('Pb1!' + [guid]::NewGuid().ToString('N')) -AsPlainText -Force
    $probeOut = Join-Path $env:PUBLIC 'fwprobe.txt'
    $probeRule = 'Pulsar sandbox loopback probe'
    function Invoke-Probe {
        Remove-Item $probeOut -ErrorAction SilentlyContinue
        $connect = "try { (New-Object Net.Sockets.TcpClient).Connect('127.0.0.1', 5291); 'connected' } catch { 'refused' }"
        $process = Start-Process powershell.exe -Credential (New-Object Management.Automation.PSCredential $probeUser, $probePassword) `
            -ArgumentList '-NoProfile', '-Command', "& { $connect } | Set-Content '$probeOut'" -WorkingDirectory $env:PUBLIC -WindowStyle Hidden -PassThru -Wait
        if (Test-Path $probeOut) { (Get-Content $probeOut -Raw).Trim() } else { "no answer (exit $($process.ExitCode))" }
    }
    try {
        $null = New-LocalUser $probeUser -Password $probePassword -AccountNeverExpires
        $before = Invoke-Probe
        $null = New-NetFirewallRule -DisplayName $probeRule -Direction Outbound -Action Block -Protocol TCP -RemoteAddress '127.0.0.0/8', '::/127' `
            -RemotePort 5291 -LocalUser "D:(A;;CC;;;$((Get-LocalUser $probeUser).SID.Value))"
        $after = Invoke-Probe
        $profiles = (@(Get-NetFirewallProfile | Where-Object Enabled) | ForEach-Object Name) -join ','
        # What docs/architecture/06 says: Windows Firewall does not filter loopback, so the block cannot keep a game from a panel on its node.
        Check 'firewall: loopback is not filtered, as documented (the rule is accepted)' ($before -eq 'connected' -and $after -eq 'connected') "without the rule: $before; with it: $after; firewall on for: $profiles"
    }
    catch {
        Check 'firewall: loopback is not filtered, as documented (the rule is accepted)' $false "$($_.Exception.Message)"
    }
    finally {
        Remove-NetFirewallRule -DisplayName $probeRule -ErrorAction SilentlyContinue
        Remove-LocalUser $probeUser -ErrorAction SilentlyContinue
        Remove-Item $probeOut -ErrorAction SilentlyContinue
    }

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

    # --- The agent MSI (Phase 14) refuses to install where an agent installed without it runs (same folder, same service) ---
    $first = Get-ChildItem (Join-Path $Bundle 'msi') -Filter 'pulsar-agent-1.0.0-*.msi'
    $rebuild = Get-ChildItem (Join-Path $Bundle 'msi\rebuild') -Filter 'pulsar-agent-1.0.0-*.msi'
    $second = Get-ChildItem (Join-Path $Bundle 'msi') -Filter 'pulsar-agent-1.0.1-*.msi'
    function Invoke-Msi([string[]] $arguments) { (Start-Process msiexec.exe -ArgumentList $arguments -Wait -PassThru).ExitCode }
    function Install-Msi($package, [string] $log, [string[]] $properties = @()) {
        Invoke-Msi (@('/i', "`"$($package.FullName)`"", '/qn', '/l*v', "`"$(Join-Path $report $log)`"") + $properties)
    }
    function Uninstall-Msi($package, [string] $log) { Invoke-Msi @('/x', "`"$($package.FullName)`"", '/qn', '/l*v', "`"$(Join-Path $report $log)`"") }
    function Get-AgentProducts {
        @(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue | Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -eq 'Pulsar Agent' })
    }
    $exit = Install-Msi $first 'msi-over-zip.log' @("PANELURL=$panel", 'TOKEN=unused', "PANELKEY=$panelKey")
    $refusal = Get-Content (Join-Path $report 'msi-over-zip.log') -Raw
    Check 'msi: refused where an agent installed without it runs (service and program untouched)' ($exit -ne 0 -and $refusal -match 'Install-Pulsar\.ps1 -Uninstall' -and $null -ne (Get-Service PulsarAgent -ErrorAction SilentlyContinue) -and (Test-Path $agentExe) -and @(Get-AgentProducts).Count -eq 0) "exit $exit"

    # --- Unenroll removes the service, the key and the configuration ---
    $removed = Invoke-Native $agentExe unenroll
    $serviceGone = $null -eq (Get-Service PulsarAgent -ErrorAction SilentlyContinue)
    $keyGone = -not [Security.Cryptography.CngKey]::Exists($config.key.name, $provider, [Security.Cryptography.CngKeyOpenOptions]::MachineKey)
    Check 'unenroll removes service, key and configuration' ($LASTEXITCODE -eq 0 -and $serviceGone -and $keyGone -and -not (Test-Path (Join-Path $dataDir 'config\agent.json'))) ($removed.Trim() -replace '\s+', ' ')

    # --- The agent MSI with a reusable token (Phase 14): a refused token, install, approval, a rebuild, an upgrade with a
    # game running, a refused downgrade, uninstall, and an install with a SERVERSROOT ending in a backslash ---
    Remove-Item $installDir -Recurse -Force # the copy installed by hand above
    # Two machines' worth: the first install and the one with the trailing backslash (a refused token uses none).
    $reusable = Api POST '/api/v1/enrollment-tokens' @{ name = 'sandbox-msi'; maxUses = 2; lifetimeHours = 24; requireApproval = $true }
    $values = @("PANELURL=$panel", "TOKEN=$($reusable.value)", "PANELKEY=$($reusable.panelKeyFingerprint)")
    $agentConfig = Join-Path $dataDir 'config\agent.json'
    $enrollLog = Join-Path $dataDir 'logs\enroll.log'
    $unenrollLog = Join-Path $dataDir 'logs\unenroll.log'
    function Get-LogTail([string] $path) { ((Get-Content $path -Tail 4 -ErrorAction SilentlyContinue) -join ' ') -replace '\s+', ' ' }
    function Test-AgentRunning { $agent = Get-Service PulsarAgent -ErrorAction SilentlyContinue; $null -ne $agent -and $agent.Status -eq 'Running' }

    # The panel refuses the token: enroll fails, and the installation rolls back completely.
    $exit = Install-Msi $first 'msi-bad-token.log' @("PANELURL=$panel", "TOKEN=$($reusable.value)x", "PANELKEY=$($reusable.panelKeyFingerprint)")
    Check 'msi: a refused token fails the install and leaves nothing (no service, program, enrollment or product)' ($exit -ne 0 -and $null -eq (Get-Service PulsarAgent -ErrorAction SilentlyContinue) -and -not (Test-Path $installDir) -and -not (Test-Path $agentConfig) -and @(Get-AgentProducts).Count -eq 0) "exit $exit; enroll.log: $(Get-LogTail $enrollLog)"

    # Without SERVERSROOT: enroll gets no --servers-root, and servers go to the default folder.
    $exit = Install-Msi $first 'msi-install.log' $values
    Check 'msi: installed silently' ($exit -eq 0) "exit $exit"
    $service = Get-CimInstance Win32_Service -Filter "Name='PulsarAgent'"
    Check 'msi: the service runs from Program Files' ($service.State -eq 'Running' -and $service.PathName -like "*$installDir\PulsarAgent.exe*") "$($service.State) $($service.PathName)"
    $msiNode = $null
    foreach ($candidate in (Api GET '/api/v1/nodes')) { if ($candidate.name -eq $env:COMPUTERNAME) { $msiNode = $candidate } }
    Check 'msi: the token created a node named after the machine, waiting for approval' ($null -ne $msiNode -and $msiNode.status -eq 'pendingApproval') "$($msiNode.name) $($msiNode.status)"
    Check 'msi: enroll log written' ((Test-Path $enrollLog) -and (Get-Content $enrollLog -Raw) -match 'Enrolled with') ((Get-Content $enrollLog -Raw -ErrorAction SilentlyContinue) -replace '\s+', ' ')
    # The refused token is the real one plus a character: a log holding either matches.
    $leaked = @(Select-String -Path (Join-Path $report 'msi-bad-token.log'), (Join-Path $report 'msi-install.log'), $enrollLog -SimpleMatch $reusable.value)
    Check 'msi: the token is in no log' ($leaked.Count -eq 0) "$($leaked.Count) lines"
    $null = Api POST "/api/v1/nodes/$($msiNode.id)/approve"
    Check 'msi: node online once approved' (Wait-Until { (Api GET "/api/v1/nodes/$($msiNode.id)").node.connected } 120)
    $msiConfig = Get-Content $agentConfig -Raw | ConvertFrom-Json
    $inventory = (Api GET "/api/v1/nodes/$($msiNode.id)").node.inventory
    Check 'msi: without SERVERSROOT, the default servers root' ($null -eq $msiConfig.serversRoot -and $inventory.serversRoot -eq 'C:\PulsarServers') "configured: $($msiConfig.serversRoot); reported: $($inventory.serversRoot)"

    # Another build of the same version is a new product: it replaces the installed one, enrollment included.
    $built = @(Get-AgentProducts)
    $exit = Install-Msi $rebuild 'msi-rebuild.log'
    $products = @(Get-AgentProducts)
    Check 'msi: a rebuild of the same version replaces it (no values needed)' ($exit -eq 0 -and $products.Count -eq 1 -and $products[0].DisplayVersion -eq '1.0.0' -and $products[0].PSChildName -ne $built[0].PSChildName) "exit $exit; $(($products | ForEach-Object { "$($_.PSChildName) $($_.DisplayVersion)" }) -join ', ')"
    Check 'msi: enrollment kept through the rebuild, node online' ((Test-Path $agentConfig) -and (Wait-Until { (Api GET "/api/v1/nodes/$($msiNode.id)").node.connected } 60))

    # A test game on the new node, running while the package is upgraded.
    @{ Pulsar = @{ TestGameDirectory = $testGameDir } } | ConvertTo-Json | Set-Content (Join-Path $installDir 'appsettings.Production.json')
    Restart-Service PulsarAgent
    $null = Wait-Until { (Api GET "/api/v1/nodes/$($msiNode.id)").node.connected } 60
    $msiServer = (Api POST '/api/v1/servers' @{ nodeId = $msiNode.id; templateId = $template.id; name = 'sandbox-msi-server'; description = $null }).server
    function MsiServer { (Api GET "/api/v1/servers/$($msiServer.id)").server }
    $null = Wait-Until { $s = MsiServer; $s.installState -eq 'installed' -and $null -eq $s.activeOperation } 60
    $null = Api POST "/api/v1/servers/$($msiServer.id)/start"
    Check 'msi: server running on the node' (Wait-Until { (MsiServer).runtimeState -eq 'running' } 60)
    $before = @(Get-CimInstance Win32_Process -Filter "Name = 'Pulsar.TestGameServer.exe'")[0].ProcessId

    $exit = Install-Msi $second 'msi-upgrade.log'
    Check 'msi: upgraded silently (no values needed)' ($exit -eq 0) "exit $exit"
    $products = @(Get-AgentProducts)
    Check 'msi: one product left, the new version' ($products.Count -eq 1 -and $products[0].DisplayVersion -eq '1.0.1') (($products | ForEach-Object DisplayVersion) -join ', ')
    Check 'msi: the game kept running through the upgrade (same process)' (Wait-Until {
            $now = @(Get-CimInstance Win32_Process -Filter "Name = 'Pulsar.TestGameServer.exe'")
            (Api GET "/api/v1/nodes/$($msiNode.id)").node.connected -and (MsiServer).runtimeState -eq 'running' -and $now.Count -eq 1 -and $now[0].ProcessId -eq $before
        } 120)
    Check 'msi: enrollment kept' (Test-Path $agentConfig)

    # The ZIP's installer leaves an agent from the MSI alone (this machine has no panel service for it to handle). It runs
    # only from a folder administrators alone can change, as the install guide has the release extracted: not the bundle's.
    # In C:\Program Files, whose permissions it takes as it is made.
    $setup = Join-Path $env:ProgramFiles 'PulsarSetup'
    New-Item -ItemType Directory $setup | Out-Null
    Copy-Item (Join-Path $Bundle 'Install-Pulsar.ps1') $setup
    Copy-Item (Join-Path $Bundle 'agent') $setup -Recurse
    $installScript = Join-Path $setup 'Install-Pulsar.ps1'
    foreach ($arguments in @(@('-Update'), @('-Uninstall'), @('-Mode', 'Agent', '-PanelUrl', $panel, '-Token', 'unused', '-PanelKey', 'unused'))) {
        $refused = & {
            $ErrorActionPreference = 'Continue'
            (& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installScript @arguments 2>&1 | ForEach-Object { "$_" }) -join ' '
        }
        $code = $LASTEXITCODE
        # Windows PowerShell wraps the error it prints at the console's width, anywhere in a word.
        Check "msi: Install-Pulsar.ps1 $($arguments[0]) $($arguments[1]) refuses to touch it" ($code -ne 0 -and ($refused -replace '\s', '') -match 'msiexec/x' -and (Test-AgentRunning) -and (Test-Path $agentExe)) "exit $code; $($refused -replace '\s+', ' ')"
    }

    # An older package is refused, and the installed one keeps running.
    $exit = Install-Msi $first 'msi-downgrade.log'
    $products = @(Get-AgentProducts)
    Check 'msi: an older package does not downgrade it' ($exit -ne 0 -and (Get-Content (Join-Path $report 'msi-downgrade.log') -Raw) -match 'A newer version of the Pulsar Agent is installed' -and $products.Count -eq 1 -and $products[0].DisplayVersion -eq '1.0.1' -and (Test-AgentRunning)) "exit $exit; $(($products | ForEach-Object DisplayVersion) -join ', ')"

    # unenroll.log grows with every run (the rolled-back install above wrote to it too): only this uninstall's part counts.
    $unenrollFrom = if (Test-Path $unenrollLog) { (Get-Item $unenrollLog).Length } else { 0 }
    $exit = Uninstall-Msi $second 'msi-uninstall.log'
    Check 'msi: uninstalled silently' ($exit -eq 0) "exit $exit"
    Check 'msi: uninstall stopped the game and removed the service and the enrollment' (@(Get-CimInstance Win32_Process -Filter "Name = 'Pulsar.TestGameServer.exe'").Count -eq 0 -and $null -eq (Get-Service PulsarAgent -ErrorAction SilentlyContinue) -and -not (Test-Path $agentConfig))
    $unenrolled = ''
    if (Test-Path $unenrollLog) {
        $bytes = [IO.File]::ReadAllBytes($unenrollLog)
        $unenrolled = [Text.Encoding]::UTF8.GetString($bytes, $unenrollFrom, $bytes.Length - $unenrollFrom)
    }
    Check 'msi: unenroll log written' ($unenrolled -match 'no longer enrolled') (Get-LogTail $unenrollLog)
    # The running service stopped the game on its way out. Had something stopped the service before unenroll ran (Restart
    # Manager, at InstallValidate), the service would have left the game running and unenroll would have killed it.
    Check 'msi: uninstall stopped the game gracefully (unenroll ended no game server)' ($unenrolled -match 'no longer enrolled' -and $unenrolled -notmatch 'Ended \d+ game server') ($unenrolled -replace '\s+', ' ')
    # The package removes what it installed; the settings file this check wrote is not its own, and stays.
    $left = @(Get-ChildItem $installDir -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName.Substring($installDir.Length + 1) })
    Check 'msi: program removed (only the settings file written here is left)' (-not (Test-Path $agentExe) -and ($left -join ',') -eq 'appsettings.Production.json') ($left -join ', ')
    Check 'msi: no product left' (@(Get-AgentProducts).Count -eq 0)
    Remove-Item $installDir -Recurse -Force -ErrorAction SilentlyContinue

    # SERVERSROOT ending in a backslash: its closing quote reaches the agent escaped (C:\PulsarServers2"), which it trims.
    $exit = Install-Msi $second 'msi-install-backslash.log' ($values + 'SERVERSROOT=C:\PulsarServers2\')
    $msiConfig = Get-Content $agentConfig -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json
    $root = if ($msiConfig) { "$($msiConfig.serversRoot)" } else { '' }
    Check 'msi: SERVERSROOT with a trailing backslash' ($exit -eq 0 -and $root.TrimEnd('\') -eq 'C:\PulsarServers2') "exit $exit; servers root: $root; enroll.log: $(Get-LogTail $enrollLog)"
    $exit = Uninstall-Msi $second 'msi-uninstall-backslash.log'
    Check 'msi: uninstalled again, nothing left' ($exit -eq 0 -and $null -eq (Get-Service PulsarAgent -ErrorAction SilentlyContinue) -and -not (Test-Path $agentConfig) -and -not (Test-Path $installDir) -and @(Get-AgentProducts).Count -eq 0) "exit $exit"
    Check 'msi: no host process left' (@(Get-CimInstance Win32_Process -Filter "Name = 'PulsarAgent.exe'").Count -eq 0)

    # --- Install-Pulsar.ps1 -Mode Agent -TrustCertificate: a certificate that can issue certificates is refused; the
    # panel's own is trusted, marked as the script's, and -Uninstall takes that trust away again ---
    function Invoke-Installer([string[]] $arguments) {
        $ErrorActionPreference = 'Continue'
        $output = (& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installScript @arguments 2>&1 | ForEach-Object { "$_" }) -join ' '
        [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
    }
    $authority = New-SelfSignedCertificate -Subject 'CN=Pulsar sandbox authority' -CertStoreLocation 'Cert:\LocalMachine\My' -KeyUsage CertSign `
        -TextExtension @('2.5.29.19={critical}{text}ca=1') -NotAfter (Get-Date).AddDays(2)
    $authorityCer = Join-Path $env:TEMP 'pulsar-authority.cer'
    $null = Export-Certificate -Cert $authority -FilePath $authorityCer
    Remove-Item "Cert:\LocalMachine\My\$($authority.Thumbprint)" -DeleteKey
    $scripted = Api POST '/api/v1/nodes' @{ name = 'sandbox-script-node' }
    $agentValues = @('-Mode', 'Agent', '-PanelUrl', $panel, '-Token', $scripted.enrollment.token, '-PanelKey', $scripted.enrollment.panelKeyFingerprint)
    $refused = Invoke-Installer ($agentValues + @('-TrustCertificate', $authorityCer))
    Check 'install script: refuses to trust a certificate that can issue certificates (nothing installed)' ($refused.ExitCode -ne 0 -and ($refused.Output -replace '\s', '') -match 'CA=true' -and -not (Test-Path "Cert:\LocalMachine\Root\$($authority.Thumbprint)") -and $null -eq (Get-Service PulsarAgent -ErrorAction SilentlyContinue)) "exit $($refused.ExitCode); $($refused.Output -replace '\s+', ' ')"

    # The machine stops trusting the panel's certificate, as on a node that never saw it: the script trusts it again.
    $panelRoot = "Cert:\LocalMachine\Root\$($certificate.Thumbprint)"
    Remove-Item $panelRoot
    $installed = Invoke-Installer ($agentValues + @('-TrustCertificate', $cer))
    Add-Content (Join-Path $report 'install-script-agent.txt') $installed.Output
    $trusted = Get-Item $panelRoot -ErrorAction SilentlyContinue
    Check "install script: agent installed, the panel's certificate trusted and marked as the script's" ($installed.ExitCode -eq 0 -and $null -ne $trusted -and $trusted.FriendlyName -eq 'Pulsar panel (trusted by Install-Pulsar.ps1)' -and (Test-AgentRunning)) "exit $($installed.ExitCode); friendly name: $($trusted.FriendlyName)"
    Check 'install script: node online' (Wait-Until { (Api GET "/api/v1/nodes/$($scripted.node.id)").node.connected } 60)

    $uninstalled = Invoke-Installer @('-Uninstall')
    Add-Content (Join-Path $report 'install-script-agent.txt') $uninstalled.Output
    Check 'install script: -Uninstall removes the agent and the trust it gave' ($uninstalled.ExitCode -eq 0 -and $null -eq (Get-Service PulsarAgent -ErrorAction SilentlyContinue) -and -not (Test-Path $agentExe) -and -not (Test-Path $panelRoot)) "exit $($uninstalled.ExitCode); $($uninstalled.Output -replace '\s+', ' ')"
}
catch {
    Check 'script' $false ($_ | Out-String).Trim()
}
finally {
    # For diagnosis, whatever happened above: what the agent and unenroll logged.
    try {
        $agentLogs = Join-Path $dataDir 'logs'
        if (Test-Path $agentLogs) {
            $copies = New-Item -ItemType Directory -Force (Join-Path $report 'agent-logs')
            Get-ChildItem $agentLogs -File | Where-Object { $_.Name -eq 'unenroll.log' -or $_.Name -like 'agent-*.log' } | Copy-Item -Destination $copies
        }
    }
    catch {
        Add-Content $summary "note: the agent's logs were not copied ($($_.Exception.Message))"
    }
    # The panel's test certificate and its private key stay on a CI runner otherwise (the sandbox is thrown away anyway).
    try {
        if ($certificate) {
            Remove-Item "Cert:\LocalMachine\My\$($certificate.Thumbprint)" -DeleteKey -ErrorAction SilentlyContinue
            Remove-Item "Cert:\LocalMachine\Root\$($certificate.Thumbprint)" -ErrorAction SilentlyContinue
        }
        if ($pfx) { Remove-Item -LiteralPath $pfx -Force -ErrorAction SilentlyContinue }
        if ($cer) { Remove-Item -LiteralPath $cer -Force -ErrorAction SilentlyContinue }
    }
    catch {
        Add-Content $summary "note: the test certificate was not removed ($($_.Exception.Message))"
    }
    Stop-Transcript | Out-Null
    Add-Content $summary 'done'
}

exit $(if (Select-String -Path $summary -Pattern '^FAIL ' -Quiet) { 1 } else { 0 })
