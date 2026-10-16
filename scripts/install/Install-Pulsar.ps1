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

    Every step is described in docs/guides/install.md.

.EXAMPLE
    .\Install-Pulsar.ps1
    Panel and agent on this PC (asks for the PostgreSQL password and the administrator's password).

.EXAMPLE
    .\Install-Pulsar.ps1 -Mode Agent -PanelUrl https://panel.example.com:8443 -Token <token> -PanelKey <fingerprint> -TrustCertificate .\panel.cer
    Adds this machine as a node of an existing panel whose certificate is self-signed.
#>
[CmdletBinding(DefaultParameterSetName = 'Install')]
param(
    [Parameter(ParameterSetName = 'Install')]
    [ValidateSet('AllInOne', 'Panel', 'Agent')]
    [string] $Mode = 'AllInOne',

    # --- Panel ---
    # An existing database (Npgsql connection string). Without it, a 'pulsar' database and role are created in the
    # local PostgreSQL with -PostgresPassword (the postgres superuser's password).
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
    # Extra names for the self-signed certificate (the machine's name, its DNS name and localhost are always in it).
    # Letters, digits, '.', '-' and '_' (a leading '*.' for a wildcard), compared as written: ignoring case, the Kelvin
    # sign would pass for a 'k'.
    [ValidatePattern('^(\*\.)?[A-Za-z0-9_-]+(\.[A-Za-z0-9_-]+)*\.?\z', Options = 'None')]
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
# Certificates this script trusted carry this friendly name in Cert:\LocalMachine\Root: -Uninstall removes those only.
$TrustedName = 'Pulsar panel (trusted by Install-Pulsar.ps1)'
# Where earlier installs noted the certificate they trusted.
$TrustedMarker = Join-Path $PanelData 'tls\trusted-thumbprint.txt'
# The Extended Key Usage of a web server's certificate (id-kp-serverAuth): the only use the panel's certificate needs.
$ServerAuthentication = '1.3.6.1.5.5.7.3.1'
# Windows' own programs by full path: this runs elevated, and a program found through PATH could be anyone's.
$Icacls = Join-Path ([Environment]::SystemDirectory) 'icacls.exe'
$Sc = Join-Path ([Environment]::SystemDirectory) 'sc.exe'

function Write-Step([string] $message) { Write-Host "==> $message" -ForegroundColor Cyan }

function Assert-Administrator {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script in an elevated PowerShell (right-click > Run as administrator).'
    }
    if (-not [Environment]::Is64BitOperatingSystem) { throw 'Pulsar needs 64-bit Windows.' }
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
        throw "This script runs as administrator and installs the programs next to it to run as services, so it refuses to run from a folder that other accounts could change (a game server's account could replace a program, or this script, and take over the machine). Here, in ${PSScriptRoot}:$([Environment]::NewLine)$(Get-UntrustedWritersMessage $findings)$([Environment]::NewLine)Extract the release ZIP into a new folder in $env:ProgramFiles, which only administrators can change, as docs/guides/install.md shows (step 2), and run this script from there. Nothing was changed."
    }
}

