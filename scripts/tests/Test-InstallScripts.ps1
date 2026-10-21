# Checks the functions of the release's install and backup scripts (scripts\install) that decide what to trust, what to
# remove and where to write, without installing, trusting or backing up anything. Run it in Windows PowerShell 5.1 (the
# scripts' own), elevated or not:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\tests\Test-InstallScripts.ps1
# The scripts act as soon as they run, so their functions are read from them (with the PowerShell parser) and defined
# here. Certificates are made in memory (no certificate store is touched) and folders under -Scratch, removed at the end.
# Every check prints PASS or FAIL; the exit code is 1 on any FAIL. Pester is not needed (Windows ships Pester 3, whose
# syntax newer Pester versions refuse).
param(
    [string] $Scratch = [IO.Path]::GetTempPath()
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3

$installScript = Join-Path $PSScriptRoot '..\install\Install-Pulsar.ps1'
$backupScript = Join-Path $PSScriptRoot '..\install\Backup-Pulsar.ps1'
$script:failures = 0

function Check([string] $name, [bool] $ok, [string] $detail = '') {
    if ($ok) { Write-Host "PASS $name" -ForegroundColor Green }
    else {
        $script:failures++
        Write-Host "FAIL $name$(if ($detail) { " - $detail" })" -ForegroundColor Red
    }
}

# The message of the error a script block throws, or $null when it throws none.
function Get-Thrown([scriptblock] $block) {
    try { $null = & $block; return $null } catch { return $_.Exception.Message }
}

# The text of the named functions and top-level variable assignments of a script.
function Get-ScriptParts([string] $path, [string[]] $functions, [string[]] $variables = @()) {
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile((Resolve-Path $path).ProviderPath, [ref] $tokens, [ref] $errors)
    if ($errors.Count -gt 0) { throw "$path does not parse: $($errors[0])" }
    $parts = @{}
    foreach ($statement in $ast.EndBlock.Statements) {
        if ($statement -is [Management.Automation.Language.FunctionDefinitionAst] -and $statement.Name -in $functions) {
            $parts[$statement.Name] = $statement.Extent.Text
        }
        elseif ($statement -is [Management.Automation.Language.AssignmentStatementAst] -and
            $statement.Left -is [Management.Automation.Language.VariableExpressionAst] -and
            $statement.Left.VariablePath.UserPath -in $variables) {
            $parts['$' + $statement.Left.VariablePath.UserPath] = $statement.Extent.Text
        }
    }
    foreach ($name in @($functions) + @($variables | ForEach-Object { '$' + $_ })) {
        if (-not $parts.ContainsKey($name)) { throw "$path has no $name." }
    }
    return $parts
}

$shared = @('Get-AccountName', 'Find-UntrustedWriter', 'Find-UntrustedWriters', 'Find-UntrustedAncestors', 'Test-PrivateFolder', 'Get-PgSslEnvironment', 'Copy-PlainTree')
$sharedVariables = @('TrustedWriters', 'ModifyRights', 'ReplaceRights')
$installParts = Get-ScriptParts $installScript (@($shared) + @('Read-DerElement', 'Get-CertificateNames', 'Get-ForeignCertificateNames',
        'Test-CertificateNames', 'Get-CertificateAuthority', 'Get-CertificateUses', 'Test-ServerAuthenticationOnly', 'ConvertTo-PrintableText',
        'Assert-TrustableCertificate', 'Get-ListedNames', 'Get-NotedThumbprint', 'Select-TrustToRemove', 'Find-StaleBackupTasks',
        # Phase 18: unattended runs, downloads and their checks, node codes.
        'New-SetupError', 'Get-SetupExitCode', 'Register-Secret', 'Protect-SetupText', 'Format-SetupStep', 'Read-Secret',
        'Get-PrivateFileProblems', 'Read-SecretsFile', 'ConvertFrom-FlatJson', 'ConvertFrom-JsonString', 'ConvertFrom-JsonDocument', 'Get-JsonValue',
        'New-NodeCodeError', 'ConvertFrom-Base64Url', 'Test-RandomPart', 'Test-PanelUrl', 'ConvertFrom-NodeCode', 'Get-CertificateHash',
        'Resolve-NodeCertificate', 'Assert-PinnableCertificate', 'Get-EnrollArguments', 'Test-PostgresInstallerName', 'Select-PostgresVersion', 'Get-PostgresInstallerUrl', 'Find-NonLocalHbaRules',
        'Get-NameAttributes', 'Find-NameMismatches', 'Assert-InstallerSignature', 'Assert-InstallerVersion', 'Read-PostgresMinimum',
        'Assert-PostgresMinimum', 'ConvertTo-Secure', 'ConvertFrom-Secure', 'Assert-NotOlderThanInstalled', 'ConvertTo-CommandLine', 'Add-RuntimeRequirement', 'Read-RuntimeRequirements',
        'Get-InstalledRuntimes', 'Get-MissingRuntimes', 'Test-DotNetDownloadUrl', 'Get-DotNetInstallerEntries', 'Copy-Program', 'Repair-ProgramSwap', 'Move-ProgramFolder', 'Initialize-SetupNetwork',
        # What a panel's install checks first and undoes when it fails; EDB's builds; signers' chains; the agent's runtimes.
        'Test-PortFree', 'Test-PfxFile', 'Get-EndpointPort', 'Assert-PanelInstallable', 'Get-PanelDatabaseState', 'Get-PanelDatabaseMessage',
        'New-Secret', 'New-PanelDatabase', 'Remove-PanelDatabase', 'Undo-PanelInstall', 'Find-PostgresInstallerUrl', 'Get-SignerChain', 'Get-AgentMissingRuntimes',
        # The panel's self-signed certificate: the names and addresses it lists, made in memory.
        'New-PanelCertificate', 'New-SelfSignedPanelCertificate', 'Get-PanelCertificateNames', 'ConvertTo-CertificateAddress', 'Test-PrivateAddress',
        'Get-LanAddresses', 'Select-LanAddresses', 'Get-DhcpAddresses', 'Format-CertificateLine', 'Format-Fingerprint', 'Write-RemoteAccess', 'Assert-SameCertificate',
        'Get-MachineDomain', 'Get-SelfSignedPanelNames', 'New-SelfSignedPanelFiles', 'Write-ExclusiveFile', 'Assert-HostNames', 'Read-Certificate', 'Test-SamePath',
        # -RenewCertificate: the checks before anything changes, the swap, the rollback and what it tells the administrator.
        'Assert-RenewArguments', 'Test-PlainItem', 'Assert-PlainCertificateFiles', 'Get-RenewableCertificate', 'Compare-CertificateNames', 'Get-FileAccess',
        'Set-SameAccess', 'Assert-NewCertificateFiles', 'Stop-Panel', 'Confirm-PanelCertificate', 'Undo-CertificateRenewal', 'Get-LocalNodePin', 'Write-RepinSteps',
        'Invoke-RenewCertificate', 'Get-InstallerTrust', 'Remove-InstallerTrust', 'Initialize-FolderHold', 'Open-CertificateFolder', 'Assert-InFolder', 'Get-PfxHash',
        'New-RandomId', 'Get-PanelAccountSid', 'Assert-PrivateCertificateFolder', 'Get-PanelAccountProcesses', 'Assert-NoPanelProcesses', 'Test-FolderDenies', 'Lock-CertificateFolder', 'Unlock-CertificateFolder',
        'New-SelfSignedPanelBundle', 'Save-PanelBundle')) `
    (@($sharedVariables) + @('TrustedName', 'ServerAuthentication', 'ExitFailed', 'ExitUsage', 'ExitUnsupported', 'ExitOffline', 'ExitUntrusted',
        'ExitCertificate', 'ExitRolledBack', 'ExitRestart', 'NodeCodePrefix', 'NodeCodeMaxLength', 'NodeCodeMaxUrlLength', 'ReusableTokenPrefix', 'JsonStringPattern',
        'DotNetChannel', 'DotNetHosts', 'DotNetFrameworks', 'CodeSigning', 'PostgresDownloadsUrl', 'PostgresSigner', 'DotNetSigner', 'StepPlans',
        'SetupSecrets', 'SecretInputs', 'PulsarRoleComment'))
$backupParts = Get-ScriptParts $backupScript (@($shared) + @('Find-UntrustedDestination', 'New-PrivateFolder', 'Assert-TrustedPgDump')) $sharedVariables
# Every pg_dump the backup may run is checked first: -PostgresBin's, and each service's before its --version.
$backupText = Get-Content -Raw -LiteralPath $backupScript
Check 'backup: pg_dump.exe is checked before it runs, the one of -PostgresBin and of each service' (
    $backupText -match 'Assert-TrustedPgDump \$PostgresBin' -and
    $backupText -match "Assert-TrustedPgDump \`$bin\s+\`$version = \(& \(Join-Path \`$bin 'pg_dump\.exe'\) --version\)" -and
    ([regex]::Matches($backupText, "& \(Join-Path \`$(bin|PostgresBin) 'pg_dump\.exe'\)")).Count -eq 2)

# What the two scripts share must stay the same in both.
$different = @(@($shared) + @($sharedVariables | ForEach-Object { '$' + $_ }) | Where-Object { $installParts[$_] -cne $backupParts[$_] })
Check 'the functions the install and backup scripts share are the same in both' ($different.Count -eq 0) ($different -join ', ')

foreach ($part in @($installParts.Values) + @($backupParts.Values)) { . ([scriptblock]::Create($part)) }
$TrustedMarker = 'trusted-thumbprint.txt'
$TrustCertificateWithoutBasicConstraints = $false
$TrustCertificateWithOtherNames = $false
$TrustCertificateWithOtherUses = $false
$Unattended = $false
$WarningPreference = 'SilentlyContinue'

# --- Public IP certificate (-PublicIp) ------------------------------------------------------------------------------

# The address rule, as the panel's (PublicAddresses): what -PublicIp takes, and what it refuses before anything changes.
. ([scriptblock]::Create((Get-ScriptParts $installScript @('Test-PublicIpAddress'))['Test-PublicIpAddress']))
foreach ($address in '1.2.3.4', '8.8.8.8', '100.128.0.1', '172.32.0.1', '203.0.114.1', '2001:4860:4860::8888', '2a00:1450:4001:82a::200e', '2001:200::1') {
    Check "-PublicIp takes the public address $address" (Test-PublicIpAddress $address)
}
foreach ($address in '10.0.0.1', '172.16.5.4', '192.168.1.10', '127.0.0.1', '0.0.0.0', '169.254.1.1', '100.64.0.1', '100.127.255.254', '192.0.0.9',
    '192.0.2.10', '198.51.100.7', '203.0.113.10', '198.18.0.1', '192.88.99.1', '224.0.0.1', '240.0.0.1', '255.255.255.255', '::', '::1', 'fe80::1',
    'fd12:3456::1', 'ff02::1', '2001:db8::1', '2001::1', '2002:c000:204::1', '3fff::1', '64:ff9b::808:808', '::ffff:8.8.8.8') {
    Check "-PublicIp refuses the local, shared or reserved address $address" (-not (Test-PublicIpAddress $address))
}
foreach ($text in 'panel.example.com', '1.2.3', '01.2.3.4', '1.2.3.256', '1.2.3.4:8443', '[2001:4860::1]', '2001:4860::1%3', ' 1.2.3.4', '0x01020304', '16909060', '') {
    Check "-PublicIp refuses '$text', which is not an address literal" (-not (Test-PublicIpAddress $text))
}

# What -PublicIp does: checked before anything changes (port 80 free too), a firewall rule for port 80 only with it, for
# the panel's program on the panel's profiles, removed on -Uninstall and when an install is undone, and the setting
# written through the API once the administrator exists; an agent refuses it.
$installText = Get-Content -Raw -LiteralPath $installScript
$publicIpParts = Get-ScriptParts $installScript @('Assert-PublicIpInstallable', 'Assert-PanelInstallable', 'Install-Panel', 'Uninstall-Panel', 'Undo-PanelInstall', 'Resolve-AgentInputs')
# Assert-PanelInstallable runs it (the preflight checks below): without -PublicIp it does nothing; a private address is
# refused before port 80 is even looked at.
. ([scriptblock]::Create($publicIpParts['Assert-PublicIpInstallable']))
$PublicIp = '192.168.1.10'
Check '-PublicIp refuses a private address before anything changes' ((Get-Thrown { Assert-PublicIpInstallable }) -like "-PublicIp must be this panel's public IP address*")
$PublicIp = ''
Check 'without -PublicIp nothing is checked' ($null -eq (Get-Thrown { Assert-PublicIpInstallable }))
Check '-PublicIp is a parameter of installs only' ($installText -match "\[Parameter\(ParameterSetName = 'Install'\)\] \[string\] \`$PublicIp,")
Check '-PublicIp is checked with the panel''s ports: a public address, and port 80 free' (
    $publicIpParts['Assert-PanelInstallable'] -match 'Assert-PublicIpInstallable' -and
    $publicIpParts['Assert-PublicIpInstallable'] -match 'Test-PublicIpAddress \$PublicIp' -and $publicIpParts['Assert-PublicIpInstallable'] -match 'Test-PortFree 80')
$panelRule = [regex]::Match($publicIpParts['Install-Panel'], "(?s)New-NetFirewallRule -Name 'Pulsar-Panel-HTTPS'.*?-Profile \`$profiles").Value
$acmeRule = [regex]::Match($publicIpParts['Install-Panel'], "(?s)if \(\`$PublicIp\) \{\s+Write-Step [^\n]+\s+\`$null = New-NetFirewallRule -Name 'Pulsar-Panel-ACME'[^\n]+\s+-Protocol TCP -LocalPort 80 -Program \`$exe -Action Allow -Profile \`$profiles\s+\}").Value
Check '-PublicIp opens port 80 only with it, for the panel''s program, on the panel''s networks' ($panelRule -and $acmeRule -and
    @([regex]::Matches($installText, "New-NetFirewallRule -Name 'Pulsar-Panel-ACME'")).Count -eq 1 -and
    $publicIpParts['Install-Panel'] -match "(?s)if \(-not \`$NoFirewall\) \{.*Get-NetFirewallRule -Name 'Pulsar-Panel-ACME' -ErrorAction SilentlyContinue \| Remove-NetFirewallRule\s+if \(\`$PublicIp\)")
Check 'the port 80 rule goes with -Uninstall and when an install is undone' (
    $publicIpParts['Uninstall-Panel'] -match "Get-NetFirewallRule -Name 'Pulsar-Panel-ACME' -ErrorAction SilentlyContinue \| Remove-NetFirewallRule" -and
    $publicIpParts['Undo-PanelInstall'] -match "foreach \(\`$rule in [^\n]*'Pulsar-Panel-ACME'")
Check '-PublicIp turns the certificate on through the API once the administrator exists (after -SftpPort''s setting)' (
    $publicIpParts['Install-Panel'] -match "(?s)/api/v1/auth/setup.*/api/v1/sftp/settings.*if \(\`$PublicIp\) \{.*Invoke-Panel \`$client PUT '/api/v1/public-ip-certificate' @\{ enabled = \`$true; publicIp = \`$PublicIp; email = \`$null; directory = 'production' \}")
Check 'an agent refuses -PublicIp' ($publicIpParts['Resolve-AgentInputs'] -match "if \(\`$PublicIp\) \{ throw \(New-SetupError \`$ExitUsage")

# The wizard's field: on its own Advanced page, checked, passed on as -PublicIp (no secret), and shown in the summary.
$publicIpIss = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\release\setup\PulsarSetup.iss')
Check 'setup: the public IP page is an Advanced page of panel installs, after the network page' (
    $publicIpIss -match "PublicIpPage := CreateInputQueryPage\(NetworkPage\.ID, CustomMessage\('PublicIpCaption'\)" -and
    $publicIpIss -match "if PageID = PublicIpPage\.ID then\s+Result := UpdateMode or not InstallsPanel or not UsesAdvanced;" -and
    $publicIpIss -match "PublicIpPage\.Values\[0\] := ExpandConstant\('\{param:PUBLICIP\|\}'\);")
Check 'setup: the public IP is checked before going on, and passed on as -PublicIp' (
    $publicIpIss -match "if CurPageID = PublicIpPage\.ID then\s+Result := CheckPublicIpPage;" -and
    $publicIpIss -match "Result := \(PublicIpValue = ''\) or IsPublicIp\(PublicIpValue\);" -and
    $publicIpIss -match "if PublicIpValue <> '' then\s+Result := Result \+ NamedArgument\('PublicIp', PublicIpValue\);" -and
    $publicIpIss -match "FmtMessage\(CustomMessage\('MemoPublicIp'\), \[PublicIpValue\]\)")

# --- Certificates -------------------------------------------------------------------------------------------------

$ServerAuth = '1.3.6.1.5.5.7.3.1'
$ClientAuth = '1.3.6.1.5.5.7.3.2'
$CodeSigning = '1.3.6.1.5.5.7.3.3'

function ConvertTo-Der([int] $tag, [byte[]] $content) {
    $length = $content.Length
    if ($length -lt 0x80) { $header = @($tag, $length) }
    elseif ($length -lt 0x100) { $header = @($tag, 0x81, $length) }
    else { $header = @($tag, 0x82, ($length -shr 8), ($length -band 0xFF)) }
    return [byte[]](@($header) + @($content))
}

function Dns([string] $name) { @{ Tag = 0x82; Bytes = [Text.Encoding]::GetEncoding(28591).GetBytes($name) } }
function Ip([string] $address) { @{ Tag = 0x87; Bytes = ([Net.IPAddress]::Parse($address)).GetAddressBytes() } }
function Email([string] $address) { @{ Tag = 0x81; Bytes = [Text.Encoding]::ASCII.GetBytes($address) } }

# A certificate made in memory, without its key. -Authority: $false (CA=false), $true, or $null (no Basic Constraints).
# -Uses: the Extended Key Usage's purposes, or $null for none.
function New-TestCertificate([object[]] $Names = @(), $Authority = $false, $Uses = @($ServerAuth), [double] $Days = 1) {
    $rsa = [Security.Cryptography.RSACng]::new(2048)
    try {
        $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=Pulsar test', $rsa,
            [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        if ($null -ne $Authority) {
            $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new([bool]$Authority, $false, 0, $true))
        }
        if ($null -ne $Uses) {
            $oids = New-Object Security.Cryptography.OidCollection
            foreach ($use in $Uses) { $null = $oids.Add([Security.Cryptography.Oid]::new($use)) }
            $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($oids, $false))
        }
        if ($Names.Count -gt 0) {
            $content = @()
            foreach ($name in $Names) { $content += @(ConvertTo-Der $name.Tag $name.Bytes) }
            $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509Extension]::new('2.5.29.17', (ConvertTo-Der 0x30 ([byte[]]$content)), $false))
        }
        $certificate = $request.CreateSelfSigned([DateTimeOffset]::Now.AddDays(-1), [DateTimeOffset]::Now.AddDays($Days))
        return [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificate.RawData)
    }
    finally { $rsa.Dispose() }
}

$esc = [char]0x1B
$cr = [char]0x0D
$lf = [char]0x0A

$names = Get-CertificateNames (New-TestCertificate @((Dns 'panel.example.com'), (Dns 'PANEL'), (Ip '10.0.0.5'), (Ip '::1')))
Check 'SAN parser: DNS names and IP addresses' (($names.Dns -join ',') -eq 'panel.example.com,PANEL' -and ($names.Ips -join ',') -eq '10.0.0.5,::1' -and $names.Others.Count -eq 0) "$($names.Dns -join ',') | $($names.Ips -join ',')"
$names = Get-CertificateNames (New-TestCertificate @((Dns '*.example.com'), (Email 'admin@example.com')))
Check 'SAN parser: a wildcard is a DNS name; an e-mail address is another kind' (($names.Dns -join ',') -eq '*.example.com' -and ($names.Others -join ',') -eq 'an e-mail address')
foreach ($bad in @("panel.example.com${cr}www.github.com", "panel$esc[2K", 'pa nel', 'a*.example.com', '*', '*.', 'a..b', "caf$([char]0xE9).com", '', "panel.example.com$lf", "*.example.com$lf", "panel.example.com.$lf", "panel.example.com$lf$lf")) {
    $thrown = Get-Thrown { Get-CertificateNames (New-TestCertificate @((Dns $bad))) }
    $printable = $null -ne $thrown -and $thrown -notmatch '[\x00-\x1F\x7F]'
    Check "SAN parser: refuses the DNS name '$(ConvertTo-PrintableText $bad)', printing it without control characters" ($null -ne $thrown -and $thrown -match 'not one' -and $printable) "$(ConvertTo-PrintableText $thrown)"
}

# The pattern of -HostName, read from the script's parameters: a line feed after a name is not part of the name ($ also
# matches before a final line feed; \z does not).
$parseTokens = $null
$parseErrors = $null
$parsed = [Management.Automation.Language.Parser]::ParseFile((Resolve-Path $installScript).ProviderPath, [ref] $parseTokens, [ref] $parseErrors)
$hostNameParameter = $parsed.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'HostName' }
$hostNameAttribute = $hostNameParameter.Attributes | Where-Object { $_.TypeName.Name -eq 'ValidatePattern' }
$hostNamePattern = $hostNameAttribute.PositionalArguments[0].Value
$hostNameOptions = ($hostNameAttribute.NamedArguments | Where-Object { $_.ArgumentName -eq 'Options' }).Argument.Value
Check '-HostName: the pattern is case-sensitive' ($hostNameOptions -eq 'None' -and $hostNamePattern) "$hostNameOptions"
foreach ($good in @('panel.example.com', 'PANEL', '*.example.com', 'panel.example.com.', 'my_box-1.lan', '192.168.1.20', '2001:db8::7', '::1', 'FD00::5', '::ffff:192.168.1.20')) {
    Check "-HostName: accepts '$good'" ([regex]::IsMatch($good, $hostNamePattern, 'None'))
}
foreach ($bad in @("panel.example.com$lf", "*.example.com$lf", "panel.example.com.$lf", "panel.example.com$lf$lf", "panel$lf.example.com", "panel.example.com$cr", 'pa nel', '', 'a..b',
        '[::1]', 'fe80::1%3', "2001:db8::7$lf", '2001:db8::7/64', 'panel.example.com:8443', 'https://panel.example.com')) {
    Check "-HostName: refuses '$(ConvertTo-PrintableText $bad)'" (-not [regex]::IsMatch($bad, $hostNamePattern, 'None'))
}
$thrown = Get-Thrown { Get-CertificateNames (New-TestCertificate @(@{ Tag = 0x87; Bytes = [byte[]](1, 2, 3, 4, 5) })) }
Check 'SAN parser: refuses an IP address of 5 bytes' ($thrown -match 'malformed') "$thrown"

$names = Get-CertificateNames (New-TestCertificate @((Dns 'panel.example.com.'), (Dns '*.lan'), (Ip '192.168.1.20'), (Ip 'fd00::5')))
Check 'names: the same DNS name, in other case and without the final dot' (Test-CertificateNames $names 'PANEL.Example.com')
Check 'names: a wildcard stands for one label' (Test-CertificateNames $names 'box.lan')
Check 'names: a wildcard does not stand for two labels' (-not (Test-CertificateNames $names 'a.box.lan'))
Check 'names: a wildcard does not stand for its domain' (-not (Test-CertificateNames $names 'lan'))
Check 'names: an IP address' ((Test-CertificateNames $names '192.168.1.20') -and (Test-CertificateNames $names 'fd00:0::5'))
Check 'names: another IP address, or an IP written as a name, is not listed' (-not (Test-CertificateNames $names '192.168.1.21') -and -not (Test-CertificateNames $names 'other.example.com'))

$names = Get-CertificateNames (New-TestCertificate @((Dns 'panel.example.com'), (Dns 'localhost'), (Dns 'PANEL'), (Ip '203.0.113.5')))
Check "foreign names: none besides the panel's host, localhost and machine names (the panel's IP named in -PanelUrl)" (@(Get-ForeignCertificateNames $names @('203.0.113.5')).Count -eq 1 -and @(Get-ForeignCertificateNames $names @('panel.example.com', '203.0.113.5')).Count -eq 0 -and
    (@(Get-ForeignCertificateNames $names @('panel.example.com')) -join ',') -eq '203.0.113.5')
# The installer's certificate lists 127.0.0.1 and ::1: like localhost, this machine itself. A private address is
# another machine of the local network (the router, a NAS), unless it is the panel's host.
$names = Get-CertificateNames (New-TestCertificate @((Dns 'panel.example.com'), (Ip '127.0.0.1'), (Ip '::1'), (Ip '127.0.0.2')))
Check 'foreign names: the loopback addresses are not' (@(Get-ForeignCertificateNames $names @('panel.example.com')).Count -eq 0) (@(Get-ForeignCertificateNames $names @('panel.example.com')) -join ',')
$names = Get-CertificateNames (New-TestCertificate @((Dns 'panel.example.com'), (Ip '192.168.1.1'), (Ip '10.0.0.1'), (Ip '172.16.0.1'), (Ip '192.168.0.54')))
$foreign = @(Get-ForeignCertificateNames $names @('panel.example.com'))
Check 'foreign names: private IPv4 addresses (a router, a NAS) are' (($foreign -join ',') -ceq '192.168.1.1,10.0.0.1,172.16.0.1,192.168.0.54') ($foreign -join ',')
$foreign = @(Get-ForeignCertificateNames $names @('192.168.0.54'))
Check "foreign names: the private address of -PanelUrl is the panel's, the others are not" (($foreign -join ',') -ceq 'panel.example.com,192.168.1.1,10.0.0.1,172.16.0.1') ($foreign -join ',')
# Some resolvers read a number as an IPv4 address (134744072 is 8.8.8.8, 3232235777 is 192.168.1.1, 0xc0a80101 too).
$names = Get-CertificateNames (New-TestCertificate @((Dns 'panel.example.com'), (Dns '134744072'), (Dns '3232235777'), (Dns '0xc0a80101'), (Dns '0X7F000001'), (Dns 'PANEL'), (Dns 'box1')))
$foreign = @(Get-ForeignCertificateNames $names @('panel.example.com'))
Check 'foreign names: a single label that is a number (decimal or 0x) is, a machine name is not' (($foreign -join ',') -ceq '134744072,3232235777,0xc0a80101,0X7F000001') ($foreign -join ',')
$names = Get-CertificateNames (New-TestCertificate @((Dns 'panel.example.com'), (Ip '169.254.1.1'), (Ip '100.64.0.1'), (Ip '172.32.0.1'), (Ip '172.15.0.1'), (Ip '203.0.113.7'), (Ip 'fd00::5'), (Ip 'fe80::1'), (Ip '::ffff:10.0.0.1')))
$foreign = @(Get-ForeignCertificateNames $names @('panel.example.com'))
Check 'foreign names: link-local, carrier NAT, public and IPv6 addresses (but ::1), and an IPv4 address written as IPv6, are' ($foreign.Count -eq 8) ($foreign -join ',')
$names = Get-CertificateNames (New-TestCertificate @((Dns 'panel.example.com'), (Dns 'www.github.com'), (Dns '*.com'), (Ip '8.8.8.8'), (Email 'a@b.c')))
$foreign = @(Get-ForeignCertificateNames $names @('panel.example.com'))
Check 'foreign names: another site, a wildcard, an IP address and an e-mail address' (($foreign -join ',') -eq 'www.github.com,*.com,8.8.8.8,an e-mail address') ($foreign -join ',')
$names = Get-CertificateNames (New-TestCertificate @((Dns '*.example.com'), (Dns 'localhost')))
Check "foreign names: a wildcard the panel's own certificate was made for (-HostName) is the panel's" (@(Get-ForeignCertificateNames $names @('localhost', '*.example.com')).Count -eq 0 -and @(Get-ForeignCertificateNames $names @('panel.example.com')).Count -eq 1)

Check 'printable text: line breaks, escapes and direction overrides become ?, letters stay' ((ConvertTo-PrintableText "a${cr}b$esc[2J$([char]0x202E)c$([char]0xE9)") -eq "a?b?[2J?c$([char]0xE9)")
Check 'uses: server authentication alone' ((Test-ServerAuthenticationOnly @($ServerAuth)) -and -not (Test-ServerAuthenticationOnly $null) -and -not (Test-ServerAuthenticationOnly @()) -and -not (Test-ServerAuthenticationOnly @($ServerAuth, $ClientAuth)) -and -not (Test-ServerAuthenticationOnly @($CodeSigning)))
Check 'uses: read from the certificate; $null without Extended Key Usage' ($null -eq (Get-CertificateUses (New-TestCertificate @((Dns 'a')) $false $null)) -and ((Get-CertificateUses (New-TestCertificate @((Dns 'a')) $false @($ServerAuth, $ClientAuth))) -join ',') -eq "$ServerAuth,$ClientAuth")

$panelNames = @((Dns 'panel.example.com'), (Dns 'localhost'), (Dns 'PANEL'))
function Assert-Cert($certificate, [string] $hostName = 'panel.example.com', [string[]] $panelNames = @()) {
    Get-Thrown { Assert-TrustableCertificate $certificate 'panel.cer' $hostName $panelNames }
}
Check "trust: the panel's own certificate (CA=false, server authentication, its names)" ($null -eq (Assert-Cert (New-TestCertificate $panelNames))) "$(Assert-Cert (New-TestCertificate $panelNames))"
Check "trust: on the panel's machine, every name it was made for" ($null -eq (Assert-Cert (New-TestCertificate @((Dns 'localhost'), (Dns 'PANEL'), (Dns 'panel.example.com'), (Dns 'panel.home.lan'))) 'localhost' @('localhost', 'PANEL', 'panel.example.com', 'panel.home.lan')))
Check 'trust: refuses a certificate authority' ((Assert-Cert (New-TestCertificate $panelNames $true)) -match 'CA=true')
Check 'trust: refuses one without Basic Constraints, unless told' ((Assert-Cert (New-TestCertificate $panelNames $null)) -match 'TrustCertificateWithoutBasicConstraints')
$TrustCertificateWithoutBasicConstraints = $true
Check 'trust: -TrustCertificateWithoutBasicConstraints lets it through' ($null -eq (Assert-Cert (New-TestCertificate $panelNames $null)))
$TrustCertificateWithoutBasicConstraints = $false
Check "trust: refuses one that does not name the panel's host" ((Assert-Cert (New-TestCertificate $panelNames) 'other.example.com') -match 'is not made for')
Check 'trust: refuses one that also names another site' ((Assert-Cert (New-TestCertificate (@($panelNames) + @(Dns 'www.github.com')))) -match 'www\.github\.com.*TrustCertificateWithOtherNames')
Check 'trust: refuses a wildcard that covers the host' ((Assert-Cert (New-TestCertificate @((Dns '*.com'))) 'panel.com') -match 'TrustCertificateWithOtherNames')
$TrustCertificateWithOtherNames = $true
Check 'trust: -TrustCertificateWithOtherNames lets other names through' ($null -eq (Assert-Cert (New-TestCertificate (@($panelNames) + @(Dns 'www.github.com')))))
$TrustCertificateWithOtherNames = $false
Check 'trust: refuses client authentication besides server authentication (earlier installers made those)' ((Assert-Cert (New-TestCertificate $panelNames $false @($ServerAuth, $ClientAuth))) -match 'TrustCertificateWithOtherUses')
Check 'trust: refuses code signing' ((Assert-Cert (New-TestCertificate $panelNames $false @($CodeSigning))) -match 'TrustCertificateWithOtherUses')
Check 'trust: refuses one without Extended Key Usage (every use)' ((Assert-Cert (New-TestCertificate $panelNames $false $null)) -match 'no Extended Key Usage.*TrustCertificateWithOtherUses')
$TrustCertificateWithOtherUses = $true
Check 'trust: -TrustCertificateWithOtherUses lets other uses through' ($null -eq (Assert-Cert (New-TestCertificate $panelNames $false $null)))
$TrustCertificateWithOtherUses = $false
$thrown = Assert-Cert (New-TestCertificate @((Dns "evil$esc[1A"), (Dns 'panel.example.com')))
Check 'trust: a DNS name with an escape is refused, and the message has no control characters' ($null -ne $thrown -and $thrown -notmatch '[\x00-\x1F\x7F]') "$(ConvertTo-PrintableText $thrown)"
# The warnings Assert-TrustableCertificate writes for a certificate.
function Get-TrustWarnings($certificate) {
    @(& { $WarningPreference = 'Continue'; Assert-TrustableCertificate $certificate 'panel.cer' 'panel.example.com' } 3>&1 | Where-Object { $_ -is [Management.Automation.WarningRecord] } | ForEach-Object { "$_" })
}
$warned = @(Get-TrustWarnings (New-TestCertificate $panelNames $false @($ServerAuth) 3650))
Check 'trust: warns about a certificate valid for longer than 5 years (and a day)' ($warned.Count -eq 1 -and $warned[0] -match 'valid for longer than 5 years') ($warned -join ' | ')
$warned = @(Get-TrustWarnings (New-TestCertificate $panelNames $false @($ServerAuth) 1825))
Check "trust: no warning for 5 years (the installer's own)" ($warned.Count -eq 0) ($warned -join ' | ')

# The all-in-one panel trusts the certificate it made, not whatever tls\panel.cer (which its service account may
# change) holds by then.
$madeCertificate = New-TestCertificate $panelNames
$otherCertificate = New-TestCertificate $panelNames
Check 'trust: the certificate read is the one made (its SHA-256)' ($null -eq (Get-Thrown { Assert-SameCertificate $madeCertificate 'panel.cer' (Get-CertificateHash $madeCertificate) }) -and
    (Get-Thrown { Assert-SameCertificate $otherCertificate 'panel.cer' (Get-CertificateHash $madeCertificate) }) -match 'not the certificate this install made' -and
    $null -eq (Get-Thrown { Assert-SameCertificate $otherCertificate 'panel.cer' '' }))
$addTrusted = $parsed.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Add-TrustedCertificate' }, $true) | Select-Object -First 1
$ownTrust = $parsed.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Add-TrustedCertificate' -and $node.Extent.Text -match '\$certificate\.Cer' }, $true) | Select-Object -First 1
Check "trust: the all-in-one panel's certificate is checked against the one made before it is trusted" ($null -ne $addTrusted -and
    $addTrusted.Body.Extent.Text -match 'Read-Certificate \$cer\s+Assert-SameCertificate \$certificate \$cer \$sha256' -and $null -ne $ownTrust -and $ownTrust.Extent.Text -match '\$certificate\.Sha256\s*$') "$(if ($ownTrust) { $ownTrust.Extent.Text })"

# --- The panel's self-signed certificate: its names and addresses, made in memory ----------------------------------

# -HostName: an address as one, a name as one, and what is neither refused.
foreach ($case in @(@('192.168.1.20', '192.168.1.20'), @('203.0.113.7', '203.0.113.7'), @('2001:DB8:0::7', '2001:db8::7'), @('::1', '::1'), @('fd00::5', 'fd00::5'))) {
    $address = ConvertTo-CertificateAddress $case[0]
    Check "-HostName: $($case[0]) is the IP address $($case[1])" ($address -is [Net.IPAddress] -and "$address" -ceq $case[1]) "$address"
}
foreach ($name in 'panel.example.com', 'PANEL', '*.example.com', 'panel.example.com.', 'my_box-1.lan', 'face', 'box.1e', '1x.lan') {
    Check "-HostName: $name is a name" ($null -eq (ConvertTo-CertificateAddress $name))
}
foreach ($case in ([ordered]@{
        '10.1' = 'neither a name'; '192.168.1.300' = 'neither a name'; '1.2.3.4.5' = 'neither a name'; '01.2.3.4' = 'neither a name'; '192.168.01.1' = 'neither a name'
        '0x7f' = 'neither a name'; 'box.0x10' = 'neither a name'; '*.1' = 'neither a name'; 'panel.1.' = 'neither a name'
        '0.0.0.0' = 'not an address a server has'; '0.1.2.3' = 'not an address a server has'; '255.255.255.255' = 'not an address a server has'; '224.0.0.1' = 'not an address a server has'
        '::' = 'not an address a server has'; 'ff02::1' = 'not an address a server has'; '::ffff:192.168.1.20' = 'give it as IPv4 \(192\.168\.1\.20\)'
        '1:2' = 'not an IPv6 address'; '[::1]' = 'not an IPv6 address'; 'fe80::1%3' = 'not an IPv6 address'; '::1::2' = 'not an IPv6 address'
    }).GetEnumerator()) {
    $thrown = Get-Thrown { ConvertTo-CertificateAddress $case.Key }
    Check "-HostName: refuses $($case.Key)" ($thrown -match $case.Value) "$thrown"
}

# Private-use addresses (RFC 1918), and nothing else.
foreach ($case in @(@('10.0.0.1', $true), @('10.255.255.255', $true), @('172.16.0.1', $true), @('172.31.255.255', $true), @('192.168.0.54', $true),
        @('172.15.255.255', $false), @('172.32.0.1', $false), @('192.169.0.1', $false), @('169.254.1.1', $false), @('100.64.0.1', $false), @('127.0.0.1', $false),
        @('8.8.8.8', $false), @('fd00::1', $false), @('::ffff:10.0.0.1', $false))) {
    Check "private addresses: $($case[0]) $(if ($case[1]) { 'is' } else { 'is not' }) one" ((Test-PrivateAddress ([Net.IPAddress]::Parse($case[0]))) -eq $case[1])
}

# This machine's addresses on its networks, from Get-NetIPAddress, Get-NetAdapter and Get-NetRoute (as they answer on a
# PC with Ethernet, Wi-Fi, Hyper-V's Default Switch and a VPN adapter).
function Net-Address([string] $address, [int] $index, [string] $state = 'Preferred', [string] $origin = 'Dhcp') { [pscustomobject]@{ IPAddress = $address; InterfaceIndex = $index; AddressState = $state; PrefixOrigin = $origin } }
function Net-Adapter([int] $index, [string] $status) { [pscustomobject]@{ InterfaceIndex = $index; Status = $status } }
function Net-Route([int] $index) { [pscustomobject]@{ InterfaceIndex = $index; DestinationPrefix = '0.0.0.0/0' } }
$netAddresses = @(
    (Net-Address '192.168.0.54' 17), (Net-Address '172.17.80.1' 16), (Net-Address '169.254.59.181' 5 'Tentative'), (Net-Address '10.8.0.2' 9),
    (Net-Address '127.0.0.1' 1), (Net-Address '192.168.0.60' 17 'Tentative'), (Net-Address '192.168.0.61' 17 'Duplicate'), (Net-Address '203.0.113.9' 17),
    (Net-Address '10.0.0.7' 21), (Net-Address '192.168.0.54' 21), (Net-Address '100.64.1.2' 21))
$netAdapters = @((Net-Adapter 17 'Up'), (Net-Adapter 16 'Up'), (Net-Adapter 5 'Disconnected'), (Net-Adapter 9 'Disconnected'), (Net-Adapter 21 'Up'))
$netRoutes = @((Net-Route 17), (Net-Route 9), (Net-Route 21))
$selected = @(Select-LanAddresses $netAddresses $netAdapters $netRoutes)
Check "LAN addresses: private ones in use, on adapters that are up and lead to a router (not Hyper-V's Default Switch, a disconnected VPN, the loopback, 169.254, public or carrier-NAT ones), each once" (
    (@($selected | ForEach-Object { "$_" }) -join ',') -ceq '192.168.0.54,10.0.0.7' -and @($selected | Where-Object { $_ -isnot [Net.IPAddress] }).Count -eq 0) (@($selected | ForEach-Object { "$_" }) -join ',')
Check 'LAN addresses: none without a router' (@(Select-LanAddresses $netAddresses $netAdapters @()).Count -eq 0)
$lanFromCmdlets = & {
    function Get-NetIPAddress { param($AddressFamily, $ErrorAction) if ($AddressFamily -eq 'IPv4') { $netAddresses } }
    function Get-NetAdapter { param($ErrorAction) $netAdapters }
    function Get-NetRoute { param($AddressFamily, $DestinationPrefix, $ErrorAction) if ($AddressFamily -eq 'IPv4' -and $DestinationPrefix -eq '0.0.0.0/0') { $netRoutes } }
    @(Get-LanAddresses)
}
Check 'LAN addresses: read from Get-NetIPAddress (IPv4), Get-NetAdapter and Get-NetRoute (the default route)' ((@($lanFromCmdlets | ForEach-Object { "$_" }) -join ',') -ceq '192.168.0.54,10.0.0.7') (@($lanFromCmdlets | ForEach-Object { "$_" }) -join ',')
$leasedAddresses = @((Net-Address '192.168.0.54' 17), (Net-Address '10.0.0.7' 21 'Preferred' 'Manual'), (Net-Address '127.0.0.1' 1 'Preferred' 'WellKnown'), (Net-Address '172.17.80.1' 16 'Preferred' 'Dhcp'))
$leased = @(& { function Get-NetIPAddress { param($AddressFamily, $ErrorAction) if ($AddressFamily -eq 'IPv4') { $leasedAddresses } }; @(Get-DhcpAddresses) })
Check 'DHCP addresses: those Get-NetIPAddress says a DHCP server gave' (($leased -join ',') -ceq '192.168.0.54,172.17.80.1') ($leased -join ',')

# What the certificate lists: the machine's names, -HostName's names; the loopback addresses, the LAN's, -HostName's.
$lan = @([Net.IPAddress]::Parse('192.168.0.54'), [Net.IPAddress]::Parse('10.0.0.7'))
$listed = Get-PanelCertificateNames @('localhost', 'PANEL', 'PANEL.corp.example') $lan @('panel.example.com', '203.0.113.7', '2001:DB8::7', '192.168.0.54', 'PANEL', '::1')
Check 'certificate names: DNS names as given, each once' (($listed.Dns -join ',') -ceq 'localhost,PANEL,PANEL.corp.example,panel.example.com') ($listed.Dns -join ',')
Check 'certificate names: 127.0.0.1, ::1, the LAN addresses, then -HostName''s, each once, as IP addresses' (
    (@($listed.Ips | ForEach-Object { "$_" }) -join ',') -ceq '127.0.0.1,::1,192.168.0.54,10.0.0.7,203.0.113.7,2001:db8::7' -and @($listed.Ips | Where-Object { $_ -isnot [Net.IPAddress] }).Count -eq 0 -and $listed.Others.Count -eq 0) (@($listed.Ips | ForEach-Object { "$_" }) -join ',')
$thrown = Get-Thrown { Get-PanelCertificateNames @('localhost') @() @('10.1') }
Check 'certificate names: a -HostName that is neither a name nor an address stops it' ($thrown -match 'neither a name') "$thrown"

# The certificate itself, made in memory as the installer makes it (no store, no key container).
$built = New-SelfSignedPanelCertificate $listed
try {
    $extensionOids = @($built.Extensions | ForEach-Object { $_.Oid.Value })
    $basic = $built.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.19' }
    $keyUsage = $built.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.15' }
    $enhanced = $built.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' }
    $alternative = $built.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.17' }
    $usageFlags = [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature -bor [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyEncipherment
    $read = Get-CertificateNames $built
    Check 'self-signed certificate: subject and issuer CN=localhost' ($built.Subject -ceq 'CN=localhost' -and $built.Issuer -ceq 'CN=localhost') $built.Subject
    Check 'self-signed certificate: its DNS names as DNS names, its addresses as IP addresses (browsers match addresses only against those)' (
        ($read.Dns -join ',') -ceq 'localhost,PANEL,PANEL.corp.example,panel.example.com' -and (@($read.Ips | ForEach-Object { "$_" }) -join ',') -ceq '127.0.0.1,::1,192.168.0.54,10.0.0.7,203.0.113.7,2001:db8::7' -and $read.Others.Count -eq 0) "$($read.Dns -join ',') | $($read.Ips -join ',')"
    Check 'self-signed certificate: Basic Constraints, critical, CA=false' ($null -ne $basic -and $basic.Critical -and (Get-CertificateAuthority $built) -eq $false)
    Check 'self-signed certificate: Key Usage, critical, digital signature and key encipherment' ($null -ne $keyUsage -and $keyUsage.Critical -and
        ([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new($keyUsage, $true)).KeyUsages -eq $usageFlags)
    Check 'self-signed certificate: Extended Key Usage server authentication alone, not critical' ($null -ne $enhanced -and -not $enhanced.Critical -and (Test-ServerAuthenticationOnly (Get-CertificateUses $built)))
    Check 'self-signed certificate: the Subject Alternative Name is not critical, and there is a Subject Key Identifier' ($null -ne $alternative -and -not $alternative.Critical -and '2.5.29.14' -in $extensionOids)
    Check 'self-signed certificate: those five extensions, each once' ($extensionOids.Count -eq 5 -and @($extensionOids | Select-Object -Unique).Count -eq 5) ($extensionOids -join ',')
    Check 'self-signed certificate: RSA 2048 with SHA-256' ([Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($built).KeySize -eq 2048 -and $built.SignatureAlgorithm.Value -eq '1.2.840.113549.1.1.11')
    $now = Get-Date
    Check 'self-signed certificate: valid from 10 minutes ago for 5 years' ($built.NotBefore -gt $now.AddMinutes(-11) -and $built.NotBefore -lt $now.AddMinutes(-9) -and
        [Math]::Abs(($built.NotAfter - $now.AddYears(5)).TotalMinutes) -lt 2) "$($built.NotBefore) $($built.NotAfter)"
    Check "self-signed certificate: with its key, named 'Pulsar panel'" ($built.HasPrivateKey -and $built.FriendlyName -ceq 'Pulsar panel')

    # What consumes it: the all-in-one panel trusts it for what it was made for; a node's -TrustCertificate for its
    # host and the loopback addresses besides; a node code pins it at an address it lists.
    $madeFor = @($listed.Dns) + @($listed.Ips | ForEach-Object { "$_" })
    Check "self-signed certificate: trusted on the panel's own machine for everything it lists" ($null -eq (Assert-Cert $built 'localhost' $madeFor)) "$(Assert-Cert $built 'localhost' $madeFor)"
    $homeCertificate = New-SelfSignedPanelCertificate (Get-PanelCertificateNames @('localhost', 'PANEL') @([Net.IPAddress]::Parse('192.168.0.54')) @())
    try {
        Check "self-signed certificate: a node's -TrustCertificate takes the home panel's by its LAN address" ($null -eq (Assert-Cert $homeCertificate '192.168.0.54')) "$(Assert-Cert $homeCertificate '192.168.0.54')"
        Check "self-signed certificate: by its name, its LAN address is another name (-TrustCertificateWithOtherNames)" ((Assert-Cert $homeCertificate 'PANEL') -match '192\.168\.0\.54.*TrustCertificateWithOtherNames') "$(Assert-Cert $homeCertificate 'PANEL')"
        Check "self-signed certificate: a node's -TrustCertificate refuses it for an address it does not list" ((Assert-Cert $homeCertificate '192.168.0.55') -match 'is not made for 192\.168\.0\.55')
        Check 'self-signed certificate: a node code pins it at its LAN address, not at another' ($null -eq (Get-Thrown { Assert-PinnableCertificate $homeCertificate '192.168.0.54' }) -and
            (Get-Thrown { Assert-PinnableCertificate $homeCertificate '192.168.0.55' }) -match 'is not made for')
    }
    finally { $homeCertificate.Dispose() }
    Check "self-signed certificate: a node's -TrustCertificate refuses one listing a public address but its host's" ((Assert-Cert $built 'PANEL') -match '203\.0\.113\.7.*TrustCertificateWithOtherNames' -and $null -eq (Assert-Cert $built '203.0.113.7' @('2001:db8::7', 'panel.example.com', 'PANEL.corp.example', '192.168.0.54', '10.0.0.7'))) "$(Assert-Cert $built 'PANEL')"
}
finally { $built.Dispose() }
$certificateFunctions = @($parsed.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -in 'New-PanelCertificate', 'Get-SelfSignedPanelNames', 'New-SelfSignedPanelBundle', 'Save-PanelBundle', 'New-SelfSignedPanelFiles', 'New-SelfSignedPanelCertificate' }, $true))
$storeUses = @($certificateFunctions | Where-Object { $_.Extent.Text -match '(?i)Cert:\\|New-SelfSignedCertificate|Export-PfxCertificate|Export-Certificate|X509Store|CngKey\]::Create|PersistKeySet|MachineKeySet' })
Check 'self-signed certificate: made without a certificate store or a stored key' ($certificateFunctions.Count -eq 6 -and $storeUses.Count -eq 0) (@($storeUses | ForEach-Object Name) -join ', ')
# -CertificatePath's PFX is opened with an ephemeral key (none written to a key container) and disposed.
$panelCertificateText = @($certificateFunctions | Where-Object Name -eq 'New-PanelCertificate')[0].Extent.Text
$pfxOpens = @([regex]::Matches($panelCertificateText, 'X509Certificate2\b[^\r\n]*') | ForEach-Object { $_.Value })
Check '-CertificatePath: its PFX is opened with EphemeralKeySet, and disposed' ($pfxOpens.Count -eq 1 -and $pfxOpens[0] -match 'EphemeralKeySet' -and
    ([regex]::Matches($panelCertificateText, '\$certificate\.Dispose\(\)')).Count -eq 1) ($pfxOpens -join ' | ')
# The install and -RenewCertificate make the certificate with the same functions (the renewal's own code only decides where it is written).
$renewalText = @($parsed.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-RenewCertificate' }, $true))[0].Extent.Text
Check 'renew: the new certificate is made by the install''s own functions (names, then files), exclusively' (
    $panelCertificateText -match 'Get-SelfSignedPanelNames \$HostName\s+\$password = New-Secret 32\s+Register-Secret \$password\s+return New-SelfSignedPanelFiles \$names \$pfx \$cer \$password \$false' -and
    $renewalText -match 'Get-SelfSignedPanelNames \$HostName' -and $renewalText -match 'New-SelfSignedPanelBundle \$names \$renewal\.Password' -and $renewalText -match 'Save-PanelBundle \$bundle \$staged\.Pfx \$staged\.Cer \$true \$hold\.Path')

# The install's own: files in the panel's private folder (here a scratch one), what the summary prints.
$tlsRoot = Join-Path $Scratch ("pulsar-script-tests-" + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory $tlsRoot
try {
    $madeOutput = @(& {
            $PanelData = $tlsRoot; $CertificatePath = $null; $CertificatePassword = $null; $HostName = @('panel.example.com', '203.0.113.7')
            function Get-CimInstance { [pscustomobject]@{ Domain = 'WORKGROUP' } }
            function Get-LanAddresses { [Net.IPAddress]::Parse('192.168.0.54') }
            function Get-DhcpAddresses { '192.168.0.54'; '172.17.80.1' }
            function Write-Step { }
            $WarningPreference = 'Continue'
            New-PanelCertificate
        } 3>&1)
    $made = @($madeOutput | Where-Object { $_ -is [hashtable] })[0]
    $madeWarnings = @($madeOutput | Where-Object { $_ -is [Management.Automation.WarningRecord] } | ForEach-Object { "$_" })
    Check 'install certificate: a DHCP address it lists gets one warning, to reserve it in the router' ($madeWarnings.Count -eq 1 -and $madeWarnings[0] -match '192\.168\.0\.54' -and
        $madeWarnings[0] -notmatch '172\.17\.80\.1' -and $madeWarnings[0] -match 'reservation') ($madeWarnings -join ' | ')
    $pfxBytes = [IO.File]::ReadAllBytes((Join-Path $tlsRoot 'tls\panel.pfx'))
    $opened = New-Object Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList @($pfxBytes, $made.Password, [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
    try {
        $cerBytes = [IO.File]::ReadAllBytes((Join-Path $tlsRoot 'tls\panel.cer'))
        Check 'install certificate: panel.pfx opens with its password, with its key; panel.cer is the same certificate' ($opened.HasPrivateKey -and $opened.Thumbprint -eq $made.Thumbprint -and
            [Convert]::ToBase64String($cerBytes) -ceq [Convert]::ToBase64String($opened.RawData) -and $made.Path -eq (Join-Path $tlsRoot 'tls\panel.pfx') -and $made.SelfSigned)
        Check 'install certificate: its SHA-256 is the hash of its DER' ($made.Sha256 -ceq (Get-CertificateHash $opened) -and $made.Sha256 -cmatch '^[0-9a-f]{64}\z') "$($made.Sha256)"
        Check 'install certificate: the names it is trusted for, the addresses other computers use' (($made.Names -join ',') -ceq "localhost,$env:COMPUTERNAME,panel.example.com,127.0.0.1,::1,192.168.0.54,203.0.113.7" -and
            ($made.Addresses -join ',') -ceq '192.168.0.54,203.0.113.7') "$($made.Names -join ',') | $($made.Addresses -join ',')"
        Check 'install certificate: its password is a secret of the run (never in the log)' ((Protect-SetupText "x $($made.Password) y") -ceq 'x [secret] y')
    }
    finally { $opened.Dispose() }

    # -CertificatePath: a PFX of yours (here the one just made), copied into the panel's folder.
    $ownRoot = Join-Path $tlsRoot 'own'
    $null = New-Item -ItemType Directory $ownRoot
    $own = & {
        $PanelData = $ownRoot; $CertificatePath = Join-Path $tlsRoot 'tls\panel.pfx'; $CertificatePassword = ConvertTo-Secure $made.Password
        New-PanelCertificate
    }
    Check '-CertificatePath: copied with its password, panel.cer and the SHA-256 of the same certificate' ($own.Thumbprint -eq $made.Thumbprint -and $own.Sha256 -ceq $made.Sha256 -and -not $own.SelfSigned -and
        [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $ownRoot 'tls\panel.cer'))) -ceq [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $tlsRoot 'tls\panel.cer')))) "$($own.Thumbprint)"

    $Unattended = $true
    $printed = @(& { Write-RemoteAccess $made 8443 } 6>&1 | ForEach-Object { "$_" })
    $Unattended = $false
    $plain = @(& { Write-RemoteAccess @{ SelfSigned = $false; Sha256 = $made.Sha256 } 8443 } 6>&1 | ForEach-Object { "$_" })
    Check 'summary: the addresses other computers use, and the fingerprint to compare' (($printed -join "`n") -match [regex]::Escape("https://192.168.0.54:8443 or https://203.0.113.7:8443 or https://$($env:COMPUTERNAME):8443") -and ($printed -join "`n") -match "fingerprint.*$(Format-Fingerprint $made.Sha256)" -and ($printed -join "`n") -match 'Account page') ($printed -join ' | ')
    Check 'summary: with -Unattended, the line "PULSAR-CERTIFICATE <sha256>" for the setup wizard' (@($printed | Where-Object { $_ -ceq "PULSAR-CERTIFICATE $($made.Sha256)" }).Count -eq 1)
    Check 'summary: without -Unattended no machine line, and no trust steps for a certificate of your own' (@($plain | Where-Object { $_ -like 'PULSAR-*' }).Count -eq 0 -and ($plain -join "`n") -notmatch 'to download' -and ($plain -join "`n") -match [regex]::Escape("https://$($env:COMPUTERNAME):8443")) ($plain -join ' | ')
    $ipv6 = @(& { Write-RemoteAccess @{ SelfSigned = $true; Sha256 = $made.Sha256; Addresses = @('2001:db8::7') } 8443 } 6>&1 | ForEach-Object { "$_" })
    Check 'summary: an IPv6 address in brackets' (($ipv6 -join "`n") -match [regex]::Escape('https://[2001:db8::7]:8443'))
}
finally {
    Remove-Item -LiteralPath $tlsRoot -Recurse -Force
    $SetupSecrets.Clear()
}
Check 'PULSAR-CERTIFICATE: the lowercase hexadecimal SHA-256, nothing else' ((Format-CertificateLine ('0f' * 32)) -ceq "PULSAR-CERTIFICATE $('0f' * 32)" -and
    $null -ne (Get-Thrown { Format-CertificateLine ('0F' * 32) }) -and $null -ne (Get-Thrown { Format-CertificateLine ('0f' * 31) }) -and $null -ne (Get-Thrown { Format-CertificateLine "$('0f' * 32)$lf" }))
Check 'fingerprint: as browsers show it, upper case with a colon between each two digits' ((Format-Fingerprint ('0123456789abcdef' * 4)) -ceq ((@('01', '23', '45', '67', '89', 'AB', 'CD', 'EF') * 4) -join ':'))
$installMain = Get-Content -Raw -LiteralPath $installScript
Check 'summary: printed for both a panel and an all-in-one install' (([regex]::Matches($installMain, 'Write-RemoteAccess \$certificate \$installed\.Port')).Count -eq 2)

# --- Removing the trust an install gave ------------------------------------------------------------------------------

$marked = '1111111111111111111111111111111111111111'
$unmarked = '2222222222222222222222222222222222222222'
$other = '3333333333333333333333333333333333333333'
$trusted = @(
    [pscustomobject]@{ Thumbprint = $marked; FriendlyName = $TrustedName; Subject = 'CN=localhost' },
    [pscustomobject]@{ Thumbprint = $unmarked; FriendlyName = ''; Subject = 'CN=localhost' },
    [pscustomobject]@{ Thumbprint = $other; FriendlyName = 'Some root'; Subject = 'CN=Some root' })
Check 'noted thumbprint: a wildcard, an empty note and text are ignored' ($null -eq (Get-NotedThumbprint '*') -and $null -eq (Get-NotedThumbprint '') -and $null -eq (Get-NotedThumbprint "$marked*"))
Check 'noted thumbprint: 40 hexadecimal characters, trimmed, in upper case' ((Get-NotedThumbprint "  $('ab' * 20)`r`n") -eq ('AB' * 20))
$selected = Select-TrustToRemove $trusted $null
Check "uninstall: removes what the installer marked, without a note" ((@($selected.Remove) -join ',') -eq $marked -and $null -eq $selected.Unmarked)
$selected = Select-TrustToRemove $trusted $other
Check "uninstall: a note naming another root removes nothing more, and shows it" ((@($selected.Remove) -join ',') -eq $marked -and $null -ne $selected.Unmarked -and $selected.Unmarked.Thumbprint -eq $other)
$selected = Select-TrustToRemove $trusted $unmarked.ToLowerInvariant()
Check "uninstall: a note naming an unmarked certificate (an install before the mark) removes nothing more, and shows it" ((@($selected.Remove) -join ',') -eq $marked -and $null -ne $selected.Unmarked -and $selected.Unmarked.Thumbprint -eq $unmarked)
$selected = Select-TrustToRemove $trusted '*'
Check 'uninstall: a note with a wildcard removes nothing more' ((@($selected.Remove) -join ',') -eq $marked -and $null -eq $selected.Unmarked)
$selected = Select-TrustToRemove $trusted $marked
Check 'uninstall: a note naming the marked certificate shows nothing' ((@($selected.Remove) -join ',') -eq $marked -and $null -eq $selected.Unmarked)

# --- Scheduled backup tasks --------------------------------------------------------------------------------------

$expected = 'C:\Program Files\Pulsar\Panel\Backup-Pulsar.ps1'
function Task([string] $name, [object[]] $actions) { [pscustomobject]@{ TaskPath = '\'; TaskName = $name; Actions = $actions } }
function Exec([string] $execute, [string] $arguments, [string] $folder = $null) { [pscustomobject]@{ Execute = $execute; Arguments = $arguments; WorkingDirectory = $folder } }
$tasks = @(
    (Task 'good' @(Exec 'powershell.exe' "-NoProfile -ExecutionPolicy Bypass -File `"$expected`" -Destination D:\PulsarBackups")),
    (Task 'good in other case' @(Exec 'powershell.exe' "-File `"$($expected.ToUpperInvariant())`"")),
    (Task 'relative in the panel folder' @(Exec 'powershell.exe' '-File .\Backup-Pulsar.ps1' 'C:\Program Files\Pulsar\Panel')),
    (Task 'unrelated' @(Exec 'cmd.exe' '/c echo hi')),
    (Task 'com handler' @([pscustomobject]@{ ClassId = '{00000000-0000-0000-0000-000000000000}'; Data = '' })),
    (Task 'old setup' @(Exec 'powershell.exe' '-File C:\PulsarSetup\Backup-Pulsar.ps1 -Destination D:\Backups')),
    (Task 'old quoted' @(Exec 'powershell.exe' '-File "C:\Pulsar Setup\Backup-Pulsar.ps1"')),
    (Task 'relative elsewhere' @(Exec 'powershell.exe' '-File Backup-Pulsar.ps1' 'C:\PulsarSetup')))
$stale = @(Find-StaleBackupTasks $tasks $expected)
Check 'backup tasks: those that run another copy than the panel folder''s' ($stale.Count -eq 3 -and $stale[0] -match 'old setup' -and $stale[1] -match 'old quoted' -and $stale[2] -match 'relative elsewhere') ($stale -join ' | ')

# --- SFTP port and firewall (read from the script, never run) ------------------------------------------------------

$sftpParameter = $parsed.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'SftpPort' }
$sftpRange = $sftpParameter.Attributes | Where-Object { $_.TypeName.Name -eq 'ValidateRange' }
Check '-SftpPort: an integer defaulting to 2022' ($null -ne $sftpParameter -and $null -ne ($sftpParameter.Attributes | Where-Object { $_.TypeName.Name -eq 'int' }) -and "$($sftpParameter.DefaultValue.Value)" -eq '2022') "$($sftpParameter.DefaultValue)"
Check '-SftpPort: validated 1 to 65535' ($null -ne $sftpRange -and "$($sftpRange.PositionalArguments[0].Value)" -eq '1' -and "$($sftpRange.PositionalArguments[1].Value)" -eq '65535')

$installText = Get-Content -Raw -LiteralPath $installScript
Check 'SFTP firewall: its own inbound rule, for the panel''s program and the chosen port, like the HTTPS one' ($installText -match "(?s)New-NetFirewallRule -Name 'Pulsar-Panel-SFTP' -DisplayName 'Pulsar Panel \(SFTP\)' -Direction Inbound\s+``\s*-Protocol TCP -LocalPort \`$SftpPort -Program \`$exe -Action Allow -Profile \`$profiles")
Check 'SFTP firewall: the rule is replaced at an install and removed at an uninstall' (([regex]::Matches($installText, "Get-NetFirewallRule -Name 'Pulsar-Panel-SFTP' -ErrorAction SilentlyContinue \| Remove-NetFirewallRule")).Count -eq 2)
Check '-SftpPort: must differ from -Port and -AgentsPort' ($installText -match "-SftpPort must be another port than -Port and -AgentsPort")
Check 'SFTP: a port other than 2022 is set through the panel''s settings endpoint after the administrator exists' ($installText -match "PUT '/api/v1/sftp/settings' @\{ enabled = \`$true; port = \`$SftpPort") 

# --- Backup destination ------------------------------------------------------------------------------------------

$root = Join-Path $Scratch ("pulsar-script-tests-" + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory $root
$private = Join-Path $root 'private'
$link = Join-Path $root 'link'
$me = [Security.Principal.WindowsIdentity]::GetCurrent()
$elevated = (New-Object Security.Principal.WindowsPrincipal $me).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
try {
    New-PrivateFolder $private
    $acl = Get-Acl -LiteralPath $private
    $rules = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
    $sids = @($rules | ForEach-Object { $_.IdentityReference.Value } | Select-Object -Unique | Sort-Object)
    Check 'private folder: made with SYSTEM and Administrators alone, nothing inherited' ($acl.AreAccessRulesProtected -and ($sids -join ',') -eq 'S-1-5-18,S-1-5-32-544' -and @($rules | Where-Object { $_.IsInherited }).Count -eq 0 -and @($rules | Where-Object { $_.FileSystemRights -ne 'FullControl' }).Count -eq 0) ($sids -join ',')
    Check 'private folder: one that already exists is refused' ((Get-Thrown { New-PrivateFolder $private }) -match 'already exists')
    if ($elevated) {
        Check 'private folder: as made, it is private' (Test-PrivateFolder $private)
    }
    else {
        # Not elevated, the folder's owner is this account, which could change its permissions.
        Check 'private folder: owned by an account other than SYSTEM and Administrators, it is not private' (-not (Test-PrivateFolder $private))
    }
    Check 'private folder: an ordinary folder is not private' (-not (Test-PrivateFolder $root))
    Check 'private folder: a missing folder is not private' (-not (Test-PrivateFolder (Join-Path $root 'missing')))

    $findings = @(Find-UntrustedDestination $root)
    Check 'destination: a folder this account can change is refused' ($findings.Count -gt 0 -and ($findings -join ' ') -match [regex]::Escape($root)) ($findings -join ' | ')
    $findings = @(Find-UntrustedDestination (Join-Path $root 'new'))
    Check 'destination: a new folder in a folder this account can rename is refused' ($findings.Count -gt 0) ($findings -join ' | ')
    $findings = @(Find-UntrustedDestination (Join-Path $root 'missing\new'))
    Check 'destination: a new folder in a missing one is refused' (($findings -join ' ') -match 'does not exist') ($findings -join ' | ')
    $findings = @(Find-UntrustedDestination ([Environment]::SystemDirectory))
    Check 'destination: a folder only SYSTEM, Administrators and TrustedInstaller can change passes' ($findings.Count -eq 0) ($findings -join ' | ')
    $findings = @(Find-UntrustedDestination (Join-Path $env:ProgramFiles ("pulsar-test-" + [Guid]::NewGuid().ToString('N'))))
    Check 'destination: a new folder in a folder only administrators can change passes' ($findings.Count -eq 0) ($findings -join ' | ')
    # A junction to a folder of the test's own (Windows PowerShell's Remove-Item -Recurse can go through a junction).
    $null = New-Item -ItemType Junction -Path $link -Value $private
    $findings = @(Find-UntrustedDestination $link)
    Check 'destination: a link is refused' (($findings -join ' ') -match 'is a link') ($findings -join ' | ')

    # The backup's pg_dump: refused where this account could change it (never run here: an empty file).
    $bin = Join-Path $root 'bin'
    $null = New-Item -ItemType Directory $bin
    Set-Content -LiteralPath (Join-Path $bin 'pg_dump.exe') -Value '' -Encoding ASCII
    Check 'backup: a pg_dump.exe other accounts could change is refused' ((Get-Thrown { Assert-TrustedPgDump $bin }) -match 'could be changed by other accounts')
}
finally {
    # The junction alone, never what it leads to.
    if (Test-Path -LiteralPath $link) { [IO.Directory]::Delete($link) }
    # The owner may always change the permissions: given back to this account, to remove the folder.
    if (Test-Path -LiteralPath $private) {
        $null = & (Join-Path ([Environment]::SystemDirectory) 'icacls.exe') $private /grant "*$($me.User.Value):(OI)(CI)F"
    }
    Remove-Item -LiteralPath $root -Recurse -Force
}

# --- Exit codes, progress lines and secrets in the log (Phase 18) --------------------------------------------------

function Get-Caught([scriptblock] $block) {
    try { $null = & $block; return $null } catch { return $_ }
}

$codes = @($ExitFailed, $ExitUsage, $ExitUnsupported, $ExitOffline, $ExitUntrusted, $ExitCertificate, $ExitRolledBack, $ExitRestart)
Check 'exit codes: 1 to 7 and 3010, each once' ((@($codes | Sort-Object) -join ',') -eq '1,2,3,4,5,6,7,3010') ($codes -join ',')
$caught = Get-Caught { throw (New-SetupError $ExitUntrusted 'refused') }
Check 'exit codes: an error made with one ends the run with it, its message kept' ((Get-SetupExitCode $caught) -eq 5 -and $caught.Exception.Message -eq 'refused')
Check 'exit codes: any other error ends it with 1' ((Get-SetupExitCode (Get-Caught { throw 'plain' })) -eq 1 -and (Get-SetupExitCode (Get-Caught { [int]::Parse('x') })) -eq 1)
$inner = New-SetupError $ExitCertificate 'inner'
Check 'exit codes: found in an inner exception' ((Get-SetupExitCode ([pscustomobject]@{ Exception = (New-Object Exception('outer', $inner)) })) -eq 6)
$guide = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\..\docs\guides\install.md')
foreach ($code in $codes) {
    Check "exit codes: $code is in install.md's table" ($guide -match ('\|\s*`' + $code + '`\s*\|'))
}
foreach ($name in @($SecretInputs.Values) + 'PULSAR_SECRETS_FILE', 'PULSAR-STEP', 'PULSAR-ERROR', 'PULSAR-LOG', 'PULSAR-PANEL-URL', 'PULSAR-CERTIFICATE', '-PostgresInstaller', '-DotNetInstallers', '-NodeCode', '-Unattended') {
    Check "install.md documents $name" ($guide.Contains($name))
}

Check 'progress: "PULSAR-STEP <n>/<total> <text>"' ((Format-SetupStep 'dotnet' 'Installing .NET 10' $StepPlans['AllInOne']) -ceq 'PULSAR-STEP 3/7 Installing .NET 10')
Check 'progress: the last step of an agent''s run' ((Format-SetupStep 'agent' 'Installing the agent' $StepPlans['Agent']) -ceq 'PULSAR-STEP 4/4 Installing the agent')
Check 'progress: control characters in the text are shown as ?' ((Format-SetupStep 'check' "a${cr}b$esc[2J" $StepPlans['Uninstall']) -ceq 'PULSAR-STEP 1/2 a?b?[2J')
Check 'progress: a step the run does not have is an error' ($null -ne (Get-Thrown { Format-SetupStep 'postgres' 'x' $StepPlans['Agent'] }))
# Every step a run prints is one of its plan's, and every step of every plan is printed somewhere.
$stepKeys = @($parsed.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Write-SetupStep' }, $true) |
    ForEach-Object { $_.CommandElements[1].Value } | Select-Object -Unique)
$planKeys = @($StepPlans.Values | ForEach-Object { $_ } | Select-Object -Unique)
Check 'progress: the steps printed are the plans''' (@($stepKeys | Where-Object { $_ -notin $planKeys }).Count -eq 0 -and @($planKeys | Where-Object { $_ -notin $stepKeys }).Count -eq 0) "printed: $($stepKeys -join ','); planned: $($planKeys -join ',')"

$SetupSecrets.Clear()
Register-Secret 'hunter2-is-a-long-secret'
Register-Secret 'abc'
Register-Secret 'part-of'
Register-Secret 'part-of-a-longer-one'
Check 'log: registered secrets are replaced, the longest first; very short ones are not registered' ((Protect-SetupText "x hunter2-is-a-long-secret y abc part-of-a-longer-one part-of") -ceq 'x [secret] y abc [secret] [secret]') (Protect-SetupText "x hunter2-is-a-long-secret y abc part-of-a-longer-one part-of")
$SetupSecrets.Clear()

# Nothing is asked with -Unattended: a missing secret is an input error that says where it should come from.
$Unattended = $true
$caught = Get-Caught { Read-Secret "Password for the panel's first administrator, 'admin' (at least 12 characters)" 'adminPassword' }
$Unattended = $false
Check 'unattended: a missing secret is an input error naming its variable and its name in the secrets file' ((Get-SetupExitCode $caught) -eq 2 -and $caught.Exception.Message -match 'PULSAR_ADMIN_PASSWORD' -and $caught.Exception.Message -match '"adminPassword"' -and $caught.Exception.Message -notmatch '\(at least') "$($caught.Exception.Message)"
$prompts = @($parsed.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Read-Host' }, $true))
$promptFunctions = @($prompts | ForEach-Object { $node = $_; while ($node -and $node -isnot [Management.Automation.Language.FunctionDefinitionAst]) { $node = $node.Parent }; if ($node) { $node.Name } else { '(top level)' } } | Select-Object -Unique)
Check 'unattended: Read-Host is called only in Read-Secret, which refuses to with -Unattended' (($promptFunctions -join ',') -eq 'Read-Secret') ($promptFunctions -join ',')

# --- No secret on a command line (static) ----------------------------------------------------------------------------

# The commands that start programs, with the arguments that may hold a secret because they never reach a command line:
# Invoke-NativeWithInput's standard input, Invoke-Psql's password (its environment) and SQL (standard input).
$runners = @{ 'Invoke-Native' = @(); 'Invoke-NativeWithInput' = @(1); 'Start-Installer' = @(); 'Start-Process' = @(); 'Invoke-Psql' = @(2, 4) }
$secretName = '(?i)password|secret|token|nodecode|connection|inputs'
function Find-SecretsOnCommandLines([Management.Automation.Language.Ast] $ast) {
    foreach ($command in $ast.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] }, $true)) {
        $name = $command.GetCommandName()
        $isRunner = $name -and $runners.ContainsKey($name)
        if (-not $isRunner -and $command.InvocationOperator -ne 'Ampersand') { continue }
        $skip = @()
        if ($isRunner) { $skip = $runners[$name] }
        for ($i = 1; $i -lt $command.CommandElements.Count; $i++) {
            if ($i -in $skip) { continue }
            foreach ($variable in $command.CommandElements[$i].FindAll({ param($node) $node -is [Management.Automation.Language.VariableExpressionAst] }, $true)) {
                if ($variable.VariablePath.UserPath -match $secretName) { "line $($variable.Extent.StartLineNumber): $($command.Extent.Text)" }
            }
        }
    }
    # Lists of arguments built beforehand ($arguments, a ProcessStartInfo's Arguments) hold none either.
    foreach ($assignment in $ast.FindAll({ param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] }, $true)) {
        if ($assignment.Left.Extent.Text -notmatch '(?i)arguments') { continue }
        foreach ($variable in $assignment.Right.FindAll({ param($node) $node -is [Management.Automation.Language.VariableExpressionAst] }, $true)) {
            if ($variable.VariablePath.UserPath -match $secretName) { "line $($variable.Extent.StartLineNumber): $($assignment.Extent.Text)" }
        }
    }
}
$sample = [Management.Automation.Language.Parser]::ParseInput('Invoke-Native $psql --password $password; & $exe $Token; $arguments = @("--key", $secret); Invoke-NativeWithInput $password $psql -f -', [ref] $null, [ref] $null)
Check 'command lines: the scan finds secrets passed as arguments, not on standard input' (@(Find-SecretsOnCommandLines $sample).Count -eq 3) (@(Find-SecretsOnCommandLines $sample) -join ' | ')
$found = @(Find-SecretsOnCommandLines $parsed)
Check 'command lines: Install-Pulsar.ps1 passes no secret as an argument to any program it starts' ($found.Count -eq 0) ($found -join ' | ')
$secureParameters = @($parsed.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -match 'Password' })
Check 'command lines: every password parameter is a SecureString (never plain text on a command line)' ($secureParameters.Count -eq 3 -and @($secureParameters | Where-Object { $_.StaticType -ne [SecureString] }).Count -eq 0) (($secureParameters | ForEach-Object { "$($_.Name) $($_.StaticType)" }) -join ', ')
$edbCall = @($parsed.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Start-Installer' -and $node.Extent.Text -match "'--mode'" }, $true))
Check 'command lines: PostgreSQL''s installer gets its superuser password from an options file, not its arguments' ($edbCall.Count -eq 1 -and $edbCall[0].Extent.Text -match "'--optionfile', \`$options" -and $edbCall[0].Extent.Text -notmatch '(?i)superpassword' -and $installText -match 'Write-PrivateFile \$options "superpassword=') "$($edbCall.Count)"
Check "command lines: PostgreSQL's installer runs unattended, without a UI, pgAdmin or StackBuilder" ($edbCall.Count -eq 1 -and $edbCall[0].Extent.Text -match "'--mode', 'unattended', '--unattendedmodeui', 'none'" -and $edbCall[0].Extent.Text -match "'--disable-components', 'pgAdmin,stackbuilder'")

# Arguments come back as they were given (CommandLineToArgvW, as the programs read them).
if (-not ('PulsarTestArgv' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PulsarTestArgv
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
    public static string[] Split(string commandLine)
    {
        int count;
        IntPtr argv = CommandLineToArgvW(commandLine, out count);
        try
        {
            string[] result = new string[count];
            for (int i = 0; i < count; i++) { result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)); }
            return result;
        }
        finally { LocalFree(argv); }
    }
}
'@
}
$arguments = @('--prefix', 'C:\Program Files\PostgreSQL\18', '--optionfile', 'C:\ProgramData\Pulsar\Setup\d\postgresql-options.txt', '', 'a"b', 'end\', 'C:\with space\', 'x\\"y')
$back = @([PulsarTestArgv]::Split('installer.exe ' + (ConvertTo-CommandLine $arguments)) | Select-Object -Skip 1)
Check 'command lines: quoted so that programs read every argument back as given' (($back -join '|') -ceq ($arguments -join '|')) ($back -join '|')

# The downloader and the certificate reader compile (here, without connecting anywhere): the script compiles them only
# when it first needs them.
$caught = Get-Caught { Initialize-SetupNetwork }
Check 'downloads: the HTTPS downloader and the TLS certificate reader compile' ($null -eq $caught -and ('PulsarDownloads' -as [type]) -and ('PulsarTlsProbe' -as [type])) "$caught"
Check 'downloads: TLS 1.2 at least, nothing older' (([int][Net.ServicePointManager]::SecurityProtocol -band 3072) -ne 0 -and ([int][Net.ServicePointManager]::SecurityProtocol -band (48 -bor 192 -bor 768)) -eq 0) "$([Net.ServicePointManager]::SecurityProtocol)"

# --- Strict JSON (the node code, the secrets file) ---------------------------------------------------------------

$values = ConvertFrom-FlatJson " {`"a`" : `"b`", `"c`":null,`"e`":`"\u00e9\`"\\\/\n`"} `r`n" 'Test'
Check 'JSON: names and text values, null, escapes' ($values.Count -eq 3 -and $values['a'] -ceq 'b' -and $null -eq $values['c'] -and $values['e'] -ceq "$([char]0xE9)`"\/`n")
Check 'JSON: names are case-sensitive' ($values.ContainsKey('a') -and -not $values.ContainsKey('A'))
Check 'JSON: an empty object' ((ConvertFrom-FlatJson '{}' 'Test').Count -eq 0)
foreach ($bad in @('', '[]', '{"a":1}', '{"a":true}', '{"a":{"b":"c"}}', '{"a":["b"]}', '{"a":"b",}', '{"a":"b","a":"c"}', '{"a":"b"} x',
        "{`"a`":`"b$([char]1)`"}", '{"a":"\ud800"}', '{"a":"\x41"}', "{'a':'b'}", '{"a":NULL}', '{"a":"b"', '{a:"b"}', "$([char]0xFEFF){`"a`":`"b`"}")) {
    $caught = Get-Caught { ConvertFrom-FlatJson $bad 'Test' }
    Check "JSON: refuses $(ConvertTo-PrintableText $bad)" ($null -ne $caught -and (Get-SetupExitCode $caught) -eq 2) "$caught"
}

# --- Node codes (as src/Pulsar.Contracts/NodeCode.cs reads them) ------------------------------------------------------

function ConvertTo-Base64Url([byte[]] $bytes) { [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') }
function New-Random32 { $bytes = New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes); ConvertTo-Base64Url $bytes }
function New-Code([string] $json) { $NodeCodePrefix + (ConvertTo-Base64Url ([Text.Encoding]::UTF8.GetBytes($json))) }
$token = New-Random32
$key = 'sha256:' + (New-Random32)
$hash = '0123456789abcdef' * 4
function Code-Json([string] $url = 'https://panel.example.com:8443', $tokenValue = """$token""", $keyValue = """$key""", $certValue = """$hash""") {
    "{`"url`":`"$url`",`"token`":$tokenValue,`"key`":$keyValue,`"cert`":$certValue}"
}
$code = ConvertFrom-NodeCode (New-Code (Code-Json))
Check 'node code: read' ($code.Url -ceq 'https://panel.example.com:8443' -and $code.Token -ceq $token -and $code.PanelKey -ceq $key -and $code.CertificateHash -ceq $hash)
$code = ConvertFrom-NodeCode ("  " + (New-Code "{ `"cert`" : null, `"key`":`"$key`", `"token`":`"pulsar_enroll_$token`", `"url`":`"https://\u0070anel.lan`" }") + "`r`n")
Check 'node code: members in any order, spaces around it and in its JSON, a reusable token, no certificate, escapes' ($code.Url -ceq 'https://panel.lan' -and $code.Token -ceq "pulsar_enroll_$token" -and $null -eq $code.CertificateHash)
foreach ($url in 'http://localhost:5000', 'http://127.0.0.1:5000', 'http://[::1]:8080', 'https://[fd00::5]:8443', 'https://10.0.0.5') {
    Check "node code: the address $url" ((ConvertFrom-NodeCode (New-Code (Code-Json $url))).Url -ceq $url)
}
# Spaces after the object (JSON allows them) until the data leaves unused bits in the last character; then that
# character with one of them set: the same bytes, another spelling.
$json = Code-Json
while ([Text.Encoding]::UTF8.GetByteCount($json) % 3 -eq 0) { $json += ' ' }
$valid = New-Code $json
Check 'node code: spaces after its JSON object' ((ConvertFrom-NodeCode $valid).Token -ceq $token)
$payload = $valid.Substring($NodeCodePrefix.Length)
$alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_'
$twin = $payload.Substring(0, $payload.Length - 1) + $alphabet[$alphabet.IndexOf($payload[-1]) + 1]
$shortPadding = if ($payload.Length % 4 -ne 0) { '=' * (4 - $payload.Length % 4) } else { '====' }
$badCodes = [ordered]@{
    'another version' = 'pulsar-node-2.' + $payload
    'another case' = 'Pulsar-node-1.' + $payload
    'no prefix' = $payload
    'padding' = $valid + $shortPadding
    'a base64 character' = $NodeCodePrefix + $payload.Substring(1) + '+'
    'a space inside' = $NodeCodePrefix + $payload.Substring(0, 10) + ' ' + $payload.Substring(10)
    'a length no base64 has' = $NodeCodePrefix + $payload + 'A' * (5 - ($payload.Length % 4))
    'a non-canonical spelling' = $NodeCodePrefix + $twin
    'too long' = $NodeCodePrefix + ('A' * 2048)
    'nothing after the prefix' = $NodeCodePrefix
    'not UTF-8' = $NodeCodePrefix + (ConvertTo-Base64Url ([byte[]](0x7B, 0xFF, 0x7D)))
    'a list' = New-Code '[]'
    'a nested object' = New-Code (Code-Json -certValue '{"a":"b"}')
    'a number' = New-Code (Code-Json -certValue '1')
    'true' = New-Code (Code-Json -certValue 'true')
    'a member twice' = New-Code ((Code-Json).TrimEnd('}') + ",`"url`":`"https://other.example.com`"}")
    'another member' = New-Code ((Code-Json).TrimEnd('}') + ',"extra":"x"}')
    'a member missing' = New-Code "{`"url`":`"https://panel.example.com`",`"token`":`"$token`",`"key`":`"$key`"}"
    'a null url' = New-Code "{`"url`":null,`"token`":`"$token`",`"key`":`"$key`",`"cert`":null}"
    'a path' = New-Code (Code-Json 'https://panel.example.com/pulsar')
    'a trailing slash' = New-Code (Code-Json 'https://panel.example.com/')
    'upper case' = New-Code (Code-Json 'https://Panel.example.com')
    'the default port' = New-Code (Code-Json 'https://panel.example.com:443')
    'a user name' = New-Code (Code-Json 'https://admin@panel.example.com')
    'a query' = New-Code (Code-Json 'https://panel.example.com?x=1')
    'a fragment' = New-Code (Code-Json 'https://panel.example.com#x')
    'plain http to another machine' = New-Code (Code-Json 'http://panel.example.com:8080')
    'plain http to "loopback"' = New-Code (Code-Json 'http://loopback:8080')
    'another scheme' = New-Code (Code-Json 'ftp://panel.example.com')
    'an address too long' = New-Code (Code-Json ('https://' + ('a' * 506) + '.lan'))
    'a short token' = New-Code (Code-Json -tokenValue """$($token.Substring(1))""")
    'a token of another prefix' = New-Code (Code-Json -tokenValue """pulsar_other_$token""")
    'a non-canonical token' = New-Code (Code-Json -tokenValue """$($token.Substring(0, 42) + $alphabet[$alphabet.IndexOf($token[-1]) + 1])""")
    'a token with a dot' = New-Code (Code-Json -tokenValue """$($token.Substring(0, 42)).""")
    'a key without its prefix' = New-Code (Code-Json -keyValue """$($key.Substring(7))""")
    'a key too long' = New-Code (Code-Json -keyValue """$($key)A""")
    'an upper-case hash' = New-Code (Code-Json -certValue """$($hash.ToUpperInvariant())""")
    'a short hash' = New-Code (Code-Json -certValue """$($hash.Substring(1))""")
}
foreach ($case in $badCodes.GetEnumerator()) {
    $caught = Get-Caught { ConvertFrom-NodeCode $case.Value }
    Check "node code: refuses $($case.Key)" ($null -ne $caught -and (Get-SetupExitCode $caught) -eq 2 -and $caught.Exception.Message -match 'not a node code') "$caught"
}
$caught = Get-Caught { ConvertFrom-NodeCode $badCodes['a short token'] }
Check 'node code: the message never shows the code or its token' ($null -ne $caught -and $caught.Exception.Message -notmatch [regex]::Escape($token.Substring(1, 20)))

# What the setup does with the certificate the panel presents.
$panelCertificate = New-TestCertificate @((Dns 'panel.example.com'), (Dns 'localhost'), (Dns 'PANEL'))
$panelHash = Get-CertificateHash $panelCertificate
$sha = [Security.Cryptography.SHA256]::Create()
Check 'node code: the certificate''s hash is the lowercase hexadecimal SHA-256 of its DER' ($panelHash -ceq ([BitConverter]::ToString($sha.ComputeHash($panelCertificate.RawData)) -replace '-', '').ToLowerInvariant())
$sha.Dispose()
function Presented($certificate, [bool] $trusted) { [pscustomobject]@{ Certificate = $certificate; Trusted = $trusted; Errors = $(if ($trusted) { 'None' } else { 'RemoteCertificateChainErrors' }) } }
function Pin($value) { [pscustomobject]@{ Url = 'https://panel.example.com:8443'; Token = $token; PanelKey = $key; CertificateHash = $value } }
Check 'node code: the certificate the code names, not trusted, is pinned in the agent' ((Resolve-NodeCertificate (Pin $panelHash) (Presented $panelCertificate $false) 'panel.example.com') -eq 'pin')
Check 'node code: the certificate the code names, trusted already, is checked as any other (no pin)' ((Resolve-NodeCertificate (Pin $panelHash) (Presented $panelCertificate $true) 'panel.example.com') -eq 'trusted')
# A code made to pass off other sites (localhost, machine names, another domain): only the agent pins it, for the panel.
$luring = New-TestCertificate @((Dns 'evil.example'), (Dns 'localhost'), (Dns 'intranet'), (Dns 'wpad'), (Dns 'dc01'), (Dns '*.example.com'))
Check 'node code: a certificate that names other sites is only ever pinned, never trusted' ((Resolve-NodeCertificate (Pin (Get-CertificateHash $luring)) (Presented $luring $false) 'evil.example') -eq 'pin')
foreach ($case in ([ordered]@{
        'without Basic Constraints' = @((New-TestCertificate @((Dns 'panel.example.com')) $null), 'Basic Constraints')
        'for more than a web server' = @((New-TestCertificate @((Dns 'panel.example.com')) $false @($ServerAuth, '1.3.6.1.5.5.7.3.3')), 'Extended Key Usage')
        'without Extended Key Usage' = @((New-TestCertificate @((Dns 'panel.example.com')) $false $null), 'Extended Key Usage')
    }).GetEnumerator()) {
    $caught = Get-Caught { Resolve-NodeCertificate (Pin (Get-CertificateHash $case.Value[0])) (Presented $case.Value[0] $false) 'panel.example.com' }
    Check "node code: a certificate $($case.Key) is not pinned (no switch allows it)" ((Get-SetupExitCode $caught) -eq 6 -and $caught.Exception.Message -match $case.Value[1]) "$caught"
}
$TrustCertificateWithoutBasicConstraints = $true
$caught = Get-Caught { Resolve-NodeCertificate (Pin (Get-CertificateHash (New-TestCertificate @((Dns 'panel.example.com')) $null))) (Presented (New-TestCertificate @((Dns 'panel.example.com')) $null) $false) 'panel.example.com' }
$TrustCertificateWithoutBasicConstraints = $false
Check 'node code: -TrustCertificate''s switches do not apply to a pin' ($null -ne $caught) "$caught"
Check "node code: the agent is enrolled with the pin, and without one when there is none" (
    ((Get-EnrollArguments 'https://panel.example.com:8443' $key $panelHash 'D:\Games') -join ' ') -ceq "enroll --panel https://panel.example.com:8443 --panel-key $key --panel-certificate $panelHash --servers-root D:\Games" -and
    ((Get-EnrollArguments 'https://panel.example.com:8443' $key '' '') -join ' ') -ceq "enroll --panel https://panel.example.com:8443 --panel-key $key")
Check 'node code: no pin and a certificate this machine trusts' ((Resolve-NodeCertificate (Pin $null) (Presented $panelCertificate $true) 'panel.example.com') -eq 'trusted')
$caught = Get-Caught { Resolve-NodeCertificate (Pin $null) (Presented $panelCertificate $false) 'panel.example.com' }
Check 'node code: no pin and a certificate this machine does not trust is refused' ((Get-SetupExitCode $caught) -eq 6) "$caught"
$caught = Get-Caught { Resolve-NodeCertificate (Pin $hash) (Presented $panelCertificate $true) 'panel.example.com' }
Check 'node code: another certificate than the pinned one is refused, even one this machine trusts' ((Get-SetupExitCode $caught) -eq 6 -and $caught.Exception.Message -match $panelHash -and $caught.Exception.Message -match $hash) "$caught"
$authorityCertificate = New-TestCertificate @((Dns 'panel.example.com')) $true
$caught = Get-Caught { Resolve-NodeCertificate (Pin (Get-CertificateHash $authorityCertificate)) (Presented $authorityCertificate $false) 'panel.example.com' }
Check 'node code: a certificate authority is not pinned' ((Get-SetupExitCode $caught) -eq 6 -and $caught.Exception.Message -match 'CA=true') "$caught"
$caught = Get-Caught { Resolve-NodeCertificate (Pin $panelHash) (Presented $panelCertificate $false) 'other.example.com' }
Check 'node code: a certificate not made for the address used is not pinned' ((Get-SetupExitCode $caught) -eq 6 -and $caught.Exception.Message -match 'is not made for') "$caught"
$caught = Get-Caught { Resolve-NodeCertificate (Pin $hash) (Presented $panelCertificate $false) 'panel.example.com' }
Check 'node code: another certificate than the code''s says a proxy may be serving it' ($caught.Exception.Message -match 'proxy') "$caught"

# A node code never changes a certificate store: the only writes to one are Add-TrustedCertificate's, reached from
# -TrustCertificate and the all-in-one panel's own certificate alone.
$installTokens = $null
$installErrors = $null
$installAst = [Management.Automation.Language.Parser]::ParseFile((Resolve-Path $installScript).ProviderPath, [ref] $installTokens, [ref] $installErrors)
$storeWriters = @($installAst.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] -or $node -is [Management.Automation.Language.TypeExpressionAst] -or
            $node -is [Management.Automation.Language.StringConstantExpressionAst] -or $node -is [Management.Automation.Language.ExpandableStringExpressionAst] }, $true) |
        Where-Object { $_.Extent.Text -match 'X509Store|Import-Certificate|Import-PfxCertificate|certutil|Cert:\\LocalMachine\\(Root|CA|TrustedPeople|AuthRoot|TrustedPublisher)' } |
        ForEach-Object { $function = $_; while ($function -and $function -isnot [Management.Automation.Language.FunctionDefinitionAst]) { $function = $function.Parent }; if ($function) { $function.Name } else { '(script)' } } | Select-Object -Unique)
Check 'node code: only Add-TrustedCertificate, Remove-TrustedCertificates and the two the renewal uses (Get-InstallerTrust, Remove-InstallerTrust) touch the trusted roots' (
    @($storeWriters | Where-Object { $_ -notin 'Add-TrustedCertificate', 'Remove-TrustedCertificates', 'Get-InstallerTrust', 'Remove-InstallerTrust' }).Count -eq 0) ($storeWriters -join ', ')
$trustCalls = @($installAst.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -like 'Add-TrustedCertificate*' }, $true))
$trustConditions = @(foreach ($trustCall in $trustCalls) {
        $clause = $trustCall.Parent
        while ($clause -and $clause -isnot [Management.Automation.Language.IfStatementAst]) { $clause = $clause.Parent }
        if (-not $clause) { '(none)'; continue }
        @($clause.Clauses | Where-Object { $_.Item2.Extent.StartOffset -le $trustCall.Extent.StartOffset -and $_.Item2.Extent.EndOffset -ge $trustCall.Extent.EndOffset } | ForEach-Object { $_.Item1.Extent.Text }) -join ''
    })
Check 'node code: the certificate stores are written for -TrustCertificate, the all-in-one panel''s own certificate and the renewal of one this script trusted only' (
    $trustCalls.Count -eq 3 -and @($trustConditions | Where-Object { $_ -notin '$TrustCertificate', '$certificate.SelfSigned', '$renewal.Trust -ceq ''marked''' }).Count -eq 0 -and
    @($trustCalls | Where-Object { $_.GetCommandName() -cne 'Add-TrustedCertificate' }).Count -eq 0) ($trustConditions -join ' | ')
$codeFunctions = @('ConvertFrom-NodeCode', 'Read-PanelCertificate', 'Resolve-NodeCertificate', 'Assert-PinnableCertificate', 'Resolve-AgentInputs', 'Install-Agent', 'Get-EnrollArguments')
$codeTexts = @($installAst.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -in $codeFunctions }, $true) | ForEach-Object { $_.Extent.Text })
Check 'node code: what reads and uses a code never reaches a certificate store' ($codeTexts.Count -eq $codeFunctions.Count -and @($codeTexts | Where-Object { $_ -match 'Add-Trusted|X509Store|Cert:\\|Import-Certificate|certutil' }).Count -eq 0)

# --- PostgreSQL's versions and EDB's installers ------------------------------------------------------------------

# As https://www.postgresql.org/versions.json has them.
$versionsJson = '[{"current": true, "eolDate": "2030-11-14", "firstRelDate": "2025-09-25", "latestMinor": "6", "major": "18", "relDate": "2026-08-13", "supported": true}, {"current": false, "eolDate": "2029-11-08", "firstRelDate": "2024-09-26", "latestMinor": "11", "major": "17", "relDate": "2026-08-13", "supported": true}, {"current": false, "eolDate": "2025-11-13", "firstRelDate": "2020-09-24", "latestMinor": "23", "major": "13", "relDate": "2025-11-13", "supported": false}, {"current": false, "eolDate": "2021-11-11", "firstRelDate": "2016-09-29", "latestMinor": "24", "major": "9.6", "relDate": "2021-11-11", "supported": false}]'
$versions = ConvertFrom-JsonDocument $versionsJson
Check 'PostgreSQL: the newest release of 18' ((Select-PostgresVersion $versions 18) -ceq '18.6')
Check 'PostgreSQL: of another major version, never a newer one' ((Select-PostgresVersion $versions 17) -ceq '17.11' -and (Select-PostgresVersion $versions 13) -ceq '13.23')
foreach ($case in ([ordered]@{
        'a major version it does not list' = @($versionsJson, 19)
        'a minor that is not a number' = @($versionsJson.Replace('"latestMinor": "6"', '"latestMinor": "6; x"'), 18)
        'a minor that is a number, not text' = @($versionsJson.Replace('"latestMinor": "6"', '"latestMinor": 6'), 18)
        'a major listed twice' = @($versionsJson.Replace('"major": "17"', '"major": "18"'), 18)
        'an object, not a list' = @('{"major": "18", "latestMinor": "6"}', 18)
    }).GetEnumerator()) {
    $caught = Get-Caught { Select-PostgresVersion (ConvertFrom-JsonDocument $case.Value[0]) $case.Value[1] }
    Check "PostgreSQL: refuses $($case.Key)" ((Get-SetupExitCode $caught) -eq 5) "$caught"
}
Check 'PostgreSQL: not JSON is refused as untrusted' ((Get-SetupExitCode (Get-Caught { ConvertFrom-JsonDocument '<html>' })) -eq 5)
Check "PostgreSQL: EDB's installer address for a release and build" ((Get-PostgresInstallerUrl '18.6' 5) -ceq 'https://get.enterprisedb.com/postgresql/postgresql-18.6-5-windows-x64.exe')
$named = Test-PostgresInstallerName 'postgresql-18.6-5-windows-x64.exe'
Check "PostgreSQL: the version in EDB's installer name" ($named.Major -eq 18 -and $named.Minor -eq 6 -and $named.Build -eq 5)
foreach ($bad in 'postgresql-18.6-windows-x64.exe', 'PostgreSQL-18.6-1-windows-x64.exe', 'postgresql-18.6-1-windows-x64.exe.lnk', 'postgresql-18.6-1-windows.exe', 'x-postgresql-18.6-1-windows-x64.exe') {
    Check "PostgreSQL: not an installer name: $bad" ($null -eq (Test-PostgresInstallerName $bad))
}
$rows = @('host|127.0.0.1|255.255.255.255|', 'host|::1|ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff|', 'local|||', 'hostssl|127.0.0.0|255.0.0.0|', '')
Check 'PostgreSQL: pg_hba.conf rules for this machine only pass' (@(Find-NonLocalHbaRules $rows).Count -eq 0) (@(Find-NonLocalHbaRules $rows) -join ' | ')
foreach ($bad in 'host|0.0.0.0|0.0.0.0|', 'host|192.168.1.0|255.255.255.0|', 'hostnossl|::|::|', 'host|samehost||', 'host|127.0.0.1|0.0.0.0|', 'host|::1|ffff:ffff:ffff:ffff::|', 'host|::ffff:127.0.0.1|ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff|', 'host|127.0.0.1|255.255.255.255|invalid', 'garbage') {
    Check "PostgreSQL: a pg_hba.conf rule that lets in more than this machine: $bad" (@(Find-NonLocalHbaRules @($bad)).Count -eq 1)
}
# EDB's newest build of a release: what its server answers for each build (Get-DownloadStatus, mocked), 403 and 404
# meaning there is none; anything else is a server that could not answer, not a missing build.
function Find-Build([object[]] $answers) {
    $script:probed = @()
    $result = & {
        function Get-DownloadStatus([string] $url) {
            $script:probed += $url
            $answer = $answers[$script:probed.Count - 1]
            if ($answer -is [string]) { throw $answer }
            return $answer
        }
        Get-Caught { $script:found = Find-PostgresInstallerUrl '18.6' }
    }
    if ($null -eq $result) { return $script:found }
    return $result
}
Check "PostgreSQL: EDB's newest build, looking until three in a row are missing (404)" ((Find-Build @(200, 200, 404, 404, 404)) -ceq (Get-PostgresInstallerUrl '18.6' 2) -and $script:probed.Count -eq 5)
Check "PostgreSQL: a build EDB withholds (403) is missing too, and a later one is found" ((Find-Build @(200, 403, 200, 404, 403, 404)) -ceq (Get-PostgresInstallerUrl '18.6' 3))
foreach ($case in ([ordered]@{ 'too many requests (429)' = @(200, 429); 'a server error (503)' = @(503); 'a redirect without an address (302)' = @(200, 200, 302); 'no answer' = @(200, 'the connection was reset') }).GetEnumerator()) {
    $caught = Find-Build $case.Value
    Check "PostgreSQL: EDB's server answering with $($case.Key) is an offline error, not a missing build" ($caught -isnot [string] -and (Get-SetupExitCode $caught) -eq 4 -and $script:probed.Count -eq $case.Value.Count) "$caught"
}
$caught = Find-Build @(404, 404, 404)
Check "PostgreSQL: no build of the release at all says so" ($caught -isnot [string] -and (Get-SetupExitCode $caught) -eq 4 -and $caught.Exception.Message -match 'offers no Windows installer') "$caught"

# --- .NET's release metadata and runtimes ------------------------------------------------------------------------

$runtimeHash = '44da78e619ae23e352e81fbc3a5803d78b1375dff89c176dd17a884d4e42e17850bb7adb78c799ca15b2127b67529395df1143976e33f6c8157f53bfc5a42e62'
$aspHash = '680bf274a7ecc1818d49e1845072aa3064102992fb00547965107938ff6808ea35f832c46f1b4e05231cae47e40e746419c024e5622f7a1254c0dcb946c5c07c'
$oldHash = 'ab' * 64
# As https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json has it (shortened), newest first.
function Release-Json([string] $runtimeUrl = 'https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.13/dotnet-runtime-10.0.13-win-x64.exe', [string] $runtimeFileHash = $runtimeHash, [string] $channel = '10.0', [string] $extraFile = '') {
    @"
{"channel-version": "$channel", "latest-release": "10.0.13", "latest-runtime": "10.0.13", "releases": [
 {"release-version": "10.0.13", "security": true,
  "runtime": {"version": "10.0.13", "version-display": "10.0.13", "files": [
   {"name": "dotnet-apphost-pack-win-x64.zip", "rid": "win-x64", "url": "https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.13/dotnet-apphost-pack-10.0.13-win-x64.zip", "hash": "$oldHash"},
   {"name": "dotnet-runtime-win-x64.exe", "rid": "win-x64", "url": "$runtimeUrl", "hash": "$runtimeFileHash"},
   {"name": "dotnet-runtime-win-arm64.exe", "rid": "win-arm64", "url": "https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.13/dotnet-runtime-10.0.13-win-arm64.exe", "hash": "$oldHash"}$extraFile]},
  "aspnetcore-runtime": {"version": "10.0.13", "files": [
   {"name": "aspnetcore-runtime-win-x64.exe", "rid": "win-x64", "url": "https://builds.dotnet.microsoft.com/dotnet/aspnetcore/Runtime/10.0.13/aspnetcore-runtime-10.0.13-win-x64.exe", "hash": "$aspHash"}]}},
 {"release-version": "10.0.12", "security": true,
  "runtime": {"version": "10.0.12", "files": [
   {"name": "dotnet-runtime-win-x64.exe", "rid": "win-x64", "url": "https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.12/dotnet-runtime-10.0.12-win-x64.exe", "hash": "$oldHash"}]},
  "aspnetcore-runtime": {"version": "10.0.12", "files": []}},
 {"release-version": "10.0.0-rc.2", "runtime": {"version": "10.0.0-rc.2.25502.107", "files": [
   {"name": "dotnet-runtime-win-x64.exe", "rid": "win-x64", "url": "https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.0-rc.2.25502.107/dotnet-runtime-10.0.0-rc.2.25502.107-win-x64.exe", "hash": "$oldHash"}]}}
]}
"@
}
$entries = @(Get-DotNetInstallerEntries (ConvertFrom-JsonDocument (Release-Json)) 'runtime' 'dotnet-runtime')
$newest = $entries | Sort-Object Version -Descending | Select-Object -First 1
Check '.NET: the runtime installers of each stable release, previews left out' ((@($entries | ForEach-Object Text) -join ',') -eq '10.0.13,10.0.12') (@($entries | ForEach-Object Text) -join ',')
Check '.NET: the newest, with its address and SHA-512' ($newest.Text -eq '10.0.13' -and $newest.Url -ceq 'https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.13/dotnet-runtime-10.0.13-win-x64.exe' -and $newest.Hash -ceq $runtimeHash)
$asp = @(Get-DotNetInstallerEntries (ConvertFrom-JsonDocument (Release-Json)) 'aspnetcore-runtime' 'aspnetcore-runtime')
Check '.NET: ASP.NET Core''s installer (a release without one is skipped)' ($asp.Count -eq 1 -and $asp[0].Text -eq '10.0.13' -and $asp[0].Hash -ceq $aspHash)
Check '.NET: an older release''s SHA-512 is there for an installer given offline' ((@($entries | Where-Object { $_.Version -eq [version]'10.0.12' })[0].Hash) -ceq $oldHash)
foreach ($case in ([ordered]@{
        'an http address' = Release-Json 'http://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.13/dotnet-runtime-10.0.13-win-x64.exe'
        'another host' = Release-Json 'https://downloads.example.com/dotnet/Runtime/10.0.13/dotnet-runtime-10.0.13-win-x64.exe'
        'a host that only ends like Microsoft''s' = Release-Json 'https://evilbuilds.dotnet.microsoft.com.example.com/dotnet-runtime-10.0.13-win-x64.exe'
        'another port' = Release-Json 'https://builds.dotnet.microsoft.com:8443/dotnet/Runtime/10.0.13/dotnet-runtime-10.0.13-win-x64.exe'
        'another version''s file' = Release-Json 'https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.12/dotnet-runtime-10.0.12-win-x64.exe'
        'a query' = Release-Json 'https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.13/dotnet-runtime-10.0.13-win-x64.exe?x=1'
        'a short hash' = Release-Json -runtimeFileHash $runtimeHash.Substring(1)
        'a hash that is not hexadecimal' = Release-Json -runtimeFileHash ('zz' + $runtimeHash.Substring(2))
        'another channel' = Release-Json -channel '9.0'
        'the installer twice' = Release-Json -extraFile ",{`"name`": `"dotnet-runtime-win-x64.exe`", `"rid`": `"win-x64`", `"url`": `"https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.13/dotnet-runtime-10.0.13-win-x64.exe`", `"hash`": `"$oldHash`"}"
    }).GetEnumerator()) {
    $caught = Get-Caught { Get-DotNetInstallerEntries (ConvertFrom-JsonDocument $case.Value) 'runtime' 'dotnet-runtime' }
    Check ".NET: metadata with $($case.Key) is refused" ((Get-SetupExitCode $caught) -eq 5) "$caught"
}
$caught = Get-Caught { Get-DotNetInstallerEntries (ConvertFrom-JsonDocument '{"channel-version": "10.0", "releases": {}}') 'runtime' 'dotnet-runtime' }
Check '.NET: metadata without a list of releases is refused' ((Get-SetupExitCode $caught) -eq 5)

$frameworkDependentPanel = '{"runtimeOptions": {"tfm": "net10.0", "frameworks": [{"name": "Microsoft.NETCore.App", "version": "10.0.11"}, {"name": "Microsoft.AspNetCore.App", "version": "10.0.11"}], "configProperties": {}}}'
$frameworkDependentAgent = '{"runtimeOptions": {"tfm": "net10.0", "framework": {"name": "Microsoft.NETCore.App", "version": "10.0.12"}}}'
$selfContained = '{"runtimeOptions": {"tfm": "net10.0", "includedFrameworks": [{"name": "Microsoft.NETCore.App", "version": "10.0.13"}]}}'
$releaseJson = '{"version": "1.0.0", "runtimes": {"Microsoft.NETCore.App": "10.0.13", "Microsoft.AspNetCore.App": "10.0.12"}}'
$needed = Read-RuntimeRequirements @($frameworkDependentPanel, $frameworkDependentAgent) $null
Check '.NET: what framework-dependent programs need, the highest of each' ($needed.Count -eq 2 -and $needed['Microsoft.NETCore.App'] -eq [version]'10.0.12' -and $needed['Microsoft.AspNetCore.App'] -eq [version]'10.0.11')
$needed = Read-RuntimeRequirements @($frameworkDependentPanel, $frameworkDependentAgent) $releaseJson
Check '.NET: at least the patches the release was built with (release.json)' ($needed['Microsoft.NETCore.App'] -eq [version]'10.0.13' -and $needed['Microsoft.AspNetCore.App'] -eq [version]'10.0.12')
$needed = Read-RuntimeRequirements @($frameworkDependentAgent) $releaseJson
Check '.NET: an agent alone needs no ASP.NET Core' ($needed.Count -eq 1 -and $needed['Microsoft.NETCore.App'] -eq [version]'10.0.13')
Check '.NET: self-contained programs need nothing' ((Read-RuntimeRequirements @($selfContained) $releaseJson).Count -eq 0)
foreach ($case in ([ordered]@{
        'another framework' = '{"runtimeOptions": {"framework": {"name": "Microsoft.WindowsDesktop.App", "version": "10.0.13"}}}'
        'another channel' = '{"runtimeOptions": {"framework": {"name": "Microsoft.NETCore.App", "version": "9.0.4"}}}'
        'a preview' = '{"runtimeOptions": {"framework": {"name": "Microsoft.NETCore.App", "version": "10.0.0-rc.2"}}}'
    }).GetEnumerator()) {
    Check ".NET: a program that needs $($case.Key) is refused" ((Get-SetupExitCode (Get-Caught { Read-RuntimeRequirements @($case.Value) $null })) -eq 3)
}
$needed = @{ 'Microsoft.NETCore.App' = [version]'10.0.13'; 'Microsoft.AspNetCore.App' = [version]'10.0.12' }
Check '.NET: what is missing, in the order it is installed' ((@(Get-MissingRuntimes $needed @{}) -join ',') -eq 'Microsoft.NETCore.App,Microsoft.AspNetCore.App' -and
    (@(Get-MissingRuntimes $needed @{ 'Microsoft.NETCore.App' = [version]'10.0.14'; 'Microsoft.AspNetCore.App' = [version]'10.0.5' }) -join ',') -eq 'Microsoft.AspNetCore.App' -and
    @(Get-MissingRuntimes $needed @{ 'Microsoft.NETCore.App' = [version]'10.0.13'; 'Microsoft.AspNetCore.App' = [version]'10.0.12' }).Count -eq 0)
Check '.NET: download addresses are Microsoft''s' ((Test-DotNetDownloadUrl 'https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.13/dotnet-runtime-10.0.13-win-x64.exe' 'dotnet-runtime-10.0.13-win-x64.exe') -and
    (Test-DotNetDownloadUrl 'https://download.visualstudio.microsoft.com/download/pr/x/y/dotnet-runtime-10.0.13-win-x64.exe' 'dotnet-runtime-10.0.13-win-x64.exe') -and
    -not (Test-DotNetDownloadUrl 'https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.13/dotnet-runtime-10.0.13-win-x64.exe.zip' 'dotnet-runtime-10.0.13-win-x64.exe') -and
    -not (Test-DotNetDownloadUrl 'https://user@builds.dotnet.microsoft.com/dotnet-runtime-10.0.13-win-x64.exe' 'dotnet-runtime-10.0.13-win-x64.exe') -and
    -not (Test-DotNetDownloadUrl 42 'dotnet-runtime-10.0.13-win-x64.exe'))

# --- Signatures --------------------------------------------------------------------------------------------------

# The issuers of the signing certificates below: DigiCert's for EDB's (by name only), another for the others.
function New-IssuerCertificate([string] $subject) {
    $rsa = [Security.Cryptography.RSACng]::new(2048)
    try {
        $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new($subject, $rsa, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true, $false, 0, $true))
        return $request.CreateSelfSigned([DateTimeOffset]::Now.AddDays(-2), [DateTimeOffset]::Now.AddDays(2))
    }
    finally { $rsa.Dispose() }
}
$digiCert = New-IssuerCertificate 'CN=DigiCert Trusted G4 Code Signing RSA4096 SHA384 2021 CA1, O="DigiCert, Inc.", C=US'
$otherIssuer = New-IssuerCertificate 'CN=Some Code Signing CA, O="Some CA, Inc.", C=US'
function New-SigningCertificate([string] $subject, $uses = @($CodeSigning), $issuer = $digiCert) {
    $rsa = [Security.Cryptography.RSACng]::new(2048)
    try {
        $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new($subject, $rsa, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        if ($null -ne $uses) {
            $oids = New-Object Security.Cryptography.OidCollection
            foreach ($use in $uses) { $null = $oids.Add([Security.Cryptography.Oid]::new($use)) }
            $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($oids, $false))
        }
        if ($null -eq $issuer) { $made = $request.CreateSelfSigned([DateTimeOffset]::Now.AddDays(-1), [DateTimeOffset]::Now.AddDays(1)) }
        else { $made = $request.Create($issuer, [DateTimeOffset]::Now.AddDays(-1), [DateTimeOffset]::Now.AddDays(1), [byte[]](1..12)) }
        return [Security.Cryptography.X509Certificates.X509Certificate2]::new($made.RawData)
    }
    finally { $rsa.Dispose() }
}
# What Get-AuthenticodeSignature gives, for a file nothing here needs to read.
function Signature([string] $status, $certificate, [string] $type = 'Authenticode') {
    [pscustomobject]@{ Status = $status; StatusMessage = "status $status"; SignerCertificate = $certificate; SignatureType = $type }
}
# What Get-SignerChain gives: the thumbprints from the signer to its root, the problems besides time and revocation, and
# what the revocation check found.
function Chain([string] $root, [string[]] $problems = @(), [string] $revocation = 'Good', [string] $issuer = ('22' * 20)) {
    [pscustomobject]@{ Thumbprints = @(('11' * 20), $issuer, $root); Problems = $problems; Revocation = $revocation }
}
$microsoftRoot = '8F43288AD272F3103B6FB1428485EA3014C0BCFE'
# EDB's chain, as Windows builds it for EDB's signed PostgreSQL 18 programs: DigiCert's code-signing authority, then
# DigiCert Trusted Root G4.
$edbIssuer = '7B0F360B775F76C94A12CA48445AA2D2A875701C'
$edbRoot = 'DDFB16CD4931C973A2037D3FC83A4D7D775D05E4'
function EdbChain([string] $revocation = 'Good') { Chain $edbRoot @() $revocation $edbIssuer }
# As EDB's and Microsoft's installers are signed (read from EDB's PostgreSQL 18 programs and Microsoft's .NET 10 installers).
$edb = New-SigningCertificate 'CN=EnterpriseDB Corporation, O=EnterpriseDB Corporation, L=Wilmington, S=Delaware, C=US'
$microsoft = New-SigningCertificate 'CN=.NET, O=Microsoft Corporation, L=Redmond, S=Washington, C=US' -issuer $otherIssuer
function Assert-Signature($signature, $signer = $PostgresSigner, $chain = $null) { Get-Caught { Assert-InstallerSignature $signature $signer 'The installer' $chain } }
Check "signature: EDB's installer, valid, by EnterpriseDB, from DigiCert's authority, up to DigiCert Trusted Root G4" ($null -eq (Assert-Signature (Signature 'Valid' $edb) $PostgresSigner (EdbChain))) "$(Assert-Signature (Signature 'Valid' $edb) $PostgresSigner (EdbChain))"
Check "signature: EDB's signer is pinned to DigiCert Trusted Root G4 and DigiCert's code-signing authority by thumbprint" ((@($PostgresSigner.Roots) -join ',') -ceq $edbRoot -and (@($PostgresSigner.IssuerThumbprints) -join ',') -ceq $edbIssuer)
foreach ($case in ([ordered]@{
        'without a chain' = $null
        'whose chain ends at another root' = Chain ('AB' * 20) @() 'Good' $edbIssuer
        'issued by another authority of the same name' = Chain $edbRoot @() 'Good' ('CD' * 20)
        'whose chain has a problem' = Chain $edbRoot @('NotSignatureValid') 'Good' $edbIssuer
    }).GetEnumerator()) {
    $caught = Assert-Signature (Signature 'Valid' $edb) $PostgresSigner $case.Value
    Check "signature: refuses EDB's installer $($case.Key)" ((Get-SetupExitCode $caught) -eq 5) "$caught"
}
Check "signature: Microsoft's installer, valid, by Microsoft, up to Microsoft's root (2011 or 2010)" ($null -eq (Assert-Signature (Signature 'Valid' $microsoft) $DotNetSigner (Chain $microsoftRoot)) -and
    $null -eq (Assert-Signature (Signature 'Valid' $microsoft) $DotNetSigner (Chain '3B1EFD3A66EA28B16697394703A72CA340A05BD5'))) "$(Assert-Signature (Signature 'Valid' $microsoft) $DotNetSigner (Chain $microsoftRoot))"
Check 'signature: without the SignatureType of newer PowerShell versions, the rest is checked' ($null -eq (Assert-Signature ([pscustomobject]@{ Status = 'Valid'; SignerCertificate = $edb }) $PostgresSigner (EdbChain)))
# Revocation (Get-AuthenticodeSignature does not check it in Windows PowerShell): a revoked signer is refused, one whose
# revocation could not be checked (offline) is let through with a warning, for both publishers.
foreach ($case in ([ordered]@{ "EDB's" = @($edb, $PostgresSigner, 'EdbChain'); "Microsoft's" = @($microsoft, $DotNetSigner, 'Chain') }).GetEnumerator()) {
    $certificate, $signer, $maker = $case.Value
    $revoked = if ($maker -eq 'EdbChain') { EdbChain 'Revoked' } else { Chain $microsoftRoot @() 'Revoked' }
    $unknown = if ($maker -eq 'EdbChain') { EdbChain 'Unknown' } else { Chain $microsoftRoot @() 'Unknown' }
    $caught = Assert-Signature (Signature 'Valid' $certificate) $signer $revoked
    Check "signature: refuses $($case.Key) installer whose signer's certificate (or an authority's above it) is revoked" ((Get-SetupExitCode $caught) -eq 5 -and $caught.Exception.Message -match 'revoked') "$caught"
    $warnings = @(& { $WarningPreference = 'Continue'; Assert-InstallerSignature (Signature 'Valid' $certificate) $signer 'The installer' $unknown } 3>&1 | Where-Object { $_ -is [Management.Automation.WarningRecord] })
    Check "signature: $($case.Key) installer whose revocation could not be checked runs, with a warning" ($warnings.Count -eq 1 -and $warnings[0].Message -match 'revoked') (@($warnings | ForEach-Object Message) -join ' | ')
}
# The chain is built for real (no network: these certificates name no revocation address), with revocation checked
# online, the root excluded.
$issuerFile = Join-Path ([IO.Path]::GetTempPath()) ("pulsar-test-issuer-" + [Guid]::NewGuid().ToString('N') + '.cer')
[IO.File]::WriteAllBytes($issuerFile, $digiCert.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
try { $built = Get-SignerChain $issuerFile $edb }
finally { Remove-Item -LiteralPath $issuerFile -Force }
Check 'signer chain: built from the certificates the signature carries, revocation unknown when it cannot be checked, never a problem of its own' (
    @($built.Thumbprints).Count -eq 2 -and @($built.Thumbprints)[0] -ceq $edb.Thumbprint -and @($built.Thumbprints)[1] -ceq $digiCert.Thumbprint -and
    $built.Revocation -ceq 'Unknown' -and @($built.Problems | Where-Object { $_ -match 'Revocation|Revoked' }).Count -eq 0) "$(@($built.Thumbprints) -join ',') $($built.Revocation) $(@($built.Problems) -join ',')"
$chainText = $installParts['Get-SignerChain']
Check 'signer chain: revocation is checked online, for every certificate but the root' ($chainText -match 'RevocationMode = \[Security\.Cryptography\.X509Certificates\.X509RevocationMode\]::Online' -and
    $chainText -match 'RevocationFlag = \[Security\.Cryptography\.X509Certificates\.X509RevocationFlag\]::ExcludeRoot' -and $chainText -notmatch 'NoCheck')
$installTextForChains = Get-Content -Raw -LiteralPath $installScript
Check "signer chain: EDB's installer is checked with its chain, as Microsoft's" ($installTextForChains -match '(?s)\$chain = Get-SignerChain \$file \$signature\.SignerCertificate[^\r\n]*\r?\n\s*Assert-InstallerSignature \$signature \$PostgresSigner \$what \$chain')
foreach ($case in ([ordered]@{
        'not signed' = Signature 'NotSigned' $null
        'changed after it was signed' = Signature 'HashMismatch' $edb
        'signed by a certificate Windows does not trust' = Signature 'NotTrusted' $edb
        'with a chain that cannot be built' = Signature 'UnknownError' $edb
        'signed through a catalog' = Signature 'Valid' $edb 'Catalog'
        'valid without a signer' = Signature 'Valid' $null
        'signed by Microsoft, for EDB''s' = Signature 'Valid' $microsoft
        'signed by an organization whose name starts alike' = Signature 'Valid' (New-SigningCertificate 'CN=EnterpriseDB Corporation, O=EnterpriseDB Corporation Ltd, L=Wilmington, S=Delaware, C=US')
        'signed by the name in another case' = Signature 'Valid' (New-SigningCertificate 'CN=EnterpriseDB Corporation, O=enterprisedb corporation, L=Wilmington, S=Delaware, C=US')
        'signed with the name as common name only' = Signature 'Valid' (New-SigningCertificate 'CN=EnterpriseDB Corporation')
        'signed by two organizations' = Signature 'Valid' (New-SigningCertificate 'CN=x, O=EnterpriseDB Corporation, O=Someone Else, L=Wilmington, S=Delaware, C=US')
        'signed by the organization elsewhere (another state)' = Signature 'Valid' (New-SigningCertificate 'CN=EnterpriseDB Corporation, O=EnterpriseDB Corporation, L=Bedford, S=Massachusetts, C=US')
        'signed by the organization without its locality' = Signature 'Valid' (New-SigningCertificate 'CN=EnterpriseDB Corporation, O=EnterpriseDB Corporation, S=Delaware, C=US')
        'signed by EnterpriseDB with a certificate of another authority' = Signature 'Valid' (New-SigningCertificate 'CN=EnterpriseDB Corporation, O=EnterpriseDB Corporation, L=Wilmington, S=Delaware, C=US' -issuer $otherIssuer)
        'signed by EnterpriseDB with a certificate of its own' = Signature 'Valid' (New-SigningCertificate 'CN=EnterpriseDB Corporation, O=EnterpriseDB Corporation, L=Wilmington, S=Delaware, C=US' -issuer $null)
        'signed with a certificate for web servers' = Signature 'Valid' (New-SigningCertificate 'CN=EnterpriseDB Corporation, O=EnterpriseDB Corporation, L=Wilmington, S=Delaware, C=US' @($ServerAuth))
        'signed with a certificate without Extended Key Usage' = Signature 'Valid' (New-SigningCertificate 'CN=EnterpriseDB Corporation, O=EnterpriseDB Corporation, L=Wilmington, S=Delaware, C=US' $null)
    }).GetEnumerator()) {
    $caught = Assert-Signature $case.Value
    Check "signature: refuses an installer $($case.Key)" ($null -ne $caught -and (Get-SetupExitCode $caught) -eq 5 -and $caught.Exception.Message -match 'Nothing was changed') "$caught"
}
foreach ($case in ([ordered]@{
        'without a chain' = $null
        'whose chain ends at another root' = Chain ('AB' * 20)
        'whose chain to Microsoft''s root has a problem' = Chain $microsoftRoot @('NotSignatureValid')
        'whose chain is the signer alone' = [pscustomobject]@{ Thumbprints = @($microsoftRoot); Problems = @() }
    }).GetEnumerator()) {
    $caught = Assert-Signature (Signature 'Valid' $microsoft) $DotNetSigner $case.Value
    Check "signature: refuses Microsoft's installer $($case.Key)" ((Get-SetupExitCode $caught) -eq 5 -and $caught.Exception.Message -match 'root') "$caught"
}
$caught = Assert-Signature (Signature 'Valid' (New-SigningCertificate 'CN=.NET, O=Microsoft Corporation, L=Redmond, S=Washington, C=GB' -issuer $otherIssuer)) $DotNetSigner (Chain $microsoftRoot)
Check "signature: refuses Microsoft's installer signed by Microsoft elsewhere (another country)" ((Get-SetupExitCode $caught) -eq 5) "$caught"
Check 'signature: a name''s attributes, read from the encoding (a comma inside one)' ((@(Get-NameAttributes (New-SigningCertificate 'CN=x, O="Pulsar, Inc.", C=US').SubjectName | ForEach-Object { "$($_.Type)=$($_.Value)" }) -join '|') -ceq 'C=US|O=Pulsar, Inc.|CN=x')

# The version an installer says it is, against the one it was chosen as.
function VersionInfo([string] $name, [string] $product, [string] $file = $product) { [pscustomobject]@{ ProductName = $name; ProductVersion = $product; FileVersion = $file } }
Check "version: .NET's installer of the version chosen (as Microsoft's say it)" ($null -eq (Get-Caught { Assert-InstallerVersion (VersionInfo 'Microsoft .NET Runtime - 10.0.12 (x64)' '10.0.12.36422') 'Microsoft .NET Runtime - 10.0.12 (x64)' '10.0.12' 'The installer' }))
Check "version: EDB's installer of the release chosen" ($null -eq (Get-Caught { Assert-InstallerVersion (VersionInfo 'PostgreSQL 18' '18.6-1') 'PostgreSQL' '18.6' 'The installer' -contains }))
foreach ($case in ([ordered]@{
        'another release' = @((VersionInfo 'Microsoft .NET Runtime - 10.0.1 (x64)' '10.0.1.1234'), 'Microsoft .NET Runtime - 10.0.12 (x64)', '10.0.12', $false)
        'a release that only starts alike' = @((VersionInfo 'PostgreSQL 18' '18.60-1'), 'PostgreSQL', '18.6', $true)
        'an older release of PostgreSQL' = @((VersionInfo 'PostgreSQL 18' '18.5-1'), 'PostgreSQL', '18.6', $true)
        'another product of that version' = @((VersionInfo 'Microsoft ASP.NET Core Runtime - 10.0.12 (x64)' '10.0.12.36422'), 'Microsoft .NET Runtime - 10.0.12 (x64)', '10.0.12', $false)
        'another product than PostgreSQL' = @((VersionInfo 'StackBuilder' '18.6'), 'PostgreSQL', '18.6', $true)
        'no version information' = @((VersionInfo '' ''), 'PostgreSQL', '18.6', $true)
    }).GetEnumerator()) {
    $value = $case.Value
    $caught = Get-Caught { Assert-InstallerVersion $value[0] $value[1] $value[2] 'The installer' -contains:$value[3] }
    Check "version: refuses an installer of $($case.Key)" ((Get-SetupExitCode $caught) -eq 5) "$caught"
}

# The oldest PostgreSQL a release installs.
Check 'PostgreSQL: the floor release.json records' ((Read-PostgresMinimum '{"version": "1.0.0", "runtimes": {}, "postgresMinimum": "18.6"}') -ceq '18.6' -and $null -eq (Read-PostgresMinimum '{"version": "1.0.0", "runtimes": {}}') -and $null -eq (Read-PostgresMinimum $null))
Check 'PostgreSQL: a floor that is not a release is refused' ((Get-SetupExitCode (Get-Caught { Read-PostgresMinimum '{"postgresMinimum": "18.6; x"}' })) -eq 3)
Check 'PostgreSQL: the floor and newer releases pass, and other major versions' ($null -eq (Get-Caught { Assert-PostgresMinimum '18.6' '18.6' 'x'; Assert-PostgresMinimum '18.10' '18.6' 'x'; Assert-PostgresMinimum '17.2' '18.6' 'x'; Assert-PostgresMinimum '18.0' $null 'x' }))
$caught = Get-Caught { Assert-PostgresMinimum '18.5' '18.6' 'The installer' }
Check 'PostgreSQL: an older release than the floor is refused' ((Get-SetupExitCode $caught) -eq 5 -and $caught.Exception.Message -match '18\.6') "$caught"
# SecureStrings without ConvertTo-SecureString -AsPlainText, whose parameters module logging records.
$secure = ConvertTo-Secure 'a secret of the test'
Check 'secrets: SecureStrings are built from the text, read-only' ((ConvertFrom-Secure $secure) -ceq 'a secret of the test' -and $secure.IsReadOnly() -and (ConvertFrom-Secure (ConvertTo-Secure '')) -ceq '')
$installText = Get-Content -Raw -LiteralPath $installScript
$plainSecure = @(foreach ($path in $installScript, $backupScript) {
        $parsed = [Management.Automation.Language.Parser]::ParseFile((Resolve-Path $path).ProviderPath, [ref] $null, [ref] $null)
        $parsed.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'ConvertTo-SecureString' }, $true) | ForEach-Object { $_.Extent.Text }
    })
Check 'secrets: no ConvertTo-SecureString in the install and backup scripts' ($plainSecure.Count -eq 0) ($plainSecure -join ' | ')

# -Update never goes back to an older release.
Check 'update: a release as new as what is installed, or newer, updates it' ($null -eq (Get-Caught { Assert-NotOlderThanInstalled '1.2.0' @('1.2.0.0', '1.1.9.0'); Assert-NotOlderThanInstalled '1.3.0-beta' @('1.2.0.0'); Assert-NotOlderThanInstalled '1.0.0' @(); Assert-NotOlderThanInstalled '' @('9.0.0.0') }))
$caught = Get-Caught { Assert-NotOlderThanInstalled '1.1.0' @('1.0.0.0', '1.2.0.0') }
Check 'update: a release older than a program installed here is refused, nothing changed' ((Get-SetupExitCode $caught) -eq 2 -and $caught.Exception.Message -match '1\.2\.0') "$caught"
Check 'update: -Update compares release.json''s version with the installed programs'' before anything else' ($installText -match 'if \(\$Update\) \{\s+\$releaseText = Get-ReleaseText\s+if \(\$releaseText\) \{ Assert-NotOlderThanInstalled')

Check 'downloads: a download''s time is capped by its size, 10 minutes at least' ([PulsarDownloads]::TimeLimit(0) -eq [TimeSpan]::FromMinutes(10) -and
    [PulsarDownloads]::TimeLimit(1GB) -eq ([TimeSpan]::FromMinutes(10) + [TimeSpan]::FromSeconds(8192)) -and [PulsarDownloads]::TimeLimit(-5) -eq [TimeSpan]::FromMinutes(10))

# --- Files of secrets, runtimes on disk, programs replaced ---------------------------------------------------------

function New-FileSecurity([string] $owner, [string[]] $allowed, [string[]] $denied = @()) {
    $security = New-Object Security.AccessControl.FileSecurity
    if ($owner) { $security.SetOwner((New-Object Security.Principal.SecurityIdentifier $owner)) }
    foreach ($sid in $allowed) { $security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier $sid), 'Read', 'Allow'))) }
    foreach ($sid in $denied) { $security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier $sid), 'Read', 'Deny'))) }
    return $security
}
Check 'secret files: SYSTEM and Administrators alone, owned by Administrators' (@(Get-PrivateFileProblems (New-FileSecurity 'S-1-5-32-544' @('S-1-5-18', 'S-1-5-32-544'))).Count -eq 0)
Check 'secret files: a denial for others changes nothing' (@(Get-PrivateFileProblems (New-FileSecurity 'S-1-5-18' @('S-1-5-18') @('S-1-1-0'))).Count -eq 0)
Check 'secret files: Users may read it' (@(Get-PrivateFileProblems (New-FileSecurity 'S-1-5-32-544' @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-32-545'))).Count -eq 1)
Check 'secret files: owned by another account' (@(Get-PrivateFileProblems (New-FileSecurity 'S-1-5-32-545' @('S-1-5-18'))).Count -eq 1)
Check 'secret files: owned by nobody' (@(Get-PrivateFileProblems (New-FileSecurity $null @('S-1-5-18'))).Count -eq 1)

$root = Join-Path $Scratch ("pulsar-script-tests-" + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory $root
try {
    # A secrets file anyone else may open (this account's, in an ordinary folder) is refused, not read and not deleted.
    $secrets = Join-Path $root 'secrets.json'
    Set-Content -LiteralPath $secrets -Value '{"adminPassword": "a password of the test"}' -Encoding ASCII
    $caught = Get-Caught { Read-SecretsFile $secrets }
    Check 'secrets file: one other accounts may open or replace is refused, and left' ((Get-SetupExitCode $caught) -eq 2 -and $caught.Exception.Message -match 'It was not read' -and (Test-Path -LiteralPath $secrets)) "$caught"
    $caught = Get-Caught { Read-SecretsFile (Join-Path $root 'missing.json') }
    Check 'secrets file: a missing one is an input error' ((Get-SetupExitCode $caught) -eq 2)
    if ($elevated) {
        # Made where only SYSTEM and Administrators may go, as a wizard would.
        $private = Join-Path $root 'private'
        New-PrivateFolder $private
        $secrets = Join-Path $private 'secrets.json'
        [IO.File]::WriteAllText($secrets, '{"adminPassword": "a password of the test", "nodeCode": null}')
        $read = Read-SecretsFile $secrets
        Check 'secrets file: a private one is read and deleted' ($read['adminPassword'] -ceq 'a password of the test' -and $read.ContainsKey('nodeCode') -and -not (Test-Path -LiteralPath $secrets))
        [IO.File]::WriteAllText($secrets, '{"adminPasword": "typo"}')
        Check 'secrets file: a name it does not know is refused' ((Get-SetupExitCode (Get-Caught { Read-SecretsFile $secrets })) -eq 2)
    }

    # .NET's folders as its installers leave them: the newest 10.0 patch of each framework, with a host resolver.
    $dotnet = Join-Path $root 'dotnet'
    foreach ($folder in 'host\fxr\10.0.13', 'shared\Microsoft.NETCore.App\10.0.13', 'shared\Microsoft.NETCore.App\10.0.2', 'shared\Microsoft.NETCore.App\9.0.9',
        'shared\Microsoft.NETCore.App\10.0.0-rc.2.25502.107', 'shared\Microsoft.NETCore.App\10.0.99', 'shared\Microsoft.AspNetCore.App\10.0.12') {
        $null = New-Item -ItemType Directory -Force (Join-Path $dotnet $folder)
    }
    $null = New-Item -ItemType File (Join-Path $dotnet 'host\fxr\10.0.13\hostfxr.dll')
    foreach ($version in '10.0.13', '10.0.2', '9.0.9', '10.0.0-rc.2.25502.107') { $null = New-Item -ItemType File (Join-Path $dotnet "shared\Microsoft.NETCore.App\$version\Microsoft.NETCore.App.deps.json") }
    $null = New-Item -ItemType File (Join-Path $dotnet 'shared\Microsoft.AspNetCore.App\10.0.12\Microsoft.AspNetCore.App.deps.json')
    $installed = Get-InstalledRuntimes $dotnet
    Check '.NET: the newest complete 10.0 runtime of each framework (an empty folder, previews and 9.0 do not count)' ($installed['Microsoft.NETCore.App'] -eq [version]'10.0.13' -and $installed['Microsoft.AspNetCore.App'] -eq [version]'10.0.12') "$($installed['Microsoft.NETCore.App']) $($installed['Microsoft.AspNetCore.App'])"
    Remove-Item -LiteralPath (Join-Path $dotnet 'host\fxr\10.0.13\hostfxr.dll')
    Check '.NET: none without a host resolver' ((Get-InstalledRuntimes $dotnet).Count -eq 0)
    Check '.NET: none in a missing folder' ((Get-InstalledRuntimes (Join-Path $root 'missing')).Count -eq 0)

    # An update replaces the whole program: a self-contained release's runtime files go, the settings file and the
    # panel's downloads folder stay.
    $source = Join-Path $root 'release\panel'
    $target = Join-Path $root 'installed\panel'
    $null = New-Item -ItemType Directory -Force $source, (Join-Path $target 'runtimes'), (Join-Path $target 'downloads')
    Set-Content (Join-Path $source 'Pulsar.Api.dll') 'new'
    Set-Content (Join-Path $source 'Pulsar.Api.runtimeconfig.json') '{}'
    Set-Content (Join-Path $target 'Pulsar.Api.dll') 'old'
    Set-Content (Join-Path $target 'hostfxr.dll') 'old runtime'
    Set-Content (Join-Path $target 'runtimes\coreclr.dll') 'old runtime'
    Set-Content (Join-Path $target 'appsettings.Production.json') 'settings'
    Set-Content (Join-Path $target 'downloads\PulsarAgentSetup.exe') 'setup'
    Copy-Program $source $target
    $left = @(Get-ChildItem -LiteralPath $target -Recurse -Force | ForEach-Object { $_.FullName.Substring($target.Length + 1) } | Sort-Object)
    Check 'update: the program folder is replaced, keeping only the settings file and the downloads folder' (($left -join ',') -eq 'appsettings.Production.json,downloads,downloads\PulsarAgentSetup.exe,Pulsar.Api.dll,Pulsar.Api.runtimeconfig.json' -and (Get-Content (Join-Path $target 'Pulsar.Api.dll')) -eq 'new' -and (Get-Content (Join-Path $target 'appsettings.Production.json')) -eq 'settings') ($left -join ',')
    Check 'program swap: nothing is left beside the program folder once it is replaced (no .new, no .old)' (
        (@(Get-ChildItem -LiteralPath (Split-Path -Parent $target) -Force | ForEach-Object Name) -join ',') -ceq 'panel')

    # The new program is built beside the old one and swapped in by renames: a power cut leaves the old program, or the new one,
    # or a folder Repair-ProgramSwap puts right, never half of each.
    function Reset-Program([string] $folder, [string] $text) {
        if (Test-Path -LiteralPath $folder) { Remove-Item -LiteralPath $folder -Recurse -Force }
        $null = New-Item -ItemType Directory $folder
        Set-Content -LiteralPath (Join-Path $folder 'Pulsar.Api.dll') $text
        Set-Content -LiteralPath (Join-Path $folder 'appsettings.Production.json') 'settings'
    }
    function Get-ProgramText([string] $folder) { if (Test-Path -LiteralPath (Join-Path $folder 'Pulsar.Api.dll')) { (Get-Content -LiteralPath (Join-Path $folder 'Pulsar.Api.dll')) } else { '<none>' } }
    Reset-Program $target 'old'
    Reset-Program "$target.new" 'half copied by a run that was stopped'
    Set-Content -LiteralPath (Join-Path "$target.new" 'garbage.bin') 'x'
    Copy-Program $source $target
    Check 'program swap: a half-built .new left by a run that was stopped is deleted, not merged into the new program' (
        (Get-ProgramText $target) -ceq 'new' -and -not (Test-Path -LiteralPath (Join-Path $target 'garbage.bin')) -and -not (Test-Path -LiteralPath "$target.new"))
    Reset-Program $target 'old'
    $script:moves = New-Object Collections.Generic.List[string]
    $realMove = (Get-Item function:Move-ProgramFolder).ScriptBlock
    function Move-ProgramFolder([string] $from, [string] $to) {
        $script:moves.Add("$(Split-Path -Leaf $from)>$(Split-Path -Leaf $to)")
        if ($script:failMove -and $script:moves.Count -eq $script:failMove) { throw 'Access to the path is denied (test).' }
        & $realMove $from $to
    }
    $script:failMove = 2
    $caught = Get-Thrown { Copy-Program $source $target }
    Check 'program swap: when the new folder cannot be moved into place the old program is put back, and the new one is not left behind' (
        $caught -match 'denied' -and (Get-ProgramText $target) -ceq 'old' -and -not (Test-Path -LiteralPath "$target.new") -and -not (Test-Path -LiteralPath "$target.old")) "$caught $(Get-ProgramText $target) $($script:moves -join ',')"
    $script:moves.Clear(); $script:failMove = 1
    $caught = Get-Thrown { Copy-Program $source $target }
    Check 'program swap: when the old folder cannot be moved aside (a file in it is open) nothing changed: the old program stays whole' (
        $caught -match 'denied' -and (Get-ProgramText $target) -ceq 'old' -and -not (Test-Path -LiteralPath "$target.new") -and -not (Test-Path -LiteralPath "$target.old"))
    $script:failMove = 0; $script:moves.Clear()
    Copy-Program $source $target
    Check 'program swap: the old folder is moved aside, the new one moved in, then the old one deleted' (($script:moves -join ',') -ceq 'panel>panel.old,panel.new>panel' -and (Get-ProgramText $target) -ceq 'new')
    Set-Item function:Move-ProgramFolder $realMove

    # Repair-ProgramSwap: what a run that was stopped between the steps left.
    Reset-Program "$target.old" 'the old program'
    Remove-Item -LiteralPath $target -Recurse -Force
    Reset-Program "$target.new" 'a new program'
    $said = Repair-ProgramSwap $target
    Check 'program swap: stopped between the two renames (no program, the old one in .old): the old program is put back' (
        (Get-ProgramText $target) -ceq 'the old program' -and -not (Test-Path -LiteralPath "$target.old") -and -not (Test-Path -LiteralPath "$target.new") -and "$said" -match 'put back')
    Reset-Program $target 'the new program'
    Reset-Program "$target.old" 'the old program'
    $null = Repair-ProgramSwap $target
    Check 'program swap: stopped after the swap (the new program in place, .old left): the program stays, .old goes' (
        (Get-ProgramText $target) -ceq 'the new program' -and -not (Test-Path -LiteralPath "$target.old"))
    Check 'program swap: with nothing left over there is nothing to repair' ([string]::IsNullOrEmpty("$(Repair-ProgramSwap $target)") -and (Get-ProgramText $target) -ceq 'the new program')
    Reset-Program "$target.old" 'the old program'
    Remove-Item -LiteralPath $target -Recurse -Force
    Copy-Program $source $target
    Check 'program swap: Copy-Program starts by putting right what an earlier run left, so a program folder that is missing is not replaced from nothing' (
        (Get-ProgramText $target) -ceq 'new' -and -not (Test-Path -LiteralPath "$target.old"))
}
finally {
    if (Test-Path -LiteralPath (Join-Path $root 'private')) {
        $null = & (Join-Path ([Environment]::SystemDirectory) 'icacls.exe') (Join-Path $root 'private') /grant "*$($me.User.Value):(OI)(CI)F"
    }
    Remove-Item -LiteralPath $root -Recurse -Force
}

# --- A panel's install: checked before anything changes, undone when it fails -------------------------------------

$root = Join-Path $Scratch ("pulsar-script-tests-" + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory $root
$listeners = @()
try {
    # Ports: taken when something listens on them, on any address (the panel listens on all of them).
    $taken = New-Object Net.Sockets.TcpListener ([Net.IPAddress]::Loopback, 0)
    $taken.Start()
    $listeners += $taken
    $takenPort = ([Net.IPEndPoint]$taken.LocalEndpoint).Port
    $free = @(1..3 | ForEach-Object { $probe = New-Object Net.Sockets.TcpListener ([Net.IPAddress]::Any, 0); $probe.Start(); $probe })
    $freePorts = @($free | ForEach-Object { ([Net.IPEndPoint]$_.LocalEndpoint).Port })
    $free | ForEach-Object { $_.Stop() }
    Check 'ports: one nothing listens on is free' (Test-PortFree $freePorts[0])
    Check 'ports: one a program listens on, on the loopback address only, is taken' (-not (Test-PortFree $takenPort))
    Check 'ports: a port in a URL' ((Get-EndpointPort 'https://*:8443') -eq 8443 -and (Get-EndpointPort 'https://[::]:9443/') -eq 9443)

    # A PFX with its key, as -CertificatePath gives one.
    $rsa = [Security.Cryptography.RSACng]::new(2048)
    $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=localhost', $rsa, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $made = $request.CreateSelfSigned([DateTimeOffset]::Now.AddDays(-1), [DateTimeOffset]::Now.AddDays(1))
    $pfxFile = Join-Path $root 'panel.pfx'
    [IO.File]::WriteAllBytes($pfxFile, $made.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, 'the right password'))
    $cerFile = Join-Path $root 'panel.cer'
    [IO.File]::WriteAllBytes($cerFile, $made.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    $rsa.Dispose()
    Check 'PFX: opens with its password, with its key' ($null -eq (Test-PfxFile $pfxFile 'the right password')) "$(Test-PfxFile $pfxFile 'the right password')"
    Check 'PFX: another password is a problem' ((Test-PfxFile $pfxFile 'wrong') -match 'password')
    Check 'PFX: a certificate without its key is a problem' ((Test-PfxFile $cerFile '') -match 'private key')

    # Assert-PanelInstallable as the install calls it, with nothing installed here (the services mocked).
    $backupFolder = Join-Path $root 'backup'
    $null = New-Item -ItemType Directory -Force (Join-Path $backupFolder 'panel-data\keys'), (Join-Path $backupFolder 'panel-data\tls')
    Copy-Item -LiteralPath $pfxFile (Join-Path $backupFolder 'panel-data\tls\panel.pfx')
    function Write-BackupSettings([int] $https, [string] $password) {
        @{ Kestrel = @{ Endpoints = @{ Https = @{ Url = "https://*:$https"; Certificate = @{ Path = 'x'; Password = $password } } } }; DataProtection = @{ CertificatePassword = 'k' } } |
            ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $backupFolder 'appsettings.Production.json') -Encoding UTF8
    }
    function Test-Preflight([hashtable] $values) {
        & {
            $PanelService = 'PulsarPanel'; $AgentService = 'PulsarAgent'; $Mode = 'Panel'; $AdminUserName = 'admin'; $AdminPassword = ConvertTo-Secure 'a long enough password'
            $PanelData = Join-Path $root 'no-panel-data'; $UpdateRoot = Join-Path $root 'no-updates'
            $Port = $freePorts[0]; $AgentsPort = 0; $SftpPort = $freePorts[1]; $CertificatePath = $null; $CertificatePassword = $null; $RestoreFrom = $null; $HostName = @(); $ConnectionString = $null
            foreach ($name in $values.Keys) { Set-Variable -Name $name -Value $values[$name] }
            function Test-Service([string] $name) { return $false }
            # The database of -ConnectionString is asked about its migrations in the checks below.
            function Assert-DatabaseNotNewer([string] $connection) { $script:checkedConnection = $connection }
            # Copying and checking the backup have their own checks below; here the folder is read as it is.
            function Copy-RestoreSource([string] $source) { return $source }
            Get-Caught { Assert-PanelInstallable }
        }
    }
    Check 'preflight: free ports and no certificate pass' ($null -eq (Test-Preflight @{})) "$(Test-Preflight @{})"
    $script:checkedConnection = 'not called'
    $null = Test-Preflight @{}
    Check 'preflight: without -ConnectionString no database is asked about' ($script:checkedConnection -ceq 'not called')
    $null = Test-Preflight @{ ConnectionString = 'Host=db;Database=pulsar;Username=u;Password=p' }
    Check 'preflight: the database of -ConnectionString is checked for migrations of a newer Pulsar' ($script:checkedConnection -ceq 'Host=db;Database=pulsar;Username=u;Password=p')
    Check "preflight: a PFX that opens with its password passes" ($null -eq (Test-Preflight @{ CertificatePath = $pfxFile; CertificatePassword = (ConvertTo-Secure 'the right password'); AgentsPort = $freePorts[2] }))
    Check 'preflight: -HostName names and IP addresses pass' ($null -eq (Test-Preflight @{ HostName = @('panel.example.com', '203.0.113.7', '2001:db8::7') }))
    foreach ($case in ([ordered]@{
            'the HTTPS port taken' = @(@{ Port = $takenPort }, 3, "$takenPort")
            'the agents'' port taken' = @(@{ AgentsPort = $takenPort }, 3, "$takenPort")
            'the SFTP port taken' = @(@{ SftpPort = $takenPort }, 3, "$takenPort")
            'a PFX its password does not open' = @(@{ CertificatePath = $pfxFile; CertificatePassword = (ConvertTo-Secure 'wrong') }, 2, 'password')
            'a PFX without its key' = @(@{ CertificatePath = $cerFile }, 2, 'private key')
            'a -HostName that is neither a name nor an IP address' = @(@{ HostName = @('panel.example.com', '192.168.1.300') }, 2, '-HostName: 192.168.1.300 is neither a name')
            'a -HostName address no server has' = @(@{ HostName = @('0.0.0.0') }, 2, '-HostName: 0.0.0.0 is not an address')
            'a backup whose panel port is taken' = @(@{ RestoreFrom = $backupFolder; Port = 1; Backup = @($takenPort, 'the right password') }, 3, "$takenPort")
            'a backup whose certificate does not open with its password' = @(@{ RestoreFrom = $backupFolder; Backup = @($freePorts[0], 'wrong') }, 2, 'password')
        }).GetEnumerator()) {
        $values = $case.Value[0].Clone()
        if ($values.ContainsKey('Backup')) { Write-BackupSettings $values.Backup[0] $values.Backup[1]; $values.Remove('Backup') }
        $caught = Test-Preflight $values
        Check "preflight: refuses $($case.Key), nothing changed (exit code $($case.Value[1]))" ((Get-SetupExitCode $caught) -eq $case.Value[1] -and $caught.Exception.Message -match [regex]::Escape($case.Value[2]) -and $caught.Exception.Message -match 'Nothing was changed') "$caught"
    }
    Write-BackupSettings $freePorts[0] 'the right password'
    Check 'preflight: a backup with free ports and a certificate that opens passes' ($null -eq (Test-Preflight @{ RestoreFrom = $backupFolder }))

    # PostgreSQL's 'pulsar' role and database (psql mocked): none, the leftover of an install that failed (a role this
    # script marked, without a database), or something to keep (any 'pulsar' database: never looked into).
    function Invoke-PsqlMock([string] $role, [string] $owner, [string] $relations, [scriptblock] $block) {
        $script:sql = New-Object Collections.Generic.List[string]
        & {
            $PostgresPort = 5432
            $PostgresPassword = ConvertTo-Secure 'the postgres password'
            function Find-PostgresBin { 'C:\PostgreSQL\bin' }
            function Write-Step { }
            function Invoke-Psql([string] $bin, [string] $password, [string[]] $arguments, [string] $stdin = $null, [string] $database = 'postgres') {
                $text = if ($stdin) { $stdin } else { $arguments -join ' ' }
                $script:sql.Add("${database}: $text")
                $output = ''
                if ($text -match "FROM pg_roles WHERE rolname = 'pulsar'") { $output = $role }
                elseif ($text -match "FROM pg_database WHERE datname = 'pulsar'") { $output = $owner }
                elseif ($text -match 'FROM pg_class') { $output = $relations }
                [pscustomobject]@{ ExitCode = 0; Output = $output }
            }
            & $block
        }
    }
    $marked = "role:$PulsarRoleComment"
    Check "database: no 'pulsar' role or database" ((Invoke-PsqlMock '' '' '' { Get-PanelDatabaseState 'b' 'p' }) -ceq 'none')
    Check "database: the role this script made, without a database, is a leftover" ((Invoke-PsqlMock $marked '' '' { Get-PanelDatabaseState 'b' 'p' }) -ceq 'leftover')
    # Its owner sets what a session in it runs with (ALTER DATABASE ... SET search_path, and objects of its own): the
    # superuser never connects to it, so an empty one is kept as one with tables is.
    $state = Invoke-PsqlMock $marked 'owner:pulsar' '0' { Get-PanelDatabaseState 'b' 'p' }
    Check "database: keeps the database of the role this script made, even an empty one, without connecting to it" (
        $state -notin 'none', 'leftover' -and $state -match "'pulsar' database" -and @($script:sql | Where-Object { $_ -notlike 'postgres: *' }).Count -eq 0) "$state / $($script:sql -join ' | ')"
    foreach ($case in ([ordered]@{
            'a database with tables in it (a panel removed with -Uninstall)' = @($marked, 'owner:pulsar', '42')
            'a role this script did not mark' = @('role:', 'owner:pulsar', '0')
            'a role marked otherwise' = @('role:someone else''s', '', '')
            'a database owned by another role' = @($marked, 'owner:postgres', '0')
            'a database without the role' = @('', 'owner:postgres', '')
        }).GetEnumerator()) {
        $state = Invoke-PsqlMock $case.Value[0] $case.Value[1] $case.Value[2] { Get-PanelDatabaseState 'b' 'p' }
        Check "database: keeps $($case.Key)" ($state -notin 'none', 'leftover' -and $state -match 'pulsar') "$state"
    }
    Check 'database: what is kept says how to start over' ((Get-PanelDatabaseMessage 'x') -match 'Starting over' -and (Get-PanelDatabaseMessage 'x') -match '-ConnectionString')
    $connection = Invoke-PsqlMock '' '' '' { New-PanelDatabase }
    $created = @($script:sql | Where-Object { $_ -match 'CREATE ROLE' })
    Check "database: a new 'pulsar' role, marked as this script's in the same transaction, on standard input, and its database" (
        $connection -match '^Host=localhost;Port=5432;Database=pulsar;Username=pulsar;Password=[A-Za-z0-9]{32}$' -and $created.Count -eq 1 -and
        $created[0] -match "BEGIN;[\s\S]*CREATE ROLE pulsar LOGIN PASSWORD '[A-Za-z0-9]{32}';[\s\S]*COMMENT ON ROLE pulsar IS '$([regex]::Escape($PulsarRoleComment))';[\s\S]*COMMIT;" -and
        @($script:sql | Where-Object { $_ -match 'CREATE DATABASE pulsar OWNER pulsar' }).Count -eq 1 -and @($script:sql | Where-Object { $_ -match 'DROP' }).Count -eq 0) ($script:sql -join ' | ')
    $connection = Invoke-PsqlMock $marked '' '' { New-PanelDatabase }
    $drop = [array]::IndexOf(@($script:sql), 'postgres: -c DROP ROLE pulsar')
    $create = [array]::IndexOf(@($script:sql), @($script:sql | Where-Object { $_ -match 'CREATE ROLE' })[0])
    Check 'database: a leftover role is dropped (no database with it: a DROP ROLE that fails if one appeared), then made again' (
        $connection -match 'Password=' -and $drop -ge 0 -and $create -gt $drop -and @($script:sql | Where-Object { $_ -match 'DROP DATABASE' }).Count -eq 0) ($script:sql -join ' | ')
    foreach ($relations in '0', '42') {
        $caught = Invoke-PsqlMock $marked 'owner:pulsar' $relations { Get-Caught { New-PanelDatabase } }
        Check "database: its 'pulsar' database is never dropped ($relations relations)" ($null -ne $caught -and $caught.Exception.Message -match 'Starting over' -and @($script:sql | Where-Object { $_ -match 'DROP|CREATE' }).Count -eq 0) "$caught"
    }
    # Every psql the script runs as the superuser connects to the 'postgres' database, which no role of Pulsar's owns:
    # Invoke-Psql takes no other, and psql.exe runs nowhere else.
    $psqlFunction = $installAst.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-Psql' }, $true)
    $psqlRuns = @($installAst.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] -and
                ($node.GetCommandName() -in 'Invoke-Native', 'Invoke-NativeWithInput' -or $node.InvocationOperator -eq 'Ampersand') -and @($node.CommandElements | Where-Object { $_ -is [Management.Automation.Language.VariableExpressionAst] -and $_.VariablePath.UserPath -eq 'psql' }).Count -gt 0 }, $true) |
            Where-Object { $_.Extent.StartOffset -lt $psqlFunction.Extent.StartOffset -or $_.Extent.EndOffset -gt $psqlFunction.Extent.EndOffset })
    Check "database: psql runs as the superuser in the 'postgres' database only" (
        $null -ne $psqlFunction -and @($psqlFunction.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'database' }).Count -eq 0 -and
        $psqlFunction.Extent.Text -match "'-U', 'postgres', '-d', 'postgres'" -and @($psqlRuns | Where-Object { $_.Extent.Text -notmatch '^Invoke-Native \$psql --version$' }).Count -eq 0 -and
        $installText -notmatch '(?i)Invoke-Psql[^\r\n]*-database') (@($psqlRuns | ForEach-Object { $_.Extent.Text }) -join ' | ')
    # Even there, a role may have planted functions or operators in public (clusters upgraded from PostgreSQL 14 or
    # older): the session searches pg_catalog alone, reads no psqlrc, and libpq's variables from the caller are gone.
    Check "database: the superuser's psql searches pg_catalog only and reads no startup file" (
        $psqlFunction.Extent.Text -match "\`$common = @\('-X'," -and
        $psqlFunction.Extent.Text -match "\`$env:PGOPTIONS = '-c search_path=pg_catalog,pg_temp'" -and
        $psqlFunction.Extent.Text -match 'Remove-Item Env:PGPASSWORD, Env:PGOPTIONS')
    $mainBlock = $installAst.Find({ param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq '$main' }, $true)
    Check 'database: libpq and psql variables from the caller are cleared before anything runs' (
        $null -ne $mainBlock -and $mainBlock.Extent.Text -match 'Clear-PostgresEnvironment' -and
        $installText -match "function Clear-PostgresEnvironment[\s\S]*?'\^\(PG\|PSQL\)'")
    $backupText = Get-Content -Raw (Join-Path $PSScriptRoot '..\install\Backup-Pulsar.ps1')
    Check "backup: pg_dump gets no libpq variables from the caller" ($backupText -match "'\^\(PG\|PSQL\)'[\s\S]*?\`$env:PGPASSWORD = ")

    # Undoing a panel's install that failed after it made its database: its service, firewall rules, program and the
    # database and role it made.
    $panelFolder = Join-Path $root 'Panel'
    function Test-Undo([bool] $service, [bool] $database, [int] $dropCode = 0) {
        $null = New-Item -ItemType Directory -Force $panelFolder
        Set-Content -LiteralPath (Join-Path $panelFolder 'Pulsar.Api.exe') 'x'
        $script:calls = New-Object Collections.Generic.List[string]
        & {
            $PanelDir = $panelFolder; $PanelService = 'PulsarPanel'; $Sc = 'sc.exe'; $NoFirewall = $false
            $PostgresPassword = ConvertTo-Secure 'the postgres password'
            function Stop-Service { $script:calls.Add("Stop-Service $($args[0])") }
            function Start-Sleep { }
            function Write-Step { }
            function Invoke-Native([string] $exe) { $script:calls.Add("$exe $($args -join ' ')"); [pscustomobject]@{ ExitCode = 0; Output = '' } }
            function Get-NetFirewallRule([string] $Name) { $script:calls.Add("rule $Name") }
            function Remove-NetFirewallRule { }
            function Find-PostgresBin { 'C:\PostgreSQL\bin' }
            function Invoke-Psql([string] $bin, [string] $password, [string[]] $arguments) { $script:calls.Add("psql $($arguments -join ' ')"); [pscustomobject]@{ ExitCode = $dropCode; Output = 'it failed' } }
            @(Undo-PanelInstall $service $database)
        }
    }
    $left = @(Test-Undo $true $true)
    Check 'undo: the service it made, its firewall rules, its program and the database and role it made' (
        $left.Count -eq 0 -and $script:calls -contains 'Stop-Service PulsarPanel' -and $script:calls -contains 'sc.exe delete PulsarPanel' -and
        $script:calls -contains 'rule Pulsar-Panel-HTTPS' -and $script:calls -contains 'rule Pulsar-Panel-SFTP' -and
        $script:calls -contains 'psql -c DROP DATABASE IF EXISTS pulsar WITH (FORCE) -c DROP ROLE IF EXISTS pulsar' -and -not (Test-Path -LiteralPath $panelFolder)) "$($left -join ' | ') / $($script:calls -join ' | ')"
    $left = @(Test-Undo $false $false)
    Check 'undo: no service or database it did not make' ($left.Count -eq 0 -and @($script:calls | Where-Object { $_ -match 'sc\.exe|psql|Stop-Service' }).Count -eq 0) ($script:calls -join ' | ')
    $left = @(Test-Undo $true $true 1)
    Check 'undo: what could not be undone is said' ($left.Count -eq 1 -and $left[0] -match "'pulsar'" -and $left[0] -match 'it failed') ($left -join ' | ')

    # The agent's runtimes, removed from Apps before Pulsar: -Uninstall cannot run its program then.
    $agentFolder = Join-Path $root 'Agent'
    $dotnetFolder = Join-Path $root 'dotnet'
    $null = New-Item -ItemType Directory -Force $agentFolder, (Join-Path $dotnetFolder 'host\fxr\10.0.13'), (Join-Path $dotnetFolder 'shared\Microsoft.NETCore.App\10.0.13')
    Set-Content -LiteralPath (Join-Path $agentFolder 'PulsarAgent.runtimeconfig.json') '{"runtimeOptions": {"tfm": "net10.0", "framework": {"name": "Microsoft.NETCore.App", "version": "10.0.12"}}}'
    $AgentDir = $agentFolder
    Check 'agent runtimes: all missing in a folder without .NET' ((@(Get-AgentMissingRuntimes (Join-Path $root 'missing')) -join ',') -ceq 'Microsoft.NETCore.App')
    $null = New-Item -ItemType File (Join-Path $dotnetFolder 'host\fxr\10.0.13\hostfxr.dll'), (Join-Path $dotnetFolder 'shared\Microsoft.NETCore.App\10.0.13\Microsoft.NETCore.App.deps.json')
    Check 'agent runtimes: none missing when a newer patch is there' (@(Get-AgentMissingRuntimes $dotnetFolder).Count -eq 0)
    Remove-Item -LiteralPath (Join-Path $agentFolder 'PulsarAgent.runtimeconfig.json')
    Check 'agent runtimes: none for a program that carries its own' (@(Get-AgentMissingRuntimes (Join-Path $root 'missing')).Count -eq 0)
}
finally {
    $listeners | ForEach-Object { $_.Stop() }
    Remove-Item -LiteralPath $root -Recurse -Force
}

# The order of a panel's install (read from the script): every check before anything changes, then what fails once its
# database is made undone.
$mainText = ($installAst.FindAll({ param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq '$main' }, $false) | Select-Object -First 1).Extent.Text
$order = @('Assert-PanelInstallable', 'Get-PostgresPlan', 'New-SetupDownloads', 'Install-DotNetRuntimes', 'Install-PostgresServer', 'Install-Panel' | ForEach-Object { $mainText.IndexOf($_) })
Check 'install order: the panel''s checks (ports, PFX, database) come before any download or install' (@($order | Where-Object { $_ -lt 0 }).Count -eq 0 -and (@($order) -join ',') -eq (@($order | Sort-Object) -join ',')) ($order -join ',')
$installPanel = $installAst.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Install-Panel' }, $true) | Select-Object -First 1
$panelTry = @($installPanel.Body.FindAll({ param($node) $node -is [Management.Automation.Language.TryStatementAst] -and @($node.CatchClauses | Where-Object { $_.Body.Extent.Text -match 'Undo-PanelInstall' }).Count -gt 0 }, $true))
$madeAt = $installPanel.Extent.Text.IndexOf('New-PanelDatabase')
Check 'install order: once the database is made, a failure undoes the install (from the restore of the dump to the last call to the panel)' (
    $panelTry.Count -eq 1 -and $madeAt -ge 0 -and ($installPanel.Extent.StartOffset + $madeAt) -lt $panelTry[0].Extent.StartOffset -and
    @('Restore-PanelDatabase', 'Copy-Program', 'New-PanelCertificate', 'Invoke-PanelMigrations', 'Start-Panel', "'/api/v1/sftp'" | Where-Object { $panelTry[0].Body.Extent.Text -notmatch [regex]::Escape($_) }).Count -eq 0 -and
    @($installPanel.Body.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Assert-PanelInstallable' }, $true)).Count -eq 0)
$uninstallAgent = ($installAst.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Uninstall-Agent' }, $true) | Select-Object -First 1).Extent.Text
Check 'uninstall: without the runtimes the agent needs, its service is removed with sc.exe instead of running its program' (
    $uninstallAgent.IndexOf('Get-AgentMissingRuntimes') -ge 0 -and $uninstallAgent.IndexOf('Get-AgentMissingRuntimes') -lt $uninstallAgent.IndexOf('unenroll'))
$withoutRuntimes = $uninstallAgent.Substring(0, $uninstallAgent.IndexOf('return'))
Check 'uninstall: without the runtimes, a running service is asked to stop its game servers (its stop-servers file) before it is stopped' (
    $withoutRuntimes -match "\`$AgentStopServersMarker" -and $withoutRuntimes.IndexOf('Write-AgentStopServersMarker') -ge 0 -and
    $withoutRuntimes.IndexOf('Write-AgentStopServersMarker') -lt $withoutRuntimes.IndexOf('Stop-Service') -and $installText -match "\`$AgentStopServersMarker = Join-Path \`$AgentData 'state\\stop-servers'")
Check 'uninstall: without the runtimes, it says which game servers may keep running, and that the node is to be deleted in the panel' (
    $withoutRuntimes -match 'keep running' -and $withoutRuntimes -match 'host-programs' -and $withoutRuntimes -match 'delete the node in the panel')
Check 'uninstall: "Pulsar is removed" only when the agent''s program is gone' ($installText -match "if \(Test-Path -LiteralPath \(Join-Path \`$AgentDir 'PulsarAgent\.exe'\)\) \{ 'Pulsar is removed, but for the agent")

# --- The release: framework-dependent ZIP, self-contained MSI (read from the build scripts, never run) --------------

$buildRelease = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\release\Build-Release.ps1')
$buildMsi = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\release\Build-AgentMsi.ps1')
Check 'release: the ZIP''s panel and agent are framework-dependent, for the newest patch the SDK knows' ($buildRelease -match "\`$zipOptions = \`$common \+ @\('--no-self-contained', '-p:TargetLatestRuntimePatch=true'\)" -and
    $buildRelease -match "dotnet publish \(Join-Path \`$repo 'src\\Pulsar.Api'\) @zipOptions" -and $buildRelease -match "dotnet publish \(Join-Path \`$repo 'src\\Pulsar.Agent'\) @zipOptions")
Check 'release: the MSI''s agent is self-contained' ($buildRelease -match "dotnet publish \(Join-Path \`$repo 'src\\Pulsar.Agent'\) @common --self-contained -o \`$msiAgent" -and $buildRelease -match '-AgentDir \$msiAgent')
Check 'release: the ZIP records the runtimes it was built for (release.json)' ($buildRelease -match "Join-Path \`$staging 'release.json'")
$releaseWorkflow = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\..\.github\workflows\release.yml')
Check 'release: CI builds the setups with Inno Setup 7.1.0 pinned by SHA-256 and signer, and fails without them' (
    $releaseWorkflow -match 'releases/download/is-7_1_0/innosetup-7\.1\.0-x64\.exe' -and $releaseWorkflow -match "-ne '0362a383ed217d4c4239b5933866dd96d3eb2102737da92f80f6057a4b40df2f'" -and
    $releaseWorkflow -match "-cne 'O=Pyrsys B\.V\.'" -and $releaseWorkflow -match 'Build-Release\.ps1 .*-RequireSetups' -and
    $releaseWorkflow.IndexOf('innosetup-7.1.0-x64.exe') -lt $releaseWorkflow.IndexOf('./scripts/release/Build-Release.ps1'))
Check 'release: the setups are uploaded by the build, then attested and published with their SHA-256 files' (
    $releaseWorkflow -match 'path: \|[\s\S]*artifacts/release/\*\.exe[\s\S]*artifacts/release/\*\.sha256' -and
    $releaseWorkflow -match 'subject-path: \|[\s\S]*release/\*\.exe' -and
    $releaseWorkflow -match '"PulsarSetup-\$version\.exe" "PulsarAgentSetup-\$version\.exe"; do\s+files\+=\("release/\$name" "release/\$name\.sha256"\)')
# Only the publish job may write, and it runs nothing of the repository; every action is pinned by commit SHA.
$jobs = [regex]::Split($releaseWorkflow, '(?m)^  publish:')
$actions = @([regex]::Matches($releaseWorkflow, '(?m)^\s*- uses: (\S+)') | ForEach-Object { $_.Groups[1].Value })
Check 'release: a read-only build job without stored credentials, a publish job that runs no repository code, actions pinned by SHA' (
    $jobs.Count -eq 2 -and $jobs[0] -notmatch ':\s*write' -and $jobs[0] -match 'persist-credentials: false' -and $jobs[1] -notmatch 'checkout|scripts/|npm|dotnet' -and
    $actions.Count -gt 0 -and @($actions | Where-Object { $_ -notmatch '@[0-9a-f]{40}\z' }).Count -eq 0) ($actions -join ', ')
Check 'release: ISCC comes from Program Files first, and it and its DLLs are checked as signed by Pyrsys B.V. before each compile' (
    $buildRelease -match "@\(\`$env:ProgramFiles, \`$\{env:ProgramFiles\(x86\)\}, \`$env:LOCALAPPDATA\)" -and $buildRelease -match "\`$InnoSetupSigner = 'Pyrsys B\.V\.'" -and
    $buildRelease -match 'Assert-InnoSetupSigned \$candidate' -and $buildRelease -match "Assert-InnoSetupSigned \`$compiler\s+\`$lines = & \{")
Check 'release: release.json records the oldest PostgreSQL 18 the installer takes, a release of 18' ($buildRelease -match 'postgresMinimum = \$PostgresMinimum' -and
    $buildRelease -match "\[ValidatePattern\('\^18\\\.\\d\{1,4\}\$'\)\] \[string\] \`$PostgresMinimum = '18\.\d+'")
Check 'release: the MSI refuses an agent that is not self-contained' ($buildMsi -match 'includedFrameworks')
Check 'release: Build-Release.ps1 builds the agent''s setup into the panel''s downloads folder before the ZIP, then PulsarSetup.exe' (
    $buildRelease -match "Build-Setup \`$iscc 'PulsarAgentSetup\.iss'" -and $buildRelease -match "Join-Path \`$staging 'panel\\downloads'" -and
    $buildRelease.IndexOf("'PulsarAgentSetup.iss'") -lt $buildRelease.IndexOf('Compress-Archive') -and
    $buildRelease.IndexOf('Compress-Archive') -lt $buildRelease.IndexOf("'PulsarSetup.iss'") -and $buildRelease -match "Join-Path \`$downloads\.FullName 'PulsarAgentSetup\.exe'")
Check 'release: an Inno Setup warning fails the build' ($buildRelease -match "'--messages-jsonl'" -and $buildRelease -match '\$messages\.Count -gt 0')

# --- Code signing in the release (optional; Build-Release.ps1's functions run on files of this test's own, nothing is signed) ---

$buildReleasePath = Join-Path $PSScriptRoot '..\release\Build-Release.ps1'
$signingParts = Get-ScriptParts $buildReleasePath @('Get-SignatureProblem', 'Assert-InnoSetupSigned', 'Assert-InnoSetupLocation', 'Get-AccountName', 'Find-UntrustedWriter', 'Find-UntrustedWriters', 'Find-UntrustedAncestors',
    'Assert-AdministratorsOnly', 'Test-CurrentUserOnly', 'New-SigningFolder', 'Hide-AzureCredentials', 'Set-AzureCredentials', 'Remove-AzureCredentials', 'Restore-AzureCredentials',
    'Get-WindowsKitsRoots', 'Find-SignTool', 'Assert-SignToolSigned', 'Assert-SigningCertificate', 'Read-TrustedSigningMetadata', 'New-SigningMetadata', 'Find-TrustedSigningClient',
    'Assert-TrustedSigningClient', 'Assert-SigningTools', 'Initialize-Signing', 'Invoke-SignTool', 'Assert-Signed', 'Invoke-Signing', 'Get-PulsarAssemblies', 'Get-PulsarPrograms',
    'Assert-ProgramsSigned', 'Format-InnoToolWord', 'Get-InnoSignCommand', 'Publish-Release', 'Remove-FolderPatiently', 'Remove-BuildWork') `
    @('TrustedWriters', 'ModifyRights', 'ReplaceRights', 'InnoSetupSigner', 'SignToolSigner', 'SignToolLibraries', 'CodeSigningUsage', 'SignedDescription', 'SignedInformationUrl', 'HiddenEnvironment')
# Who may change what signs is the install script's rule, with the same functions.
$differentFromInstall = @(@('Get-AccountName', 'Find-UntrustedWriter', 'Find-UntrustedWriters', 'Find-UntrustedAncestors', '$TrustedWriters', '$ModifyRights', '$ReplaceRights') | Where-Object { $installParts[$_] -cne $signingParts[$_] })
Check 'signing: the folder rule for what signs is Install-Pulsar.ps1''s, the same functions' ($differentFromInstall.Count -eq 0) ($differentFromInstall -join ', ')
foreach ($part in $signingParts.Values) { . ([scriptblock]::Create($part)) }
$SignTool = $null; $SigningThumbprint = $null; $TrustedSigningMetadata = $null; $TrustedSigningDlib = $null; $TimestampUrl = 'http://timestamp.digicert.com'; $GivenSigningOptions = @()
$RequireSigning = $false; $Signing = $null; $Work = $null; $ProgramSets = @()
$realFunctions = @{}
foreach ($function in 'Invoke-SignTool', 'Assert-Signed', 'Assert-SigningTools', 'Assert-AdministratorsOnly') { $realFunctions[$function] = (Get-Item "function:$function").ScriptBlock }
# Dot-sourced (. $restoreReal), so the functions it puts back are the script's own again.
$restoreReal = { foreach ($function in $realFunctions.Keys) { Set-Item "function:$function" $realFunctions[$function] } }
function Sort-Ordinal([string[]] $names) { $sorted = [string[]]@($names); [Array]::Sort($sorted, [StringComparer]::Ordinal); return $sorted }
function New-TestFile([string] $path, [string] $text = 'x') { $null = New-Item -ItemType Directory -Force (Split-Path -Parent $path); Set-Content -LiteralPath $path -Value $text; return $path }
function Get-SigningFolders { @(Get-ChildItem ([IO.Path]::GetTempPath()) -Directory -Filter 'pulsar-signing-*' -ErrorAction SilentlyContinue) }

Check 'signing: off unless asked (no signing option, no signing)' ($null -eq (Initialize-Signing))
$RequireSigning = $true
Check 'signing: -RequireSigning fails a build that would be unsigned' ((Get-Thrown { Initialize-Signing }) -match '-RequireSigning: this release would be unsigned')
$RequireSigning = $false
foreach ($lonely in 'TimestampUrl', 'SignTool', 'TrustedSigningDlib') {
    $GivenSigningOptions = @($lonely)
    Check "signing: -$lonely without a signing way is refused, not ignored" ((Get-Thrown { Initialize-Signing }) -match "These options only apply to a signed release \(-$lonely\)")
}
$GivenSigningOptions = @('SigningThumbprint', 'TrustedSigningMetadata')
Check 'signing: a thumbprint and Trusted Signing together are refused' ((Get-Thrown { Initialize-Signing }) -match 'not both')
$GivenSigningOptions = @('SigningThumbprint', 'TrustedSigningDlib')
Check 'signing: the Trusted Signing client with a thumbprint is refused' ((Get-Thrown { Initialize-Signing }) -match '-TrustedSigningDlib goes with -TrustedSigningMetadata')
$GivenSigningOptions = @()

$signRoot = Join-Path $Scratch ("pulsar-script-tests-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $signRoot | Out-Null
# This test's own temp folder, so the folders it counts are its own (a release being built meanwhile has its own).
$tempBefore = @($env:TEMP, $env:TMP)
$testTemp = Join-Path $signRoot 'temp'
$null = New-Item -ItemType Directory $testTemp
$env:TEMP = $testTemp; $env:TMP = $testTemp
$signingFoldersBefore = @(Get-SigningFolders).Count
try {
    # signtool.exe: the newest SDK's x64 one, found by version number; checked to be Microsoft's.
    $kits = Join-Path $signRoot 'Windows Kits\10'
    foreach ($relative in 'bin\10.0.9.0\x64', 'bin\10.0.19041.0\x64', 'bin\10.0.26100.0\x64', 'bin\10.0.26100.0\x86', 'bin\notaversion\x64', 'bin\x64') {
        New-Item -ItemType Directory -Force (Join-Path $kits $relative) | Out-Null
        Set-Content (Join-Path $kits "$relative\signtool.exe") 'x'
    }
    Check 'signing: signtool.exe is the newest SDK''s x64 one' ((Find-SignTool @($kits)) -eq (Join-Path $kits 'bin\10.0.26100.0\x64\signtool.exe'))
    Remove-Item (Join-Path $kits 'bin\10.0.26100.0') -Recurse -Force
    Check 'signing: SDK versions compare as numbers (10.0.19041.0 over 10.0.9.0)' ((Find-SignTool @($kits)) -eq (Join-Path $kits 'bin\10.0.19041.0\x64\signtool.exe'))
    Remove-Item (Join-Path $kits 'bin\10.0.19041.0'), (Join-Path $kits 'bin\10.0.9.0') -Recurse -Force
    Check 'signing: the older bin\x64 layout is the last resort' ((Find-SignTool @($kits)) -eq (Join-Path $kits 'bin\x64\signtool.exe'))
    $missing = Get-Thrown { Find-SignTool @((Join-Path $signRoot 'no-sdk')) }
    Check 'signing: no signtool.exe says where it looked and what to install' ($missing -match 'signtool\.exe was not found' -and $missing -match 'no-sdk' -and $missing -match 'Windows SDK')
    $SignTool = Join-Path $signRoot 'other.exe'; Set-Content $SignTool 'x'
    Check 'signing: -SignTool must be a signtool.exe' ((Get-Thrown { Find-SignTool @() }) -match 'must name an existing signtool\.exe')
    $SignTool = Join-Path $signRoot 'missing\signtool.exe'
    Check 'signing: -SignTool must exist' ((Get-Thrown { Find-SignTool @() }) -match 'must name an existing signtool\.exe')
    $SignTool = $null
    $unsignedTool = Join-Path $signRoot 'signtool.exe'; Set-Content $unsignedTool 'MZ'
    Check 'signing: an unsigned signtool.exe is refused' ((Get-Thrown { Assert-SignToolSigned $unsignedTool }) -match 'is not validly signed by Microsoft Corporation')
    $realTool = $null
    try { $realTool = Find-SignTool } catch { }
    if ($realTool) {
        Check 'signing: the installed SDK''s signtool.exe is validly signed by Microsoft' ($null -eq (Get-Thrown { Assert-SignToolSigned $realTool })) $realTool
        Check 'signing: another organization''s name is not Microsoft''s' ($null -ne (Get-SignatureProblem $realTool 'Microsoft') -and $null -ne (Get-SignatureProblem $realTool 'Pyrsys B.V.'))
        # signtool.exe takes the DLLs it imports from beside it: a swapped one is refused.
        $copyFolder = Join-Path $signRoot 'signtool-copy'; New-Item -ItemType Directory $copyFolder | Out-Null
        Copy-Item $realTool (Join-Path $copyFolder 'signtool.exe')
        Check 'signing: a signtool.exe copied alone is taken (by signature)' ($null -eq (Get-Thrown { Assert-SignToolSigned (Join-Path $copyFolder 'signtool.exe') }))
        if (Test-Path (Join-Path (Split-Path $realTool) 'mssign32.dll')) {
            Copy-Item (Join-Path (Split-Path $realTool) 'mssign32.dll') $copyFolder
            Check 'signing: with the SDK''s own mssign32.dll beside it' ($null -eq (Get-Thrown { Assert-SignToolSigned (Join-Path $copyFolder 'signtool.exe') }))
        }
        Set-Content (Join-Path $copyFolder 'wintrust.dll') 'not Microsoft''s'
        Check 'signing: a wintrust.dll beside signtool.exe that is not validly signed by Microsoft is refused' ((Get-Thrown { Assert-SignToolSigned (Join-Path $copyFolder 'signtool.exe') }) -match 'wintrust\.dll is not validly signed by Microsoft Corporation')
    }
    Check 'signing: a catalog-signed Windows file is not an embedded Authenticode signature' ($null -ne (Get-SignatureProblem (Join-Path $env:windir 'System32\notepad.exe') 'Microsoft Corporation'))
    # The same check ISCC gets: an unsigned compiler, or a folder that is no Inno Setup install, is refused.
    $fakeInno = Join-Path $signRoot 'Inno Setup 7'; New-Item -ItemType Directory $fakeInno | Out-Null
    Set-Content (Join-Path $fakeInno 'ISCC.exe') 'x'
    Check 'signing: a folder with no ISCmplr.dll is not an Inno Setup install' ((Get-Thrown { Assert-InnoSetupSigned (Join-Path $fakeInno 'ISCC.exe') }) -match 'has no ISCmplr\.dll')
    Set-Content (Join-Path $fakeInno 'ISCmplr.dll') 'x'
    Check 'signing: an unsigned ISCC.exe is refused as before' ((Get-Thrown { Assert-InnoSetupSigned (Join-Path $fakeInno 'ISCC.exe') }) -match "is not validly signed by Pyrsys B\.V\., Inno Setup's publisher")

    # What signs runs in place, so its folder must be one only administrators can change (a copy in the user's temp folder
    # is refused, however well signed); the SDK's own folder in Program Files is.
    $writableFolder = Join-Path $signRoot 'writable-sdk'
    $null = New-TestFile (Join-Path $writableFolder 'signtool.exe')
    $refused = Get-Thrown { Assert-AdministratorsOnly $writableFolder 'The folder of signtool.exe' }
    Check 'signing: a folder the current user can write in is refused' ($refused -match 'The folder of signtool\.exe .* could be changed by accounts other than SYSTEM, Administrators and TrustedInstaller') $refused
    Check 'signing: a path that does not exist is not taken for safe' ($null -ne (Get-Thrown { Assert-AdministratorsOnly (Join-Path $signRoot 'nope') 'x' }))
    # The Inno Setup compiler runs signtool with Azure's credentials when signing, so its folder is held to the same rule then.
    $Signing = [pscustomobject]@{ SignTool = 'x'; Dlib = $null }
    $refused = Get-Thrown { Assert-InnoSetupLocation (Join-Path $fakeInno 'ISCC.exe') }
    Check 'signing: an Inno Setup compiler in a folder the user can write in is refused for a signed release' ($refused -match 'The folder of the Inno Setup compiler .* could be changed by accounts other than') $refused
    $Signing = $null
    Check 'signing: ...and taken for an unsigned release, as before' ($null -eq (Get-Thrown { Assert-InnoSetupLocation (Join-Path $fakeInno 'ISCC.exe') }))
    if ($realTool) {
        Check 'signing: the SDK''s folder in Program Files is one only administrators can change' ($null -eq (Get-Thrown { Assert-AdministratorsOnly (Split-Path -Parent $realTool) 'The folder of signtool.exe' })) $realTool
        Copy-Item $realTool (Join-Path $writableFolder 'signtool.exe') -Force
        $SignTool = Join-Path $writableFolder 'signtool.exe'
        $GivenSigningOptions = @('SigningThumbprint', 'SignTool'); $SigningThumbprint = '0123456789ABCDEF0123456789ABCDEF01234567'
        Check 'signing: a validly signed signtool.exe in a folder the user can write in is refused before any build' ((Get-Thrown { Initialize-Signing }) -match 'The folder of signtool\.exe')
        $SignTool = $null
        $Signing = [pscustomobject]@{ SignTool = (Join-Path $writableFolder 'signtool.exe'); Dlib = $null }
        Check 'signing: a signed signtool.exe in a folder the user can write in is refused again when the tools are checked before each use' ((Get-Thrown { Assert-SigningTools }) -match 'The folder of signtool\.exe')
        function Assert-AdministratorsOnly { }
        try {
            $Signing = [pscustomobject]@{ SignTool = $unsignedTool; Dlib = $null }
            Check 'signing: a signtool.exe that is not Microsoft''s is refused when the tools are checked before each use, whatever its folder' ((Get-Thrown { Assert-SigningTools }) -match 'is not validly signed by Microsoft Corporation')
            $Signing = [pscustomobject]@{ SignTool = $realTool; Dlib = $null }
            Check 'signing: ...and the SDK''s is taken' ($null -eq (Get-Thrown { Assert-SigningTools }))
        }
        finally { . $restoreReal; $Signing = $null }
        $GivenSigningOptions = @('SigningThumbprint'); $SigningThumbprint = '0123456789ABCDEF0123456789ABCDEF01234567'
        Check 'signing: a thumbprint that is not in the store is refused before any build' ((Get-Thrown { Initialize-Signing }) -match "is in the current user's Personal store")
        $GivenSigningOptions = @(); $SigningThumbprint = $null
    }

    # The certificate in the store: its key, its dates, its purpose (made in memory).
    function New-CodeSigningTestCertificate([string] $subject, [double] $fromDays, [double] $toDays, [string[]] $usages) {
        $rsa = [Security.Cryptography.RSA]::Create(2048)
        $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new($subject, $rsa, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        if ($usages.Count -gt 0) {
            $oids = [Security.Cryptography.OidCollection]::new()
            foreach ($usage in $usages) { [void]$oids.Add([Security.Cryptography.Oid]::new($usage)) }
            $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($oids, $true))
        }
        return $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays($fromDays), [DateTimeOffset]::UtcNow.AddDays($toDays))
    }
    $goodSigner = New-CodeSigningTestCertificate 'CN=Signer, O=Test' -1 30 @($CodeSigningUsage)
    Check 'signing: a valid code-signing certificate with its key is taken' ($null -eq (Get-Thrown { Assert-SigningCertificate $goodSigner }))
    Check 'signing: an expired certificate is refused' ((Get-Thrown { Assert-SigningCertificate (New-CodeSigningTestCertificate 'CN=Old, O=Test' -30 -1 @($CodeSigningUsage)) }) -match 'not now')
    Check 'signing: a certificate that is not valid yet is refused' ((Get-Thrown { Assert-SigningCertificate (New-CodeSigningTestCertificate 'CN=Soon, O=Test' 1 30 @($CodeSigningUsage)) }) -match 'not now')
    Check 'signing: a certificate for another purpose (TLS) is refused' ((Get-Thrown { Assert-SigningCertificate (New-CodeSigningTestCertificate 'CN=Tls, O=Test' -1 30 @('1.3.6.1.5.5.7.3.1')) }) -match 'not for code signing')
    Check 'signing: a certificate with no key usage at all is refused' ((Get-Thrown { Assert-SigningCertificate (New-CodeSigningTestCertificate 'CN=Any, O=Test' -1 30 @()) }) -match 'not for code signing')
    $publicOnly = [Security.Cryptography.X509Certificates.X509Certificate2]::new($goodSigner.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
    Check 'signing: a certificate without its private key (a token that is not plugged in) is refused' ((Get-Thrown { Assert-SigningCertificate $publicOnly }) -match 'has no private key')

    # Azure Trusted Signing's metadata: the endpoint is the service's own, only the three values are used, and signtool is
    # given a fresh file in a folder of the current user alone.
    function Write-SigningFile([string] $name, [string] $text) { return New-TestFile (Join-Path $signRoot $name) $text }
    $goodMetadata = Write-SigningFile 'metadata.json' '{ "Endpoint": "https://eus.codesigning.azure.net", "CodeSigningAccountName": "account", "CertificateProfileName": "profile", "CorrelationId": "x" }'
    $values = Read-TrustedSigningMetadata $goodMetadata
    Check 'signing: Trusted Signing metadata gives its three values, and only them' (($values.Keys -join ',') -ceq 'Endpoint,CodeSigningAccountName,CertificateProfileName' -and $values['Endpoint'] -ceq 'https://eus.codesigning.azure.net' -and $values['CodeSigningAccountName'] -ceq 'account')
    Check 'signing: a metadata file that is not there is refused' ((Get-Thrown { Read-TrustedSigningMetadata (Join-Path $signRoot 'nope.json') }) -match 'existing JSON file')
    Check 'signing: metadata that is not JSON is refused' ((Get-Thrown { Read-TrustedSigningMetadata (Write-SigningFile 'bad.json' '{ nope') }) -match 'is not JSON')
    $badMetadata = [ordered]@{
        'a field missing' = '{ "Endpoint": "https://x.codesigning.azure.net", "CodeSigningAccountName": "a" }'
        'a name in another case' = '{ "endpoint": "https://x.codesigning.azure.net", "CodeSigningAccountName": "a", "CertificateProfileName": "p" }'
        'an empty field' = '{ "Endpoint": "https://x.codesigning.azure.net", "CodeSigningAccountName": " ", "CertificateProfileName": "p" }'
        'a field that is not text' = '{ "Endpoint": "https://x.codesigning.azure.net", "CodeSigningAccountName": 5, "CertificateProfileName": "p" }'
        'a control character in a field' = '{ "Endpoint": "https://x.codesigning.azure.net", "CodeSigningAccountName": "a\nb", "CertificateProfileName": "p" }'
        'an http endpoint' = '{ "Endpoint": "http://x.codesigning.azure.net", "CodeSigningAccountName": "a", "CertificateProfileName": "p" }'
        'an endpoint of another service' = '{ "Endpoint": "https://signing.example.com", "CodeSigningAccountName": "a", "CertificateProfileName": "p" }'
        'an endpoint that only ends like the service''s' = '{ "Endpoint": "https://x.codesigning.azure.net.example.com", "CodeSigningAccountName": "a", "CertificateProfileName": "p" }'
        'an endpoint that only contains the service''s' = '{ "Endpoint": "https://example.com/x.codesigning.azure.net", "CodeSigningAccountName": "a", "CertificateProfileName": "p" }'
        'an endpoint with a user' = '{ "Endpoint": "https://user@x.codesigning.azure.net", "CodeSigningAccountName": "a", "CertificateProfileName": "p" }'
        'an endpoint with a port' = '{ "Endpoint": "https://x.codesigning.azure.net:8443", "CodeSigningAccountName": "a", "CertificateProfileName": "p" }'
        'an endpoint with a path' = '{ "Endpoint": "https://x.codesigning.azure.net/a", "CodeSigningAccountName": "a", "CertificateProfileName": "p" }'
        'an endpoint with a space' = '{ "Endpoint": "https://x.codesigning.azure.net /a", "CodeSigningAccountName": "a", "CertificateProfileName": "p" }'
        'an endpoint in capitals' = '{ "Endpoint": "https://X.CODESIGNING.AZURE.NET", "CodeSigningAccountName": "a", "CertificateProfileName": "p" }'
        'a JSON string' = '"hello"'
    }
    $index = 0
    foreach ($case in $badMetadata.Keys) {
        $index++
        Check "signing: metadata with $case is refused" ($null -ne (Get-Thrown { Read-TrustedSigningMetadata (Write-SigningFile "bad$index.json" $badMetadata[$case]) }))
    }
    Check 'signing: a regional endpoint with a trailing slash is taken' ($null -eq (Get-Thrown { Read-TrustedSigningMetadata (Write-SigningFile 'slash.json' '{ "Endpoint": "https://weu.codesigning.azure.net/", "CodeSigningAccountName": "a", "CertificateProfileName": "p" }') }))
    $made = New-SigningMetadata $values
    try {
        $written = Get-Content -LiteralPath $made.Path -Raw | ConvertFrom-Json
        Check 'signing: signtool gets a fresh metadata file with the three values and nothing else' (@($written.PSObject.Properties).Count -eq 3 -and $written.Endpoint -ceq $values['Endpoint'] -and $written.CertificateProfileName -ceq 'profile' -and $made.Path -ne $goodMetadata -and $made.Text -ceq [IO.File]::ReadAllText($made.Path))
        Check 'signing: that file''s folder is the current user''s alone' ((Test-CurrentUserOnly $made.Folder) -and (Split-Path -Leaf $made.Folder) -like 'pulsar-signing-*')
        $madeDirectory = Get-Item -LiteralPath $made.Folder
        $acl = $madeDirectory.GetAccessControl([Security.AccessControl.AccessControlSections]::Access)
        $everyone = [Security.Principal.SecurityIdentifier]'S-1-1-0'
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($everyone, 'Read', 'ContainerInherit, ObjectInherit', 'None', 'Allow')))
        $madeDirectory.SetAccessControl($acl)
        Check 'signing: a folder anyone else was let into is not the current user''s alone' (-not (Test-CurrentUserOnly $made.Folder))
    }
    finally { Remove-Item -LiteralPath $made.Folder -Recurse -Force }
    Check 'signing: an ordinary folder (permissions inherited) is not the current user''s alone' (-not (Test-CurrentUserOnly $signRoot))
    Check 'signing: a file is not a folder of the current user''s alone' (-not (Test-CurrentUserOnly $goodMetadata))

    # The Trusted Signing client: named by -TrustedSigningDlib (no default, since the per-user install folder is the user's to
    # write), signed by Microsoft with the libraries beside it, in a folder only administrators can change.
    Check 'signing: Trusted Signing needs the client''s path' ((Get-Thrown { Find-TrustedSigningClient }) -match '-TrustedSigningDlib is required')
    $TrustedSigningDlib = Join-Path $signRoot 'client\Azure.CodeSigning.Dlib.dll'
    Check 'signing: a Trusted Signing client that is not there is refused' ((Get-Thrown { Find-TrustedSigningClient }) -match 'Azure\.CodeSigning\.Dlib\.dll was not found')
    $null = New-TestFile $TrustedSigningDlib
    Check 'signing: the Trusted Signing client is found by its file name' ((Find-TrustedSigningClient) -eq (Resolve-Path $TrustedSigningDlib).Path)
    Check 'signing: an unsigned Trusted Signing client is refused' ((Get-Thrown { Assert-TrustedSigningClient (Resolve-Path $TrustedSigningDlib).Path }) -match 'is not validly signed by Microsoft Corporation')
    $TrustedSigningDlib = Join-Path $signRoot 'client\other.dll'; Set-Content $TrustedSigningDlib 'x'
    Check 'signing: a client under another file name is refused' ((Get-Thrown { Find-TrustedSigningClient }) -match 'was not found')
    $TrustedSigningDlib = $null
    if ($realTool) {
        $client = Join-Path $signRoot 'client2'; New-Item -ItemType Directory $client | Out-Null
        Copy-Item (Join-Path (Split-Path $realTool) 'mssign32.dll') (Join-Path $client 'Azure.CodeSigning.Dlib.dll')
        Check 'signing: a client signed by Microsoft is taken (by signature)' ($null -eq (Get-Thrown { Assert-TrustedSigningClient (Join-Path $client 'Azure.CodeSigning.Dlib.dll') }))
        # In a folder the user can write in, however well signed, it is refused before anything is hidden or made.
        $GivenSigningOptions = @('TrustedSigningMetadata', 'TrustedSigningDlib'); $TrustedSigningMetadata = $goodMetadata; $TrustedSigningDlib = Join-Path $client 'Azure.CodeSigning.Dlib.dll'
        $env:AZURE_PULSAR_TEST_SECRET = 'secret-value'
        $refused = Get-Thrown { Initialize-Signing }
        Check 'signing: a Trusted Signing client in a folder the user can write in is refused' ($refused -match 'The folder of the Trusted Signing client') $refused
        Check 'signing: ...and then nothing was hidden or made (no credentials taken from the environment, no folder of metadata left)' (
            $HiddenEnvironment.Count -eq 0 -and [Environment]::GetEnvironmentVariable('AZURE_PULSAR_TEST_SECRET') -ceq 'secret-value' -and @(Get-SigningFolders).Count -eq $signingFoldersBefore)
        Remove-Item Env:\AZURE_PULSAR_TEST_SECRET
        Set-Content (Join-Path $client 'Azure.Core.dll') 'unsigned'
        Check 'signing: an unsigned library beside the client is refused' ((Get-Thrown { Assert-TrustedSigningClient (Join-Path $client 'Azure.CodeSigning.Dlib.dll') }) -match 'Azure\.Core\.dll is not validly signed')
        Remove-Item (Join-Path $client 'Azure.Core.dll')
        # The whole Trusted Signing set-up, with the folder rule stubbed (it cannot be satisfied without administrator rights
        # here, and is tested above): signtool is given the fresh file, and the credentials leave the environment.
        function Assert-AdministratorsOnly { }
        $env:AZURE_PULSAR_TEST_SECRET = 'secret-value'
        $started = $null
        try {
            $started = Initialize-Signing
            Check 'signing: Trusted Signing is set up with signtool, the client and a fresh metadata file' (
                $started.SignTool -eq $realTool -and $started.Dlib -eq (Resolve-Path (Join-Path $client 'Azure.CodeSigning.Dlib.dll')).Path -and
                $started.Arguments[($started.Arguments.IndexOf('/dmdf') + 1)] -eq $started.MetadataPath -and $started.MetadataPath -ne $goodMetadata -and
                $started.Arguments -contains '/dlib' -and (Test-Path -LiteralPath $started.MetadataPath) -and (Test-CurrentUserOnly $started.MetadataFolder) -and
                (Split-Path -Parent $started.MetadataPath) -eq $started.MetadataFolder)
            Check 'signing: the credentials are out of this process''s environment, kept for signtool' ($null -eq [Environment]::GetEnvironmentVariable('AZURE_PULSAR_TEST_SECRET') -and $HiddenEnvironment['AZURE_PULSAR_TEST_SECRET'] -ceq 'secret-value')
            $Signing = $started
            Check 'signing: the set-up passes the tools check' ($null -eq (Get-Thrown { Assert-SigningTools }))
            Add-Content -LiteralPath $started.MetadataPath -Value ' '
            Check 'signing: a metadata file changed since it was written is refused before signtool is run' ((Get-Thrown { Assert-SigningTools }) -match 'is not as this script wrote it')
            Set-Content -LiteralPath $started.MetadataPath -Value $started.MetadataText -NoNewline
            Check 'signing: ...and restored, it passes again' ($null -eq (Get-Thrown { Assert-SigningTools }))
            $startedFolder = Get-Item -LiteralPath $started.MetadataFolder
            $startedAcl = $startedFolder.GetAccessControl([Security.AccessControl.AccessControlSections]::Access)
            $startedAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule([Security.Principal.SecurityIdentifier]'S-1-1-0', 'Read', 'ContainerInherit, ObjectInherit', 'None', 'Allow')))
            $startedFolder.SetAccessControl($startedAcl)
            Check 'signing: a metadata folder anyone else was let into is refused before signtool is run' ((Get-Thrown { Assert-SigningTools }) -match 'no longer the current user')
            $Signing = $null
        }
        finally {
            . $restoreReal
            Remove-BuildWork $null $started
            Restore-AzureCredentials
            if ($null -ne [Environment]::GetEnvironmentVariable('AZURE_PULSAR_TEST_SECRET')) { Remove-Item Env:\AZURE_PULSAR_TEST_SECRET }
            $GivenSigningOptions = @(); $TrustedSigningMetadata = $null; $TrustedSigningDlib = $null
        }
        Check 'signing: the end of the build removes the metadata folder' ($null -ne $started -and -not (Test-Path -LiteralPath $started.MetadataFolder) -and @(Get-SigningFolders).Count -eq $signingFoldersBefore)
    }

    # The Azure credentials: out of the environment the builds inherit, in only for signtool (and the Inno Setup compiler
    # that runs it), and back in the shell at the end.
    $env:AZURE_PULSAR_TEST_CREDENTIAL = 'abc'
    $cmd = $env:ComSpec
    try {
        Hide-AzureCredentials
        $seenByChild = (& $cmd /c 'echo [%AZURE_PULSAR_TEST_CREDENTIAL%]') -join ''
        Check 'signing: Azure''s credentials are taken out of the environment the builds inherit' ($seenByChild -ceq '[%AZURE_PULSAR_TEST_CREDENTIAL%]' -and $HiddenEnvironment['AZURE_PULSAR_TEST_CREDENTIAL'] -ceq 'abc')
        Check 'signing: without them, a program that needs them does not get them' ((Get-Thrown { Invoke-SignTool $cmd @('/c', 'if defined AZURE_PULSAR_TEST_CREDENTIAL (exit 0) else (exit 7)') 'The probe failed.' }) -match 'exit code 7')
        Check 'signing: -WithCredentials gives signtool them for its run' ($null -eq (Get-Thrown { Invoke-SignTool $cmd @('/c', 'if defined AZURE_PULSAR_TEST_CREDENTIAL (exit 0) else (exit 7)') 'The probe failed.' -WithCredentials }))
        Check 'signing: ...and takes them out again' ($null -eq [Environment]::GetEnvironmentVariable('AZURE_PULSAR_TEST_CREDENTIAL'))
        $thrownWithCredentials = Get-Thrown { Invoke-SignTool $cmd @('/c', 'exit 9') 'The probe failed.' -WithCredentials }
        Check 'signing: ...even when signtool fails' ($thrownWithCredentials -match 'exit code 9' -and $null -eq [Environment]::GetEnvironmentVariable('AZURE_PULSAR_TEST_CREDENTIAL'))
    }
    finally {
        Restore-AzureCredentials
    }
    Check 'signing: at the end, they are back in the shell the script was run from' ([Environment]::GetEnvironmentVariable('AZURE_PULSAR_TEST_CREDENTIAL') -ceq 'abc' -and $HiddenEnvironment.Count -eq 0)
    Remove-Item Env:\AZURE_PULSAR_TEST_CREDENTIAL

    # signtool's exit code and output.
    Check 'signing: signtool exiting with 0 passes' ($null -eq (Get-Thrown { Invoke-SignTool $cmd @('/c', 'exit 0') 'It failed.' }))
    $failed = Get-Thrown { Invoke-SignTool $cmd @('/c', 'echo the output of the tool& exit 3') 'Signing the test files failed.' }
    Check 'signing: a failing signtool stops the build, with its message, exit code and output' ($failed -match 'Signing the test files failed\.' -and $failed -match 'exit code 3' -and $failed -match 'the output of the tool') $failed
    Check 'signing: signtool''s warning exit code (2, a timestamp it could not get) fails the build too' ((Get-Thrown { Invoke-SignTool $cmd @('/c', 'exit 2') 'Signing failed.' }) -match 'exit code 2')

    # Verifying what was signed, on real files: signtool.exe itself is signed by Microsoft with a timestamp.
    if ($realTool) {
        $signedFile = Join-Path $signRoot 'verify\signtool.exe'
        $null = New-Item -ItemType Directory (Split-Path $signedFile); Copy-Item $realTool $signedFile
        $microsoftSignature = Get-AuthenticodeSignature -LiteralPath $signedFile
        $Signing = [pscustomobject]@{ SignTool = $realTool; Thumbprint = $null; Subject = $null; Arguments = [string[]]@('sign') }
        Check 'signing: a signed, timestamped file verifies, and its signer becomes the release''s' ($null -eq (Get-Thrown { Assert-Signed @($signedFile) }) -and $Signing.Subject -ceq $microsoftSignature.SignerCertificate.Subject)
        $Signing.Thumbprint = $microsoftSignature.SignerCertificate.Thumbprint
        Check 'signing: ...with the certificate that was asked to sign' ($null -eq (Get-Thrown { Assert-Signed @($signedFile) }))
        $Signing.Thumbprint = 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA'
        Check 'signing: a file signed with another certificate than the one asked is refused' ((Get-Thrown { Assert-Signed @($signedFile) }) -match 'is signed with certificate')
        $Signing.Thumbprint = $null
        $Signing.Subject = 'CN=Someone Else, O=Other'
        Check 'signing: a file of another signer than the rest of the release is refused' ((Get-Thrown { Assert-Signed @($signedFile) }) -match 'not by CN=Someone Else')
        $Signing.Subject = $null
        $unsignedFile = New-TestFile (Join-Path $signRoot 'verify\unsigned.dll') 'MZ not signed'
        Check 'signing: an unsigned file is refused' ((Get-Thrown { Assert-Signed @($unsignedFile) }) -match 'does not verify after signing')
        $altered = Join-Path $signRoot 'verify\altered.exe'
        $bytes = [IO.File]::ReadAllBytes($signedFile); $bytes[1024] = $bytes[1024] -bxor 0xFF; [IO.File]::WriteAllBytes($altered, $bytes)
        Check 'signing: a signed file that was changed afterwards is refused' ((Get-Thrown { Assert-Signed @($altered) }) -match 'does not verify after signing')
        $selfSigned = Join-Path $signRoot 'verify\selfsigned.ps1'
        Set-Content -LiteralPath $selfSigned -Value 'Write-Host hi'
        try { $null = Set-AuthenticodeSignature -FilePath $selfSigned -Certificate $goodSigner -ErrorAction Stop } catch { }
        if ((Get-AuthenticodeSignature -LiteralPath $selfSigned).SignerCertificate) {
            Check 'signing: a signature that does not chain to a root this PC trusts (a self-signed certificate) is refused' ((Get-Thrown { Assert-Signed @($selfSigned) }) -match 'does not verify after signing')
        }
        Check 'signing: a list of files is all checked (the second, unsigned, is found)' ((Get-Thrown { Assert-Signed @($signedFile, $unsignedFile) }) -match 'unsigned\.dll does not verify')
        $Signing = $null
    }

    # Assert-Signed's decisions on what Windows reports about a file (signtool's own verification stubbed to pass, and
    # Get-AuthenticodeSignature to say what the case needs): each way a signature can be wrong is refused on its own.
    function Invoke-SignTool { }
    function Get-AuthenticodeSignature { param([string] $LiteralPath) $fakeSignature }
    try {
        $Signing = [pscustomobject]@{ SignTool = 'x'; Thumbprint = $null; Subject = $null; Arguments = [string[]]@('sign') }
        function New-FakeSignature($status = 'Valid', $type = 'Authenticode', $subject = 'CN=Signer, O=Test', $thumbprint = 'ABCDEF0123456789ABCDEF0123456789ABCDEF01', $timestamped = $true, $signed = $true) {
            [pscustomobject]@{
                Status = $status; SignatureType = $type
                SignerCertificate = $(if ($signed) { [pscustomobject]@{ Subject = $subject; Thumbprint = $thumbprint } })
                TimeStamperCertificate = $(if ($timestamped) { [pscustomobject]@{ Subject = 'CN=Timestamp' } })
            }
        }
        $fakeSignature = New-FakeSignature
        Check 'signing: a valid, timestamped, embedded signature is taken, and its signer becomes the release''s' ($null -eq (Get-Thrown { Assert-Signed @('C:\a.dll') }) -and $Signing.Subject -ceq 'CN=Signer, O=Test')
        $badSignatures = [ordered]@{
                'a signature that is not valid (changed afterwards)' = (New-FakeSignature -status 'HashMismatch')
                'a file that is not signed' = (New-FakeSignature -status 'NotSigned' -signed $false -timestamped $false)
                'a signature that is not trusted' = (New-FakeSignature -status 'UnknownError')
                'a catalog signature, not one in the file' = (New-FakeSignature -type 'Catalog')
                'a signature with no signer' = (New-FakeSignature -signed $false)
                'a signature with no timestamp' = (New-FakeSignature -timestamped $false)
                'a signer other than the release''s' = (New-FakeSignature -subject 'CN=Other, O=Other')
        }
        foreach ($case in $badSignatures.GetEnumerator()) {
            $fakeSignature = $case.Value
            Check "signing: $($case.Key) is refused" ($null -ne (Get-Thrown { Assert-Signed @('C:\a.dll') })) 
        }
        $fakeSignature = New-FakeSignature
        $Signing.Thumbprint = 'ABCDEF0123456789ABCDEF0123456789ABCDEF01'
        Check 'signing: ...the thumbprint asked for is the one that signed' ($null -eq (Get-Thrown { Assert-Signed @('C:\a.dll') }))
        $Signing.Thumbprint = '0000000000000000000000000000000000000000'
        Check 'signing: ...and another certificate''s is refused' ((Get-Thrown { Assert-Signed @('C:\a.dll') }) -match 'is signed with certificate')
    }
    finally { Remove-Item function:Get-AuthenticodeSignature; Remove-Item function:New-FakeSignature -ErrorAction SilentlyContinue; . $restoreReal; $Signing = $null }

    # Signing in batches: every file goes to signtool once, in order, 40 at a time, then they are all verified.
    $calls = New-Object Collections.Generic.List[object]
    function Invoke-SignTool([string] $tool, [string[]] $arguments, [string] $failure, [switch] $WithCredentials) { $calls.Add([pscustomobject]@{ Tool = $tool; Arguments = $arguments; Credentials = [bool]$WithCredentials; Failure = $failure }) }
    function Assert-Signed([string[]] $files) { $calls.Add([pscustomobject]@{ Verified = @($files) }) }
    function Assert-SigningTools { $calls.Add([pscustomobject]@{ Checked = $true }) }
    try {
        $Signing = [pscustomobject]@{ SignTool = 'C:\sdk\signtool.exe'; Arguments = [string[]]@('sign', '/fd', 'sha256'); Thumbprint = $null; Subject = 'CN=Test, O=Test' }
        foreach ($count in 1, 40, 41, 80, 81) {
            $files = @(1..$count | ForEach-Object { "C:\out\file$_.dll" })
            $calls.Clear()
            Invoke-Signing $files 'the test files' | Out-Null
            $signCalls = @($calls | Where-Object { $_.PSObject.Properties['Tool'] })
            $sent = @($signCalls | ForEach-Object { $_.Arguments[3..($_.Arguments.Count - 1)] })
            $expectedBatches = [Math]::Ceiling($count / 40)
            Check "signing: $count file(s) go to signtool in $expectedBatches run(s), each once and in order" (
                $signCalls.Count -eq $expectedBatches -and ($sent -join '|') -ceq ($files -join '|') -and @($signCalls | Where-Object { $_.Arguments.Count -gt 43 }).Count -eq 0 -and
                @($signCalls | Where-Object { $_.Tool -ne 'C:\sdk\signtool.exe' -or -not $_.Credentials -or ($_.Arguments[0..2] -join ' ') -cne 'sign /fd sha256' }).Count -eq 0) "$($signCalls.Count) runs; sent $($sent.Count)"
            Check "signing: ...the tools are checked first and all $count file(s) verified afterwards, once" (
                $calls[0].PSObject.Properties['Checked'] -and $calls[$calls.Count - 1].PSObject.Properties['Verified'] -and @($calls | Where-Object { $_.PSObject.Properties['Verified'] }).Count -eq 1 -and
                ($calls[$calls.Count - 1].Verified -join '|') -ceq ($files -join '|'))
        }
        $calls.Clear()
        Check 'signing: nothing to sign is an error, not a signed release' ((Get-Thrown { Invoke-Signing @() 'the test files' }) -match 'There is nothing to sign for the test files' -and $calls.Count -eq 0)
        $Signing = $null
    }
    finally { . $restoreReal }

    # The programs to sign: an explicit list from the project files (here a project and its reference), nothing else.
    $panelAssemblies = Sort-Ordinal @(Get-PulsarAssemblies (Join-Path $PSScriptRoot '..\..\src\Pulsar.Api\Pulsar.Api.csproj'))
    $agentAssemblies = Sort-Ordinal @(Get-PulsarAssemblies (Join-Path $PSScriptRoot '..\..\src\Pulsar.Agent\Pulsar.Agent.csproj'))
    Check 'signing: the panel''s assemblies, from the project files, are the project and everything it references' (($panelAssemblies -join ',') -ceq 'Pulsar.Api,Pulsar.Application,Pulsar.Contracts,Pulsar.Domain,Pulsar.Infrastructure,Pulsar.Manifests') ($panelAssemblies -join ',')
    Check 'signing: the agent''s are PulsarAgent (its AssemblyName) and what it references' (($agentAssemblies -join ',') -ceq 'Pulsar.Agent.Core,Pulsar.Agent.Windows,Pulsar.Contracts,Pulsar.Manifests,PulsarAgent') ($agentAssemblies -join ',')
    $projects = Join-Path $signRoot 'src'
    $null = New-TestFile (Join-Path $projects 'Pulsar.Fake.Api\Pulsar.Fake.Api.csproj') '<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><AssemblyName>PulsarFake</AssemblyName></PropertyGroup><ItemGroup><ProjectReference Include="..\Pulsar.Fake.Core\Pulsar.Fake.Core.csproj" /><ProjectReference Include="..\Pulsar.Fake.Shared\Pulsar.Fake.Shared.csproj" /></ItemGroup></Project>'
    $null = New-TestFile (Join-Path $projects 'Pulsar.Fake.Core\Pulsar.Fake.Core.csproj') '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="..\Pulsar.Fake.Shared\Pulsar.Fake.Shared.csproj" /></ItemGroup></Project>'
    $null = New-TestFile (Join-Path $projects 'Pulsar.Fake.Shared\Pulsar.Fake.Shared.csproj') '<Project Sdk="Microsoft.NET.Sdk"></Project>'
    $fakeProject = Join-Path $projects 'Pulsar.Fake.Api\Pulsar.Fake.Api.csproj'
    Check 'signing: a project''s assemblies are found through its references once each, its AssemblyName first' ((@(Get-PulsarAssemblies $fakeProject) -join ',') -ceq 'PulsarFake,Pulsar.Fake.Core,Pulsar.Fake.Shared')
    $published = Join-Path $signRoot 'published'
    foreach ($file in 'PulsarFake.exe', 'PulsarFake.dll', 'Pulsar.Fake.Core.dll', 'Pulsar.Fake.Shared.dll', 'Npgsql.dll', 'Serilog.dll', 'PulsarFake.pdb', 'wwwroot\app.js', 'es\PulsarFake.resources.dll', 'es\Pulsar.Fake.Core.resources.dll') { $null = New-TestFile (Join-Path $published $file) }
    $listed = Sort-Ordinal @(Get-PulsarPrograms $published $fakeProject | ForEach-Object { $_.Substring($published.Length + 1) })
    Check 'signing: the programs are the listed assemblies, the apphost and their satellites, never the libraries or other files' (($listed -join ',') -ceq 'Pulsar.Fake.Core.dll,Pulsar.Fake.Shared.dll,PulsarFake.dll,PulsarFake.exe,es\Pulsar.Fake.Core.resources.dll,es\PulsarFake.resources.dll') ($listed -join ',')
    $unexpectedPrograms = [ordered]@{
            'a Pulsar assembly no project lists' = 'Pulsar.Evil.dll'
            'a Pulsar program no project lists' = 'PulsarEvil.exe'
            'another apphost (a project that is no entry has none)' = 'Pulsar.Fake.Core.exe'
            'a Pulsar assembly in a subfolder' = 'tools\Pulsar.Fake.Core.dll'
            'a satellite of an assembly no project lists' = 'es\Pulsar.Evil.resources.dll'
            'a satellite two folders down' = 'es\x\PulsarFake.resources.dll'
            'a Pulsar program in the downloads folder that is not the agent''s setup' = 'downloads\PulsarEvil.exe'
    }
    foreach ($case in $unexpectedPrograms.GetEnumerator()) {
        $path = New-TestFile (Join-Path $published $case.Value)
        $refused = Get-Thrown { Get-PulsarPrograms $published $fakeProject }
        Check "signing: $($case.Key) fails the build instead of being signed or skipped" ($refused -match 'has a program Pulsar does not list for signing' -and $refused -match [regex]::Escape($case.Value)) $refused
        Remove-Item -LiteralPath $path
    }
    Check 'signing: Pulsar-looking names with other extensions, and other names, are not programs' ($null -eq (Get-Thrown { $null = New-TestFile (Join-Path $published 'Pulsar.Notes.txt'); $null = New-TestFile (Join-Path $published 'NotPulsar.dll'); Get-PulsarPrograms $published $fakeProject }))
    Remove-Item (Join-Path $published 'PulsarFake.dll')
    Check 'signing: a listed program that is missing fails the build' ((Get-Thrown { Get-PulsarPrograms $published $fakeProject }) -match 'has no PulsarFake\.dll to sign')
    $null = New-TestFile (Join-Path $published 'PulsarFake.dll')
    $null = New-TestFile (Join-Path $published 'downloads\PulsarAgentSetup.exe')
    Check 'signing: the agent''s setup in the downloads folder is expected, and returned to be verified, when it is there' (
        (@(Get-PulsarPrograms $published $fakeProject @('downloads\PulsarAgentSetup.exe')) -contains (Join-Path (Resolve-Path $published).Path 'downloads\PulsarAgentSetup.exe')) -and
        $null -ne (Get-Thrown { Get-PulsarPrograms $published $fakeProject }))
    Remove-Item (Join-Path $published 'downloads') -Recurse -Force
    Check 'signing: ...and not required when it is not' ($null -eq (Get-Thrown { Get-PulsarPrograms $published $fakeProject @('downloads\PulsarAgentSetup.exe') }))

    # The programs are re-read and verified again right before each package is made of them.
    $calls = New-Object Collections.Generic.List[object]
    function Assert-Signed([string[]] $files) { $calls.Add([pscustomobject]@{ Verified = @($files) }) }
    function Assert-SigningTools { $calls.Add([pscustomobject]@{ Checked = $true }) }
    try {
        $ProgramSets = @(@{ Folder = $published; Project = $fakeProject; Extra = @('downloads\PulsarAgentSetup.exe') })
        $Signing = $null
        Assert-ProgramsSigned
        Check 'signing: an unsigned build verifies nothing' ($calls.Count -eq 0)
        $Signing = [pscustomobject]@{ SignTool = 'x'; Arguments = [string[]]@('sign') }
        Assert-ProgramsSigned
        $verified = @($calls | Where-Object { $_.PSObject.Properties['Verified'] } | ForEach-Object { $_.Verified })
        Check 'signing: a signed build checks the tools, then every program of every set' ($calls.Count -gt 0 -and $calls[0].PSObject.Properties['Checked'] -and $verified.Count -eq 6)
        $null = New-TestFile (Join-Path $published 'downloads\PulsarAgentSetup.exe')
        $calls.Clear(); Assert-ProgramsSigned
        $verified = @($calls | Where-Object { $_.PSObject.Properties['Verified'] } | ForEach-Object { $_.Verified })
        Check 'signing: ...the agent''s setup in the downloads folder too, once it is there' ($verified.Count -eq 7 -and @($verified | Where-Object { $_ -like '*downloads\PulsarAgentSetup.exe' }).Count -eq 1)
        $null = New-TestFile (Join-Path $published 'Pulsar.Left.Behind.dll')
        $calls.Clear()
        Check 'signing: a program that appeared since fails the build before anything is verified as good' ((Get-Thrown { Assert-ProgramsSigned }) -match 'Pulsar.Left.Behind.dll' -and @($calls | Where-Object { $_.PSObject.Properties['Verified'] }).Count -eq 0)
        Remove-Item (Join-Path $published 'Pulsar.Left.Behind.dll')
        $ProgramSets = @(@{ Folder = (Join-Path $signRoot 'gone'); Project = $fakeProject; Extra = @() })
        Check 'signing: a folder that is not there fails the build, it is not skipped' ($null -ne (Get-Thrown { Assert-ProgramsSigned }))
    }
    finally { . $restoreReal; $Signing = $null; $ProgramSets = @() }

    # The work folder: what the build made moves into the output folder only when it is all there, replacing the same names;
    # a build that fails leaves the output as it was, and the work folder is removed.
    $outputFolder = Join-Path $signRoot 'out'; $workFolder = Join-Path $outputFolder '.build-0123abcd'
    $null = New-TestFile (Join-Path $outputFolder 'old-release.zip') 'old release of another version'
    $null = New-TestFile (Join-Path $outputFolder 'a.zip') 'old'
    $null = New-TestFile (Join-Path $outputFolder 'staged\old.txt') 'old'
    $null = New-TestFile (Join-Path $workFolder 'a.zip') 'new'
    $null = New-TestFile (Join-Path $workFolder 'a.zip.sha256') 'hash'
    $null = New-TestFile (Join-Path $workFolder 'staged\new.txt') 'new'
    Check 'signing: nothing is published unless everything is there' ((Get-Thrown { Publish-Release $workFolder $outputFolder @('a.zip', 'staged', 'b.msi') }) -match 'did not make b\.msi' -and (Get-Content (Join-Path $outputFolder 'a.zip')) -ceq 'old' -and (Test-Path (Join-Path $workFolder 'a.zip')))
    $published = Get-Thrown { Publish-Release $workFolder $outputFolder @('a.zip', 'a.zip.sha256', 'staged') }
    Check 'signing: what a finished build made replaces the same names in the output folder, folders too' (
        $null -eq $published -and (Get-Content (Join-Path $outputFolder 'a.zip')) -ceq 'new' -and (Test-Path (Join-Path $outputFolder 'a.zip.sha256')) -and (Test-Path (Join-Path $outputFolder 'staged\new.txt')) -and
        -not (Test-Path (Join-Path $outputFolder 'staged\old.txt')) -and (Get-Content (Join-Path $outputFolder 'old-release.zip')) -ceq 'old release of another version')
    Check 'signing: ...and nothing of the earlier build is left behind' (@(Get-ChildItem -LiteralPath $outputFolder -Force -Filter '.replaced-*').Count -eq 0)
    # All or nothing: an earlier file that something holds open (it cannot be moved aside), or a new one that cannot move in,
    # publishes nothing, and every earlier file is back where it was.
    foreach ($heldIn in $outputFolder, $workFolder) {
        $null = New-TestFile (Join-Path $outputFolder 'c.zip') 'old c'; $null = New-TestFile (Join-Path $outputFolder 'd.zip') 'old d'
        $null = New-TestFile (Join-Path $workFolder 'c.zip') 'new c'; $null = New-TestFile (Join-Path $workFolder 'd.zip') 'new d'
        $held = [IO.File]::Open((Join-Path $heldIn 'd.zip'), 'Open', 'Read', 'None')
        try { $notPublished = Get-Thrown { Publish-Release $workFolder $outputFolder @('c.zip', 'd.zip') } } finally { $held.Dispose() }
        Check "signing: a release that cannot all move in publishes nothing, the earlier files are back ($(Split-Path -Leaf $heldIn) held)" (
            $notPublished -match 'The release was not published .* are as they were' -and (Get-Content (Join-Path $outputFolder 'c.zip')) -ceq 'old c' -and (Get-Content (Join-Path $outputFolder 'd.zip')) -ceq 'old d' -and
            (Get-Content (Join-Path $workFolder 'c.zip')) -ceq 'new c' -and (Get-Content (Join-Path $workFolder 'd.zip')) -ceq 'new d' -and
            @(Get-ChildItem -LiteralPath $outputFolder -Force -Filter '.replaced-*').Count -eq 0) $notPublished
    }
    # A new folder that cannot move back out stays, and the earlier one of that name stays aside: moved onto it, it would go
    # inside it with no error. The error says where each is.
    $null = New-TestFile (Join-Path $outputFolder 'staged2\old.txt') 'old'; $null = New-TestFile (Join-Path $outputFolder 'e.zip') 'old e'
    $null = New-TestFile (Join-Path $workFolder 'staged2\new.txt') 'new'; $null = New-TestFile (Join-Path $workFolder 'e.zip') 'new e'
    $stuckFolder = Join-Path $outputFolder 'staged2'
    function Move-Item([string] $LiteralPath, [string] $Destination) {
        if ($LiteralPath -eq $stuckFolder -and $Destination -like '*.build-*') { throw 'held' }
        Microsoft.PowerShell.Management\Move-Item -LiteralPath $LiteralPath -Destination $Destination
    }
    $held = [IO.File]::Open((Join-Path $workFolder 'e.zip'), 'Open', 'Read', 'None')
    try { $notPublished = Get-Thrown { Publish-Release $workFolder $outputFolder @('staged2', 'e.zip') } }
    finally { $held.Dispose(); Remove-Item function:Move-Item }
    $asideLeft = @(Get-ChildItem -LiteralPath $outputFolder -Force -Directory -Filter '.replaced-*')
    Check 'signing: a folder that cannot move back out keeps the earlier one aside, not inside it, and the error says where each is' (
        $notPublished -match 'staged2 \(new, in .*staged2 \(earlier, in .*\.replaced-' -and $asideLeft.Count -eq 1 -and (Test-Path (Join-Path $asideLeft[0].FullName 'staged2\old.txt')) -and
        -not (Test-Path (Join-Path $stuckFolder 'staged2')) -and (Test-Path (Join-Path $stuckFolder 'new.txt')) -and (Get-Content (Join-Path $outputFolder 'e.zip')) -ceq 'old e') $notPublished
    foreach ($left in $asideLeft) { Remove-Item -LiteralPath $left.FullName -Recurse -Force }
    $otherFolder = Join-Path $outputFolder 'keep-me'; $null = New-TestFile (Join-Path $otherFolder 'f.txt')
    $madeFolder = New-SigningFolder
    $keptSigning = [pscustomobject]@{ MetadataFolder = $madeFolder }
    Remove-BuildWork $otherFolder ([pscustomobject]@{ MetadataFolder = $otherFolder })
    Check 'signing: only a work folder of this script''s name (and its metadata folder) is ever removed' ((Test-Path $otherFolder) -and (Test-Path $madeFolder))
    Remove-BuildWork $workFolder $keptSigning
    Check 'signing: however the build ended, its work folder and its metadata folder are removed' (-not (Test-Path $workFolder) -and -not (Test-Path $madeFolder) -and (Test-Path (Join-Path $outputFolder 'a.zip')))
    Remove-BuildWork $workFolder $null
    Remove-BuildWork $null $null
    Check 'signing: ...and removing what is already gone is no error' $true
}
finally { $env:TEMP = $tempBefore[0]; $env:TMP = $tempBefore[1]; Remove-Item -LiteralPath $signRoot -Recurse -Force; . $restoreReal }

# The order and the options, read from the scripts: programs first, then the MSI that holds them, then the setups.
$publishText = $buildRelease.IndexOf('dotnet publish (Join-Path $repo ''src\Pulsar.Agent'') @common --self-contained')
$signedPrograms = $buildRelease.IndexOf('Invoke-Signing $programs')
$msiBuilt = $buildRelease.IndexOf("'Build-AgentMsi.ps1'")
$msiSigned = $buildRelease.IndexOf("Invoke-Signing @(`$msi) 'the agent MSI'")
$msiChecksum = $buildRelease.IndexOf('$msiHash = Write-Checksum $msi')
$releaseJson = $buildRelease.IndexOf("Join-Path `$staging 'release.json'")
$recheck = @([regex]::Matches($buildRelease, '(?m)^\s*Assert-ProgramsSigned\s*$') | ForEach-Object { $_.Index })
Check 'signing: the programs are signed after they are published and before the MSI and the setups pack them' (
    $signedPrograms -gt $publishText -and $signedPrograms -lt $msiBuilt -and $signedPrograms -lt $buildRelease.IndexOf("Build-Setup `$iscc 'PulsarAgentSetup.iss'") -and
    $buildRelease -match "foreach \(\`$set in \`$ProgramSets\) \{ \`$programs \+= Get-PulsarPrograms" -and $buildRelease -match "Project = \`$panelProject" -and $buildRelease -match "Project = \`$agentProject")
Check 'signing: the programs are verified again right before the MSI, the agent''s setup, the ZIP and PulsarSetup.exe are made of them' (
    $recheck.Count -ge 5 -and @($recheck | Where-Object { $_ -lt $msiBuilt -and $_ -gt $signedPrograms }).Count -ge 1 -and
    @($recheck | Where-Object { $_ -gt $buildRelease.IndexOf('Compress-Archive') - 400 -and $_ -lt $buildRelease.IndexOf('Compress-Archive') }).Count -ge 1 -and
    $buildRelease -match "Assert-ProgramsSigned\s+\`$agentSetup = Build-Setup" -and $buildRelease -match "Assert-ProgramsSigned\s+\`$setup = Build-Setup")
Check 'signing: the MSI is signed after it is built and before its checksum is written' ($msiSigned -gt $msiBuilt -and $msiSigned -lt $msiChecksum)
Check 'signing: release.json says it is signed only after the programs and the MSI are signed and verified again' (
    $releaseJson -gt $msiSigned -and $buildRelease.LastIndexOf('Assert-ProgramsSigned', $releaseJson) -gt $msiSigned)
Check 'signing: always SHA-256 file and timestamp digests and an RFC 3161 timestamp, verified with /pa' (
    $buildRelease -match "'sign', '/fd', 'sha256', '/tr', \`$TimestampUrl, '/td', 'sha256'" -and $buildRelease -match "'verify', '/pa'" -and $buildRelease -match '-not \$signature\.TimeStamperCertificate')
Check 'signing: the certificate is chosen by thumbprint from the user''s store, or by Trusted Signing''s client; no PFX and no password option' (
    $buildRelease -match "'/sha1', \`$thumbprint, '/s', 'My'" -and $buildRelease -match "'/dlib', \`$dlib, '/dmdf', \`$metadata\.Path" -and $buildRelease -notmatch "'/f'" -and $buildRelease -notmatch "'/p'" -and $buildRelease -notmatch '(?i)\[string\] \$\w*(password|pfx)')
Check 'signing: the Inno Setup compiler''s folder is checked when it is found and again before each compile' (
    $buildRelease -match 'Assert-InnoSetupSigned \$candidate\s+Assert-InnoSetupLocation \$candidate' -and $buildRelease -match 'Assert-SigningTools\s+Assert-InnoSetupLocation \$compiler')
Check 'signing: Azure''s credentials never go through a cmdlet (module logging records parameters and output), only .NET' (
    $buildRelease -notmatch '(?m)^[^#\r\n]*(?<![${])Env:' -and $buildRelease -match '\[Environment\]::SetEnvironmentVariable\(\$name, \$HiddenEnvironment\[\$name\], ''Process''\)')
Check 'signing: signtool runs in place, never from a copy in a folder the user can write in' ($buildRelease -notmatch 'Copy-SignTool|Remove-SignToolCopy|pulsar-signtool' -and $buildRelease -match 'Assert-AdministratorsOnly \(Split-Path -Parent \$Signing\.SignTool\)')
Check 'signing: the setups are signed by Inno Setup (so the uninstaller is) with the one named sign tool, signtool in place, and verified' (
    $buildRelease -match "'/DSignSetups', '/NI', `"/Spulsarsign=\`$\(Get-InnoSignCommand \`$Signing\.SignTool\)`"" -and $buildRelease -match 'Assert-Signed @\(\$setup\)')
$signedBlock = [regex]::Match((Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\release\setup\Common.iss')), '(?s)#ifdef SignSetups(.*?)#endif')
$unsignedPart = (Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\release\setup\Common.iss')).Replace($signedBlock.Value, '')
$signedLines = @($signedBlock.Groups[1].Value -split "`r?`n" | Where-Object { $_.Trim() -and $_ -notmatch '^\s*;' })
Check 'signing: Common.iss names the sign tool, and a signed uninstaller, only when the setups are to be signed' (
    $signedBlock.Success -and ($signedLines -join ',') -ceq 'SignTool=pulsarsign,SignedUninstaller=yes' -and $unsignedPart -notmatch '(?m)^\s*(SignTool|SignedUninstaller)=') ($signedLines -join ',')
Check 'signing: release.json records the signer of a signed build and is unchanged for an unsigned one' (
    $buildRelease -match "if \(\`$Signing\) \{ \`$releaseInfo\['signing'\] = \[ordered\]@\{ signed = \`$true; signer = \`$Signing\.Subject \} \}")
$tryAt = [regex]::Match($buildRelease, '(?m)^try \{\s*\r?\n\s+# Before the long builds').Index
Check 'signing: the build runs in a work folder inside the output folder, and ends however it ends by removing it and giving the credentials back' (
    $tryAt -gt 0 -and $buildRelease -match "\`$Work = Join-Path \`$Output \('\.build-'" -and $buildRelease -match '(?s)\nfinally \{\s+Remove-BuildWork \$Work \$Signing\s+Restore-AzureCredentials\s+Restore-UpdatePassphrase\s+\}\s*\z' -and
    $buildRelease.IndexOf('Initialize-Signing', $tryAt) -gt $tryAt -and $buildRelease -match '"/O\$Work"' -and $buildRelease -notmatch '"/O\$Output"' -and $buildRelease -match '-Output \$Work')
Check 'signing: the release is published into the output folder last, after every signature was verified' (
    $buildRelease.LastIndexOf('Publish-Release $Work $Output') -gt $buildRelease.LastIndexOf('Build-Setup $iscc ''PulsarSetup.iss''') -and
    $buildRelease.LastIndexOf('Publish-Release $Work $Output') -gt $buildRelease.IndexOf('Compress-Archive') -and $buildRelease -notmatch 'Compress-Archive[^\n]*\$Output')
Check 'signing: the builds do not carry on past the signing: no install scripts, no resident MSBuild or compiler server, Azure''s credentials taken out first' (
    $buildRelease -match 'npm ci --ignore-scripts' -and $buildRelease -notmatch 'npm ci\s*\r?\n' -and $buildRelease -match "-nodeReuse:false', '-p:UseSharedCompilation=false" -and $buildRelease -match 'dotnet build-server shutdown' -and
    $buildRelease.IndexOf('dotnet build-server shutdown') -lt $signedPrograms -and $buildRelease.IndexOf('$Signing = Initialize-Signing') -lt $buildRelease.IndexOf('npm ci --ignore-scripts') -and
    $buildRelease -match 'Hide-AzureCredentials\s+Write-Host "Signing with Azure')
# (Windows PowerShell's ConvertFrom-Json refuses this file: it has names that differ only in case.) Each package of the
# lock file starts at its own line, four spaces in.
$webLock = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\..\src\Pulsar.Web\package-lock.json')
$lockEntries = @([regex]::Matches($webLock, '(?m)^    "(node_modules/[^"]+)": \{'))
$withScripts = @(for ($i = 0; $i -lt $lockEntries.Count; $i++) {
        $end = if ($i + 1 -lt $lockEntries.Count) { $lockEntries[$i + 1].Index } else { $webLock.Length }
        if ($webLock.Substring($lockEntries[$i].Index, $end - $lockEntries[$i].Index) -match '"hasInstallScript": true') { $lockEntries[$i].Groups[1].Value }
    })
Check 'signing: no package of the web app needs an install script on Windows (npm ci --ignore-scripts changes nothing there)' (($withScripts -join ',') -ceq 'node_modules/fsevents') ($withScripts -join ',')

# --- The setup wizards (scripts\release\setup, Inno Setup 7; read here, never compiled or run) --------------------------

$setupFolder = Join-Path $PSScriptRoot '..\release\setup'
$commonIss = Get-Content -Raw -LiteralPath (Join-Path $setupFolder 'Common.iss')
$wizardIss = [ordered]@{}
foreach ($name in 'PulsarSetup.iss', 'PulsarAgentSetup.iss') { $wizardIss[$name] = Get-Content -Raw -LiteralPath (Join-Path $setupFolder $name) }

# A section's lines (all its occurrences), without comment lines.
function Get-IssSection([string] $text, [string] $section) {
    $current = ''
    foreach ($line in $text -split "`r?`n") {
        if ($line -match '^\s*\[([A-Za-z]+)\]\s*$') { $current = $Matches[1]; continue }
        if ($current -eq $section -and $line.Trim() -and $line -notmatch '^\s*;') { $line }
    }
}

# The [Code] of a setup (Common.iss's and its own) as blocks, one per function, procedure or external declaration (each
# starts at the line's beginning, as these scripts write them): name -> text, without comment lines.
function Get-IssCode([string[]] $texts) {
    $blocks = [ordered]@{}
    $name = '(top)'
    foreach ($line in @($texts | ForEach-Object { Get-IssSection $_ 'Code' })) {
        if ($line -match '^\s*//') { continue }
        if ($line -match '^(?:function|procedure)\s+(\w+)') { $name = $Matches[1]; $blocks[$name] = '' }
        if (-not $blocks.Contains($name)) { $blocks[$name] = '' }
        $blocks[$name] += $line + "`n"
    }
    return $blocks
}

$setupDirective = @{}
foreach ($line in Get-IssSection $commonIss 'Setup') { if ($line -match '^\s*(\w+)=(.*)$') { $setupDirective[$Matches[1]] = $Matches[2].Trim() } }
foreach ($pair in ([ordered]@{
        PrivilegesRequired = 'admin'; SetupArchitecture = 'x64'; ArchitecturesAllowed = 'x64compatible'; ArchitecturesInstallIn64BitMode = 'x64compatible'
        MinVersion = '10.0.19045'; DefaultDirName = '{commonpf64}\Pulsar\Setup'; DisableDirPage = 'yes'; UsePreviousAppDir = 'no'
        AllowCancelDuringInstall = 'no'; AppPublisher = 'Pulsar contributors'; Compression = 'lzma2/ultra64'; SolidCompression = 'yes'
        AppId = '{{{#AppGuid}}'
    }).GetEnumerator()) {
    Check "setups: $($pair.Key)=$($pair.Value)" ($setupDirective[$pair.Key] -ceq $pair.Value) "$($setupDirective[$pair.Key])"
}
Check 'setups: a fixed AppId GUID, the same in both (one entry in Apps)' ($commonIss -match '#define AppGuid "[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}"' -and @($wizardIss.Values | Where-Object { $_ -match '(?m)^\s*AppId=' }).Count -eq 0)
Check 'setups: English and Spanish, from Inno Setup''s own message files' ((@(Get-IssSection $commonIss 'Languages') -join '|') -match 'Name: "en"; MessagesFile: "compiler:Default\.isl"' -and (@(Get-IssSection $commonIss 'Languages') -join '|') -match 'Name: "es"; MessagesFile: "compiler:Languages\\Spanish\.isl"')

foreach ($name in $wizardIss.Keys) {
    $text = $wizardIss[$name]
    $files = @(Get-IssSection $text 'Files')
    Check "$name includes Common.iss and builds $([IO.Path]::GetFileNameWithoutExtension($name))-<version>.exe" ($text -match '(?m)^#include "Common\.iss"' -and $text -match "(?m)^OutputBaseFilename=$([IO.Path]::GetFileNameWithoutExtension($name))-\{#Version\}$")
    # The payload: in the setup's own folder in Program Files (never {tmp} or a profile), with no permissions added.
    $elsewhere = @($files | Where-Object { $_ -notmatch 'DestDir: "\{app\}\\payload(\\(panel|agent))?"' -or $_ -match '(?i)Permissions:|\{tmp\}|\{user|\{localappdata\}|\{commonappdata\}' })
    Check "${name}: the payload goes to {app}\payload, with Program Files' permissions" ($files.Count -gt 0 -and $elsewhere.Count -eq 0) ($elsewhere -join ' | ')
    $last = $files[$files.Count - 1]
    Check "${name}: Install-Pulsar.ps1 is the last file and runs once it is in place; the rest goes after the install" ($last -match 'Source: "\{#Release\}\\Install-Pulsar\.ps1"' -and $last -match 'AfterInstall: RunPulsarSetup' -and $last -notmatch 'deleteafterinstall' -and
        @($files | Select-Object -First ($files.Count - 1) | Where-Object { $_ -notmatch 'deleteafterinstall' }).Count -eq 0)
    $sections = @('Registry', 'INI', 'Icons', 'Tasks', 'Run' | Where-Object { @(Get-IssSection $text $_).Count -gt 0 -or ($_ -ne 'Run' -and @(Get-IssSection $commonIss $_).Count -gt 0) })
    Check "${name}: no [Registry], [INI], [Icons] or [Tasks] section, and only Common.iss's [Run] entry" ($sections.Count -eq 0) ($sections -join ', ')

    $code = Get-IssCode @($commonIss, $text)
    $all = ($code.Values -join '')
    # Programs run by the setup: one, Windows PowerShell 64-bit (a 64-bit setup's {sys}; {sysnative} is not there for
    # it), with -File and -Unattended.
    $runs = @([regex]::Matches($all, '\b(Exec\w*|ShellExec\w*|CreateProcess\w*|WinExec)\s*\(') | ForEach-Object { $_.Groups[1].Value })
    Check "${name}: the only program it starts is Install-Pulsar.ps1's PowerShell (ExecAndLogOutput)" (($runs -join ',') -ceq 'ExecAndLogOutput') ($runs -join ',')
    Check "${name}: 64-bit Windows PowerShell by full path, -NoProfile -NonInteractive -File, -Unattended" (
        $code['RunInstallScript'] -match "ExecAndLogOutput\(ExpandConstant\('\{sys\}\\WindowsPowerShell\\v1\.0\\powershell\.exe'\)" -and
        $code['RunInstallScript'] -match "'-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' \+ QuoteArgument\(Folder \+ '\\Install-Pulsar\.ps1'\) \+ ' -Unattended' \+ Arguments" -and
        $all -notmatch '\{sysnative\}' -and $all -notmatch '(?i)-Command\b')
    # No secret on the command line: what builds and runs it reads no secret field and no secret.
    $secretPattern = '(?i)password|nodecode|connectionstring|token|secret'
    $leaks = @('InstallArguments', 'RunInstallScript', 'NamedArgument', 'QuoteArgument', 'RunInstallStep' | Where-Object { $code[$_] -and ($code[$_] -replace '^.*\n', '' -replace 'ClearSecretVariables', '') -match $secretPattern })
    Check "${name}: no secret on Install-Pulsar.ps1's command line (its arguments are built from no secret)" ($leaks.Count -eq 0 -and $code.Contains('InstallArguments')) ($leaks -join ', ')
    $fields = @('AdminPage.Values[1]', 'AdminPage.Values[2]', 'PostgresPage.Values[0]', 'CertificatePasswordEdit.Text', 'DatabasePage.Values[0]', 'NodePage.Values[1]')
    $readers = @(foreach ($block in $code.Keys) {
            if ($block -like 'Secret*' -or $block -eq 'ClearSecretFields') { continue }
            foreach ($field in $fields) { if ($code[$block].Contains($field)) { "$block reads $field" } }
        })
    Check "${name}: the secret fields are read only by the Secret* functions (and emptied by ClearSecretFields)" ($readers.Count -eq 0) ($readers -join ', ')
    $users = @(foreach ($block in $code.Keys) {
            if ($block -like 'Secret*' -or $block -like 'Check*' -or $block -in 'PassSecrets', 'HasConnectionString') { continue }
            if ($code[$block] -cmatch '\bSecret[A-Z]\w*') { "$block uses $($Matches[0])" }
        })
    Check "${name}: secrets are used only to check the pages and to pass them on (PassSecrets)" ($users.Count -eq 0) ($users -join ', ')
    $passed = @(($code['PassSecrets'] -split "`n") | Where-Object { $_ -cmatch '\bSecret[A-Z]\w*' })
    $passedNames = @($passed | ForEach-Object { if ($_ -cmatch "^\s*(?:if .* then\s+)?SetSecretVariable\('(PULSAR_[A-Z_]+)', Secret[A-Z]\w*\);\s*$") { $Matches[1] } })
    Check "${name}: PassSecrets hands every secret to the environment (SetSecretVariable) and nowhere else" ($passed.Count -gt 0 -and $passedNames.Count -eq $passed.Count) ($passed -join ' | ')
    Check "${name}: the secrets go in the variables Install-Pulsar.ps1 reads" (@($passedNames | Where-Object { $_ -notin @($SecretInputs.Values) }).Count -eq 0) ($passedNames -join ', ')
    # Never stored: no saved wizard values, nothing written to the registry or to files, no secret logged.
    $stores = @([regex]::Matches($all, '\b(SetPreviousData|RegisterPreviousData|RegWrite\w*|SetIni\w*|SaveStringToFile|SaveStringsTo\w*)\b') | ForEach-Object { $_.Value })
    Check "${name}: nothing is stored (no SetPreviousData, registry writes or files)" ($stores.Count -eq 0) ($stores -join ', ')
    $logged = @(($all -split "`n") | Where-Object { $line = $_; $line -match '\bLog\(' -and ($line -cmatch '\bSecret[A-Z]' -or $line -cmatch '\bLine\b' -or @($fields | Where-Object { $line.Contains($_) }).Count -gt 0) })
    Check "${name}: no secret and no line of the installer's output goes to the setup's log" ($logged.Count -eq 0) ($logged -join ' | ')
    Check "${name}: updates with -Update, asking nothing" ($code['InstallArguments'] -match "if UpdateMode then\s+Result := ' -Update'" -and $code['ShouldSkipPage'] -match 'UpdateMode')
}

$commonCode = Get-IssCode @($commonIss)
Check 'setups: the secrets leave the setup''s environment once Install-Pulsar.ps1 has started, and after it ends' ($commonCode['ScriptOutput'] -match 'if FirstLine then\s+ClearSecretVariables' -and $commonCode['RunInstallScript'] -match 'finally\s+ClearSecretVariables')
$cleared = @([regex]::Matches($commonCode['ClearSecretVariables'], "RemoveEnvironmentVariable\('(PULSAR_[A-Z_]+)', 0\)") | ForEach-Object { $_.Groups[1].Value })
Check 'setups: ClearSecretVariables removes every variable Install-Pulsar.ps1 takes a secret from' (@(@($SecretInputs.Values) + 'PULSAR_SECRETS_FILE' | Where-Object { $_ -notin $cleared }).Count -eq 0) ($cleared -join ', ')
Check 'setups: the payload''s folder is C:\Program Files\Pulsar\Setup and no other (/DIR= refused)' ($commonCode['PrepareToInstall'] -match "PathSame\(ExpandConstant\('\{app\}'\), ExpandConstant\('\{commonpf64\}\\Pulsar\\Setup'\)\)")
Check 'setups: the uninstaller runs Install-Pulsar.ps1 -Uninstall and stops (entry kept) when it fails' ($commonCode['CurUninstallStepChanged'] -match "RunInstallScript\(' -Uninstall'\)" -and $commonCode['CurUninstallStepChanged'] -match 'Abort;')
Check 'setups: nothing changed (2 to 6), or an update put back as it was (7), rolls the setup back; otherwise its entry in Apps stays' (
    $commonCode['RunInstallStep'] -match '\(Code < ExitUsage\) or \(\(Code > ExitCertificate\) and \(Code <> ExitRolledBack\)\)' -and
    $commonCode['RunInstallStep'] -match 'RunKept := True' -and $commonCode['RunInstallStep'] -match 'Abort;' -and
    $commonIss -match '(?m)^\s*ExitRolledBack = 7;' -and $commonCode['OutcomeText'] -match "ExitRolledBack: Result := CustomMessage\('OutcomeRolledBack'\)" -and
    $commonIss -match '(?m)^en\.OutcomeRolledBack=' -and $commonIss -match '(?m)^es\.OutcomeRolledBack=')
Check 'setups: nothing is opened at the end (started elevated, a browser would run as administrator): the Finished page shows https://localhost:<port>/ from PULSAR-PANEL-URL in a read-only box' (
    $commonCode['ScriptOutput'] -match "if IsLocalPanelUrl\(Copy\(Line, 18, Length\(Line\)\)\) then\s+RunPanelUrl := " -and
    @(@($commonIss) + @($wizardIss.Values) | ForEach-Object { @(Get-IssSection $_ 'Run') }).Count -eq 0 -and
    @((@($commonIss) + @($wizardIss.Values) -join "`n") -split "`r?`n" | Where-Object { $_ -notmatch '^\s*(;|//)' -and $_ -match 'runasoriginaluser' }).Count -eq 0 -and
    $commonCode['ShowFinishedText'] -match 'Address\.ReadOnly := True;' -and $commonCode['ShowFinishedText'] -match 'Address\.Text := RunPanelUrl;')
Check 'setups: one setup at a time in this session, with no Global\ name another account could take first' ($setupDirective['SetupMutex'] -ceq 'PulsarSetup')
Check 'setups: a newer Pulsar is found by the version a setup recorded and by the installed programs'' own' (
    $commonCode['NewerVersionInstalled'] -match "'DisplayVersion'" -and
    $commonCode['NewerVersionInstalled'] -match "NewerProgramInstalled\(ExpandConstant\('\{commonpf64\}\\Pulsar\\Panel\\Pulsar\.Api\.exe'\)" -and
    $commonCode['NewerVersionInstalled'] -match "NewerProgramInstalled\(ExpandConstant\('\{commonpf64\}\\Pulsar\\Agent\\PulsarAgent\.exe'\)" -and
    $commonCode['NewerProgramInstalled'] -match 'GetPackedVersion\(Path, Version\)' -and $commonCode['NewerProgramInstalled'] -match 'ComparePackedVersion\(Version, Current\) > 0')
foreach ($case in @(@('https://localhost:8443/', $true), @('https://localhost:65535/', $true), @('https://localhost:8443', $false), @('https://evil.example/', $false), @('https://localhost:8443/x', $false), @('https://localhost:/', $false), @('https://localhost:99999/', $false), @('https://localhost:84a3/', $false))) {
    # The same rule, in PowerShell: the Pascal function cannot run here.
    $url = $case[0]
    $ok = $url.Length -ge 20 -and $url.Length -le 24 -and $url.StartsWith('https://localhost:', [StringComparison]::Ordinal) -and $url.EndsWith('/') -and $url.Substring(18, $url.Length - 19) -match '^\d+$' -and [int]$url.Substring(18, $url.Length - 19) -le 65535
    Check "setups: IsLocalPanelUrl's rule $(if ($case[1]) { 'takes' } else { 'refuses' }) $url" ($ok -eq $case[1])
}
Check 'setups: IsLocalPanelUrl is that rule' ($commonCode['IsLocalPanelUrl'] -match "\(Length\(Url\) < 20\) or \(Length\(Url\) > 24\) then\s+Exit;\s+if \(Copy\(Url, 1, 18\) <> 'https://localhost:'\) or \(Url\[Length\(Url\)\] <> '/'\)" -and $commonCode['IsLocalPanelUrl'] -match '<= 65535')
# The panel's address a node page takes (NormalizePanelAddress): put in the one form Install-Pulsar.ps1 takes, or refused
# there with the page's message. The same rule, in PowerShell (the Pascal function cannot run here), and each address it
# gives must pass the script's own Test-PanelUrl.
function ConvertTo-WizardAddress([string] $address) {
    if ($address.Length -le 8 -or $address.Length -gt 512 -or -not $address.Substring(0, 8).Equals('https://', [StringComparison]::OrdinalIgnoreCase)) { return $null }
    $rest = $address.Substring(8)
    if ($rest.EndsWith('/')) { $rest = $rest.Substring(0, $rest.Length - 1) }
    if ($rest.StartsWith('[')) {
        $close = $rest.IndexOf(']') + 1
        if ($close -lt 3) { return $null }
        $hostPart = $rest.Substring(0, $close); $portPart = $rest.Substring($close)
        if ($hostPart.Substring(1, $hostPart.Length - 2) -cnotmatch '^[0-9a-fA-F:.]*\z') { return $null }
    }
    else {
        $colon = $rest.IndexOf(':')
        if ($colon -lt 0) { $colon = $rest.Length }
        $hostPart = $rest.Substring(0, $colon); $portPart = $rest.Substring($colon)
        if (-not $hostPart -or $hostPart.StartsWith('.') -or $hostPart.Contains('..') -or $hostPart -cnotmatch '^[0-9a-zA-Z._-]+\z') { return $null }
    }
    if ($portPart) {
        if ($portPart.Length -lt 2 -or $portPart.Length -gt 6 -or $portPart[0] -ne ':' -or $portPart[1] -eq '0' -or $portPart.Substring(1) -cnotmatch '^[0-9]+\z') { return $null }
        $number = [int]$portPart.Substring(1)
        if ($number -lt 1 -or $number -gt 65535 -or $number -eq 443) { return $null }
    }
    return 'https://' + $hostPart.ToLowerInvariant() + $portPart
}
foreach ($case in @(@('https://panel.example.com:8443', 'https://panel.example.com:8443'), @('HTTPS://Panel.Example.COM:8443/', 'https://panel.example.com:8443'),
        @('https://PANEL', 'https://panel'), @('https://10.0.0.5:9443/', 'https://10.0.0.5:9443'), @('https://[FD00::5]:8443', 'https://[fd00::5]:8443'), @('https://my_box-1.lan', 'https://my_box-1.lan'))) {
    $normal = ConvertTo-WizardAddress $case[0]
    Check "setups: the panel's address $($case[0]) is taken as $($case[1]), which Install-Pulsar.ps1 takes" ($normal -ceq $case[1] -and (Test-PanelUrl $normal)) "$normal"
}
foreach ($bad in 'https://panel.example.com/pulsar', 'https://panel.example.com//', 'https://panel.example.com?x=1', 'https://panel.example.com#x', 'https://admin@panel.example.com',
    'https://admin:pw@panel.example.com:8443', 'https://panel.example.com:443', 'https://panel.example.com:', 'https://panel.example.com:08443', 'https://panel.example.com:65536',
    'https://panel.example.com:84a3', 'http://panel.example.com', 'https://', 'https:///', 'https://pa nel', 'https://a..b', 'https://.panel', 'https://[]:8443', 'https://[fd00::5', 'ftp://panel') {
    Check "setups: the panel's address $bad is refused" ($null -eq (ConvertTo-WizardAddress $bad))
}
$normalize = $commonCode['NormalizePanelAddress']
Check 'setups: NormalizePanelAddress is that rule' ($null -ne $normalize -and $normalize -match "SameText\(Copy\(Address, 1, 8\), 'https://'\)" -and
    $normalize -match "if Rest\[Length\(Rest\)\] = '/' then" -and $normalize -match "\(PortText\[2\] = '0'\)" -and $normalize -match '\(Number = 443\)' -and
    $normalize -match "Normal := 'https://' \+ Lowercase\(Host\) \+ PortText;" -and $normalize -match "\(Pos\('\.\.', Host\) > 0\)" -and -not $commonCode.Contains('IsPanelAddress'))
foreach ($name in $wizardIss.Keys) {
    $nodeCheck = (Get-IssCode @($commonIss, $wizardIss[$name]))['CheckNodePage']
    Check "${name}: the node page puts the address in that form before passing it on, and refuses another" ($nodeCheck -match 'if not NormalizePanelAddress\(PanelAddress, Address\) then begin\s+Result := Refuse\(CustomMessage\(''ErrorAddress''\)\);' -and $nodeCheck -match 'NodePage\.Values\[0\] := Address;')
}
# The certificate's fingerprint on the Finished page: from PULSAR-CERTIFICATE, only when it is a SHA-256, read-only.
Check 'setups: the Finished page shows the fingerprint PULSAR-CERTIFICATE gives, only a lowercase SHA-256, read-only, next to the address' (
    $commonCode['ScriptOutput'] -match "Copy\(Line, 1, 19\) = 'PULSAR-CERTIFICATE ' then begin\s+if IsCertificateHash\(Copy\(Line, 20, Length\(Line\)\)\) then\s+RunCertificate := Copy\(Line, 20, Length\(Line\)\);" -and
    $commonCode['RunInstallScript'] -match "RunCertificate := '';" -and
    $commonCode['IsCertificateHash'] -match 'Length\(Hash\) <> 64' -and $commonCode['IsCertificateHash'] -match "\(Hash\[I\] >= 'a'\) and \(Hash\[I\] <= 'f'\)" -and $commonCode['IsCertificateHash'] -cnotmatch "'A'" -and
    $commonCode['ShowFinishedText'] -match 'if RunCertificate <> '''' then begin' -and $commonCode['ShowFinishedText'] -match 'Fingerprint\.ReadOnly := True;' -and
    $commonCode['ShowFinishedText'] -match 'Fingerprint\.Text := FormatFingerprint\(RunCertificate\);' -and $commonCode['ShowFinishedText'] -match "CustomMessage\('FinishedFingerprint'\)" -and
    $commonCode['ShowFinishedText'].IndexOf('if RunCertificate') -gt $commonCode['ShowFinishedText'].IndexOf('if HasPanelUrl then begin'))
# FormatFingerprint's rule, in PowerShell: upper case, ':' between each two digits, as browsers show fingerprints.
$hash = '0123456789abcdef' * 4
$shown = (@([regex]::Matches($hash.ToUpperInvariant(), '..') | ForEach-Object { $_.Value })) -join ':'
Check 'setups: FormatFingerprint writes the hash in upper case, a colon between each two digits' ($shown.Length -eq 95 -and $shown.StartsWith('01:23:45') -and
    $commonCode['FormatFingerprint'] -match 'Upper := Uppercase\(Hash\);' -and $commonCode['FormatFingerprint'] -match "if \(I > 1\) and \(I mod 2 = 1\) then\s+Result := Result \+ ':';")
# The Advanced page's extra name or address (IsHostName), as -HostName takes it. The same rule, in PowerShell (the Pascal
# functions cannot run here); what it takes, Install-Pulsar.ps1 takes too.
function Test-WizardHostName([string] $name) {
    if ($name.Contains(':')) { return $name.Length -ge 2 -and $name.Length -le 45 -and $name -cmatch '^[0-9A-Fa-f:.]+\z' -and ([regex]::Matches($name, ':')).Count -ge 2 }
    if ($name -cmatch '^(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])(\.(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])){3}\z') { return $true }
    if ($name.StartsWith('*.')) { $name = $name.Substring(2) }
    if ($name.EndsWith('.')) { $name = $name.Substring(0, $name.Length - 1) }
    if (-not $name -or $name.Length -gt 253 -or $name -cnotmatch '^[A-Za-z0-9_-]+(\.[A-Za-z0-9_-]+)*\z') { return $false }
    $last = $name.Split('.')[-1].ToLowerInvariant()
    if ($last.StartsWith('0x')) { return $last.Substring(2) -cnotmatch '^[0-9a-f]*\z' }
    return $last -cnotmatch '^[0-9]+\z'
}
foreach ($good in 'panel.example.com', 'PANEL', '*.example.com', 'panel.example.com.', 'my_box-1.lan', '192.168.1.20', '203.0.113.7', '2001:db8::7', 'fd00::5', 'face', 'box.1e') {
    Check "setups: the extra name or address $good is taken, as Install-Pulsar.ps1 takes it" ((Test-WizardHostName $good) -and [regex]::IsMatch($good, $hostNamePattern, 'None') -and $null -eq (Get-Thrown { ConvertTo-CertificateAddress $good }))
}
foreach ($bad in '10.1', '192.168.1.300', '1.2.3.4.5', '01.2.3.4', '0x7f', 'box.0x10', '*.1', '[::1]', 'fe80::1%3', '1:2', 'pa nel', '', 'a..b', 'panel.example.com:8443') {
    Check "setups: the extra name or address $bad is refused" (-not (Test-WizardHostName $bad))
}
$hostCheck = (Get-IssCode @($commonIss, $wizardIss['PulsarSetup.iss']))
Check 'setups: IsHostName is that rule' ($hostCheck['IsHostName'] -match 'if Pos\('':'', Name\) > 0 then begin\s+Result := IsIPv6Address\(Name\);' -and $hostCheck['IsHostName'] -match 'if IsIPv4Address\(Name\) then begin\s+Result := True;' -and
    $hostCheck['IsHostName'] -match "Hex := Copy\(Last, 1, 2\) = '0x';" -and $hostCheck['IsIPv4Address'] -match '\(Digits = 1\) and \(Value = 0\)' -and $hostCheck['IsIPv4Address'] -match '\(Digits > 3\) or \(Value > 255\)' -and
    $hostCheck['IsIPv4Address'] -match 'Result := \(Parts = 4\) and \(Digits > 0\);' -and $hostCheck['IsIPv6Address'] -match 'Result := Colons >= 2;' -and $hostCheck['CheckNetworkPage'] -match 'not IsHostName\(HostNameValue\)')

# Every message the code uses is there in English and Spanish.
$allIss = @($commonIss) + @($wizardIss.Values)
$defined = @{}
foreach ($line in @($allIss | ForEach-Object { @(Get-IssSection $_ 'CustomMessages') })) { if ($line -match '^(en|es)\.(\w+)=') { $defined["$($Matches[1]).$($Matches[2])"] = $true } }
$used = @(@($allIss | ForEach-Object { [regex]::Matches($_, "CustomMessage\('(\w+)'\)|\{cm:(\w+)\}") | ForEach-Object { if ($_.Groups[1].Success) { $_.Groups[1].Value } else { $_.Groups[2].Value } } }) | Select-Object -Unique)
$untranslated = @($used | Where-Object { -not $defined["en.$_"] -or -not $defined["es.$_"] })
$onlyOne = @($defined.Keys | Where-Object { $other = if ($_ -like 'en.*') { 'es.' + $_.Substring(3) } else { 'en.' + $_.Substring(3) }; -not $defined[$other] })
Check 'setups: every message the code uses is defined in English and Spanish' ($used.Count -gt 20 -and $untranslated.Count -eq 0 -and $onlyOne.Count -eq 0) "missing: $($untranslated -join ', '); one language only: $($onlyOne -join ', ')"

# --- -RenewCertificate (Phase 18): a new certificate for an address that changed ----------------------------------------
# Its parameters, what it checks before anything changes, the swap with the old files kept, the rollback, and what it tells
# the administrator. Folders under -Scratch; no service, certificate store, firewall rule or account of this PC is touched
# (they are mocked here: the functions that start and stop the panel, trust a certificate and probe its ports).

# Parameter sets, as PowerShell reads the script's param block: -RenewCertificate cannot be combined with another mode.
$renewCommand = Get-Command -Name (Resolve-Path $installScript).ProviderPath
$renewSets = @{}
foreach ($set in $renewCommand.ParameterSets) { $renewSets[$set.Name] = @($set.Parameters | ForEach-Object Name) }
$renewMandatory = @(@($renewCommand.ParameterSets | Where-Object Name -eq 'RenewCertificate')[0].Parameters | Where-Object IsMandatory | ForEach-Object Name)
Check 'renew: -RenewCertificate is mandatory in a parameter set of its own, and the install stays the default' (
    (($renewMandatory -join ',') -ceq 'RenewCertificate') -and @($renewSets.Keys | Where-Object { $renewSets[$_] -contains 'RenewCertificate' }).Count -eq 1 -and
    @($renewCommand.ParameterSets | Where-Object IsDefault).Name -ceq 'Install')
foreach ($mode in 'Update', 'Uninstall', 'RenewCertificate') {
    $holding = @($renewSets.Keys | Where-Object { $renewSets[$_] -contains $mode })
    Check "renew: -$mode is in one parameter set, its own, so no other mode combines with it" ($holding.Count -eq 1 -and $holding[0] -ceq $mode) ($holding -join ',')
}
foreach ($installOnly in 'Mode', 'PublicIp') {
    Check "renew: -$installOnly (an install's) is in no set with -RenewCertificate, -Update or -Uninstall" (@($renewSets.Keys | Where-Object { $renewSets[$_] -contains $installOnly }).Count -eq 1 -and
        $renewSets['Install'] -contains $installOnly)
}
Check 'renew: -HostName, -Unattended, -InstallRoot and -DataRoot work with it' (@('HostName', 'Unattended', 'InstallRoot', 'DataRoot' | Where-Object { $renewSets['RenewCertificate'] -notcontains $_ }).Count -eq 0)
$renewSetupCode = (Get-ScriptParts $installScript @('Get-StepPlan'))['Get-StepPlan']
Check 'renew: its progress steps are the plan''s own (check, certificate, panel, trust)' (((@($StepPlans['RenewCertificate'])) -join ',') -ceq 'check,certificate,panel,trust' -and
    $renewSetupCode -match "if \(\`$RenewCertificate\) \{ return \`$StepPlans\['RenewCertificate'\] \}")

# Everything else on the command line is refused, not ignored.
function Test-RenewArguments([string[]] $given) {
    $SetupParameters = @{}
    foreach ($name in $given) { $SetupParameters[$name] = $true }
    Get-Caught { Assert-RenewArguments }
}
Check 'renew: -HostName, -Unattended, the folders and the common parameters are taken' ($null -eq (Test-RenewArguments @('RenewCertificate', 'HostName', 'Unattended', 'InstallRoot', 'DataRoot', 'Verbose', 'ErrorAction', 'InformationAction')))
# (-Mode and -PublicIp, -Update and -Uninstall never get this far: PowerShell refuses the command line, see the parameter sets above.)
foreach ($parameter in 'Port', 'CertificatePath', 'CertificatePassword', 'RestoreFrom', 'AgentsPort', 'SecretsFile', 'NodeCode', 'TrustCertificate', 'NoFirewall', 'ConnectionString') {
    $caught = Test-RenewArguments @('RenewCertificate', 'HostName', $parameter)
    Check "renew: -$parameter is refused (exit code 2, nothing changed)" ($null -ne $caught -and (Get-SetupExitCode $caught) -eq 2 -and $caught.Exception.Message -match [regex]::Escape("-$parameter") -and $caught.Exception.Message -match 'Nothing was changed') "$caught"
}

# The names the old certificate lists and the new one does not.
function Names([string[]] $dns, [string[]] $ips) { [pscustomobject]@{ Dns = $dns; Ips = @($ips | ForEach-Object { [Net.IPAddress]::Parse($_) }); Others = @() } }
$gone = Compare-CertificateNames (Names @('localhost', 'PANEL', 'OLDBOX', 'panel.example.com') @('127.0.0.1', '::1', '192.168.0.54', '10.0.0.7', '203.0.113.7', '2001:db8::7')) (Names @('localhost', 'PANEL', 'NEWBOX') @('127.0.0.1', '::1', '10.0.0.7', '192.168.0.77'))
Check 'renew names: an address or name this PC may have had (a private address, a single label) is stale; a DNS name, a public address are not' (
    ($gone.Stale -join ',') -ceq 'OLDBOX,192.168.0.54' -and ($gone.Other -join ',') -ceq 'panel.example.com,203.0.113.7,2001:db8::7') "$($gone.Stale -join ',') | $($gone.Other -join ',')"
$gone = Compare-CertificateNames (Names @('Panel', 'panel.example.com.') @('127.0.0.1')) (Names @('PANEL', 'PANEL.EXAMPLE.COM') @('127.0.0.1'))
Check 'renew names: compared ignoring case and a trailing dot; nothing gone is nothing said' ($gone.Stale.Count -eq 0 -and $gone.Other.Count -eq 0)
$gone = Compare-CertificateNames (Names @('OLDBOX.corp.example', 'panel.example.com', 'x.notcorp.example') @()) (Names @('NEWBOX.corp.example') @()) 'corp.example'
Check 'renew names: a name under the domain this PC is joined to (its old full name) is stale; another is not' (($gone.Stale -join ',') -ceq 'OLDBOX.corp.example' -and ($gone.Other -join ',') -ceq 'panel.example.com,x.notcorp.example') "$($gone.Stale -join ',') | $($gone.Other -join ',')"
$gone = Compare-CertificateNames (Names @("evil$esc[1A.example.com") @()) (Names @() @())
Check 'renew names: what the old certificate lists is printed without control characters' (@($gone.Other).Count -eq 1 -and $gone.Other[0] -notmatch '[\x00-\x1F\x7F]')

# Whose trust the old certificate has (the mark Add-TrustedCertificate gives), and what is removed.
$trustLog = New-Object Collections.Generic.List[string]
function Test-InstallerTrust([string] $state, [scriptblock] $block) {
    & {
        function Test-Path { param($LiteralPath) $state -ne 'none' }
        function Get-Item { param($LiteralPath) [pscustomobject]@{ FriendlyName = $(if ($state -eq 'marked') { $TrustedName } elseif ($state -eq 'unmarked') { 'Some friendly name' } else { '' }) } }
        function Remove-Item { param($LiteralPath) $trustLog.Add($LiteralPath) }
        & $block
    }
}
$thumbprint = 'ABCDEF0123456789ABCDEF0123456789ABCDEF01'
foreach ($state in 'none', 'marked', 'unmarked') {
    Check "renew trust: a root certificate that is $state is $state" ((Test-InstallerTrust $state { Get-InstallerTrust $thumbprint }) -ceq $state)
}
$trustLog.Clear()
Test-InstallerTrust 'marked' { Remove-InstallerTrust $thumbprint }
Check 'renew trust: the one this script trusted (marked) is removed, by its thumbprint' ($trustLog.Count -eq 1 -and $trustLog[0] -ceq "Cert:\LocalMachine\Root\$thumbprint")
$trustLog.Clear()
Test-InstallerTrust 'unmarked' { Remove-InstallerTrust $thumbprint }
Test-InstallerTrust 'none' { Remove-InstallerTrust $thumbprint }
Test-InstallerTrust 'none' { Remove-InstallerTrust $thumbprint $true }
Check 'renew trust: one someone else trusted, or none, is never removed' ($trustLog.Count -eq 0)
Test-InstallerTrust 'unmarked' { Remove-InstallerTrust $thumbprint $true }
Check 'renew trust: one this run has just added and not marked yet is removed when it is undone' ($trustLog.Count -eq 1)
foreach ($bad in '*', '', "$thumbprint*", 'Cert:\LocalMachine\Root\*', ('A' * 39)) {
    Check "renew trust: '$bad' is not a thumbprint, so nothing is looked up or removed" ($null -ne (Get-Thrown { Test-InstallerTrust 'marked' { Get-InstallerTrust $bad } }) -and $null -ne (Get-Thrown { Test-InstallerTrust 'marked' { Remove-InstallerTrust $bad } }))
}

# A panel installed in a scratch folder: its service's program, its settings, its certificate (as -Mode AllInOne made it).
# The scratch root is private, as the panel's data folder is (SYSTEM, Administrators and this user, protected; what is made in it
# inherits only those): the check that a folder lets nobody else in (Assert-PrivateCertificateFolder) would refuse the temporary
# folder's inherited entries. (Changing a protected folder's permissions with Set-Acl asks for a privilege a user without
# elevation lacks; the folders under test, like the panel's tls folder, inherit theirs.)
$rnRoot = Join-Path $Scratch ('pulsar-renew-tests-' + [Guid]::NewGuid().ToString('N'))
$rnRootSecurity = New-Object Security.AccessControl.DirectorySecurity
$rnRootSecurity.SetAccessRuleProtection($true, $false)
foreach ($sid in 'S-1-5-18', 'S-1-5-32-544', [Security.Principal.WindowsIdentity]::GetCurrent().User.Value) {
    $rnRootSecurity.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier $sid), 'FullControl', 'ContainerInherit, ObjectInherit', 'None', 'Allow')))
}
$null = [IO.Directory]::CreateDirectory($rnRoot, $rnRootSecurity)
$rnPassword = 'the-old-pfx-password-0123456789'
$rnAgentLock = $null
try {
    function New-IssuedPfx([string] $path, [string] $password) {
        $authorityKey = [Security.Cryptography.RSACng]::new(2048)
        $leafKey = [Security.Cryptography.RSACng]::new(2048)
        try {
            $authorityRequest = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=Test Authority', $authorityKey, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
            $authorityRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true, $false, 0, $true))
            $authority = $authorityRequest.CreateSelfSigned([DateTimeOffset]::Now.AddDays(-1), [DateTimeOffset]::Now.AddDays(30))
            $leafRequest = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=localhost', $leafKey, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
            $issued = $leafRequest.Create($authority, [DateTimeOffset]::Now.AddDays(-1), [DateTimeOffset]::Now.AddDays(10), [byte[]](1, 2, 3, 4, 5, 6, 7, 8))
            $withKey = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::CopyWithPrivateKey($issued, $leafKey)
            [IO.File]::WriteAllBytes($path, $withKey.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $password))
        }
        finally { $authorityKey.Dispose(); $leafKey.Dispose() }
    }
    # The install's permissions for the panel's folder (Set-PrivateAcl): SYSTEM, Administrators and the panel's account (a stand-in SID
    # here, Modify), inherited by the files. Here the first two come from the private scratch root; the account is an explicit entry.
    $rnPanelSid = 'S-1-5-80-1111111111-2222222222-3333333333-1234567890-987654321'
    $PanelAccount = 'NT SERVICE\PulsarPanel'
    function Set-PanelLikeAcl([string] $folder, [string[]] $extra = @()) {
        $acl = Get-Acl -LiteralPath $folder
        foreach ($sid in @($rnPanelSid) + $extra) {
            $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier $sid), 'Modify', 'ContainerInherit, ObjectInherit', 'None', 'Allow')))
        }
        Set-Acl -LiteralPath $folder -AclObject $acl
    }    # -Subject: another subject than the install's; -Issued: a certificate an authority issued; -Settings: the settings' text,
    # whatever it is; -Password: the one in the settings; -CertificateFile: where the settings say the PFX is.
    function New-RenewInstall([string] $name, [hashtable] $options = @{}) {
        $base = Join-Path $rnRoot $name
        $panelDir = Join-Path $base 'pf\Panel'
        $panelData = Join-Path $base 'data\Panel'
        $tls = Join-Path $panelData 'tls'
        $null = New-Item -ItemType Directory -Force $panelDir, $tls, (Join-Path $base 'AgentData\config')
        Set-Content -LiteralPath (Join-Path $panelDir 'Pulsar.Api.exe') 'x'
        $pfx = Join-Path $tls 'panel.pfx'
        $cer = Join-Path $tls 'panel.cer'
        if ($options['Issued']) { New-IssuedPfx $pfx $rnPassword; $certificate = New-Object Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList @([IO.File]::ReadAllBytes($pfx), $rnPassword, [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet) }
        else {
            $listed = Get-PanelCertificateNames @('localhost', 'OLDBOX') @([Net.IPAddress]::Parse('192.168.0.54'), [Net.IPAddress]::Parse('10.0.0.7')) @($options['ExtraName'])
            $certificate = New-SelfSignedPanelCertificate $listed
            if ($options['Subject']) {
                $rsa = [Security.Cryptography.RSACng]::new(2048)
                $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new($options['Subject'], $rsa, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
                $certificate = $request.CreateSelfSigned([DateTimeOffset]::Now.AddDays(-1), [DateTimeOffset]::Now.AddDays(30))
            }
            [IO.File]::WriteAllBytes($pfx, $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $rnPassword))
        }
        [IO.File]::WriteAllBytes($cer, $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
        $thumbprint = $certificate.Thumbprint
        $sha256 = Get-CertificateHash $certificate
        $certificate.Dispose()
        $password = $rnPassword
        if ($options.ContainsKey('Password')) { $password = $options['Password'] }
        $file = $pfx
        if ($options['CertificateFile']) { $file = $options['CertificateFile'] }
        $endpoints = @{ Https = @{ Url = 'https://*:8443'; Certificate = @{ Path = $file; Password = $password } } }
        if ($options['Agents']) { $endpoints.Agents = $options['Agents'] }
        $config = Join-Path $panelDir 'appsettings.Production.json'
        if ($options.ContainsKey('Settings')) { Set-Content -LiteralPath $config -Value $options['Settings'] -Encoding UTF8 }
        elseif (-not $options['NoSettings']) {
            @{ ConnectionStrings = @{ Pulsar = 'Host=localhost;Database=pulsar;Username=pulsar;Password=a-database-password' }; Kestrel = @{ Endpoints = $endpoints } } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $config -Encoding UTF8
        }
        Set-PanelLikeAcl $tls
        [pscustomobject]@{ Base = $base; PanelDir = $panelDir; PanelData = $panelData; Tls = $tls; Pfx = $pfx; Cer = $cer; Config = $config; Exe = (Join-Path $panelDir 'Pulsar.Api.exe')
            Thumbprint = $thumbprint; Sha256 = $sha256 }
    }

    # Runs Invoke-RenewCertificate (or Get-RenewableCertificate with -Only) on an install, with the panel's service, the trust
    # and the probes of its ports mocked, and keeps what it printed ($rnOutput), the mocked calls ($rnCalls) and its error ($rnError).
    function Invoke-Renewal($install, [hashtable] $options = @{}, [switch] $Only) {
        $script:rnCalls = New-Object Collections.Generic.List[string]
        $script:rnError = $null
        $script:rnResult = $null
        $script:rnOutput = @(& {
                $WarningPreference = 'Continue'
                $PanelService = 'PulsarPanel'; $PanelAccount = 'NT SERVICE\PulsarPanel'; $AgentService = 'PulsarAgent'; $PanelDir = $install.PanelDir; $PanelData = $install.PanelData
                $PanelConfig = $install.Config; $AgentDir = Join-Path $install.Base 'Agent'; $AgentData = Join-Path $install.Base 'AgentData'
                $HostName = @()
                if ($options.ContainsKey('HostName')) { $HostName = @($options['HostName']) }
                $Unattended = [bool]$options['Unattended']
                $SetupParameters = @{ RenewCertificate = $true }
                $StepPlan = $StepPlans['RenewCertificate']
                $trustState = 'marked'
                if ($options.ContainsKey('Trust')) { $trustState = $options['Trust'] }
                $program = "`"$($install.Exe)`""
                if ($options['Program']) { $program = $options['Program'] }
                function Get-CimInstance {
                    if ($args[0] -eq 'Win32_ComputerSystem') { return [pscustomobject]@{ Domain = 'WORKGROUP' } }
                    if ($options['NoService']) { return $null }
                    [pscustomobject]@{ PathName = $program }
                }
                function Get-LanAddresses { [Net.IPAddress]::Parse('192.168.0.77'); [Net.IPAddress]::Parse('10.0.0.7') }
                function Get-DhcpAddresses { '192.168.0.77' }
                function Get-Service { [pscustomobject]@{ Status = $(if ($options['Stopped']) { 'Stopped' } else { 'Running' }) } }
                function Get-InstallerTrust([string] $thumbprint) { if ($thumbprint -ceq $install.Thumbprint) { $trustState } else { 'none' } }
                function Remove-InstallerTrust([string] $thumbprint, [bool] $added = $false) { $script:rnCalls.Add("untrust $thumbprint $added") }
                function Add-TrustedCertificate([string] $cer, [string] $hostName, [string[]] $panelNames = @(), [string] $sha256 = '') {
                    $script:rnCalls.Add("trust $cer $hostName $sha256")
                    if ($options['TrustFails']) { throw 'The certificate store refused it (a test).' }
                }
                function Stop-Panel {
                    $script:rnCalls.Add('stop')
                    # The panel's service account would swap the certificate's folder for a link as the panel stops: it cannot, the
                    # folder is held (the same process opens it here, and the hold refuses to share it for deleting).
                    if ($options['RenameWhileStopped']) {
                        try {
                            Move-Item -LiteralPath $install.Tls "$($install.Tls)-real"
                            $script:rnCalls.Add('renamed')
                            Move-Item -LiteralPath "$($install.Tls)-real" $install.Tls
                        }
                        catch { $script:rnCalls.Add('rename-blocked') }
                    }
                }
                function Start-Panel { $script:rnCalls.Add('start') }
                function Confirm-PanelCertificate([int] $port, [int] $agentsPort, $certificate) {
                    $script:rnCalls.Add("confirm $($certificate.Thumbprint) $port $agentsPort")
                    $times = @($script:rnCalls | Where-Object { $_ -like 'confirm *' }).Count
                    if ($options['ConfirmFails'] -ceq 'always' -or ($options['ConfirmFails'] -ceq 'first' -and $times -eq 1)) { throw 'The panel did not answer on port 8443 (a test).' }
                }
                function Get-LocalNodePin { $options['LocalPin'] }
                # What the folder's permissions are while the files are swapped, and whether a process still runs as the panel's account.
                function Get-PanelAccountProcesses { $script:rnLockedAcl = Get-FileAccess $install.Tls; if ($options['Lingering']) { 'panel (1234)' } }
                function Start-Sleep { }
                function Get-PanelAccountSid { New-Object Security.Principal.SecurityIdentifier $rnPanelSid }
                function Write-Step([string] $message) { Write-Host "==> $message" }
                function Write-SetupStep([string] $key, [string] $text) { Write-Host (Format-SetupStep $key $text $StepPlan) }
                if ($options['StagedCheckFails']) { function Assert-NewCertificateFiles { throw 'The new certificate''s PFX cannot be the panel''s: a test.' } }
                try {
                    if ($Only) { $script:rnResult = Get-RenewableCertificate } else { Invoke-RenewCertificate }
                }
                catch { $script:rnError = $_ }
            } 6>&1 3>&1 | ForEach-Object { "$_" })
    }
    function Get-FilesIn($install) { @(Get-ChildItem -LiteralPath $install.Tls -Force | ForEach-Object Name | Sort-Object) -join ',' }
    function Get-Digests($install) { "$((Get-FileHash -LiteralPath $install.Pfx).Hash) $((Get-FileHash -LiteralPath $install.Cer).Hash) $(Get-FileAccess $install.Pfx) $(Get-FileAccess $install.Cer) $(Get-FileAccess $install.Tls)" }

    # --- What it checks before anything changes ---
    $install = New-RenewInstall 'ok'
    Invoke-Renewal $install @{} -Only
    $found = $script:rnResult
    Check 'renew check: an installed panel with its self-signed certificate passes, and says what it needs' (
        $null -eq $script:rnError -and $found.Port -eq 8443 -and $found.AgentsPort -eq 0 -and $found.Thumbprint -ceq $install.Thumbprint -and $found.Sha256 -ceq $install.Sha256 -and
        $found.Password -ceq $rnPassword -and $found.Trust -ceq 'marked' -and ($found.Names.Dns -join ',') -ceq 'localhost,OLDBOX' -and $found.Pfx -ceq $install.Pfx -and $found.Cer -ceq $install.Cer) "$($script:rnError)"
    Check 'renew check: the certificate''s password is a secret of the run (never in the log)' ((Protect-SetupText "x $rnPassword y") -ceq 'x [secret] y')
    $SetupSecrets.Clear()
    $withAgents = New-RenewInstall 'agents' @{ Agents = @{ Url = 'https://*:8444'; Certificate = @{ Path = (Join-Path $rnRoot 'agents\data\Panel\tls\panel.pfx'); Password = $rnPassword } } }
    Invoke-Renewal $withAgents @{} -Only
    Check 'renew check: the agents'' port is read, and uses the same certificate' ($null -eq $script:rnError -and $script:rnResult.AgentsPort -eq 8444) "$($script:rnError)"
    $SetupSecrets.Clear()

    function Test-Refusal([string] $name, [hashtable] $installOptions, [hashtable] $runOptions, [int] $code, [string] $pattern) {
        $subject = New-RenewInstall $name $installOptions
        if ($installOptions['Debris']) { Set-Content -LiteralPath (Join-Path $subject.Tls $installOptions['Debris']) 'x' }
        Invoke-Renewal $subject $runOptions -Only
        $SetupSecrets.Clear()
        Check "renew check: $name is refused with exit code $code, nothing changed" ($null -ne $script:rnError -and (Get-SetupExitCode $script:rnError) -eq $code -and $script:rnError.Exception.Message -match $pattern -and
            $script:rnError.Exception.Message -match 'Nothing was changed') "$($script:rnError)"
        $subject
    }
    $null = Test-Refusal 'no panel service' @{} @{ NoService = $true } 2 'is not installed on this machine'
    $null = Test-Refusal 'a service that runs another panel' @{} @{ Program = '"C:\Elsewhere\Panel\Pulsar.Api.exe"' } 2 'InstallRoot'
    $null = Test-Refusal 'no settings' @{ NoSettings = $true } @{} 2 'settings .* are missing'
    $null = Test-Refusal 'settings that are not a panel''s' @{ Settings = '{ "Logging": { } }' } @{} 2 'not a Pulsar panel'
    $null = Test-Refusal 'settings that are not JSON' @{ Settings = 'not json' } @{} 2 'not a Pulsar panel'
    $null = Test-Refusal 'a certificate that is somewhere else than the installer puts it' @{ CertificateFile = 'C:\certs\mine.pfx' } @{} 6 'changed by hand'
    $null = Test-Refusal 'a certificate without a password' @{ Password = '' } @{} 6 'no password'
    $null = Test-Refusal 'a PFX the settings'' password does not open' @{ Password = 'another password' } @{} 6 'cannot be read'
    $null = Test-Refusal 'a certificate an authority issued' @{ Issued = $true } @{} 6 'not a self-signed one'
    $null = Test-Refusal 'a self-signed certificate for another subject' @{ Subject = 'CN=example.com' } @{} 6 'not a self-signed one'
    $null = Test-Refusal 'a self-signed CN=localhost certificate of your own (no ''Pulsar panel'' friendly name)' @{ Subject = 'CN=localhost' } @{} 6 'friendly name '''', not ''Pulsar panel'''
    $null = Test-Refusal 'an agents'' port with another certificate' @{ Agents = @{ Url = 'https://*:8444'; Certificate = @{ Path = 'C:\certs\other.pfx'; Password = 'x' } } } @{} 6 'agents'' port uses another certificate'
    $null = Test-Refusal 'files left by a stopped renewal' @{ Debris = 'panel.pfx.old-0123456789abcdef' } @{} 3 'panel\.pfx\.old-0123456789abcdef.*stopped'
    $linked = New-RenewInstall 'a link as the tls folder'
    $realTls = Join-Path $linked.Base 'real-tls'
    Move-Item -LiteralPath $linked.Tls $realTls
    $null = New-Item -ItemType Junction -Path $linked.Tls -Target $realTls
    Invoke-Renewal $linked @{} -Only
    [IO.Directory]::Delete($linked.Tls)
    $SetupSecrets.Clear()
    Check 'renew check: a link (junction) in place of the certificate''s folder is refused (exit code 3)' ($null -ne $script:rnError -and (Get-SetupExitCode $script:rnError) -eq 3 -and $script:rnError.Exception.Message -match 'is a link') "$($script:rnError)"

    # --- The swap: the old files are kept until the new certificate answers; then trust moves, the old files go ---
    function Get-Pfx($path) { New-Object Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList @([IO.File]::ReadAllBytes($path), $rnPassword, [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet) }
    $swap = New-RenewInstall 'swap' @{ ExtraName = 'panel.example.com' }
    $users = New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-545'
    $acl = Get-Acl -LiteralPath $swap.Pfx
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($users, 'ReadAndExecute', 'Allow')))
    Set-Acl -LiteralPath $swap.Pfx -AclObject $acl
    $oldAccess = @{ Pfx = (Get-FileAccess $swap.Pfx); Cer = (Get-FileAccess $swap.Cer) }
    $tlsBefore = Get-FileAccess $swap.Tls
    Invoke-Renewal $swap @{ HostName = @('panel.example.com'); Unattended = $true }
    $printed = $script:rnOutput -join "`n"
    $SetupSecrets.Clear()
    Check 'renew: the happy path ends without an error' ($null -eq $script:rnError) "$($script:rnError) $printed"
    $newCertificate = Get-Pfx $swap.Pfx
    $newThumbprint = $newCertificate.Thumbprint
    try {
        $newHash = Get-CertificateHash $newCertificate
        $newNames = Get-CertificateNames $newCertificate
        $cerCertificate = Read-Certificate $swap.Cer
        try { $sameCer = (Get-CertificateHash $cerCertificate) -ceq $newHash } finally { $cerCertificate.Dispose() }
        Check 'renew: panel.pfx is a new certificate (a new key), and opens with the same password, so the settings stay as they are' ($newCertificate.Thumbprint -cne $swap.Thumbprint -and $newCertificate.HasPrivateKey)
        Check 'renew: panel.cer is that same certificate' $sameCer
        Check 'renew: it lists this PC''s names and current addresses and -HostName''s, and not the old address' (
            ($newNames.Dns -join ',') -ceq "localhost,$env:COMPUTERNAME,panel.example.com" -and (@($newNames.Ips | ForEach-Object { "$_" }) -join ',') -ceq '127.0.0.1,::1,192.168.0.77,10.0.0.7') "$($newNames.Dns -join ',') | $($newNames.Ips -join ',')"
        Check 'renew: it is made like the install''s (CA=false, server authentication alone, 5 years)' ((Get-CertificateAuthority $newCertificate) -eq $false -and (Test-ServerAuthenticationOnly (Get-CertificateUses $newCertificate)) -and
            [Math]::Abs(($newCertificate.NotAfter - (Get-Date).AddYears(5)).TotalMinutes) -lt 5)
        Check 'renew: the SHA-256 printed for it (PULSAR-CERTIFICATE) is that of the file now in place' ($printed -match ('(?m)^PULSAR-CERTIFICATE ' + $newHash + '$') -and $printed -match [regex]::Escape((Format-Fingerprint $newHash)))
    }
    finally { $newCertificate.Dispose() }
    Check 'renew: the new files have the old ones'' permissions (explicit entries included)' ((Get-FileAccess $swap.Pfx) -ceq $oldAccess.Pfx -and (Get-FileAccess $swap.Cer) -ceq $oldAccess.Cer) "$(Get-FileAccess $swap.Pfx) vs $($oldAccess.Pfx)"
    Check 'renew: nothing is left in the folder but the two files (the old files and the staged ones are gone)' ((Get-FilesIn $swap) -ceq 'panel.cer,panel.pfx') (Get-FilesIn $swap)
    Check 'renew: while the files are swapped the folder denies the panel''s account everything (and its files inherit that), and its permissions are then put back exactly' (
        "$script:rnLockedAcl" -match [regex]::Escape("(D;OICI;FA;;;$rnPanelSid)") -and "$tlsBefore" -notmatch '\(D;' -and "$tlsBefore" -match [regex]::Escape("(A;OICI;0x1301bf;;;$rnPanelSid)") -and
        (Get-FileAccess $swap.Tls) -ceq $tlsBefore) "locked: $script:rnLockedAcl | before: $tlsBefore | after: $(Get-FileAccess $swap.Tls)"
    $steps = @($script:rnCalls)
    Check 'renew: it stops the panel, starts it, and checks it answers with the new certificate (its HTTPS port; the agents'' port 0: none here) before anything else' (
        $steps.Count -eq 5 -and $steps[0] -ceq 'stop' -and $steps[1] -ceq 'start' -and $steps[2] -ceq "confirm $newThumbprint 8443 0") ($steps -join ' | ')
    Check 'renew: then it trusts the new certificate as the install does (panel.cer, localhost, the SHA-256 made), and only then stops trusting the old one' (
        $steps[3] -ceq "trust $($swap.Cer) localhost $newHash" -and $steps[4] -ceq "untrust $($swap.Thumbprint) False" -and $steps.Count -eq 5) ($steps -join ' | ')
    Check 'renew: it prints a step for each of its plan, in order' (@($script:rnOutput | Where-Object { $_ -like 'PULSAR-STEP *' }).Count -eq 3 -and
        (@($script:rnOutput | Where-Object { $_ -like 'PULSAR-STEP *' }) -join '|') -match '^PULSAR-STEP 2/4 .*\|PULSAR-STEP 3/4 .*\|PULSAR-STEP 4/4 ') (@($script:rnOutput | Where-Object { $_ -like 'PULSAR-STEP *' }) -join '|')
    Check 'renew: it prints no password (the PFX''s, nor the database''s)' ($printed -notmatch [regex]::Escape($rnPassword) -and $printed -notmatch 'a-database-password')
    Check 'renew: it tells what pinned nodes must do, with the new certificate''s SHA-256, as the guide does' (
        $printed -match 'pin the old certificate and stay offline until each is pinned again' -and $printed -match [regex]::Escape("set ""panelCertificate"" in C:\ProgramData\Pulsar\Agent\config\agent.json to") -and
        $printed -match ('(?m)^ +' + $newHash + '$') -and $printed -match 'PulsarAgent service stop, then PulsarAgent service start' -and
        $printed -match [regex]::Escape("PulsarAgent enroll --panel https://192.168.0.77:8443 --token - --panel-key <the panel key's fingerprint> --panel-certificate $newHash") -and
        $printed -match 'New enrollment command' -and $printed -match 'TrustCertificate')
    Check 'renew: it says a Let''s Encrypt public IP certificate is not affected' ($printed -match "Let's Encrypt public IP certificate, if you use one, is not affected")
    Check 'renew: it says which addresses and names are listed now and where to reach the panel' ($printed -match 'https://192\.168\.0\.77:8443 or https://10\.0\.0\.7:8443 or https://' -and $printed -match 'DHCP server gave this PC 192\.168\.0\.77')
    Check 'renew: a node on this PC that is not pinned is not mentioned' ($printed -notmatch 'The node on this PC is pinned')
    Check 'renew: names the old certificate had that this PC does not (a stale address) are mentioned, the -HostName one is kept' (
        $printed -match 'also listed .*192\.168\.0\.54.*OLDBOX|also listed .*OLDBOX.*192\.168\.0\.54' -and $printed -notmatch 'run this again with -HostName')

    # --- A name given to the install with -HostName: not remembered, so not repeated is a decision, never a silent loss ---
    $named = New-RenewInstall 'a name to keep' @{ ExtraName = 'panel.example.com' }
    $before = Get-Digests $named
    Invoke-Renewal $named @{}
    $SetupSecrets.Clear()
    Check 'renew: without -HostName, a name of its own that the old certificate lists (panel.example.com) stops it before anything changes (exit code 2), saying how to keep it or drop it' (
        $null -ne $script:rnError -and (Get-SetupExitCode $script:rnError) -eq 2 -and $script:rnError.Exception.Message -match 'also lists panel\.example\.com' -and $script:rnError.Exception.Message -match '-HostName panel\.example\.com keeps' -and
        $script:rnError.Exception.Message -match 'Nothing was changed' -and $script:rnCalls.Count -eq 0 -and (Get-Digests $named) -ceq $before -and (Get-FilesIn $named) -ceq 'panel.cer,panel.pfx') "$($script:rnError)"

    # --- The same install, the panel stopped, the node on this PC pinned, the name left out on purpose ---
    $local = New-RenewInstall 'local pin' @{ ExtraName = 'panel.example.com' }
    Invoke-Renewal $local @{ Stopped = $true; LocalPin = ('ab' * 32); HostName = @('localhost') }
    $printed = $script:rnOutput -join "`n"
    $SetupSecrets.Clear()
    Check 'renew: a node on this PC that is pinned is mentioned, with the file to change' ($null -eq $script:rnError -and $printed -match 'The node on this PC is pinned too' -and $printed -match ('ab' * 32) -and $printed -match 'agent\.json')
    Check 'renew: with another -HostName, a name left out is warned about (twice: when it is made, and at the end), and how to keep it' (
        $printed -match 'also listed panel\.example\.com' -and $printed -match 'run this again with them in -HostName to keep them' -and $printed -match 'Not in the new certificate: panel\.example\.com')

    # --- Trust: only when the install trusted the old certificate ---
    foreach ($state in 'none', 'unmarked') {
        $subject = New-RenewInstall "trust $state"
        Invoke-Renewal $subject @{ Trust = $state }
        $printed = $script:rnOutput -join "`n"
        $SetupSecrets.Clear()
        Check "renew: an old certificate that is $state (not trusted by the install on this PC) leaves every certificate store alone" ($null -eq $script:rnError -and @($script:rnCalls | Where-Object { $_ -like 'trust *' -or $_ -like 'untrust *' }).Count -eq 0 -and
            (Get-FilesIn $subject) -ceq 'panel.cer,panel.pfx') ($script:rnCalls -join ' | ')
        if ($state -eq 'unmarked') { Check 'renew: and says so when someone else trusts it' ($printed -match 'not as this script''s') }
    }

    # --- Failures: the old certificate comes back ---
    $failing = New-RenewInstall 'confirm fails'
    $before = Get-Digests $failing
    Invoke-Renewal $failing @{ ConfirmFails = 'first' }
    $SetupSecrets.Clear()
    $calls = @($script:rnCalls)
    Check 'renew rollback: when the panel does not answer with the new certificate, the old files are back, byte for byte, and nothing else is left' (
        $null -ne $script:rnError -and (Get-Digests $failing) -ceq $before -and (Get-FilesIn $failing) -ceq 'panel.cer,panel.pfx') "$($script:rnError) | $(Get-FilesIn $failing)"
    Check 'renew rollback: the panel is stopped, the files put back, the panel started again and it answers with the OLD certificate' (
        ($calls -join '|') -ceq "stop|start|confirm $($calls[2].Split(' ')[1]) 8443 0|stop|start|confirm $($failing.Thumbprint) 8443 0" -and $calls[2].Split(' ')[1] -cne $failing.Thumbprint) ($calls -join ' | ')
    Check 'renew rollback: the trust was not touched (not added, the old one not removed), and the exit code is 1 with the reason and the outcome' (
        @($calls | Where-Object { $_ -like 'trust *' -or $_ -like 'untrust *' }).Count -eq 0 -and (Get-SetupExitCode $script:rnError) -eq 1 -and
        $script:rnError.Exception.Message -match 'did not answer' -and $script:rnError.Exception.Message -match 'The old certificate is back, and the panel runs with it\.') "$($script:rnError.Exception.Message)"

    $failing = New-RenewInstall 'trust fails'
    $before = Get-Digests $failing
    Invoke-Renewal $failing @{ TrustFails = $true }
    $SetupSecrets.Clear()
    $calls = @($script:rnCalls)
    $addedThumbprint = ''
    if ($calls.Count -gt 2) { $addedThumbprint = $calls[2].Split(' ')[1] }
    Check 'renew rollback: when trusting the new certificate fails, the new trust is removed (the one just added, even unmarked), the old one is kept, and the files are back' (
        $null -ne $script:rnError -and (Get-Digests $failing) -ceq $before -and (Get-FilesIn $failing) -ceq 'panel.cer,panel.pfx' -and
        @($calls | Where-Object { $_ -ceq "untrust $addedThumbprint True" }).Count -eq 1 -and @($calls | Where-Object { $_ -ceq "untrust $($failing.Thumbprint) False" -or $_ -ceq "untrust $($failing.Thumbprint) True" }).Count -eq 0 -and
        $script:rnError.Exception.Message -match 'store refused') ($calls -join ' | ')

    $failing = New-RenewInstall 'answers never'
    $before = Get-Digests $failing
    Invoke-Renewal $failing @{ ConfirmFails = 'always' }
    $SetupSecrets.Clear()
    Check 'renew rollback: when the old certificate does not answer either, the files are still back and the message says what is left and where the old files are' (
        (Get-Digests $failing) -ceq $before -and $script:rnError.Exception.Message -match 'Putting the old certificate back left: the panel does not answer with the old certificate' -and
        $script:rnError.Exception.Message -match 'copy them over panel\.pfx and panel\.cer' -and (Get-SetupExitCode $script:rnError) -eq 1) "$($script:rnError.Exception.Message)"

    $failing = New-RenewInstall 'staged files wrong'
    $before = Get-Digests $failing
    Invoke-Renewal $failing @{ StagedCheckFails = $true }
    $SetupSecrets.Clear()
    Check 'renew rollback: a new certificate that does not check out (the panel stopped and the folder locked by then) is dropped: the old files, their permissions and the folder''s are as they were, nothing is left, the panel runs again' (
        (Get-Digests $failing) -ceq $before -and (Get-FilesIn $failing) -ceq 'panel.cer,panel.pfx' -and ($script:rnCalls -join '|') -ceq "stop|stop|start|confirm $($failing.Thumbprint) 8443 0" -and
        $script:rnError.Exception.Message -match 'The old certificate is back, and the panel runs with it' -and (Get-SetupExitCode $script:rnError) -eq 1) "$($script:rnError) | $($script:rnCalls -join '|')"

    # A process still running as the panel's account once it has stopped could redirect the files being swapped.
    $failing = New-RenewInstall 'lingering process'
    $before = Get-Digests $failing
    Invoke-Renewal $failing @{ Lingering = $true }
    $SetupSecrets.Clear()
    Check 'renew: a process still running as the panel''s account after it stopped ends the renewal before anything is written (exit code 3), the certificate untouched, the folder''s permissions and the panel as they were' (
        $null -ne $script:rnError -and (Get-SetupExitCode $script:rnError) -eq 3 -and $script:rnError.Exception.Message -match 'panel \(1234\) still runs as NT SERVICE\\PulsarPanel' -and $script:rnError.Exception.Message -match 'The certificate was not changed' -and
        (Get-Digests $failing) -ceq $before -and (Get-FilesIn $failing) -ceq 'panel.cer,panel.pfx' -and ($script:rnCalls -join '|') -ceq "stop|stop|start|confirm $($failing.Thumbprint) 8443 0" -and
        $script:rnError.Exception.Message -match 'The old certificate is back') "$($script:rnError) | $($script:rnCalls -join '|')"

    # The certificate's folder is held from the first check to the end: while the panel is stopped nobody can rename it (to put a
    # junction in its place), and the renewal still goes through.
    $holding = New-RenewInstall 'held while stopped'
    Invoke-Renewal $holding @{ RenameWhileStopped = $true; Trust = 'none' }
    $SetupSecrets.Clear()
    Check 'renew: the certificate folder cannot be renamed while the renewal holds it (the panel stopped, the swap not yet done)' (
        $null -eq $script:rnError -and $script:rnCalls -contains 'rename-blocked' -and $script:rnCalls -notcontains 'renamed' -and (Get-FilesIn $holding) -ceq 'panel.cer,panel.pfx') "$($script:rnError) | $($script:rnCalls -join ' | ')"
    Check 'renew: and the hold is gone when it ends (the folder can be renamed again)' ($null -eq (Get-Thrown { Move-Item -LiteralPath $holding.Tls "$($holding.Tls)-moved"; Move-Item -LiteralPath "$($holding.Tls)-moved" $holding.Tls }))

    # Once the panel is stopped, a file or folder that has become a link is a failure to undo (exit code 1), not a refusal.
    $afterStop = New-RenewInstall 'plain after stop'
    $cerReal = Join-Path $afterStop.Base 'elsewhere'
    $null = New-Item -ItemType Directory $cerReal
    Remove-Item -LiteralPath $afterStop.Cer
    $null = New-Item -ItemType Junction -Path $afterStop.Cer -Target $cerReal
    $paths = [pscustomobject]@{ Tls = $afterStop.Tls; Pfx = $afterStop.Pfx; Cer = $afterStop.Cer }
    $refusal = & { $PanelData = $afterStop.PanelData; Get-Caught { Assert-PlainCertificateFiles $paths } }
    $failure = & { $PanelData = $afterStop.PanelData; Get-Caught { Assert-PlainCertificateFiles $paths $true } }
    [IO.Directory]::Delete($afterStop.Cer)
    Check 'renew: a file that is a link is a refusal before anything changes (exit code 3, nothing changed), and a failure after the panel stopped (exit code 1, it changed meanwhile)' (
        (Get-SetupExitCode $refusal) -eq 3 -and $refusal.Exception.Message -match 'is missing or is a link' -and $refusal.Exception.Message -match 'Nothing was changed' -and
        (Get-SetupExitCode $failure) -eq 1 -and $failure.Exception.Message -match 'is missing or is a link' -and $failure.Exception.Message -match 'while the panel was being renewed' -and $failure.Exception.Message -notmatch 'Nothing was changed') "$refusal | $failure"

    # The lock itself: the panel's account denied while it lasts, the folder's own permissions back exactly, its files' with them.
    function Use-PanelSid([string] $sid, [scriptblock] $block) {
        & { function Get-PanelAccountSid { New-Object Security.Principal.SecurityIdentifier $sid }; & $block }
    }
    $lockRoot = Join-Path $rnRoot 'lock'
    $lockFolder = Join-Path $lockRoot 'tls'
    $null = New-Item -ItemType Directory $lockFolder
    $lockFile = Join-Path $lockFolder 'panel.pfx'
    [IO.File]::WriteAllText($lockFile, 'x')
    Set-PanelLikeAcl $lockFolder
    $folderBefore = Get-FileAccess $lockFolder
    $fileBefore = Get-FileAccess $lockFile
    $lockSid = New-Object Security.Principal.SecurityIdentifier $rnPanelSid
    $progress = [pscustomobject]@{ Locked = $false; FolderAccess = $null; Denied = $null }
    Check 'lock: before it, the stand-in for the panel''s account is allowed on the folder and its file, and denied nowhere' (
        -not (Test-FolderDenies $lockFolder $lockSid) -and $folderBefore -match [regex]::Escape("(A;OICI;0x1301bf;;;$rnPanelSid)") -and $fileBefore -match [regex]::Escape("(A;ID;0x1301bf;;;$rnPanelSid)") -and "$folderBefore$fileBefore" -notmatch '\(D;') "$folderBefore | $fileBefore"
    Check 'lock: a folder only SYSTEM, Administrators, the running administrator and the panel''s account may use passes the check that denying the account closes it' (
        $null -eq (Get-Caught { Use-PanelSid $rnPanelSid { Assert-PrivateCertificateFolder $lockFolder } }))
    $wideFolder = Join-Path $lockRoot 'wide'
    $null = New-Item -ItemType Directory $wideFolder
    Set-PanelLikeAcl $wideFolder @('S-1-5-32-545')
    $thrown = Get-Caught { Use-PanelSid $rnPanelSid { Assert-PrivateCertificateFolder $wideFolder } }
    Check 'lock: a folder that also lets Users in is refused before anything changes (exit code 3): denying the panel''s account would not close it' (
        $null -ne $thrown -and (Get-SetupExitCode $thrown) -eq 3 -and $thrown.Exception.Message -match ('lets in ' + [regex]::Escape((New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-545').Translate([Security.Principal.NTAccount]).Value)) -and $thrown.Exception.Message -match 'Nothing was changed') "$thrown"
    $ownerCheck = & {
        $fake = New-Object psobject
        Add-Member -InputObject $fake -MemberType ScriptMethod -Name GetOwner -Value { param($type) New-Object Security.Principal.SecurityIdentifier 'S-1-5-80-1-2-3-4-5' }
        function Get-Acl { $fake }
        Get-Caught { Assert-PrivateCertificateFolder 'C:\fake' }
    }
    Check 'lock: a folder owned by someone else (the service account, say) is refused (exit code 3, nothing changed): its owner could change the permissions back' (
        $null -ne $ownerCheck -and (Get-SetupExitCode $ownerCheck) -eq 3 -and $ownerCheck.Exception.Message -match 'is owned by' -and $ownerCheck.Exception.Message -match 'Nothing was changed') "$ownerCheck"
    Use-PanelSid $rnPanelSid { Lock-CertificateFolder $lockFolder $progress }
    Check 'lock: locked, the panel''s account is denied everything on the folder, and on its file by inheritance; the rest of the permissions are as they were' (
        $progress.Locked -and (Test-FolderDenies $lockFolder $lockSid) -and (Get-FileAccess $lockFolder) -match [regex]::Escape("(D;OICI;FA;;;$rnPanelSid)") -and (Get-FileAccess $lockFile) -match [regex]::Escape("(D;ID;FA;;;$rnPanelSid)") -and
        $progress.FolderAccess -ceq $folderBefore -and ((Get-FileAccess $lockFolder) -replace [regex]::Escape("(D;OICI;FA;;;$rnPanelSid)"), '') -ceq $folderBefore) "$(Get-FileAccess $lockFolder)"
    Use-PanelSid $rnPanelSid { Unlock-CertificateFolder $lockFolder $progress }
    Check 'lock: unlocked, the folder''s permissions are exactly as they were, and its file inherits them again' (-not $progress.Locked -and (Get-FileAccess $lockFolder) -ceq $folderBefore -and (Get-FileAccess $lockFile) -ceq $fileBefore) "$(Get-FileAccess $lockFolder) | $folderBefore"
    Use-PanelSid $rnPanelSid { Lock-CertificateFolder $lockFolder $progress; Unlock-CertificateFolder $lockFolder $progress; Unlock-CertificateFolder $lockFolder $progress }
    Check 'lock: locking again after an unlock keeps the permissions to put back (the folder''s own, not the lock''s), and unlocking what is not locked changes nothing' (
        $progress.FolderAccess -ceq $folderBefore -and (Get-FileAccess $lockFolder) -ceq $folderBefore -and (Get-FileAccess $lockFile) -ceq $fileBefore)

    # What the lock is for, with this user standing in for the panel's account: an empty folder, held. The hold does not stop
    # whoever can write attributes on it from changing it in place (making it a mount point needs that right); denied, they cannot.
    if (-not ('PulsarTestAccess' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class PulsarTestAccess
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    public static bool CanOpen(string path, uint access)
    {
        using (SafeFileHandle handle = CreateFile(path, access, 7, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero)) { return !handle.IsInvalid; }
    }
}
'@
    }
    $emptyFolder = Join-Path $lockRoot 'empty'
    $null = New-Item -ItemType Directory $emptyFolder
    Set-PanelLikeAcl $emptyFolder
    $myId = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $emptyHold = Open-CertificateFolder $emptyFolder
    $emptyProgress = [pscustomobject]@{ Locked = $false; FolderAccess = $null; Denied = $null }
    try {
        $writeAttributes = 0x100
        $canBefore = [PulsarTestAccess]::CanOpen($emptyFolder, $writeAttributes)
        Use-PanelSid $myId { Lock-CertificateFolder $emptyFolder $emptyProgress }
        $canLocked = [PulsarTestAccess]::CanOpen($emptyFolder, $writeAttributes)
        $createdLocked = $null -eq (Get-Thrown { [IO.File]::WriteAllText((Join-Path $emptyFolder 'x'), 'x') })
        Use-PanelSid $myId { Unlock-CertificateFolder $emptyFolder $emptyProgress }
        $canAfter = [PulsarTestAccess]::CanOpen($emptyFolder, $writeAttributes)
    }
    finally { $emptyHold.Dispose() }
    Check 'lock: a held empty folder can still be opened for writing attributes (what turning it into a mount point takes) by an account with Modify; denied, that account cannot open it so, nor create a file in it; and can again once unlocked' (
        $canBefore -and -not $canLocked -and -not $createdLocked -and $canAfter -and -not $emptyProgress.Locked) "before=$canBefore locked=$canLocked created=$createdLocked after=$canAfter"

    # The hold itself, on folders of its own.
    $holdRoot = Join-Path $rnRoot 'hold'
    $heldFolder = Join-Path $holdRoot 'held'
    $otherFolder = Join-Path $holdRoot 'other'
    $null = New-Item -ItemType Directory $heldFolder, $otherFolder
    $linkToOther = Join-Path $holdRoot 'link-to-other'
    $null = New-Item -ItemType Junction -Path $linkToOther -Target $otherFolder
    $linkToHeld = Join-Path $holdRoot 'link-to-held'
    $null = New-Item -ItemType Junction -Path $linkToHeld -Target $heldFolder
    function Test-Renamable([string] $path) {
        $moved = $null -eq (Get-Thrown { Move-Item -LiteralPath $path "$path-moved" })
        if ($moved) { Move-Item -LiteralPath "$path-moved" $path }
        return $moved
    }
    $hold = Open-CertificateFolder $heldFolder
    try {
        Check 'hold: the real path of a folder it holds is the folder''s own' ($hold.Path -eq [IO.Path]::GetFullPath($heldFolder).TrimEnd('\')) "$($hold.Path)"
        Check 'hold: a held folder cannot be renamed, deleted or replaced' (-not (Test-Renamable $heldFolder) -and $null -ne (Get-Thrown { [IO.Directory]::Delete($heldFolder) }) -and (Test-Path -LiteralPath $heldFolder))
        Check 'hold: nor a folder above it' (-not (Test-Renamable $holdRoot) -and (Test-Path -LiteralPath $heldFolder))
        $inside = Join-Path $heldFolder 'x.txt'
        $replacement = Join-Path $heldFolder 'x.new'
        [IO.File]::WriteAllText($inside, 'old')
        [IO.File]::WriteAllText($replacement, 'new')
        [IO.File]::Replace($replacement, $inside, (Join-Path $heldFolder 'x.old'))
        Remove-Item -LiteralPath (Join-Path $heldFolder 'x.old'), $inside
        Check 'hold: the files in it can still be made, replaced and deleted' (@(Get-ChildItem -LiteralPath $heldFolder -Force).Count -eq 0)

        # What is made through a link into another folder is found out by handle, and deleted again.
        Write-ExclusiveFile (Join-Path $heldFolder 'made.new') ([byte[]](1, 2, 3)) $hold.Path
        Check 'hold: a new file made in the held folder is written' ([IO.File]::ReadAllBytes((Join-Path $heldFolder 'made.new')).Length -eq 3)
        $thrown = Get-Thrown { Write-ExclusiveFile (Join-Path $linkToOther 'stray.new') ([byte[]](1, 2, 3)) $hold.Path }
        Check 'hold: a new file that lands in another folder through a link is refused, nothing written, and removed again' (
            $thrown -match 'outside the folder held for it' -and @(Get-ChildItem -LiteralPath $otherFolder -Force).Count -eq 0) "$thrown | $(@(Get-ChildItem -LiteralPath $otherFolder -Force | ForEach-Object Name) -join ',')"
        Write-ExclusiveFile (Join-Path $linkToHeld 'via-link.new') ([byte[]](1)) $hold.Path
        Check 'hold: a file made through a link that leads into the held folder is in it, so it is accepted' (Test-Path -LiteralPath (Join-Path $heldFolder 'via-link.new'))
        $thrown = Get-Thrown { Write-ExclusiveFile (Join-Path $heldFolder 'made.new') ([byte[]](1)) $hold.Path }
        Check 'hold: a new file is never made over one that is there' ($null -ne $thrown)

        [IO.File]::WriteAllText((Join-Path $otherFolder 'panel.pfx'), 'not ours')
        [IO.File]::WriteAllText((Join-Path $heldFolder 'panel.pfx'), 'ours')
        Check 'hold: a file of the held folder is checked by handle to be one' ($null -eq (Get-Thrown { Assert-InFolder (Join-Path $heldFolder 'panel.pfx') $hold }))
        Check 'hold: a file reached through a link to another folder is not in it' ((Get-Thrown { Assert-InFolder (Join-Path $linkToOther 'panel.pfx') $hold }) -match 'not a file of the folder held for it')
        Remove-Item -LiteralPath (Join-Path $otherFolder 'panel.pfx')
    }
    finally { $hold.Dispose() }
    Check 'hold: once released, the folder can be renamed again' (Test-Renamable $heldFolder)
    $thrown = Get-Caught { Open-CertificateFolder $linkToHeld }
    Check 'hold: a folder that is a link is not held (exit code 3, nothing changed)' ($null -ne $thrown -and (Get-SetupExitCode $thrown) -eq 3 -and $thrown.Exception.Message -match 'is a link' -and $thrown.Exception.Message -match 'Nothing was changed') "$thrown"
    $nested = Join-Path $heldFolder 'nested'
    $null = New-Item -ItemType Directory $nested
    $thrown = Get-Caught { Open-CertificateFolder (Join-Path $linkToHeld 'nested') }
    Check 'hold: a folder reached through a link on the way is not the folder the installer made (exit code 3, nothing changed)' (
        $null -ne $thrown -and (Get-SetupExitCode $thrown) -eq 3 -and $thrown.Exception.Message -match 'once the links on its way are followed' -and $thrown.Exception.Message -match 'Nothing was changed') "$thrown"
    [IO.Directory]::Delete($linkToOther)
    [IO.Directory]::Delete($linkToHeld)

    $failing = New-RenewInstall 'was stopped'
    $before = Get-Digests $failing
    Invoke-Renewal $failing @{ Stopped = $true; ConfirmFails = 'first' }
    $SetupSecrets.Clear()
    Check 'renew rollback: a panel that was stopped is left stopped (not started again) when the renewal is undone' (
        (Get-Digests $failing) -ceq $before -and @($script:rnCalls | Where-Object { $_ -ceq 'start' }).Count -eq 1 -and $script:rnError.Exception.Message -match 'is stopped, as it was')

    # One file swapped, the other not: panel.cer is held open (a virus scanner, say), so its swap fails after panel.pfx's.
    $failing = New-RenewInstall 'half swapped'
    $before = Get-Digests $failing
    $rnAgentLock = New-Object IO.FileStream($failing.Cer, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    try { Invoke-Renewal $failing @{} }
    finally { $rnAgentLock.Dispose(); $rnAgentLock = $null }
    $SetupSecrets.Clear()
    Check 'renew rollback: a swap that stops halfway (panel.pfx replaced, panel.cer not) puts panel.pfx back too' (
        $null -ne $script:rnError -and (Get-Digests $failing) -ceq $before -and (Get-FilesIn $failing) -ceq 'panel.cer,panel.pfx' -and
        $script:rnError.Exception.Message -match 'The old certificate is back') "$($script:rnError.Exception.Message) | $(Get-FilesIn $failing) | before: $before | after: $(Get-Digests $failing)"

    # An interrupted run (Ctrl+C, a closed window): no catch sees it, only finally runs. The run is in a runspace of its own, which
    # is stopped as Ctrl+C stops a pipeline, while the panel is being checked with the new certificate.
    $interrupted = New-RenewInstall 'interrupted' @{ ExtraName = 'panel.example.com' }
    $before = Get-Digests $interrupted
    $flagFile = Join-Path $interrupted.Base 'confirm-started.txt'
    $callsFile = Join-Path $interrupted.Base 'calls.txt'
    $mocks = @'
function Get-CimInstance { if ($args[0] -eq 'Win32_ComputerSystem') { return [pscustomobject]@{ Domain = 'WORKGROUP' } }; [pscustomobject]@{ PathName = ('"' + $ExePath + '"') } }
function Get-LanAddresses { [Net.IPAddress]::Parse('192.168.0.77') }
function Get-DhcpAddresses { }
function Get-Service { [pscustomobject]@{ Status = 'Running' } }
function Get-InstallerTrust([string] $thumbprint) { 'none' }
function Stop-Panel { Add-Content -LiteralPath $CallsFile 'stop' }
function Start-Panel { Add-Content -LiteralPath $CallsFile 'start' }
function Confirm-PanelCertificate([int] $port, [int] $agentsPort, $certificate) {
    Add-Content -LiteralPath $CallsFile "confirm $($certificate.Thumbprint)"
    if (-not (Test-Path -LiteralPath $FlagFile)) { Set-Content -LiteralPath $FlagFile 'started'; Start-Sleep -Seconds 120 }
}
function Get-LocalNodePin { $null }
function Get-PanelAccountProcesses { }
function Get-PanelAccountSid { New-Object Security.Principal.SecurityIdentifier $PanelSid }
function Write-Step([string] $message) { }
function Write-SetupStep([string] $key, [string] $text) { }
'@
    $runspaceScript = @('$ErrorActionPreference = ''Stop''', (@($installParts.Values) -join "`r`n"), $mocks, 'Invoke-RenewCertificate') -join "`r`n"
    $shell = [powershell]::Create()
    try {
        $variables = @{ PanelService = 'PulsarPanel'; PanelAccount = 'NT SERVICE\PulsarPanel'; PanelSid = $rnPanelSid; AgentService = 'PulsarAgent'; PanelDir = $interrupted.PanelDir; PanelData = $interrupted.PanelData; PanelConfig = $interrupted.Config
            AgentDir = (Join-Path $interrupted.Base 'Agent'); AgentData = (Join-Path $interrupted.Base 'AgentData'); HostName = [string[]]@('panel.example.com'); Unattended = $false
            SetupParameters = @{ RenewCertificate = $true }; StepPlan = $StepPlans['RenewCertificate']; ExePath = $interrupted.Exe; FlagFile = $flagFile; CallsFile = $callsFile; WarningPreference = 'SilentlyContinue' }
        foreach ($name in $variables.Keys) { $shell.Runspace.SessionStateProxy.SetVariable($name, $variables[$name]) }
        $null = $shell.AddScript($runspaceScript)
        $async = $shell.BeginInvoke()
        $waited = [Diagnostics.Stopwatch]::StartNew()
        while (-not (Test-Path -LiteralPath $flagFile) -and $waited.Elapsed.TotalSeconds -lt 90 -and -not $async.IsCompleted) { Start-Sleep -Milliseconds 200 }
        $reachedCheck = Test-Path -LiteralPath $flagFile
        $shell.Stop()
        $stopped = $shell.InvocationStateInfo.State
        $problem = ''
        try { $null = $shell.EndInvoke($async) } catch { $problem = $_.Exception.GetBaseException().GetType().Name }
        $runErrors = @($shell.Streams.Error | ForEach-Object { "$_" })
    }
    finally { $shell.Dispose() }
    $calls = @(Get-Content -LiteralPath $callsFile -ErrorAction SilentlyContinue)
    Check 'renew rollback: Ctrl+C while the new certificate is being checked (no catch runs; finally does) puts the old files back, starts the panel again and leaves nothing' (
        $reachedCheck -and "$stopped" -ceq 'Stopped' -and (Get-Digests $interrupted) -ceq $before -and (Get-FilesIn $interrupted) -ceq 'panel.cer,panel.pfx' -and
        $calls.Count -eq 6 -and ($calls[0..1] -join ',') -ceq 'stop,start' -and $calls[2] -like 'confirm *' -and $calls[2] -cne "confirm $($interrupted.Thumbprint)" -and
        ($calls[3..4] -join ',') -ceq 'stop,start' -and $calls[5] -ceq "confirm $($interrupted.Thumbprint)") "reached=$reachedCheck state=$stopped $problem | $($calls -join ' | ') | $(Get-FilesIn $interrupted) | $($runErrors -join ' / ')"
}
finally {
    if ($null -ne $rnAgentLock) { $rnAgentLock.Dispose() }
    Remove-Item -LiteralPath $rnRoot -Recurse -Force
    $SetupSecrets.Clear()
}

# What the script says it does (read from it, never run): the order, the rollback, the allow-list of what touches a store, and
# no secret in anything printed.
$renewFunctions = @($installAst.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -in
            'Invoke-RenewCertificate', 'Get-RenewableCertificate', 'Undo-CertificateRenewal', 'Confirm-PanelCertificate', 'Write-RepinSteps', 'Assert-NewCertificateFiles',
            'Assert-PlainCertificateFiles', 'Assert-RenewArguments', 'Compare-CertificateNames', 'Set-SameAccess', 'Get-LocalNodePin', 'Stop-Panel', 'Open-CertificateFolder',
            'Lock-CertificateFolder', 'Unlock-CertificateFolder', 'Assert-NoPanelProcesses', 'Get-PanelAccountProcesses', 'Assert-PrivateCertificateFolder' }, $true))
$renewalText = @($renewFunctions | Where-Object Name -eq 'Invoke-RenewCertificate')[0].Extent.Text
$undoText = @($renewFunctions | Where-Object Name -eq 'Undo-CertificateRenewal')[0].Extent.Text
Check 'renew: every function of the renewal is in the script' ($renewFunctions.Count -eq 18) "$($renewFunctions.Count)"
$renewIndexes = @('Get-RenewableCertificate', 'Open-CertificateFolder', 'Get-PfxHash $renewal.Pfx', 'Get-SelfSignedPanelNames', 'New-SelfSignedPanelBundle', 'Stop-Panel', 'Lock-CertificateFolder $renewal.Tls $progress',
    'Assert-NoPanelProcesses', 'Assert-PlainCertificateFiles $renewal $true', 'Save-PanelBundle $bundle', 'Assert-NewCertificateFiles', 'Assert-InFolder $current[$kind] $hold', '[IO.File]::Replace($staged[$kind]',
    'Unlock-CertificateFolder $renewal.Tls $progress', 'Start-Panel', 'Confirm-PanelCertificate', 'Add-TrustedCertificate $renewal.Cer', 'Remove-InstallerTrust $renewal.Thumbprint', 'Remove-Item -LiteralPath $file -Force' | ForEach-Object { $renewalText.IndexOf($_) })
Check 'renew order: checks, the folder held, the certificate made in memory, then stop, lock, no process of the account, files written and checked, swap, unlock, start, answer, trust, and only then the old trust and the old files go' (
    @($renewIndexes | Where-Object { $_ -lt 0 }).Count -eq 0 -and ($renewIndexes -join ',') -ceq (@($renewIndexes | Sort-Object) -join ',')) ($renewIndexes -join ',')
Check 'renew hold: the certificate folder is held right after the first checks, until the swap and the cleanup end, and every file made goes through the hold''s check' (
    $renewalText -match '(?s)\$renewal = Get-RenewableCertificate.*?\$hold = Open-CertificateFolder \$renewal\.Tls\s+\$bundle = \$null\s+try \{.*Save-PanelBundle \$bundle \$staged\.Pfx \$staged\.Cer \$true \$hold\.Path.*Remove-Item -LiteralPath \$file -Force.*\}\s+finally \{\s+\$hold\.Dispose\(\).*# The new certificate where it is now' -and
    $installParts['Write-ExclusiveFile'] -match 'FinalPath\(\$stream\.SafeFileHandle\)' -and $installParts['Open-CertificateFolder'] -match 'GetFullPath' -and
    $installText -match '(?s)CreateFile\(path, access, share, IntPtr\.Zero, 3, 0x02000000 \| 0x00200000, IntPtr\.Zero\)' -and $installText -match 'OpenItem\(path, 0x81, 3\)')
Check 'renew rollback: any failure goes through Undo-CertificateRenewal, which puts the files back (Replace, or Move for a swap that stopped halfway), removes the new trust and starts the panel again' (
    $renewalText -match '(?s)catch \{\s+\$failure = \$_\.Exception\.Message.*?Undo-CertificateRenewal \$renewal \$progress \$wasRunning' -and
    $undoText -match [regex]::Escape('[IO.File]::Replace($swap.Backup, $swap.Target, [NullString]::Value)') -and $undoText -match [regex]::Escape('[IO.File]::Move($swap.Backup, $swap.Target)') -and
    $undoText -match [regex]::Escape('Remove-InstallerTrust $progress.Trusted $true') -and $undoText -match 'if \(\$wasRunning\)' -and
    $undoText -match '(?s)Lock-CertificateFolder \$renewal\.Tls \$progress.*Replace\(\$swap\.Backup.*Unlock-CertificateFolder \$renewal\.Tls \$progress.*Start-Panel' -and
    $renewalText -match '(?s)finally \{.*?if \(\$progress\.Stopped -and -not \$finished -and -not \$rolledBack\) \{\s+\$null = @\(Undo-CertificateRenewal \$renewal \$progress \$wasRunning\)' -and
    $renewalText -match 'Assert-PlainCertificateFiles \$renewal \$true' -and
    $renewalText.IndexOf('$progress.Trusted = $made.Thumbprint') -lt $renewalText.IndexOf('Add-TrustedCertificate $renewal.Cer') -and $renewalText.IndexOf('$progress.Swaps.Add') -lt $renewalText.IndexOf('[IO.File]::Replace'))
Check 'renew rollback: the old certificate''s trust is never touched by the rollback (it is removed only once all is done)' ($undoText -notmatch 'Remove-InstallerTrust \$renewal')
$renewCalls = @($renewFunctions | ForEach-Object { $_.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] }, $true) } | ForEach-Object { $_.GetCommandName() } | Where-Object { $_ } | Select-Object -Unique)
$renewForbidden = @($renewCalls | Where-Object { $_ -in 'Install-Panel', 'Update-Panel', 'Uninstall-Panel', 'Copy-Program', 'Write-PanelConfig', 'New-PanelDatabase', 'Remove-PanelDatabase', 'Invoke-Native', 'Invoke-NativeWithInput',
        'Invoke-Psql', 'Set-PrivateAcl', 'New-NetFirewallRule', 'Remove-NetFirewallRule', 'Restart-Service', 'Start-Process', 'New-KeyRingCertificate', 'Install-Agent', 'Uninstall-Agent', 'Update-Agent', 'Remove-TrustedCertificates', 'Invoke-PanelMigrations' })
Check 'renew: it never installs, updates or removes a program, nor changes the settings, the database, the firewall or an account' ($renewForbidden.Count -eq 0) ($renewForbidden -join ', ')
$renewStores = @($renewFunctions | Where-Object { $_.Extent.Text -match 'X509Store|Import-Certificate|certutil|Cert:\\' } | ForEach-Object Name)
Check 'renew: its functions reach a certificate store only through Add-TrustedCertificate and Remove-InstallerTrust (and only read it, in Get-InstallerTrust)' ($renewStores.Count -eq 0) ($renewStores -join ', ')
Check 'renew: it trusts only when the install trusted the old certificate (marked), and Add-TrustedCertificate''s checks apply to the new one' (
    $renewalText -match "(?s)if \(\`$renewal\.Trust -ceq 'marked'\) \{\s+Write-Step [^\n]+\s+if \(\(Get-InstallerTrust \`$made\.Thumbprint\) -cne 'none'\).*?Add-TrustedCertificate \`$renewal\.Cer 'localhost' \`$made\.Names \`$made\.Sha256" -and
    $renewalText -match "(?s)if \(\`$renewal\.Trust -ceq 'marked'\) \{\s+\`$listed = Read-Certificate \`$staged\.Cer.*?Assert-TrustableCertificate \`$listed \`$staged\.Cer 'localhost' \`$made\.Names")
# Where a password is an argument (`New-SelfSignedPanelFiles ... $renewal.Password`), the command it goes to.
$passwordUses = @($renewFunctions | ForEach-Object { $_.FindAll({ param($node) $node -is [Management.Automation.Language.MemberExpressionAst] -and $node.Member.Extent.Text -eq 'Password' }, $true) } | ForEach-Object {
        $command = $_.Parent; while ($command -and $command -isnot [Management.Automation.Language.CommandAst]) { $command = $command.Parent }
        if ($command) { $command.GetCommandName() }
    })
Check 'renew: the PFX''s password is only ever handed to the functions that make or open the PFX, never printed' ($passwordUses.Count -eq 3 -and @($passwordUses | Where-Object { $_ -notin 'New-SelfSignedPanelBundle', 'Assert-NewCertificateFiles', 'Get-PfxHash' }).Count -eq 0) ($passwordUses -join ', ')
$printedPasswords = @($renewFunctions | ForEach-Object { $_.FindAll({ param($node) $node -is [Management.Automation.Language.ExpandableStringExpressionAst] }, $true) } |
        Where-Object { @($_.NestedExpressions | Where-Object { $_.Extent.Text -match '(?i)password' }).Count -gt 0 } | ForEach-Object { $_.Extent.Text })
Check 'renew: no message interpolates a password' ($printedPasswords.Count -eq 0) ($printedPasswords -join ' | ')
Check 'renew: the PFX''s password is registered as a secret as soon as it is read' ((@($renewFunctions | Where-Object Name -eq 'Get-RenewableCertificate')[0].Extent.Text) -match 'Register-Secret \$password')
Check 'renew: it takes the files'' permissions from the old ones, and checks them' (
    (@($renewFunctions | Where-Object Name -eq 'Set-SameAccess')[0].Extent.Text) -match 'Get-FileAccess \$target\) -cne \$wanted\) \{ throw' -and $renewalText -match 'Set-SameAccess \$current\.Pfx \$staged\.Pfx' -and
    $renewalText -match 'Set-SameAccess \$backup\[\$kind\] \$current\[\$kind\]')
$mainBlockText = $mainText
Check 'renew: it runs before the checks of an agent package and of the other modes, with no download or runtime check' (
    $mainBlockText.IndexOf('Invoke-RenewCertificate') -ge 0 -and $mainBlockText.IndexOf('Invoke-RenewCertificate') -lt $mainBlockText.IndexOf('Get-AgentPackage') -and
    $mainBlockText.IndexOf('Invoke-RenewCertificate') -lt $mainBlockText.IndexOf('if ($Uninstall)') -and $mainBlockText.IndexOf('Assert-TrustedLocation') -lt $mainBlockText.IndexOf('Invoke-RenewCertificate') -and
    $renewalText -notmatch 'Get-RuntimeRequirements|New-SetupDownloads|Get-DotNetInstallers')
Check 'renew: its parameters are checked first and it reads no secrets file (an install''s one is not read and deleted by it)' (
    $mainBlockText.IndexOf('if ($RenewCertificate) { Assert-RenewArguments }') -ge 0 -and
    $mainBlockText.IndexOf('if ($RenewCertificate) { Assert-RenewArguments }') -lt $mainBlockText.IndexOf('Use-SetupInputs (Read-SetupInputs)') -and
    $mainBlockText -match '(?s)if \(\$RenewCertificate\) \{ Assert-RenewArguments \}\s+else \{ Use-SetupInputs \(Read-SetupInputs\) \}' -and
    $renewalText -notmatch 'Read-SetupInputs|Use-SetupInputs|Read-SecretsFile')
Check 'renew: the install checks -HostName the same way (Assert-HostNames)' ($installParts['Assert-PanelInstallable'] -match 'Assert-HostNames' -and $renewalText -match 'Assert-HostNames')
$guide = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\..\docs\guides\install.md')
Check 'renew: install.md documents it, the stale-address steps, the pin steps and that Let''s Encrypt is unaffected' (
    $guide.Contains('-RenewCertificate') -and $guide.Contains("Your server's IP changed") -and $guide.Contains('"panelCertificate"') -and $guide.Contains('PulsarAgent enroll') -and
    $guide -match "(?s)Your server's IP changed.*Let's Encrypt.*(not affected|unaffected|unchanged)")
$roadmap = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\..\docs\architecture\09-roadmap.md')
Check 'renew: the roadmap''s Phase 18 mentions it' ($roadmap -match '(?s)## Phase 18.*-RenewCertificate.*## Post-v1')

# --- Phase 19: the update manifest and its signature (Build-Release.ps1's functions run on files of this test's own) -------------

$manifestParts = Get-ScriptParts $buildReleasePath @('Test-UpdateVersion', 'Read-ReleaseNotes', 'ConvertTo-ManifestJson', 'New-UpdateManifest', 'Build-UpdateSigner', 'Invoke-UpdateSigner',
    'Invoke-UpdateSigning', 'Hide-UpdatePassphrase', 'Set-UpdatePassphrase', 'Remove-UpdatePassphrase', 'Restore-UpdatePassphrase') `
    @('UpdateManifestName', 'UpdateSignatureName', 'MaxManifestBytes', 'MaxManifestNotesBytes', 'MaxManifestFiles', 'MaxManifestFileBytes', 'UpdateVersionPattern', 'ManifestFileNamePattern', 'HiddenUpdatePassphrase')
foreach ($part in $manifestParts.Values) { . ([scriptblock]::Create($part)) }
$realUpdateSigner = (Get-Item 'function:Invoke-UpdateSigner').ScriptBlock

foreach ($version in '1.0.0', '0.0.1', '10.20.30', '123456789.0.1') { Check "manifest: $version is a release version" (Test-UpdateVersion $version) }
foreach ($version in '1.0', '01.0.0', '1.0.0-dev', '1.0.0.0', '0.0.0-dev', ' 1.0.0', "1.0.0`n", '1.0.0+abc', 'v1.0.0', '1.2.3456789012', '', '1.-1.0') {
    Check "manifest: '$($version -replace "`n", '\n')' is not a release version, so no manifest is written" (-not (Test-UpdateVersion $version))
}

$manifestRoot = Join-Path $Scratch ('pulsar-manifest-tests-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory $manifestRoot
try {
    function New-ReleaseFolder([string] $name, [string] $version = '1.2.3') {
        $folder = Join-Path $manifestRoot $name
        $null = New-Item -ItemType Directory $folder
        [IO.File]::WriteAllBytes((Join-Path $folder "pulsar-$version-windows-x64.zip"), [byte[]](1..200))
        [IO.File]::WriteAllText((Join-Path $folder "pulsar-$version-windows-x64.zip.sha256"), "unused`n")
        [IO.File]::WriteAllBytes((Join-Path $folder "pulsar-agent-$version-windows-x64.msi"), [byte[]](5..60))
        [IO.File]::WriteAllBytes((Join-Path $folder "PulsarSetup-$version.exe"), [byte[]](9..99))
        return $folder
    }
    $names = 'pulsar-1.2.3-windows-x64.zip', 'pulsar-agent-1.2.3-windows-x64.msi', 'PulsarSetup-1.2.3.exe'
    $folder = New-ReleaseFolder 'shape'
    $path = New-UpdateManifest $folder '1.2.3' '1.0.0' 'Fixes a thing.' $names ([DateTime]::new(2026, 10, 20, 12, 30, 45, [DateTimeKind]::Utc))
    $bytes = [IO.File]::ReadAllBytes($path)
    $json = [Text.Encoding]::UTF8.GetString($bytes)
    $manifest = $json | ConvertFrom-Json
    Check 'manifest: written as pulsar-update.json in the release folder, UTF-8 without a byte order mark' ((Split-Path -Leaf $path) -ceq 'pulsar-update.json' -and $bytes[0] -eq [byte][char]'{')
    Check 'manifest: camelCase product, version, minimumUpdateFrom, published (ISO-8601 UTC), notes and files' (
        (@($manifest.PSObject.Properties.Name) -join ',') -ceq 'product,version,minimumUpdateFrom,published,notes,files' -and $manifest.product -ceq 'pulsar' -and $manifest.version -ceq '1.2.3' -and
        $manifest.minimumUpdateFrom -ceq '1.0.0' -and $json -cmatch '"published":"2026-10-20T12:30:45Z"' -and $manifest.notes -ceq 'Fixes a thing.') $json
    $listed = @($manifest.files | ForEach-Object { $_.name })
    Check 'manifest: every file is named once, with its size and its lower-case SHA-256' (
        ($listed -join ',') -ceq ($names -join ',') -and @($manifest.files | Where-Object {
                $file = Join-Path $folder $_.name
                (@($_.PSObject.Properties.Name) -join ',') -ceq 'name,size,sha256' -and $_.size -eq (Get-Item $file).Length -and $_.sha256 -cmatch '^[0-9a-f]{64}$' -and
                $_.sha256 -ceq (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
            }).Count -eq 3) $json
    Check 'manifest: the checksum files, the manifest and its signature are never listed' (
        @($listed | Where-Object { $_ -like '*.sha256' -or $_ -like 'pulsar-update.json*' }).Count -eq 0 -and
        (Get-Thrown { New-UpdateManifest $folder '1.2.3' '1.0.0' $null @($names + 'pulsar-1.2.3-windows-x64.zip.sha256') }) -match 'cannot be named' -and
        (Get-Thrown { New-UpdateManifest $folder '1.2.3' '1.0.0' $null @($names + 'pulsar-update.json') }) -match 'cannot be named' -and
        (Get-Thrown { New-UpdateManifest $folder '1.2.3' '1.0.0' $null @($names + 'pulsar-update.json.sig') }) -match 'cannot be named')
    Check 'manifest: Build-Release.ps1 lists the published files but the checksum files, and publishes the manifest and its signature with the rest' (
        $buildRelease -match "\`$manifestFiles = @\(\`$published \| Where-Object \{ \`$_ -notlike '\*\.sha256' -and \(Test-Path -LiteralPath \(Join-Path \`$Work \`$_\) -PathType Leaf\) \}\)" -and
        $buildRelease -match '\$published \+= \$UpdateManifestName' -and $buildRelease -match '\$published \+= \$UpdateSignatureName' -and
        $buildRelease.IndexOf('$published += $UpdateManifestName') -lt $buildRelease.IndexOf('Publish-Release $Work $Output $published'))
    $noNotes = (New-UpdateManifest (New-ReleaseFolder 'nonotes') '1.2.3' '1.2.0' $null $names) | ForEach-Object { [IO.File]::ReadAllText($_) | ConvertFrom-Json }
    Check 'manifest: without notes the notes are null, and -MinimumUpdateFrom is what was given' ($null -eq $noNotes.notes -and $noNotes.minimumUpdateFrom -ceq '1.2.0')
    Check 'manifest: the version must be exactly major.minor.patch' ((Get-Thrown { New-UpdateManifest $folder '1.2.3-dev' '1.0.0' $null $names }) -match 'not major\.minor\.patch')
    Check '-MinimumUpdateFrom: newer than the version, or not a version, is refused' (
        (Get-Thrown { New-UpdateManifest $folder '1.2.3' '1.2.4' $null $names }) -match 'must be major\.minor\.patch and not newer' -and
        (Get-Thrown { New-UpdateManifest $folder '1.2.3' '1.0' $null $names }) -match 'must be major\.minor\.patch' -and
        $null -eq (Get-Thrown { New-UpdateManifest $folder '1.2.3' '1.2.3' $null $names }))
    Check 'manifest: the release''s ZIP must be listed' ((Get-Thrown { New-UpdateManifest $folder '1.2.3' '1.0.0' $null @('PulsarSetup-1.2.3.exe') }) -match 'must list the release''s ZIP')
    Check 'manifest: a file that is not there, an empty one and a name the programs refuse are refused' (
        (Get-Thrown { New-UpdateManifest $folder '1.2.3' '1.0.0' $null @($names + 'gone.exe') }) -match 'gone\.exe is not in' -and
        (Get-Thrown { New-UpdateManifest $folder '1.2.3' '1.0.0' $null @($names + '.hidden') }) -match 'cannot be named' -and
        (Get-Thrown { New-UpdateManifest $folder '1.2.3' '1.0.0' $null @($names + 'a b.exe') }) -match 'cannot be named' -and
        (Get-Thrown { New-UpdateManifest $folder '1.2.3' '1.0.0' $null @($names + '..\x.exe') }) -match 'cannot be named')
    [IO.File]::WriteAllBytes((Join-Path $folder 'empty.exe'), [byte[]]@())
    Check 'manifest: an empty file is refused' ((Get-Thrown { New-UpdateManifest $folder '1.2.3' '1.0.0' $null @($names + 'empty.exe') }) -match 'is 0 bytes')
    $many = New-ReleaseFolder 'many'
    $manyNames = @($names)
    foreach ($index in 1..14) { [IO.File]::WriteAllBytes((Join-Path $many "extra$index.bin"), [byte[]](1..3)); $manyNames += "extra$index.bin" }
    Check 'manifest: 16 files are listed, 17 are refused' (
        $manyNames.Count -eq 17 -and (Get-Thrown { New-UpdateManifest $many '1.2.3' '1.0.0' $null $manyNames }) -match 'at most 16 files' -and
        $null -eq (Get-Thrown { New-UpdateManifest $many '1.2.3' '1.0.0' $null @($manyNames | Select-Object -First 16) }))

    # The notes: UTF-8 plain text of at most 16 KB, the characters the programs show as they are.
    $notesFile = Join-Path $manifestRoot 'notes.txt'
    [IO.File]::WriteAllBytes($notesFile, [byte[]](0xEF, 0xBB, 0xBF) + [Text.Encoding]::UTF8.GetBytes("First line`r`nSecond line`r`n`r`n"))
    Check 'notes: a byte order mark is dropped, line breaks become \n and trailing ones go' ((Read-ReleaseNotes $notesFile) -ceq "First line`nSecond line")
    [IO.File]::WriteAllBytes($notesFile, [byte[]]((1..(16 * 1024) | ForEach-Object { [byte][char]'a' })))
    Check 'notes: 16 KB exactly is fine' ((Read-ReleaseNotes $notesFile).Length -eq 16384)
    [IO.File]::WriteAllBytes($notesFile, [byte[]]((1..(16 * 1024 + 1) | ForEach-Object { [byte][char]'a' })))
    Check 'notes: one byte more is refused' ((Get-Thrown { Read-ReleaseNotes $notesFile }) -match 'longer than 16 KB')
    [IO.File]::WriteAllBytes($notesFile, [byte[]](0x61, 0xFF, 0xFE, 0x62))
    Check 'notes: text that is not UTF-8 is refused' ((Get-Thrown { Read-ReleaseNotes $notesFile }) -match 'not UTF-8')
    foreach ($control in [char]0, [char]7, [char]27, [char]0x7F) {
        [IO.File]::WriteAllText($notesFile, "bad ${control}text")
        Check "notes: control character $([int]$control) is refused (plain text only)" ((Get-Thrown { Read-ReleaseNotes $notesFile }) -match 'control characters')
    }
    [IO.File]::WriteAllText($notesFile, "  `r`n ")
    Check 'notes: an empty file is refused' ((Get-Thrown { Read-ReleaseNotes $notesFile }) -match 'is empty')
    Check 'notes: a file that is not there is refused' ((Get-Thrown { Read-ReleaseNotes (Join-Path $manifestRoot 'nothing.txt') }) -match 'existing text file')
    $tricky = "Fixes <b>bold</b> & 'single' `"double`" $([char]92)u003c and \\ and tab`tand u$([char]0xFC)n$([char]0xEF)code"
    $parsed = [IO.File]::ReadAllText((New-UpdateManifest (New-ReleaseFolder 'tricky') '1.2.3' '1.0.0' $tricky $names)) | ConvertFrom-Json
    $trickyJson = [IO.File]::ReadAllText((Join-Path $manifestRoot 'tricky\pulsar-update.json'))
    Check 'manifest: notes with < > & '' \ and quotes come out as they went in, and < > & '' are not written as six-byte escapes' (
        $parsed.notes -ceq $tricky -and $trickyJson.Contains('<b>bold</b> & ''single''') -and -not $trickyJson.Contains("$([char]92)u003c</b>") -and $trickyJson.Contains('\\u003c and \\\\')) $trickyJson
    Check 'manifest: notes over 16 KB are refused here too' ((Get-Thrown { New-UpdateManifest $folder '1.2.3' '1.0.0' ('a' * (16 * 1024 + 1)) $names }) -match 'longer than 16 KB')
    Check 'manifest: the largest notes still leave the manifest under the programs'' 64 KB' (
        ((Get-Item (New-UpdateManifest (New-ReleaseFolder 'large') '1.2.3' '1.0.0' ('"' * (16 * 1024)) $names)).Length) -lt 64 * 1024)

    # Signing: the signer is mocked (it asks for a passphrase at a console). It signs, then verify checks the pinned keys.
    $signerCalls = New-Object Collections.Generic.List[string]
    $signerFails = @{}
    function Invoke-UpdateSigner([string] $signer, [string[]] $arguments) {
        $signerCalls.Add("$signer $($arguments -join ' ')")
        if ($signerFails.ContainsKey($arguments[0])) { throw $signerFails[$arguments[0]] }
        if ($arguments[0] -ceq 'sign' -and -not $signerFails.ContainsKey('nosig')) { Set-Content -LiteralPath "$($arguments[2]).sig" -Value 'signature' }
    }
    $signFolder = New-ReleaseFolder 'sign'
    $signManifest = New-UpdateManifest $signFolder '1.2.3' '1.0.0' $null $names
    Set-Content -LiteralPath "$signManifest.sig" -Value 'a stale signature'
    Invoke-UpdateSigning 'signer.exe' 'the.key' $signManifest
    Check 'signing: the manifest is signed with the key file, then verified against the pinned keys (no --trust)' (
        $signerCalls.Count -eq 2 -and $signerCalls[0] -ceq "signer.exe sign the.key $signManifest" -and $signerCalls[1] -ceq "signer.exe verify $signManifest" -and (Get-Content -LiteralPath "$signManifest.sig") -ceq 'signature')
    $signerCalls.Clear()
    $signerFails['verify'] = 'update.unknown_key: a key this version does not trust'
    Check 'signing: a signature that does not verify fails the build' ((Get-Thrown { Invoke-UpdateSigning 'signer.exe' 'the.key' $signManifest }) -match 'unknown_key' -and $signerCalls.Count -eq 2)
    $signerCalls.Clear(); $signerFails.Clear(); $signerFails['sign'] = 'The key is not in UpdateKeys.Trusted'
    Check 'signing: a key the programs do not trust is not signed with, and nothing is verified' ((Get-Thrown { Invoke-UpdateSigning 'signer.exe' 'the.key' $signManifest }) -match 'not in UpdateKeys' -and $signerCalls.Count -eq 1 -and -not (Test-Path -LiteralPath "$signManifest.sig"))
    $signerCalls.Clear(); $signerFails.Clear(); $signerFails['nosig'] = $true
    Check 'signing: a signer that writes no signature fails the build' ((Get-Thrown { Invoke-UpdateSigning 'signer.exe' 'the.key' $signManifest }) -match 'did not write' -and $signerCalls.Count -eq 1)
    $signerCalls.Clear(); $signerFails.Clear()

    # The real Invoke-UpdateSigner, with cmd.exe as the signer: the passphrase of the environment reaches it for the call
    # only, a failure is an error, and an argument with a quote is refused.
    Set-Item function:Invoke-UpdateSigner $realUpdateSigner
    $script:HiddenUpdatePassphrase = $null
    $markerFolder = Join-Path $manifestRoot 'marker'
    $null = New-Item -ItemType Directory $markerFolder; Set-Content -LiteralPath (Join-Path $markerFolder 'marker.txt') 'x'
    $env:PULSAR_UPDATE_KEY_PASSPHRASE = $markerFolder
    Hide-UpdatePassphrase
    $hidden = [Environment]::GetEnvironmentVariable('PULSAR_UPDATE_KEY_PASSPHRASE', 'Process')
    Check 'passphrase: PULSAR_UPDATE_KEY_PASSPHRASE is out of the environment the builds inherit' ($null -eq $hidden -and $script:HiddenUpdatePassphrase -ceq $markerFolder)
    # Windows' where.exe as the signer: it looks for marker.txt in the folder the passphrase variable holds (here, a folder of the test), so it finds it only if the variable reached it.
    $whereExe = Join-Path ([Environment]::SystemDirectory) 'where.exe'
    $seen = Get-Thrown { Invoke-UpdateSigner $whereExe @('$PULSAR_UPDATE_KEY_PASSPHRASE:marker.txt') }
    Check 'passphrase: the signer gets it in its environment while it runs' ($null -eq $seen) "$seen"
    Check 'passphrase: ...and it is gone from this process again afterwards' ($null -eq [Environment]::GetEnvironmentVariable('PULSAR_UPDATE_KEY_PASSPHRASE', 'Process'))
    Check 'signing: a signer that fails is an error naming its exit code' ((Get-Thrown { Invoke-UpdateSigner $whereExe @('nothing-like-this.exe') }) -match 'failed \(exit code 1\)')
    Check 'signing: an argument with a quote is refused, not passed on' ((Get-Thrown { Invoke-UpdateSigner $whereExe @('say "hi"') }) -match 'A quote cannot go')
    $failed = Get-Thrown { Invoke-UpdateSigner $whereExe @('nothing-like-this.exe') }
    Check 'passphrase: ...also when the signer fails' ($null -eq [Environment]::GetEnvironmentVariable('PULSAR_UPDATE_KEY_PASSPHRASE', 'Process'))
    Restore-UpdatePassphrase
    Check 'passphrase: restored to the shell when the script ends' ([Environment]::GetEnvironmentVariable('PULSAR_UPDATE_KEY_PASSPHRASE', 'Process') -ceq $markerFolder -and $null -eq $script:HiddenUpdatePassphrase)
    Remove-Item Env:PULSAR_UPDATE_KEY_PASSPHRASE
}
finally {
    Remove-Item -LiteralPath $manifestRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item Env:PULSAR_UPDATE_KEY_PASSPHRASE -ErrorAction SilentlyContinue
}

# How Build-Release.ps1 uses them (read, never run): the key signs last, after every file is made, and never when the build failed.
$signCall = $buildRelease.IndexOf('Invoke-UpdateSigning $updateSigner $UpdateSigningKey $manifestPath')
$publishCall = $buildRelease.IndexOf('Publish-Release $Work $Output $published')
$tryStart = $buildRelease.IndexOf('$Work = Join-Path $Output')
Check 'update signing: the manifest is signed after the last checksum is written and every package is built, right before the release is published' (
    $signCall -gt $buildRelease.LastIndexOf('Write-Checksum $built') -and $signCall -gt $buildRelease.LastIndexOf("Build-Setup `$iscc 'PulsarSetup.iss'") -and $signCall -gt $buildRelease.IndexOf('Compress-Archive') -and $signCall -lt $publishCall -and $publishCall -gt 0)
Check 'update signing: it is in the build''s try block only (a build that threw never reaches it), once, and in no finally' (
    ([regex]::Matches($buildRelease, 'Invoke-UpdateSigning \$updateSigner')).Count -eq 1 -and $signCall -gt $tryStart -and $buildRelease.IndexOf("`nfinally {") -gt $publishCall -and
    $buildRelease.Substring($buildRelease.IndexOf("`nfinally {")) -notmatch 'Invoke-UpdateSign')
Check 'update signing: only with -UpdateSigningKey, and a manifest without it is published with a warning, not silently' (
    $buildRelease -match 'if \(\$UpdateSigningKey\) \{\s+Invoke-UpdateSigning' -and $buildRelease -match 'is not signed \(no -UpdateSigningKey\)')
Check 'update signing: -RequireUpdateSignature fails a build with no key, and one whose version has no manifest, before the long builds' (
    $buildRelease -match "-RequireUpdateSignature: this release would have no signed update manifest" -and $buildRelease -match '-RequireUpdateSignature: the version ''\$Version'' is not exactly major\.minor\.patch' -and
    $buildRelease.IndexOf('-RequireUpdateSignature: this release') -lt $buildRelease.IndexOf('dotnet publish (Join-Path $repo ''src\Pulsar.Api'')'))
Check 'update signing: a version that is not major.minor.patch writes no manifest (a warning says so)' ($buildRelease -match '\$manifestWanted = Test-UpdateVersion \$Version' -and $buildRelease -match 'no update manifest is written')
Check 'update signing: the passphrase is never a parameter and not on a command line; it is hidden from the builds and restored at the end' (
    @($buildRelease | Select-String -Pattern '\[string\] \$\w*(Passphrase|Password)' -AllMatches).Count -eq 0 -and
    $buildRelease.LastIndexOf('Hide-UpdatePassphrase') -lt $buildRelease.IndexOf('npm ci --ignore-scripts') -and $buildRelease -match '(?s)finally \{[^}]*Restore-UpdatePassphrase')
Check 'update signing: the signer is built in locked mode before the long builds and run from the work folder, not from the output folder' (
    $buildRelease -match "Build-UpdateSigner \`$repo \(Join-Path \`$Work 'update-signer'\)" -and $buildRelease.IndexOf('Build-UpdateSigner $repo') -lt $buildRelease.IndexOf('npm ci --ignore-scripts') -and
    $buildRelease -match 'tools\\Pulsar\.UpdateSigner.*-p:RestoreLockedMode=true')
$buildParams = [Management.Automation.Language.Parser]::ParseFile((Resolve-Path $buildReleasePath).ProviderPath, [ref] $null, [ref] $null).ParamBlock.Parameters
$minimumParameter = @($buildParams | Where-Object { $_.Name.VariablePath.UserPath -eq 'MinimumUpdateFrom' })
Check '-MinimumUpdateFrom defaults to 1.0.0 and takes only major.minor.patch; -ReleaseNotes and -UpdateSigningKey are file names' (
    $minimumParameter.Count -eq 1 -and $minimumParameter[0].DefaultValue.Value -ceq '1.0.0' -and $minimumParameter[0].Extent.Text -match '\[ValidatePattern\(' -and
    @($buildParams | Where-Object { $_.Name.VariablePath.UserPath -in 'ReleaseNotes', 'UpdateSigningKey', 'RequireUpdateSignature' }).Count -eq 3)

# --- Phase 19: Publish-GitHubRelease.ps1 (gh and the signer are mocked) -------------------------

$publishPath = Join-Path $PSScriptRoot '..\release\Publish-GitHubRelease.ps1'
$publishText = Get-Content -Raw -LiteralPath $publishPath
$publishParts = Get-ScriptParts $publishPath @('Invoke-Gh', 'Test-GhInstalled', 'Assert-ManifestVerifies', 'Get-UpdateSigner', 'Test-ReleaseFolder', 'Assert-GitHubReady') @('Repository', 'UpdateManifestName', 'UpdateSignatureName')
foreach ($part in $publishParts.Values) { . ([scriptblock]::Create($part)) }
Check 'publish: only the project''s own repository' ($Repository -ceq 'Ayrton09/Pulsar' -and $publishText -match "'--repo', \`$Repository")

$publishRoot = Join-Path $Scratch ('pulsar-publish-tests-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory $publishRoot
try {
    $ghCalls = New-Object Collections.Generic.List[string]
    $ghState = @{ Installed = $true; Visibility = 'PUBLIC'; ReleaseExists = $false; TagExists = $false; Broken = $false }
    function Test-GhInstalled { return $ghState.Installed }
    function Invoke-Gh([string[]] $arguments) {
        $ghCalls.Add($arguments -join ' ')
        if ($ghState.Broken) { return [pscustomobject]@{ ExitCode = 4; Output = 'gh: To use GitHub CLI, run: gh auth login' } }
        if ($arguments[0] -ceq 'repo') { return [pscustomobject]@{ ExitCode = 0; Output = "{`"visibility`":`"$($ghState.Visibility)`"}" } }
        if ($arguments[0] -ceq 'release' -and $arguments[1] -ceq 'view') {
            if ($ghState.ReleaseExists) { return [pscustomobject]@{ ExitCode = 0; Output = '{}' } }
            return [pscustomobject]@{ ExitCode = 1; Output = 'release not found' }
        }
        if ($arguments[0] -ceq 'api') {
            if ($ghState.TagExists) { return [pscustomobject]@{ ExitCode = 0; Output = '{}' } }
            return [pscustomobject]@{ ExitCode = 1; Output = 'gh: Not Found (HTTP 404)' }
        }
        return [pscustomobject]@{ ExitCode = 0; Output = '' }
    }
    $verifyCalls = New-Object Collections.Generic.List[string]
    $verifyFails = $false
    function Assert-ManifestVerifies([string] $signer, [string] $manifest) {
        $verifyCalls.Add($manifest)
        if ($verifyFails) { throw "The signature of $manifest does not verify against the keys Pulsar trusts: update.bad_signature" }
    }
    function New-PublishFolder([string] $name, [string] $version = '1.2.3') {
        $folder = Join-Path $publishRoot $name
        $null = New-Item -ItemType Directory $folder
        $files = [ordered]@{ "pulsar-$version-windows-x64.zip" = [byte[]](1..120); "pulsar-agent-$version-windows-x64.msi" = [byte[]](3..80) }
        foreach ($file in $files.Keys) {
            [IO.File]::WriteAllBytes((Join-Path $folder $file), $files[$file])
            $hash = (Get-FileHash -LiteralPath (Join-Path $folder $file) -Algorithm SHA256).Hash.ToLowerInvariant()
            [IO.File]::WriteAllText((Join-Path $folder "$file.sha256"), "$hash  $file`n")
        }
        # Files of earlier builds in the same folder, which are not this release's.
        [IO.File]::WriteAllBytes((Join-Path $folder 'pulsar-1.0.0-windows-x64.zip'), [byte[]](7, 7, 7))
        $manifest = New-UpdateManifest $folder $version '1.0.0' 'The notes of the test.' @($files.Keys)
        Set-Content -LiteralPath "$manifest.sig" -Value 'signature'
        return $folder
    }
    $good = New-PublishFolder 'good'
    $verifyCalls.Clear()
    $release = Test-ReleaseFolder $good '1.2.3' 'signer.exe'
    $uploads = @($release.Files | ForEach-Object { Split-Path -Leaf $_ })
    Check 'publish: the manifest''s files, their checksum files, the manifest and its signature are uploaded, and nothing else in the folder' (
        ($uploads -join ',') -ceq 'pulsar-1.2.3-windows-x64.zip,pulsar-1.2.3-windows-x64.zip.sha256,pulsar-agent-1.2.3-windows-x64.msi,pulsar-agent-1.2.3-windows-x64.msi.sha256,pulsar-update.json,pulsar-update.json.sig') ($uploads -join ',')
    Check 'publish: the signature is verified (the signer''s verify, the pinned keys) on the manifest of the folder' ($verifyCalls.Count -eq 1 -and $verifyCalls[0] -ceq (Join-Path $good 'pulsar-update.json'))
    $verifyFails = $true
    Check 'publish: a signature that does not verify refuses the release' ((Get-Thrown { Test-ReleaseFolder $good '1.2.3' 'signer.exe' }) -match 'does not verify against the keys Pulsar trusts')
    $verifyFails = $false
    $missing = New-PublishFolder 'nosig'
    Remove-Item (Join-Path $missing 'pulsar-update.json.sig')
    Check 'publish: a folder without the signature is refused, telling how to build one' ((Get-Thrown { Test-ReleaseFolder $missing '1.2.3' 'signer.exe' }) -match 'pulsar-update\.json\.sig is missing.*-UpdateSigningKey')
    Remove-Item (Join-Path $missing 'pulsar-update.json')
    Check 'publish: a folder without the manifest is refused' ((Get-Thrown { Test-ReleaseFolder $missing '1.2.3' 'signer.exe' }) -match 'pulsar-update\.json is missing')
    Check 'publish: a manifest for another version is refused' ((Get-Thrown { Test-ReleaseFolder $good '1.2.4' 'signer.exe' }) -match 'for version 1\.2\.3, not 1\.2\.4')
    $other = New-PublishFolder 'gone'
    Remove-Item (Join-Path $other 'pulsar-agent-1.2.3-windows-x64.msi')
    Check 'publish: a file the manifest names that is not there is refused' ((Get-Thrown { Test-ReleaseFolder $other '1.2.3' 'signer.exe' }) -match 'msi, named by the manifest, is not in')
    $other = New-PublishFolder 'size'
    [IO.File]::WriteAllBytes((Join-Path $other 'pulsar-1.2.3-windows-x64.zip'), [byte[]](1..121))
    Check 'publish: a file of another size than the manifest''s is refused' ((Get-Thrown { Test-ReleaseFolder $other '1.2.3' 'signer.exe' }) -match 'is 121 bytes; the manifest says 120')
    $other = New-PublishFolder 'hash'
    [IO.File]::WriteAllBytes((Join-Path $other 'pulsar-1.2.3-windows-x64.zip'), [byte[]](2..121))
    Check 'publish: a file with the manifest''s size and another SHA-256 is refused' ((Get-Thrown { Test-ReleaseFolder $other '1.2.3' 'signer.exe' }) -match 'has SHA-256 [0-9a-f]{64}; the manifest says')
    $other = New-PublishFolder 'checksum'
    [IO.File]::WriteAllText((Join-Path $other 'pulsar-1.2.3-windows-x64.zip.sha256'), ('0' * 64) + "  pulsar-1.2.3-windows-x64.zip`n")
    Check 'publish: a .sha256 file that disagrees with the manifest is refused' ((Get-Thrown { Test-ReleaseFolder $other '1.2.3' 'signer.exe' }) -match 'does not say what the manifest says')

    Assert-GitHubReady 'Ayrton09/Pulsar' 'v1.2.3' $false
    Check 'publish: a public repository with no such release or tag is ready' ($ghCalls -contains 'repo view Ayrton09/Pulsar --json visibility' -and $ghCalls -contains 'release view v1.2.3 --repo Ayrton09/Pulsar' -and $ghCalls -contains 'api repos/Ayrton09/Pulsar/git/ref/tags/v1.2.3')
    $ghState.Installed = $false
    Check 'publish: without gh it stops, telling where to get it' ((Get-Thrown { Assert-GitHubReady 'Ayrton09/Pulsar' 'v1.2.3' $false }) -match 'GitHub CLI \(gh\) is not installed')
    $ghState.Installed = $true; $ghState.Visibility = 'PRIVATE'
    Check 'publish: a private repository is refused (installations download without signing in)' ((Get-Thrown { Assert-GitHubReady 'Ayrton09/Pulsar' 'v1.2.3' $false }) -match 'is PRIVATE.*without signing in')
    Check 'publish: ...unless -AllowPrivate' ($null -eq (Get-Thrown { Assert-GitHubReady 'Ayrton09/Pulsar' 'v1.2.3' $true }))
    $ghState.Visibility = 'PUBLIC'; $ghState.ReleaseExists = $true
    Check 'publish: a release that exists already is refused' ((Get-Thrown { Assert-GitHubReady 'Ayrton09/Pulsar' 'v1.2.3' $false }) -match 'release v1\.2\.3 exists already')
    $ghState.ReleaseExists = $false; $ghState.TagExists = $true
    Check 'publish: a tag that exists already is refused' ((Get-Thrown { Assert-GitHubReady 'Ayrton09/Pulsar' 'v1.2.3' $false }) -match 'tag v1\.2\.3 exists already')
    $ghState.TagExists = $false; $ghState.Broken = $true
    Check 'publish: gh failing (not signed in) is an error, not taken for "no such release"' ((Get-Thrown { Assert-GitHubReady 'Ayrton09/Pulsar' 'v1.2.3' $false }) -match 'gh could not read')
    $ghState.Broken = $false

    # The script's own steps (everything after its functions), run in this process with gh and the signer mocked: what it asks gh to do.
    $publishAst = [Management.Automation.Language.Parser]::ParseFile((Resolve-Path $publishPath).ProviderPath, [ref] $null, [ref] $null)
    $statements = @($publishAst.EndBlock.Statements)
    $first = (0..($statements.Count - 1) | Where-Object { $statements[$_].Extent.Text -like '$tag = *' } | Select-Object -First 1)
    $publishMain = [scriptblock]::Create((@($statements[$first..($statements.Count - 1)] | ForEach-Object { $_.Extent.Text }) -join "`n"))
    $fakeSigner = Join-Path $publishRoot 'pulsar-update-signer.exe'
    Set-Content -LiteralPath $fakeSigner -Value 'not run: Assert-ManifestVerifies is mocked'
    function Get-UpdateSigner([string] $path, [string] $folder) { return $path }
    function Invoke-PublishMain([hashtable] $options = @{}) {
        $ghCalls.Clear(); $verifyCalls.Clear()
        $ghState.Visibility = $(if ($options['Visibility']) { $options['Visibility'] } else { 'PUBLIC' })
        $verifyFails = [bool]$options['VerifyFails']
        & {
            $Version = $(if ($options['Version']) { $options['Version'] } else { '1.2.3' }); $Folder = $good; $Notes = $options['Notes']; $Target = 'main'; $Signer = $fakeSigner
            $AllowPrivate = [switch][bool]$options['AllowPrivate']; $CheckOnly = [switch][bool]$options['CheckOnly']
            Get-Caught { $null = & $publishMain }
        }
    }
    $caught = Invoke-PublishMain
    $create = @($ghCalls | Where-Object { $_ -like 'release create*' })
    Check 'publish: the script''s steps end well and create one release' ($null -eq $caught -and $create.Count -eq 1) "$caught"
    if ($create.Count -eq 1) {
        $line = $create[0]
        Check 'publish: gh release create v<version> <files> --repo Ayrton09/Pulsar --title ... --notes-file ... --target ... as the latest release' (
            $line -match '^release create v1\.2\.3 ' -and $line -match '--repo Ayrton09/Pulsar' -and $line -match '--title Pulsar 1\.2\.3' -and $line -match '--notes-file .+notes\.md' -and $line -match '--target main' -and $line -match '--latest' -and
            $line -notmatch '--draft|--prerelease' -and $line -match 'pulsar-update\.json ' -and $line -match 'pulsar-update\.json\.sig ' -and $line -match 'zip\.sha256' -and $line -notmatch 'pulsar-1\.0\.0') $line
        Check 'publish: the checks all come before the upload (visibility, release and tag, then create)' (
            $ghCalls[0] -like 'repo view*' -and $ghCalls[1] -like 'release view*' -and $ghCalls[2] -like 'api repos/*' -and $ghCalls[3] -like 'release create*' -and $ghCalls.Count -eq 4) ($ghCalls -join ' | ')
    }
    $caught = Invoke-PublishMain @{ Visibility = 'PRIVATE' }
    Check 'publish: a private repository ends the script before anything is uploaded' ($null -ne $caught -and $caught.Exception.Message -match 'is PRIVATE' -and @($ghCalls | Where-Object { $_ -like 'release create*' }).Count -eq 0) "$caught"
    $caught = Invoke-PublishMain @{ Visibility = 'PRIVATE'; AllowPrivate = $true }
    Check 'publish: -AllowPrivate publishes to a private repository' ($null -eq $caught -and @($ghCalls | Where-Object { $_ -like 'release create*' }).Count -eq 1) "$caught"
    $caught = Invoke-PublishMain @{ CheckOnly = $true }
    Check 'publish: -CheckOnly runs the checks and uploads nothing' ($null -eq $caught -and $ghCalls.Count -eq 3 -and @($ghCalls | Where-Object { $_ -like 'release create*' }).Count -eq 0) "$caught"
    $caught = Invoke-PublishMain @{ Version = '1.2.4' }
    Check 'publish: a folder built for another version uploads nothing (gh is not even asked)' ($null -ne $caught -and $caught.Exception.Message -match 'for version 1\.2\.3, not 1\.2\.4' -and $ghCalls.Count -eq 0) "$caught"
    $caught = Invoke-PublishMain @{ VerifyFails = $true }
    Check 'publish: a signature that does not verify stops the script before gh is even called' ($null -ne $caught -and $caught.Exception.Message -match 'does not verify' -and $ghCalls.Count -eq 0) "$caught"
    $notesText = Join-Path $publishRoot 'mynotes.md'
    [IO.File]::WriteAllText($notesText, 'My notes')
    $caught = Invoke-PublishMain @{ Notes = $notesText }
    Check 'publish: -Notes names the notes file given' ($null -eq $caught -and (@($ghCalls | Where-Object { $_ -like 'release create*' })[0]) -like '*--notes-file*mynotes.md*') "$caught"
    Check 'publish: the tag is made at a commit (-Target, or this checkout''s HEAD), no token is a parameter or an argument, and no draft or pre-release' (
        $publishText -match "'--target', \`$Target" -and $publishText -match 'rev-parse HEAD' -and $publishText -notmatch '(?i)GH_TOKEN|GITHUB_TOKEN|password|passphrase|--draft|--prerelease' -and $publishText -match "'--latest'")
    $order = @('Test-ReleaseFolder $Folder', 'Assert-GitHubReady $Repository', "'release', 'create'") | ForEach-Object { $publishText.IndexOf($_) }
    Check 'publish: the folder and the signature are checked, then GitHub, and only then the release is created' ($order[0] -gt 0 -and $order[0] -lt $order[1] -and $order[1] -lt $order[2]) ($order -join ',')
}
finally {
    Remove-Item -LiteralPath $publishRoot -Recurse -Force -ErrorAction SilentlyContinue
}
# The release workflow no longer runs on a tag (Publish-GitHubRelease.ps1 makes the tag, and Actions minutes are few).
Check 'workflow: release.yml is run by hand only: no tag or push trigger, workflow_dispatch with the version' (
    $releaseWorkflow -match '(?m)^on:\r?\n  workflow_dispatch:\r?\n    inputs:\r?\n      version:' -and $releaseWorkflow -notmatch '(?m)^\s*push:' -and $releaseWorkflow -notmatch '(?m)^\s*tags:' -and $releaseWorkflow -notmatch 'GITHUB_REF_NAME')
Check 'workflow: its header says releases are built and update-signed on the maintainer''s PC, the key never goes to GitHub, and a tag must not start a run' (
    $releaseWorkflow -match 'update-signed on the maintainer''s' -and $releaseWorkflow -match 'update key never goes to GitHub' -and $releaseWorkflow -match 'Pushing a tag does not start a run')

# --- Phase 19: -Update backs up, checks, rolls back and says how it ended (Install-Pulsar.ps1's functions, with mocked services,
# --- copies and health checks; folders under -Scratch) -------------------------------------------------------------------------

$updateParts = Get-ScriptParts $installScript @('Invoke-Native', 'New-SetupFolder', 'Get-UntrustedWritersMessage', 'Get-ShortVersion', 'Get-UpdateVersions', 'Initialize-UpdateFolder', 'Lock-Update', 'Unlock-Update',
    'Start-UpdateRun', 'Write-UpdateResult', 'New-UpdateBackup', 'Remove-OldUpdateBackups', 'Save-PreviousPrograms', 'Get-PanelConnection', 'Invoke-PanelPsql', 'Get-PanelMigrations',
    'Test-DatabaseChanged', 'Restore-RolledBackDatabase', 'Stop-UpdateService', 'Wait-UpdatedPanelReady', 'Wait-UpdatedAgentHealthy', 'Assert-UpdateHealthy', 'Undo-Update', 'Invoke-Update',
    'Wait-PanelReady', 'Test-InstalledHealthy', 'Write-UpdateJournal', 'Read-UpdateJournal', 'Remove-UpdateJournal', 'Resume-InterruptedUpdate', 'Repair-ProgramSwap', 'Move-ProgramFolder', 'Assert-RollbackPossible') `
    @('ExitRolledBack', 'UpdateRoot', 'UpdateBackups', 'UpdateBackupsKept', 'UpdatePrevious', 'UpdateResultFile', 'UpdateLockFile', 'UpdateJournal', 'UpdatePanelMinutes', 'UpdateAgentStableSeconds', 'UpdateAgentCheckMinutes', 'UpdateRun', 'UpdateLock')
# The variables built from one another (UpdateRoot, then the paths in it) are loaded in any order: the folder they start from is set first.
$DataRoot = Join-Path $Scratch 'pulsar-update-data-root'; $UpdateRoot = Join-Path $DataRoot 'Updates'
foreach ($part in $updateParts.Values) { . ([scriptblock]::Create($part)) }
$realUpdateFunctions = @{}
foreach ($function in 'Initialize-UpdateFolder', 'Write-UpdateResult', 'Wait-UpdatedAgentHealthy', 'Restore-RolledBackDatabase', 'Invoke-PanelPsql', 'Wait-UpdatedPanelReady') { $realUpdateFunctions[$function] = (Get-Item "function:$function").ScriptBlock }
$restoreUpdateFunctions = { foreach ($function in $realUpdateFunctions.Keys) { Set-Item "function:$function" $realUpdateFunctions[$function] } }

foreach ($case in @(@('1.2.3', '1.2.3'), @('1.2.3.0', '1.2.3'), @('1.2.3+abc', '1.2.3'), @('1.2.3-dev', '1.2.3'), @('10.20.30.40', '10.20.30'), @('1.2', $null), @('', $null), @('abc', $null), @('1.2.3x', $null))) {
    Check "update: the version '$($case[0])' is shown as '$($case[1])'" ((Get-ShortVersion $case[0]) -ceq $case[1])
}
Check 'update: the database changed when the migrations differ, or when either is unknown' (
    -not (Test-DatabaseChanged "a`nb" "a`nb") -and (Test-DatabaseChanged "a`nb" "a`nb`nc") -and (Test-DatabaseChanged "a`nb" "a") -and (Test-DatabaseChanged $null "a") -and (Test-DatabaseChanged "a" $null) -and
    (Test-DatabaseChanged $null $null) -and -not (Test-DatabaseChanged '' ''))

$upRoot = Join-Path $Scratch ('pulsar-update-tests-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory $upRoot
try {
    # --- Version, connection and folder helpers ---
    $PanelConfig = Join-Path $upRoot 'settings.json'
    [IO.File]::WriteAllText($PanelConfig, '{"ConnectionStrings": {"Pulsar": "Host=localhost;Port=5433;Database=pulsar;Username=pulsar;Password=s3cretPassw0rd"}}')
    $SetupSecrets.Clear()
    $db = Get-PanelConnection
    Check 'update: the panel''s connection is read from its settings (the port too), and its password is registered as a secret' (
        $db['host'] -ceq 'localhost' -and $db['port'] -ceq '5433' -and $db['database'] -ceq 'pulsar' -and $db['username'] -ceq 'pulsar' -and $db['password'] -ceq 's3cretPassw0rd' -and $SetupSecrets.Contains('s3cretPassw0rd'))
    [IO.File]::WriteAllText($PanelConfig, '{"ConnectionStrings": {"Pulsar": "Host=db.example.com;Database=pulsar;Username=u;Password=pw1234"}}')
    Check 'update: the port defaults to 5432' ((Get-PanelConnection)['port'] -ceq '5432')
    foreach ($bad in '{"ConnectionStrings": {"Pulsar": "Host=localhost;Database=pulsar"}}', '{"ConnectionStrings": {}}', '{}', 'not json') {
        [IO.File]::WriteAllText($PanelConfig, $bad)
        Check "update: settings that are not a panel's ($bad) give no connection" ($null -eq (Get-PanelConnection))
    }
    $SetupSecrets.Clear()

    function Get-FakePrograms {
        $PanelDir = Join-Path $upRoot 'Program Files\Panel'; $AgentDir = Join-Path $upRoot 'Program Files\Agent'
        foreach ($folder in $PanelDir, $AgentDir) { if (Test-Path -LiteralPath $folder) { Remove-Item -LiteralPath $folder -Recurse -Force } }
        $null = New-Item -ItemType Directory -Force (Join-Path $PanelDir 'wwwroot\assets'), (Join-Path $PanelDir 'downloads'), $AgentDir
        Set-Content -LiteralPath (Join-Path $PanelDir 'Pulsar.Api.exe') 'panel 1.0.0'
        Set-Content -LiteralPath (Join-Path $PanelDir 'wwwroot\assets\app.js') 'old app'
        Set-Content -LiteralPath (Join-Path $PanelDir 'downloads\PulsarAgentSetup.exe') 'old setup'
        Set-Content -LiteralPath (Join-Path $PanelDir 'appsettings.Production.json') '{"settings": "of the panel"}'
        Set-Content -LiteralPath (Join-Path $AgentDir 'PulsarAgent.exe') 'agent 1.0.0'
        Set-Content -LiteralPath (Join-Path $AgentDir 'appsettings.Production.json') '{"settings": "of the agent"}'
        return $PanelDir, $AgentDir
    }

    # Save-PreviousPrograms: the programs about to be replaced, whole, after what an earlier update left is gone.
    $PanelDir, $AgentDir = Get-FakePrograms
    $UpdatePrevious = Join-Path $upRoot 'previous'
    $null = New-Item -ItemType Directory -Force (Join-Path $UpdatePrevious 'Panel\old'), (Join-Path $UpdatePrevious 'Agent')
    Set-Content -LiteralPath (Join-Path $UpdatePrevious 'Panel\old\left-over.txt') 'from an earlier update'
    function Initialize-UpdateFolder([string] $path) { $script:upFolders.Add($path) }
    # Not elevated, the real one makes a folder its owner (this account) fails its own check.
    $realNewSetupFolder = (Get-Item function:New-SetupFolder).ScriptBlock
    function New-SetupFolder([string] $path) { $null = New-Item -ItemType Directory $path }
    function Write-SetupLog([string] $text) { }
    $script:upFolders = New-Object Collections.Generic.List[string]
    Save-PreviousPrograms @('panel', 'agent')
    Check 'update: the programs about to be replaced are copied whole to previous\Panel and previous\Agent (nested files and settings too)' (
        (Get-Content -LiteralPath (Join-Path $UpdatePrevious 'Panel\wwwroot\assets\app.js')) -ceq 'old app' -and (Get-Content -LiteralPath (Join-Path $UpdatePrevious 'Panel\Pulsar.Api.exe')) -ceq 'panel 1.0.0' -and
        (Test-Path (Join-Path $UpdatePrevious 'Panel\downloads\PulsarAgentSetup.exe')) -and (Test-Path (Join-Path $UpdatePrevious 'Panel\appsettings.Production.json')) -and
        (Get-Content -LiteralPath (Join-Path $UpdatePrevious 'Agent\PulsarAgent.exe')) -ceq 'agent 1.0.0')
    Check 'update: what an earlier update left in previous is deleted first, and the folder is checked as an administrators-only one' (
        -not (Test-Path (Join-Path $UpdatePrevious 'Panel\old')) -and $script:upFolders -contains $UpdatePrevious)
    Save-PreviousPrograms @('agent')
    Check 'update: an agent-only update keeps no panel copy (an older one is not left to be put back)' (-not (Test-Path (Join-Path $UpdatePrevious 'Panel')) -and (Test-Path (Join-Path $UpdatePrevious 'Agent\PulsarAgent.exe')))
    Check 'previous: it is built beside the old copy and swapped in: nothing is left beside previous afterwards' (
        (@(Get-ChildItem -LiteralPath $upRoot -Force | ForEach-Object Name | Where-Object { $_ -like 'previous*' }) -join ',') -ceq 'previous')
    # The copy of the last good version is never wiped to make room for a broken one.
    $null = New-Item -ItemType Directory -Force (Join-Path $UpdatePrevious 'Panel'), (Join-Path $UpdatePrevious 'Agent')
    Set-Content -LiteralPath (Join-Path $UpdatePrevious 'Panel\Pulsar.Api.exe') 'panel 0.9.0 (the last good one)'
    Set-Content -LiteralPath (Join-Path $UpdatePrevious 'Agent\PulsarAgent.exe') 'agent 0.9.0 (the last good one)'
    Set-Content -LiteralPath (Join-Path $PanelDir 'Pulsar.Api.exe') 'panel 1.0.0 (broken)'
    Save-PreviousPrograms @('panel', 'agent') $false
    Check 'previous: an installed version that does not pass its checks does not replace the good copy of the one before (the update then rolls back to that)' (
        (Get-Content -LiteralPath (Join-Path $UpdatePrevious 'Panel\Pulsar.Api.exe')) -ceq 'panel 0.9.0 (the last good one)' -and (Get-Content -LiteralPath (Join-Path $UpdatePrevious 'Agent\PulsarAgent.exe')) -ceq 'agent 0.9.0 (the last good one)')
    Remove-Item -LiteralPath (Join-Path $UpdatePrevious 'Agent') -Recurse -Force
    Save-PreviousPrograms @('panel', 'agent') $false
    Check 'previous: ...but only when it has every part this update replaces; otherwise the installed programs are kept, as nothing better exists' (
        (Get-Content -LiteralPath (Join-Path $UpdatePrevious 'Panel\Pulsar.Api.exe')) -ceq 'panel 1.0.0 (broken)' -and (Test-Path -LiteralPath (Join-Path $UpdatePrevious 'Agent\PulsarAgent.exe')))
    Set-Content -LiteralPath (Join-Path $UpdatePrevious 'Panel\Pulsar.Api.exe') 'panel 0.9.0 (the last good one)'
    Remove-Item (Join-Path $PanelDir 'Pulsar.Api.exe')
    Check 'update: a program that cannot be copied stops the update before it starts' ((Get-SetupExitCode (Get-Caught { Save-PreviousPrograms @('panel') })) -eq 1 -and (Get-Thrown { Save-PreviousPrograms @('panel') }) -match 'nothing was changed')
    Check 'previous: ...and the copy that was there stays whole, with no half-built one beside it' (
        (Get-Content -LiteralPath (Join-Path $UpdatePrevious 'Panel\Pulsar.Api.exe')) -ceq 'panel 0.9.0 (the last good one)' -and
        (@(Get-ChildItem -LiteralPath $upRoot -Force | ForEach-Object Name | Where-Object { $_ -like 'previous*' }) -join ',') -ceq 'previous')

    # Remove-OldUpdateBackups: the newest three by the time in the name; other things are not backups and stay.
    $UpdateBackups = Join-Path $upRoot 'backups'
    $null = New-Item -ItemType Directory $UpdateBackups
    $stamps = '1.0.0-20261001-100000', '1.0.1-20261003-100000', 'unknown-20261002-100000', '1.0.2-20261005-100000', '1.0.3-20261004-235959'
    foreach ($stamp in $stamps) { $null = New-Item -ItemType Directory (Join-Path $UpdateBackups $stamp); Set-Content (Join-Path $UpdateBackups "$stamp\pulsar.dump") 'x' }
    $null = New-Item -ItemType Directory (Join-Path $UpdateBackups 'my-own-notes'), (Join-Path $UpdateBackups '1.0.0-2026')
    Set-Content (Join-Path $UpdateBackups '1.0.9-20260101-000000') 'a file, not a folder'
    $linkTarget = Join-Path $upRoot 'link-target'
    $null = New-Item -ItemType Directory $linkTarget
    Set-Content (Join-Path $linkTarget 'precious.txt') 'precious'
    $null = New-Item -ItemType Junction -Path (Join-Path $UpdateBackups '1.0.8-20250101-000000') -Target $linkTarget
    Remove-OldUpdateBackups $UpdateBackups 3
    $left = @(Get-ChildItem -LiteralPath $UpdateBackups -Force | ForEach-Object Name | Sort-Object)
    Check 'update: the 3 newest backups stay (newest by the time in the name, not by version), the older ones go' (
        (@($left | Where-Object { $_ -in $stamps }) -join ',') -ceq '1.0.1-20261003-100000,1.0.2-20261005-100000,1.0.3-20261004-235959' -and -not ($left -contains '1.0.0-20261001-100000') -and -not ($left -contains 'unknown-20261002-100000')) ($left -join ',')
    Check 'update: folders of other names, files and links are not backups: they stay, and a link''s target is not touched' (
        $left -contains 'my-own-notes' -and $left -contains '1.0.0-2026' -and $left -contains '1.0.9-20260101-000000' -and $left -contains '1.0.8-20250101-000000' -and (Test-Path (Join-Path $linkTarget 'precious.txt')))
    Remove-OldUpdateBackups $UpdateBackups 3
    Check 'update: keeping the newest 3 again changes nothing' (@(Get-ChildItem -LiteralPath $UpdateBackups -Force).Count -eq $left.Count)
    [IO.Directory]::Delete((Join-Path $UpdateBackups '1.0.8-20250101-000000'))

    # New-UpdateBackup: the release's own Backup-Pulsar.ps1, in a PowerShell of its own, into <version>-<time>.
    $script:backupCalls = New-Object Collections.Generic.List[string]
    $script:backupMode = 'ok'
    $realInvokeNative = (Get-Item function:Invoke-Native).ScriptBlock
    # $PSScriptRoot is empty in a function made from text: the one that reads it gets the release folder of the test instead.
    $ReleaseFolder = Join-Path $upRoot 'release'
    . ([scriptblock]::Create($updateParts['New-UpdateBackup'].Replace('$PSScriptRoot', '$ReleaseFolder')))
    $InstallRoot = Join-Path $upRoot 'Program Files'; $DataRoot = Join-Path $upRoot 'ProgramData'
    function Invoke-Native([string] $exe) {
        $script:backupCalls.Add("$exe $($args -join ' ')")
        $destination = $args[([Array]::IndexOf($args, '-Destination') + 1)]
        switch ($script:backupMode) {
            'ok' { $made = Join-Path $destination 'pulsar-backup-20261020-101010'; $null = New-Item -ItemType Directory -Force $made; Set-Content (Join-Path $made 'pulsar.dump') 'a dump'; return [pscustomobject]@{ ExitCode = 0; Output = 'Backup written' } }
            'empty' { $made = Join-Path $destination 'pulsar-backup-20261020-101010'; $null = New-Item -ItemType Directory -Force $made; [IO.File]::WriteAllBytes((Join-Path $made 'pulsar.dump'), [byte[]]@()); return [pscustomobject]@{ ExitCode = 0; Output = 'Backup written' } }
            default { $null = New-Item -ItemType Directory -Force (Join-Path $destination 'pulsar-backup-partial'); return [pscustomobject]@{ ExitCode = 1; Output = 'pg_dump.exe was not found: pass the folder of PostgreSQL''s programs with -PostgresBin.' } }
        }
    }
    $backup = New-UpdateBackup '1.0.0'
    $backupLine = $script:backupCalls[0]
    Check 'update: the backup is Backup-Pulsar.ps1 run in Windows PowerShell with the install and data folders, into <version>-<time> under backups' (
        $backupLine -match 'WindowsPowerShell\\v1\.0\\powershell\.exe -NoProfile -ExecutionPolicy Bypass -File \S*Backup-Pulsar\.ps1 -Destination .*backups\\1\.0\.0-\d{8}-\d{6} -InstallRoot .* -DataRoot ' -and
        $backup.Folder -match 'pulsar-backup-20261020-101010\z' -and (Test-Path -LiteralPath $backup.Dump -PathType Leaf)) $backupLine
    Check 'update: ...from the release''s own script ($PSScriptRoot), not the copy in the panel''s folder that the update replaces' ($updateParts['New-UpdateBackup'] -match "Join-Path \`$PSScriptRoot 'Backup-Pulsar\.ps1'" -and $updateParts['New-UpdateBackup'] -notmatch '\$PanelDir')
    Check 'update: its folder is checked as an administrators-only one first' ($script:upFolders -contains $UpdateBackups)
    $script:backupMode = 'fail'
    $caught = Get-Caught { New-UpdateBackup '1.0.0' }
    Check 'update: a backup that fails stops the update before anything changes, saying why (and that a remote database needs pg_dump here); its partial folder is deleted' (
        (Get-SetupExitCode $caught) -eq 1 -and $caught.Exception.Message -match 'nothing was changed' -and $caught.Exception.Message -match 'pg_dump\.exe was not found' -and $caught.Exception.Message -match 'another machine' -and
        @(Get-ChildItem -LiteralPath $UpdateBackups -Directory | Where-Object { $_.Name -like '1.0.0-*' -and (Test-Path (Join-Path $_.FullName 'pulsar-backup-partial')) }).Count -eq 0) "$caught"
    $script:backupMode = 'empty'
    Check 'update: an empty dump is no backup' ((Get-Thrown { New-UpdateBackup $null }) -match 'nothing was changed')
    Check 'update: without a known version the backup folder is called unknown-<time>' ($script:backupCalls[$script:backupCalls.Count - 1] -match 'backups\\unknown-\d{8}-\d{6} ')
    Set-Item function:Invoke-Native $realInvokeNative

    # Write-UpdateResult: last-update.json.
    $UpdateRoot = Join-Path $upRoot 'Updates'
    $null = New-Item -ItemType Directory $UpdateRoot
    $UpdateResultFile = Join-Path $UpdateRoot 'last-update.json'
    $SetupLogFile = Join-Path $upRoot 'install.log'
    function Write-SetupLog([string] $text) { }
    function New-Run($from = '1.0.0', $to = '1.0.1', $parts = @('agent', 'panel')) { [pscustomobject]@{ From = $from; To = $to; Parts = $parts; Touched = @(); Written = $false; MigrationsBefore = $null; Backup = $null } }
    $script:UpdateRun = New-Run
    $SetupSecrets.Clear(); Register-Secret 'hunter2-secret'
    Write-UpdateResult 'succeeded'
    $bytes = [IO.File]::ReadAllBytes($UpdateResultFile)
    $result = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
    $fields = @($result.PSObject.Properties.Name) -join ','
    Check 'result: last-update.json is UTF-8 without a byte order mark and starts with an object' ($bytes[0] -eq 123)
    Check 'result: its fields are from, to, parts, status, error, log and finishedAt' ($fields -ceq 'from,to,parts,status,error,log,finishedAt') $fields
    Check 'result: a good update says succeeded, from 1.0.0 to 1.0.1, agent and panel, no error, and the log of the run' (
        $result.from -ceq '1.0.0' -and $result.to -ceq '1.0.1' -and (@($result.parts) -join ',') -ceq 'agent,panel' -and $result.status -ceq 'succeeded' -and $null -eq $result.error -and $result.log -ceq $SetupLogFile)
    Check 'result: finishedAt is an ISO-8601 UTC time' ($result.finishedAt -cmatch '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z\z') $result.finishedAt
    Check 'result: finishedAt is the current UTC time' (([DateTime]::Parse($result.finishedAt, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AdjustToUniversal) - [DateTime]::UtcNow).Duration().TotalMinutes -lt 2)
    Write-UpdateResult 'failed' 'a second result of the same run'
    Check 'result: written once per run (the first stands)' (([IO.File]::ReadAllText($UpdateResultFile) | ConvertFrom-Json).status -ceq 'succeeded')
    $script:UpdateRun = New-Run '1.0.0' $null @('panel')
    Write-UpdateResult 'failed' ("It failed with hunter2-secret in it: " + ('x' * 3000) + "$([char]27)[31m")
    $result = [IO.File]::ReadAllText($UpdateResultFile) | ConvertFrom-Json
    Check 'result: a failure''s error is at most 2000 characters, with no secret and no control character; an unknown version is null; one panel part' (
        $result.status -ceq 'failed' -and $result.error.Length -le 2000 -and $result.error -notmatch 'hunter2' -and $result.error -match '\[secret\]' -and $result.error -notmatch '[\x00-\x1F]' -and $null -eq $result.to -and (@($result.parts) -join ',') -ceq 'panel')
    Check 'result: only the result file is left in the folder (no temporary file)' ((@(Get-ChildItem -LiteralPath $UpdateRoot -Force | ForEach-Object Name) -join ',') -ceq 'last-update.json')
    $script:UpdateRun = New-Run
    Set-Content -LiteralPath $UpdateResultFile -Value 'an earlier result'
    Write-UpdateResult 'rolled_back' 'The update to 1.0.1 failed and Pulsar was put back to 1.0.0: test'
    Check 'result: it replaces the earlier run''s file' ((([IO.File]::ReadAllText($UpdateResultFile) | ConvertFrom-Json).status) -ceq 'rolled_back')
    $script:UpdateRun = $null
    Remove-Item $UpdateResultFile
    Write-UpdateResult 'failed' 'before the run started'
    Check 'result: a failure before the run began (another update holds the lock, the folder is not as it must be) writes none: the earlier run''s file is not overwritten' (-not (Test-Path $UpdateResultFile))
    $script:UpdateRun = New-Run
    function Initialize-UpdateFolder([string] $path) { throw (New-SetupError $ExitUnsupported "$path could be changed by others.") }
    Write-UpdateResult 'failed' 'folder not trusted'
    Check 'result: a folder that is no longer administrators-only gets no file, and the run''s own outcome stands' (-not (Test-Path $UpdateResultFile) -and -not $script:UpdateRun.Written -and @(Get-ChildItem -LiteralPath $UpdateRoot -Force).Count -eq 0)
    function Initialize-UpdateFolder([string] $path) { $script:upFolders.Add($path) }

    # The lock: no two updates at once.
    $UpdateLockFile = Join-Path $UpdateRoot 'update.lock'
    $script:UpdateLock = $null
    Lock-Update
    $held = $script:UpdateLock
    $second = Get-Caught { $first = $script:UpdateLock; $script:UpdateLock = $null; try { Lock-Update } finally { $script:UpdateLock = $first } }
    Check 'lock: a second update while one runs is refused, changing nothing' ($null -ne $second -and (Get-SetupExitCode $second) -eq 3 -and $second.Exception.Message -match 'Another update of Pulsar is running' -and $second.Exception.Message -match 'Nothing was changed')
    Unlock-Update
    Lock-Update
    Check 'lock: once the first one ended, the next can start' ($null -ne $script:UpdateLock)
    Unlock-Update
    Check 'lock: Unlock-Update is safe with nothing held' ($null -eq (Get-Thrown { Unlock-Update }) -and $null -eq $script:UpdateLock)
    $SetupSecrets.Clear()

    # --- Whole-update scenarios ---
    # A world: the program folders, the update folders, what the mocks do. The services, copies, database calls and health checks
    # are mocks that record what was asked of them.
    function Invoke-UpdateScenario([hashtable] $options = @{}) {
        $PanelDir, $AgentDir = Get-FakePrograms
        if ($options['SaveFails']) { Remove-Item -LiteralPath (Join-Path $PanelDir 'Pulsar.Api.exe') }
        $UpdateRoot = Join-Path $upRoot 'Updates'
        foreach ($folder in $UpdateRoot) { if (Test-Path -LiteralPath $folder) { Remove-Item -LiteralPath $folder -Recurse -Force }; $null = New-Item -ItemType Directory $folder }
        $UpdateBackups = Join-Path $UpdateRoot 'backups'; $UpdatePrevious = Join-Path $UpdateRoot 'previous'
        $UpdateResultFile = Join-Path $UpdateRoot 'last-update.json'; $UpdateLockFile = Join-Path $UpdateRoot 'update.lock'; $UpdateJournal = Join-Path $UpdateRoot 'in-progress.json'
        $null = New-Item -ItemType Directory $UpdateBackups, $UpdatePrevious
        $script:UpdateLock = $null; $script:UpdateRun = $null
        $script:upCalls = New-Object Collections.Generic.List[string]
        $script:upHealth = @{ panel = 0; agent = 0 }
        $script:upMigrations = if ($options['Interrupted']) { @($options['Current'], $options['Before'], $options['After']) } else { @($options['Before'], $options['After']) }
        $script:upMigrationReads = 0
        $script:upJournalAtReplace = $null
        $script:upSavedHealthy = $null
        $PanelService = 'PulsarPanel'; $AgentService = 'PulsarAgent'
        $parts = if ($options.ContainsKey('Parts')) { $options['Parts'] } else { @('agent', 'panel') }
        function Get-UpdateVersions { [pscustomobject]@{ From = '1.0.0'; To = '1.0.1' } }
        function Write-Step { }
        function New-UpdateBackup([string] $from) {
            $script:upCalls.Add("backup $from")
            if ($options['BackupFails']) { throw (New-SetupError $ExitFailed 'The backup of the panel before the update failed (test), so the update was not started and nothing was changed.') }
            [pscustomobject]@{ Folder = (Join-Path $UpdateBackups '1.0.0-20261020-101010\pulsar-backup-20261020-101010'); Dump = (Join-Path $UpdateBackups '1.0.0-20261020-101010\pulsar-backup-20261020-101010\pulsar.dump') }
        }
        function Remove-OldUpdateBackups([string] $folder, [int] $keep) { $script:upCalls.Add("prune $keep") }
        function Get-PanelMigrations { $value = $script:upMigrations[[Math]::Min($script:upMigrationReads, $script:upMigrations.Count - 1)]; $script:upMigrationReads++; $script:upCalls.Add('read migrations'); $value }
        function Test-InstalledHealthy([string[]] $parts) { -not $options['Unhealthy'] }
        function Assert-RollbackPossible { if ($options['NotOwner']) { throw (New-SetupError $ExitUnsupported 'The panel''s database user does not own the schema public (test). Nothing was changed.') } }
        function Update-Agent { $script:upJournalAtReplace = Test-Path -LiteralPath $UpdateJournal; $script:upCalls.Add('update agent'); if ($options['Fail'] -ceq 'agent') { throw 'The agent''s service could not start (test).' } }
        function Update-Panel { if ($null -eq $script:upJournalAtReplace) { $script:upJournalAtReplace = Test-Path -LiteralPath $UpdateJournal }; $script:upCalls.Add('update panel'); if ($options['Fail'] -ceq 'panel') { throw 'The panel service did not start (test).' } }
        function Wait-UpdatedPanelReady {
            $script:upHealth['panel']++; $script:upCalls.Add("health panel $($script:upHealth['panel'])")
            if ($script:upHealth['panel'] -eq 1 -and $options['Fail'] -ceq 'panelHealth') { throw 'The panel did not become ready in 5 minutes (test).' }
            if ($script:upHealth['panel'] -ge 2 -and $options['StillBroken']) { throw 'The panel did not become ready in 5 minutes (still, test).' }
        }
        function Wait-UpdatedAgentHealthy {
            $script:upHealth['agent']++; $script:upCalls.Add("health agent $($script:upHealth['agent'])")
            if ($script:upHealth['agent'] -eq 1 -and $options['Fail'] -ceq 'agentHealth') { throw 'The agent could not sign in at the panel within 2 minutes (test).' }
        }
        $script:upStops = @{}
        function Stop-UpdateService([string] $name) {
            $script:upCalls.Add("stop $name")
            $script:upStops[$name] = 1 + $(if ($script:upStops.ContainsKey($name)) { $script:upStops[$name] } else { 0 })
            if ($options['StopFails'] -ceq $name -or ($options['StopFailsOnRollback'] -ceq $name -and $script:upStops[$name] -ge 2)) { throw 'Access denied (test).' }
        }
        function Copy-Program([string] $source, [string] $target) {
            $script:upCalls.Add("copy $(Split-Path -Leaf $source) to $(Split-Path -Leaf $target)")
            if ($options['CopyFails'] -ceq (Split-Path -Leaf $target)) { throw 'The file is in use (test).' }
        }
        function Restore-RolledBackDatabase([string] $dump) { $script:upCalls.Add("restore $(Split-Path -Leaf $dump)"); if ($options['RestoreFails']) { throw 'pg_restore failed (test)' } }
        function Start-Panel { $script:upCalls.Add('start panel') }
        function Start-Service { $script:upCalls.Add("start $($args[0])") }
        # An update that was stopped half way: its journal, the backup it made and (HalfSwap) a panel folder moved aside and never replaced.
        if ($options['Interrupted']) {
            $dumpFolder = Join-Path $UpdateBackups '1.0.0-20261020-101010\pulsar-backup-20261020-101010'
            $null = New-Item -ItemType Directory $dumpFolder
            Set-Content -LiteralPath (Join-Path $dumpFolder 'pulsar.dump') 'a dump'
            $null = New-Item -ItemType Directory -Force (Join-Path $UpdatePrevious 'Panel'), (Join-Path $UpdatePrevious 'Agent')
            Set-Content -LiteralPath (Join-Path $UpdatePrevious 'Panel\Pulsar.Api.exe') 'panel 1.0.0'
            Set-Content -LiteralPath (Join-Path $UpdatePrevious 'Agent\PulsarAgent.exe') 'agent 1.0.0'
            Write-UpdateJournal ([pscustomobject]@{ From = '1.0.0'; To = '1.0.1'; Parts = @('agent', 'panel'); MigrationsBefore = $options['Before']; Backup = [pscustomobject]@{ Folder = $dumpFolder; Dump = (Join-Path $dumpFolder 'pulsar.dump') } })
            if ($options['HalfSwap']) { Rename-Item -LiteralPath $PanelDir -NewName 'Panel.old' }
        }
        $failure = $null
        try {
            Start-UpdateRun
            $script:UpdateRun.Parts = @()
            Invoke-Update $parts
        }
        catch { $failure = $_ }
        $record = $null
        if (Test-Path -LiteralPath $UpdateResultFile) { $record = [IO.File]::ReadAllText($UpdateResultFile) | ConvertFrom-Json }
        Unlock-Update
        return [pscustomobject]@{ Error = $failure; Calls = @($script:upCalls); Result = $record; Previous = $UpdatePrevious; Panel = $PanelDir; Agent = $AgentDir; JournalLeft = (Test-Path -LiteralPath $UpdateJournal); JournalAtReplace = $script:upJournalAtReplace }
    }
    $calls = { param($s) $s.Calls -join ' | ' }

    $ok = Invoke-UpdateScenario @{ Before = "m1`nm2"; After = "m1`nm2`nm3" }
    Check 'update: a good update backs the panel up, notes the migrations, keeps the programs, updates the agent then the panel, checks the panel then the agent' (
        $null -eq $ok.Error -and (& $calls $ok) -ceq 'stop PulsarPanel | backup 1.0.0 | prune 3 | read migrations | update agent | update panel | health panel 1 | health agent 1') (& $calls $ok)
    Check 'update: ...and the previous programs are in previous\Panel and \Agent' ((Test-Path (Join-Path $ok.Previous 'Panel\Pulsar.Api.exe')) -and (Test-Path (Join-Path $ok.Previous 'Agent\PulsarAgent.exe')))
    Check 'update: ...and last-update.json says succeeded, from 1.0.0 to 1.0.1, agent and panel, no error' (
        $ok.Result.status -ceq 'succeeded' -and $ok.Result.from -ceq '1.0.0' -and $ok.Result.to -ceq '1.0.1' -and (@($ok.Result.parts) -join ',') -ceq 'agent,panel' -and $null -eq $ok.Result.error)
    $agentOnly = Invoke-UpdateScenario @{ Parts = @('agent') }
    Check 'update: a node with no panel is backed up for nothing (no backup), updated and checked, and the result lists the agent only' (
        $null -eq $agentOnly.Error -and (& $calls $agentOnly) -ceq 'update agent | health agent 1' -and (@($agentOnly.Result.parts) -join ',') -ceq 'agent' -and -not (Test-Path (Join-Path $agentOnly.Previous 'Panel')))
    $panelOnly = Invoke-UpdateScenario @{ Parts = @('panel'); Before = 'm1'; After = 'm1' }
    Check 'update: a panel alone: backup, notes, panel update, panel check, no agent check' ((& $calls $panelOnly) -ceq 'stop PulsarPanel | backup 1.0.0 | prune 3 | read migrations | update panel | health panel 1')

    $backupFailed = Invoke-UpdateScenario @{ BackupFails = $true }
    Check 'update: a backup that fails stops the update before anything is replaced: no copy, no rollback, and the panel that was stopped for the dump runs again' (
        (Get-SetupExitCode $backupFailed.Error) -eq 1 -and $backupFailed.Error.Exception.Message -match 'nothing was changed' -and (& $calls $backupFailed) -ceq 'stop PulsarPanel | backup 1.0.0 | start panel' -and -not $backupFailed.JournalLeft)
    $saveFailed = Invoke-UpdateScenario @{ SaveFails = $true }
    Check 'update: programs that cannot be kept for a rollback stop the update before anything is replaced, with the panel running again and no journal' (
        (Get-SetupExitCode $saveFailed.Error) -eq 1 -and (& $calls $saveFailed) -ceq 'stop PulsarPanel | backup 1.0.0 | prune 3 | read migrations | start panel' -and -not $saveFailed.JournalLeft)
    Check 'update: the panel is stopped, and the backup waits for it, before the dump is taken (writes made between the dump and the stop would be lost by a rollback)' (
        $updateParts['Invoke-Update'].IndexOf('Stop-UpdateService $PanelService') -ge 0 -and $updateParts['Invoke-Update'].IndexOf('Stop-UpdateService $PanelService') -lt $updateParts['Invoke-Update'].IndexOf('New-UpdateBackup'))

    $bad = Invoke-UpdateScenario @{ Fail = 'panelHealth'; Before = "m1`nm2"; After = "m1`nm2`nm3" }
    Check 'rollback: a panel that does not become ready puts the previous version back: services stopped, database restored from the backup, previous programs copied back, services started, previous version checked' (
        (& $calls $bad) -ceq 'stop PulsarPanel | backup 1.0.0 | prune 3 | read migrations | update agent | update panel | health panel 1 | stop PulsarPanel | read migrations | restore pulsar.dump | copy Panel to Panel | start panel | stop PulsarAgent | copy Agent to Agent | start PulsarAgent | health panel 2 | health agent 1') (& $calls $bad)
    Check 'rollback: it ends with the exit code for "rolled back" and a message naming both versions and why' (
        (Get-SetupExitCode $bad.Error) -eq 7 -and $bad.Error.Exception.Message -ceq 'The update to 1.0.1 failed and Pulsar was put back to 1.0.0: The panel did not become ready in 5 minutes (test).') $bad.Error.Exception.Message
    Check 'rollback: last-update.json says rolled_back with that message' ($bad.Result.status -ceq 'rolled_back' -and $bad.Result.error -ceq $bad.Error.Exception.Message -and $bad.Result.to -ceq '1.0.1' -and (@($bad.Result.parts) -join ',') -ceq 'agent,panel')
    $same = Invoke-UpdateScenario @{ Fail = 'panelHealth'; Before = "m1`nm2"; After = "m1`nm2" }
    Check 'rollback: with the same migrations as before the update the database is left as it is (what the new version wrote stays)' (
        @($same.Calls | Where-Object { $_ -like 'restore*' }).Count -eq 0 -and (Get-SetupExitCode $same.Error) -eq 7 -and $same.Calls -contains 'copy Panel to Panel' -and $same.Calls -contains 'start panel')
    $unknown = Invoke-UpdateScenario @{ Fail = 'panelHealth'; Before = $null; After = "m1" }
    Check 'rollback: migrations that could not be read count as changed: the database is restored from the backup' (@($unknown.Calls | Where-Object { $_ -like 'restore*' }).Count -eq 1 -and (Get-SetupExitCode $unknown.Error) -eq 7)
    $agentBroke = Invoke-UpdateScenario @{ Fail = 'agentHealth'; Before = 'm1'; After = 'm1' }
    Check 'rollback: an agent that does not sign in rolls back the panel too (both were replaced), in the order panel then agent' (
        (Get-SetupExitCode $agentBroke.Error) -eq 7 -and ($agentBroke.Calls -join ' | ') -match 'stop PulsarPanel.*stop PulsarAgent' -and $agentBroke.Error.Exception.Message -match 'could not sign in')
    $agentFailed = Invoke-UpdateScenario @{ Fail = 'agent'; Before = 'm1'; After = 'm1' }
    Check 'rollback: an agent that fails to update rolls back the agent only: the panel was not touched, so no panel is stopped, restored or copied' (
        (Get-SetupExitCode $agentFailed.Error) -eq 7 -and ($agentFailed.Calls -join ' | ') -ceq 'stop PulsarPanel | backup 1.0.0 | prune 3 | read migrations | update agent | start panel | stop PulsarAgent | copy Agent to Agent | start PulsarAgent | health agent 1' -and
        $agentFailed.Result.status -ceq 'rolled_back') ($agentFailed.Calls -join ' | ')
    $panelFailed = Invoke-UpdateScenario @{ Fail = 'panel'; Before = 'm1'; After = 'm1' }
    Check 'rollback: a panel that fails to start while it is updated is put back with the agent, whose update was fine' ((Get-SetupExitCode $panelFailed.Error) -eq 7 -and $panelFailed.Calls -contains 'copy Agent to Agent' -and $panelFailed.Calls -contains 'copy Panel to Panel')

    # A rollback that does not work says exactly what state things are in.
    $broken = Invoke-UpdateScenario @{ Fail = 'panelHealth'; Before = "m1"; After = "m1`nm2"; CopyFails = 'Panel' }
    $text = $broken.Error.Exception.Message
    Check 'rollback failure: a program that cannot be put back is said so, with what was done, where the backup and the previous programs are' (
        (Get-SetupExitCode $broken.Error) -eq 1 -and $text -match 'The update to 1\.0\.1 failed \(The panel did not become ready' -and $text -match 'putting Pulsar back to 1\.0\.0 failed too' -and
        $text -match 'the previous panel program could not be put back \(The file is in use \(test\)\.\), so the panel stays stopped' -and $text -match 'Done: .*the panel''s service was stopped; the database was restored from the backup' -and
        $text -match 'backup from before the update is in .*pulsar-backup-20261020-101010' -and $text -match 'The programs before the update are in .*previous') $text
    Check 'rollback failure: the panel is not started, the agent (which was fine to put back) is, and the result is failed with the same message' (
        $broken.Calls -notcontains 'start panel' -and $broken.Calls -contains 'start PulsarAgent' -and $broken.Result.status -ceq 'failed' -and $broken.Result.error -ceq $text -and $broken.Calls -notcontains 'health panel 2')
    $dbBroken = Invoke-UpdateScenario @{ Fail = 'panelHealth'; Before = "m1"; After = "m1`nm2"; RestoreFails = $true }
    Check 'rollback failure: a database that cannot be restored keeps the new panel program and leaves the panel stopped (an older panel on newer tables is worse)' (
        (Get-SetupExitCode $dbBroken.Error) -eq 1 -and $dbBroken.Error.Exception.Message -match 'database could not be restored from the backup \(pg_restore failed \(test\)\), so the panel keeps the new program and stays stopped' -and
        $dbBroken.Calls -notcontains 'copy Panel to Panel' -and $dbBroken.Calls -notcontains 'start panel' -and $dbBroken.Result.status -ceq 'failed')
    $stuck = Invoke-UpdateScenario @{ Fail = 'panelHealth'; Before = 'm1'; After = 'm1'; StopFailsOnRollback = 'PulsarPanel' }
    Check 'rollback failure: a service that will not stop is left alone, and nothing is copied over its running program' ($stuck.Error.Exception.Message -match 'service could not be stopped' -and $stuck.Calls -notcontains 'copy Panel to Panel')
    $noStop = Invoke-UpdateScenario @{ StopFails = 'PulsarPanel' }
    Check 'update: a panel that will not stop for its backup stops the update before anything is replaced (no dump, no journal)' (
        $null -ne $noStop.Error -and $noStop.Error.Exception.Message -match 'Access denied' -and (& $calls $noStop) -ceq 'stop PulsarPanel | start panel' -and -not $noStop.JournalLeft)
    $still = Invoke-UpdateScenario @{ Fail = 'panelHealth'; Before = 'm1'; After = 'm1'; StillBroken = $true }
    Check 'rollback failure: a previous version that does not work either is said so' ((Get-SetupExitCode $still.Error) -eq 1 -and $still.Error.Exception.Message -match 'the previous version does not work either \(The panel did not become ready' -and $still.Result.status -ceq 'failed')

    # --- The journal: an update that is stopped half way (a power cut, a killed process) is rolled back by the next run ---
    Check 'journal: it is written before the first program is replaced, and is gone when the update succeeded' (
        $ok.JournalAtReplace -eq $true -and -not $ok.JournalLeft -and $agentOnly.JournalAtReplace -eq $true -and -not $agentOnly.JournalLeft)
    Check 'journal: a rollback that worked removes it; one that did not keeps it, for the next run to try again' (
        -not $bad.JournalLeft -and -not $agentFailed.JournalLeft -and $broken.JournalLeft -and $dbBroken.JournalLeft -and $still.JournalLeft)
    $unhealthy = Invoke-UpdateScenario @{ Unhealthy = $true }
    Check 'previous: whether the installed Pulsar works is looked at before the panel is stopped, and decides whether previous is replaced' (
        $updateParts['Invoke-Update'].IndexOf('Test-InstalledHealthy') -ge 0 -and $updateParts['Invoke-Update'].IndexOf('Test-InstalledHealthy') -lt $updateParts['Invoke-Update'].IndexOf('Stop-UpdateService $PanelService') -and
        $updateParts['Invoke-Update'] -match 'Save-PreviousPrograms \$parts \$installedHealthy' -and $null -eq $unhealthy.Error)

    $resumed = Invoke-UpdateScenario @{ Interrupted = $true; Current = "m1`nm2"; Before = 'm1'; After = "m1`nm2" }
    Check 'resume: a journal found at the start of an update is rolled back first: the database restored (its migrations differ from the journal''s), the previous programs put back, the services started and checked; then this update goes on' (
        $null -eq $resumed.Error -and (& $calls $resumed) -cmatch '^stop PulsarPanel \| read migrations \| restore pulsar\.dump \| copy Panel to Panel \| start panel \| stop PulsarAgent \| copy Agent to Agent \| start PulsarAgent \| health panel 1 \| health agent 1 \| stop PulsarPanel \| backup 1\.0\.0 .* update agent \| update panel') (& $calls $resumed)
    Check 'resume: ...and the journal of the interrupted update is gone, this update wrote its own and removed it' (-not $resumed.JournalLeft -and $resumed.Result.status -ceq 'succeeded')
    $resumedSame = Invoke-UpdateScenario @{ Interrupted = $true; Current = 'm1'; Before = 'm1'; After = 'm1' }
    Check 'resume: with the same migrations as the journal''s the database is left as it is' (@($resumedSame.Calls | Where-Object { $_ -like 'restore*' }).Count -eq 0 -and $null -eq $resumedSame.Error -and $resumedSame.Calls -contains 'copy Panel to Panel')
    $halfSwap = Invoke-UpdateScenario @{ Interrupted = $true; HalfSwap = $true; Current = 'm1'; Before = 'm1'; After = 'm1' }
    Check 'resume: a program folder moved aside and never replaced is put back before anything else' ((Test-Path -LiteralPath $halfSwap.Panel -PathType Container) -and -not (Test-Path -LiteralPath "$($halfSwap.Panel).old") -and $null -eq $halfSwap.Error)
    $stuckResume = Invoke-UpdateScenario @{ Interrupted = $true; RestoreFails = $true; Current = "m1`nm2"; Before = 'm1'; After = 'm1' }
    Check 'resume: a rollback that fails stops this update before it changes anything, says what is left, and keeps the journal' (
        (Get-SetupExitCode $stuckResume.Error) -eq 1 -and $stuckResume.Error.Exception.Message -match 'stopped before it ended' -and $stuckResume.Error.Exception.Message -match 'database could not be restored' -and
        $stuckResume.JournalLeft -and $stuckResume.Calls -notcontains 'update agent' -and ($stuckResume.Calls -join ' | ') -notmatch 'backup')

    # A rollback sets the failed version's tables aside, which needs the panel's database user to own the schema public.
    $notOwner = Invoke-UpdateScenario @{ NotOwner = $true }
    Check 'rollback: a database whose schema public the panel''s user does not own is found before anything changes: exit code 3, no service stopped, no backup' (
        (Get-SetupExitCode $notOwner.Error) -eq 3 -and $notOwner.Error.Exception.Message -match 'Nothing was changed' -and $notOwner.Calls.Count -eq 0 -and -not $notOwner.JournalLeft)
    Check 'rollback: ...and it is asked before the panel is stopped' (
        $updateParts['Invoke-Update'].IndexOf('Assert-RollbackPossible') -ge 0 -and $updateParts['Invoke-Update'].IndexOf('Assert-RollbackPossible') -lt $updateParts['Invoke-Update'].IndexOf('Stop-UpdateService $PanelService'))
    $agentNotOwner = Invoke-UpdateScenario @{ NotOwner = $true; Parts = @('agent') }
    Check 'rollback: an agent-only update has no database and is not asked' ($null -eq $agentNotOwner.Error)
    $script:psqlAnswer = [pscustomobject]@{ ExitCode = 0; Output = '1' }
    $script:psqlSql = $null
    $ownerCheck = {
        function Get-PanelConnection { @{ host = 'localhost'; port = '5432'; username = 'pulsar'; database = 'pulsar'; password = 'x' } }
        function Invoke-PanelPsql($db, [string[]] $arguments) { $script:psqlSql = $arguments[1]; $script:psqlAnswer }
        Get-Caught { Assert-RollbackPossible }
    }
    $caught = & $ownerCheck
    Check 'rollback: the owner of the schema public passes, and the question asks for membership of the owning role and CREATE on the database' (
        $null -eq $caught -and $script:psqlSql -match "pg_has_role\(current_user, nspowner, 'MEMBER'\)" -and $script:psqlSql -match "has_database_privilege\(current_database\(\), 'CREATE'\)")
    $script:psqlAnswer = [pscustomobject]@{ ExitCode = 0; Output = '0' }
    $caught = & $ownerCheck
    Check 'rollback: a user that does not own it is refused, saying how to give it the schema' ((Get-SetupExitCode $caught) -eq 3 -and $caught.Exception.Message -match 'ALTER SCHEMA public OWNER TO pulsar' -and $caught.Exception.Message -match 'Nothing was changed')
    foreach ($answer in [pscustomobject]@{ ExitCode = 2; Output = 'psql: error' }, [pscustomobject]@{ ExitCode = 0; Output = '' }, [pscustomobject]@{ ExitCode = 0; Output = 'maybe' }) {
        $script:psqlAnswer = $answer
        Check "rollback: when the database cannot be asked ($($answer.Output)) the update is not refused, only warned about" ($null -eq (& $ownerCheck))
    }

    # The journal's file.
    $UpdateJournal = Join-Path $UpdateRoot 'in-progress.json'
    $UpdateBackups = Join-Path $UpdateRoot 'backups'
    $journalDump = Join-Path $UpdateBackups '1.0.0-20261020-101010\pulsar-backup-20261020-101010\pulsar.dump'
    if (Test-Path -LiteralPath $UpdateJournal) { Remove-Item -LiteralPath $UpdateJournal }
    Check 'journal: none when no update was interrupted' ($null -eq (Read-UpdateJournal))
    Write-UpdateJournal ([pscustomobject]@{ From = '1.0.0'; To = '1.0.1'; Parts = @('agent', 'panel'); MigrationsBefore = "m1`nm2"; Backup = [pscustomobject]@{ Folder = (Split-Path -Parent $journalDump); Dump = $journalDump } })
    $journalBytes = [IO.File]::ReadAllBytes($UpdateJournal)
    $read = Read-UpdateJournal
    Check 'journal: it is UTF-8 JSON without a byte order mark and holds the parts, the migrations before, the backup and both versions' (
        $journalBytes[0] -eq 123 -and (@($read.Parts) -join ',') -ceq 'agent,panel' -and $read.MigrationsBefore -ceq "m1`nm2" -and $read.Dump -ceq $journalDump -and $read.Folder -ceq (Split-Path -Parent $journalDump) -and $read.From -ceq '1.0.0' -and $read.To -ceq '1.0.1')
    Check 'journal: only the journal is left in the folder (no temporary file)' ((@(Get-ChildItem -LiteralPath $UpdateRoot -Force | ForEach-Object Name | Where-Object { $_ -like '*journal*' -or $_ -like 'in-progress*' -or $_ -like '.in-progress*' }) -join ',') -ceq 'in-progress.json')
    Write-UpdateJournal ([pscustomobject]@{ From = $null; To = '1.0.1'; Parts = @('agent'); MigrationsBefore = $null; Backup = $null })
    $read = Read-UpdateJournal
    Check 'journal: an update with no panel has no backup and no migrations; versions may be unknown' (@($read.Parts).Count -eq 1 -and $null -eq $read.Dump -and $null -eq $read.MigrationsBefore -and $null -eq $read.From)
    foreach ($case in @(
            @('text that is not JSON', 'this is not json', 'cannot be read'),
            @('a format it does not know', '{"format":2,"parts":["panel"]}', 'format'),
            @('a part that is neither the panel nor the agent', '{"format":1,"parts":["database"]}', 'part'),
            @('no part at all', '{"format":1,"parts":[]}', 'part'),
            @('a backup outside the update backups folder', ('{"format":1,"parts":["panel"],"backup":{"folder":"C:\\Windows","dump":"C:\\Windows\\evil.dump"}}'), 'backup'))) {
        [IO.File]::WriteAllText($UpdateJournal, $case[1])
        $caught = Get-Thrown { Read-UpdateJournal }
        Check "journal: $($case[0]) is refused, not guessed at" ($null -ne $caught -and $caught -match $case[2]) "$caught"
    }
    Remove-UpdateJournal
    Check 'journal: it can be removed, also when it is not there' (-not (Test-Path -LiteralPath $UpdateJournal) -and $null -eq (Get-Thrown { Remove-UpdateJournal }))
    Check 'journal: Uninstall removes it (an update that was stopped must not be rolled back into a new install)' ($installText -match '(?s)if \(\$Uninstall\) \{.*Remove-UpdateJournal.*Kept: the database')

    Set-Item function:New-SetupFolder $realNewSetupFolder

    # --- Health checks of the agent ---
    . $restoreUpdateFunctions
    function Write-Step { }
    $AgentDir = Join-Path $upRoot 'Program Files\Agent'; $AgentData = Join-Path $upRoot 'AgentData'; $AgentService = 'PulsarAgent'
    $UpdateAgentStableSeconds = 20; $UpdateAgentCheckMinutes = 2
    function Test-AgentCheck([hashtable] $options) {
        $script:agentStatus = New-Object Collections.Generic.Queue[string]
        foreach ($status in $options['Statuses']) { $script:agentStatus.Enqueue($status) }
        $script:agentChecks = New-Object Collections.Generic.Queue[object]
        foreach ($result in $options['Checks']) { $script:agentChecks.Enqueue($result) }
        $script:sleeps = New-Object Collections.Generic.List[int]
        $script:checkArguments = @()
        & {
            if ($options.ContainsKey('Minutes')) { $UpdateAgentCheckMinutes = $options['Minutes'] }
            function Get-Service { [pscustomobject]@{ Status = $(if ($script:agentStatus.Count -gt 1) { $script:agentStatus.Dequeue() } else { $script:agentStatus.Peek() }) } }
            function Start-Sleep { $script:sleeps.Add([int]$args[1]) }
            function Invoke-Native([string] $exe) { $script:checkArguments = @($exe) + $args; if ($script:agentChecks.Count -gt 1) { $script:agentChecks.Dequeue() } else { $script:agentChecks.Peek() } }
            Get-Caught { Wait-UpdatedAgentHealthy }
        }
    }
    $good = [pscustomobject]@{ ExitCode = 0; Output = 'Check: OK' }
    $badCheck = [pscustomobject]@{ ExitCode = 1; Output = "Pulsar Agent 1.0.1`n  Check:      FAILED, the panel refused this node" }
    $caught = Test-AgentCheck @{ Statuses = @('Running'); Checks = @($good) }
    Check 'agent check: a service that runs 20 seconds and signs in passes (PulsarAgent.exe status --check)' (
        $null -eq $caught -and ($script:sleeps -join ',') -ceq '20' -and $script:checkArguments[0] -like '*PulsarAgent.exe' -and ($script:checkArguments[1..2] -join ' ') -ceq 'status --check')
    $caught = Test-AgentCheck @{ Statuses = @('Stopped'); Checks = @($good) }
    Check 'agent check: a service that is not running at once fails, without waiting' ($null -ne $caught -and $caught.Exception.Message -match 'service is Stopped, not Running' -and $script:sleeps.Count -eq 0)
    $caught = Test-AgentCheck @{ Statuses = @('Running', 'StartPending'); Checks = @($good) }
    Check 'agent check: a service that stops within the 20 seconds (it crashes and Windows restarts it) fails' ($null -ne $caught -and $caught.Exception.Message -match 'stopped within 20 seconds of starting' -and ($script:sleeps -join ',') -ceq '20')
    $caught = Test-AgentCheck @{ Statuses = @('Running'); Checks = @($badCheck, $badCheck, $good) }
    Check 'agent check: a sign-in that fails and then works passes (retried every 10 seconds)' ($null -eq $caught -and ($script:sleeps -join ',') -ceq '20,10,10')
    $caught = Test-AgentCheck @{ Statuses = @('Running'); Checks = @($badCheck); Minutes = 0 }
    Check 'agent check: a sign-in that never works fails after the bounded time, with the check''s last line' (
        $null -ne $caught -and $caught.Exception.Message -match 'could not sign in at the panel within 0 minutes' -and $caught.Exception.Message -match 'exit code 1' -and $caught.Exception.Message -match 'FAILED, the panel refused this node')
    Check 'agent check: the check is bounded in time, not endless' ($updateParts['Wait-UpdatedAgentHealthy'] -match '\$deadline = \(Get-Date\)\.AddMinutes\(\$UpdateAgentCheckMinutes\)' -and $updateParts['Wait-UpdatedAgentHealthy'] -match '\(Get-Date\) -ge \$deadline\) \{ break \}')
    Check 'update: the updated panel is awaited with a bound, for UpdatePanelMinutes, on its own address with any certificate' (
        $updateParts['Wait-UpdatedPanelReady'] -match 'Wait-PanelReady \$client \$minutes' -and $updateParts['Wait-UpdatedPanelReady'] -match '\[int\] \$minutes = \$UpdatePanelMinutes' -and $updateParts['Wait-UpdatedPanelReady'] -match '"https://localhost:\$panelPort", ''''\)' -and
        $installText -match '\[int\] \$minutes = 3' -and $installText -match 'if \(string\.IsNullOrEmpty\(thumbprint\) && !_base\.IsLoopback\)')

    # --- The database restore ---
    . $restoreUpdateFunctions
    $PanelConfig = Join-Path $upRoot 'settings.json'
    [IO.File]::WriteAllText($PanelConfig, '{"ConnectionStrings": {"Pulsar": "Host=localhost;Port=5433;Database=pulsar;Username=pulsar;Password=s3cretPassw0rd"}}')
    function Test-DatabaseRestore([hashtable] $options = @{}) {
        $script:sqlCalls = New-Object Collections.Generic.List[string]
        $script:restoreEnvironment = $null
        $script:restoreArguments = @()
        & {
            function Invoke-PanelPsql($db, [string[]] $arguments) {
                $sql = $arguments[1]
                $script:sqlCalls.Add($sql)
                $failed = ($options['SetAsideFails'] -and $sql -like '*RENAME TO pulsar_failed_update_*CREATE SCHEMA public*') -or ($options['UndoFails'] -and $sql -like '*DROP SCHEMA public CASCADE*') -or ($options['DropFails'] -and $sql -like 'DROP SCHEMA pulsar_failed*')
                [pscustomobject]@{ ExitCode = $(if ($failed) { 1 } else { 0 }); Output = $(if ($failed) { 'permission denied' } else { '' }) }
            }
            function Find-PostgresBin { 'C:\PostgreSQL\bin' }
            function Invoke-Native([string] $exe) {
                $script:restoreEnvironment = $env:PGPASSWORD
                $script:restoreArguments = @($exe) + $args
                $script:sqlCalls.Add('pg_restore')
                [pscustomobject]@{ ExitCode = $(if ($options['RestoreFails']) { 1 } else { 0 }); Output = $(if ($options['RestoreFails']) { 'pg_restore: error: out of memory' } else { '' }) }
            }
            Get-Caught { Restore-RolledBackDatabase 'C:\backups\pulsar.dump' }
        }
    }
    $caught = Test-DatabaseRestore
    $aside = [regex]::Match(($script:sqlCalls -join ' '), 'pulsar_failed_update_\d{14}').Value
    Check 'database restore: the failed version''s tables are renamed aside and a new public made (one transaction), the dump is restored, and only then is the renamed schema dropped' (
        $null -eq $caught -and $aside -and $script:sqlCalls.Count -eq 3 -and $script:sqlCalls[0] -ceq "BEGIN; ALTER SCHEMA public RENAME TO $aside; CREATE SCHEMA public; COMMIT;" -and $script:sqlCalls[1] -ceq 'pg_restore' -and $script:sqlCalls[2] -ceq "DROP SCHEMA $aside CASCADE") ($script:sqlCalls -join ' | ')
    Check 'database restore: pg_restore runs in one transaction, stops at the first error, as the panel''s own user, with the password in the environment of the call only (never an argument)' (
        ($script:restoreArguments -join ' ') -ceq 'C:\PostgreSQL\bin\pg_restore.exe -h localhost -p 5433 -U pulsar -d pulsar --no-owner --single-transaction --exit-on-error C:\backups\pulsar.dump' -and
        $script:restoreEnvironment -ceq 's3cretPassw0rd' -and $null -eq $env:PGPASSWORD -and ($script:restoreArguments -join ' ') -notmatch 's3cretPassw0rd')
    $caught = Test-DatabaseRestore @{ RestoreFails = $true }
    Check 'database restore: a restore that fails puts the failed version''s tables back as they were, and says so' (
        $null -ne $caught -and $caught.Exception.Message -match 'pg_restore failed \(pg_restore: error: out of memory\); the failed version''s tables were put back as they were' -and
        $script:sqlCalls[2] -match '^BEGIN; DROP SCHEMA public CASCADE; ALTER SCHEMA pulsar_failed_update_\d{14} RENAME TO public; COMMIT;\z' -and @($script:sqlCalls | Where-Object { $_ -like 'DROP SCHEMA pulsar_failed*' }).Count -eq 0)
    $caught = Test-DatabaseRestore @{ RestoreFails = $true; UndoFails = $true }
    Check 'database restore: ...and when even that fails, names the schema the failed version''s tables are in' ($null -ne $caught -and $caught.Exception.Message -match 'tables are in the schema pulsar_failed_update_\d{14}')
    $caught = Test-DatabaseRestore @{ SetAsideFails = $true }
    Check 'database restore: tables that cannot be set aside stop it before pg_restore runs' ($null -ne $caught -and $caught.Exception.Message -match 'could not be set aside \(permission denied\)' -and $script:sqlCalls -notcontains 'pg_restore')
    $caught = Test-DatabaseRestore @{ DropFails = $true }
    Check 'database restore: a renamed schema that cannot be dropped afterwards is a warning, not a failure' ($null -eq $caught)
    Check 'database restore: psql runs as the panel''s own user, in its database, with the password in the environment of the call only' (
        $realUpdateFunctions['Invoke-PanelPsql'].ToString() -match "'-U', \`$db\['username'\], '-d', \`$db\['database'\]" -and $realUpdateFunctions['Invoke-PanelPsql'].ToString() -match "\`$env:PGPASSWORD = \`$db\['password'\]" -and
        $realUpdateFunctions['Invoke-PanelPsql'].ToString() -match 'finally \{ Remove-Item Env:PGPASSWORD')

    # --- The folders are administrators-only ---
    . $restoreUpdateFunctions
    $folderRoot = Join-Path $upRoot 'folders'
    $null = New-Item -ItemType Directory $folderRoot
    $elevatedNow = (New-Object Security.Principal.WindowsPrincipal ([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    $new = Join-Path $folderRoot 'Updates'
    $made = Get-Caught { Initialize-UpdateFolder $new }
    # Not elevated, the folder is made with the right permissions but its owner is this account, which New-SetupFolder refuses.
    Check 'folders: one that does not exist is made (elevated), or refused for its owner when this account is not an administrator' (
        $(if ($elevatedNow) { $null -eq $made } else { $null -ne $made -and $made.Exception.Message -match 'could not be made for SYSTEM and Administrators alone' })) "$made"
    $acl = Get-Acl -LiteralPath $new
    $rules = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
    $sids = @($rules | ForEach-Object { $_.IdentityReference.Value } | Select-Object -Unique | Sort-Object)
    Check 'folders: one that does not exist is made for SYSTEM and Administrators alone (full control), inheritance protected' (
        $acl.AreAccessRulesProtected -and ($sids -join ',') -ceq 'S-1-5-18,S-1-5-32-544' -and @($rules | Where-Object { $_.IsInherited }).Count -eq 0 -and @($rules | Where-Object { $_.FileSystemRights -ne 'FullControl' }).Count -eq 0) ($sids -join ',')
    if ($elevatedNow) {
        $again = Get-Thrown { Initialize-UpdateFolder $new }
        Check 'folders: one made so is accepted again (the agent''s update makes it too)' ($null -eq $again) "$again"
    }
    $ordinary = Join-Path $folderRoot 'ordinary'
    $null = New-Item -ItemType Directory $ordinary
    $caught = Get-Caught { Initialize-UpdateFolder $ordinary }
    Check 'folders: an existing folder others could change is refused, not repaired, and nothing is changed' (
        (Get-SetupExitCode $caught) -eq 3 -and $caught.Exception.Message -match 'which others could change' -and $caught.Exception.Message -match 'Nothing was changed' -and $caught.Exception.Message -match [regex]::Escape($ordinary)) "$caught"
    $junction = Join-Path $folderRoot 'junction'
    $null = New-Item -ItemType Junction -Path $junction -Target $ordinary
    $caught = Get-Caught { Initialize-UpdateFolder $junction }
    Check 'folders: a link in place of the folder is refused' ((Get-SetupExitCode $caught) -eq 3 -and $caught.Exception.Message -match 'is a link|could change|not for SYSTEM') "$caught"
    [IO.Directory]::Delete($junction)
    $asFile = Join-Path $folderRoot 'afile'
    Set-Content -LiteralPath $asFile -Value 'x'
    Check 'folders: a file in place of the folder is refused' ((Get-SetupExitCode (Get-Caught { Initialize-UpdateFolder $asFile })) -eq 3)
    Check 'folders: every folder an update uses (Updates, backups, previous) goes through it, and last-update.json and update.lock live in Updates' (
        $installText -match "\`$UpdateBackups = Join-Path \`$UpdateRoot 'backups'" -and $installText -match "\`$UpdatePrevious = Join-Path \`$UpdateRoot 'previous'" -and $installText -match "\`$UpdateResultFile = Join-Path \`$UpdateRoot 'last-update\.json'" -and
        $installText -match "\`$UpdateLockFile = Join-Path \`$UpdateRoot 'update\.lock'" -and $updateParts['Start-UpdateRun'] -match 'Initialize-UpdateFolder \$UpdateRoot' -and $updateParts['New-UpdateBackup'] -match 'Initialize-UpdateFolder \$UpdateBackups' -and
        $updateParts['Save-PreviousPrograms'] -match 'Initialize-UpdateFolder \$UpdatePrevious' -and $updateParts['Write-UpdateResult'] -match 'Initialize-UpdateFolder \$UpdateRoot' -and $updateParts['Start-UpdateRun'].IndexOf('Initialize-UpdateFolder') -lt $updateParts['Start-UpdateRun'].IndexOf('Lock-Update'))
    Check 'folders: the Updates folder is made like the setup folder (New-SetupFolder: the permissions in the call that creates it, then checked)' ($updateParts['Initialize-UpdateFolder'] -match 'New-SetupFolder \$path' -and $updateParts['New-SetupFolder'] -match 'CreateDirectory\(\$path, \$security\)')
}
finally {
    # The folder made for administrators alone: given back to this account (its owner may always change the permissions) to be removed.
    foreach ($leftover in (Join-Path $upRoot 'folders\Updates')) {
        if (Test-Path -LiteralPath $leftover) { $null = & (Join-Path ([Environment]::SystemDirectory) 'icacls.exe') $leftover /grant "*$($me.User.Value):(OI)(CI)F" }
    }
    Remove-Item -LiteralPath $upRoot -Recurse -Force -ErrorAction SilentlyContinue
    . $restoreUpdateFunctions
}

# How the main part uses all this (read, never run): the run starts first (folder, lock), -Update goes through Invoke-Update, a failure
# writes its result, the lock is released at the end, and nothing here puts a secret on a command line.
$mainStart = $installText.IndexOf('$main = {')
$mainEnd = $installText.IndexOf('$SetupParameters = $PSBoundParameters')
$mainBlock = $installText.Substring($mainStart, $mainEnd - $mainStart)
Check 'update: the run (folder and lock) starts before the machine is looked at, so an update that stops for any reason leaves its result' (
    $mainBlock.IndexOf('if ($Update) { Start-UpdateRun }') -gt $mainBlock.IndexOf('Assert-TrustedLocation') -and $mainBlock.IndexOf('if ($Update) { Start-UpdateRun }') -lt $mainBlock.IndexOf('$agentPackage = Get-AgentPackage'))
Check 'update: the programs are replaced only through Invoke-Update (backup, previous programs, checks, rollback), after the runtimes are checked' (
    $mainBlock -match 'Invoke-Update \$parts' -and $mainBlock -notmatch 'Update-Agent|Update-Panel' -and
    $mainBlock.IndexOf('Invoke-Update $parts') -gt $mainBlock.IndexOf('Assert-RuntimeSupported') -and $mainBlock.IndexOf('Invoke-Update $parts') -gt $mainBlock.IndexOf('Install-DotNetRuntimes'))
$invokeUpdateText = $updateParts['Invoke-Update']
Check 'update: the backup and the previous programs come before the first service is stopped, and the checks and rollback after' (
    $invokeUpdateText.IndexOf('New-UpdateBackup') -lt $invokeUpdateText.IndexOf('Save-PreviousPrograms') -and $invokeUpdateText.IndexOf('Save-PreviousPrograms') -lt $invokeUpdateText.IndexOf('Update-Agent') -and
    $invokeUpdateText.IndexOf('Update-Agent') -lt $invokeUpdateText.IndexOf('Update-Panel') -and $invokeUpdateText.IndexOf('Update-Panel') -lt $invokeUpdateText.IndexOf('Assert-UpdateHealthy') -and $invokeUpdateText.IndexOf('Assert-UpdateHealthy') -lt $invokeUpdateText.IndexOf('Undo-Update'))
Check 'update: the existing Update-Agent and Update-Panel are as they were (stop, replace, start), settings are never copied over' (
    $installText -match '(?s)function Update-Agent \{\s+Write-Step ''Updating the agent[^\n]+\s+Stop-Service \$AgentService\s+Copy-Program \(Join-Path \$PSScriptRoot ''agent''\) \$AgentDir\s+Start-Service \$AgentService\s+\}' -and
    $installText -match "(?s)function Update-Panel \{\s+Write-Step 'Updating the panel'\s+Stop-Service \`$PanelService\s+Copy-Program \(Join-Path \`$PSScriptRoot 'panel'\) \`$PanelDir\s+Copy-BackupScript\s+Invoke-PanelMigrations\s+Start-Panel\s+Write-StaleBackupTasks\s+\}" -and
    $installParts['Copy-Program'] -match "'appsettings\.Production\.json', 'downloads'")
$tail = $installText.Substring($mainEnd)
Check 'update: a failure writes the result file (failed) in the catch, and the lock is released in the finally' (
    $tail -match "(?s)catch \{.*if \(\`$Update\) \{ Write-UpdateResult 'failed' \`$message \}.*if \(-not \`$Unattended\)" -and $tail -match '(?s)finally \{\s+Remove-SetupDownloads\s+Remove-RestoreCopy\s+Unlock-Update\s+\}')
Check 'update: the exit code 7 is "rolled back", and the progress steps of an update are as they were (check, downloads, dotnet, update)' (
    $ExitRolledBack -eq 7 -and $installText -match "Update = @\('check', 'downloads', 'dotnet', 'update'\)" -and $installText -match '\$ExitRolledBack = 7 ')
Check 'update: the result is written as UTF-8 without a byte order mark, beside the file and moved over it' (
    $updateParts['Write-UpdateResult'] -match 'New-Object Text\.UTF8Encoding \$false' -and $updateParts['Write-UpdateResult'] -match 'Move-Item -LiteralPath \$written -Destination \$UpdateResultFile -Force' -and $updateParts['Write-UpdateResult'] -notmatch 'Set-Content|Out-File')
$guideInstall = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\..\docs\guides\install.md')
$guideUpdate = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\..\docs\guides\update.md')
$guideReleasing = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\..\docs\guides\releasing.md')
Check 'docs: update.md describes the backup, previous programs, health checks, rollback, exit code 7, last-update.json and the 3 backups kept' (
    $guideUpdate.Contains('Updates\backups') -and $guideUpdate.Contains('Updates\previous') -and $guideUpdate.Contains('last-update.json') -and $guideUpdate -match 'exit code `7`' -and $guideUpdate -match 'newest three' -and
    $guideUpdate.Contains('status --check') -and $guideUpdate.Contains('__EFMigrationsHistory'))
Check 'docs: install.md lists exit code 7 in the unattended exit codes' ($guideInstall -match '\| `7` \| .*rolled back')
Check 'docs: releasing.md covers the key, the build, the publishing, strictly increasing versions, -MinimumUpdateFrom and key rotation' (
    $guideReleasing.Contains('new-key') -and $guideReleasing.Contains('UpdateKeys.Trusted') -and $guideReleasing.Contains('-UpdateSigningKey') -and $guideReleasing.Contains('Publish-GitHubRelease.ps1') -and $guideReleasing.Contains('-MinimumUpdateFrom') -and
    $guideReleasing -match '(?i)strictly increas' -and $guideReleasing -match '(?i)rotat' -and $guideReleasing -match '(?i)public' -and $guideReleasing.Contains('PULSAR_UPDATE_KEY_PASSPHRASE') -and $guideReleasing.Contains('offline'))

# --- Audit fixes of the install, update and release scripts (each section runs the script's own functions on folders of this test's own) ---

$elevatedNow = (New-Object Security.Principal.WindowsPrincipal ([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$installText = Get-Content -Raw -LiteralPath $installScript
$auditRoot = Join-Path $Scratch ('pulsar-audit-tests-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory $auditRoot

# 1. The data folders are made with their permissions in the call that creates them, or checked.
$dataParts = Get-ScriptParts $installScript @('New-SetupError', 'Get-AccountName', 'Get-UntrustedWritersMessage', 'Get-ServiceAccountSid', 'Get-DataFolderProblems', 'New-DataFolderSecurity', 'Initialize-DataFolder') @('ExitUnsupported')
foreach ($part in $dataParts.Values) { . ([scriptblock]::Create($part)) }
Check 'data folders: a service''s account SID is worked out from its name (TrustedInstaller''s is the known one)' (
    (Get-ServiceAccountSid 'TrustedInstaller').Value -ceq $TrustedWriters[2] -and (Get-ServiceAccountSid 'trustedinstaller').Value -ceq $TrustedWriters[2] -and
    (Get-ServiceAccountSid 'PulsarPanel').Value -match '^S-1-5-80-\d+-\d+-\d+-\d+-\d+\z' -and (Get-ServiceAccountSid 'PulsarPanel').Value -cne (Get-ServiceAccountSid 'PulsarAgent').Value)
$usersSid = 'S-1-5-32-545'
$ordinaryFolder = Join-Path $auditRoot 'ordinary'
$null = New-Item -ItemType Directory $ordinaryFolder
$ordinaryProblems = @(Get-DataFolderProblems $ordinaryFolder)
Check 'data folders: one that inherits its permissions is a problem' (@($ordinaryProblems | Where-Object { $_ -match 'inherits its permissions' }).Count -eq 1) ($ordinaryProblems -join '; ')
$wideFolder = Join-Path $auditRoot 'wide'
$null = [IO.Directory]::CreateDirectory($wideFolder, (New-DataFolderSecurity @($usersSid)))
Check 'data folders: somebody allowed in besides SYSTEM, Administrators and the accounts given is named' (
    @(Get-DataFolderProblems $wideFolder | Where-Object { $_ -match 'is allowed in' }).Count -eq 1 -and @(Get-DataFolderProblems $wideFolder @($usersSid) | Where-Object { $_ -match 'is allowed in|inherits' }).Count -eq 0)
$wideRules = @((Get-Acl -LiteralPath $wideFolder).GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
Check 'data folders: the permissions given at creation are SYSTEM and Administrators full control and the given account modify, nothing inherited' (
    (Get-Acl -LiteralPath $wideFolder).AreAccessRulesProtected -and @($wideRules | Where-Object { $_.IsInherited }).Count -eq 0 -and
    (($wideRules | ForEach-Object { "$($_.IdentityReference.Value)=$($_.FileSystemRights)" } | Sort-Object) -join ',') -ceq 'S-1-5-18=FullControl,S-1-5-32-544=FullControl,S-1-5-32-545=Modify, Synchronize')
$linkTarget = Join-Path $auditRoot 'link-target'
$null = New-Item -ItemType Directory $linkTarget
$junction = Join-Path $auditRoot 'junction'
$null = New-Item -ItemType Junction -Path $junction -Target $linkTarget
Check 'data folders: a link is a problem' (@(Get-DataFolderProblems $junction | Where-Object { $_ -match 'is a link' }).Count -eq 1 -and (Get-SetupExitCode (Get-Caught { Initialize-DataFolder $junction })) -eq 3)
[IO.Directory]::Delete($junction)
$asFile = Join-Path $auditRoot 'a-file'
Set-Content -LiteralPath $asFile -Value 'x'
Check 'data folders: a file in place of the folder is a problem' (@(Get-DataFolderProblems $asFile | Where-Object { $_ -match 'is not a folder' }).Count -eq 1)
$caught = Get-Caught { Initialize-DataFolder $ordinaryFolder }
Check 'data folders: an existing folder that is not fit is refused, not repaired, with how to fix it, exit code 3' (
    (Get-SetupExitCode $caught) -eq 3 -and $caught.Exception.Message -match 'Move out of it what you need, delete that folder' -and $caught.Exception.Message -match [regex]::Escape($ordinaryFolder) -and
    @(Get-DataFolderProblems $ordinaryFolder | Where-Object { $_ -match 'inherits' }).Count -eq 1) "$caught"
$newFolder = Join-Path $auditRoot 'new\inner'
$made = Get-Caught { Initialize-DataFolder $newFolder @($usersSid) }
Check 'data folders: one that does not exist is made with those permissions (elevated), or refused for its owner when this account is not an administrator' (
    $(if ($elevatedNow) { $null -eq $made -and @(Get-DataFolderProblems $newFolder @($usersSid)).Count -eq 0 } else { $null -ne $made -and $made.Exception.Message -match 'is owned by' })) "$made"
$repairable = Join-Path $auditRoot 'repairable'
$null = New-Item -ItemType Directory $repairable
$repaired = Get-Caught { Initialize-DataFolder $repairable @() $true }
Check 'data folders: with $repair a folder owned by SYSTEM or Administrators that only inherits is given the permissions (elevated); one owned by anybody else never is' (
    $(if ($elevatedNow) { $null -eq $repaired -and @(Get-DataFolderProblems $repairable).Count -eq 0 } else { $null -ne $repaired -and $repaired.Exception.Message -match 'is owned by' -and (Get-Acl -LiteralPath $repairable).AreAccessRulesProtected -eq $false })) "$repaired"
Check 'data folders: the data root, the panel''s data folder and the update folder are made or checked before anything is kept in them' (
    $installText -match '(?s)function Start-SetupLog \{\s+Initialize-DataFolder \$DataRoot @\(\) \$true' -and $installText -notmatch 'New-Item -ItemType Directory \$DataRoot' -and
    $installText -match '(?s)Initialize-DataFolder \$PanelData @\(\(Get-ServiceAccountSid \$PanelService\)\.Value\)\s+Initialize-UpdateFolder \$UpdateRoot\s+foreach \(\$folder in ''keys'', ''logs'', ''tls''\)' -and
    $installText -notmatch 'Set-PrivateAcl \$PanelData' -and $installText -notmatch 'New-Item -ItemType Directory -Force \$PanelData')
Check 'data folders: an existing panel data or update folder is checked before the install changes anything' (
    (Get-ScriptParts $installScript @('Assert-PanelInstallable'))['Assert-PanelInstallable'] -match '(?s)Test-Path -LiteralPath \$PanelData\) \{\s+\$problems = @\(Get-DataFolderProblems \$PanelData @\(\(Get-ServiceAccountSid \$PanelService\)\.Value\)\).*Nothing was changed.*Initialize-UpdateFolder \$UpdateRoot')

# 2. -RestoreFrom copies the backup to a private folder and checks that copy: its backup.json, the Pulsar version, the migrations.
$restoreParts = Get-ScriptParts $installScript @('Copy-PlainTree', 'Get-ReleaseMigrations', 'Find-UnshippedMigrations', 'Read-BackupManifest', 'Get-BackupFileList', 'Get-BackupFileProblems',
    'Get-BackupVersionProblems', 'Copy-RestoreSource', 'Remove-RestoreCopy', 'New-SetupError', 'Get-UntrustedWritersMessage', 'Get-SetupExitCode') @('RestoreCopy', 'ExitUsage', 'ExitUnsupported')
foreach ($part in $restoreParts.Values) { . ([scriptblock]::Create($part)) }
$restoreRoot = Join-Path $auditRoot 'restore'
$null = New-Item -ItemType Directory $restoreRoot

# The migrations of a release, read from its assembly without running it.
$fakeDll = Join-Path $restoreRoot 'Fake.dll'
[IO.File]::WriteAllBytes($fakeDll, [Text.Encoding]::GetEncoding(28591).GetBytes("MZjunk`0" + '5' + '20261001154302_Alpha' + "`0" + 'x' + '20261002105727_Beta' + 'Q' + "`0" + '99999999999999 no id 2026_x'))
$shipped = @(Get-ReleaseMigrations $fakeDll)
Check 'restore: the release''s migrations are read from its assembly, also when a length byte before one is a digit or a letter follows it' (
    $shipped.Count -eq 2 -and $shipped[0] -ceq '20261001154302_Alpha' -and $shipped[1] -ceq '20261002105727_BetaQ') ($shipped -join ',')
Check 'restore: a release without its assembly lists none' (@(Get-ReleaseMigrations (Join-Path $restoreRoot 'nothing.dll')).Count -eq 0)
Check 'restore: an applied migration the release does not ship is found (by prefix), the shipped ones are not, and nothing is found when the release''s list is not known' (
    (@(Find-UnshippedMigrations @('20261001154302_Alpha', '20261002105727_Beta', '20261003000000_Gamma') $shipped) -join ',') -ceq '20261003000000_Gamma' -and
    @(Find-UnshippedMigrations @('20261003000000_Gamma') @()).Count -eq 0 -and @(Find-UnshippedMigrations @() $shipped).Count -eq 0)
$realDll = Join-Path $PSScriptRoot '..\..\src\Pulsar.Infrastructure\bin\Release\net10.0\Pulsar.Infrastructure.dll'
$migrationFiles = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot '..\..\src\Pulsar.Infrastructure\Persistence\Migrations') -Filter '2*_*.cs' | Where-Object { $_.Name -notmatch 'Designer' })
if ((Test-Path -LiteralPath $realDll) -and (Get-Item -LiteralPath $realDll).LastWriteTime -gt ($migrationFiles | Sort-Object LastWriteTime | Select-Object -Last 1).LastWriteTime) {
    $inDll = @(Get-ReleaseMigrations $realDll)
    Check 'restore: every migration of the repository is found in the built Pulsar.Infrastructure.dll' (
        $migrationFiles.Count -gt 0 -and @(Find-UnshippedMigrations @($migrationFiles | ForEach-Object { $_.BaseName }) $inDll).Count -eq 0) (@(Find-UnshippedMigrations @($migrationFiles | ForEach-Object { $_.BaseName }) $inDll) -join ',')
}

# The private copy: file by file, never through a link.
$tree = Join-Path $restoreRoot 'tree'
$null = New-Item -ItemType Directory (Join-Path $tree 'panel-data\keys')
Set-Content -LiteralPath (Join-Path $tree 'pulsar.dump') -Value 'dump'
Set-Content -LiteralPath (Join-Path $tree 'panel-data\keys\key-1.xml') -Value 'key'
$treeCopy = Join-Path $restoreRoot 'tree-copy'
Copy-PlainTree $tree $treeCopy
Check 'restore: a folder is copied whole, nested files too' (
    (Get-Content -LiteralPath (Join-Path $treeCopy 'pulsar.dump')) -ceq 'dump' -and (Get-Content -LiteralPath (Join-Path $treeCopy 'panel-data\keys\key-1.xml')) -ceq 'key')
$linkedTree = Join-Path $restoreRoot 'linked'
Copy-Item -LiteralPath $tree -Destination $linkedTree -Recurse
$outside = Join-Path $restoreRoot 'outside'
$null = New-Item -ItemType Directory $outside
Set-Content -LiteralPath (Join-Path $outside 'other.txt') -Value 'not the backup''s'
$null = New-Item -ItemType Junction -Path (Join-Path $linkedTree 'panel-data\tls') -Target $outside
$caught = Get-Caught { Copy-PlainTree $linkedTree (Join-Path $restoreRoot 'linked-copy') }
Check 'restore: a link in the backup is refused, and what it points at is not copied' (
    $null -ne $caught -and $caught.Exception.Message -match 'is a link' -and -not (Test-Path -LiteralPath (Join-Path $restoreRoot 'linked-copy\panel-data\tls\other.txt')))
[IO.Directory]::Delete((Join-Path $linkedTree 'panel-data\tls'))

# backup.json as Backup-Pulsar.ps1 writes it.
function New-TestBackup([string] $name, [hashtable] $overrides = @{}) {
    $folder = Join-Path $restoreRoot $name
    $null = New-Item -ItemType Directory (Join-Path $folder 'panel-data\keys')
    Set-Content -LiteralPath (Join-Path $folder 'pulsar.dump') -Value "dump of $name"
    Set-Content -LiteralPath (Join-Path $folder 'appsettings.Production.json') -Value '{}'
    Set-Content -LiteralPath (Join-Path $folder 'panel-data\keys\key-1.xml') -Value 'key'
    $files = [ordered]@{}
    foreach ($relative in 'appsettings.Production.json', 'panel-data/keys/key-1.xml', 'pulsar.dump') { $files[$relative] = (Get-FileHash -LiteralPath (Join-Path $folder $relative) -Algorithm SHA256).Hash.ToLowerInvariant() }
    $manifest = [ordered]@{ format = 1; pulsarVersion = '1.2.0'; created = '2026-10-21T10:00:00Z'; migrations = @('20261001154302_Alpha', '20261002105727_Beta'); files = $files }
    foreach ($key in $overrides.Keys) { $manifest[$key] = $overrides[$key] }
    [IO.File]::WriteAllText((Join-Path $folder 'backup.json'), ($manifest | ConvertTo-Json -Depth 4), (New-Object Text.UTF8Encoding $false))
    return $folder
}
$good = New-TestBackup 'good'
$manifest = Read-BackupManifest $good
Check 'restore: backup.json is read: version, migrations and the files with their hashes' (
    $manifest.Version -ceq '1.2.0' -and $manifest.Migrations.Count -eq 2 -and $manifest.Files.Count -eq 3 -and $manifest.Files['pulsar.dump'] -cmatch '^[0-9a-f]{64}\z')
Check 'restore: a folder without backup.json has no manifest' ($null -eq (Read-BackupManifest $tree))
Check 'restore: the files of a backup match their hashes, and nothing is missing or more' (@(Get-BackupFileProblems $manifest $good).Count -eq 0)
Set-Content -LiteralPath (Join-Path $good 'panel-data\keys\key-1.xml') -Value 'a key someone changed'
Set-Content -LiteralPath (Join-Path $good 'panel-data\keys\key-2.xml') -Value 'a key someone added'
Remove-Item -LiteralPath (Join-Path $good 'appsettings.Production.json')
$problems = @(Get-BackupFileProblems $manifest $good)
Check 'restore: a file changed, one added and one missing are each found' (
    $problems.Count -eq 3 -and ($problems -join '|') -match 'key-1\.xml is not what backup\.json says' -and ($problems -join '|') -match 'key-2\.xml is in the backup but not in backup\.json' -and ($problems -join '|') -match 'appsettings\.Production\.json is in backup\.json but not in the backup') ($problems -join '|')
foreach ($case in @(
        @('a format it does not know', @{ format = 2 }, 'format this script does not know'),
        @('a version that is not major.minor.patch', @{ pulsarVersion = '1.2' }, 'not major\.minor\.patch'),
        @('a migration that is not an id', @{ migrations = @('not an id') }, 'not a migration id'),
        @('a path that leaves the backup', @{ files = @{ '../x' = ('a' * 64) } }, 'not one of a backup'),
        @('a hash that is not SHA-256', @{ files = @{ 'pulsar.dump' = 'abc' } }, 'no SHA-256 for pulsar\.dump'))) {
    $caught = Get-Caught { Read-BackupManifest (New-TestBackup ('bad-' + [Guid]::NewGuid().ToString('N')) $case[1]) }
    Check "restore: backup.json with $($case[0]) is refused" ($null -ne $caught -and $caught.Exception.Message -match $case[2]) "$caught"
}
$notJson = Join-Path $restoreRoot 'not-json'
$null = New-Item -ItemType Directory $notJson
Set-Content -LiteralPath (Join-Path $notJson 'backup.json') -Value 'this is not json'
Check 'restore: a backup.json that is not JSON is refused' ($null -ne (Get-Caught { Read-BackupManifest $notJson }))
$versioned = [pscustomobject]@{ Version = '1.2.0'; Migrations = @('20261001154302_Alpha'); Files = @{} }
Check 'restore: a backup from a newer Pulsar than this release is refused; the same or an older one, or no version to compare, is not' (
    @(Get-BackupVersionProblems $versioned '1.1.9' $shipped).Count -eq 1 -and (Get-BackupVersionProblems $versioned '1.1.9' $shipped) -match 'newer than this release \(1\.1\.9\)' -and
    @(Get-BackupVersionProblems $versioned '1.2.0' $shipped).Count -eq 0 -and @(Get-BackupVersionProblems $versioned '1.10.0' $shipped).Count -eq 0 -and @(Get-BackupVersionProblems $versioned $null $shipped).Count -eq 0)
$futureDb = [pscustomobject]@{ Version = '1.0.0'; Migrations = @('20261001154302_Alpha', '20270101000000_Future'); Files = @{} }
Check 'restore: a backup whose database has a migration this release does not ship is refused' (
    (Get-BackupVersionProblems $futureDb '1.2.0' $shipped) -match '20270101000000_Future' -and @(Get-BackupVersionProblems $futureDb '1.2.0' @()).Count -eq 0 -and
    @(Get-BackupVersionProblems ([pscustomobject]@{ Version = $null; Migrations = $null; Files = @{} }) '1.2.0' $shipped).Count -eq 0)

# Copy-RestoreSource as a whole, with the machine's checks mocked (the folders here are the test's own, writable by this account).
function Test-RestoreSource([string] $source, [hashtable] $options = @{}) {
    & {
        $SetupRoot = Join-Path $restoreRoot ('setup-' + [Guid]::NewGuid().ToString('N'))
        $null = New-Item -ItemType Directory $SetupRoot
        $TrustRestoreSource = [bool]$options['Trust']
        $script:RestoreCopy = $null
        $script:mockWriters = $(if ($options.ContainsKey('Writers')) { @($options['Writers']) } else { @() })
        function Find-UntrustedWriters([string] $path) { $script:mockWriters }
        function New-SetupFolder([string] $path) { $null = New-Item -ItemType Directory $path }
        function Get-UpdateVersions { [pscustomobject]@{ From = $null; To = $(if ($options.ContainsKey('Release')) { $options['Release'] } else { '1.2.0' }) } }
        function Get-ReleaseMigrations { $shipped }
        $caught = Get-Caught { $script:copied = Copy-RestoreSource $source }
        [pscustomobject]@{ Error = $caught; Copy = $script:copied; Root = $SetupRoot; Kept = $script:RestoreCopy }
    }
}
$fresh = New-TestBackup 'fresh'
$ok = Test-RestoreSource $fresh
Check 'restore: a good backup is copied under the setup folder and that copy is what is returned (and remembered, to be deleted)' (
    $null -eq $ok.Error -and $ok.Copy -like (Join-Path $ok.Root 'restore-*\backup') -and (Get-Content -LiteralPath (Join-Path $ok.Copy 'pulsar.dump')) -ceq 'dump of fresh' -and $ok.Kept -ceq (Split-Path -Parent $ok.Copy)) "$($ok.Error)"
$tampered = New-TestBackup 'tampered'
Set-Content -LiteralPath (Join-Path $tampered 'pulsar.dump') -Value 'a dump someone else made'
$bad = Test-RestoreSource $tampered
Check 'restore: a dump that is not the one backup.json names is refused, exit code 2, nothing changed' (
    (Get-SetupExitCode $bad.Error) -eq 2 -and $bad.Error.Exception.Message -match 'pulsar\.dump is not what backup\.json says' -and $bad.Error.Exception.Message -match 'Nothing was changed')
$newer = Test-RestoreSource (New-TestBackup 'newer' @{ pulsarVersion = '9.0.0' })
Check 'restore: a backup from a newer Pulsar is refused with that reason' ((Get-SetupExitCode $newer.Error) -eq 2 -and $newer.Error.Exception.Message -match 'newer than this release')
$old = Join-Path $restoreRoot 'old-backup'
Copy-Item -LiteralPath $tree -Destination $old -Recurse
$oldRestore = Test-RestoreSource $old
Check 'restore: a backup from before backup.json is copied with a warning (it has nothing to check)' ($null -eq $oldRestore.Error -and (Test-Path -LiteralPath (Join-Path $oldRestore.Copy 'pulsar.dump')))
$open = Test-RestoreSource $fresh @{ Writers = @('Users can change D:\usb\backup') }
Check 'restore: a backup folder others could change is refused, naming who, unless -TrustRestoreSource says the drive keeps no permissions' (
    (Get-SetupExitCode $open.Error) -eq 3 -and $open.Error.Exception.Message -match 'Users can change D:\\usb\\backup' -and $open.Error.Exception.Message -match '-TrustRestoreSource' -and
    $null -eq (Test-RestoreSource $fresh @{ Writers = @('Users can change D:\usb\backup'); Trust = $true }).Error)
$withLink = Join-Path $restoreRoot 'with-link'
Copy-Item -LiteralPath $tree -Destination $withLink -Recurse
$null = New-Item -ItemType Junction -Path (Join-Path $withLink 'panel-data\tls') -Target $outside
$linked = Test-RestoreSource $withLink
Check 'restore: a link inside the backup stops the copy' ((Get-SetupExitCode $linked.Error) -eq 2 -and $linked.Error.Exception.Message -match 'is a link')
[IO.Directory]::Delete((Join-Path $withLink 'panel-data\tls'))
$asLink = Join-Path $restoreRoot 'as-link'
$null = New-Item -ItemType Junction -Path $asLink -Target $fresh
Check 'restore: -RestoreFrom that is itself a link, or not a folder, is refused' (
    (Get-SetupExitCode (Test-RestoreSource $asLink).Error) -eq 2 -and (Get-SetupExitCode (Test-RestoreSource (Join-Path $fresh 'pulsar.dump')).Error) -eq 2)
[IO.Directory]::Delete($asLink)
Check 'restore: the copy is deleted when the run ends' (
    $null -ne $ok.Kept -and ($(& { $RestoreCopy = $ok.Kept; Remove-RestoreCopy; -not (Test-Path -LiteralPath $ok.Kept) })))
$assertText = (Get-ScriptParts $installScript @('Assert-PanelInstallable'))['Assert-PanelInstallable']
$installPanelText = (Get-ScriptParts $installScript @('Install-Panel'))['Install-Panel']
Check 'restore: the install reads only the private copy: the preflight copies and checks it first, and Install-Panel never reads -RestoreFrom''s folder' (
    $assertText -match '\$backupCopy = Copy-RestoreSource \$RestoreFrom' -and $assertText -notmatch 'Join-Path \$RestoreFrom' -and $installPanelText -notmatch 'Join-Path \$RestoreFrom' -and
    $installPanelText -match 'Join-Path \$RestoreCopy' -and $installText -match '(?s)finally \{\s+Remove-SetupDownloads\s+Remove-RestoreCopy\s+Unlock-Update')
Check 'restore: -TrustRestoreSource is a parameter, said in the script''s help' ($installText -match '\[switch\] \$TrustRestoreSource' -and $installText -match '-TrustRestoreSource says a\s+drive keeps no permissions')

# Backup-Pulsar.ps1 writes backup.json.
$backupJsonParts = Get-ScriptParts $backupScript @('ConvertFrom-MigrationDump')
. ([scriptblock]::Create($backupJsonParts['ConvertFrom-MigrationDump']))
$dumpText = @('--', '-- Data for Name: __EFMigrationsHistory; Type: TABLE DATA; Schema: public; Owner: pulsar', '--', '',
    'COPY public."__EFMigrationsHistory" ("MigrationId", "ProductVersion") FROM stdin;', "20261002105727_Beta`t10.0.0", "20261001154302_Alpha`t10.0.0", '\.', '', 'COPY public.other (id) FROM stdin;', "1", '\.')
$ids = ConvertFrom-MigrationDump $dumpText
Check 'backup: the migration ids come from the COPY block of the history table in the dump, sorted' ($ids.Count -eq 2 -and $ids[0] -ceq '20261001154302_Alpha' -and $ids[1] -ceq '20261002105727_Beta')
Check 'backup: a dump without that table, or with a row that is not an id, gives none (not a guess)' (
    $null -eq (ConvertFrom-MigrationDump @('COPY public.other (id) FROM stdin;', '1', '\.')) -and $null -eq (ConvertFrom-MigrationDump @('COPY public."__EFMigrationsHistory" ("MigrationId", "ProductVersion") FROM stdin;', "not an id`t1", '\.')))
$backupFullText = Get-Content -Raw -LiteralPath $backupScript
Check 'backup: backup.json is written last, with the format, the Pulsar version, the dump''s migrations and the SHA-256 of every file' (
    $backupFullText -match '(?s)Copy-Item \$config \$target.*format = 1.*pulsarVersion = \$version.*migrations = \$migrations.*files = \$files.*backup\.json.*Backup written to' -and
    $backupFullText -match "Get-FileHash -LiteralPath \`$file\.FullName -Algorithm SHA256" -and $backupFullText -match 'Get-DumpMigrations \$PostgresBin \(Join-Path \$target ''pulsar\.dump''\)')
Check 'backup: pg_restore.exe, which reads the dump for its migrations, is run only when others cannot change it' ((Get-ScriptParts $backupScript @('Get-DumpMigrations'))['Get-DumpMigrations'] -match 'Find-UntrustedWriters \$restore')

# 4. -ConnectionString: a database that a newer Pulsar migrated is refused before anything changes.
. ([scriptblock]::Create((Get-ScriptParts $installScript @('Assert-DatabaseNotNewer'))['Assert-DatabaseNotNewer']))
function Test-DatabaseVersion([string] $connection, $psqlResult, [string[]] $releaseMigrations) {
    & {
        $script:psqlArguments = $null
        function Invoke-PanelPsql($db, [string[]] $arguments) { $script:psqlDb = $db; $script:psqlArguments = $arguments; $psqlResult }
        function Get-ReleaseMigrations { $releaseMigrations }
        Get-Caught { Assert-DatabaseNotNewer $connection }
    }
}
$shippedIds = @('20261001154302_Alpha', '20261002105727_Beta')
$connection = 'Host=db.example.com;Port=5433;Database=pulsar;Username=u;Password=pw1234'
$SetupSecrets.Clear()
$caught = Test-DatabaseVersion $connection ([pscustomobject]@{ ExitCode = 0; Output = "20261001154302_Alpha`n20261002105727_Beta`n20270101000000_Future" }) $shippedIds
Check 'database: a -ConnectionString database with a migration this release does not ship is refused (exit code 2), naming it, before anything changes' (
    (Get-SetupExitCode $caught) -eq 2 -and $caught.Exception.Message -match '20270101000000_Future' -and $caught.Exception.Message -match 'newer Pulsar' -and $caught.Exception.Message -match 'Nothing was changed')
Check 'database: ...asked with the connection string''s own host, port and user, and its password is a secret' (
    $script:psqlDb['host'] -ceq 'db.example.com' -and $script:psqlDb['port'] -ceq '5433' -and $script:psqlDb['username'] -ceq 'u' -and $SetupSecrets.Contains('pw1234') -and $script:psqlArguments[1] -match '__EFMigrationsHistory')
Check 'database: one whose migrations this release ships all of passes' ($null -eq (Test-DatabaseVersion $connection ([pscustomobject]@{ ExitCode = 0; Output = "20261001154302_Alpha`r`n20261002105727_Beta" }) $shippedIds))
Check 'database: a new, empty database (no migrations table: psql fails) passes' ($null -eq (Test-DatabaseVersion $connection ([pscustomobject]@{ ExitCode = 1; Output = 'relation "__EFMigrationsHistory" does not exist' }) $shippedIds))
Check 'database: without the release''s list of migrations nothing can be compared, and nothing is refused' ($null -eq (Test-DatabaseVersion $connection ([pscustomobject]@{ ExitCode = 0; Output = '20270101000000_Future' }) @()))
Check 'database: a connection string without host, user, database or password is left to the install to refuse' ($null -eq (Test-DatabaseVersion 'Host=db;Database=pulsar' ([pscustomobject]@{ ExitCode = 0; Output = '20270101000000_Future' }) $shippedIds))
Check 'database: the migrations of the dump a backup holds are checked against the release as well (-RestoreFrom)' ($installText -match 'Get-BackupVersionProblems \$manifest')

# 7. pg_dump, psql and pg_restore connect with the SSL settings of the Npgsql connection string, not libpq's default (prefer).
$ssl = Get-PgSslEnvironment @{ 'ssl mode' = 'VerifyFull'; 'root certificate' = 'C:\certs\ca.crt' }
Check 'ssl: SSL Mode=VerifyFull and Root Certificate become PGSSLMODE=verify-full and PGSSLROOTCERT' ($ssl['PGSSLMODE'] -ceq 'verify-full' -and $ssl['PGSSLROOTCERT'] -ceq 'C:\certs\ca.crt' -and $ssl.Count -eq 2)
$wrongModes = @(foreach ($pair in @(@('Disable', 'disable'), @('Allow', 'allow'), @('Prefer', 'prefer'), @('Require', 'require'), @('VerifyCA', 'verify-ca'), @('verifyfull', 'verify-full'))) {
        if ((Get-PgSslEnvironment @{ sslmode = $pair[0] })['PGSSLMODE'] -cne $pair[1]) { $pair[0] }
    })
Check 'ssl: every Npgsql mode maps to libpq''s, under the key SslMode too' ($wrongModes.Count -eq 0) ($wrongModes -join ',')
Check 'ssl: no SSL setting asks for none (libpq''s default, prefer, is Npgsql''s)' ((Get-PgSslEnvironment @{ host = 'localhost' }).Count -eq 0)
Check 'ssl: a mode this script does not know is refused, not guessed' ((Get-Thrown { Get-PgSslEnvironment @{ 'ssl mode' = 'Verify-Everything' } }) -match 'SSL Mode')
$sslSpots = [ordered]@{
    'Backup-Pulsar.ps1' = $backupText
    'Restore-PanelDatabase' = (Get-ScriptParts $installScript @('Restore-PanelDatabase'))['Restore-PanelDatabase']
    'Invoke-PanelPsql' = (Get-ScriptParts $installScript @('Invoke-PanelPsql'))['Invoke-PanelPsql']
    'Restore-RolledBackDatabase' = (Get-ScriptParts $installScript @('Restore-RolledBackDatabase'))['Restore-RolledBackDatabase']
}
foreach ($spot in $sslSpots.Keys) {
    Check "ssl: $spot passes the connection string's SSL settings to the program it runs, and clears them afterwards" (
        $sslSpots[$spot] -match 'Get-PgSslEnvironment' -and $sslSpots[$spot] -match 'Remove-Item Env:PGPASSWORD, Env:PGSSLMODE, Env:PGSSLROOTCERT')
}

# 9. The backup copies the panel's keys and certificates without following a link the panel's account could have planted.
$planted = Join-Path $restoreRoot 'planted'
$null = New-Item -ItemType Directory (Join-Path $planted 'keys')
Set-Content -LiteralPath (Join-Path $planted 'keys\key-1.xml') -Value 'key'
$loopTarget = Join-Path $restoreRoot 'loop-target'
$null = New-Item -ItemType Directory $loopTarget
$null = New-Item -ItemType Junction -Path (Join-Path $planted 'keys\loop') -Target $loopTarget
$caught = Get-Caught { Copy-PlainTree (Join-Path $planted 'keys') (Join-Path $restoreRoot 'planted-copy') }
Check 'backup: a junction in the panel''s keys is refused, not followed' ($null -ne $caught -and $caught.Exception.Message -match 'is a link')
[IO.Directory]::Delete((Join-Path $planted 'keys\loop'))
Check 'backup: the keys, keyring and tls folders are copied with Copy-PlainTree, and a backup that was stopped by a link is deleted' (
    $backupFullText -notmatch 'Copy-Item \(Join-Path \$panelData \$folder\)' -and $backupFullText -match 'Copy-PlainTree \(Join-Path \$panelData \$folder\) \(Join-Path \$data \$folder\)' -and
    $backupFullText -match '(?s)catch \{\s+Remove-Item -LiteralPath \$target -Recurse -Force.*?throw')

if ($script:failures -gt 0) {
    Write-Host "$($script:failures) check(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host 'All checks passed.' -ForegroundColor Green
