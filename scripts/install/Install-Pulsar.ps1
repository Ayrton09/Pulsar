<#
.SYNOPSIS
    Installs, updates or removes Pulsar on this Windows machine: the panel, the agent, or both paired together.

.DESCRIPTION
    Run it from the folder of the release ZIP, in an elevated Windows PowerShell (Run as administrator).

    -Mode AllInOne (the default): the panel and an agent on this PC, paired automatically. The panel's database is
    created in the local PostgreSQL (or -ConnectionString names one), its HTTPS certificate is made here (or
    -CertificatePath gives one) and trusted by this machine, the first administrator is created, and this PC is added
    as a node.

    -Mode Panel: the panel only. Nodes are added later from the panel (Nodes > Add node), which shows the command to
    run on each node.

    -Mode Agent: the agent only, with the values the panel's "Add node" dialog shows.

    -Update replaces the programs of whatever is installed and keeps settings and data. -Uninstall removes the
    services and programs; the database, the panel's keys and logs and the game servers' folders are kept.

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
    # Extra names for the self-signed certificate (the machine's name, its DNS name and localhost are always in it).
    [string[]] $HostName = @(),
    # A PFX certificate to use instead of a self-signed one.
    [string] $CertificatePath,
    [SecureString] $CertificatePassword,
    [string] $AdminUserName = 'admin',
    [SecureString] $AdminPassword,
    # A backup folder made by Backup-Pulsar.ps1 (with -Mode Panel).
    [string] $RestoreFrom,
    # Leaves the Windows Firewall alone (by default the panel's port is opened for the panel's program).
    [switch] $NoFirewall,

    # --- Agent ---
    [string] $PanelUrl,
    [string] $Token,
    [string] $PanelKey,
    # Where game servers are installed (default: C:\PulsarServers).
    [string] $ServersRoot,
    # The panel's certificate (.cer), when it is self-signed: trusted by this machine so the agent accepts it.
    [string] $TrustCertificate,

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
$TrustedMarker = Join-Path $PanelData 'tls\trusted-thumbprint.txt'

function Write-Step([string] $message) { Write-Host "==> $message" -ForegroundColor Cyan }

