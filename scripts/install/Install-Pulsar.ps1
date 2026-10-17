<#
.SYNOPSIS
    Installs, updates or removes Pulsar on this Windows machine: the panel, the agent, or both paired together.

.DESCRIPTION
    Run it from the folder of the release ZIP, in an elevated Windows PowerShell (Run as administrator). That folder must
    be one only administrators can change (docs/guides/install.md, step 2): the script refuses to run anywhere else.

    -Mode AllInOne (the default): the panel and an agent on this PC, paired automatically. The panel's database is
    created in the local PostgreSQL (or -ConnectionString names one), its HTTPS certificate is made here (or
    -CertificatePath gives one) and trusted by this machine, the first administrator is created, and this PC is added
    as a node.

    -Mode Panel: the panel only. Nodes are added later from the panel (Nodes > Add node), which shows the command to
    run on each node.

    -Mode Agent: the agent only, with the values the panel's "Add node" dialog shows.

    -Update replaces the programs of whatever is installed and keeps settings and data. -Uninstall removes the
    services and programs, and the trust this script gave the panel's certificate; the database, the panel's keys and
    logs and the game servers' folders are kept.

    An agent installed with the agent MSI is updated and removed with msiexec, never by this script: -Mode Agent and
    -Mode AllInOne refuse to run next to it, and -Update and -Uninstall handle only the panel.

    -Mode Panel -RestoreFrom <folder> installs the panel from a backup made by Backup-Pulsar.ps1 (on this machine or
    another): its database, keys, certificates and settings. Nodes reconnect on their own when the panel's address
    leads to this machine.

    What Pulsar needs and this machine lacks is downloaded over HTTPS and checked before anything is installed: the
    .NET 10 runtimes (Microsoft's installers, against the SHA-512 in Microsoft's release metadata and Microsoft's
    signature) and, for a panel without -ConnectionString or a local PostgreSQL 15 or later, the newest PostgreSQL 18
    (EDB's installer, against EnterpriseDB's signature). A download that cannot be checked is not run, and nothing is
    changed. -DotNetInstallers and -PostgresInstaller take installers from a folder instead (offline machines).

    -Unattended is for the setup wizards and other automation: nothing prompts, secrets come from the environment or
    -SecretsFile (never the command line), progress lines start with "PULSAR-STEP <n>/<total> ", and the exit code
    says how it ended. Every run writes a log, without secrets, to <DataRoot>\Setup\logs.

    Every step is described in docs/guides/install.md.

.EXAMPLE
    .\Install-Pulsar.ps1
    Panel and agent on this PC (asks for the administrator's password, and for the PostgreSQL password when a
    PostgreSQL is installed already).

.EXAMPLE
    .\Install-Pulsar.ps1 -HostName panel.example.com, 203.0.113.7
    The same, with a DNS name and a public IP address the panel is also reached by in its certificate (this PC's
    names and private IPv4 addresses are always there).

.EXAMPLE
    $env:PULSAR_NODE_CODE = '<the code the panel shows>'; .\Install-Pulsar.ps1 -Mode Agent
    Adds this machine as a node with the code from the panel's Add node dialog: the panel's certificate is checked
    against the code and pinned in the agent (not trusted by this machine), without copying it by hand.

.EXAMPLE
    .\Install-Pulsar.ps1 -Mode Agent -PanelUrl https://panel.example.com:8443 -Token <token> -PanelKey <fingerprint> -TrustCertificate .\panel.cer
    Adds this machine as a node of an existing panel whose certificate is self-signed.
#>
[CmdletBinding(DefaultParameterSetName = 'Install')]
param(
    [Parameter(ParameterSetName = 'Install')]
    [ValidateSet('AllInOne', 'Panel', 'Agent')]
    [string] $Mode = 'AllInOne',

    # --- Unattended (the setup wizards, automation) ---
    # Nothing prompts: what would be asked comes from this (elevated) process's environment or -SecretsFile, never
    # from the command line, which other programs can read (docs/guides/install.md, "Unattended installs").
    [switch] $Unattended,
    # A JSON file of secrets ({"adminPassword": "...", ...}) that only SYSTEM and Administrators may own or open, in a
    # folder only they may change; deleted once read. Or set PULSAR_SECRETS_FILE.
    [string] $SecretsFile,
    # EDB's PostgreSQL installer (postgresql-<version>-<build>-windows-x64.exe, PostgreSQL 15 or later) to use instead
    # of downloading the newest PostgreSQL 18; checked the same way.
    [string] $PostgresInstaller,
    # A folder with Microsoft's .NET runtime installers (dotnet-runtime-10.0.<n>-win-x64.exe and
    # aspnetcore-runtime-10.0.<n>-win-x64.exe) to use instead of downloading them; checked the same way.
    [string] $DotNetInstallers,

    # --- Panel ---
    # An existing database (Npgsql connection string; or PULSAR_CONNECTION_STRING, since it holds a password).
    # Without it, a 'pulsar' database and role are created in the local PostgreSQL with -PostgresPassword (the postgres
    # superuser's password), or in a PostgreSQL 18 this script installs when there is none.
    [string] $ConnectionString,
    [SecureString] $PostgresPassword,
    [int] $PostgresPort = 5432,
    # The panel's HTTPS port.
    [ValidateRange(1, 65535)] [int] $Port = 8443,
    # A second HTTPS port for agents only: there they present their node key as a TLS client certificate, and the panel
    # moves the agents it knows there on its own. Browsers keep using -Port. 0 (the default): none.
    [ValidateRange(0, 65535)] [int] $AgentsPort = 0,
    # The port of the panel's SFTP server (users reach their servers' files with an SFTP client). Another port than
    # -Port and -AgentsPort. An administrator can change it later in the panel (Nodes > SFTP).
    [ValidateRange(1, 65535)] [int] $SftpPort = 2022,
    # Extra names or IP addresses for the self-signed certificate (localhost, the machine's name and DNS name, 127.0.0.1,
    # ::1 and the machine's private IPv4 addresses on networks with a router are always in it). A name has letters,
    # digits, '.', '-' and '_' (a leading '*.' for a wildcard), compared as written: ignoring case, the Kelvin sign would
    # pass for a 'k'. An address is IPv4, as four numbers (203.0.113.7), or IPv6 (2001:db8::7), and the certificate
    # lists it as an address: browsers match an address only against those.
    [ValidatePattern('^((\*\.)?[A-Za-z0-9_-]+(\.[A-Za-z0-9_-]+)*\.?|[0-9A-Fa-f.]*:[0-9A-Fa-f:.]*)\z', Options = 'None')]
    [string[]] $HostName = @(),
    # A PFX certificate to use instead of a self-signed one.
    [string] $CertificatePath,
    [SecureString] $CertificatePassword,
    [string] $AdminUserName = 'admin',
    [SecureString] $AdminPassword,
    # A backup folder made by Backup-Pulsar.ps1 (with -Mode Panel).
    [string] $RestoreFrom,
    # Leaves the Windows Firewall alone (by default the panel's ports (HTTPS, agents, SFTP) are opened for the panel's
    # program, on Domain and Private networks).
    [switch] $NoFirewall,
    # Opens the panel's ports on Public networks too.
    [switch] $FirewallPublic,

    # --- Agent ---
    # The code the panel's Add node dialog shows (pulsar-node-1....): the panel's address, an enrollment token, the panel
    # key's fingerprint and the SHA-256 of the panel's certificate, instead of -Token, -PanelKey and -TrustCertificate.
    # The agent pins that certificate (it accepts it and no other); no certificate store of this machine is changed.
    # Prefer PULSAR_NODE_CODE: a command line shows in the process list.
    [string] $NodeCode,
    # The panel's address (with -NodeCode: only when this machine reaches the panel at another address than the code's).
    [string] $PanelUrl,
    # The enrollment token; or leave it out and set PULSAR_ENROLLMENT_TOKEN: a command line shows in the process list.
    [string] $Token,
    [string] $PanelKey,
    # Where game servers are installed (default: C:\PulsarServers).
    [string] $ServersRoot,
    # The panel's certificate (.cer), when it is self-signed: trusted by this machine so the agent accepts it (refused
    # when it can issue certificates, does not name the -PanelUrl host, names others or serves more than a web server's
    # identity; -Uninstall removes the trust).
    [string] $TrustCertificate,
    # Allows -TrustCertificate with a certificate that does not say whether it can issue certificates (no Basic
    # Constraints: made by an earlier installer, or by hand). Windows may take such a certificate for a certificate
    # authority; the default is to refuse it.
    [switch] $TrustCertificateWithoutBasicConstraints,
    # Allows -TrustCertificate with a certificate that also lists names that are not the panel's (anything but the
    # -PanelUrl host, localhost and machine names: a wildcard, another site's name): trusted, it vouches for them too.
    [switch] $TrustCertificateWithOtherNames,
    # Allows -TrustCertificate with a certificate that may be used for more than a web server's identity (an Extended Key
    # Usage beyond server authentication, or none at all, which allows every use).
    [switch] $TrustCertificateWithOtherUses,

    # --- Maintenance ---
    [Parameter(Mandatory, ParameterSetName = 'Update')] [switch] $Update,
    [Parameter(Mandatory, ParameterSetName = 'Uninstall')] [switch] $Uninstall,

    [string] $InstallRoot = (Join-Path $env:ProgramFiles 'Pulsar'),
    [string] $DataRoot = (Join-Path $env:ProgramData 'Pulsar')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3

$PanelService = 'PulsarPanel'
$AgentService = 'PulsarAgent'
$PanelAccount = "NT SERVICE\$PanelService"
$PanelDir = Join-Path $InstallRoot 'Panel'
$AgentDir = Join-Path $InstallRoot 'Agent'
$PanelData = Join-Path $DataRoot 'Panel'
$PanelConfig = Join-Path $PanelDir 'appsettings.Production.json'
# The agent's state directory (its own default: this script never enrolls it with another), and the file there that asks
# a running agent to stop its game servers as it stops (docs/architecture/06, "Server hosts").
$AgentData = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'Pulsar\Agent'
$AgentStopServersMarker = Join-Path $AgentData 'state\stop-servers'
# Certificates this script trusted carry this friendly name in Cert:\LocalMachine\Root: -Uninstall removes those only.
$TrustedName = 'Pulsar panel (trusted by Install-Pulsar.ps1)'
# Where earlier installs noted the certificate they trusted.
$TrustedMarker = Join-Path $PanelData 'tls\trusted-thumbprint.txt'
# The Extended Key Usage of a web server's certificate (id-kp-serverAuth): the only use the panel's certificate needs.
$ServerAuthentication = '1.3.6.1.5.5.7.3.1'
# What marks the 'pulsar' role in PostgreSQL as this script's (COMMENT ON ROLE): a run may drop it when it is all that an
# install which failed left (Get-PanelDatabaseState). Not a proof: a role with CREATEROLE can mark one too, so the mark
# only ever lets a run drop a role with no database (PostgreSQL refuses to drop one that owns anything), never look into
# or drop a database.
$PulsarRoleComment = 'Pulsar panel (made by Install-Pulsar.ps1)'
# Windows' own programs by full path: this runs elevated, and a program found through PATH could be anyone's.
$Icacls = Join-Path ([Environment]::SystemDirectory) 'icacls.exe'
$Sc = Join-Path ([Environment]::SystemDirectory) 'sc.exe'

# What this script downloads, and from where (docs/guides/install.md, "What the installer downloads"). Only over
# HTTPS; nothing it cannot check runs.
# - PostgreSQL: the newest release of this major version (never a newer major version on its own: that needs a data
#   upgrade), from postgresql.org's list of versions, never older than the release's floor (release.json's
#   postgresMinimum); EDB's Windows installer, signed by EnterpriseDB, of that version.
$PostgresMajor = 18
$PostgresVersionsUrl = 'https://www.postgresql.org/versions.json'
$PostgresDownloadsUrl = 'https://get.enterprisedb.com/postgresql'
# Who signs EDB's installers: its code-signing certificate's subject (country, state, locality and organization, each
# exactly), the authority that issues it, by name and by thumbprint (DigiCert Trusted G4 Code Signing RSA4096 SHA384
# 2021 CA1), and the root its chain ends at (DigiCert Trusted Root G4), as read from EDB's signed programs of PostgreSQL
# 18 (docs/guides/install.md says what a release must confirm when EDB changes them).
$PostgresSigner = @{
    Subject = [ordered]@{ C = 'US'; S = 'Delaware'; L = 'Wilmington'; O = 'EnterpriseDB Corporation' }
    Issuer = [ordered]@{ CN = 'DigiCert Trusted G4 Code Signing RSA4096 SHA384 2021 CA1'; O = 'DigiCert, Inc.'; C = 'US' }
    IssuerThumbprints = @('7B0F360B775F76C94A12CA48445AA2D2A875701C')
    Roots = @('DDFB16CD4931C973A2037D3FC83A4D7D775D05E4')
}
# - .NET: the newest runtimes of this channel, from Microsoft's release metadata, which lists each installer's SHA-512;
#   the installers are signed by Microsoft, and only Microsoft's download hosts are taken from the metadata.
$DotNetChannel = '10.0'
$DotNetReleasesUrl = 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
$DotNetHosts = @('builds.dotnet.microsoft.com', 'download.visualstudio.microsoft.com')
# Who signs Microsoft's installers: the subject of its code-signing certificates, which chain up to one of Microsoft's
# code-signing roots (Microsoft Root Certificate Authority 2011, which .NET's installers chain to, and 2010).
$DotNetSigner = @{
    Subject = [ordered]@{ C = 'US'; S = 'Washington'; L = 'Redmond'; O = 'Microsoft Corporation' }
    Issuer = $null
    IssuerThumbprints = @()
    Roots = @('8F43288AD272F3103B6FB1428485EA3014C0BCFE', '3B1EFD3A66EA28B16697394703A72CA340A05BD5')
}
# The shared frameworks Pulsar's programs may use, in the order they are installed, with the installer of each (its
# component in releases.json, its file name there, and the product name its version information gives, followed by
# " - <version> (x64)").
$DotNetFrameworks = [ordered]@{
    'Microsoft.NETCore.App' = @{ Component = 'runtime'; File = 'dotnet-runtime'; Product = 'Microsoft .NET Runtime' }
    'Microsoft.AspNetCore.App' = @{ Component = 'aspnetcore-runtime'; File = 'aspnetcore-runtime'; Product = 'Microsoft ASP.NET Core Runtime' }
}
# The Extended Key Usage of a certificate for signing code (id-kp-codeSigning): an installer's signer must have it.
$CodeSigning = '1.3.6.1.5.5.7.3.3'
# The node code (version 1) the panel's Add node dialog shows, as src/Pulsar.Contracts/NodeCode.cs writes and reads it:
# its prefix, its longest form, the longest panel origin it carries, and what starts a reusable enrollment token.
$NodeCodePrefix = 'pulsar-node-1.'
$NodeCodeMaxLength = 2048
$NodeCodeMaxUrlLength = 512
$ReusableTokenPrefix = 'pulsar_enroll_'
# This script's own folder: its logs and, while it runs, its downloads. SYSTEM and Administrators only.
$SetupRoot = Join-Path $DataRoot 'Setup'
$SetupLogs = Join-Path $SetupRoot 'logs'

# Exit codes with -Unattended (docs/guides/install.md, "Unattended installs"). Without it, a failure is an error, as it
# always was (exit code 1 under powershell -File).
$ExitFailed = 1         # anything else: the message says what failed, and what was left
$ExitUsage = 2          # wrong or missing parameters, secrets or node code: nothing was changed
$ExitUnsupported = 3    # this machine, this folder or this account cannot install Pulsar as they are: nothing was changed
$ExitOffline = 4        # a download, or the panel, could not be reached: nothing was changed
$ExitUntrusted = 5      # a download or a given installer failed its checks (signature, hash, version): nothing was changed
$ExitCertificate = 6    # the panel's certificate is not the node code's, or may not be trusted: nothing was changed
$ExitRestart = 3010     # done; an installer (.NET) asks for Windows to be restarted to finish

# The progress steps of each kind of run: every run prints each of its steps once, in this order, a step it does not
# need too (saying so), so that "<n>/<total>" only moves forward.
$StepPlans = @{
    AllInOne = @('check', 'downloads', 'dotnet', 'postgres', 'panel', 'administrator', 'agent')
    Panel = @('check', 'downloads', 'dotnet', 'postgres', 'panel', 'administrator')
    Agent = @('check', 'downloads', 'dotnet', 'agent')
    Update = @('check', 'downloads', 'dotnet', 'update')
    Uninstall = @('check', 'remove')
}

# The secrets a run may be given outside its command line: their names in -SecretsFile and their environment variables.
$SecretInputs = [ordered]@{
    adminPassword = 'PULSAR_ADMIN_PASSWORD'
    postgresPassword = 'PULSAR_POSTGRES_PASSWORD'
    connectionString = 'PULSAR_CONNECTION_STRING'
    certificatePassword = 'PULSAR_CERTIFICATE_PASSWORD'
    enrollmentToken = 'PULSAR_ENROLLMENT_TOKEN'
    nodeCode = 'PULSAR_NODE_CODE'
}
# A JSON string: no raw control characters, only JSON's escapes.
$JsonStringPattern = '"(?:[^"\\\x00-\x1F]|\\["\\/bfnrt]|\\u[0-9A-Fa-f]{4})*"'

# What this run knows that must never be written out (Register-Secret), its log, its downloads folder, its step plan
# and whether an installer asked for a restart.
$SetupSecrets = New-Object Collections.Generic.List[string]
$SetupLogFile = $null
$SetupDownloads = $null
$StepPlan = @()
$RestartNeeded = $false

function Write-Step([string] $message) { Write-Host "==> $message" -ForegroundColor Cyan }

# --- Log, progress and exit codes ----------------------------------------------------------------------------------

# An error that ends this run with $code (one of the $Exit* codes above).
function New-SetupError([int] $code, [string] $message) {
    $exception = New-Object InvalidOperationException $message
    $exception.Data['PulsarExitCode'] = $code
    return $exception
}

# The exit code an error ($record: what a catch block gets) ends this run with: its own, or $ExitFailed.
function Get-SetupExitCode($record) {
    for ($exception = $record.Exception; $null -ne $exception; $exception = $exception.InnerException) {
        if ($exception.Data.Contains('PulsarExitCode')) { return [int]$exception.Data['PulsarExitCode'] }
    }
    return $ExitFailed
}

# Text that must never be written out (passwords, tokens, the node code, connection strings): the log and the console
# show "[secret]" instead. Very short ones are left alone: replacing every "abc" would garble the log and hide little.
function Register-Secret([string] $value) {
    if ($value -and $value.Length -ge 4 -and -not $SetupSecrets.Contains($value)) { $SetupSecrets.Add($value) }
}

# $text with every registered secret replaced, the longest first (one may contain another).
function Protect-SetupText([string] $text) {
    $result = "$text"
    foreach ($secret in @($SetupSecrets | Sort-Object Length -Descending)) { $result = $result.Replace($secret, '[secret]') }
    return $result
}

# Appends to this run's log, each line with the time, the secrets replaced. Not a transcript: that would record this
# script's command line, where -Token or -NodeCode may be.
function Write-SetupLog([string] $text) {
    if (-not $SetupLogFile) { return }
    $stamp = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff')
    $lines = @((Protect-SetupText $text) -split "`r?`n" | ForEach-Object { "$stamp $_" })
    [IO.File]::AppendAllText($SetupLogFile, ($lines -join "`r`n") + "`r`n", (New-Object Text.UTF8Encoding $false))
}

# A progress line: "PULSAR-STEP <n>/<total> <text>", n being $key's place in $plan (this run's steps).
function Format-SetupStep([string] $key, [string] $text, [string[]] $plan) {
    $index = [Array]::IndexOf($plan, $key)
    if ($index -lt 0) { throw "This run has no step '$key'." }
    return "PULSAR-STEP $($index + 1)/$($plan.Count) $(ConvertTo-PrintableText $text)"
}

# Prints a progress line, as is with -Unattended (the wizards show the text after the prefix), as "[n/total] text"
# otherwise.
function Write-SetupStep([string] $key, [string] $text) {
    $line = Format-SetupStep $key $text $StepPlan
    if ($Unattended) { Write-Host $line }
    else { Write-Host ($line -replace '^PULSAR-STEP (\d+/\d+) ', '[$1] ') -ForegroundColor Cyan }
}

# Shows one record of the main part's output (Write-Host, warnings, errors, objects) as it would have been shown, and
# writes it to the log, both with the secrets replaced.
function Write-SetupRecord($record) {
    if ($record -is [Management.Automation.InformationRecord] -and $record.MessageData -is [Management.Automation.HostInformationMessage]) {
        $message = $record.MessageData
        $text = Protect-SetupText $message.Message
        Write-SetupLog $text
        $options = @{ Object = $text; NoNewline = [bool]$message.NoNewLine }
        if ($null -ne $message.ForegroundColor) { $options.ForegroundColor = $message.ForegroundColor }
        Write-Host @options
        return
    }
    if ($record -is [Management.Automation.WarningRecord]) {
        $text = Protect-SetupText $record.Message
        Write-SetupLog "WARNING: $text"
        Write-Warning $text
        return
    }
    if ($record -is [Management.Automation.VerboseRecord] -or $record -is [Management.Automation.DebugRecord]) {
        Write-SetupLog (Protect-SetupText $record.Message)
        return
    }
    if ($record -is [Management.Automation.ErrorRecord]) { $text = "ERROR: $($record.Exception.Message)" }
    elseif ($record -is [Management.Automation.InformationRecord]) { $text = "$($record.MessageData)" }
    else { $text = ($record | Out-String).TrimEnd() }
    $text = Protect-SetupText $text
    Write-SetupLog $text
    Write-Host $text
}

# The parameters this run was given, fit for the log: secrets are named, never shown.
function Get-ParameterSummary($bound) {
    $parts = foreach ($name in $bound.Keys) {
        $value = $bound[$name]
        if ($name -in 'Token', 'NodeCode', 'ConnectionString' -or $value -is [SecureString]) { "-$name [secret]" }
        elseif ($value -is [Management.Automation.SwitchParameter]) { "-${name}:$([bool]$value)" }
        else { "-$name $(@($value) -join ',')" }
    }
    return ($parts -join ' ')
}

# Makes this script's folder ($SetupRoot, then its logs folder) for SYSTEM and Administrators only, in the call that
# creates it, and checks it. Whoever else could change it could read the log, or replace a download before it runs.
function New-SetupFolder([string] $path) {
    if (Test-Path -LiteralPath $path) { throw (New-SetupError $ExitUnsupported "$path already exists: someone else may have made it. Nothing was changed.") }
    $security = New-Object Security.AccessControl.DirectorySecurity
    $security.SetAccessRuleProtection($true, $false)
    foreach ($sid in 'S-1-5-18', 'S-1-5-32-544') {
        $security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier $sid), 'FullControl', 'ContainerInherit, ObjectInherit', 'None', 'Allow')))
    }
    $null = [IO.Directory]::CreateDirectory($path, $security)
    if (-not (Test-PrivateFolder $path)) {
        throw (New-SetupError $ExitUnsupported "$path could not be made for SYSTEM and Administrators alone (someone made it meanwhile, or the drive keeps no permissions). Nothing was changed.")
    }
}

# Whether a folder is as New-PrivateFolder makes it: not a link, owned by SYSTEM or Administrators, nothing inherited
# and nobody else allowed in. Made by someone else meanwhile, or on a drive that keeps no permissions (FAT, exFAT, a
# share that ignores them), it is not.
function Test-PrivateFolder([string] $path) {
    try {
        $item = Get-Item -LiteralPath $path -Force
        if ($item -isnot [IO.DirectoryInfo] -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return $false }
        $acl = Get-Acl -LiteralPath $path
        $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier])
        if ($null -eq $owner -or $owner.Value -notin 'S-1-5-18', 'S-1-5-32-544') { return $false }
        $allowed = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) | Where-Object { $_.AccessControlType -eq 'Allow' } |
            ForEach-Object { $_.IdentityReference.Value } | Select-Object -Unique)
        return $acl.AreAccessRulesProtected -and $allowed.Count -gt 0 -and @($allowed | Where-Object { $_ -notin 'S-1-5-18', 'S-1-5-32-544' }).Count -eq 0
    }
    catch {
        return $false
    }
}

# Makes this script's folder if needed and starts this run's log there.
function Start-SetupLog {
    if (-not (Test-Path -LiteralPath $DataRoot)) { $null = New-Item -ItemType Directory $DataRoot }
    if (-not (Test-Path -LiteralPath $SetupRoot)) { New-SetupFolder $SetupRoot }
    $findings = @(Find-UntrustedWriters $SetupRoot)
    if ($findings.Count -gt 0 -or -not (Test-PrivateFolder $SetupRoot)) {
        $detail = if ($findings.Count -gt 0) { Get-UntrustedWritersMessage $findings } else { "  it is not for SYSTEM and Administrators alone" }
        throw (New-SetupError $ExitUnsupported "This script keeps its log and its downloads in $SetupRoot, which others could change:$([Environment]::NewLine)$detail$([Environment]::NewLine)Delete that folder (someone else may have made it) and run this script again. Nothing was changed.")
    }
    if (-not (Test-Path -LiteralPath $SetupLogs)) { New-SetupFolder $SetupLogs }
    $script:SetupLogFile = Join-Path $SetupLogs ('install-{0:yyyyMMdd-HHmmss}-{1}.log' -f (Get-Date), $PID)
    Write-SetupLog "Install-Pulsar.ps1 in $PSScriptRoot, on $([Environment]::OSVersion.VersionString), PowerShell $($PSVersionTable.PSVersion): $(Get-ParameterSummary $SetupParameters)"
    if ($Unattended) { Write-Host "PULSAR-LOG $SetupLogFile" }
}