# Pulsar's programs (.NET 10) refuse to start on a Windows that lacks updates: Windows Server 2022 as installed from its
# original image stops them with "Your Windows doesn't fully support CET". Checked before anything is installed.
function Assert-RuntimeSupported {
    $result = Invoke-Native (Join-Path $PSScriptRoot 'agent\PulsarAgent.exe') help
    if ($result.ExitCode -eq 0) { return }
    if ($result.Output -match 'CET') {
        throw "This Windows lacks updates that Pulsar needs ($($result.Output.Trim() -replace '\s+', ' ')). Install all available Windows updates (Settings > Windows Update, or sconfig on Server Core), restart, and run this script again."
    }
    throw "Pulsar's programs do not start on this Windows (exit code $($result.ExitCode)):$([Environment]::NewLine)$($result.Output)"
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

# Copies a release folder over an installed one, keeping the local settings file.
function Copy-Program([string] $source, [string] $target) {
    if (-not (Test-Path (Join-Path $source '*'))) { throw "$source is missing: run this script from the extracted release ZIP." }
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

# The newest PostgreSQL service on this machine (there may be several versions), started if it is not running.
function Find-PostgresBin {
    $found = foreach ($service in Get-CimInstance Win32_Service -Filter "Name LIKE 'postgresql%'") {
        if ($service.PathName -match '^"?([^"]+\\bin)\\pg_ctl\.exe') {
            $bin = $Matches[1]
            $version = (Invoke-Native (Join-Path $bin 'psql.exe') --version).Output
            if ($version -match '(\d+)\.\d+') { [pscustomobject]@{ Service = $service; Bin = $bin; Major = [int]$Matches[1] } }
        }
    }
    $newest = $found | Sort-Object Major -Descending | Select-Object -First 1
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

function New-PanelDatabase {
    $bin = Find-PostgresBin
    if (-not $PostgresPassword) {
        $PostgresPassword = Read-Host -AsSecureString "Password of PostgreSQL's 'postgres' user (chosen when PostgreSQL was installed)"
    }
    $password = New-Secret 32
    $env:PGPASSWORD = ConvertFrom-Secure $PostgresPassword
    try {
        $psql = Join-Path $bin 'psql.exe'
        $common = @('-h', 'localhost', '-p', $PostgresPort, '-U', 'postgres', '-d', 'postgres', '-v', 'ON_ERROR_STOP=1', '-q', '-t')
        $exists = Invoke-Native $psql @common -c "SELECT count(*) FROM pg_roles WHERE rolname = 'pulsar'"
        if ($exists.ExitCode -ne 0) { throw "Could not connect to PostgreSQL as 'postgres': $($exists.Output)" }
        if ($exists.Output.Trim() -ne '0') {
            throw "PostgreSQL already has a 'pulsar' role (an earlier install?). Pass its database with -ConnectionString, or drop it first."
        }
        # On standard input, not the command line: every process on this machine can read command lines.
        $created = Invoke-NativeWithInput "CREATE ROLE pulsar LOGIN PASSWORD '$password';" $psql @common -f -
        if ($created.ExitCode -ne 0) { throw "Could not create the 'pulsar' role: $($created.Output)" }
        $created = Invoke-Native $psql @common -c 'CREATE DATABASE pulsar OWNER pulsar'
        if ($created.ExitCode -ne 0) {
            # Nobody else knows the role's password: kept, it would only make the next run refuse.
            $dropped = Invoke-Native $psql @common -c 'DROP ROLE pulsar'
            $left = ''
            if ($dropped.ExitCode -ne 0) { $left = " The 'pulsar' role created for it could not be dropped ($($dropped.Output)): drop it before running this script again." }
            throw "Could not create the 'pulsar' database: $($created.Output)$left"
        }
    }
    finally {
        Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    }
    return "Host=localhost;Port=$PostgresPort;Database=pulsar;Username=pulsar;Password=$password"
}

# --- Certificate ------------------------------------------------------------------------------------------------

# Returns @{ Path; Password; Thumbprint; Cer } for the panel's HTTPS certificate, in the panel's private folder.
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
        return @{ Path = $pfx; Password = $password; Thumbprint = $certificate.Thumbprint; Cer = $cer; SelfSigned = $false }
    }

    $names = @('localhost', $env:COMPUTERNAME)
    $domain = (Get-CimInstance Win32_ComputerSystem).Domain
    if ($domain -and $domain -ne 'WORKGROUP') { $names += "$($env:COMPUTERNAME).$domain" }
    $names = @($names + $HostName | Where-Object { $_ } | Select-Object -Unique)
    Write-Step "Creating a self-signed HTTPS certificate for $($names -join ', ')"
    # Trusted machine-wide (here, and on nodes with -TrustCertificate), so it must not be able to issue certificates:
    # Basic Constraints, critical, CA=false. Without them Windows may take it for a certificate authority, and whoever
    # holds its key could vouch for any site. And for a web server's identity only (Extended Key Usage: server
    # authentication; New-SelfSignedCertificate adds client authentication unless told).
    $certificate = New-SelfSignedCertificate -DnsName $names -CertStoreLocation 'Cert:\LocalMachine\My' `
        -FriendlyName 'Pulsar panel' -KeyExportPolicy Exportable -KeyLength 2048 -NotAfter (Get-Date).AddYears(5) `
        -TextExtension @('2.5.29.19={critical}{text}ca=0', "2.5.29.37={text}$ServerAuthentication")
    try {
        if ((Get-CertificateAuthority $certificate) -ne $false) {
            throw 'The self-signed certificate came out able to issue certificates (its Basic Constraints do not say CA=false).'
        }
        if (-not (Test-ServerAuthenticationOnly (Get-CertificateUses $certificate))) {
            throw 'The self-signed certificate came out usable for more than a web server (its Extended Key Usage is not server authentication alone).'
        }
        $password = New-Secret 32
        $null = Export-PfxCertificate -Cert $certificate -FilePath $pfx -Password (ConvertTo-SecureString $password -AsPlainText -Force)
        $null = Export-Certificate -Cert $certificate -FilePath $cer
    }
    finally {
        # The panel reads the PFX file; the store copy is not needed.
        Remove-Item "Cert:\LocalMachine\My\$($certificate.Thumbprint)" -DeleteKey
    }
    return @{ Path = $pfx; Password = $password; Thumbprint = $certificate.Thumbprint; Cer = $cer; SelfSigned = $true; Names = $names }
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
    $null = Export-PfxCertificate -Cert $certificate -FilePath $pfx -Password (ConvertTo-SecureString $password -AsPlainText -Force)
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
# host on a node; on the panel's own machine, the names its certificate was made for), localhost, and single-label
# names (a machine's name on the local network, never an internet site's: the installer's certificate always lists the
# panel machine's own) are the panel's. A wildcard stands for every name under it, so it never is, unless the panel's
# own certificate was made for it (-HostName); neither are other kinds of names.
function Get-ForeignCertificateNames($names, [string[]] $panelNames) {
    $panel = @($panelNames | Where-Object { $_ } | ForEach-Object { $_.TrimEnd('.').ToLowerInvariant() })
    foreach ($listed in $names.Dns) {
        $name = $listed.TrimEnd('.').ToLowerInvariant()
        if ($name -in $panel) { continue }
        if (-not $name.StartsWith('*.') -and ($name -eq 'localhost' -or -not $name.Contains('.'))) { continue }
        ConvertTo-PrintableText $listed
    }
    foreach ($address in $names.Ips) {
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
        $message = "$cer also names $($foreign -join ', '), which this check cannot tell are the panel's (only $hostName, localhost and machine names are): trusted by this machine, whoever holds its key could pass for them to it. Make the panel's certificate for the names nodes use and no others (-HostName when the panel is installed)"
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

# A certificate's names as they would be printed.
function Get-ListedNames($names) {
    foreach ($name in $names.Dns) { ConvertTo-PrintableText $name }
    foreach ($address in $names.Ips) { "$address" }
    foreach ($other in $names.Others) { $other }
}

# Trusts a panel's certificate machine-wide (the agent runs as SYSTEM) for the panel at $hostName, marked so -Uninstall
# removes it again. One that was already trusted is left unmarked: something else trusted it, and -Uninstall leaves it.
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

function Install-Panel {
    if (Test-Service $PanelService) { throw 'The panel is already installed on this machine. Use -Update to update it, or -Uninstall first.' }
    if ($AgentsPort -and $AgentsPort -eq $Port) { throw '-AgentsPort must be another port than -Port: browsers must never be asked for a client certificate.' }
    if ($SftpPort -eq $Port -or ($AgentsPort -and $SftpPort -eq $AgentsPort)) { throw '-SftpPort must be another port than -Port and -AgentsPort.' }
    $backup = $null
    if ($RestoreFrom) {
        $backup = @{
            Settings = Get-Content (Join-Path $RestoreFrom 'appsettings.Production.json') -Raw | ConvertFrom-Json
            Data = Join-Path $RestoreFrom 'panel-data'
            Dump = Join-Path $RestoreFrom 'pulsar.dump'
        }
        if (-not (Test-Path (Join-Path $backup.Data 'keys'))) { throw "$RestoreFrom is not a Pulsar backup (no panel-data\keys)." }
    }
    elseif (-not $AdminPassword) {
        $AdminPassword = Read-Host -AsSecureString "Password for the panel's first administrator, '$AdminUserName' (at least 12 characters)"
    }
    if (-not $backup -and (ConvertFrom-Secure $AdminPassword).Length -lt 12) { throw "The administrator's password needs at least 12 characters." }

    $connection = $ConnectionString
    if (-not $connection) {
        Write-Step 'Creating the panel database in the local PostgreSQL'
        $connection = New-PanelDatabase
    }
    if ($backup -and (Test-Path $backup.Dump)) {
        Write-Step 'Restoring the database from the backup'
        Restore-PanelDatabase $backup.Dump $connection
    }

    Write-Step "Installing the panel in $PanelDir"
    Copy-Program (Join-Path $PSScriptRoot 'panel') $PanelDir
    Copy-BackupScript

    # The service first: its virtual account exists from then on, and the folders below grant it access.
    $exe = Join-Path $PanelDir 'Pulsar.Api.exe'
    $result = Invoke-Native $Sc create $PanelService binPath= "`"$exe`"" start= delayed-auto obj= $PanelAccount DisplayName= 'Pulsar Panel'
    if ($result.ExitCode -ne 0) { throw "Could not create the panel service: $($result.Output)" }
    $null = Invoke-Native $Sc description $PanelService 'Pulsar game server panel (web interface and API).'
    $null = Invoke-Native $Sc failure $PanelService reset= 86400 actions= restart/5000/restart/15000/restart/60000
    $null = Invoke-Native $Sc failureflag $PanelService 1

    New-Item -ItemType Directory -Force $PanelData | Out-Null
    Set-PrivateAcl $PanelData -modify @($PanelAccount)
    foreach ($folder in 'keys', 'logs', 'tls') { New-Item -ItemType Directory -Force (Join-Path $PanelData $folder) | Out-Null }

    $setupToken = New-Secret 48
    if ($backup) {
        Write-Step 'Restoring the keys, certificates and settings from the backup'
        foreach ($folder in 'keys', 'keyring', 'tls') {
            if (Test-Path (Join-Path $backup.Data $folder)) { Copy-Item (Join-Path $backup.Data $folder) $PanelData -Recurse -Force }
        }
        $old = $backup.Settings
        $tlsPassword = $old.Kestrel.Endpoints.Https.Certificate.Password
        $pfx = Join-Path $PanelData 'tls\panel.pfx'
        $thumbprint = (New-Object Security.Cryptography.X509Certificates.X509Certificate2($pfx, $tlsPassword)).Thumbprint
        $certificate = @{ Path = $pfx; Password = $tlsPassword; Cer = (Join-Path $PanelData 'tls\panel.cer'); SelfSigned = $true; Thumbprint = $thumbprint }
        $Port = [int]($old.Kestrel.Endpoints.Https.Url -replace '^.*:(\d+)/?$', '$1')
        if ($old.Kestrel.Endpoints.PSObject.Properties['Agents']) {
            $AgentsPort = [int]($old.Kestrel.Endpoints.Agents.Url -replace '^.*:(\d+)/?$', '$1')
        }
        $keyRing = @{ Path = (Join-Path $PanelData 'keyring\keyring.pfx'); Password = $old.DataProtection.CertificatePassword }
        # The SFTP port is a panel setting kept in the database (restored with it): -SftpPort only names the port to open.
        if ($SftpPort -eq $Port -or ($AgentsPort -and $SftpPort -eq $AgentsPort)) { throw "-SftpPort must be another port than the backup's panel and agents' ports ($Port, $AgentsPort)." }
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
        Wait-PanelReady $client
        if ($backup) { return @{ Certificate = $certificate; Node = $null; Sftp = $null } }

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
    return @{ Certificate = $certificate; Node = $node; Sftp = $sftp }
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

function Install-Agent([string] $panel, [string] $token, [string] $panelKey) {
    if (Test-Service $AgentService) { throw 'The agent is already installed on this machine. Use -Update to update it, or -Uninstall first.' }
    Write-Step "Installing the agent in $AgentDir"
    Copy-Program (Join-Path $PSScriptRoot 'agent') $AgentDir
    # The token goes through the environment: the process list shows command lines.
    $arguments = @('enroll', '--panel', $panel, '--panel-key', $panelKey)
    if ($ServersRoot) { $arguments += @('--servers-root', $ServersRoot) }
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

function Uninstall-Agent {
    Write-Step 'Removing the agent'
    $exe = Join-Path $AgentDir 'PulsarAgent.exe'
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

# --- Main -------------------------------------------------------------------------------------------------------

Assert-Administrator
Assert-TrustedLocation
# Checked before anything changes: with an agent from the MSI, only a panel is this script's to install, update or remove.
$agentPackage = Get-AgentPackage
if ($agentPackage) {
    if ($Update -or $Uninstall) {
        if (-not (Test-Service $PanelService)) { throw (Get-AgentPackageMessage $agentPackage) }
    }
    elseif ($Mode -ne 'Panel') { throw (Get-AgentPackageMessage $agentPackage) }
}
if (-not $Uninstall) { Assert-RuntimeSupported }

if ($Update) {
    $any = $false
    if ($agentPackage) { Write-Warning "The agent is left as it is. $(Get-AgentPackageMessage $agentPackage)" }
    elseif (Test-Service $AgentService) { Update-Agent; $any = $true }
    if (Test-Service $PanelService) { Update-Panel; $any = $true }
    if (-not $any) { throw 'Pulsar is not installed on this machine.' }
    Write-Host 'Pulsar is updated.' -ForegroundColor Green
    return
}

if ($Uninstall) {
    if ($agentPackage) { Write-Warning "The agent is left as it is. $(Get-AgentPackageMessage $agentPackage)" }
    elseif (Test-Service $AgentService) { Uninstall-Agent }
    if (Test-Service $PanelService) { Uninstall-Panel }
    # What -Mode Agent -TrustCertificate or -Mode AllInOne trusted (an agent from the MSI may need it: then it stays).
    if (-not $agentPackage) { Remove-TrustedCertificates }
    $removed = if ($agentPackage) { 'The panel is removed (not the agent: see above).' } else { 'Pulsar is removed.' }
    Write-Host "$removed Kept: the database, $DataRoot (the panel's keys, needed to read its database's secrets, and logs) and the game servers' folders." -ForegroundColor Green
    return
}

if ($RestoreFrom -and $Mode -ne 'Panel') { throw '-RestoreFrom restores a panel: use it with -Mode Panel (nodes keep their agents).' }

switch ($Mode) {
    'Agent' {
        # The process list shows this script's own command line: the token can come from the environment instead.
        if (-not $Token -and $env:PULSAR_ENROLLMENT_TOKEN) { $Token = $env:PULSAR_ENROLLMENT_TOKEN }
        if (-not ($PanelUrl -and $Token -and $PanelKey)) {
            throw 'An agent needs -PanelUrl, -Token (or the PULSAR_ENROLLMENT_TOKEN environment variable) and -PanelKey: the panel shows them in Nodes > Add node.'
        }
        if ($TrustCertificate) {
            $panelUri = $null
            if (-not [Uri]::TryCreate($PanelUrl, [UriKind]::Absolute, [ref] $panelUri)) { throw "-PanelUrl is not an address: $PanelUrl" }
            Write-Step "Trusting the panel's certificate ($TrustCertificate)"
            Add-TrustedCertificate $TrustCertificate $panelUri.DnsSafeHost
        }
        Install-Agent $PanelUrl $Token $PanelKey
        Write-Host 'The agent is installed. The node appears as online in the panel.' -ForegroundColor Green
    }
    'Panel' {
        $installed = Install-Panel
        Write-Host ''
        Write-Host "The panel is running: https://$($env:COMPUTERNAME):$Port (also https://localhost:$Port on this machine)." -ForegroundColor Green
        if ($installed.Certificate.SelfSigned) {
            Write-Host "Its certificate is self-signed: browsers warn about it until it is trusted. Nodes need it trusted: copy"
            Write-Host "$($installed.Certificate.Cer) to each node and pass it with -TrustCertificate."
        }
        Write-SftpSummary $installed.Sftp
    }
    'AllInOne' {
        $installed = Install-Panel
        $certificate = $installed.Certificate
        if ($certificate.SelfSigned) {
            Write-Step "Trusting the panel's certificate on this machine (the agent and this PC's browsers check it)"
            Add-TrustedCertificate $certificate.Cer 'localhost' $certificate.Names
        }
        $enrollment = $installed.Node.enrollment
        Install-Agent "https://localhost:$Port" $enrollment.token $enrollment.panelKeyFingerprint
        Write-Host ''
        Write-Host "Pulsar is running: open https://localhost:$Port and sign in as '$AdminUserName'." -ForegroundColor Green
        Write-Host "From other computers: https://$($env:COMPUTERNAME):$Port (they warn about the self-signed certificate unless they trust $($certificate.Cer))."
        Write-SftpSummary $installed.Sftp
    }
}
