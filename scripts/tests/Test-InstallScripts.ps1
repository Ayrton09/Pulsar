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

$shared = @('Get-AccountName', 'Find-UntrustedWriter', 'Find-UntrustedWriters', 'Find-UntrustedAncestors')
$sharedVariables = @('TrustedWriters', 'ModifyRights', 'ReplaceRights')
$installParts = Get-ScriptParts $installScript (@($shared) + @('Read-DerElement', 'Get-CertificateNames', 'Get-ForeignCertificateNames',
        'Test-CertificateNames', 'Get-CertificateAuthority', 'Get-CertificateUses', 'Test-ServerAuthenticationOnly', 'ConvertTo-PrintableText',
        'Assert-TrustableCertificate', 'Get-ListedNames', 'Get-NotedThumbprint', 'Select-TrustToRemove', 'Find-StaleBackupTasks')) `
    (@($sharedVariables) + @('TrustedName', 'ServerAuthentication'))
$backupParts = Get-ScriptParts $backupScript (@($shared) + @('Find-UntrustedDestination', 'New-PrivateFolder', 'Test-PrivateFolder')) $sharedVariables

# What the two scripts share must stay the same in both.
$different = @(@($shared) + @($sharedVariables | ForEach-Object { '$' + $_ }) | Where-Object { $installParts[$_] -cne $backupParts[$_] })
Check 'the functions the install and backup scripts share are the same in both' ($different.Count -eq 0) ($different -join ', ')

foreach ($part in @($installParts.Values) + @($backupParts.Values)) { . ([scriptblock]::Create($part)) }
$TrustedMarker = 'trusted-thumbprint.txt'
$TrustCertificateWithoutBasicConstraints = $false
$TrustCertificateWithOtherNames = $false
$TrustCertificateWithOtherUses = $false
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

$names = Get-CertificateNames (New-TestCertificate @((Dns 'panel.example.com'), (Dns 'PANEL'), (Ip '10.0.0.5'), (Ip '::1')))
Check 'SAN parser: DNS names and IP addresses' (($names.Dns -join ',') -eq 'panel.example.com,PANEL' -and ($names.Ips -join ',') -eq '10.0.0.5,::1' -and $names.Others.Count -eq 0) "$($names.Dns -join ',') | $($names.Ips -join ',')"
$names = Get-CertificateNames (New-TestCertificate @((Dns '*.example.com'), (Email 'admin@example.com')))
Check 'SAN parser: a wildcard is a DNS name; an e-mail address is another kind' (($names.Dns -join ',') -eq '*.example.com' -and ($names.Others -join ',') -eq 'an e-mail address')
foreach ($bad in @("panel.example.com${cr}www.github.com", "panel$esc[2K", 'pa nel', 'a*.example.com', '*', '*.', 'a..b', "caf$([char]0xE9).com", '')) {
    $thrown = Get-Thrown { Get-CertificateNames (New-TestCertificate @((Dns $bad))) }
    $printable = $null -ne $thrown -and $thrown -notmatch '[\x00-\x1F\x7F]'
    Check "SAN parser: refuses the DNS name '$(ConvertTo-PrintableText $bad)', printing it without control characters" ($null -ne $thrown -and $thrown -match 'not one' -and $printable) "$(ConvertTo-PrintableText $thrown)"
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

if ($script:failures -gt 0) {
    Write-Host "$($script:failures) check(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host 'All checks passed.' -ForegroundColor Green