function Assert-Administrator {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw (New-SetupError $ExitUnsupported 'Run this script in an elevated PowerShell (right-click > Run as administrator).')
    }
    if (-not [Environment]::Is64BitOperatingSystem) { throw (New-SetupError $ExitUnsupported 'Pulsar needs 64-bit Windows.') }
    # A 32-bit program (a 32-bit setup, say) that starts powershell.exe gets the 32-bit one, which sees another Program
    # Files and another registry.
    if (-not [Environment]::Is64BitProcess) {
        throw (New-SetupError $ExitUnsupported "Run this script in 64-bit Windows PowerShell ($([Environment]::GetFolderPath('Windows'))\System32\WindowsPowerShell\v1.0\powershell.exe; a 32-bit program reaches it as Sysnative\WindowsPowerShell\v1.0\powershell.exe).")
    }
}

# --- Where this script runs from ------------------------------------------------------------------------------------

# This script runs elevated: it runs the agent next to it and copies the panel and the agent into Program Files, where
# they run as services. Whoever else could change them, or this script, before it runs (a game server's account, say:
# its token keeps Users and Authenticated Users, which may modify a folder made directly under C:\) would get SYSTEM.
# The rule the agent applies before it runs as a service (src/Pulsar.Agent.Windows/ServiceBinarySecurity.cs): only
# SYSTEM, Administrators and TrustedInstaller may own or modify what is used and the folder it is in, or rename or
# replace a folder above it. Links are refused: the check would look at something other than what is used.
# Backup-Pulsar.ps1 has the same functions. The check lives in the script it protects: it stops a release extracted
# where others can write, or changed in part (a program), but not someone who already rewrote this script. The control
# is the folder itself, made where only administrators can change it (docs/guides/install.md, step 2).
$TrustedWriters = @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
# Write data and append data (in a folder: create files and folders), write extended attributes, delete children, write
# attributes, delete, change permissions, take ownership, and the generic rights (all, write) that include them.
$ModifyRights = 0x2 -bor 0x4 -bor 0x10 -bor 0x40 -bor 0x100 -bor 0x10000 -bor 0x40000 -bor 0x80000 -bor 0x10000000 -bor 0x40000000
# On a folder above: what lets someone rename or replace it (delete children, delete, change permissions, take
# ownership, generic all). A drive's root cannot be renamed or deleted, so "delete" is harmless there.
$ReplaceRights = 0x40 -bor 0x10000 -bor 0x40000 -bor 0x80000 -bor 0x10000000

function Get-AccountName([Security.Principal.SecurityIdentifier] $sid) {
    try { return $sid.Translate([Security.Principal.NTAccount]).Value } catch { return $sid.Value }
}

# What lets anyone but the trusted accounts change one file or folder with these rights; nothing when nobody can.
function Find-UntrustedWriter([IO.FileSystemInfo] $item, [int] $rights) {
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return "$($item.FullName) is a link" }
    $acl = Get-Acl -LiteralPath $item.FullName
    # The owner can always change the permissions.
    $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier])
    if ($owner.Value -notin $TrustedWriters) { "$($item.FullName) is owned by $(Get-AccountName $owner)" }
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -ne 'Allow' -or $rule.IdentityReference.Value -in $TrustedWriters) { continue }
        # Inherit-only entries (CREATOR OWNER, say) apply to what is created inside it later, by whoever may create there,
        # which this check already looks at; inherited entries count like any other.
        if (([int]$rule.PropagationFlags -band [int][Security.AccessControl.PropagationFlags]::InheritOnly) -ne 0) { continue }
        if (([int]$rule.FileSystemRights -band $rights) -ne 0) { "$(Get-AccountName $rule.IdentityReference) can change $($item.FullName)" }
    }
}

# Who besides SYSTEM, Administrators and TrustedInstaller could change $path (a folder: everything in it), or rename or
# replace the folders above it.
function Find-UntrustedWriters([string] $path) {
    $item = Get-Item -LiteralPath $path -Force
    $folder = $item
    if ($item -is [IO.FileInfo]) {
        Find-UntrustedWriter $item $ModifyRights
        $folder = $item.Directory
        Find-UntrustedWriter $folder $ModifyRights
    }
    else {
        # Not through links (refused above).
        $pending = New-Object Collections.Generic.Stack[IO.FileSystemInfo]
        $pending.Push($item)
        while ($pending.Count -gt 0) {
            $current = $pending.Pop()
            Find-UntrustedWriter $current $ModifyRights
            if ($current -is [IO.DirectoryInfo] -and ($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
                foreach ($child in $current.GetFileSystemInfos()) { $pending.Push($child) }
            }
        }
    }
    Find-UntrustedAncestors $folder
}

# Who besides SYSTEM, Administrators and TrustedInstaller could rename or replace the folders above $folder.
function Find-UntrustedAncestors([IO.DirectoryInfo] $folder) {
    for ($ancestor = $folder.Parent; $null -ne $ancestor; $ancestor = $ancestor.Parent) {
        $rights = $ReplaceRights
        if ($null -eq $ancestor.Parent) { $rights = $ReplaceRights -band (-bnot 0x10000) }
        Find-UntrustedWriter $ancestor $rights
    }
}

function Get-UntrustedWritersMessage([string[]] $findings) {
    $shown = @($findings | Select-Object -First 10)
    $more = ''
    if ($findings.Count -gt $shown.Count) { $more = "$([Environment]::NewLine)  ... and $($findings.Count - $shown.Count) more" }
    return "  $($shown -join "$([Environment]::NewLine)  ")$more"
}

function Assert-TrustedLocation {
    $findings = @(Find-UntrustedWriters $PSScriptRoot)
    if ($findings.Count -gt 0) {
        throw (New-SetupError $ExitUnsupported "This script runs as administrator and installs the programs next to it to run as services, so it refuses to run from a folder that other accounts could change (a game server's account could replace a program, or this script, and take over the machine). Here, in ${PSScriptRoot}:$([Environment]::NewLine)$(Get-UntrustedWritersMessage $findings)$([Environment]::NewLine)Extract the release ZIP into a new folder in $env:ProgramFiles, which only administrators can change, as docs/guides/install.md shows (step 2), and run this script from there. Nothing was changed.")
    }
}

# Pulsar's programs (.NET 10) refuse to start on a Windows that lacks updates: Windows Server 2022 as installed from its
# original image stops them with "Your Windows doesn't fully support CET". Checked before Pulsar is installed (once
# the .NET runtimes the programs need are, which then stay).
function Assert-RuntimeSupported {
    $result = Invoke-Native (Join-Path $PSScriptRoot 'agent\PulsarAgent.exe') help
    if ($result.ExitCode -eq 0) { return }
    if ($result.Output -match 'CET') {
        throw (New-SetupError $ExitUnsupported "This Windows lacks updates that Pulsar needs ($($result.Output.Trim() -replace '\s+', ' ')). Install all available Windows updates (Settings > Windows Update, or sconfig on Server Core), restart, and run this script again. Pulsar was not installed.")
    }
    throw (New-SetupError $ExitUnsupported "Pulsar's programs do not start on this Windows (exit code $($result.ExitCode)):$([Environment]::NewLine)$($result.Output)")
}

function Test-Service([string] $name) { return $null -ne (Get-Service -Name $name -ErrorAction SilentlyContinue) }

# The agent MSI (pulsar-agent-<version>-windows-x64.msi) installs the same program, in the same folder, as the same
# service. Whichever installed the agent maintains it: this script must not replace or remove what Windows Installer
# tracks (a later MSI upgrade or uninstall would then fail or remove the wrong files).
function Get-AgentPackage {
    $products = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
        Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -eq 'Pulsar Agent' -and $_.PSObject.Properties['WindowsInstaller'] -and $_.WindowsInstaller -eq 1 }
    return $products | Select-Object -First 1
}

function Get-AgentPackageMessage($package) {
    return "The agent on this machine was installed with the agent MSI (Pulsar Agent $($package.DisplayVersion)), which maintains it: update it with a newer package (msiexec /i pulsar-agent-<version>-windows-x64.msi /qn), and remove it from Apps in Windows settings or with msiexec /x $($package.PSChildName) /qn."
}

function Invoke-Native([string] $exe) {
    # Windows PowerShell turns a native program's stderr into errors under ErrorActionPreference=Stop.
    $ErrorActionPreference = 'Continue'
    $output = (& $exe @args 2>&1 | ForEach-Object { "$_" }) -join [Environment]::NewLine
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
}

# Invoke-Native, with $stdin on the program's standard input. The program's options must not abbreviate this function's
# parameter names (PowerShell would bind -s to -stdin).
function Invoke-NativeWithInput([string] $stdin, [string] $exe) {
    $ErrorActionPreference = 'Continue'
    $output = ($stdin | & $exe @args 2>&1 | ForEach-Object { "$_" }) -join [Environment]::NewLine
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
}

# A read-only SecureString holding $text, built character by character: ConvertTo-SecureString -AsPlainText takes the
# secret as a cmdlet's parameter, which PowerShell's module logging writes to the event log (this script's functions
# belong to no module, and method calls are not logged).
function ConvertTo-Secure([string] $text) {
    $secure = New-Object Security.SecureString
    foreach ($character in $text.ToCharArray()) { $secure.AppendChar($character) }
    $secure.MakeReadOnly()
    return $secure
}

function ConvertFrom-Secure([SecureString] $secure) {
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}

# Letters and digits only: safe in connection strings, JSON and command lines without quoting.
function New-Secret([int] $length = 32) {
    $alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789'
    $bytes = New-Object byte[] ($length * 2)
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $random.GetBytes($bytes) } finally { $random.Dispose() }
    $chars = foreach ($b in $bytes) { if ($b -lt (256 - (256 % $alphabet.Length))) { $alphabet[$b % $alphabet.Length] } }
    $text = -join $chars
    if ($text.Length -lt $length) { return New-Secret $length }
    return $text.Substring(0, $length)
}

# Backup-Pulsar.ps1 next to the panel's program, where only administrators can change it: a nightly backup task runs it
# as SYSTEM (docs/guides/backup.md). It goes with the panel when the panel is removed.
function Copy-BackupScript {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Backup-Pulsar.ps1') -Destination $PanelDir -Force
}

# Scheduled tasks ($tasks, as Get-ScheduledTask gives them) that run a Backup-Pulsar.ps1 other than $expected, the copy
# in the panel's folder: earlier guides had the nightly task run the one in the extracted release, which other accounts
# could change, and the task runs it as SYSTEM. One line for each, fit to print.
function Find-StaleBackupTasks([object[]] $tasks, [string] $expected) {
    foreach ($task in $tasks) {
        foreach ($action in @($task.Actions)) {
            if ($null -eq $action -or -not $action.PSObject.Properties['Execute']) { continue }
            $line = "$($action.Execute) $(if ($action.PSObject.Properties['Arguments']) { $action.Arguments })"
            foreach ($match in [regex]::Matches($line, '"([^"]*Backup-Pulsar\.ps1)"|([^\s"]*Backup-Pulsar\.ps1)', 'IgnoreCase')) {
                $scriptPath = if ($match.Groups[1].Success) { $match.Groups[1].Value } else { $match.Groups[2].Value }
                $full = $null
                try {
                    $folder = ''
                    if ($action.PSObject.Properties['WorkingDirectory'] -and $action.WorkingDirectory) { $folder = $action.WorkingDirectory }
                    $full = [IO.Path]::GetFullPath([IO.Path]::Combine($folder, $scriptPath))
                }
                catch { $full = $null }
                if ($null -eq $full -or -not [string]::Equals($full, $expected, [StringComparison]::OrdinalIgnoreCase)) {
                    ConvertTo-PrintableText "$($task.TaskPath)$($task.TaskName) runs $scriptPath"
                }
            }
        }
    }
}

# On an update: says which scheduled tasks still run Backup-Pulsar.ps1 from elsewhere than the panel's folder.
function Write-StaleBackupTasks {
    try { $tasks = @(Get-ScheduledTask -ErrorAction Stop) } catch { return }
    $stale = @(Find-StaleBackupTasks $tasks (Join-Path $PanelDir 'Backup-Pulsar.ps1'))
    if ($stale.Count -gt 0) {
        Write-Warning "A scheduled task runs Backup-Pulsar.ps1 from elsewhere than the panel's folder, where other accounts may have changed it, and it runs as SYSTEM: point it at $(Join-Path $PanelDir 'Backup-Pulsar.ps1') and delete the old copy's folder (docs/guides/backup.md).$([Environment]::NewLine)  $($stale -join "$([Environment]::NewLine)  ")"
    }
}

# Puts a release folder's program in place of an installed one, keeping the local settings file and the panel's
# downloads folder (PulsarAgentSetup.exe, which the panel serves to new nodes; a release that has one replaces it).
# What else was there goes first: a program from a self-contained release left its own copy of the .NET runtime there,
# which the new framework-dependent program would load instead of the shared runtime that Microsoft Update patches.
function Copy-Program([string] $source, [string] $target) {
    if (-not (Test-Path (Join-Path $source '*'))) { throw "$source is missing: run this script from the extracted release ZIP." }
    if (Test-Path -LiteralPath $target) {
        Get-ChildItem -LiteralPath $target -Force | Where-Object { $_.Name -notin 'appsettings.Production.json', 'downloads' } | Remove-Item -Recurse -Force
    }
    New-Item -ItemType Directory -Force $target | Out-Null
    Get-ChildItem $source | Where-Object Name -ne 'appsettings.Production.json' | Copy-Item -Destination $target -Recurse -Force
}

# SYSTEM and Administrators full control, plus the given accounts; nothing inherited.
function Set-PrivateAcl([string] $path, [string[]] $modify = @(), [string[]] $read = @()) {
    $arguments = @($path, '/inheritance:r', '/grant:r', '*S-1-5-18:(OI)(CI)F', '/grant:r', '*S-1-5-32-544:(OI)(CI)F')
    foreach ($account in $modify) { $arguments += @('/grant:r', "${account}:(OI)(CI)M") }
    foreach ($account in $read) { $arguments += @('/grant:r', "${account}:(OI)(CI)RX") }
    if ((Get-Item $path) -isnot [IO.DirectoryInfo]) { $arguments = $arguments -replace '\(OI\)\(CI\)', '' }
    $result = Invoke-Native $Icacls @arguments
    if ($result.ExitCode -ne 0) { throw "Could not set the permissions of ${path}: $($result.Output)" }
}

# HTTPS calls to the panel with its certificate pinned by thumbprint (it may be self-signed and not trusted yet).
function Initialize-PanelClient {
    if ('PulsarInstallerClient' -as [type]) { return }
    $source = @'
using System;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;

public sealed class PulsarInstallerClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly CookieContainer _cookies = new CookieContainer();
    private readonly Uri _base;
    private readonly string _thumbprint;

    public PulsarInstallerClient(string baseUrl, string thumbprint)
    {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        _base = new Uri(baseUrl);
        _thumbprint = thumbprint;
        var handler = new HttpClientHandler();
        handler.CookieContainer = _cookies;
        handler.ServerCertificateCustomValidationCallback = Validate;
        _http = new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public int Status;
    public string Body;

    private bool Validate(HttpRequestMessage message, X509Certificate2 certificate, X509Chain chain, SslPolicyErrors errors)
    {
        return certificate != null && string.Equals(certificate.GetCertHashString(), _thumbprint, StringComparison.OrdinalIgnoreCase);
    }

    public bool Send(string method, string path, string json)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), new Uri(_base, path));
        foreach (Cookie cookie in _cookies.GetCookies(_base))
        {
            if (cookie.Name == "__Host-pulsar-xsrf" || cookie.Name == "pulsar-xsrf")
            {
                request.Headers.Add("X-XSRF-TOKEN", Uri.UnescapeDataString(cookie.Value));
            }
        }
        if (!string.IsNullOrEmpty(json))
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        try
        {
            var response = _http.SendAsync(request).GetAwaiter().GetResult();
            Status = (int)response.StatusCode;
            Body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            Status = 0;
            Body = e.GetBaseException().Message;
        }
        return Status >= 200 && Status < 300;
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
'@
    if ($PSVersionTable.PSEdition -eq 'Desktop') {
        Add-Type -TypeDefinition $source -ReferencedAssemblies System.Net.Http
    }
    else {
        Add-Type -TypeDefinition $source
    }
}

function Invoke-Panel($client, [string] $method, [string] $path, $body = $null) {
    $json = $null
    if ($null -ne $body) { $json = $body | ConvertTo-Json -Depth 5 -Compress }
    if (-not $client.Send($method, $path, $json)) {
        throw "The panel answered $method $path with $($client.Status): $($client.Body)"
    }
    if ($client.Body) { return $client.Body | ConvertFrom-Json }
    return $null
}

# Applies the database migrations in this console, before the service starts: a database problem shows here, and the
# service starts quickly (Windows gives a service 30 seconds to report that it runs).
function Invoke-PanelMigrations {
    Write-Step 'Preparing the database'
    $result = Invoke-Native (Join-Path $PanelDir 'Pulsar.Api.exe') migrate --contentRoot $PanelDir
    if ($result.ExitCode -ne 0) { throw "The panel could not prepare its database:$([Environment]::NewLine)$($result.Output)" }
}

function Start-Panel {
    try {
        Start-Service $PanelService
    }
    catch {
        $log = Get-ChildItem (Join-Path $PanelData 'logs') -Filter 'panel-*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
        $tail = ''
        if ($log) { $tail = (Get-Content $log.FullName -Tail 20) -join [Environment]::NewLine }
        throw "The panel service did not start ($($_.Exception.Message)). The end of its log ($($log.FullName)):$([Environment]::NewLine)$tail"
    }
}

function Wait-PanelReady($client) {
    $deadline = (Get-Date).AddMinutes(3)
    while ((Get-Date) -lt $deadline) {
        if ($client.Send('GET', '/health/ready', $null)) { return }
        if ((Get-Service $PanelService).Status -ne 'Running') {
            throw "The panel service stopped. Its log is in $(Join-Path $PanelData 'logs')."
        }
        Start-Sleep -Seconds 2
    }
    throw "The panel did not become ready in 3 minutes ($($client.Status) $($client.Body)). Its log is in $(Join-Path $PanelData 'logs')."
}

# --- Database ---------------------------------------------------------------------------------------------------

# The newest PostgreSQL service on this machine (there may be several versions), or $null when there is none. Its psql
# runs as administrator here, so a PostgreSQL whose programs others could change is refused.
function Get-LocalPostgres {
    $found = foreach ($service in Get-CimInstance Win32_Service -Filter "Name LIKE 'postgresql%'") {
        if ($service.PathName -match '^"?([^"]+\\bin)\\pg_ctl\.exe') {
            $bin = $Matches[1]
            $psql = Join-Path $bin 'psql.exe'
            if (-not (Test-Path -LiteralPath $psql)) { continue }
            $findings = @(Find-UntrustedWriters $psql)
            if ($findings.Count -gt 0) {
                throw (New-SetupError $ExitUnsupported "This script runs PostgreSQL's psql.exe as administrator, and the one of the service $($service.Name) could be changed by other accounts:$([Environment]::NewLine)$(Get-UntrustedWritersMessage $findings)$([Environment]::NewLine)Install PostgreSQL in a folder only administrators can change (its installer's default, in Program Files), or pass -ConnectionString. Nothing was changed.")
            }
            $version = (Invoke-Native $psql --version).Output
            if ($version -match '(\d+)\.\d+') { [pscustomobject]@{ Service = $service; Bin = $bin; Major = [int]$Matches[1] } }
        }
    }
    return $found | Sort-Object Major -Descending | Select-Object -First 1
}

# The newest PostgreSQL service on this machine (there may be several versions), started if it is not running.
function Find-PostgresBin {
    $newest = Get-LocalPostgres
    if (-not $newest) {
        throw 'No local PostgreSQL service was found. Install PostgreSQL (docs/guides/install.md, step 1) or pass -ConnectionString.'
    }
    if ($newest.Major -lt 15) {
        throw "Pulsar needs PostgreSQL 15 or later; this machine has PostgreSQL $($newest.Major). Install a newer one (docs/guides/install.md, step 1) or pass -ConnectionString."
    }
    if ($newest.Service.State -ne 'Running') {
        Write-Step "Starting PostgreSQL $($newest.Major) ($($newest.Service.Name))"
        Start-Service $newest.Service.Name
    }
    return $newest.Bin
}

# psql in $bin as the postgres superuser over localhost, its password in its environment (never its command line);
# $stdin, when given, is the SQL instead of $arguments (a statement with a secret in it goes there). Its exit code and
# output (unaligned, without headers). Always in the 'postgres' database: a database's owner sets what sessions in it
# run with (search_path, and its own functions), so the superuser never connects to one a role of Pulsar's owns. Even
# in 'postgres', a cluster upgraded from PostgreSQL 14 or older may still let every role create in its public schema:
# the session searches pg_catalog alone (pg_temp last), so no function or operator a role planted there runs as the
# superuser. -X: no psqlrc (a startup file can run shell commands); PostgreSQL's own variables were cleared at the
# start (Clear-PostgresEnvironment).
function Invoke-Psql([string] $bin, [string] $password, [string[]] $arguments, [string] $stdin = $null) {
    $psql = Join-Path $bin 'psql.exe'
    $common = @('-X', '-h', 'localhost', '-p', $PostgresPort, '-U', 'postgres', '-d', 'postgres', '-v', 'ON_ERROR_STOP=1', '-q', '-t', '-A')
    $env:PGPASSWORD = $password
    # Startup options win over the role's and the database's own settings.
    $env:PGOPTIONS = '-c search_path=pg_catalog,pg_temp'
    try {
        if ($stdin) { return Invoke-NativeWithInput $stdin $psql @common -f - }
        return Invoke-Native $psql @common @arguments
    }
    finally {
        Remove-Item Env:PGPASSWORD, Env:PGOPTIONS -ErrorAction SilentlyContinue
    }
}

# Whether the postgres password this run was given opens the local PostgreSQL: checked before anything is downloaded.
function Assert-PostgresLogin {
    $bin = Find-PostgresBin
    $result = Invoke-Psql $bin (ConvertFrom-Secure $PostgresPassword) @('-c', 'SELECT 1')
    if ($result.ExitCode -ne 0) {
        throw (New-SetupError $ExitUsage "Could not connect to the local PostgreSQL as 'postgres' with the password given: $(ConvertTo-PrintableText $result.Output.Trim()) Nothing was changed.")
    }
}

# What the panel's database needs on this machine: $null (-ConnectionString names one), the newest local PostgreSQL
# (15 or later, with its postgres password, asked for unless given), or a PostgreSQL to install: the newest
# $PostgresMajor (or what -PostgresInstaller holds), as the service postgresql-x64-<major> in Program Files, on
# -PostgresPort. Whatever would stop that install (the service, its folder or the port taken) stops this run now.
function Get-PostgresPlan {
    if ($ConnectionString) { return $null }
    $local = Get-LocalPostgres
    if ($local -and $local.Major -ge 15) {
        if ($PostgresInstaller) { Write-Warning "PostgreSQL $($local.Major) is installed here already: Pulsar's database is made in it, and -PostgresInstaller is not used." }
        if (-not $PostgresPassword) {
            $script:PostgresPassword = Read-Secret "Password of PostgreSQL's 'postgres' user (chosen when PostgreSQL $($local.Major) was installed)" 'postgresPassword'
        }
        Assert-PostgresLogin
        # A 'pulsar' role or database this run could not replace (a panel's data, someone else's) stops it now.
        $state = Get-PanelDatabaseState (Find-PostgresBin) (ConvertFrom-Secure $PostgresPassword)
        if ($state -cnotin 'none', 'leftover') { throw (New-SetupError $ExitUsage "$(Get-PanelDatabaseMessage $state) Nothing was changed.") }
        return [pscustomobject]@{ Install = $false; Major = $local.Major }
    }
    $major = $PostgresMajor
    if ($PostgresInstaller) {
        $named = Test-PostgresInstallerName (Split-Path -Leaf $PostgresInstaller)
        if (-not $named -or $named.Major -lt 15) {
            throw (New-SetupError $ExitUsage "-PostgresInstaller must be EDB's installer of PostgreSQL 15 or later, named as EDB names it (postgresql-$PostgresMajor.<minor>-<build>-windows-x64.exe), not $(Split-Path -Leaf $PostgresInstaller).")
        }
        if (-not (Test-Path -LiteralPath $PostgresInstaller -PathType Leaf)) { throw (New-SetupError $ExitUsage "$PostgresInstaller was not found.") }
        $major = $named.Major
    }
    $service = "postgresql-x64-$major"
    $prefix = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) "PostgreSQL\$major"
    $older = ''
    if ($local) { $older = " (PostgreSQL $($local.Major), the one installed here, is too old for Pulsar)" }
    if (Test-Service $service) {
        throw (New-SetupError $ExitUnsupported "PostgreSQL $major would be installed as the service $service, which exists already but is not a PostgreSQL this script can use$older. Remove it, or pass -ConnectionString. Nothing was changed.")
    }
    if (Test-Path -LiteralPath $prefix) {
        throw (New-SetupError $ExitUnsupported "PostgreSQL $major would be installed in $prefix, which exists already (left by an earlier PostgreSQL?)$older. Remove it, or pass -ConnectionString. Nothing was changed.")
    }
    if (@(Get-NetTCPConnection -LocalPort $PostgresPort -State Listen -ErrorAction SilentlyContinue).Count -gt 0) {
        throw (New-SetupError $ExitUnsupported "PostgreSQL $major would listen on port $PostgresPort, which another program uses$older. Pass another port with -PostgresPort, or pass -ConnectionString. Nothing was changed.")
    }
    return [pscustomobject]@{ Install = $true; Major = $major; Port = $PostgresPort; Service = $service; Prefix = $prefix; Data = (Join-Path $prefix 'data') }
}

