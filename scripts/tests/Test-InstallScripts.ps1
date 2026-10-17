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
        'Resolve-NodeCertificate', 'Test-PostgresInstallerName', 'Select-PostgresVersion', 'Get-PostgresInstallerUrl', 'Find-NonLocalHbaRules',
        'Get-CertificateOrganizations', 'Assert-InstallerSignature', 'ConvertTo-CommandLine', 'Add-RuntimeRequirement', 'Read-RuntimeRequirements',
        'Get-InstalledRuntimes', 'Get-MissingRuntimes', 'Test-DotNetDownloadUrl', 'Get-DotNetInstallerEntries', 'Copy-Program', 'Initialize-SetupNetwork')) `
    (@($sharedVariables) + @('TrustedName', 'ServerAuthentication', 'ExitFailed', 'ExitUsage', 'ExitUnsupported', 'ExitOffline', 'ExitUntrusted',
        'ExitCertificate', 'ExitRestart', 'NodeCodePrefix', 'NodeCodeMaxLength', 'NodeCodeMaxUrlLength', 'ReusableTokenPrefix', 'JsonStringPattern',
        'DotNetChannel', 'DotNetHosts', 'DotNetFrameworks', 'CodeSigning', 'PostgresDownloadsUrl', 'PostgresSigner', 'DotNetSigner', 'StepPlans',
        'SetupSecrets', 'SecretInputs'))
$backupParts = Get-ScriptParts $backupScript (@($shared) + @('Find-UntrustedDestination', 'New-PrivateFolder')) $sharedVariables

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
function New-TestCertificate([object[]] $Names = @(), $Authority = $false, $Uses = @($ServerAuth)) {
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
        $certificate = $request.CreateSelfSigned([DateTimeOffset]::Now.AddDays(-1), [DateTimeOffset]::Now.AddDays(1))
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
foreach ($good in @('panel.example.com', 'PANEL', '*.example.com', 'panel.example.com.', 'my_box-1.lan')) {
    Check "-HostName: accepts '$good'" ([regex]::IsMatch($good, $hostNamePattern, 'None'))
}
foreach ($bad in @("panel.example.com$lf", "*.example.com$lf", "panel.example.com.$lf", "panel.example.com$lf$lf", "panel$lf.example.com", "panel.example.com$cr", 'pa nel', '', 'a..b')) {
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

$names = Get-CertificateNames (New-TestCertificate @((Dns 'panel.example.com'), (Dns 'localhost'), (Dns 'PANEL'), (Ip '10.0.0.5')))
Check "foreign names: none besides the panel's host, localhost and machine names (the panel's IP named in -PanelUrl)" (@(Get-ForeignCertificateNames $names @('10.0.0.5')).Count -eq 1 -and @(Get-ForeignCertificateNames $names @('panel.example.com', '10.0.0.5')).Count -eq 0)
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
foreach ($name in @($SecretInputs.Values) + 'PULSAR_SECRETS_FILE', 'PULSAR-STEP', 'PULSAR-ERROR', 'PULSAR-LOG', 'PULSAR-PANEL-URL', '-PostgresInstaller', '-DotNetInstallers', '-NodeCode', '-Unattended') {
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
Check 'node code: the pinned certificate, not trusted yet, is to be trusted' ((Resolve-NodeCertificate (Pin $panelHash) (Presented $panelCertificate $false) 'panel.example.com') -eq 'trust')
Check 'node code: the pinned certificate, trusted already, is left as it is' ((Resolve-NodeCertificate (Pin $panelHash) (Presented $panelCertificate $true) 'panel.example.com') -eq 'trusted')
Check 'node code: no pin and a certificate this machine trusts' ((Resolve-NodeCertificate (Pin $null) (Presented $panelCertificate $true) 'panel.example.com') -eq 'trusted')
$caught = Get-Caught { Resolve-NodeCertificate (Pin $null) (Presented $panelCertificate $false) 'panel.example.com' }
Check 'node code: no pin and a certificate this machine does not trust is refused' ((Get-SetupExitCode $caught) -eq 6) "$caught"
$caught = Get-Caught { Resolve-NodeCertificate (Pin $hash) (Presented $panelCertificate $true) 'panel.example.com' }
Check 'node code: another certificate than the pinned one is refused, even one this machine trusts' ((Get-SetupExitCode $caught) -eq 6 -and $caught.Exception.Message -match $panelHash -and $caught.Exception.Message -match $hash) "$caught"
$authorityCertificate = New-TestCertificate @((Dns 'panel.example.com')) $true
$caught = Get-Caught { Resolve-NodeCertificate (Pin (Get-CertificateHash $authorityCertificate)) (Presented $authorityCertificate $false) 'panel.example.com' }
Check 'node code: a pinned certificate authority is refused (the -TrustCertificate checks)' ((Get-SetupExitCode $caught) -eq 6 -and $caught.Exception.Message -match 'CA=true') "$caught"
$caught = Get-Caught { Resolve-NodeCertificate (Pin $panelHash) (Presented $panelCertificate $false) 'other.example.com' }
Check 'node code: a pinned certificate not made for the address used is refused' ((Get-SetupExitCode $caught) -eq 6 -and $caught.Exception.Message -match 'is not made for') "$caught"

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

function New-SigningCertificate([string] $subject, $uses = @($CodeSigning)) {
    $rsa = [Security.Cryptography.RSACng]::new(2048)
    try {
        $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new($subject, $rsa, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        if ($null -ne $uses) {
            $oids = New-Object Security.Cryptography.OidCollection
            foreach ($use in $uses) { $null = $oids.Add([Security.Cryptography.Oid]::new($use)) }
            $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($oids, $false))
        }
        $made = $request.CreateSelfSigned([DateTimeOffset]::Now.AddDays(-1), [DateTimeOffset]::Now.AddDays(1))
        return [Security.Cryptography.X509Certificates.X509Certificate2]::new($made.RawData)
    }
    finally { $rsa.Dispose() }
}
# What Get-AuthenticodeSignature gives, for a file nothing here needs to read.
function Signature([string] $status, $certificate, [string] $type = 'Authenticode') {
    [pscustomobject]@{ Status = $status; StatusMessage = "status $status"; SignerCertificate = $certificate; SignatureType = $type }
}
$edb = New-SigningCertificate 'CN=EnterpriseDB Corporation, O=EnterpriseDB Corporation, L=Bedford, S=Massachusetts, C=US'
$microsoft = New-SigningCertificate 'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US'
function Assert-Signature($signature, [string] $organization = $PostgresSigner) { Get-Caught { Assert-InstallerSignature $signature $organization 'The installer' } }
Check "signature: EDB's installer, valid, by EnterpriseDB" ($null -eq (Assert-Signature (Signature 'Valid' $edb))) "$(Assert-Signature (Signature 'Valid' $edb))"
Check "signature: Microsoft's installer, valid, by Microsoft" ($null -eq (Assert-Signature (Signature 'Valid' $microsoft) $DotNetSigner))
Check 'signature: without the SignatureType of newer PowerShell versions, the rest is checked' ($null -eq (Assert-Signature ([pscustomobject]@{ Status = 'Valid'; SignerCertificate = $edb })))
foreach ($case in ([ordered]@{
        'not signed' = Signature 'NotSigned' $null
        'changed after it was signed' = Signature 'HashMismatch' $edb
        'signed by a certificate Windows does not trust' = Signature 'NotTrusted' $edb
        'with a chain that cannot be built' = Signature 'UnknownError' $edb
        'signed through a catalog' = Signature 'Valid' $edb 'Catalog'
        'valid without a signer' = Signature 'Valid' $null
        'signed by Microsoft, for EDB''s' = Signature 'Valid' $microsoft
        'signed by an organization whose name starts alike' = Signature 'Valid' (New-SigningCertificate 'CN=EnterpriseDB Corporation, O=EnterpriseDB Corporation Ltd, C=US')
        'signed by the name in another case' = Signature 'Valid' (New-SigningCertificate 'CN=EnterpriseDB Corporation, O=enterprisedb corporation')
        'signed with the name as common name only' = Signature 'Valid' (New-SigningCertificate 'CN=EnterpriseDB Corporation')
        'signed by two organizations' = Signature 'Valid' (New-SigningCertificate 'CN=x, O=EnterpriseDB Corporation, O=Someone Else')
        'signed with a certificate for web servers' = Signature 'Valid' (New-SigningCertificate 'CN=EnterpriseDB Corporation, O=EnterpriseDB Corporation' @($ServerAuth))
        'signed with a certificate without Extended Key Usage' = Signature 'Valid' (New-SigningCertificate 'CN=EnterpriseDB Corporation, O=EnterpriseDB Corporation' $null)
    }).GetEnumerator()) {
    $caught = Assert-Signature $case.Value
    Check "signature: refuses an installer $($case.Key)" ($null -ne $caught -and (Get-SetupExitCode $caught) -eq 5 -and $caught.Exception.Message -match 'Nothing was changed') "$caught"
}
Check 'signature: an organization with a comma, read from the encoding' ((@(Get-CertificateOrganizations (New-SigningCertificate 'CN=x, O="Pulsar, Inc.", C=US')) -join '|') -ceq 'Pulsar, Inc.')

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

# --- The release: framework-dependent ZIP, self-contained MSI (read from the build scripts, never run) --------------

$buildRelease = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\release\Build-Release.ps1')
$buildMsi = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\release\Build-AgentMsi.ps1')
Check 'release: the ZIP''s panel and agent are framework-dependent, for the newest patch the SDK knows' ($buildRelease -match "\`$zipOptions = \`$common \+ @\('--no-self-contained', '-p:TargetLatestRuntimePatch=true'\)" -and
    $buildRelease -match "dotnet publish \(Join-Path \`$repo 'src\\Pulsar.Api'\) @zipOptions" -and $buildRelease -match "dotnet publish \(Join-Path \`$repo 'src\\Pulsar.Agent'\) @zipOptions")
Check 'release: the MSI''s agent is self-contained' ($buildRelease -match "dotnet publish \(Join-Path \`$repo 'src\\Pulsar.Agent'\) @common --self-contained -o \`$msiAgent" -and $buildRelease -match '-AgentDir \$msiAgent')
Check 'release: the ZIP records the runtimes it was built for (release.json)' ($buildRelease -match "Join-Path \`$staging 'release.json'")
Check 'release: the MSI refuses an agent that is not self-contained' ($buildMsi -match 'includedFrameworks')

if ($script:failures -gt 0) {
    Write-Host "$($script:failures) check(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host 'All checks passed.' -ForegroundColor Green