function Assert-Administrator {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script in an elevated PowerShell (right-click > Run as administrator).'
    }
    if (-not [Environment]::Is64BitOperatingSystem) { throw 'Pulsar needs 64-bit Windows.' }
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
    $result = Invoke-Native icacls.exe @arguments
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
        $created = Invoke-Native $psql @common -c "CREATE ROLE pulsar LOGIN PASSWORD '$password'"
        if ($created.ExitCode -ne 0) { throw "Could not create the 'pulsar' role: $($created.Output)" }
        $created = Invoke-Native $psql @common -c 'CREATE DATABASE pulsar OWNER pulsar'
        if ($created.ExitCode -ne 0) { throw "Could not create the 'pulsar' database: $($created.Output)" }
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
    $certificate = New-SelfSignedCertificate -DnsName $names -CertStoreLocation 'Cert:\LocalMachine\My' `
        -FriendlyName 'Pulsar panel' -KeyExportPolicy Exportable -KeyLength 2048 -NotAfter (Get-Date).AddYears(5)
    $password = New-Secret 32
    $null = Export-PfxCertificate -Cert $certificate -FilePath $pfx -Password (ConvertTo-SecureString $password -AsPlainText -Force)
    $null = Export-Certificate -Cert $certificate -FilePath $cer
    # The panel reads the PFX file; the store copy is not needed.
    Remove-Item "Cert:\LocalMachine\My\$($certificate.Thumbprint)" -DeleteKey
    return @{ Path = $pfx; Password = $password; Thumbprint = $certificate.Thumbprint; Cer = $cer; SelfSigned = $true }
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

function Add-TrustedCertificate([string] $cer) {
    $certificate = Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\LocalMachine\Root'
    return $certificate.Thumbprint
}

# --- Panel --------------------------------------------------------------------------------------------------------

function Write-PanelConfig([hashtable] $settings) {
    $settings | ConvertTo-Json -Depth 6 | Set-Content -Path $PanelConfig -Encoding UTF8
    # Database and certificate passwords: SYSTEM, Administrators and the panel's service account only.
    Set-PrivateAcl $PanelConfig -read @($PanelAccount)
}

function Install-Panel {
    if (Test-Service $PanelService) { throw 'The panel is already installed on this machine. Use -Update to update it, or -Uninstall first.' }
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

    # The service first: its virtual account exists from then on, and the folders below grant it access.
    $exe = Join-Path $PanelDir 'Pulsar.Api.exe'
    $result = Invoke-Native sc.exe create $PanelService binPath= "`"$exe`"" start= delayed-auto obj= $PanelAccount DisplayName= 'Pulsar Panel'
    if ($result.ExitCode -ne 0) { throw "Could not create the panel service: $($result.Output)" }
    $null = Invoke-Native sc.exe description $PanelService 'Pulsar game server panel (web interface and API).'
    $null = Invoke-Native sc.exe failure $PanelService reset= 86400 actions= restart/5000/restart/15000/restart/60000
    $null = Invoke-Native sc.exe failureflag $PanelService 1

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
        $keyRing = @{ Path = (Join-Path $PanelData 'keyring\keyring.pfx'); Password = $old.DataProtection.CertificatePassword }
    }
    else {
        $certificate = New-PanelCertificate
        $keyRing = New-KeyRingCertificate
    }

    $settings = @{
        ConnectionStrings = @{ Pulsar = $connection }
        Kestrel = @{ Endpoints = @{ Https = @{ Url = "https://*:$Port"; Certificate = @{ Path = $certificate.Path; Password = $certificate.Password } } } }
        DataProtection = @{ KeysPath = (Join-Path $PanelData 'keys'); CertificatePath = $keyRing.Path; CertificatePassword = $keyRing.Password }
        Pulsar = @{ LogsDirectory = (Join-Path $PanelData 'logs') }
    }
    if (-not $backup) { $settings.InitialSetup = @{ Token = $setupToken } }
    Write-PanelConfig $settings

    if (-not $NoFirewall) {
        Write-Step "Allowing the panel's HTTPS port ($Port) in Windows Firewall"
        Get-NetFirewallRule -Name 'Pulsar-Panel-HTTPS' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        $null = New-NetFirewallRule -Name 'Pulsar-Panel-HTTPS' -DisplayName 'Pulsar Panel (HTTPS)' -Direction Inbound `
            -Protocol TCP -LocalPort $Port -Program $exe -Action Allow
    }

    Invoke-PanelMigrations
    Write-Step 'Starting the panel'
    Start-Panel
    Initialize-PanelClient
    $client = [PulsarInstallerClient]::new("https://localhost:$Port", $certificate.Thumbprint)
    try {
        Wait-PanelReady $client
        if ($backup) { return @{ Certificate = $certificate; Node = $null } }

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
    }
    finally {
        $client.Dispose()
    }
    return @{ Certificate = $certificate; Node = $node }
}

function Update-Panel {
    Write-Step 'Updating the panel'
    Stop-Service $PanelService
    Copy-Program (Join-Path $PSScriptRoot 'panel') $PanelDir
    Invoke-PanelMigrations
    Start-Panel
}

function Uninstall-Panel {
    Write-Step 'Removing the panel'
    Stop-Service $PanelService -ErrorAction SilentlyContinue
    $null = Invoke-Native sc.exe delete $PanelService
    Get-NetFirewallRule -Name 'Pulsar-Panel-HTTPS' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    if (Test-Path $TrustedMarker) {
        $thumbprint = (Get-Content $TrustedMarker -Raw).Trim()
        Remove-Item "Cert:\LocalMachine\Root\$thumbprint" -ErrorAction SilentlyContinue
        Remove-Item $TrustedMarker
    }
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
        # Deletes the node key and the configuration, and removes the service.
        $result = Invoke-Native $exe unenroll
        Write-Host $result.Output
    }
    if (Test-Service $AgentService) {
        Stop-Service $AgentService -ErrorAction SilentlyContinue
        $null = Invoke-Native sc.exe delete $AgentService
    }
    Start-Sleep -Seconds 2
    Remove-Item $AgentDir -Recurse -Force -ErrorAction SilentlyContinue
}

# --- Main -------------------------------------------------------------------------------------------------------

Assert-Administrator
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
    $removed = if ($agentPackage) { 'The panel is removed (not the agent: see above).' } else { 'Pulsar is removed.' }
    Write-Host "$removed Kept: the database, $DataRoot (the panel's keys, needed to read its database's secrets, and logs) and the game servers' folders." -ForegroundColor Green
    return
}

if ($RestoreFrom -and $Mode -ne 'Panel') { throw '-RestoreFrom restores a panel: use it with -Mode Panel (nodes keep their agents).' }

switch ($Mode) {
    'Agent' {
        if (-not ($PanelUrl -and $Token -and $PanelKey)) {
            throw 'An agent needs -PanelUrl, -Token and -PanelKey: the panel shows them in Nodes > Add node.'
        }
        if ($TrustCertificate) {
            Write-Step "Trusting the panel's certificate ($TrustCertificate)"
            $null = Add-TrustedCertificate $TrustCertificate
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
    }
    'AllInOne' {
        $installed = Install-Panel
        $certificate = $installed.Certificate
        if ($certificate.SelfSigned) {
            Write-Step "Trusting the panel's certificate on this machine (the agent and this PC's browsers check it)"
            Add-TrustedCertificate $certificate.Cer | Set-Content $TrustedMarker
        }
        $enrollment = $installed.Node.enrollment
        Install-Agent "https://localhost:$Port" $enrollment.token $enrollment.panelKeyFingerprint
        Write-Host ''
        Write-Host "Pulsar is running: open https://localhost:$Port and sign in as '$AdminUserName'." -ForegroundColor Green
        Write-Host "From other computers: https://$($env:COMPUTERNAME):$Port (they warn about the self-signed certificate unless they trust $($certificate.Cer))."
    }
}