# The PostgreSQL version EDB's installer file name gives ("postgresql-18.6-2-windows-x64.exe": major 18, minor 6,
# build 2), or $null for another name.
function Test-PostgresInstallerName([string] $name) {
    if ($name -cnotmatch '^postgresql-(\d{2,3})\.(\d{1,4})-(\d{1,3})-windows-x64\.exe\z') { return $null }
    return [pscustomobject]@{ Major = [int]$Matches[1]; Minor = [int]$Matches[2]; Build = [int]$Matches[3] }
}

# The newest release of PostgreSQL $major in postgresql.org's list of versions ($versions: versions.json read with
# ConvertFrom-JsonDocument, a list of {"major": "18", "latestMinor": "6", "supported": true, ...}), as "18.6". Only
# that major version: a newer one needs a data upgrade, never done on its own.
function Select-PostgresVersion($versions, [int] $major) {
    if ($versions -isnot [Collections.IList]) { throw (New-SetupError $ExitUntrusted "postgresql.org's list of versions is not a list. Nothing was changed.") }
    $matching = @($versions | Where-Object { $_ -is [Collections.IDictionary] -and (Get-JsonValue $_ 'major') -is [string] -and (Get-JsonValue $_ 'major') -ceq "$major" })
    if ($matching.Count -ne 1) { throw (New-SetupError $ExitUntrusted "postgresql.org's list of versions has $($matching.Count) entries for PostgreSQL $major, not one. Nothing was changed.") }
    $minor = Get-JsonValue $matching[0] 'latestMinor'
    if ($minor -isnot [string] -or $minor -cnotmatch '^\d{1,4}\z') { throw (New-SetupError $ExitUntrusted "postgresql.org's list of versions gives no release of PostgreSQL $major. Nothing was changed.") }
    if ((Get-JsonValue $matching[0] 'supported') -ne $true) { Write-Warning "postgresql.org no longer supports PostgreSQL ${major}: plan an upgrade to a newer major version." }
    return "$major.$([int]$minor)"
}

# The address of EDB's Windows installer of a PostgreSQL release and build ("18.6", 2).
function Get-PostgresInstallerUrl([string] $version, [int] $build) {
    return "$PostgresDownloadsUrl/postgresql-$version-$build-windows-x64.exe"
}

# The HTTP status a HEAD request for $url ends with, after its redirects.
function Get-DownloadStatus([string] $url) {
    return [PulsarDownloads]::Probe($url)
}

# The newest build of EDB's installer of $version (EDB numbers its rebuilds of a release: -1, -2...): the highest that
# exists, looking until three in a row do not (EDB's server answers 403 or 404 for those). Any other answer (too many
# requests, a server error) is a server that could not say, not a build that is missing: the run stops, as offline.
function Find-PostgresInstallerUrl([string] $version) {
    $found = $null
    $misses = 0
    for ($build = 1; $build -le 20 -and $misses -lt 3; $build++) {
        $url = Get-PostgresInstallerUrl $version $build
        $status = 0
        try { $status = Get-DownloadStatus $url }
        catch { throw (New-SetupError $ExitOffline "EDB's download server could not be reached ($($_.Exception.GetBaseException().Message)).") }
        if ($status -eq 200) { $found = $url; $misses = 0 }
        elseif ($status -in 403, 404) { $misses++ }
        else { throw (New-SetupError $ExitOffline "EDB's download server answered $status for $url, so the newest build of PostgreSQL $version cannot be told. Try again later.") }
    }
    if (-not $found) { throw (New-SetupError $ExitOffline "EDB offers no Windows installer of PostgreSQL $version at $PostgresDownloadsUrl.") }
    return $found
}

# EDB's installer of the PostgreSQL to install, in the private downloads folder and checked: the one -PostgresInstaller
# names (copied), or the newest build of the newest release of the plan's major version, downloaded. Either must be
# signed by EnterpriseDB.
function Get-PostgresInstallerFile($plan) {
    $minimum = Read-PostgresMinimum (Get-ReleaseText)
    if ($PostgresInstaller) {
        $named = Test-PostgresInstallerName (Split-Path -Leaf $PostgresInstaller)
        $version = "$($named.Major).$($named.Minor)"
        $what = "The PostgreSQL installer $PostgresInstaller"
        Assert-PostgresMinimum $version $minimum $what
        $file = Copy-SetupInstaller $PostgresInstaller
    }
    else {
        try {
            Write-Step "Looking up the newest PostgreSQL $($plan.Major) ($PostgresVersionsUrl)"
            $version = Select-PostgresVersion (ConvertFrom-JsonDocument (Get-SetupText $PostgresVersionsUrl 1MB)) $plan.Major
            Assert-PostgresMinimum $version $minimum "postgresql.org's newest release"
            $url = Find-PostgresInstallerUrl $version
            Write-Step "Downloading PostgreSQL $version from EDB ($url)"
            $file = Save-SetupDownload $url (Split-Path -Leaf ([Uri]$url).AbsolutePath) 1GB
        }
        catch {
            $code = Get-SetupExitCode $_
            if ($code -ne $ExitOffline) { throw }
            throw (New-SetupError $ExitOffline "PostgreSQL could not be downloaded ($($_.Exception.Message)) and this machine has no PostgreSQL 15 or later for Pulsar's database. Connect it to the internet and run this script again, or pass EDB's installer with -PostgresInstaller, or install PostgreSQL yourself, or use a database elsewhere with -ConnectionString. Nothing was changed.")
        }
        $what = "The PostgreSQL installer downloaded from $url"
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $file
    $chain = $null
    if ($signature.SignerCertificate) { $chain = Get-SignerChain $file $signature.SignerCertificate }
    Assert-InstallerSignature $signature $PostgresSigner $what $chain
    # Signed for the release it was chosen for, not an older one of EDB's under the expected name.
    Assert-InstallerVersion (Get-Item -LiteralPath $file).VersionInfo 'PostgreSQL' $version $what -contains
    return $file
}

# The pg_hba.conf rules that let in more than this machine ($rows: "type|address|netmask|error", one per rule, as psql
# prints pg_hba_file_rules), each fit to print: a rule with an error, or a host rule for anything but a loopback
# address (127.0.0.0/8 or ::1). Nothing when every rule is local.
function Find-NonLocalHbaRules([string[]] $rows) {
    foreach ($row in $rows) {
        if (-not $row.Trim()) { continue }
        $parts = $row.Split('|')
        if ($parts.Count -ne 4) { "a rule this script cannot read ($(ConvertTo-PrintableText $row))"; continue }
        $type, $address, $netmask, $problem = $parts
        if ($problem) { "a rule PostgreSQL cannot use ($(ConvertTo-PrintableText $problem))"; continue }
        if ($type -ceq 'local') { continue }
        $ip = $null
        $mask = $null
        if ([Net.IPAddress]::TryParse($address, [ref] $ip) -and [Net.IPAddress]::TryParse($netmask, [ref] $mask)) {
            $bytes = $ip.GetAddressBytes()
            $maskBytes = $mask.GetAddressBytes()
            if ($bytes.Length -eq 4 -and $maskBytes.Length -eq 4 -and $bytes[0] -eq 127 -and $maskBytes[0] -eq 255) { continue }
            if ($bytes.Length -eq 16 -and $ip.Equals([Net.IPAddress]::IPv6Loopback) -and @($maskBytes | Where-Object { $_ -ne 255 }).Count -eq 0) { continue }
        }
        ConvertTo-PrintableText "$type $address $netmask"
    }
}

# Waits for a PostgreSQL service to run (an installer or a restart may leave it starting), and for it to take
# connections with $password.
function Wait-PostgresReady([string] $service, [string] $bin, [string] $password) {
    $deadline = (Get-Date).AddMinutes(2)
    $last = $null
    while ((Get-Date) -lt $deadline) {
        $state = (Get-Service -Name $service -ErrorAction SilentlyContinue).Status
        if ($state -eq 'Stopped') { Start-Service -Name $service -ErrorAction SilentlyContinue }
        if ($state -eq 'Running') {
            $last = Invoke-Psql $bin $password @('-c', 'SELECT 1')
            if ($last.ExitCode -eq 0) { return }
        }
        Start-Sleep -Seconds 2
    }
    $detail = if ($last) { ConvertTo-PrintableText $last.Output.Trim() } else { "the service $service does not run" }
    throw "PostgreSQL did not take connections within 2 minutes ($detail)."
}

# Installs PostgreSQL with EDB's installer ($installer, checked; $plan from Get-PostgresPlan), unattended: the server
# and its command-line tools (no pgAdmin, no StackBuilder). Its superuser password is random and given in an options
# file only SYSTEM and Administrators can read, deleted when the installer ends (never on a command line, which other
# programs can read). It is replaced at once by another random one (the installer may pass the first to its own
# helpers), which is kept nowhere: install.md says how an administrator sets a new one. Then PostgreSQL listens on
# this machine only, and its pg_hba.conf may let in nothing else. Returns the superuser's password, for this run only.
function Install-PostgresServer([string] $installer, $plan) {
    $installPassword = New-Secret 32
    Register-Secret $installPassword
    $options = Join-Path $SetupDownloads 'postgresql-options.txt'
    # The installer unpacks and logs into its TEMP: one of this script's private folders.
    $temp = Join-Path $SetupDownloads 'postgresql-temp'
    $null = New-Item -ItemType Directory $temp
    Write-PrivateFile $options "superpassword=$installPassword`r`n"
    Write-Step "Installing PostgreSQL $($plan.Major) in $($plan.Prefix) (the service $($plan.Service), port $($plan.Port); this takes a few minutes)"
    try {
        $code = Start-Installer $installer @('--mode', 'unattended', '--unattendedmodeui', 'none', '--optionfile', $options,
            '--prefix', $plan.Prefix, '--datadir', $plan.Data, '--serverport', "$($plan.Port)", '--servicename', $plan.Service,
            '--disable-components', 'pgAdmin,stackbuilder') $temp 60
    }
    finally {
        Remove-Item -LiteralPath $options -Force -ErrorAction SilentlyContinue
    }
    $left = "If PostgreSQL $($plan.Major) now shows in Apps, remove it (and $($plan.Prefix)) before running this script again."
    if ($code -ne 0) {
        $kept = Join-Path $SetupRoot ('postgresql-install-{0:yyyyMMdd-HHmmss}' -f (Get-Date))
        Move-Item -LiteralPath $temp $kept -ErrorAction SilentlyContinue
        throw "PostgreSQL's installer failed (exit code $code). Its logs are in $kept, which only administrators can open: delete it once read, since they may hold the password that attempt was given. $left"
    }
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
    $bin = Join-Path $plan.Prefix 'bin'
    try {
        Wait-PostgresReady $plan.Service $bin $installPassword
        $password = New-Secret 32
        Register-Secret $password
        $changed = Invoke-Psql $bin $installPassword @() "ALTER ROLE postgres PASSWORD '$password';"
        if ($changed.ExitCode -ne 0) { throw "The postgres password could not be changed: $(ConvertTo-PrintableText $changed.Output.Trim())" }
        # Only the superuser and PostgreSQL's own roles: anything else was made by whoever connected meanwhile.
        $roles = Invoke-Psql $bin $password @('-c', "SELECT rolname FROM pg_roles WHERE rolname <> 'postgres' AND rolname !~ '^pg_'")
        if ($roles.ExitCode -ne 0) { throw "Its roles could not be listed: $(ConvertTo-PrintableText $roles.Output.Trim())" }
        if ($roles.Output.Trim()) { throw "It has roles this script did not make ($(ConvertTo-PrintableText ($roles.Output.Trim() -replace '\s+', ', '))): something else connected to it while it was being installed." }
        Write-Step 'PostgreSQL: listening on this machine only'
        $set = Invoke-Psql $bin $password @('-c', "ALTER SYSTEM SET listen_addresses = 'localhost'")
        if ($set.ExitCode -ne 0) { throw "listen_addresses could not be set: $(ConvertTo-PrintableText $set.Output.Trim())" }
        Restart-Service -Name $plan.Service -Force
        Wait-PostgresReady $plan.Service $bin $password
        $listen = Invoke-Psql $bin $password @('-c', 'SHOW listen_addresses')
        if ($listen.ExitCode -ne 0 -or $listen.Output.Trim() -cne 'localhost') { throw "It listens on '$(ConvertTo-PrintableText $listen.Output.Trim())', not on localhost only." }
        $rules = Invoke-Psql $bin $password @('-F', '|', '-c', "SELECT type, coalesce(address, ''), coalesce(netmask, ''), coalesce(error, '') FROM pg_hba_file_rules")
        if ($rules.ExitCode -ne 0) { throw "pg_hba.conf's rules could not be read: $(ConvertTo-PrintableText $rules.Output.Trim())" }
        $open = @(Find-NonLocalHbaRules ($rules.Output -split "`r?`n"))
        if ($open.Count -gt 0) { throw "pg_hba.conf in $($plan.Data) lets in more than this machine: $($open -join '; ')." }
    }
    catch {
        throw "PostgreSQL $($plan.Major) was installed, but setting it up failed: $($_.Exception.Message) $left"
    }
    return ConvertTo-Secure $password
}

# What the local PostgreSQL has of Pulsar's ($bin, $password: its postgres superuser's): 'none' (no 'pulsar' role or
# database), 'leftover' (the 'pulsar' role this script made, marked with $PulsarRoleComment, and no database: what an
# install that failed left, which nothing can use, since only the panel's settings had the role's password), or, for
# anything else, what is there, fit to print: a panel's data (a panel removed with -Uninstall keeps it) or someone
# else's are never dropped. A 'pulsar' database is never looked into, even an empty one: its owner chooses what a
# session in it runs with (ALTER DATABASE pulsar SET search_path, and functions or views of its own that would then
# run as the superuser), and whoever has the panel's database password is that owner. A failed install drops the
# database it made itself (Undo-PanelInstall); one that could not is for the administrator to drop ("Starting over").
function Get-PanelDatabaseState([string] $bin, [string] $password) {
    $role = Invoke-Psql $bin $password @('-c', "SELECT 'role:' || coalesce(shobj_description(oid, 'pg_authid'), '') FROM pg_roles WHERE rolname = 'pulsar'")
    if ($role.ExitCode -ne 0) { throw "Could not connect to PostgreSQL as 'postgres': $(ConvertTo-PrintableText $role.Output.Trim())" }
    $database = Invoke-Psql $bin $password @('-c', "SELECT 'owner:' || pg_get_userbyid(datdba) FROM pg_database WHERE datname = 'pulsar'")
    if ($database.ExitCode -ne 0) { throw "Could not list PostgreSQL's databases: $(ConvertTo-PrintableText $database.Output.Trim())" }
    $roleText = $role.Output.Trim()
    $owner = $database.Output.Trim()
    if (-not $roleText -and -not $owner) { return 'none' }
    if ($owner -and $owner -cne 'owner:pulsar') {
        return "PostgreSQL already has a 'pulsar' database, owned by '$(ConvertTo-PrintableText $owner.Substring(6))', which this script did not make"
    }
    if ($roleText -cne "role:$PulsarRoleComment") {
        return "PostgreSQL already has a 'pulsar' role, which this script did not mark as its own (an install made before it marked them, or something else)"
    }
    if ($owner) {
        return "PostgreSQL already has Pulsar's 'pulsar' database: a panel's data (a panel removed with -Uninstall keeps it), or what an install that failed could not drop"
    }
    return 'leftover'
}

# What to do about a 'pulsar' role or database this script may not replace ($state, from Get-PanelDatabaseState).
function Get-PanelDatabaseMessage([string] $state) {
    return "$state. To start over, drop the 'pulsar' database and role (docs/guides/install.md, ""Starting over""; first back up what you need of it), then run this script again; to install the panel with a backup's data, drop them and use -RestoreFrom; or give the panel another database with -ConnectionString."
}

function New-PanelDatabase {
    $bin = Find-PostgresBin
    if (-not $PostgresPassword) {
        $script:PostgresPassword = Read-Secret "Password of PostgreSQL's 'postgres' user (chosen when PostgreSQL was installed)" 'postgresPassword'
    }
    $superuser = ConvertFrom-Secure $PostgresPassword
    $state = Get-PanelDatabaseState $bin $superuser
    if ($state -ceq 'leftover') {
        # The role alone: PostgreSQL refuses to drop it if a database of its own appeared meanwhile.
        Write-Step "Dropping the 'pulsar' role that an install which failed left"
        $dropped = Invoke-Psql $bin $superuser @('-c', 'DROP ROLE pulsar')
        if ($dropped.ExitCode -ne 0) { throw "Could not drop the 'pulsar' role an install that failed left: $(ConvertTo-PrintableText $dropped.Output.Trim())" }
    }
    elseif ($state -cne 'none') { throw (Get-PanelDatabaseMessage $state) }
    $password = New-Secret 32
    Register-Secret $password
    # On standard input, not the command line: every process on this machine can read command lines. Marked as this
    # script's in the same transaction (Get-PanelDatabaseState).
    $created = Invoke-Psql $bin $superuser @() "BEGIN;`nCREATE ROLE pulsar LOGIN PASSWORD '$password';`nCOMMENT ON ROLE pulsar IS '$PulsarRoleComment';`nCOMMIT;`n"
    if ($created.ExitCode -ne 0) { throw "Could not create the 'pulsar' role: $(ConvertTo-PrintableText $created.Output.Trim())" }
    $created = Invoke-Psql $bin $superuser @('-c', 'CREATE DATABASE pulsar OWNER pulsar')
    if ($created.ExitCode -ne 0) {
        # Nobody else knows the role's password: kept, it would only make the next run refuse.
        $dropped = Invoke-Psql $bin $superuser @('-c', 'DROP ROLE pulsar')
        $left = ''
        if ($dropped.ExitCode -ne 0) { $left = " The 'pulsar' role created for it could not be dropped ($(ConvertTo-PrintableText $dropped.Output.Trim())): drop it before running this script again." }
        throw "Could not create the 'pulsar' database: $(ConvertTo-PrintableText $created.Output.Trim())$left"
    }
    return "Host=localhost;Port=$PostgresPort;Database=pulsar;Username=pulsar;Password=$password"
}

# Drops the 'pulsar' database and role New-PanelDatabase made, for an install that failed after it: nobody else knows
# the role's password. What could not be dropped, fit to print, or nothing.
function Remove-PanelDatabase {
    $dropped = Invoke-Psql (Find-PostgresBin) (ConvertFrom-Secure $PostgresPassword) @('-c', 'DROP DATABASE IF EXISTS pulsar WITH (FORCE)', '-c', 'DROP ROLE IF EXISTS pulsar')
    if ($dropped.ExitCode -ne 0) { return "the 'pulsar' database and role in PostgreSQL (they could not be dropped: $(ConvertTo-PrintableText $dropped.Output.Trim()))" }
}

# --- Certificate ------------------------------------------------------------------------------------------------

# Returns @{ Path; Password; Thumbprint; Cer; SelfSigned; Sha256 } for the panel's HTTPS certificate, in the panel's
# private folder; a self-signed one also has Names (what it lists, as Add-TrustedCertificate takes them) and Addresses
# (the IP addresses other computers may use, the loopback ones left out).
function New-PanelCertificate {
    $tls = Join-Path $PanelData 'tls'
    New-Item -ItemType Directory -Force $tls | Out-Null
    $pfx = Join-Path $tls 'panel.pfx'
    $cer = Join-Path $tls 'panel.cer'
    if ($CertificatePath) {
        $password = ''
        if ($CertificatePassword) { $password = ConvertFrom-Secure $CertificatePassword }
        Copy-Item $CertificatePath $pfx -Force
        $certificate = New-Object Security.Cryptography.X509Certificates.X509Certificate2($pfx, $password)
        [IO.File]::WriteAllBytes($cer, $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
        return @{ Path = $pfx; Password = $password; Thumbprint = $certificate.Thumbprint; Cer = $cer; SelfSigned = $false; Sha256 = (Get-CertificateHash $certificate) }
    }

    $machine = @('localhost', $env:COMPUTERNAME)
    $domain = (Get-CimInstance Win32_ComputerSystem).Domain
    if ($domain -and $domain -ne 'WORKGROUP') { $machine += "$($env:COMPUTERNAME).$domain" }
    $lan = @(Get-LanAddresses)
    $names = Get-PanelCertificateNames $machine $lan $HostName
    Write-Step "Creating a self-signed HTTPS certificate for $(@(Get-ListedNames $names) -join ', ')"
    if ($lan.Count -eq 0) {
        Write-Warning 'This PC has no private IPv4 address on a network with a router, so the certificate lists none: other computers must use its name (or pass -HostName with the address they use).'
    }
    $certificate = New-SelfSignedPanelCertificate $names
    try {
        # Trusted machine-wide (here, and on nodes with -TrustCertificate), so it must not be able to issue
        # certificates, and it is for a web server's identity only.
        if ((Get-CertificateAuthority $certificate) -ne $false) {
            throw 'The self-signed certificate came out able to issue certificates (its Basic Constraints do not say CA=false).'
        }
        if (-not (Test-ServerAuthenticationOnly (Get-CertificateUses $certificate))) {
            throw 'The self-signed certificate came out usable for more than a web server (its Extended Key Usage is not server authentication alone).'
        }
        $password = New-Secret 32
        Register-Secret $password
        # Written into the panel's private folder, whose permissions the files take as they are made.
        [IO.File]::WriteAllBytes($pfx, $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, (ConvertTo-Secure $password)))
        [IO.File]::WriteAllBytes($cer, $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
        $listed = @($names.Dns) + @($names.Ips | ForEach-Object { "$_" })
        $addresses = @($names.Ips | Where-Object { -not [Net.IPAddress]::IsLoopback($_) } | ForEach-Object { "$_" })
        return @{ Path = $pfx; Password = $password; Thumbprint = $certificate.Thumbprint; Cer = $cer; SelfSigned = $true; Names = $listed; Addresses = $addresses; Sha256 = (Get-CertificateHash $certificate) }
    }
    finally {
        # Its key was never in a store or a key container: it goes with the certificate.
        $certificate.Dispose()
    }
}

# A self-signed certificate for the panel, made in memory: no certificate store and no key container is touched, and
# its key leaves this process only in the PFX. It lists $names (Get-PanelCertificateNames) in its Subject Alternative
# Name, each of its kind (DNS names, IP addresses: browsers match an address only against IP addresses, so an address
# written as a DNS name would not do), and is otherwise what New-SelfSignedCertificate made before (it cannot list IP
# addresses): subject CN=localhost, RSA 2048 with SHA-256, valid from 10 minutes ago for 5 years, a Subject Key
# Identifier, Key Usage (critical) digital signature and key encipherment, and, since it is trusted machine-wide (here,
# and on nodes with -TrustCertificate), Basic Constraints (critical) CA=false, so that whoever holds its key cannot vouch
# for other sites with certificates it issues, and Extended Key Usage server authentication alone. Friendly name
# 'Pulsar panel'.
function New-SelfSignedPanelCertificate($names) {
    # An ephemeral key (no name: nothing is stored), which only the PFX export reads.
    $rsa = [Security.Cryptography.RSACng]::new(2048)
    try {
        if (-not $rsa.Key.IsEphemeral) { throw 'The certificate''s key came out stored in a key container.' }
        $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=localhost', $rsa,
            [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $alternative = New-Object Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder
        foreach ($name in $names.Dns) { $alternative.AddDnsName($name) }
        foreach ($address in $names.Ips) { $alternative.AddIpAddress($address) }
        $extensions = $request.CertificateExtensions
        $extensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
        $usage = [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature -bor [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyEncipherment
        $extensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new($usage, $true))
        $uses = New-Object Security.Cryptography.OidCollection
        $null = $uses.Add([Security.Cryptography.Oid]::new($ServerAuthentication))
        $extensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($uses, $false))
        $extensions.Add($alternative.Build($false))
        $extensions.Add([Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension]::new($request.PublicKey, $false))
        $now = [DateTimeOffset]::Now
        $certificate = $request.CreateSelfSigned($now.AddMinutes(-10), $now.AddYears(5))
        $certificate.FriendlyName = 'Pulsar panel'
        return $certificate
    }
    finally { $rsa.Dispose() }
}

# What the panel's self-signed certificate lists, in the shape Get-CertificateNames reads one (Dns, Ips, Others): the
# names of this machine ($machine: localhost, its name and DNS name) and -HostName's ($extra), as given, each once; then
# 127.0.0.1 and ::1, this machine's private addresses ($lan, Get-LanAddresses) and -HostName's addresses, each once.
function Get-PanelCertificateNames([string[]] $machine, $lan, [string[]] $extra) {
    $dns = @($machine | Where-Object { $_ })
    $ips = @([Net.IPAddress]::Loopback, [Net.IPAddress]::IPv6Loopback) + @($lan | Where-Object { $_ })
    foreach ($name in @($extra | Where-Object { $_ })) {
        $address = ConvertTo-CertificateAddress $name
        if ($address) { $ips += $address }
        else { $dns += $name }
    }
    $listed = @()
    $seen = @{}
    foreach ($address in $ips) {
        if ($seen.ContainsKey("$address")) { continue }
        $seen["$address"] = $true
        $listed += $address
    }
    return [pscustomobject]@{ Dns = @($dns | Select-Object -Unique); Ips = $listed; Others = @() }
}

# What a -HostName is ($text): an IP address, given back as one ([Net.IPAddress]), or $null for a name, which -HostName's
# pattern checked. An IPv4 address is four numbers from 0 to 255 (192.168.1.20); an IPv6 address has ':' (2001:db8::20),
# without a zone ('%') or an IPv4 address written as IPv6 (::ffff:192.168.1.20: write it as IPv4). Anything else is an
# error: a name whose last label is a number (10.1, 192.168.1.300, 0x7f), which browsers read as an IPv4 address written
# in a form no certificate lists, and addresses no server has (0.0.0.0, ::, broadcast, multicast).
function ConvertTo-CertificateAddress([string] $text) {
    $octet = '(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])'
    $address = $null
    if ($text -cmatch "^$octet(\.$octet){3}\z") {
        $address = [Net.IPAddress]::Parse($text)
        $first = $address.GetAddressBytes()[0]
        if ($first -eq 0 -or $first -ge 224) { throw "$text is not an address a server has: give the one other computers reach the panel by." }
        return $address
    }
    if ($text.Contains(':')) {
        if ($text -cnotmatch '^[0-9A-Fa-f:.]+\z' -or -not [Net.IPAddress]::TryParse($text, [ref] $address) -or $address.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetworkV6) {
            throw "$(ConvertTo-PrintableText $text) is not an IPv6 address (give it without brackets, a zone or a port, like 2001:db8::20)."
        }
        if ($address.IsIPv4MappedToIPv6) { throw "$text is an IPv4 address written as IPv6: give it as IPv4 ($($address.MapToIPv4()))." }
        if ($address.Equals([Net.IPAddress]::IPv6Any) -or $address.IsIPv6Multicast) { throw "$text is not an address a server has: give the one other computers reach the panel by." }
        return $address
    }
    $last = $text.TrimEnd('.').Split('.')[-1]
    if ($last -match '^(0x[0-9a-f]*|[0-9]+)\z') {
        throw "$text is neither a name (its last part is a number) nor an IPv4 address: write an address as four numbers from 0 to 255, like 192.168.1.20."
    }
    return $null
}

# Whether $address is a private-use IPv4 address (RFC 1918: 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16): one on a local
# network, never an internet site's. 169.254.0.0/16 (no DHCP server answered), 100.64.0.0/10 (a carrier's NAT, some
# VPNs) and IPv6 addresses are not.
function Test-PrivateAddress([Net.IPAddress] $address) {
    if ($address.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) { return $false }
    $bytes = $address.GetAddressBytes()
    return $bytes[0] -eq 10 -or ($bytes[0] -eq 172 -and $bytes[1] -ge 16 -and $bytes[1] -le 31) -or ($bytes[0] -eq 192 -and $bytes[1] -eq 168)
}

# This machine's addresses on its local networks, for the self-signed certificate (Select-LanAddresses).
function Get-LanAddresses {
    $addresses = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue)
    $adapters = @(Get-NetAdapter -ErrorAction SilentlyContinue)
    $routes = @(Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue)
    Select-LanAddresses $addresses $adapters $routes
}

# Of this machine's IPv4 addresses ($addresses, Get-NetIPAddress), those other computers on its networks can reach it by,
# each once, in order: private-use ones (Test-PrivateAddress) in use (state Preferred, not Tentative or Duplicate), on a
# network adapter that is up ($adapters, Get-NetAdapter, which lists neither the loopback nor hidden ones) and has a
# default route ($routes, Get-NetRoute 0.0.0.0/0: a router). That leaves out the virtual switches that reach only this
# PC's virtual machines and WSL (Hyper-V's Default Switch, WSL's, VirtualBox's and VMware's host-only networks), whose
# addresses change at every start, without guessing from their names: the host's address on a Hyper-V external switch,
# which has the router, stays. Public addresses and IPv6 ones are never added on their own (-HostName adds them).
function Select-LanAddresses($addresses, $adapters, $routes) {
    $up = @($adapters | Where-Object { "$($_.Status)" -eq 'Up' } | ForEach-Object { [int]$_.InterfaceIndex })
    $routed = @($routes | ForEach-Object { [int]$_.InterfaceIndex })
    $seen = @{}
    foreach ($entry in $addresses) {
        if ("$($entry.AddressState)" -ne 'Preferred') { continue }
        $index = [int]$entry.InterfaceIndex
        if ($index -notin $up -or $index -notin $routed) { continue }
        $address = $null
        if (-not [Net.IPAddress]::TryParse("$($entry.IPAddress)", [ref] $address) -or -not (Test-PrivateAddress $address)) { continue }
        if ($seen.ContainsKey("$address")) { continue }
        $seen["$address"] = $true
        $address
    }
}

# The line the setup wizard reads the panel certificate's fingerprint from: "PULSAR-CERTIFICATE <the lowercase
# hexadecimal SHA-256 of its DER>", the fingerprint browsers show for it.
function Format-CertificateLine([string] $sha256) {
    if ($sha256 -cnotmatch '^[0-9a-f]{64}\z') { throw "Not a certificate's SHA-256: $(ConvertTo-PrintableText $sha256)" }
    return "PULSAR-CERTIFICATE $sha256"
}

# A SHA-256 as browsers, the setup's last page and the panel's Account page show a certificate's fingerprint: in upper
# case, a ':' between each two digits (Get-FileHash shows the same digits without them).
function Format-Fingerprint([string] $sha256) {
    return (@([regex]::Matches($sha256.ToUpperInvariant(), '..') | ForEach-Object { $_.Value }) -join ':')
}

# How other computers reach the panel at $port and come to trust its certificate ($certificate, New-PanelCertificate's),
# printed once the install is done: its addresses, and the fingerprint to compare.
function Write-RemoteAccess($certificate, [int] $port) {
    $hosts = @()
    if ($certificate.ContainsKey('Addresses')) {
        $hosts += @($certificate.Addresses | ForEach-Object { if ($_.Contains(':')) { "[$_]" } else { $_ } })
    }
    $hosts += $env:COMPUTERNAME
    Write-Host "From other computers: $(@($hosts | ForEach-Object { "https://${_}:$port" }) -join ' or ')."
    if ($certificate.SelfSigned) {
        Write-Host "Browsers there warn about its self-signed certificate until they trust it: the panel's Account page has it to download (Panel certificate), with the steps."
    }
    Write-Host "Its SHA-256 fingerprint, to check the certificate other PCs download or their browsers show before they trust it: $(Format-Fingerprint $certificate.Sha256)"
    if ($Unattended) { Write-Host (Format-CertificateLine $certificate.Sha256) }
}

# What a certificate's Basic Constraints say: $true (it may issue certificates), $false (it may not), or $null when it
# has none.
function Get-CertificateAuthority([Security.Cryptography.X509Certificates.X509Certificate2] $certificate) {
    $extension = $certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.19' } | Select-Object -First 1
    if (-not $extension) { return $null }
    return (New-Object Security.Cryptography.X509Certificates.X509BasicConstraintsExtension($extension, $extension.Critical)).CertificateAuthority
}

# What a certificate may be used for, by its Extended Key Usage: the purposes' identifiers, or $null when it has none
# (then it may be used for anything).
function Get-CertificateUses([Security.Cryptography.X509Certificates.X509Certificate2] $certificate) {
    $extension = $certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' } | Select-Object -First 1
    if (-not $extension) { return $null }
    $usages = (New-Object Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension($extension, $extension.Critical)).EnhancedKeyUsages
    return , [string[]]@($usages | ForEach-Object { $_.Value })
}

# Whether those uses are a web server's identity and nothing else.
function Test-ServerAuthenticationOnly([string[]] $uses) {
    return $null -ne $uses -and $uses.Count -eq 1 -and $uses[0] -eq $ServerAuthentication
}

# Text from a certificate, fit to print: control and format characters (a carriage return or an escape sequence could
# rewrite what the console shows, a direction override reorder it) become '?'.
function ConvertTo-PrintableText([string] $text) {
    return [regex]::Replace("$text", '\p{C}', '?')
}

# The certificate that encrypts the panel's Data Protection key ring. Unlike machine DPAPI, it works on any machine:
# a backup of the panel's data folder and settings restores the keys, and with them the database's secrets.
function New-KeyRingCertificate {
    $folder = Join-Path $PanelData 'keyring'
    New-Item -ItemType Directory -Force $folder | Out-Null
    $pfx = Join-Path $folder 'keyring.pfx'
    $certificate = New-SelfSignedCertificate -Subject 'CN=Pulsar panel key ring' -CertStoreLocation 'Cert:\LocalMachine\My' `
        -KeyExportPolicy Exportable -KeyAlgorithm RSA -KeyLength 3072 -KeyUsage KeyEncipherment, DataEncipherment `
        -NotAfter (Get-Date).AddYears(100)
    $password = New-Secret 32
    Register-Secret $password
    $null = Export-PfxCertificate -Cert $certificate -FilePath $pfx -Password (ConvertTo-Secure $password)
    Remove-Item "Cert:\LocalMachine\My\$($certificate.Thumbprint)" -DeleteKey
    return @{ Path = $pfx; Password = $password }
}

# A database dump from a backup, into the database the panel will use.
function Restore-PanelDatabase([string] $dump, [string] $connection) {
    $bin = Find-PostgresBin
    $parts = @{}
    foreach ($part in $connection.Split(';')) {
        if ($part -match '^\s*([^=]+?)\s*=\s*(.*)$') { $parts[$Matches[1].ToLowerInvariant()] = $Matches[2] }
    }
    $env:PGPASSWORD = $parts['password']
    try {
        $result = Invoke-Native (Join-Path $bin 'pg_restore.exe') -h $parts['host'] -p $parts['port'] -U $parts['username'] -d $parts['database'] --no-owner --exit-on-error $dump
        if ($result.ExitCode -ne 0) { throw "Could not restore the database: $($result.Output)" }
    }
    finally {
        Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    }
}

# Reads a certificate file once: what is checked is what is trusted (a file read again could have changed meanwhile).
function Read-Certificate([string] $path) {
    $bytes = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $path).ProviderPath)
    if ([Security.Cryptography.X509Certificates.X509Certificate2]::GetCertContentType($bytes) -ne [Security.Cryptography.X509Certificates.X509ContentType]::Cert) {
        throw "$path is not a certificate file (.cer): pass the panel's panel.cer."
    }
    return [Security.Cryptography.X509Certificates.X509Certificate2]::new($bytes)
}

