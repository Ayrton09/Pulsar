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

$shared = @('Get-AccountName', 'Find-UntrustedWriter', 'Find-UntrustedWriters', 'Find-UntrustedAncestors', 'Test-PrivateFolder')
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
        'Get-InstalledRuntimes', 'Get-MissingRuntimes', 'Test-DotNetDownloadUrl', 'Get-DotNetInstallerEntries', 'Copy-Program', 'Initialize-SetupNetwork',
        # What a panel's install checks first and undoes when it fails; EDB's builds; signers' chains; the agent's runtimes.
        'Test-PortFree', 'Test-PfxFile', 'Get-EndpointPort', 'Assert-PanelInstallable', 'Get-PanelDatabaseState', 'Get-PanelDatabaseMessage',
        'New-Secret', 'New-PanelDatabase', 'Remove-PanelDatabase', 'Undo-PanelInstall', 'Find-PostgresInstallerUrl', 'Get-SignerChain', 'Get-AgentMissingRuntimes',
        # The panel's self-signed certificate: the names and addresses it lists, made in memory.
        'New-PanelCertificate', 'New-SelfSignedPanelCertificate', 'Get-PanelCertificateNames', 'ConvertTo-CertificateAddress', 'Test-PrivateAddress',
        'Get-LanAddresses', 'Select-LanAddresses', 'Get-DhcpAddresses', 'Format-CertificateLine', 'Format-Fingerprint', 'Write-RemoteAccess', 'Assert-SameCertificate')) `
    (@($sharedVariables) + @('TrustedName', 'ServerAuthentication', 'ExitFailed', 'ExitUsage', 'ExitUnsupported', 'ExitOffline', 'ExitUntrusted',
        'ExitCertificate', 'ExitRestart', 'NodeCodePrefix', 'NodeCodeMaxLength', 'NodeCodeMaxUrlLength', 'ReusableTokenPrefix', 'JsonStringPattern',
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
$certificateFunctions = @($parsed.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -in 'New-PanelCertificate', 'New-SelfSignedPanelCertificate' }, $true))
$storeUses = @($certificateFunctions | Where-Object { $_.Extent.Text -match '(?i)Cert:\\|New-SelfSignedCertificate|Export-PfxCertificate|Export-Certificate|X509Store|CngKey\]::Create|PersistKeySet|MachineKeySet' })
Check 'self-signed certificate: made without a certificate store or a stored key' ($certificateFunctions.Count -eq 2 -and $storeUses.Count -eq 0) (@($storeUses | ForEach-Object Name) -join ', ')
# -CertificatePath's PFX is opened with an ephemeral key (none written to a key container) and disposed.
$panelCertificateText = @($certificateFunctions | Where-Object Name -eq 'New-PanelCertificate')[0].Extent.Text
$pfxOpens = @([regex]::Matches($panelCertificateText, 'X509Certificate2\b[^\r\n]*') | ForEach-Object { $_.Value })
Check '-CertificatePath: its PFX is opened with EphemeralKeySet, and disposed' ($pfxOpens.Count -eq 1 -and $pfxOpens[0] -match 'EphemeralKeySet' -and
    ([regex]::Matches($panelCertificateText, '\$certificate\.Dispose\(\)')).Count -eq 2) ($pfxOpens -join ' | ')

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

$codes = @($ExitFailed, $ExitUsage, $ExitUnsupported, $ExitOffline, $ExitUntrusted, $ExitCertificate, $ExitRestart)
Check 'exit codes: 1 to 6 and 3010, each once' ((@($codes | Sort-Object) -join ',') -eq '1,2,3,4,5,6,3010') ($codes -join ',')
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
Check 'node code: only Add-TrustedCertificate and Remove-TrustedCertificates touch the trusted roots' (@($storeWriters | Where-Object { $_ -notin 'Add-TrustedCertificate', 'Remove-TrustedCertificates' }).Count -eq 0) ($storeWriters -join ', ')
$trustCalls = @($installAst.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -like 'Add-TrustedCertificate*' }, $true))
$trustConditions = @(foreach ($trustCall in $trustCalls) {
        $clause = $trustCall.Parent
        while ($clause -and $clause -isnot [Management.Automation.Language.IfStatementAst]) { $clause = $clause.Parent }
        if (-not $clause) { '(none)'; continue }
        @($clause.Clauses | Where-Object { $_.Item2.Extent.StartOffset -le $trustCall.Extent.StartOffset -and $_.Item2.Extent.EndOffset -ge $trustCall.Extent.EndOffset } | ForEach-Object { $_.Item1.Extent.Text }) -join ''
    })
Check 'node code: the certificate stores are written for -TrustCertificate and the all-in-one panel''s own certificate only' (
    $trustCalls.Count -eq 2 -and @($trustConditions | Where-Object { $_ -notin '$TrustCertificate', '$certificate.SelfSigned' }).Count -eq 0 -and
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
            $Port = $freePorts[0]; $AgentsPort = 0; $SftpPort = $freePorts[1]; $CertificatePath = $null; $CertificatePassword = $null; $RestoreFrom = $null; $HostName = @()
            foreach ($name in $values.Keys) { Set-Variable -Name $name -Value $values[$name] }
            function Test-Service([string] $name) { return $false }
            Get-Caught { Assert-PanelInstallable }
        }
    }
    Check 'preflight: free ports and no certificate pass' ($null -eq (Test-Preflight @{})) "$(Test-Preflight @{})"
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

$signingParts = Get-ScriptParts (Join-Path $PSScriptRoot '..\release\Build-Release.ps1') @('Get-SignatureProblem', 'Assert-InnoSetupSigned', 'Get-WindowsKitsRoots', 'Find-SignTool',
    'Assert-SignToolSigned', 'Assert-SigningCertificate', 'Read-TrustedSigningMetadata', 'Find-TrustedSigningClient', 'Assert-TrustedSigningClient', 'Initialize-Signing',
    'Get-PulsarPrograms', 'Format-InnoToolWord', 'Get-InnoSignCommand') @('InnoSetupSigner', 'SignToolSigner', 'SignToolLibraries', 'CodeSigningUsage', 'SignedDescription', 'SignedInformationUrl')
foreach ($part in $signingParts.Values) { . ([scriptblock]::Create($part)) }
$SignTool = $null; $SigningThumbprint = $null; $TrustedSigningMetadata = $null; $TrustedSigningDlib = $null; $TimestampUrl = 'http://timestamp.digicert.com'; $GivenSigningOptions = @()
$Signing = $null

Check 'signing: off unless asked (no signing option, no signing)' ($null -eq (Initialize-Signing))
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
        # A copy of signtool.exe takes the DLLs it imports from beside it: a swapped one is refused.
        $copyFolder = Join-Path $signRoot 'signtool-copy'; New-Item -ItemType Directory $copyFolder | Out-Null
        Copy-Item $realTool (Join-Path $copyFolder 'signtool.exe')
        Check 'signing: a signtool.exe copied alone is taken' ($null -eq (Get-Thrown { Assert-SignToolSigned (Join-Path $copyFolder 'signtool.exe') }))
        if (Test-Path (Join-Path (Split-Path $realTool) 'mssign32.dll')) {
            Copy-Item (Join-Path (Split-Path $realTool) 'mssign32.dll') $copyFolder
            Check 'signing: with the SDK''s own mssign32.dll beside it' ($null -eq (Get-Thrown { Assert-SignToolSigned (Join-Path $copyFolder 'signtool.exe') }))
        }
        Set-Content (Join-Path $copyFolder 'wintrust.dll') 'not Microsoft''s'
        Check 'signing: a wintrust.dll beside signtool.exe that is not validly signed by Microsoft is refused' ((Get-Thrown { Assert-SignToolSigned (Join-Path $copyFolder 'signtool.exe') }) -match 'wintrust\.dll is not validly signed by Microsoft Corporation')
        $GivenSigningOptions = @('SigningThumbprint'); $SigningThumbprint = '0123456789ABCDEF0123456789ABCDEF01234567'
        Check 'signing: a thumbprint that is not in the store is refused before any build' ((Get-Thrown { Initialize-Signing }) -match "is in the current user's Personal store")
        $GivenSigningOptions = @(); $SigningThumbprint = $null
    }
    Check 'signing: a catalog-signed Windows file is not an embedded Authenticode signature' ($null -ne (Get-SignatureProblem (Join-Path $env:windir 'System32\notepad.exe') 'Microsoft Corporation'))
    # The same check ISCC gets: an unsigned compiler, or a folder that is no Inno Setup install, is refused.
    $fakeInno = Join-Path $signRoot 'Inno Setup 7'; New-Item -ItemType Directory $fakeInno | Out-Null
    Set-Content (Join-Path $fakeInno 'ISCC.exe') 'x'
    Check 'signing: a folder with no ISCmplr.dll is not an Inno Setup install' ((Get-Thrown { Assert-InnoSetupSigned (Join-Path $fakeInno 'ISCC.exe') }) -match 'has no ISCmplr\.dll')
    Set-Content (Join-Path $fakeInno 'ISCmplr.dll') 'x'
    Check 'signing: an unsigned ISCC.exe is refused as before' ((Get-Thrown { Assert-InnoSetupSigned (Join-Path $fakeInno 'ISCC.exe') }) -match "is not validly signed by Pyrsys B\.V\., Inno Setup's publisher")

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

    # Azure Trusted Signing's metadata and client.
    function Write-SigningFile([string] $name, [string] $text) { $path = Join-Path $signRoot $name; Set-Content $path $text -Encoding UTF8; return $path }
    $goodMetadata = Write-SigningFile 'metadata.json' '{ "Endpoint": "https://eus.codesigning.azure.net", "CodeSigningAccountName": "account", "CertificateProfileName": "profile", "CorrelationId": "x" }'
    Check 'signing: Trusted Signing metadata with its three fields is taken' ((Read-TrustedSigningMetadata $goodMetadata) -eq (Resolve-Path $goodMetadata).Path)
    Check 'signing: a metadata file that is not there is refused' ((Get-Thrown { Read-TrustedSigningMetadata (Join-Path $signRoot 'nope.json') }) -match 'existing JSON file')
    Check 'signing: metadata that is not JSON is refused' ((Get-Thrown { Read-TrustedSigningMetadata (Write-SigningFile 'bad.json' '{ nope') }) -match 'is not JSON')
    $badMetadata = [ordered]@{
        'a field missing' = '{ "Endpoint": "https://x.codesigning.azure.net", "CodeSigningAccountName": "a" }'
        'a name in another case' = '{ "endpoint": "https://x.codesigning.azure.net", "CodeSigningAccountName": "a", "CertificateProfileName": "p" }'
        'an empty field' = '{ "Endpoint": "https://x.codesigning.azure.net", "CodeSigningAccountName": " ", "CertificateProfileName": "p" }'
        'a field that is not text' = '{ "Endpoint": "https://x.codesigning.azure.net", "CodeSigningAccountName": 5, "CertificateProfileName": "p" }'
        'an http endpoint' = '{ "Endpoint": "http://x.codesigning.azure.net", "CodeSigningAccountName": "a", "CertificateProfileName": "p" }'
        'an endpoint with a space' = '{ "Endpoint": "https://x.net/a b", "CodeSigningAccountName": "a", "CertificateProfileName": "p" }'
        'a JSON string' = '"hello"'
    }
    $index = 0
    foreach ($case in $badMetadata.Keys) {
        $index++
        Check "signing: metadata with $case is refused" ($null -ne (Get-Thrown { Read-TrustedSigningMetadata (Write-SigningFile "bad$index.json" $badMetadata[$case]) }))
    }
    $TrustedSigningDlib = Join-Path $signRoot 'client\Azure.CodeSigning.Dlib.dll'
    Check 'signing: a Trusted Signing client that is not there is refused' ((Get-Thrown { Find-TrustedSigningClient }) -match 'Azure\.CodeSigning\.Dlib\.dll was not found')
    New-Item -ItemType Directory (Split-Path $TrustedSigningDlib) | Out-Null; Set-Content $TrustedSigningDlib 'x'
    Check 'signing: the Trusted Signing client is found by its file name' ((Find-TrustedSigningClient) -eq (Resolve-Path $TrustedSigningDlib).Path)
    Check 'signing: an unsigned Trusted Signing client is refused' ((Get-Thrown { Assert-TrustedSigningClient (Resolve-Path $TrustedSigningDlib).Path }) -match 'is not validly signed by Microsoft Corporation')
    $TrustedSigningDlib = Join-Path $signRoot 'client\other.dll'; Set-Content $TrustedSigningDlib 'x'
    Check 'signing: a client under another file name is refused' ((Get-Thrown { Find-TrustedSigningClient }) -match 'was not found')
    $TrustedSigningDlib = $null
    if ($realTool) {
        $client = Join-Path $signRoot 'client2'; New-Item -ItemType Directory $client | Out-Null
        Copy-Item (Join-Path (Split-Path $realTool) 'mssign32.dll') (Join-Path $client 'Azure.CodeSigning.Dlib.dll')
        Check 'signing: a client signed by Microsoft is taken' ($null -eq (Get-Thrown { Assert-TrustedSigningClient (Join-Path $client 'Azure.CodeSigning.Dlib.dll') }))
        Set-Content (Join-Path $client 'Azure.Core.dll') 'unsigned'
        Check 'signing: an unsigned library beside the client is refused' ((Get-Thrown { Assert-TrustedSigningClient (Join-Path $client 'Azure.CodeSigning.Dlib.dll') }) -match 'Azure\.Core\.dll is not validly signed')
    }

    # The programs to sign, and the command Inno Setup runs.
    $published = Join-Path $signRoot 'published'
    New-Item -ItemType Directory $published, (Join-Path $published 'es'), (Join-Path $published 'wwwroot') | Out-Null
    foreach ($file in 'Pulsar.Api.exe', 'Pulsar.Api.dll', 'Pulsar.Domain.dll', 'PulsarAgent.dll', 'Npgsql.dll', 'Serilog.dll', 'Pulsar.Api.pdb', 'es\Pulsar.Api.resources.dll', 'wwwroot\app.js') { Set-Content (Join-Path $published $file) 'x' }
    $programs = @(Get-PulsarPrograms $published @('Pulsar.Api.exe', 'Pulsar.Api.dll') | ForEach-Object { $_.Substring($published.Length + 1) } | Sort-Object)
    Check 'signing: the programs are Pulsar''s own .exe and .dll, never the libraries or other files' (($programs -join ',') -eq 'es\Pulsar.Api.resources.dll,Pulsar.Api.dll,Pulsar.Api.exe,Pulsar.Domain.dll,PulsarAgent.dll') ($programs -join ',')
    Check 'signing: a program that should be there and is not fails the build (a filter that matched nothing would sign nothing)' ((Get-Thrown { Get-PulsarPrograms $published @('PulsarAgent.exe') }) -match 'has no PulsarAgent\.exe to sign')
    Check 'signing: Inno Setup''s command words are quoted with $q when they have a space, and $ is doubled' ((Format-InnoToolWord 'sha256') -ceq 'sha256' -and (Format-InnoToolWord 'C:\Program Files\x.exe') -ceq '$qC:\Program Files\x.exe$q' -and
        (Format-InnoToolWord 'C:\a$b') -ceq 'C:\a$$b' -and (Format-InnoToolWord 'C:\a b\$c') -ceq '$qC:\a b\$$c$q' -and $null -ne (Get-Thrown { Format-InnoToolWord 'a"b' }))
    $Signing = [pscustomobject]@{ Arguments = [string[]]@('sign', '/fd', 'sha256', '/tr', 'http://t.example', '/dlib', 'C:\My Tools\d.dll') }
    $innoCommand = Get-InnoSignCommand 'C:\Users\Jo Do\signtool.exe'
    Check 'signing: Inno Setup runs signtool with the release''s arguments on $f' ($innoCommand -ceq '$qC:\Users\Jo Do\signtool.exe$q sign /fd sha256 /tr http://t.example /dlib $qC:\My Tools\d.dll$q $f') $innoCommand
    $Signing = $null
}
finally { Remove-Item -LiteralPath $signRoot -Recurse -Force }

# The order and the options, read from the scripts: programs first, then the MSI that holds them, then the setups.
$signedPrograms = $buildRelease.IndexOf('Invoke-Signing $programs')
$msiBuilt = $buildRelease.IndexOf("'Build-AgentMsi.ps1'")
$msiChecksum = $buildRelease.IndexOf('$msiHash = Write-Checksum $msi')
Check 'signing: the programs are signed after they are published and before the MSI, release.json and the setups pack them' (
    $signedPrograms -gt $buildRelease.IndexOf('dotnet publish (Join-Path $repo ''src\Pulsar.Agent'') @common --self-contained') -and $signedPrograms -lt $msiBuilt -and
    $signedPrograms -lt $buildRelease.IndexOf("Join-Path `$staging 'release.json'") -and $signedPrograms -lt $buildRelease.IndexOf("Build-Setup `$iscc 'PulsarAgentSetup.iss'") -and
    $buildRelease -match "Get-PulsarPrograms \(Join-Path \`$staging 'panel'\) @\('Pulsar\.Api\.exe', 'Pulsar\.Api\.dll'\)" -and $buildRelease -match "Get-PulsarPrograms \`$msiAgent @\('PulsarAgent\.exe', 'PulsarAgent\.dll'\)")
Check 'signing: the MSI is signed after it is built and before its checksum is written' (
    $buildRelease.IndexOf("Invoke-Signing @(`$msi) 'the agent MSI'") -gt $msiBuilt -and $buildRelease.IndexOf("Invoke-Signing @(`$msi) 'the agent MSI'") -lt $msiChecksum)
Check 'signing: always SHA-256 file and timestamp digests and an RFC 3161 timestamp, verified with /pa' (
    $buildRelease -match "'sign', '/fd', 'sha256', '/tr', \`$TimestampUrl, '/td', 'sha256'" -and $buildRelease -match "'verify', '/pa'" -and $buildRelease -match '-not \$signature\.TimeStamperCertificate')
Check 'signing: the certificate is chosen by thumbprint from the user''s store, or by Trusted Signing''s client; no PFX and no password option' (
    $buildRelease -match "'/sha1', \`$thumbprint, '/s', 'My'" -and $buildRelease -match "'/dlib', \`$dlib, '/dmdf', \`$metadata" -and $buildRelease -notmatch "'/f'" -and $buildRelease -notmatch "'/p'" -and $buildRelease -notmatch '(?i)\[string\] \$\w*(password|pfx)')
Check 'signing: the setups are signed by Inno Setup (so the uninstaller is) with the one named sign tool, on a checked copy of signtool, and verified' (
    $buildRelease -match "'/DSignSetups', '/NI', `"/Spulsarsign=" -and $buildRelease -match 'Assert-Signed @\(\$setup\) \$signTool' -and $buildRelease -match 'Remove-SignToolCopy \$signTool')
