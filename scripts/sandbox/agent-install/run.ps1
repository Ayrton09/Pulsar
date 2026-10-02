# Runs inside Windows Sandbox (LogonCommand, elevated): PostgreSQL + panel on 127.0.0.1, then the agent installed the
# production way. Every check is written to report\summary.txt as PASS/FAIL; the last line is "done".
param([Parameter(Mandatory)] [string] $Bundle)
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

$panel = 'http://127.0.0.1:5290'
$setupToken = 'sandbox-setup-token-not-a-secret-0123456789'
$admin = @{ userName = 'sandbox-admin'; password = 'sandbox admin password' }
$agentSource = Join-Path $Bundle 'agent'
$installDir = 'C:\Program Files\Pulsar\Agent'
$agentExe = Join-Path $installDir 'PulsarAgent.exe'
$dataDir = 'C:\ProgramData\Pulsar\Agent'

try {
    # --- PostgreSQL (trust authentication on loopback: the sandbox is disposable and has no network) ---
    $env:PATH = "$(Join-Path $Bundle 'vcrt');C:\pgsql\bin;$env:PATH"
    & initdb.exe -D C:\pgdata -U postgres -A trust -E UTF8 --no-locale *> (Join-Path $report 'initdb.txt')
    if ($LASTEXITCODE -ne 0) { throw 'initdb failed' }
    # pg_ctl's server inherits handles; through cmd with output to NUL the call returns. Not -Wait: in Windows
    # PowerShell it also waits for child processes, and the server keeps running.
    (Start-Process cmd.exe -ArgumentList '/d', '/c', 'pg_ctl -D C:\pgdata -l C:\pgdata\server.log -w start > NUL 2>&1 < NUL' -PassThru -WindowStyle Hidden).WaitForExit()
    & createdb.exe -h 127.0.0.1 -U postgres pulsar
    Check 'postgresql' ($LASTEXITCODE -eq 0)

    # --- Panel ---
    $env:ConnectionStrings__Pulsar = 'Host=127.0.0.1;Port=5432;Database=pulsar;Username=postgres'
    $env:InitialSetup__Token = $setupToken
    $env:Security__AllowInsecureHttp = 'true'
    $panelDir = Join-Path $Bundle 'panel'
    Start-Process (Join-Path $panelDir 'Pulsar.Api.exe') -ArgumentList '--contentRoot', "`"$panelDir`"", '--urls', $panel `
        -RedirectStandardOutput (Join-Path $report 'panel.txt') -RedirectStandardError (Join-Path $report 'panel-error.txt') -WindowStyle Hidden
    Check 'panel ready' (Wait-Until { (Invoke-WebRequest "$panel/health/ready" -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200 } 90)

    $null = Invoke-WebRequest "$panel/api/v1/auth/session" -SessionVariable web -UseBasicParsing
    function Api([string] $method, [string] $path, $body = $null) {
        $xsrf = ($web.Cookies.GetCookies($panel) | Where-Object Name -eq 'pulsar-xsrf').Value
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
    $unsafeDir = 'C:\Users\WDAGUtilityAccount\Downloads\PulsarAgent'
    Copy-Item $agentSource $unsafeDir -Recurse
    $refused = Invoke-Native (Join-Path $unsafeDir 'PulsarAgent.exe') enroll --panel $panel --token $token --panel-key $panelKey
    Check 'enroll refuses a user-writable folder' ($LASTEXITCODE -eq 1 -and $refused -match 'other accounts could replace it') ($refused.Trim() -replace '\s+', ' ')

    # --- Production install from Program Files ---
    New-Item -ItemType Directory -Force (Split-Path $installDir) | Out-Null
    Copy-Item $agentSource $installDir -Recurse
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
    Add-Content $summary "INFO key storage: $($credential.keyStorage)"

    $ping = Api POST "/api/v1/nodes/$nodeId/ping"
    Check 'signed ping accepted by the service' ($ping.status -eq 'accepted') "$([math]::Round($ping.roundTripMilliseconds, 1)) ms"

    # --- What enroll left on disk and in the key store ---
    $acl = Get-Acl $dataDir
    $principals = ($acl.Access | ForEach-Object { $_.IdentityReference.Value }) | Sort-Object -Unique
    $sids = $principals | ForEach-Object { (New-Object Security.Principal.NTAccount $_).Translate([Security.Principal.SecurityIdentifier]).Value }
    Check 'state directory: SYSTEM + Administrators only, protected' ($acl.AreAccessRulesProtected -and (@($sids | Sort-Object) -join ',') -eq 'S-1-5-18,S-1-5-32-544') "owner $($acl.Owner); $($principals -join ', ')"

    $config = Get-Content (Join-Path $dataDir 'config\agent.json') -Raw | ConvertFrom-Json
    Check 'machine key in config' ($config.key.scope -eq 'machine' -and -not $config.development) "$($config.key.name) $($config.key.storage)"
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

    # --- Revocation cuts the service off ---
    $credentialId = $credential.id
    $null = Api POST "/api/v1/nodes/$nodeId/credentials/$credentialId/revoke" @{ reason = 'sandbox' }
    Check 'offline after revocation' (Wait-Until { -not (Api GET "/api/v1/nodes/$nodeId").node.connected } 15)
    $log = Get-ChildItem (Join-Path $dataDir 'logs') -Filter 'agent-*.log' | Sort-Object LastWriteTime | Select-Object -Last 1
    Check 'the service logs the refusal' (Wait-Until { (Get-Content $log.FullName -Raw) -match 'The panel refused this node' } 20)
    Copy-Item $log.FullName (Join-Path $report 'agent.log')

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