# One DER element at $position: its tag, where its content starts, its length and where the next element starts.
function Read-DerElement([byte[]] $data, [int] $position) {
    if ($position + 2 -gt $data.Length) { throw 'The certificate is malformed.' }
    $length = [int]$data[$position + 1]
    $start = $position + 2
    if ($length -ge 0x80) {
        $count = $length - 0x80
        if ($count -lt 1 -or $count -gt 3 -or $start + $count -gt $data.Length) { throw 'The certificate is malformed.' }
        $length = 0
        for ($i = 0; $i -lt $count; $i++) { $length = ($length * 256) + $data[$start + $i] }
        $start += $count
    }
    if ($start + $length -gt $data.Length) { throw 'The certificate is malformed.' }
    return [pscustomobject]@{ Tag = $data[$position]; Start = $start; Length = $length; Next = $start + $length }
}

# The names in a certificate's Subject Alternative Name, read from its encoding (the extension's text form is in
# Windows' display language): DNS names, IP addresses, and the kinds of the others (e-mail addresses, URIs...). A DNS
# name must be one: letters, digits, '.', '-' and '_', with a leading '*.' for a wildcard; anything else (a line break
# or an escape that would rewrite what is printed, a space) is refused.
function Get-CertificateNames([Security.Cryptography.X509Certificates.X509Certificate2] $certificate) {
    $dns = @()
    $ips = @()
    $others = @()
    $extension = $certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.17' } | Select-Object -First 1
    if ($extension) {
        $data = $extension.RawData
        $names = Read-DerElement $data 0
        if ($names.Tag -ne 0x30) { throw 'The certificate is malformed.' }
        $position = $names.Start
        while ($position -lt $names.Next) {
            $name = Read-DerElement $data $position
            if ($name.Next -gt $names.Next) { throw 'The certificate is malformed.' }
            $value = New-Object byte[] $name.Length
            [Array]::Copy($data, $name.Start, $value, 0, $name.Length)
            # [2] dNSName, [7] iPAddress (4 or 16 bytes).
            if ($name.Tag -eq 0x82) {
                # Byte for byte, so that whatever is not a DNS name's character shows as one.
                $text = [Text.Encoding]::GetEncoding(28591).GetString($value)
                # \z, not $: $ also matches before a final line feed.
                if ($text -cnotmatch '^(\*\.)?[A-Za-z0-9_-]+(\.[A-Za-z0-9_-]+)*\.?\z') {
                    throw "The certificate lists a DNS name that is not one: '$(ConvertTo-PrintableText $text)'."
                }
                $dns += $text
            }
            elseif ($name.Tag -eq 0x87) {
                if ($value.Length -notin 4, 16) { throw 'The certificate is malformed.' }
                $ips += New-Object Net.IPAddress (, $value)
            }
            else {
                $kinds = @{ 0xA0 = 'another name'; 0x81 = 'an e-mail address'; 0xA3 = 'an X.400 address'; 0xA4 = 'a directory name'; 0xA5 = 'an EDI party name'; 0x86 = 'a URI'; 0x88 = 'a registered ID' }
                $kind = $kinds[[int]$name.Tag]
                if (-not $kind) { $kind = 'a name of an unknown kind' }
                $others += $kind
            }
            $position = $name.Next
        }
    }
    return [pscustomobject]@{ Dns = $dns; Ips = $ips; Others = $others }
}

# The names a certificate lists besides the panel's, as they would be printed: those in $panelNames (the -PanelUrl
# host on a node; on the panel's own machine, the names and addresses its certificate was made for), localhost and the
# loopback addresses (this machine itself), and single-label names and private-use IPv4 addresses (RFC 1918: a machine
# on the local network, never an internet site; the installer's certificate always lists the panel machine's own) are
# the panel's. A wildcard stands for every name under it, so it never is, unless the panel's own certificate was made
# for it (-HostName); neither are other addresses (public ones, 169.254 ones, IPv6 ones but ::1) or other kinds of names.
function Get-ForeignCertificateNames($names, [string[]] $panelNames) {
    $panel = @($panelNames | Where-Object { $_ } | ForEach-Object { $_.TrimEnd('.').ToLowerInvariant() })
    foreach ($listed in $names.Dns) {
        $name = $listed.TrimEnd('.').ToLowerInvariant()
        if ($name -in $panel) { continue }
        if (-not $name.StartsWith('*.') -and ($name -eq 'localhost' -or -not $name.Contains('.'))) { continue }
        ConvertTo-PrintableText $listed
    }
    foreach ($address in $names.Ips) {
        if ([Net.IPAddress]::IsLoopback($address) -or (Test-PrivateAddress $address)) { continue }
        $isPanel = $false
        foreach ($panelName in $panel) {
            $parsed = $null
            if ([Net.IPAddress]::TryParse($panelName, [ref] $parsed) -and $parsed.Equals($address)) { $isPanel = $true }
        }
        if (-not $isPanel) { "$address" }
    }
    foreach ($other in $names.Others) { $other }
}

# Whether the certificate's Subject Alternative Name lists $hostName (as the agent checks it): a DNS name, a wildcard
# that stands for its leftmost label, or an IP address.
function Test-CertificateNames($names, [string] $hostName) {
    $address = $null
    if ([Net.IPAddress]::TryParse($hostName, [ref] $address)) {
        return @($names.Ips | Where-Object { $_.Equals($address) }).Count -gt 0
    }
    $name = $hostName.TrimEnd('.').ToLowerInvariant()
    $dot = $name.IndexOf('.')
    foreach ($listed in $names.Dns) {
        $listed = $listed.TrimEnd('.').ToLowerInvariant()
        if ($listed -eq $name) { return $true }
        if ($listed.StartsWith('*.') -and $dot -gt 0 -and $name.Substring($dot + 1) -eq $listed.Substring(2)) { return $true }
    }
    return $false
}

# Checks that a panel's certificate may be trusted machine-wide, where it vouches for every name it lists and for every
# use it allows: one that can issue certificates is refused, since whoever holds its key could then vouch for any site
# to this machine, and so is one that does not name the panel's host (the agent would refuse it anyway), one that also
# names others than the panel ($panelNames: the names the panel's own certificate was made for, when known) and one
# whose uses go beyond a web server's identity, unless the switches for those say so.
function Assert-TrustableCertificate([Security.Cryptography.X509Certificates.X509Certificate2] $certificate, [string] $cer, [string] $hostName, [string[]] $panelNames = @()) {
    # An extension twice: what is checked here could be the one Windows does not use.
    $repeated = @($certificate.Extensions | Group-Object { $_.Oid.Value } | Where-Object Count -gt 1)
    if ($repeated.Count -gt 0) { throw "$cer is malformed: it has an extension more than once ($(($repeated | ForEach-Object Name) -join ', '))." }
    $authority = Get-CertificateAuthority $certificate
    if ($authority -eq $true) {
        throw "$cer can issue certificates (its Basic Constraints say CA=true): trusted by this machine, whoever holds its key could vouch for any site. Pass the panel's own certificate (panel.cer), not a certificate authority's."
    }
    if ($null -eq $authority) {
        $message = "$cer does not say whether it can issue certificates (it has no Basic Constraints), and Windows may take it for a certificate authority: trusted by this machine, whoever holds its key could then vouch for any site. The installer's own self-signed certificate says it cannot; make a new one like it (Basic Constraints, critical, CA=false)"
        if (-not $TrustCertificateWithoutBasicConstraints) {
            throw "$message, or pass -TrustCertificateWithoutBasicConstraints if you know it is the panel's own."
        }
        Write-Warning "$message. Trusted anyway, as -TrustCertificateWithoutBasicConstraints says."
    }
    $names = Get-CertificateNames $certificate
    if (-not (Test-CertificateNames $names $hostName)) {
        $listed = @(Get-ListedNames $names)
        $listedText = 'no names in a Subject Alternative Name'
        if ($listed.Count -gt 0) { $listedText = $listed -join ', ' }
        throw "$cer is not made for $hostName, the panel's address here (it lists $listedText). Pass the panel's own certificate, made for the address nodes use (-HostName when the panel is installed)."
    }
    $foreign = @(Get-ForeignCertificateNames $names (@($hostName) + $panelNames))
    if ($foreign.Count -gt 0) {
        $message = "$cer also names $($foreign -join ', '), which this check cannot tell are the panel's (only $hostName, localhost, machine names, the loopback addresses and private IPv4 addresses are): trusted by this machine, whoever holds its key could pass for them to it. Make the panel's certificate for the names nodes use and no others (-HostName when the panel is installed)"
        if (-not $TrustCertificateWithOtherNames) {
            throw "$message, or pass -TrustCertificateWithOtherNames if every name it lists is yours."
        }
        Write-Warning "$message. Trusted anyway, as -TrustCertificateWithOtherNames says."
    }
    $uses = Get-CertificateUses $certificate
    if (-not (Test-ServerAuthenticationOnly $uses)) {
        $usesText = 'it has no Extended Key Usage, so it allows every use'
        if ($null -ne $uses) { $usesText = "its Extended Key Usage lists $(if ($uses.Count -gt 0) { $uses -join ', ' } else { 'nothing' })" }
        $message = "$cer may be used for more than a web server's identity ($usesText): trusted by this machine, whoever holds its key could also vouch for code, e-mail or a client to it. The installer's own self-signed certificate lists server authentication ($ServerAuthentication) alone; make a new one like it"
        if (-not $TrustCertificateWithOtherUses) {
            throw "$message, or pass -TrustCertificateWithOtherUses if you know it is the panel's own."
        }
        Write-Warning "$message. Trusted anyway, as -TrustCertificateWithOtherUses says."
    }
}

# Checks that the panel's certificate a node code names may be pinned in the agent (PulsarAgent enroll
# --panel-certificate), which then accepts that certificate for the panel and nothing else, and changes no certificate
# store: it must be a web server's own (Basic Constraints that say CA=false, server authentication alone) and name
# $hostName, the address the agent uses. The names it lists besides do not matter: nothing on this machine trusts it.
# No switch lets another through: -TrustCertificate is for those.
function Assert-PinnableCertificate([Security.Cryptography.X509Certificates.X509Certificate2] $certificate, [string] $hostName) {
    $label = "The panel's certificate"
    $repeated = @($certificate.Extensions | Group-Object { $_.Oid.Value } | Where-Object Count -gt 1)
    if ($repeated.Count -gt 0) { throw "$label is malformed: it has an extension more than once ($(($repeated | ForEach-Object Name) -join ', '))." }
    if ((Get-CertificateAuthority $certificate) -ne $false) {
        throw "$label does not say it cannot issue certificates (its Basic Constraints are missing or say CA=true): it is not a web server's own, and the node code is not used with it. The installer's own self-signed certificate says CA=false; make a new one like it, or trust the panel's with -TrustCertificate."
    }
    if (-not (Test-ServerAuthenticationOnly (Get-CertificateUses $certificate))) {
        throw "$label may be used for more than a web server's identity (its Extended Key Usage is not server authentication, $ServerAuthentication, alone), and the node code is not used with it. Make a new one like the installer's own, or trust the panel's with -TrustCertificate."
    }
    $names = Get-CertificateNames $certificate
    if (-not (Test-CertificateNames $names $hostName)) {
        $listed = @(Get-ListedNames $names)
        $listedText = 'no names in a Subject Alternative Name'
        if ($listed.Count -gt 0) { $listedText = $listed -join ', ' }
        throw "$label is not made for $hostName, the panel's address here (it lists $listedText), so the agent would refuse it. Use the address the certificate names (-PanelUrl), or make the panel's certificate for the address nodes use (-HostName when the panel is installed)."
    }
}

# A certificate's names as they would be printed.
function Get-ListedNames($names) {
    foreach ($name in $names.Dns) { ConvertTo-PrintableText $name }
    foreach ($address in $names.Ips) { "$address" }
    foreach ($other in $names.Others) { $other }
}

# Trusts a panel's certificate machine-wide (the agent runs as SYSTEM) for the panel at $hostName, marked so -Uninstall
# removes it again. One that was already trusted is left unmarked: something else trusted it, and -Uninstall leaves it.
# Only for -TrustCertificate and the all-in-one panel's own certificate: a node code never comes here (its certificate
# is pinned in the agent, Assert-PinnableCertificate).
function Add-TrustedCertificate([string] $cer, [string] $hostName, [string[]] $panelNames = @()) {
    $certificate = Read-Certificate $cer
    Assert-TrustableCertificate $certificate $cer $hostName $panelNames
    $names = Get-CertificateNames $certificate
    Write-Host "    $(ConvertTo-PrintableText $certificate.Subject), thumbprint $($certificate.Thumbprint), for $(@(Get-ListedNames $names) -join ', ')"
    $trusted = "Cert:\LocalMachine\Root\$($certificate.Thumbprint)"
    if (Test-Path -LiteralPath $trusted) { return }
    # The certificate read and checked above, not the file again.
    $store = New-Object Security.Cryptography.X509Certificates.X509Store('Root', 'LocalMachine')
    $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    try { $store.Add($certificate) }
    finally { $store.Close() }
    (Get-Item -LiteralPath $trusted).FriendlyName = $TrustedName
}

# The thumbprint an earlier install noted, if the note holds one: 40 hexadecimal characters (Remove-Item takes
# wildcards, so a "*" would name every trusted root certificate). An empty note names nothing.
function Get-NotedThumbprint([string] $noted) {
    $noted = "$noted".Trim()
    if ($noted -notmatch '^[0-9A-Fa-f]{40}\z') {
        Write-Warning "$TrustedMarker does not hold a certificate thumbprint; it is ignored."
        return $null
    }
    return $noted.ToUpperInvariant()
}

# Which of the trusted root certificates -Uninstall removes: those Add-TrustedCertificate marked as the installer's
# (only administrators can mark one), never one because an earlier install's note ($note, $null when there is none)
# names it. The note is in the panel's data folder, which the panel's service account may change: a compromised panel
# could name any root there, and replace tls\panel.cer to match it. A noted certificate that is not marked (trusted by
# an install made before the mark) comes back as Unmarked, for the administrator to look at.
function Select-TrustToRemove([object[]] $trusted, $note) {
    $remove = @($trusted | Where-Object { $_.FriendlyName -eq $TrustedName } | ForEach-Object { $_.Thumbprint })
    $unmarked = $null
    if ($null -ne $note) {
        $noted = Get-NotedThumbprint $note
        if ($noted -and $noted -notin $remove) { $unmarked = $trusted | Where-Object { $_.Thumbprint -eq $noted } | Select-Object -First 1 }
    }
    return [pscustomobject]@{ Remove = $remove; Unmarked = $unmarked }
}

# Removes the trust Add-TrustedCertificate gave; what an earlier install only noted is shown, not removed.
function Remove-TrustedCertificates {
    $note = $null
    if (Test-Path -LiteralPath $TrustedMarker) { $note = "$(Get-Content -LiteralPath $TrustedMarker -Raw)" }
    $selected = Select-TrustToRemove @(Get-ChildItem 'Cert:\LocalMachine\Root') $note
    if ($selected.Unmarked) {
        Write-Warning "An earlier install noted that it trusted the certificate $($selected.Unmarked.Thumbprint) ($(ConvertTo-PrintableText $selected.Unmarked.Subject)). It is not marked as the installer's, and the note is in a folder the panel's service account can change, so it stays trusted: if it is the panel's own certificate, remove it from Trusted Root Certification Authorities (certlm.msc)."
    }
    if (Test-Path -LiteralPath $TrustedMarker) { Remove-Item -LiteralPath $TrustedMarker }
    foreach ($thumbprint in $selected.Remove | Select-Object -Unique) {
        Write-Step "No longer trusting the panel's certificate ($thumbprint)"
        Remove-Item -LiteralPath "Cert:\LocalMachine\Root\$thumbprint" -ErrorAction SilentlyContinue
    }
}