$commonText = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\release\setup\Common.iss')
$signedBlock = [regex]::Match($commonText, '(?s)#ifdef SignSetups(.*?)#endif')
$unsignedPart = $commonText.Replace($signedBlock.Value, '')
$signedLines = @($signedBlock.Groups[1].Value -split "`r?`n" | Where-Object { $_.Trim() -and $_ -notmatch '^\s*;' })
Check 'signing: Common.iss names the sign tool, and a signed uninstaller, only when the setups are to be signed' (
    $signedBlock.Success -and ($signedLines -join ',') -ceq 'SignTool=pulsarsign,SignedUninstaller=yes' -and $unsignedPart -notmatch '(?m)^\s*(SignTool|SignedUninstaller)=') ($signedLines -join ',')
Check 'signing: release.json records the signer of a signed build and is unchanged for an unsigned one' (
    $buildRelease -match "if \(\`$Signing\) \{ \`$releaseInfo\['signing'\] = \[ordered\]@\{ signed = \`$true; signer = \`$Signing\.Subject \} \}")

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
Check 'setups: nothing changed (2 to 6) rolls the setup back; otherwise its entry in Apps stays' ($commonCode['RunInstallStep'] -match '\(Code < ExitUsage\) or \(Code > ExitCertificate\)' -and $commonCode['RunInstallStep'] -match 'RunKept := True' -and $commonCode['RunInstallStep'] -match 'Abort;')
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

if ($script:failures -gt 0) {
    Write-Host "$($script:failures) check(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host 'All checks passed.' -ForegroundColor Green