# --- Panel --------------------------------------------------------------------------------------------------------

function Write-PanelConfig([hashtable] $settings) {
    # Database and certificate passwords, and on a first install the setup token: SYSTEM, Administrators and the panel's
    # service account only, from the start. A file created in the program folder is readable by every user until its
    # permissions change (and a handle opened meanwhile keeps reading), so it is written in a private folder next to
    # it, which the file inherits from, and moved into place (the same volume: it keeps its permissions).
    $staging = Join-Path $PanelDir '.settings'
    New-Item -ItemType Directory -Force $staging | Out-Null
    Set-PrivateAcl $staging -read @($PanelAccount)
    $file = Join-Path $staging 'appsettings.Production.json'
    $settings | ConvertTo-Json -Depth 6 | Set-Content -Path $file -Encoding UTF8
    Set-PrivateAcl $file -read @($PanelAccount)
    Move-Item $file $PanelConfig -Force
    Remove-Item $staging
}

# Whether the panel can listen on TCP port $port as it does, on every address (Kestrel's https://*:<port>, and its SFTP
# server, which follows it): nothing listens there on any address, and Windows lets a program take it for itself (not
# a port in a range Hyper-V or WinNAT keep, netsh interface ipv4 show excludedportrange protocol=tcp).
function Test-PortFree([int] $port) {
    if (@(Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue).Count -gt 0) { return $false }
    $listener = $null
    try {
        if ([Net.Sockets.Socket]::OSSupportsIPv6) {
            $listener = New-Object Net.Sockets.TcpListener ([Net.IPAddress]::IPv6Any, $port)
            $listener.Server.DualMode = $true
        }
        else { $listener = New-Object Net.Sockets.TcpListener ([Net.IPAddress]::Any, $port) }
        $listener.ExclusiveAddressUse = $true
        $listener.Start()
        return $true
    }
    catch { return $false }
    finally { if ($listener) { $listener.Stop() } }
}

# What keeps a PFX file from being the panel's certificate, fit to print: it does not open with $password, or holds no
# private key. $null when it can be. Its key is read without being stored anywhere.
function Test-PfxFile([string] $path, [string] $password) {
    try {
        $bytes = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $path).ProviderPath)
        $certificate = New-Object Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList @($bytes, $password, [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
    }
    catch { return "it does not open with the password given ($($_.Exception.GetBaseException().Message.Trim()))" }
    try {
        if (-not $certificate.HasPrivateKey) { return 'it holds no private key' }
        return $null
    }
    finally { $certificate.Dispose() }
}

# The port of an endpoint's URL in the panel's settings ("https://*:8443").
function Get-EndpointPort([string] $url) {
    return [int]($url -replace '^.*:(\d+)/?$', '$1')
}

# What would stop a panel's install, checked before anything is downloaded or changed: the panel (or, in all-in-one
# mode, the agent) installed already, ports that clash or are taken (the backup's, with -RestoreFrom), a certificate
# (or a backup's) that is missing or does not open with its password, and the administrator's password (asked for
# unless given). The database is checked with PostgreSQL's (Get-PostgresPlan).
function Assert-PanelInstallable {
    if (Test-Service $PanelService) { throw (New-SetupError $ExitUsage 'The panel is already installed on this machine. Use -Update to update it, or -Uninstall first.') }
    if ($Mode -eq 'AllInOne' -and (Test-Service $AgentService)) { throw (New-SetupError $ExitUsage 'The agent is already installed on this machine. Use -Update to update it, or -Uninstall first.') }
    # -HostName's names and addresses, as the self-signed certificate would list them (Get-PanelCertificateNames).
    foreach ($name in $HostName) {
        try { $null = ConvertTo-CertificateAddress $name }
        catch { throw (New-SetupError $ExitUsage "-HostName: $($_.Exception.Message) Nothing was changed.") }
    }
    $https = $Port
    $agents = $AgentsPort
    $what = @{ Https = '-Port'; Agents = '-AgentsPort' }
    if ($RestoreFrom) {
        if (-not (Test-Path -LiteralPath (Join-Path $RestoreFrom 'panel-data\keys'))) { throw (New-SetupError $ExitUsage "$RestoreFrom is not a Pulsar backup (no panel-data\keys). Nothing was changed.") }
        $settingsFile = Join-Path $RestoreFrom 'appsettings.Production.json'
        if (-not (Test-Path -LiteralPath $settingsFile -PathType Leaf)) { throw (New-SetupError $ExitUsage "$RestoreFrom is not a Pulsar backup (no appsettings.Production.json). Nothing was changed.") }
        $old = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json
        $https = Get-EndpointPort $old.Kestrel.Endpoints.Https.Url
        $agents = 0
        if ($old.Kestrel.Endpoints.PSObject.Properties['Agents']) { $agents = Get-EndpointPort $old.Kestrel.Endpoints.Agents.Url }
        $what = @{ Https = "the backup's panel port"; Agents = "the backup's agents' port" }
        $tlsPassword = $old.Kestrel.Endpoints.Https.Certificate.Password
        Register-Secret $tlsPassword
        $problem = Test-PfxFile (Join-Path $RestoreFrom 'panel-data\tls\panel.pfx') $tlsPassword
        if ($problem) { throw (New-SetupError $ExitUsage "The backup's HTTPS certificate (panel-data\tls\panel.pfx in $RestoreFrom) cannot be the panel's: $problem. Nothing was changed.") }
    }
    elseif ($CertificatePath) {
        if (-not (Test-Path -LiteralPath $CertificatePath -PathType Leaf)) { throw (New-SetupError $ExitUsage "$CertificatePath was not found. Nothing was changed.") }
        $password = ''
        if ($CertificatePassword) { $password = ConvertFrom-Secure $CertificatePassword }
        $problem = Test-PfxFile $CertificatePath $password
        if ($problem) { throw (New-SetupError $ExitUsage "$CertificatePath cannot be the panel's certificate: $problem. Give its password in PULSAR_CERTIFICATE_PASSWORD, -CertificatePassword or the secrets file. Nothing was changed.") }
    }
    if ($agents -and $agents -eq $https) { throw (New-SetupError $ExitUsage "$($what.Agents) must be another port than $($what.Https): browsers must never be asked for a client certificate. Nothing was changed.") }
    if ($SftpPort -eq $https -or ($agents -and $SftpPort -eq $agents)) { throw (New-SetupError $ExitUsage "-SftpPort must be another port than -Port and -AgentsPort ($https$(if ($agents) { ", $agents" })). Nothing was changed.") }
    foreach ($port in @(@($what.Https, $https), @($what.Agents, $agents), @('-SftpPort', $SftpPort))) {
        if ($port[1] -and -not (Test-PortFree $port[1])) {
            throw (New-SetupError $ExitUnsupported "The panel would listen on port $($port[1]) ($($port[0])), which is taken: another program listens on it, or Windows keeps it (netsh interface ipv4 show excludedportrange protocol=tcp). Stop that program, or choose another port. Nothing was changed.")
        }
    }
    if ($RestoreFrom) { return }
    if (-not $AdminPassword) {
        $script:AdminPassword = Read-Secret "Password for the panel's first administrator, '$AdminUserName' (at least 12 characters)" 'adminPassword'
    }
    if ((ConvertFrom-Secure $AdminPassword).Length -lt 12) { throw (New-SetupError $ExitUsage "The administrator's password needs at least 12 characters.") }
}

# Undoes what a panel's install that failed did once it had made its database ($database: New-PanelDatabase made the
# 'pulsar' database and role) and maybe its service ($service): the service, the firewall rules (unless -NoFirewall),
# the program, and the database and role, whose password nobody else knows. Each thing that could not be undone, fit to
# print; nothing when all was. The panel's data folder stays, as -Uninstall leaves it.
function Undo-PanelInstall([bool] $service, [bool] $database) {
    $left = @()
    if ($service) {
        Stop-Service $PanelService -ErrorAction SilentlyContinue
        $deleted = Invoke-Native $Sc delete $PanelService
        if ($deleted.ExitCode -ne 0) { $left += "the service $PanelService (sc.exe delete: $($deleted.Output.Trim()))" }
        Start-Sleep -Seconds 2
    }
    if (-not $NoFirewall) {
        foreach ($rule in 'Pulsar-Panel-HTTPS', 'Pulsar-Panel-Agents', 'Pulsar-Panel-SFTP') {
            try { Get-NetFirewallRule -Name $rule -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction Stop }
            catch { $left += "the firewall rule $rule" }
        }
    }
    Remove-Item -LiteralPath $PanelDir -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $PanelDir) { $left += $PanelDir }
    if ($database) {
        try { $left += @(Remove-PanelDatabase) }
        catch { $left += "the 'pulsar' database and role in PostgreSQL ($($_.Exception.Message))" }
    }
    return $left
}

function Install-Panel {
    $backup = $null
    if ($RestoreFrom) {
        $backup = @{
            Settings = Get-Content (Join-Path $RestoreFrom 'appsettings.Production.json') -Raw | ConvertFrom-Json
            Data = Join-Path $RestoreFrom 'panel-data'
            Dump = Join-Path $RestoreFrom 'pulsar.dump'
        }
    }

    $connection = $ConnectionString
    if (-not $connection) {
        Write-Step 'Creating the panel database in the local PostgreSQL'
        $connection = New-PanelDatabase
    }
    # From here on, a failure undoes what this install did (Undo-PanelInstall): left, the database and its role would
    # only make the next run refuse, since their password goes with the panel's settings.
    $madeService = $false
    try {
        if ($backup -and (Test-Path $backup.Dump)) {
            Write-Step 'Restoring the database from the backup'
            Restore-PanelDatabase $backup.Dump $connection
        }

        Write-SetupStep 'panel' 'Installing the panel'
        Write-Step "Installing the panel in $PanelDir"
        Copy-Program (Join-Path $PSScriptRoot 'panel') $PanelDir
        Copy-BackupScript

        # The service first: its virtual account exists from then on, and the folders below grant it access.
        $exe = Join-Path $PanelDir 'Pulsar.Api.exe'
        $result = Invoke-Native $Sc create $PanelService binPath= "`"$exe`"" start= delayed-auto obj= $PanelAccount DisplayName= 'Pulsar Panel'
        if ($result.ExitCode -ne 0) { throw "Could not create the panel service: $($result.Output)" }
        $madeService = $true
        $null = Invoke-Native $Sc description $PanelService 'Pulsar game server panel (web interface and API).'
        $null = Invoke-Native $Sc failure $PanelService reset= 86400 actions= restart/5000/restart/15000/restart/60000
        $null = Invoke-Native $Sc failureflag $PanelService 1

        New-Item -ItemType Directory -Force $PanelData | Out-Null
        Set-PrivateAcl $PanelData -modify @($PanelAccount)
        foreach ($folder in 'keys', 'logs', 'tls') { New-Item -ItemType Directory -Force (Join-Path $PanelData $folder) | Out-Null }

        $setupToken = New-Secret 48
        Register-Secret $setupToken
        if ($backup) {
            Write-Step 'Restoring the keys, certificates and settings from the backup'
            foreach ($folder in 'keys', 'keyring', 'tls') {
                if (Test-Path (Join-Path $backup.Data $folder)) { Copy-Item (Join-Path $backup.Data $folder) $PanelData -Recurse -Force }
            }
            $old = $backup.Settings
            $tlsPassword = $old.Kestrel.Endpoints.Https.Certificate.Password
            # The backup's passwords, as the ones made here: never in the log.
            Register-Secret $tlsPassword
            Register-Secret $old.DataProtection.CertificatePassword
            $pfx = Join-Path $PanelData 'tls\panel.pfx'
            $restored = New-Object Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList @([IO.File]::ReadAllBytes($pfx), $tlsPassword, [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
            try { $thumbprint = $restored.Thumbprint; $sha256 = Get-CertificateHash $restored }
            finally { $restored.Dispose() }
            $certificate = @{ Path = $pfx; Password = $tlsPassword; Cer = (Join-Path $PanelData 'tls\panel.cer'); SelfSigned = $true; Thumbprint = $thumbprint; Sha256 = $sha256 }
            $Port = [int]($old.Kestrel.Endpoints.Https.Url -replace '^.*:(\d+)/?$', '$1')
            if ($old.Kestrel.Endpoints.PSObject.Properties['Agents']) {
                $AgentsPort = [int]($old.Kestrel.Endpoints.Agents.Url -replace '^.*:(\d+)/?$', '$1')
            }
            $keyRing = @{ Path = (Join-Path $PanelData 'keyring\keyring.pfx'); Password = $old.DataProtection.CertificatePassword }
            # The SFTP port is a panel setting kept in the database (restored with it): -SftpPort only names the port to
            # open (Assert-PanelInstallable checked it against the backup's ports).
        }
        else {
            $certificate = New-PanelCertificate
            $keyRing = New-KeyRingCertificate
        }

        $endpoints = @{ Https = @{ Url = "https://*:$Port"; Certificate = @{ Path = $certificate.Path; Password = $certificate.Password } } }
        if ($AgentsPort) {
            # The same certificate: agents already trust it.
            $endpoints.Agents = @{ Url = "https://*:$AgentsPort"; Certificate = @{ Path = $certificate.Path; Password = $certificate.Password } }
        }

        $settings = @{
            ConnectionStrings = @{ Pulsar = $connection }
            Kestrel = @{ Endpoints = $endpoints }
            DataProtection = @{ KeysPath = (Join-Path $PanelData 'keys'); CertificatePath = $keyRing.Path; CertificatePassword = $keyRing.Password }
            Pulsar = @{ LogsDirectory = (Join-Path $PanelData 'logs') }
        }
        if (-not $backup) { $settings.InitialSetup = @{ Token = $setupToken } }
        Write-PanelConfig $settings

        if (-not $NoFirewall) {
            # Domain and Private networks: on a network Windows treats as public (a coffee shop's, a hotel's), only with -FirewallPublic.
            $profiles = if ($FirewallPublic) { @('Any') } else { @('Domain', 'Private') }
            Write-Step "Allowing the panel's HTTPS port ($Port) in Windows Firewall ($($profiles -join ', ') networks)"
            Get-NetFirewallRule -Name 'Pulsar-Panel-HTTPS' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
            $null = New-NetFirewallRule -Name 'Pulsar-Panel-HTTPS' -DisplayName 'Pulsar Panel (HTTPS)' -Direction Inbound `
                -Protocol TCP -LocalPort $Port -Program $exe -Action Allow -Profile $profiles
            Get-NetFirewallRule -Name 'Pulsar-Panel-Agents' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
            if ($AgentsPort) {
                Write-Step "Allowing the agents' port ($AgentsPort) in Windows Firewall ($($profiles -join ', ') networks)"
                $null = New-NetFirewallRule -Name 'Pulsar-Panel-Agents' -DisplayName 'Pulsar Panel (agents)' -Direction Inbound `
                    -Protocol TCP -LocalPort $AgentsPort -Program $exe -Action Allow -Profile $profiles
            }
            Write-Step "Allowing the SFTP port ($SftpPort) in Windows Firewall ($($profiles -join ', ') networks)"
            Get-NetFirewallRule -Name 'Pulsar-Panel-SFTP' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
            $null = New-NetFirewallRule -Name 'Pulsar-Panel-SFTP' -DisplayName 'Pulsar Panel (SFTP)' -Direction Inbound `
                -Protocol TCP -LocalPort $SftpPort -Program $exe -Action Allow -Profile $profiles
        }

        Invoke-PanelMigrations
        Write-Step 'Starting the panel'
        Start-Panel
        Initialize-PanelClient
        $client = [PulsarInstallerClient]::new("https://localhost:$Port", $certificate.Thumbprint)
        try {
            if ($backup) { Write-SetupStep 'administrator' 'Waiting for the restored panel' }
            else { Write-SetupStep 'administrator' "Creating the administrator '$AdminUserName'" }
            Wait-PanelReady $client
            if ($backup) { return @{ Certificate = $certificate; Node = $null; Sftp = $null; Port = $Port } }

            Write-Step "Creating the administrator '$AdminUserName'"
            $null = Invoke-Panel $client GET '/api/v1/auth/session'
            $null = Invoke-Panel $client POST '/api/v1/auth/setup' @{ token = $setupToken; userName = $AdminUserName; password = (ConvertFrom-Secure $AdminPassword); displayName = $null; email = $null }
            # The setup token is spent; keep no secret the panel no longer needs.
            $settings.Remove('InitialSetup')
            Write-PanelConfig $settings

            $node = $null
            if ($Mode -eq 'AllInOne') {
                $null = Invoke-Panel $client GET '/api/v1/auth/session'
                $node = Invoke-Panel $client POST '/api/v1/nodes' @{ name = $env:COMPUTERNAME }
                # The enrollment token, and the node code that carries it: never in the log.
                Register-Secret $node.enrollment.token
                if ($node.enrollment.PSObject.Properties['nodeCode'] -and $node.enrollment.nodeCode) { Register-Secret $node.enrollment.nodeCode }
            }

            # The SFTP server starts on its default port (2022); another port is an administrator's setting in the database.
            $null = Invoke-Panel $client GET '/api/v1/auth/session'
            if ($SftpPort -ne 2022) {
                Write-Step "Setting the SFTP port to $SftpPort"
                $null = Invoke-Panel $client PUT '/api/v1/sftp/settings' @{ enabled = $true; port = $SftpPort; publicHost = $null }
            }
            $sftp = Invoke-Panel $client GET '/api/v1/sftp'
        }
        finally {
            $client.Dispose()
        }
        return @{ Certificate = $certificate; Node = $node; Sftp = $sftp; Port = $Port }
    }
    catch {
        $failure = $_.Exception.Message
        $left = @(Undo-PanelInstall $madeService (-not $ConnectionString))
        $undone = "What this install had done to the panel is undone: its service, program and firewall rules$(if (-not $ConnectionString) { ", and the 'pulsar' database and role it made" })."
        if ($left.Count -gt 0) { $undone = "Undoing this install left $($left -join '; '): remove them before running this script again (docs/guides/install.md, ""Starting over"")." }
        throw (New-SetupError $ExitFailed "$failure$([Environment]::NewLine)$undone")
    }
}

function Write-SftpSummary($sftp) {
    Write-Host "SFTP: port $SftpPort (every user's panel user name and password; the panel's Account page shows their address)."
    if ($sftp -and $sftp.hostKeyFingerprint) {
        Write-Host "The SFTP host key, to check at the first connection: $($sftp.hostKeyFingerprint)"
    }
    else {
        Write-Host "The SFTP host key's fingerprint is in the panel's Account page (SFTP access)."
    }
}

function Update-Panel {
    Write-Step 'Updating the panel'
    Stop-Service $PanelService
    Copy-Program (Join-Path $PSScriptRoot 'panel') $PanelDir
    Copy-BackupScript
    Invoke-PanelMigrations
    Start-Panel
    Write-StaleBackupTasks
}

function Uninstall-Panel {
    Write-Step 'Removing the panel'
    Stop-Service $PanelService -ErrorAction SilentlyContinue
    $null = Invoke-Native $Sc delete $PanelService
    Get-NetFirewallRule -Name 'Pulsar-Panel-HTTPS' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    Get-NetFirewallRule -Name 'Pulsar-Panel-Agents' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    Get-NetFirewallRule -Name 'Pulsar-Panel-SFTP' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    Start-Sleep -Seconds 2
    Remove-Item $PanelDir -Recurse -Force -ErrorAction SilentlyContinue
}

# --- Agent ------------------------------------------------------------------------------------------------------

# PulsarAgent enroll's arguments, the token left out (it goes through the environment: the process list shows command
# lines). $panelCertificate: the SHA-256 of the panel's certificate the agent is to pin (a node code's, once the panel
# presented it and Assert-PinnableCertificate passed it), or nothing.
function Get-EnrollArguments([string] $panel, [string] $panelKey, [string] $panelCertificate, [string] $serversRoot) {
    $arguments = @('enroll', '--panel', $panel, '--panel-key', $panelKey)
    if ($panelCertificate) { $arguments += @('--panel-certificate', $panelCertificate) }
    if ($serversRoot) { $arguments += @('--servers-root', $serversRoot) }
    return , $arguments
}

function Install-Agent([string] $panel, [string] $token, [string] $panelKey, [string] $panelCertificate = '') {
    if (Test-Service $AgentService) { throw 'The agent is already installed on this machine. Use -Update to update it, or -Uninstall first.' }
    Write-Step "Installing the agent in $AgentDir"
    Copy-Program (Join-Path $PSScriptRoot 'agent') $AgentDir
    $arguments = Get-EnrollArguments $panel $panelKey $panelCertificate $ServersRoot
    Write-Step 'Enrolling this machine as a node (the agent installs and starts its service)'
    $env:PULSAR_ENROLLMENT_TOKEN = $token
    try { $result = Invoke-Native (Join-Path $AgentDir 'PulsarAgent.exe') @arguments }
    finally { Remove-Item Env:PULSAR_ENROLLMENT_TOKEN -ErrorAction SilentlyContinue }
    Write-Host $result.Output
    if ($result.ExitCode -ne 0) { throw 'The agent could not enroll (see above).' }
}

function Update-Agent {
    Write-Step 'Updating the agent (its game servers keep running in their host processes)'
    Stop-Service $AgentService
    Copy-Program (Join-Path $PSScriptRoot 'agent') $AgentDir
    Start-Service $AgentService
}

# The .NET runtimes the installed agent's program needs that $dotnetRoot lacks (removed from Apps before Pulsar, say):
# without them it cannot run. Nothing for a program that carries its own, or that cannot be read.
function Get-AgentMissingRuntimes([string] $dotnetRoot) {
    $config = Join-Path $AgentDir 'PulsarAgent.runtimeconfig.json'
    if (-not (Test-Path -LiteralPath $config)) { return }
    try { $needed = Read-RuntimeRequirements @(Get-Content -LiteralPath $config -Raw) $null }
    catch { return }
    Get-MissingRuntimes $needed (Get-InstalledRuntimes $dotnetRoot)
}

# Asks the running agent to stop its game servers gracefully as it stops, as unenroll does: the file it reads then (and
# deletes). Only for a running service, whose state folder exists once it has had a game server.
function Write-AgentStopServersMarker {
    if (Test-Path -LiteralPath (Split-Path -Parent $AgentStopServersMarker) -PathType Container) {
        [IO.File]::WriteAllText($AgentStopServersMarker, '')
    }
}

function Uninstall-Agent {
    Write-Step 'Removing the agent'
    $exe = Join-Path $AgentDir 'PulsarAgent.exe'
    $missing = @(Get-AgentMissingRuntimes (Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'dotnet'))
    if ((Test-Path $exe) -and $missing.Count -gt 0) {
        # Its program cannot run: the service goes (it could not start anyway), the program and its data stay to finish
        # with once .NET is back. A service still running is asked to stop its game servers first; the hosts one left
        # running are not ended from here: only the agent's program can tell them from other processes (its records,
        # checked against each host's process and pipe), so they are named for the administrator instead.
        $games = "game servers it left running keep running, each in a host process (PulsarAgent.exe, run from $(Join-Path $AgentData 'host-programs')), until you end them (Task Manager, Details: End process tree; their games end with them, unsaved) or unenroll does"
        $deleted = $null
        if (Test-Service $AgentService) {
            if ((Get-Service $AgentService).Status -in 'Running', 'StartPending') {
                Write-AgentStopServersMarker
                $games = "its service was asked to stop its game servers as it stopped (any it could not stop keep running, each in a host process: PulsarAgent.exe, run from $(Join-Path $AgentData 'host-programs'); end them in Task Manager)"
            }
            else { $games = "its service was not running: $games" }
            Stop-Service $AgentService -ErrorAction SilentlyContinue
            if ((Get-Service $AgentService).Status -eq 'Stopped') { Remove-Item -LiteralPath $AgentStopServersMarker -Force -ErrorAction SilentlyContinue }
            $deleted = Invoke-Native $Sc delete $AgentService
        }
        else { $games = "its service was gone already: $games" }
        Write-Warning "The agent's program cannot run: this machine lacks $($missing -join ', ') (removed before Pulsar?). Its service is stopped and removed; $games. Its program stays in $AgentDir, with its node key, configuration and game servers' Windows accounts: install the .NET 10 runtime again (https://dotnet.microsoft.com/download/dotnet/10.0), run & '$exe' unenroll in an elevated PowerShell, then delete $AgentDir. Until then the node stays in the panel, and its key keeps working until you delete the node in the panel: delete it there (docs/guides/install.md, Uninstalling)."
        if ($deleted -and $deleted.ExitCode -ne 0) { throw "The agent's service could not be removed (sc.exe delete: $($deleted.Output.Trim()))." }
        return
    }
    if (Test-Path $exe) {
        # Stops the game servers, deletes the node key and the configuration, and removes the service. When it cannot,
        # nothing is deleted: the program stays to try again with, as the agent MSI does.
        $result = Invoke-Native $exe unenroll
        Write-Host $result.Output
        if ($result.ExitCode -ne 0) {
            throw "The agent could not unenroll (exit code $($result.ExitCode), see above), so it is kept: fix what it reports and run -Uninstall again. docs/guides/install.md (Uninstalling) has a last resort."
        }
    }
    if (Test-Service $AgentService) {
        Stop-Service $AgentService -ErrorAction SilentlyContinue
        $null = Invoke-Native $Sc delete $AgentService
    }
    Start-Sleep -Seconds 2
    Remove-Item $AgentDir -Recurse -Force -ErrorAction SilentlyContinue
}

# --- Secrets given outside the command line ---------------------------------------------------------------------

# Who besides SYSTEM and Administrators may own or open a file, by its permissions ($acl, as Get-Acl reads them): what
# makes a file unfit to hold secrets (the secrets file, PostgreSQL's options file). Nothing when nobody may.
function Get-PrivateFileProblems([Security.AccessControl.FileSystemSecurity] $acl) {
    $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier])
    if ($null -eq $owner -or $owner.Value -notin 'S-1-5-18', 'S-1-5-32-544') {
        "it is owned by $(if ($null -ne $owner) { Get-AccountName $owner } else { 'nobody' })"
    }
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Value -notin 'S-1-5-18', 'S-1-5-32-544') {
            "$(Get-AccountName $rule.IdentityReference) may open it"
        }
    }
}

# Writes a new file in one of this script's private folders, which it inherits (SYSTEM and Administrators only): never
# one that is there already (or a link left there), and checked once written.
function Write-PrivateFile([string] $path, [string] $text) {
    $stream = New-Object IO.FileStream($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $bytes = (New-Object Text.UTF8Encoding $false).GetBytes($text)
        $stream.Write($bytes, 0, $bytes.Length)
    }
    finally { $stream.Dispose() }
    $problems = @(Get-PrivateFileProblems (Get-Acl -LiteralPath $path))
    if ($problems.Count -gt 0) {
        Remove-Item -LiteralPath $path -Force
        throw "$path came out readable by others ($($problems -join '; ')): its folder is not this script's own."
    }
}

# Reads the secrets file once and deletes it: a file only SYSTEM and Administrators may own or open, in a folder only
# they may change (whoever could replace it would choose the administrator's password), at most 64 KiB of UTF-8 JSON
# whose names are $SecretInputs' and whose values are text.
function Read-SecretsFile([string] $path) {
    $full = [IO.Path]::GetFullPath($path)
    $item = Get-Item -LiteralPath $full -Force -ErrorAction SilentlyContinue
    if ($item -isnot [IO.FileInfo] -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw (New-SetupError $ExitUsage "The secrets file $full is not a file (missing, a folder or a link). Nothing was changed.")
    }
    $problems = @(Get-PrivateFileProblems (Get-Acl -LiteralPath $full)) + @(Find-UntrustedWriters $full)
    if ($problems.Count -gt 0) {
        throw (New-SetupError $ExitUsage "The secrets file $full must be one only SYSTEM and Administrators may own, open or replace, and it is not:$([Environment]::NewLine)  $(@($problems | Select-Object -First 10) -join "$([Environment]::NewLine)  ")$([Environment]::NewLine)It was not read. Nothing was changed.")
    }
    if ($item.Length -gt 64KB) { throw (New-SetupError $ExitUsage "The secrets file $full is larger than 64 KiB. Nothing was changed.") }
    $bytes = [IO.File]::ReadAllBytes($full)
    try { Remove-Item -LiteralPath $full -Force }
    catch { Write-Warning "The secrets file $full could not be deleted ($($_.Exception.Message)): delete it yourself." }
    $start = 0
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { $start = 3 }
    try { $text = (New-Object Text.UTF8Encoding $false, $true).GetString($bytes, $start, $bytes.Length - $start) }
    catch { throw (New-SetupError $ExitUsage "The secrets file $full is not UTF-8 text. Nothing was changed.") }
    $values = ConvertFrom-FlatJson $text "The secrets file $full"
    foreach ($name in $values.Keys) {
        if ($name -cnotin @($SecretInputs.Keys)) {
            throw (New-SetupError $ExitUsage "The secrets file $full names '$(ConvertTo-PrintableText $name)', which is none of $(@($SecretInputs.Keys) -join ', '). Nothing was changed.")
        }
    }
    return $values
}

# The secrets this run may need, from -SecretsFile (or PULSAR_SECRETS_FILE) and the environment ($SecretInputs), the
# file's first. Each variable is removed from this process's environment as it is read, so that nothing this script
# starts (installers, psql, the agent) inherits it.
function Read-SetupInputs {
    $fromEnvironment = @{}
    foreach ($name in $SecretInputs.Keys) {
        $value = [Environment]::GetEnvironmentVariable($SecretInputs[$name], 'Process')
        if ($null -ne $value) {
            [Environment]::SetEnvironmentVariable($SecretInputs[$name], $null, 'Process')
            if ($value) { $fromEnvironment[$name] = $value }
        }
    }
    $file = $SecretsFile
    $fileVariable = [Environment]::GetEnvironmentVariable('PULSAR_SECRETS_FILE', 'Process')
    if ($null -ne $fileVariable) {
        [Environment]::SetEnvironmentVariable('PULSAR_SECRETS_FILE', $null, 'Process')
        if (-not $file) { $file = $fileVariable }
    }
    $inputs = @{}
    if ($file) {
        $read = Read-SecretsFile $file
        foreach ($name in $read.Keys) { if ($null -ne $read[$name]) { $inputs[$name] = [string]$read[$name] } }
    }
    foreach ($name in $fromEnvironment.Keys) { if (-not $inputs.ContainsKey($name)) { $inputs[$name] = $fromEnvironment[$name] } }
    foreach ($value in $inputs.Values) { Register-Secret $value }
    return $inputs
}

# Takes the secrets read outside the command line where the parameters left them out, and registers every secret this
# run has, so that none is written out.
function Use-SetupInputs([hashtable] $inputs) {
    if (-not $AdminPassword -and $inputs['adminPassword']) { $script:AdminPassword = ConvertTo-Secure $inputs['adminPassword'] }
    if (-not $PostgresPassword -and $inputs['postgresPassword']) { $script:PostgresPassword = ConvertTo-Secure $inputs['postgresPassword'] }
    if (-not $CertificatePassword -and $inputs['certificatePassword']) { $script:CertificatePassword = ConvertTo-Secure $inputs['certificatePassword'] }
    if (-not $ConnectionString -and $inputs['connectionString']) { $script:ConnectionString = $inputs['connectionString'] }
    if (-not $Token -and $inputs['enrollmentToken']) { $script:Token = $inputs['enrollmentToken'] }
    if (-not $NodeCode -and $inputs['nodeCode']) { $script:NodeCode = $inputs['nodeCode'] }
    foreach ($secure in @($AdminPassword, $PostgresPassword, $CertificatePassword)) { if ($secure) { Register-Secret (ConvertFrom-Secure $secure) } }
    foreach ($text in @($ConnectionString, $Token, $NodeCode)) { Register-Secret $text }
    if ($ConnectionString) {
        foreach ($part in $ConnectionString.Split(';')) { if ($part -match '^\s*(password|pwd|psw)\s*=\s*(.*?)\s*$') { Register-Secret $Matches[2] } }
    }
}

# Asks for a secret, or, with -Unattended (nothing may be asked), says where it should have come from.
function Read-Secret([string] $prompt, [string] $name) {
    if ($Unattended) {
        $what = $prompt -replace ' \(.*\z', ''
        $variable = $SecretInputs[$name]
        throw (New-SetupError $ExitUsage "$what is needed, and nothing may be asked (-Unattended): give it in the environment variable $variable or as ""$name"" in the secrets file (-SecretsFile). Nothing was changed.")
    }
    $value = Read-Host -AsSecureString $prompt
    Register-Secret (ConvertFrom-Secure $value)
    return $value
}

# --- JSON ---------------------------------------------------------------------------------------------------------

# A JSON object whose values are text or null and nothing else (no numbers, lists, nesting or repeated names), as a
# dictionary with case-sensitive names: the node code and the secrets file. Strict, so that what is read is what was
# meant: anything else is refused, never guessed at.
function ConvertFrom-FlatJson([string] $text, [string] $what) {
    $space = '[ \t\r\n]*'
    $member = $space + '(' + $JsonStringPattern + ')' + $space + ':' + $space + '(' + $JsonStringPattern + '|null)' + $space
    $timeout = [TimeSpan]::FromSeconds(2)
    $object = '^' + $space + '\{(?:' + $member + '(?:,' + $member + ')*|' + $space + ')\}' + $space + '\z'
    if (-not [regex]::new($object, 'None', $timeout).IsMatch($text)) {
        throw (New-SetupError $ExitUsage "$what is not a JSON object of names and text.")
    }
    $values = New-Object 'Collections.Generic.Dictionary[string,object]' ([StringComparer]::Ordinal)
    foreach ($match in [regex]::new('\G' + $space + '[{,]' + $member, 'None', $timeout).Matches($text)) {
        $name = ConvertFrom-JsonString $match.Groups[1].Value $what
        if ($values.ContainsKey($name)) { throw (New-SetupError $ExitUsage "$what names '$(ConvertTo-PrintableText $name)' more than once.") }
        $value = $null
        if ($match.Groups[2].Value -cne 'null') { $value = ConvertFrom-JsonString $match.Groups[2].Value $what }
        $values[$name] = $value
    }
    return , $values
}

# The text of a JSON string as $JsonStringPattern matched it (quotes included): JSON's escapes, and no half of a
# UTF-16 surrogate pair alone.
function ConvertFrom-JsonString([string] $quoted, [string] $what) {
    $escapes = @{ '"' = '"'; '\' = '\'; '/' = '/'; 'b' = [string][char]8; 'f' = [string][char]12; 'n' = "`n"; 'r' = "`r"; 't' = "`t" }
    $value = [regex]::Replace($quoted.Substring(1, $quoted.Length - 2), '\\(?:u([0-9A-Fa-f]{4})|(.))', {
            param($escape)
            if ($escape.Groups[1].Success) { return [string][char][Convert]::ToInt32($escape.Groups[1].Value, 16) }
            return $escapes[$escape.Groups[2].Value]
        })
    try { $null = (New-Object Text.UTF8Encoding $false, $true).GetBytes($value) }
    catch { throw (New-SetupError $ExitUsage "$what holds text that is not Unicode (half of a surrogate pair).") }
    return $value
}

# A JSON document of any size as dictionaries (names compared as written) and arrays: Microsoft's release metadata is
# over a megabyte, and Windows PowerShell's ConvertFrom-Json has a size limit of its own.
function ConvertFrom-JsonDocument([string] $json) {
    try {
        if ($PSVersionTable.PSEdition -eq 'Desktop') {
            Add-Type -AssemblyName System.Web.Extensions
            $serializer = New-Object System.Web.Script.Serialization.JavaScriptSerializer
            $serializer.MaxJsonLength = [int]::MaxValue
            $serializer.RecursionLimit = 64
            return , $serializer.DeserializeObject($json)
        }
        return , (ConvertFrom-Json $json -AsHashtable -NoEnumerate)
    }
    catch {
        throw (New-SetupError $ExitUntrusted "A downloaded document is not JSON ($($_.Exception.GetBaseException().Message)). Nothing was changed.")
    }
}

# A value of a JSON object read by ConvertFrom-JsonDocument, or $null when it has none (or is not an object).
function Get-JsonValue($object, [string] $name) {
    if ($object -is [Collections.IDictionary] -and @($object.Keys) -ccontains $name) { return , $object[$name] }
    return $null
}

# --- Node codes -----------------------------------------------------------------------------------------------------

function New-NodeCodeError([string] $why) {
    return New-SetupError $ExitUsage "This is not a node code the panel makes ($why). Copy it again from the panel (Nodes > Add node). Nothing was changed."
}

# Base64url without padding or whitespace, in its one canonical spelling (the bits past the last byte are zero): its
# bytes, or $null for anything else. As NodeCode.TryDecode reads it.
function ConvertFrom-Base64Url([string] $text) {
    if ($text -cnotmatch '^[A-Za-z0-9_-]*\z' -or $text.Length % 4 -eq 1) { return $null }
    $base64 = $text.Replace('-', '+').Replace('_', '/')
    $base64 += '=' * ((4 - $base64.Length % 4) % 4)
    try { $bytes = [Convert]::FromBase64String($base64) }
    catch { return $null }
    if ([Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') -cne $text) { return $null }
    return , $bytes
}

# 32 random bytes (an enrollment token's) or a SHA-256 (a key's fingerprint) in canonical base64url: 43 characters.
function Test-RandomPart([string] $text) {
    if ($text.Length -ne 43) { return $false }
    $bytes = ConvertFrom-Base64Url $text
    return $null -ne $bytes -and $bytes.Length -eq 32
}

# Whether $url is a panel address a node code may carry (NodeCode.IsValidUrl): an origin (scheme://host[:port], in
# lower case, without a trailing slash, path, query, fragment or credentials, and no default port) in printable
# ASCII, at most $NodeCodeMaxUrlLength characters, over https; plain http only for localhost or a loopback address,
# as the agent accepts it.
function Test-PanelUrl([string] $url) {
    if (-not $url -or $url.Length -gt $NodeCodeMaxUrlLength -or $url -cnotmatch '^[\x21-\x7E]+\z') { return $false }
    $uri = $null
    if (-not [Uri]::TryCreate($url, [UriKind]::Absolute, [ref] $uri)) { return $false }
    if ($uri.UserInfo -or $uri.AbsolutePath -cne '/' -or $uri.Query -or $uri.Fragment) { return $false }
    # The origin as .NET writes it (Uri.GetLeftPart): Windows PowerShell's Uri writes an IPv6 address in full, .NET's
    # in its short form, which IPAddress writes too.
    $hostText = $uri.Host
    if ($uri.HostNameType -eq [UriHostNameType]::IPv6) { $hostText = '[' + [Net.IPAddress]::Parse($uri.DnsSafeHost).ToString() + ']' }
    $origin = $uri.Scheme + '://' + $hostText
    if (-not $uri.IsDefaultPort) { $origin += ':' + $uri.Port }
    if ($origin.ToLowerInvariant() -cne $url) { return $false }
    if ($uri.Scheme -ceq 'https') { return $true }
    if ($uri.Scheme -cne 'http') { return $false }
    # "localhost" or a loopback address, never a name a name server answers for ("loopback", which Uri also counts).
    if ($uri.HostNameType -eq [UriHostNameType]::IPv4 -or $uri.HostNameType -eq [UriHostNameType]::IPv6) {
        $address = $null
        return [Net.IPAddress]::TryParse($uri.DnsSafeHost, [ref] $address) -and [Net.IPAddress]::IsLoopback($address)
    }
    return $uri.Host -ceq 'localhost'
}

# The node code the panel's Add node dialog shows: $NodeCodePrefix and base64url (no padding) of UTF-8 JSON
# {"url": the panel's origin, "token": an enrollment token, "key": the panel key's fingerprint, "cert": the lowercase
# hexadecimal SHA-256 of the panel's TLS certificate (its DER), or null when the panel cannot tell which certificate its
# clients see}. Read as strictly as src/Pulsar.Contracts/NodeCode.cs reads it: at most $NodeCodeMaxLength characters
# (before decoding), exactly that prefix, canonical base64url, a JSON object (nothing nested) with exactly those four
# members, each once and well formed (Test-PanelUrl; a single-use token or one with $ReusableTokenPrefix; "sha256:"
# and a fingerprint; 64 lowercase hexadecimal digits or null). Anything else is refused. Spaces around it are a copy's.
function ConvertFrom-NodeCode([string] $code) {
    $code = "$code".Trim()
    if ($code.Length -gt $NodeCodeMaxLength) { throw (New-NodeCodeError "longer than $NodeCodeMaxLength characters") }
    if (-not $code.StartsWith($NodeCodePrefix, [StringComparison]::Ordinal)) { throw (New-NodeCodeError "it does not start with $NodeCodePrefix") }
    $bytes = ConvertFrom-Base64Url $code.Substring($NodeCodePrefix.Length)
    if ($null -eq $bytes -or $bytes.Length -eq 0) { throw (New-NodeCodeError 'its data is not canonical base64url') }
    try { $json = (New-Object Text.UTF8Encoding $false, $true).GetString($bytes) }
    catch { throw (New-NodeCodeError 'its data is not UTF-8 text') }
    try { $fields = ConvertFrom-FlatJson $json 'The node code' }
    catch { throw (New-NodeCodeError 'its data is not a JSON object of names and text') }
    $names = @('url', 'token', 'key', 'cert')
    if ($fields.Count -ne $names.Count -or @($names | Where-Object { -not $fields.ContainsKey($_) }).Count -gt 0) {
        throw (New-NodeCodeError "it must hold $($names -join ', ') and nothing else")
    }
    $url = $fields['url']
    $token = $fields['token']
    $key = $fields['key']
    $hash = $fields['cert']
    if ($url -isnot [string] -or -not (Test-PanelUrl $url)) { throw (New-NodeCodeError 'its url is not a panel''s https:// origin') }
    $tokenPart = $token
    if ($token -is [string] -and $token.StartsWith($ReusableTokenPrefix, [StringComparison]::Ordinal)) { $tokenPart = $token.Substring($ReusableTokenPrefix.Length) }
    if ($token -isnot [string] -or -not (Test-RandomPart $tokenPart)) { throw (New-NodeCodeError 'its token is not an enrollment token') }
    if ($key -isnot [string] -or -not $key.StartsWith('sha256:', [StringComparison]::Ordinal) -or -not (Test-RandomPart $key.Substring(7))) { throw (New-NodeCodeError 'its key is not a panel key''s fingerprint') }
    if ($null -ne $hash -and ($hash -isnot [string] -or $hash -cnotmatch '^[0-9a-f]{64}\z')) { throw (New-NodeCodeError 'its cert is not a lowercase SHA-256') }
    return [pscustomobject]@{ Url = $url; Token = $token; PanelKey = $key; CertificateHash = $hash }
}

# The lowercase hexadecimal SHA-256 of a certificate's encoding (DER), as a node code pins it.
function Get-CertificateHash([Security.Cryptography.X509Certificates.X509Certificate2] $certificate) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return (($sha.ComputeHash($certificate.RawData) | ForEach-Object { $_.ToString('x2') }) -join '') }
    finally { $sha.Dispose() }
}

# Reads the certificate the panel at $uri presents, without trusting it: the TLS handshake is given up as soon as the
# certificate arrives, before anything is sent. Also whether this machine already trusts it for that host (its chain
# and its names, as Windows checks them).
function Read-PanelCertificate([Uri] $uri) {
    Initialize-SetupNetwork
    try { $probe = [PulsarTlsProbe]::Read($uri.DnsSafeHost, $uri.Port, 15000) }
    catch {
        throw (New-SetupError $ExitOffline "The panel at $($uri.GetLeftPart('Authority')) could not be reached ($($_.Exception.GetBaseException().Message)). Check its address and that this machine reaches it. Nothing was changed.")
    }
    return [pscustomobject]@{
        Certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new([byte[]]$probe.Certificate)
        Trusted = ($probe.Errors -eq [Net.Security.SslPolicyErrors]::None)
        Errors = "$($probe.Errors)"
    }
}

# What to do with the certificate the panel presents ($presented, from Read-PanelCertificate) for a node code ($code,
# from ConvertFrom-NodeCode): 'trusted' when this machine trusts it already (the agent checks it as it checks any
# site's), 'pin' when it is the one the code names and may be pinned (Assert-PinnableCertificate): the agent is then
# enrolled with --panel-certificate and accepts it alone. No certificate store is ever changed for a code. Anything
# else stops the run, before anything changes.
function Resolve-NodeCertificate($code, $presented, [string] $hostName) {
    $hash = Get-CertificateHash $presented.Certificate
    if ($null -eq $code.CertificateHash) {
        if ($presented.Trusted) { return 'trusted' }
        throw (New-SetupError $ExitCertificate "This machine does not trust the panel's certificate for $hostName ($($presented.Errors)), and the node code names none (the panel could not tell which certificate its clients see: a proxy in front of it, or plain HTTP). Install the certificate's authority on this machine, or enroll with -PanelUrl, -Token, -PanelKey and -TrustCertificate instead of a code. Nothing was changed.")
    }
    if ($hash -cne $code.CertificateHash) {
        throw (New-SetupError $ExitCertificate "The panel at $hostName presents a certificate whose SHA-256 is $hash, not the $($code.CertificateHash) the node code names: this address leads to something else than the panel that made the code, or to a proxy in front of it that serves another certificate (one that passes the requests on without saying so), or the panel's certificate changed since. Check the address, or make a new code in the panel, opened at the address nodes use. Behind such a proxy, give its certificate's authority to this machine, or enroll with -PanelUrl, -Token, -PanelKey and -TrustCertificate instead of a code. Nothing was changed.")
    }
    if ($presented.Trusted) { return 'trusted' }
    try { Assert-PinnableCertificate $presented.Certificate $hostName }
    catch { throw (New-SetupError $ExitCertificate "$($_.Exception.Message) Nothing was changed.") }
    return 'pin'
}

# --- Downloads and signatures -------------------------------------------------------------------------------------

# HTTPS downloads (TLS 1.2, or 1.3 where Windows has it) that follow only redirects to other HTTPS addresses, with
# their size capped, and the reading of a server's TLS certificate without trusting it.
function Initialize-SetupNetwork {
    if ('PulsarDownloads' -as [type]) { return }
    $source = @'
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public static class PulsarDownloads
{
    public static void UseTls()
    {
        SecurityProtocolType protocols = SecurityProtocolType.Tls12;
        // TLS 1.3 (12288) where Windows has it: Windows 11 and Windows Server 2022 or later.
        if (Environment.OSVersion.Version.Build >= 20348) { protocols |= (SecurityProtocolType)12288; }
        try { ServicePointManager.SecurityProtocol = protocols; }
        catch (NotSupportedException) { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12; }
    }

    private static HttpClient NewClient()
    {
        HttpClientHandler handler = new HttpClientHandler();
        handler.AllowAutoRedirect = false;
        handler.UseCookies = false;
        HttpClient client = new HttpClient(handler);
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Pulsar-Install/1");
        return client;
    }

    private static Uri Https(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps) { throw new HttpRequestException("refused an address that is not HTTPS: " + uri.OriginalString); }
        return uri;
    }

    // Up to 5 redirects, each to an HTTPS address; 60 seconds for each answer's headers.
    private static HttpResponseMessage Send(HttpClient client, HttpMethod method, string url)
    {
        Uri uri = Https(new Uri(url, UriKind.Absolute));
        for (int hop = 0; hop <= 5; hop++)
        {
            HttpResponseMessage response;
            using (CancellationTokenSource timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
            {
                try { response = client.SendAsync(new HttpRequestMessage(method, uri), HttpCompletionOption.ResponseHeadersRead, timeout.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { throw new HttpRequestException(uri.Host + " did not answer within 60 seconds"); }
            }
            int status = (int)response.StatusCode;
            if (status >= 300 && status <= 399 && response.Headers.Location != null)
            {
                Uri next = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(uri, response.Headers.Location);
                response.Dispose();
                uri = Https(next);
                continue;
            }
            return response;
        }
        throw new HttpRequestException("too many redirects from " + url);
    }

    public static int Probe(string url)
    {
        using (HttpClient client = NewClient())
        using (HttpResponseMessage response = Send(client, HttpMethod.Head, url))
        {
            return (int)response.StatusCode;
        }
    }

    public static string GetText(string url, long maxBytes)
    {
        using (MemoryStream buffer = new MemoryStream())
        {
            Fetch(url, buffer, maxBytes);
            return new UTF8Encoding(false, true).GetString(buffer.ToArray());
        }
    }

    public static void Download(string url, string path, long maxBytes)
    {
        using (FileStream file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            Fetch(url, file, maxBytes);
        }
    }

    // How long a download of this many bytes may take in all: 10 minutes, and a second more for every 128 KiB (about
    // 1 Mbit/s), so that a server sending a byte now and then cannot hold the run for ever.
    public static TimeSpan TimeLimit(long bytes)
    {
        return TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(Math.Max(0, bytes) / (128 * 1024));
    }

    private static void Fetch(string url, Stream target, long maxBytes)
    {
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        using (HttpClient client = NewClient())
        using (HttpResponseMessage response = Send(client, HttpMethod.Get, url))
        {
            if (response.StatusCode != HttpStatusCode.OK) { throw new HttpRequestException(url + " answered " + (int)response.StatusCode + " " + response.ReasonPhrase); }
            long? announced = response.Content.Headers.ContentLength;
            if (announced.HasValue && announced.Value > maxBytes) { throw new IOException(url + " is larger than " + maxBytes + " bytes"); }
            TimeSpan limit = TimeLimit(announced.HasValue ? announced.Value : maxBytes);
            using (Stream source = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
            {
                byte[] chunk = new byte[81920];
                long total = 0;
                while (true)
                {
                    TimeSpan left = limit - clock.Elapsed;
                    if (left <= TimeSpan.Zero)
                    {
                        response.Dispose();
                        throw new IOException("the download from " + url + " took longer than " + (int)limit.TotalMinutes + " minutes");
                    }
                    Task<int> read = source.ReadAsync(chunk, 0, chunk.Length);
                    if (!read.Wait(left < TimeSpan.FromSeconds(60) ? left : TimeSpan.FromSeconds(60)))
                    {
                        response.Dispose();
                        if (clock.Elapsed >= limit) { throw new IOException("the download from " + url + " took longer than " + (int)limit.TotalMinutes + " minutes"); }
                        throw new IOException("the download from " + url + " stalled for 60 seconds");
                    }
                    int count = read.Result;
                    if (count == 0) { break; }
                    total += count;
                    if (total > maxBytes) { throw new IOException(url + " is larger than " + maxBytes + " bytes"); }
                    target.Write(chunk, 0, count);
                }
                if (announced.HasValue && total != announced.Value) { throw new IOException("the download from " + url + " ended early"); }
            }
        }
    }
}

public sealed class PulsarTlsProbe
{
    public byte[] Certificate;
    public SslPolicyErrors Errors;

    // Connects, takes the certificate the server presents and gives the handshake up: nothing is sent over it.
    public static PulsarTlsProbe Read(string host, int port, int timeoutMilliseconds)
    {
        PulsarTlsProbe result = new PulsarTlsProbe();
        using (TcpClient tcp = new TcpClient(AddressFamily.InterNetworkV6))
        {
            tcp.Client.DualMode = true;
            Task connect = tcp.ConnectAsync(host, port);
            if (!connect.Wait(timeoutMilliseconds)) { throw new TimeoutException("no connection within " + (timeoutMilliseconds / 1000) + " seconds"); }
            using (SslStream ssl = new SslStream(tcp.GetStream(), false, (sender, certificate, chain, errors) =>
            {
                if (certificate != null && result.Certificate == null)
                {
                    result.Certificate = certificate.GetRawCertData();
                    result.Errors = errors;
                }
                return false;
            }))
            {
                ssl.ReadTimeout = timeoutMilliseconds;
                ssl.WriteTimeout = timeoutMilliseconds;
                try { ssl.AuthenticateAsClient(host, null, SslProtocols.Tls12, false); }
                catch (AuthenticationException) { }
                catch (IOException) { }
            }
        }
        if (result.Certificate == null) { throw new IOException("no TLS certificate came from " + host + ":" + port); }
        return result;
    }
}
'@
    if ($PSVersionTable.PSEdition -eq 'Desktop') { Add-Type -TypeDefinition $source -ReferencedAssemblies System.Net.Http }
    else { Add-Type -TypeDefinition $source }
    [PulsarDownloads]::UseTls()
}

# A new private folder for this run's downloads, in $SetupRoot (SYSTEM and Administrators only, as every file put
# there): nobody else can replace an installer between its checks and its run. Deleted when the run ends.
function New-SetupDownloads {
    if ($SetupDownloads) { return }
    Initialize-SetupNetwork
    # Left by a run that was stopped: this script's own.
    Get-ChildItem -LiteralPath $SetupRoot -Directory -Filter 'downloads-*' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    $folder = Join-Path $SetupRoot ('downloads-' + [Guid]::NewGuid().ToString('N'))
    New-SetupFolder $folder
    $script:SetupDownloads = $folder
}

function Remove-SetupDownloads {
    if (-not $SetupDownloads -or -not (Test-Path -LiteralPath $SetupDownloads)) { return }
    try { Remove-Item -LiteralPath $SetupDownloads -Recurse -Force }
    catch { Write-SetupLog "The downloads in $SetupDownloads could not all be deleted ($($_.Exception.Message)); the next run deletes them." }
}

# A small document over HTTPS ($maxBytes at most), as text.
function Get-SetupText([string] $url, [long] $maxBytes) {
    Initialize-SetupNetwork
    try { return [PulsarDownloads]::GetText($url, $maxBytes) }
    catch { throw (New-SetupError $ExitOffline "$url could not be read ($($_.Exception.GetBaseException().Message))") }
}

# Downloads $url over HTTPS into the private downloads folder as $name ($maxBytes at most): its path.
function Save-SetupDownload([string] $url, [string] $name, [long] $maxBytes) {
    $path = Join-Path $SetupDownloads $name
    try { [PulsarDownloads]::Download($url, $path, $maxBytes) }
    catch {
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
        throw (New-SetupError $ExitOffline "$url could not be downloaded ($($_.Exception.GetBaseException().Message))")
    }
    return $path
}

# Copies an installer the administrator gave (-PostgresInstaller, -DotNetInstallers) into the private downloads
# folder: what is checked and run is that copy, which nobody else can change.
function Copy-SetupInstaller([string] $path) {
    $source = (Resolve-Path -LiteralPath $path).ProviderPath
    $copy = Join-Path $SetupDownloads (Split-Path -Leaf $source)
    [IO.File]::Copy($source, $copy, $false)
    return $copy
}

# The attributes of a certificate's subject or issuer ($distinguishedName: its SubjectName or IssuerName), read from
# its encoding (the text form quotes and escapes as Windows sees fit): one [pscustomobject]@{ Type; Value } each, the
# type 'CN', 'C', 'L', 'S', 'O' or 'OU', or the attribute's identifier for others.
function Get-NameAttributes([Security.Cryptography.X509Certificates.X500DistinguishedName] $distinguishedName) {
    $types = @{ '2.5.4.3' = 'CN'; '2.5.4.6' = 'C'; '2.5.4.7' = 'L'; '2.5.4.8' = 'S'; '2.5.4.10' = 'O'; '2.5.4.11' = 'OU' }
    $data = $distinguishedName.RawData
    $name = Read-DerElement $data 0
    if ($name.Tag -ne 0x30 -or $name.Next -ne $data.Length) { throw 'The certificate is malformed.' }
    $position = $name.Start
    while ($position -lt $name.Next) {
        $set = Read-DerElement $data $position
        if ($set.Tag -ne 0x31 -or $set.Next -gt $name.Next) { throw 'The certificate is malformed.' }
        $inner = $set.Start
        while ($inner -lt $set.Next) {
            $attribute = Read-DerElement $data $inner
            if ($attribute.Tag -ne 0x30 -or $attribute.Next -gt $set.Next) { throw 'The certificate is malformed.' }
            $oid = Read-DerElement $data $attribute.Start
            if ($oid.Tag -ne 0x06) { throw 'The certificate is malformed.' }
            $value = Read-DerElement $data $oid.Next
            if ($value.Next -ne $attribute.Next) { throw 'The certificate is malformed.' }
            # 2.5.4.x: the attributes above, all three bytes long (55 04 x).
            $type = $null
            if ($oid.Length -eq 3 -and $data[$oid.Start] -eq 0x55 -and $data[$oid.Start + 1] -eq 0x04) { $type = $types["2.5.4.$($data[$oid.Start + 2])"] }
            if (-not $type) {
                $oidBytes = New-Object byte[] $oid.Length
                [Array]::Copy($data, $oid.Start, $oidBytes, 0, $oid.Length)
                $type = 'oid:' + [BitConverter]::ToString($oidBytes)
            }
            $bytes = New-Object byte[] $value.Length
            [Array]::Copy($data, $value.Start, $bytes, 0, $value.Length)
            $text = switch ($value.Tag) {
                0x0C { (New-Object Text.UTF8Encoding $false, $true).GetString($bytes) }
                { $_ -in 0x13, 0x14, 0x16 } { [Text.Encoding]::GetEncoding(28591).GetString($bytes) }
                0x1E { [Text.Encoding]::BigEndianUnicode.GetString($bytes) }
                default { $null }
            }
            # Only an attribute checked below must be text; another (an e-mail address, a serial number) may be anything.
            if ($null -eq $text -and -not $type.StartsWith('oid:')) { throw 'The certificate is malformed.' }
            [pscustomobject]@{ Type = $type; Value = $text }
            $inner = $attribute.Next
        }
        $position = $set.Next
    }
}

# The attributes of $attributes (Get-NameAttributes) that are not as $expected says (type -> value, each exactly once
# and exactly that, case included), as "type=value" to print. Nothing when all are; other types are not looked at.
function Find-NameMismatches($attributes, $expected) {
    foreach ($type in $expected.Keys) {
        $found = @($attributes | Where-Object { $_.Type -ceq $type })
        if ($found.Count -ne 1 -or $found[0].Value -cne $expected[$type]) {
            "$type=$(if ($found.Count -eq 0) { '(none)' } else { (@($found | ForEach-Object { ConvertTo-PrintableText $_.Value }) -join ' and ') })"
        }
    }
}

# The chain Windows builds for an installer's signer ($certificate, from $path's signature), from the certificates the
# signature carries and the machine's stores, as the thumbprints from the signer to its root, what is wrong with it
# besides time (a signer certificate past its end still makes a valid signature when it was timestamped) and
# revocation, and whether a certificate in it is revoked: 'Revoked', 'Unknown' (its authority could not be asked: no
# internet, or no revocation address) or 'Good'. Revocation is checked here, online, for every certificate but the
# root: Get-AuthenticodeSignature does not check it (Windows PowerShell asks WinVerifyTrust for no revocation checks).
function Get-SignerChain([string] $path, [Security.Cryptography.X509Certificates.X509Certificate2] $certificate) {
    $carried = New-Object Security.Cryptography.X509Certificates.X509Certificate2Collection
    try { $carried.Import($path) } catch { Write-Verbose "No certificates read from the signature of ${path}: $($_.Exception.Message)" }
    $chain = New-Object Security.Cryptography.X509Certificates.X509Chain
    try {
        $chain.ChainPolicy.RevocationMode = [Security.Cryptography.X509Certificates.X509RevocationMode]::Online
        $chain.ChainPolicy.RevocationFlag = [Security.Cryptography.X509Certificates.X509RevocationFlag]::ExcludeRoot
        $chain.ChainPolicy.UrlRetrievalTimeout = [TimeSpan]::FromSeconds(30)
        $chain.ChainPolicy.ExtraStore.AddRange($carried)
        $null = $chain.Build($certificate)
        $statuses = @($chain.ChainStatus | ForEach-Object { "$($_.Status)" } | Select-Object -Unique)
        $revocation = 'Good'
        if ($statuses -contains 'Revoked') { $revocation = 'Revoked' }
        elseif ($statuses -contains 'RevocationStatusUnknown' -or $statuses -contains 'OfflineRevocation') { $revocation = 'Unknown' }
        $ignored = @('NoError', 'NotTimeValid', 'NotTimeNested', 'Revoked', 'RevocationStatusUnknown', 'OfflineRevocation')
        return [pscustomobject]@{
            Thumbprints = @($chain.ChainElements | ForEach-Object { $_.Certificate.Thumbprint })
            Problems = @($statuses | Where-Object { $_ -notin $ignored })
            Revocation = $revocation
        }
    }
    finally { $chain.Dispose() }
}

# Checks the signature of an installer before it runs ($signature: Get-AuthenticodeSignature of the file in the
# private downloads folder). It must be valid (the file is as its publisher signed it, and Windows chains the signer to
# a root it trusts), embedded in the file (not a catalog's), and by a certificate for signing code of $signer's: its
# subject's attributes as $signer.Subject says, issued by the authority $signer.Issuer names (when it names one), and
# with its chain ($chain, Get-SignerChain's) ending at one of $signer.Roots (when it lists any), through one of
# $signer.IssuerThumbprints (when it lists any). Nothing in that chain may be revoked; when that could not be checked
# (offline), a warning says so. $what names the file in the message.
function Assert-InstallerSignature($signature, $signer, [string] $what, $chain = $null) {
    $refused = "is not run. Nothing was changed."
    if ($null -eq $signature) { throw (New-SetupError $ExitUntrusted "$what has no signature that could be read, and $refused") }
    $status = "$($signature.Status)"
    if ($status -cne 'Valid') {
        $detail = ''
        if ($signature.PSObject.Properties['StatusMessage'] -and $signature.StatusMessage) { $detail = ": $($signature.StatusMessage)" }
        throw (New-SetupError $ExitUntrusted "$what is not validly signed ($status$detail), and $refused")
    }
    if ($signature.PSObject.Properties['SignatureType'] -and "$($signature.SignatureType)" -cne 'Authenticode') {
        throw (New-SetupError $ExitUntrusted "$what is not signed itself (its signature is a $($signature.SignatureType) one), and $refused")
    }
    $certificate = $signature.SignerCertificate
    if ($certificate -isnot [Security.Cryptography.X509Certificates.X509Certificate2]) { throw (New-SetupError $ExitUntrusted "$what names no signer, and $refused") }
    $subject = ConvertTo-PrintableText $certificate.Subject
    $uses = Get-CertificateUses $certificate
    if ($null -eq $uses -or $uses -notcontains $CodeSigning) {
        throw (New-SetupError $ExitUntrusted "$what is signed by $subject with a certificate that is not for signing code, and $refused")
    }
    $wrong = @(Find-NameMismatches @(Get-NameAttributes $certificate.SubjectName) $signer.Subject)
    if ($wrong.Count -gt 0) {
        $expected = @($signer.Subject.Keys | ForEach-Object { "$_=$($signer.Subject[$_])" }) -join ', '
        throw (New-SetupError $ExitUntrusted "$what is signed by $subject, not by $expected ($($wrong -join ', ')), and $refused")
    }
    if ($signer.Issuer) {
        $wrong = @(Find-NameMismatches @(Get-NameAttributes $certificate.IssuerName) $signer.Issuer)
        if ($wrong.Count -gt 0) {
            $expected = @($signer.Issuer.Keys | ForEach-Object { "$_=$($signer.Issuer[$_])" }) -join ', '
            throw (New-SetupError $ExitUntrusted "$what is signed with a certificate issued by $(ConvertTo-PrintableText $certificate.Issuer), not by $expected ($($wrong -join ', ')), and $refused")
        }
    }
    if (@($signer.Roots).Count -gt 0) {
        $root = $null
        if ($chain -and @($chain.Thumbprints).Count -ge 2) { $root = @($chain.Thumbprints)[-1] }
        if (-not $root -or @($chain.Problems).Count -gt 0 -or $root -notin @($signer.Roots)) {
            $found = 'no chain'
            if ($root) { $found = "a chain to $root$(if (@($chain.Problems).Count -gt 0) { " ($(@($chain.Problems) -join ', '))" })" }
            throw (New-SetupError $ExitUntrusted "$what is signed by $subject with a certificate that does not chain up to its publisher's own root ($(@($signer.Roots) -join ' or ')): $found, and $refused")
        }
        if (@($signer.IssuerThumbprints).Count -gt 0 -and @($chain.Thumbprints)[1] -notin @($signer.IssuerThumbprints)) {
            throw (New-SetupError $ExitUntrusted "$what is signed by $subject with a certificate issued by $(@($chain.Thumbprints)[1]), not by its publisher's authority ($(@($signer.IssuerThumbprints) -join ' or ')), and $refused")
        }
    }
    if ($chain) {
        if ($chain.Revocation -ceq 'Revoked') {
            throw (New-SetupError $ExitUntrusted "$what is signed by $subject with a certificate that is revoked (it, or an authority's above it), and $refused")
        }
        if ($chain.Revocation -cne 'Good') {
            Write-Warning "Whether the certificate that signed $what (by $subject) has been revoked could not be checked (its authority did not answer: no internet?). It is run on the strength of its other checks."
        }
    }
}

# Checks that an installer's version information ($versionInfo: its file's VersionInfo) is that of $product (its
# ProductName, exactly, or containing it with -contains) at $version: its ProductVersion or FileVersion starts with
# $version, followed by nothing or by what is not a digit ("10.0.12" for "10.0.12.36422", "18.6" for "18.6-1").
function Assert-InstallerVersion($versionInfo, [string] $product, [string] $version, [string] $what, [switch] $contains) {
    $name = "$($versionInfo.ProductName)"
    $nameMatches = if ($contains) { $name.IndexOf($product, [StringComparison]::OrdinalIgnoreCase) -ge 0 } else { $name -ceq $product }
    $pattern = '^' + [regex]::Escape($version) + '(\z|[^0-9])'
    $versionMatches = "$($versionInfo.ProductVersion)" -cmatch $pattern -or "$($versionInfo.FileVersion)" -cmatch $pattern
    if (-not $nameMatches -or -not $versionMatches) {
        throw (New-SetupError $ExitUntrusted "$what says it is '$(ConvertTo-PrintableText $name)' $(ConvertTo-PrintableText "$($versionInfo.ProductVersion)"), not $product $version, and is not run. Nothing was changed.")
    }
}

# The oldest PostgreSQL release a release of Pulsar installs ($release: the text of its release.json, Build-Release.ps1's,
# or $null): its postgresMinimum ("18.6"), or $null when it records none.
function Read-PostgresMinimum([string] $release) {
    if (-not $release) { return $null }
    $parsed = $release | ConvertFrom-Json
    if (-not $parsed.PSObject.Properties['postgresMinimum']) { return $null }
    $minimum = $parsed.postgresMinimum
    if ($minimum -isnot [string] -or $minimum -cnotmatch '^\d{2,3}\.\d{1,4}\z') { throw (New-SetupError $ExitUnsupported "release.json's postgresMinimum is not a PostgreSQL release ('18.6'). Nothing was changed.") }
    return $minimum
}

# Refuses a PostgreSQL release ("18.6") older than $minimum (Read-PostgresMinimum's) of the same major version: one
# with security fixes missing, which a list of versions or an installer given offline could pass off as current.
function Assert-PostgresMinimum([string] $version, [string] $minimum, [string] $what) {
    if (-not $minimum) { return }
    $parts = $version.Split('.')
    $floor = $minimum.Split('.')
    if ($parts[0] -ceq $floor[0] -and [int]$parts[1] -lt [int]$floor[1]) {
        throw (New-SetupError $ExitUntrusted "$what is PostgreSQL $version, older than $minimum, the oldest this release of Pulsar installs (it lacks fixes that release has). Nothing was changed.")
    }
}

# Checks a downloaded file's SHA-512 against the one its publisher lists.
function Assert-FileHash([string] $path, [string] $expected, [string] $what) {
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA512).Hash
    if (-not [string]::Equals($actual, $expected, [StringComparison]::OrdinalIgnoreCase)) {
        throw (New-SetupError $ExitUntrusted "$what does not match the SHA-512 Microsoft lists for it ($($expected.ToLowerInvariant())): it is not run. Nothing was changed.")
    }
}

# Arguments as Windows programs read them back (CommandLineToArgvW's rules): quoted when empty or holding a space, a
# tab or a quote, with the backslashes before a quote and at the end doubled inside quotes.
function ConvertTo-CommandLine([string[]] $arguments) {
    $quoted = foreach ($argument in $arguments) {
        if ($argument -and $argument -notmatch '[\s"]') { $argument; continue }
        $text = [regex]::Replace("$argument", '(\\*)"', '$1$1\"')
        $text = [regex]::Replace($text, '(\\+)\z', '$1$1')
        '"' + $text + '"'
    }
    return ($quoted -join ' ')
}

# Runs an installer from the private downloads folder, after its checks, and waits for it ($minutes at most): its
# exit code. Its arguments are never secrets (other programs can read a command line): those go in files. $temp, when
# given, is its TEMP and TMP, where it unpacks and logs.
function Start-Installer([string] $exe, [string[]] $arguments, [string] $temp = $null, [int] $minutes = 30) {
    $info = New-Object Diagnostics.ProcessStartInfo $exe
    $info.Arguments = ConvertTo-CommandLine $arguments
    $info.UseShellExecute = $false
    $info.WorkingDirectory = Split-Path $exe
    if ($temp) {
        $info.EnvironmentVariables['TEMP'] = $temp
        $info.EnvironmentVariables['TMP'] = $temp
    }
    Write-SetupLog "Running $exe $($info.Arguments)"
    $process = [Diagnostics.Process]::Start($info)
    try {
        if (-not $process.WaitForExit($minutes * 60000)) { throw "$exe did not finish within $minutes minutes." }
        return $process.ExitCode
    }
    finally { $process.Dispose() }
}

# --- .NET ---------------------------------------------------------------------------------------------------------

# Variables that change which .NET runtime a program uses or load code into it (DOTNET_ROOT, startup hooks, profilers,
# additional dependencies): removed from this process, so that the .NET programs this script runs as administrator
# (the agent, the panel's migrations) and the installers it starts never take them from whoever set this account's
# environment. The services get the machine's, checked in Assert-DotNetLocation.
# libpq's and psql's variables from whoever started this script (PGHOSTADDR would send the superuser's password
# elsewhere, PGOPTIONS or PGSERVICEFILE change the session, PSQLRC runs a startup file): the PostgreSQL programs this
# script runs as administrator get only what it sets.
function Clear-PostgresEnvironment {
    foreach ($name in @([Environment]::GetEnvironmentVariables('Process').Keys)) {
        if ("$name" -match '^(PG|PSQL)') {
            [Environment]::SetEnvironmentVariable("$name", $null, 'Process')
            Write-SetupLog "Removed $name from this process's environment."
        }
    }
}

function Clear-DotNetEnvironment {
    foreach ($name in @([Environment]::GetEnvironmentVariables('Process').Keys)) {
        if ("$name" -match '^(DOTNET_|COREHOST_|COMPlus_|CORECLR_)') {
            [Environment]::SetEnvironmentVariable("$name", $null, 'Process')
            Write-SetupLog "Removed $name from this process's environment."
        }
    }
}

# Adds a shared framework a program needs to $needed (name -> [version], the highest wins). Only the frameworks this
# script can install, of $DotNetChannel.
function Add-RuntimeRequirement([hashtable] $needed, [string] $name, [string] $version) {
    if ($name -cnotin @($DotNetFrameworks.Keys)) {
        throw (New-SetupError $ExitUnsupported "This release's programs need the .NET framework '$(ConvertTo-PrintableText $name)', which this script cannot install. Nothing was changed.")
    }
    if ($version -cnotmatch ('^' + [regex]::Escape($DotNetChannel) + '\.\d{1,4}\z')) {
        throw (New-SetupError $ExitUnsupported "This release's programs need $name $(ConvertTo-PrintableText $version), not $DotNetChannel.x. Nothing was changed.")
    }
    $parsed = [version]$version
    if (-not $needed.ContainsKey($name) -or $needed[$name] -lt $parsed) { $needed[$name] = $parsed }
}

# The .NET runtimes programs need ($configs: the text of their runtimeconfig.json; a self-contained program's names
# none), at least the patch the release was built with ($release: the text of its release.json, Build-Release.ps1's,
# or $null): name -> [version].
function Read-RuntimeRequirements([string[]] $configs, [string] $release) {
    $needed = @{}
    foreach ($text in $configs) {
        $options = ($text | ConvertFrom-Json).runtimeOptions
        $frameworks = @()
        if ($options.PSObject.Properties['framework']) { $frameworks += $options.framework }
        if ($options.PSObject.Properties['frameworks']) { $frameworks += @($options.frameworks) }
        foreach ($framework in $frameworks) { Add-RuntimeRequirement $needed $framework.name $framework.version }
    }
    if ($release -and $needed.Count -gt 0) {
        $recorded = ($release | ConvertFrom-Json).runtimes
        foreach ($property in $recorded.PSObject.Properties) {
            if ($needed.ContainsKey($property.Name)) { Add-RuntimeRequirement $needed $property.Name $property.Value }
        }
    }
    return $needed
}

# The .NET runtimes the parts this run installs need (the agent's too: its program checks this Windows first).
function Get-RuntimeRequirements([string[]] $parts) {
    $programs = @{ panel = 'panel\Pulsar.Api.runtimeconfig.json'; agent = 'agent\PulsarAgent.runtimeconfig.json' }
    $configs = foreach ($part in @(@($parts) + 'agent' | Select-Object -Unique)) {
        $path = Join-Path $PSScriptRoot $programs[$part]
        if (-not (Test-Path -LiteralPath $path)) { throw (New-SetupError $ExitUnsupported "$path is missing: run this script from the extracted release ZIP. Nothing was changed.") }
        Get-Content -LiteralPath $path -Raw
    }
    return Read-RuntimeRequirements @($configs) (Get-ReleaseText)
}

# The text of the release's release.json (Build-Release.ps1's, next to this script), or $null without one (a script run
# from the repository).
function Get-ReleaseText {
    $releaseFile = Join-Path $PSScriptRoot 'release.json'
    if (Test-Path -LiteralPath $releaseFile) { return Get-Content -LiteralPath $releaseFile -Raw }
    return $null
}

# The .NET runtimes of $DotNetChannel installed for 64-bit programs in $root: name -> the highest version, from the
# shared framework folders `dotnet --list-runtimes` lists (none without a host resolver, host\fxr, of that channel).
function Get-InstalledRuntimes([string] $root) {
    $found = @{}
    $channel = [version]$DotNetChannel
    $hosts = @(Get-ChildItem -LiteralPath (Join-Path $root 'host\fxr') -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -cmatch '^\d+\.\d+\.\d+\z' -and [version]$_.Name -ge $channel -and (Test-Path -LiteralPath (Join-Path $_.FullName 'hostfxr.dll')) })
    if ($hosts.Count -eq 0) { return $found }
    foreach ($name in $DotNetFrameworks.Keys) {
        foreach ($folder in Get-ChildItem -LiteralPath (Join-Path $root "shared\$name") -Directory -ErrorAction SilentlyContinue) {
            if ($folder.Name -cnotmatch ('^' + [regex]::Escape($DotNetChannel) + '\.\d{1,4}\z')) { continue }
            if (-not (Test-Path -LiteralPath (Join-Path $folder.FullName "$name.deps.json"))) { continue }
            $version = [version]$folder.Name
            if (-not $found.ContainsKey($name) -or $found[$name] -lt $version) { $found[$name] = $version }
        }
    }
    return $found
}

# The runtimes of $needed that $installed lacks, or has older, in the order they are installed.
function Get-MissingRuntimes([hashtable] $needed, [hashtable] $installed) {
    foreach ($name in $DotNetFrameworks.Keys) {
        if ($needed.ContainsKey($name) -and (-not $installed.ContainsKey($name) -or $installed[$name] -lt $needed[$name])) { $name }
    }
}

# Whether $url is one of Microsoft's download addresses for $file: https, a host of $DotNetHosts, the file's name last.
function Test-DotNetDownloadUrl($url, [string] $file) {
    $uri = $null
    if ($url -isnot [string] -or -not [Uri]::TryCreate($url, [UriKind]::Absolute, [ref] $uri)) { return $false }
    return $uri.Scheme -ceq 'https' -and $uri.Host -in $DotNetHosts -and $uri.IsDefaultPort -and -not $uri.UserInfo -and -not $uri.Query -and
        $uri.AbsolutePath.EndsWith("/$file", [StringComparison]::Ordinal)
}

# The win-x64 installers of a .NET runtime in Microsoft's release metadata ($metadata: releases.json read with
# ConvertFrom-JsonDocument; $component: 'runtime' or 'aspnetcore-runtime'; $file: its installer's name without the
# version): for each release of $DotNetChannel, the version (stable ones only, 10.0.n), address and SHA-512. An entry
# for that installer whose address is not Microsoft's, or whose hash is not a SHA-512, makes the metadata untrusted.
function Get-DotNetInstallerEntries($metadata, [string] $component, [string] $file) {
    if ((Get-JsonValue $metadata 'channel-version') -cne $DotNetChannel) {
        throw (New-SetupError $ExitUntrusted "Microsoft's release metadata is not that of .NET $DotNetChannel. Nothing was changed.")
    }
    $releases = Get-JsonValue $metadata 'releases'
    if ($releases -isnot [Collections.IList]) { throw (New-SetupError $ExitUntrusted "Microsoft's release metadata lists no releases. Nothing was changed.") }
    foreach ($release in $releases) {
        $part = Get-JsonValue $release $component
        $version = Get-JsonValue $part 'version'
        if ($version -isnot [string] -or $version -cnotmatch ('^' + [regex]::Escape($DotNetChannel) + '\.\d{1,4}\z')) { continue }
        $name = "$file-win-x64.exe"
        $files = Get-JsonValue $part 'files'
        if ($files -isnot [Collections.IList]) { continue }
        $entries = @(foreach ($entry in $files) {
                if ((Get-JsonValue $entry 'name') -ceq $name -and (Get-JsonValue $entry 'rid') -ceq 'win-x64') { $entry }
            })
        if ($entries.Count -eq 0) { continue }
        $url = Get-JsonValue $entries[0] 'url'
        $hash = Get-JsonValue $entries[0] 'hash'
        if ($entries.Count -ne 1 -or -not (Test-DotNetDownloadUrl $url "$file-$version-win-x64.exe") -or $hash -isnot [string] -or $hash -cnotmatch '^[0-9A-Fa-f]{128}\z') {
            throw (New-SetupError $ExitUntrusted "Microsoft's release metadata lists the $file $version installer in a way this script does not trust (more than once, not from Microsoft's download hosts, or without a SHA-512). Nothing was changed.")
        }
        [pscustomobject]@{ Version = [version]$version; Text = $version; Url = $url; Hash = $hash.ToLowerInvariant() }
    }
}

# The installers of the missing runtimes ($missing: names; $needed: name -> the least version), in the private
# downloads folder and checked: downloaded from Microsoft (the newest $DotNetChannel release its metadata lists, with
# the SHA-512 listed there), or copied from -DotNetInstallers (the newest of each there, its SHA-512 checked too when
# the metadata can be read). Each must be signed by Microsoft and be at least the version the release needs.
function Get-DotNetInstallers([string[]] $missing, [hashtable] $needed) {
    $metadata = $null
    try {
        Write-Step "Reading Microsoft's .NET $DotNetChannel release metadata ($DotNetReleasesUrl)"
        $metadata = ConvertFrom-JsonDocument (Get-SetupText $DotNetReleasesUrl 32MB)
    }
    catch {
        # Metadata that is there but not as it should be is refused, whatever the installers' source.
        if ((Get-SetupExitCode $_) -ne $ExitOffline) { throw }
        if (-not $DotNetInstallers) {
            throw (New-SetupError $ExitOffline "The .NET runtimes Pulsar needs could not be downloaded ($($_.Exception.Message)). Connect this machine to the internet and run this script again, or give Microsoft's installers with -DotNetInstallers. Nothing was changed.")
        }
        Write-Warning "Microsoft's release metadata could not be read ($($_.Exception.Message)): the installers in $DotNetInstallers are checked by their signature and version, not by their SHA-512."
    }
    foreach ($name in $missing) {
        $framework = $DotNetFrameworks[$name]
        $entries = @()
        if ($null -ne $metadata) { $entries = @(Get-DotNetInstallerEntries $metadata $framework.Component $framework.File) }
        $entry = $null
        if ($DotNetInstallers) {
            $pattern = '^' + [regex]::Escape($framework.File) + '-(' + [regex]::Escape($DotNetChannel) + '\.\d{1,4})-win-x64\.exe\z'
            $local = Get-ChildItem -LiteralPath $DotNetInstallers -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -cmatch $pattern } |
                Sort-Object { [version]($_.Name -creplace $pattern, '$1') } -Descending | Select-Object -First 1
            if (-not $local) { throw (New-SetupError $ExitUsage "$DotNetInstallers has no $($framework.File)-$DotNetChannel.<n>-win-x64.exe, the installer of $name. Nothing was changed.") }
            $version = [version]($local.Name -creplace $pattern, '$1')
            $file = Copy-SetupInstaller $local.FullName
            $what = "The $name installer $($local.FullName)"
            if ($null -ne $metadata) {
                $entry = $entries | Where-Object { $_.Version -eq $version } | Select-Object -First 1
                if (-not $entry) { throw (New-SetupError $ExitUntrusted "$what is of a version Microsoft's release metadata does not list ($version). Nothing was changed.") }
            }
        }
        else {
            $entry = $entries | Sort-Object Version -Descending | Select-Object -First 1
            if (-not $entry) { throw (New-SetupError $ExitUntrusted "Microsoft's release metadata lists no $($framework.File) installer for 64-bit Windows. Nothing was changed.") }
            $version = $entry.Version
            Write-Step "Downloading $name $($entry.Text) from Microsoft"
            $file = Save-SetupDownload $entry.Url "$($framework.File)-$($entry.Text)-win-x64.exe" 512MB
            $what = "The $name installer downloaded from $($entry.Url)"
        }
        if ($version -lt $needed[$name]) {
            throw (New-SetupError $ExitUntrusted "$what is $name $version, older than this release needs ($($needed[$name])). Nothing was changed.")
        }
        if ($entry) { Assert-FileHash $file $entry.Hash $what }
        $signature = Get-AuthenticodeSignature -LiteralPath $file
        $chain = $null
        if ($signature.SignerCertificate) { $chain = Get-SignerChain $file $signature.SignerCertificate }
        Assert-InstallerSignature $signature $DotNetSigner $what $chain
        # Signed for the version it was chosen as ("10.0.13"), not an older one under the expected name.
        Assert-InstallerVersion (Get-Item -LiteralPath $file).VersionInfo "$($framework.Product) - $($version.Major).$($version.Minor).$($version.Build) (x64)" "$($version.Major).$($version.Minor).$($version.Build)" $what
        [pscustomobject]@{ Name = $name; Version = $version; Path = $file }
    }
}

# Runs the checked runtime installers, quietly and without restarting Windows; one that asks for a restart (3010) makes
# this run end with $ExitRestart.
function Install-DotNetRuntimes([object[]] $installers) {
    foreach ($installer in $installers) {
        Write-Step "Installing $($installer.Name) $($installer.Version)"
        $log = Join-Path $SetupLogs ('{0}-{1:yyyyMMdd-HHmmss}.log' -f [IO.Path]::GetFileNameWithoutExtension($installer.Path), (Get-Date))
        $code = Start-Installer $installer.Path @('/install', '/quiet', '/norestart', '/log', $log)
        if ($code -in 3010, 1641) { $script:RestartNeeded = $true }
        elseif ($code -ne 0) { throw "The installer of $($installer.Name) $($installer.Version) failed (exit code $code); its log is $log." }
    }
}

# Whether two paths name the same folder.
function Test-SamePath([string] $a, [string] $b) {
    return [string]::Equals([IO.Path]::GetFullPath($a).TrimEnd('\'), [IO.Path]::GetFullPath($b).TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)
}

# The .NET runtime the services will load: from $root (Program Files\dotnet, where Microsoft's installers put it),
# as the machine's environment and the registry say (a DOTNET_ROOT elsewhere is refused), and from folders only
# SYSTEM, Administrators and TrustedInstaller can change, since the agent runs as SYSTEM: the host resolver and every
# runtime of the channel (a folder others could add a newer patch to would be loaded instead).
function Assert-DotNetLocation([string] $root, [hashtable] $needed) {
    foreach ($variable in 'DOTNET_ROOT_X64', 'DOTNET_ROOT') {
        $value = [Environment]::GetEnvironmentVariable($variable, 'Machine')
        if ($value -and -not (Test-SamePath $value $root)) {
            throw (New-SetupError $ExitUnsupported "The machine's environment sets $variable to ${value}: Pulsar's services would load .NET from there instead of $root, where Microsoft's installers put it. Remove the variable, or make it $root.")
        }
    }
    $registered = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\x64' -ErrorAction SilentlyContinue
    if ($registered -and $registered.PSObject.Properties['InstallLocation'] -and $registered.InstallLocation -and -not (Test-SamePath $registered.InstallLocation $root)) {
        throw (New-SetupError $ExitUnsupported "Windows has .NET registered in $($registered.InstallLocation), not in ${root}: Pulsar's services would load it from there. Repair .NET's installation (Apps) so that it is in $root.")
    }
    $item = Get-Item -LiteralPath $root -Force
    $findings = @(Find-UntrustedWriter $item $ModifyRights) + @(Find-UntrustedAncestors $item)
    $folders = @((Join-Path $root 'host\fxr'), (Join-Path $root 'shared'))
    foreach ($name in $needed.Keys) {
        $shared = Join-Path $root "shared\$name"
        $folders += $shared
        $folders += @(Get-ChildItem -LiteralPath $shared -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -like "$DotNetChannel.*" } | ForEach-Object { $_.FullName })
    }
    foreach ($folder in $folders) {
        if (-not (Test-Path -LiteralPath $folder)) { continue }
        if ($folder -like '*\shared' -or $folder -like "*\shared\Microsoft.*App") { $findings += @(Find-UntrustedWriter (Get-Item -LiteralPath $folder -Force) $ModifyRights) }
        else { $findings += @(Find-UntrustedWriters $folder) }
    }
    $findings = @($findings | Select-Object -Unique)
    if ($findings.Count -gt 0) {
        throw (New-SetupError $ExitUnsupported "Pulsar's services (the agent runs as SYSTEM) would load .NET from $root, which other accounts could change:$([Environment]::NewLine)$(Get-UntrustedWritersMessage $findings)$([Environment]::NewLine)Repair .NET's installation so that only administrators can change it.")
    }
}

# Checks, after any install, that the runtimes the release needs are there, where the services will load them.
function Assert-DotNetRuntimes([hashtable] $needed, [string] $root) {
    if ($needed.Count -eq 0) { return }
    $missing = @(Get-MissingRuntimes $needed (Get-InstalledRuntimes $root))
    if ($missing.Count -gt 0) {
        $text = @($missing | ForEach-Object { "$_ $($needed[$_])" }) -join ', '
        throw "This machine still lacks $text after installing it. Restart Windows and run this script again."
    }
    Assert-DotNetLocation $root $needed
}

# --- Main -------------------------------------------------------------------------------------------------------

# Refuses to update to a release ($release: its release.json's version, "1.2.0" or "1.2.0-beta") older than a program
# installed here ($installed: the file versions of the installed Pulsar.Api.exe and PulsarAgent.exe, "1.3.0.0"): the
# database of a newer panel may hold what an older one cannot read, and an older agent may not speak its panel's
# protocol. Nothing to compare (no release.json, nothing installed) passes.
function Assert-NotOlderThanInstalled([string] $release, [string[]] $installed) {
    if ($release -cnotmatch '^(\d+)\.(\d+)\.(\d+)') { return }
    $releaseVersion = New-Object Version ([int]$Matches[1]), ([int]$Matches[2]), ([int]$Matches[3])
    foreach ($text in @($installed | Where-Object { $_ })) {
        if ($text -cnotmatch '^(\d+)\.(\d+)\.(\d+)') { continue }
        $installedVersion = New-Object Version ([int]$Matches[1]), ([int]$Matches[2]), ([int]$Matches[3])
        if ($releaseVersion -lt $installedVersion) {
            throw (New-SetupError $ExitUsage "This release of Pulsar ($release) is older than the one installed here ($installedVersion): an update cannot go back to an older version (a newer panel's database may hold what an older one cannot read). Nothing was changed.")
        }
    }
}

# The file versions of the Pulsar programs installed here (the panel's and the agent's), for Assert-NotOlderThanInstalled.
function Get-InstalledVersions {
    foreach ($exe in (Join-Path $PanelDir 'Pulsar.Api.exe'), (Join-Path $AgentDir 'PulsarAgent.exe')) {
        if (Test-Path -LiteralPath $exe -PathType Leaf) { "$((Get-Item -LiteralPath $exe).VersionInfo.FileVersion)" }
    }
}

# The parts of Pulsar this run installs or updates: 'panel', 'agent' or both.
function Get-SetupParts($agentPackage) {
    if ($Update) {
        $parts = @()
        if (Test-Service $PanelService) { $parts += 'panel' }
        if (-not $agentPackage -and (Test-Service $AgentService)) { $parts += 'agent' }
        if ($parts.Count -eq 0) { throw (New-SetupError $ExitUsage 'Pulsar is not installed on this machine.') }
        return $parts
    }
    switch ($Mode) {
        'AllInOne' { return @('panel', 'agent') }
        'Panel' { return @('panel') }
        default { return @('agent') }
    }
}

function Get-StepPlan {
    if ($Uninstall) { return $StepPlans['Uninstall'] }
    if ($Update) { return $StepPlans['Update'] }
    return $StepPlans[$Mode]
}

# An agent's values: from a node code (-NodeCode, PULSAR_NODE_CODE or the secrets file's), or -PanelUrl, -Token and
# -PanelKey. The code as read ($null without one) and the panel's address.
function Resolve-AgentInputs {
    $code = $null
    if ($NodeCode) {
        if ($Token -or $PanelKey -or $TrustCertificate) {
            throw (New-SetupError $ExitUsage 'A node code holds the enrollment token, the panel key and the certificate: give it without -Token, -PanelKey and -TrustCertificate (and with -PanelUrl only when this machine reaches the panel at another address than the code names). Nothing was changed.')
        }
        $code = ConvertFrom-NodeCode $NodeCode
        if (-not $PanelUrl) { $script:PanelUrl = $code.Url }
        elseif (-not (Test-PanelUrl $PanelUrl)) { throw (New-SetupError $ExitUsage "-PanelUrl is not a panel's https:// address: $(ConvertTo-PrintableText $PanelUrl). Nothing was changed.") }
        $script:Token = $code.Token
        $script:PanelKey = $code.PanelKey
        Register-Secret $code.Token
    }
    if (-not ($PanelUrl -and $Token -and $PanelKey)) {
        throw (New-SetupError $ExitUsage 'An agent needs the node code the panel shows in Nodes > Add node (in PULSAR_NODE_CODE, or -NodeCode), or -PanelUrl, -Token (or the PULSAR_ENROLLMENT_TOKEN environment variable) and -PanelKey. Nothing was changed.')
    }
    $uri = $null
    if (-not [Uri]::TryCreate($PanelUrl, [UriKind]::Absolute, [ref] $uri)) { throw (New-SetupError $ExitUsage "-PanelUrl is not an address: $(ConvertTo-PrintableText $PanelUrl). Nothing was changed.") }
    if (Test-Service $AgentService) { throw (New-SetupError $ExitUsage 'The agent is already installed on this machine. Use -Update to update it, or -Uninstall first.') }
    return [pscustomobject]@{ Code = $code; Uri = $uri }
}

# Everything that prints or fails below goes through Write-SetupRecord (the log, secrets replaced) and ends in an exit
# code with -Unattended.
$main = {
    $script:StepPlan = Get-StepPlan
    Write-SetupStep 'check' 'Checking this machine and what it needs'
    Clear-DotNetEnvironment
    Clear-PostgresEnvironment
    Use-SetupInputs (Read-SetupInputs)
    Assert-TrustedLocation
    # Checked before anything changes: with an agent from the MSI, only a panel is this script's to install, update or remove.
    $agentPackage = Get-AgentPackage
    if ($agentPackage) {
        if ($Update -or $Uninstall) {
            if (-not (Test-Service $PanelService)) { throw (New-SetupError $ExitUnsupported (Get-AgentPackageMessage $agentPackage)) }
        }
        elseif ($Mode -ne 'Panel') { throw (New-SetupError $ExitUnsupported (Get-AgentPackageMessage $agentPackage)) }
    }

    if ($Uninstall) {
        Write-SetupStep 'remove' 'Removing Pulsar'
        if ($agentPackage) { Write-Warning "The agent is left as it is. $(Get-AgentPackageMessage $agentPackage)" }
        elseif (Test-Service $AgentService) { Uninstall-Agent }
        if (Test-Service $PanelService) { Uninstall-Panel }
        # What -Mode Agent -TrustCertificate or -Mode AllInOne trusted (an agent from the MSI may need it: then it stays).
        if (-not $agentPackage) { Remove-TrustedCertificates }
        $removed = if ($agentPackage) { 'The panel is removed (not the agent: see above).' }
        elseif (Test-Path -LiteralPath (Join-Path $AgentDir 'PulsarAgent.exe')) { 'Pulsar is removed, but for the agent''s program and data (see the warning above).' }
        else { 'Pulsar is removed.' }
        Write-Host "$removed Kept: the database (and PostgreSQL), the .NET runtimes, $DataRoot (the panel's keys, needed to read its database's secrets, and logs) and the game servers' folders." -ForegroundColor Green
        return
    }

    # --- What this run needs, checked before anything is downloaded or changed ---
    if (-not $Update -and $RestoreFrom -and $Mode -ne 'Panel') { throw (New-SetupError $ExitUsage '-RestoreFrom restores a panel: use it with -Mode Panel (nodes keep their agents).') }
    $parts = @(Get-SetupParts $agentPackage)
    if ($Update) {
        $releaseText = Get-ReleaseText
        if ($releaseText) { Assert-NotOlderThanInstalled "$(($releaseText | ConvertFrom-Json).version)" @(Get-InstalledVersions) }
    }
    $agent = $null
    $postgres = $null
    if (-not $Update) {
        if ($Mode -eq 'Agent') {
            $agent = Resolve-AgentInputs
            if ($TrustCertificate) {
                try { Assert-TrustableCertificate (Read-Certificate $TrustCertificate) $TrustCertificate $agent.Uri.DnsSafeHost }
                catch { throw (New-SetupError $ExitCertificate "$($_.Exception.Message) Nothing was changed.") }
            }
        }
        else {
            Assert-PanelInstallable
            $postgres = Get-PostgresPlan
        }
    }
    $needed = Get-RuntimeRequirements $parts
    $dotnetRoot = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'dotnet'
    $missing = @(Get-MissingRuntimes $needed (Get-InstalledRuntimes $dotnetRoot))
    # The certificate the agent pins (a node code's), if any: never added to a certificate store.
    $pinnedCertificate = ''
    # Plain http, which a code allows only to this machine's own panel (loopback, as the agent does), has no certificate.
    if ($agent -and $agent.Code -and $agent.Uri.Scheme -ceq 'https') {
        Write-Step "Reading the certificate the panel presents at $($agent.Uri.GetLeftPart('Authority'))"
        $presented = Read-PanelCertificate $agent.Uri
        if ((Resolve-NodeCertificate $agent.Code $presented $agent.Uri.DnsSafeHost) -eq 'trusted') { Write-Host '    This machine trusts it already.' }
        else {
            $pinnedCertificate = Get-CertificateHash $presented.Certificate
            Write-Host "    It is the one the node code names (SHA-256 $pinnedCertificate): the agent pins it, and accepts no other. This machine's certificate stores are not changed."
        }
    }

    # --- Downloads: all of them, and their checks, before anything is installed ---
    $wanted = @()
    if ($missing.Count -gt 0) { $wanted += ".NET $DotNetChannel ($($missing -join ', '))" }
    if ($postgres -and $postgres.Install) { $wanted += "PostgreSQL $($postgres.Major)" }
    $dotnetInstallers = @()
    $postgresInstaller = $null
    if ($wanted.Count -eq 0) { Write-SetupStep 'downloads' 'Nothing to download' }
    else {
        Write-SetupStep 'downloads' "Downloading and checking $($wanted -join ' and ')"
        New-SetupDownloads
        if ($missing.Count -gt 0) { $dotnetInstallers = @(Get-DotNetInstallers $missing $needed) }
        if ($postgres -and $postgres.Install) { $postgresInstaller = Get-PostgresInstallerFile $postgres }
    }

    # --- .NET ---
    if ($dotnetInstallers.Count -gt 0) {
        Write-SetupStep 'dotnet' "Installing .NET $DotNetChannel ($(@($dotnetInstallers | ForEach-Object { "$($_.Name) $($_.Version)" }) -join ', '))"
        Install-DotNetRuntimes $dotnetInstallers
    }
    elseif ($needed.Count -gt 0) { Write-SetupStep 'dotnet' ".NET ${DotNetChannel}: installed already" }
    else { Write-SetupStep 'dotnet' '.NET: not needed (these programs carry their own)' }
    Assert-DotNetRuntimes $needed $dotnetRoot
    Assert-RuntimeSupported

    if ($Update) {
        Write-SetupStep 'update' 'Updating Pulsar'
        if ($agentPackage) { Write-Warning "The agent is left as it is. $(Get-AgentPackageMessage $agentPackage)" }
        elseif ($parts -contains 'agent') { Update-Agent }
        if ($parts -contains 'panel') { Update-Panel }
        Write-Host 'Pulsar is updated.' -ForegroundColor Green
    }
    elseif ($Mode -eq 'Agent') {
        Write-SetupStep 'agent' 'Installing the agent'
        # Never with a node code (Resolve-AgentInputs refuses -TrustCertificate with one): its certificate is pinned.
        if ($TrustCertificate) {
            Write-Step "Trusting the panel's certificate ($TrustCertificate)"
            Add-TrustedCertificate $TrustCertificate $agent.Uri.DnsSafeHost
        }
        Install-Agent $PanelUrl $Token $PanelKey $pinnedCertificate
        Write-Host 'The agent is installed. The node appears as online in the panel.' -ForegroundColor Green
    }
    else {
        if (-not $postgres) { Write-SetupStep 'postgres' 'Using the database of -ConnectionString' }
        elseif ($postgres.Install) {
            Write-SetupStep 'postgres' "Installing PostgreSQL $($postgres.Major)"
            $script:PostgresPassword = Install-PostgresServer $postgresInstaller $postgres
        }
        else { Write-SetupStep 'postgres' "Using PostgreSQL $($postgres.Major), installed already" }
        try { $installed = Install-Panel }
        catch {
            if (-not ($postgres -and $postgres.Install)) { throw }
            # Its superuser's password was this run's alone: a next run would ask for it.
            throw (New-SetupError (Get-SetupExitCode $_) "$($_.Exception.Message)$([Environment]::NewLine)PostgreSQL $($postgres.Major), which this run installed, stays, and its postgres password was kept nowhere: before running this script again, remove PostgreSQL $($postgres.Major) from Apps and delete $($postgres.Prefix) (it holds nothing of Pulsar's), or give postgres a password of yours (docs/guides/install.md, ""The postgres superuser's password"").")
        }
        $certificate = $installed.Certificate
        if ($Mode -eq 'Panel') {
            Write-Host ''
            Write-Host "The panel is running: https://localhost:$($installed.Port) on this machine." -ForegroundColor Green
            Write-RemoteAccess $certificate $installed.Port
            if ($certificate.SelfSigned) {
                Write-Host "Nodes join with the code Nodes > Add node shows, whose agent pins the certificate (or copy $($certificate.Cer) to each"
                Write-Host "node and pass it with -TrustCertificate)."
            }
        }
        else {
            Write-SetupStep 'agent' 'Adding this PC as a node'
            if ($certificate.SelfSigned) {
                Write-Step "Trusting the panel's certificate on this machine (the agent and this PC's browsers check it)"
                Add-TrustedCertificate $certificate.Cer 'localhost' $certificate.Names
            }
            $enrollment = $installed.Node.enrollment
            Install-Agent "https://localhost:$($installed.Port)" $enrollment.token $enrollment.panelKeyFingerprint
            Write-Host ''
            Write-Host "Pulsar is running: open https://localhost:$($installed.Port) and sign in as '$AdminUserName'." -ForegroundColor Green
            Write-RemoteAccess $certificate $installed.Port
        }
        Write-SftpSummary $installed.Sftp
        if ($Unattended) { Write-Host "PULSAR-PANEL-URL https://localhost:$($installed.Port)/" }
    }
    if ($RestartNeeded) { Write-Warning 'Restart Windows when you can: a .NET installer asks for it to finish. Pulsar runs meanwhile.' }
}

$SetupParameters = $PSBoundParameters
$outcome = 0
try {
    Assert-Administrator
    Start-SetupLog
    & $main *>&1 | ForEach-Object { Write-SetupRecord $_ }
    if ($RestartNeeded) { $outcome = $ExitRestart }
    Write-SetupLog "Done (exit code $outcome)."
}
catch {
    $outcome = Get-SetupExitCode $_
    $message = Protect-SetupText $_.Exception.Message
    Write-SetupLog "FAILED (exit code $outcome): $message$([Environment]::NewLine)$($_.ScriptStackTrace)"
    if (-not $Unattended) {
        # As errors always were, without a secret a program's output may have carried into the message.
        if ($message -cne $_.Exception.Message) { throw $message }
        throw
    }
    Write-Host "PULSAR-ERROR $outcome $(ConvertTo-PrintableText ($message -replace '\s*\r?\n\s*', ' '))"
}
finally {
    Remove-SetupDownloads
}
if ($Unattended) { exit $outcome }
