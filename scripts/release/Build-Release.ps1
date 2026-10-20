# Builds the Windows release ZIP: the panel (with the web app) and the agent for win-x64, framework-dependent (the
# installer makes sure the machine has the .NET runtimes they need, downloaded from Microsoft and checked, and Microsoft
# Update keeps those patched), Install-Pulsar.ps1, the guides and the licenses, plus a SHA-256 checksum file; and the
# agent-only MSI next to it (Build-AgentMsi.ps1), for Intune, group policies and other silent installs, whose agent is
# self-contained (no .NET runtime needed on the target: a silent managed install has no step that would fetch one).
# With Inno Setup 7, also the setup wizards (scripts\release\setup): PulsarAgentSetup.exe, which goes into the panel's
# downloads folder (the panel serves it to new nodes, so the ZIP and PulsarSetup.exe carry it), and PulsarSetup.exe.
#   .\scripts\release\Build-Release.ps1 -Version 1.0.0
# Output: artifacts\release\pulsar-<version>-windows-x64.zip, pulsar-agent-<version>-windows-x64.msi,
# PulsarSetup-<version>.exe, PulsarAgentSetup-<version>.exe and a .sha256 of each
#
# Unsigned unless asked: with -SigningThumbprint (a certificate, or the one on a hardware token, in the current user's
# Personal store) or -TrustedSigningMetadata (Azure Trusted Signing), the release is Authenticode-signed with signtool.exe
# from the Windows SDK, SHA-256 digests and an RFC 3161 timestamp, and every signature is verified:
#   .\scripts\release\Build-Release.ps1 -Version 1.0.0 -RequireSetups -SigningThumbprint <40 hex digits>
# What is signed: the panel's and the agent's own programs (the assemblies of their projects and the projects they
# reference, and their apphosts: an explicit list, derived from the project files), before they are packed into the ZIP,
# the MSI and the setups, so the packed copies are signed; then the MSI; then both setups, which Inno Setup signs through
# its SignTool directive (the uninstaller too). Third-party libraries and the PowerShell scripts are not signed.
# Everything is built in a work folder of its own and moves into the output folder only when it is all there and every
# signature verified, replacing an earlier build's files all or nothing; a build that fails leaves the output folder as it was.
# When signing, the Inno Setup compiler too must be in a folder only administrators can change (Program Files).
# docs\architecture\09-roadmap.md ("Code signing") says how to get a certificate and how to run this.
param(
    [string] $Version = '0.0.0-dev',
    [string] $Output = (Join-Path $PSScriptRoot '..\..\artifacts\release'),
    # The web app is already built into src\Pulsar.Api\wwwroot (CI builds and tests it in its own job).
    [switch] $SkipWebBuild,
    # Inno Setup 7's command-line compiler (ISCC.exe). Without it, the per-user and the machine-wide installs of Inno
    # Setup 7 are looked in.
    [string] $InnoSetupCompiler,
    # Fails when Inno Setup 7 is not found, instead of building the ZIP and the MSI without the setups.
    [switch] $RequireSetups,
    # The oldest PostgreSQL 18 release Install-Pulsar.ps1 installs (release.json's postgresMinimum): the current 18.x when
    # the release is built (postgresql.org's versions.json, or the bin\postgres.exe --version of an up-to-date PostgreSQL).
    # It refuses an older one, which a list of versions or an installer given offline could pass off as current.
    [ValidatePattern('^18\.\d{1,4}$')] [string] $PostgresMinimum = '18.6',
    # Code signing with a certificate in the current user's Personal store (Cert:\CurrentUser\My), by its SHA-1 thumbprint:
    # a hardware token's certificate shows up there too, and signtool asks for its PIN. The private key never leaves the
    # store or the token, and no file or password is given to this script.
    [ValidatePattern('^[0-9A-Fa-f]{40}\z')] [string] $SigningThumbprint,
    # Code signing with Azure Trusted Signing: the metadata JSON (Endpoint, CodeSigningAccountName, CertificateProfileName)
    # signtool's /dlib reads. The account is signed in with Azure's own means (az login, AZURE_* environment variables, a
    # managed identity), never through this script, and its AZURE_* variables are kept from everything this script runs but
    # signtool. One of the two signing options, not both. Only the three values are read: signtool is given a fresh file
    # of them, in a folder of the current user alone.
    [ValidateNotNullOrEmpty()] [string] $TrustedSigningMetadata,
    # The Trusted Signing client's Azure.CodeSigning.Dlib.dll, required with -TrustedSigningMetadata. Its folder must be one
    # only administrators can change: the client tools install per user, so copy their folder to one under Program Files.
    [ValidateNotNullOrEmpty()] [string] $TrustedSigningDlib,
    # signtool.exe (x64). Without it, the newest installed Windows SDK's (Windows Kits\10\bin\<version>\x64). Either way, its
    # folder must be one only administrators can change.
    [ValidateNotNullOrEmpty()] [string] $SignTool,
    # The RFC 3161 timestamp authority: every signature is timestamped, so it stays valid after the certificate expires.
    # Azure Trusted Signing's certificates last days: Microsoft's own, http://timestamp.acs.microsoft.com, goes with them.
    [ValidatePattern('^https?://[A-Za-z0-9.-]+(:\d{1,5})?(/[A-Za-z0-9._~/-]*)?\z')] [string] $TimestampUrl = 'http://timestamp.digicert.com',
    # Fails an unsigned build (no signing option given): for a release that must not go out unsigned.
    [switch] $RequireSigning)
$ErrorActionPreference = 'Stop'
# The signing options that were given, whatever their value (an empty one is refused above, never taken for "unsigned").
$GivenSigningOptions = @('SigningThumbprint', 'TrustedSigningMetadata', 'TrustedSigningDlib', 'SignTool', 'TimestampUrl' | Where-Object { $PSBoundParameters.ContainsKey($_) })

# Inno Setup's signer: ISCC.exe and the DLLs next to it (its compiler, ISCmplr.dll, and the rest) must carry a valid
# Authenticode signature, in the file itself, of this organization, before each run: whoever could swap one would put
# code of their own in every setup this builds.
$InnoSetupSigner = 'Pyrsys B.V.'
# signtool.exe's: the Windows SDK's tools are Microsoft's.
$SignToolSigner = 'Microsoft Corporation'
# The code-signing purpose of a certificate's enhanced key usage.
$CodeSigningUsage = '1.3.6.1.5.5.7.3.3'
# What signtool shows (the UAC prompt, a file's properties) as the signed program's name and where to read about it.
$SignedDescription = 'Pulsar'
$SignedInformationUrl = 'https://github.com/Ayrton09/Pulsar'
# The signing in effect (Initialize-Signing), or $null: the release is unsigned.
$Signing = $null
# The folder this build works in (inside the output folder) until everything is signed and verified.
$Work = $null
# The AZURE_* variables kept out of this process's environment (Hide-AzureCredentials), name -> value.
$HiddenEnvironment = [ordered]@{}

# Why a file is not validly signed by $organization, or $null when it is: a valid Authenticode signature, in the file
# itself, of a certificate whose subject has exactly that one organization.
function Get-SignatureProblem([string] $path, [string] $organization) {
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    $subject = if ($signature.SignerCertificate) { "$($signature.SignerCertificate.Subject)" } else { '' }
    # The subject decoded one attribute to a line, not matched as text: a name that merely contains "O=Pyrsys B.V."
    # (in a CN, say) comes out quoted or on a line of its own, never as the organization line.
    $lines = if ($signature.SignerCertificate) { @($signature.SignerCertificate.SubjectName.Decode([System.Security.Cryptography.X509Certificates.X500DistinguishedNameFlags]::UseNewLines).Split([char[]]("`r`n"), [StringSplitOptions]::RemoveEmptyEntries)) } else { @() }
    $organizations = @($lines | Where-Object { $_ -clike 'O=*' })
    if ("$($signature.Status)" -cne 'Valid' -or "$($signature.SignatureType)" -cne 'Authenticode' -or $lines.Count -eq 0 -or @($lines | Where-Object { $_.Contains('"') }).Count -ne 0 -or $organizations.Count -ne 1 -or $organizations[0] -cne "O=$organization") {
        return "$($signature.Status), $subject"
    }
    return $null
}

function Assert-InnoSetupSigned([string] $compiler) {
    $folder = Split-Path -Parent $compiler
    $files = @(Get-Item -LiteralPath $compiler) + @(Get-ChildItem -LiteralPath $folder -File -Filter '*.dll')
    if (-not (Test-Path -LiteralPath (Join-Path $folder 'ISCmplr.dll') -PathType Leaf)) { throw "$folder has no ISCmplr.dll: it is not an Inno Setup install." }
    foreach ($file in $files) {
        $problem = Get-SignatureProblem $file.FullName $InnoSetupSigner
        if ($problem) {
            throw "$($file.FullName) is not validly signed by $InnoSetupSigner, Inno Setup's publisher ($problem): the setups are not built with it. Reinstall Inno Setup 7 from https://jrsoftware.org (in Program Files)."
        }
    }
}

# When signing, the Inno Setup compiler runs with Azure's credentials in its environment and hands them to signtool, so it is held to
# the rule signtool is: in a folder only SYSTEM, Administrators and TrustedInstaller can change (Program Files; a per-user install
# is refused). An unsigned release accepts it, as it always did.
function Assert-InnoSetupLocation([string] $compiler) {
    if ($Signing) { Assert-AdministratorsOnly (Split-Path -Parent $compiler) 'The folder of the Inno Setup compiler' }
}

# Inno Setup 7's ISCC.exe (-InnoSetupCompiler, or an install of it: Program Files first, where only administrators can
# change it, then a per-user install, which a signed release refuses), signed by its publisher; or $null (with a warning) when there
# is none.
function Find-InnoSetupCompiler {
    $candidates = @($InnoSetupCompiler)
    if (-not $InnoSetupCompiler) {
        $candidates = @(@($env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:LOCALAPPDATA) | Where-Object { $_ } | ForEach-Object {
                if ($_ -eq $env:LOCALAPPDATA) { Join-Path $_ 'Programs\Inno Setup 7\ISCC.exe' } else { Join-Path $_ 'Inno Setup 7\ISCC.exe' }
            })
    }
    foreach ($candidate in $candidates) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        Assert-InnoSetupSigned $candidate
        Assert-InnoSetupLocation $candidate
        $engine = & {
            $ErrorActionPreference = 'Continue'
            (& $candidate --version 2>&1 | ForEach-Object { "$_" }) -join ' '
        }
        # The scripts use Inno Setup 7 (SetupArchitecture, 64-bit setups).
        if ($engine -notmatch '^\s*(\d+)\.\d+' -or [int]$Matches[1] -lt 7) { throw "$candidate is not Inno Setup 7 or later ($engine)." }
        return (Resolve-Path -LiteralPath $candidate).Path
    }
    $message = "Inno Setup 7 was not found ($($candidates -join ', ')): the setups (PulsarSetup.exe, PulsarAgentSetup.exe) are not built."
    if ($RequireSetups -or $InnoSetupCompiler) { throw $message }
    Write-Warning $message
    return $null
}

# --- Code signing (off unless -SigningThumbprint or -TrustedSigningMetadata is given) -------------------------------------
# signtool.exe signs, always with a SHA-256 digest and an RFC 3161 timestamp, and every signature is verified before the
# build goes on. Nothing secret passes through this script: a certificate's private key stays in the store or on its
# token, and Azure signs in with its own credentials. What signs is only ever run in place from a folder only
# administrators can change, after its signature was checked, and the builds that run before it do not get the Azure
# credentials (only signtool, and the Inno Setup compiler that runs it, do).

# Who may change what signs: the rule Install-Pulsar.ps1 applies to the folder it runs from (and the agent to its own
# program), with the same functions (a test checks that they are the same): only SYSTEM, Administrators and
# TrustedInstaller may own or modify a file or folder, or rename or replace a folder above it. Links are refused.
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

# Refuses a file or folder that anyone but SYSTEM, Administrators and TrustedInstaller could change (a folder: everything
# in it, and the folders above it). What signs must not be swappable by the builds that run before it, or by anything
# else the current user runs (a build's own npm or MSBuild code, say), which could then sign what it likes, or take
# Azure's credentials.
function Assert-AdministratorsOnly([string] $path, [string] $what) {
    $findings = @(Find-UntrustedWriters $path)
    if ($findings.Count -gt 0) {
        throw "$what ($path) could be changed by accounts other than SYSTEM, Administrators and TrustedInstaller ($(@($findings | Select-Object -First 5) -join '; ')): what signs the release must not be. Use a copy in a folder only administrators can change, in $env:ProgramFiles for example, and give its path."
    }
}

# Whether a folder is as New-SigningFolder makes it: not a link, owned by the current user (or by Administrators, which
# an elevated process owns what it makes as), nothing inherited, and nobody but the current user allowed in.
function Test-CurrentUserOnly([string] $path) {
    try {
        $item = Get-Item -LiteralPath $path -Force
        if ($item -isnot [IO.DirectoryInfo] -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return $false }
        $user = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        $acl = Get-Acl -LiteralPath $path
        $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier])
        if ($null -eq $owner -or $owner.Value -notin $user, 'S-1-5-32-544') { return $false }
        $allowed = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) | Where-Object { $_.AccessControlType -eq 'Allow' } |
            ForEach-Object { $_.IdentityReference.Value } | Select-Object -Unique)
        return $acl.AreAccessRulesProtected -and $allowed.Count -gt 0 -and @($allowed | Where-Object { $_ -ne $user }).Count -eq 0
    }
    catch {
        return $false
    }
}

# A new folder for what this build hands to signtool (the Trusted Signing metadata), for the current user alone.
function New-SigningFolder {
    $folder = Join-Path ([IO.Path]::GetTempPath()) ('pulsar-signing-' + [Guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory $folder
    try {
        $user = [Security.Principal.WindowsIdentity]::GetCurrent().User
        # Only the permissions are read and written, not the owner (Get-Acl and Set-Acl write that too, which an
        # administrator's token that is not elevated may not give to the Administrators group).
        $directory = Get-Item -LiteralPath $folder
        $sections = [Security.AccessControl.AccessControlSections]::Access
        $acl = if ($directory.PSObject.Methods['GetAccessControl']) { $directory.GetAccessControl($sections) } else { [IO.FileSystemAclExtensions]::GetAccessControl($directory, $sections) }
        $acl.SetAccessRuleProtection($true, $false)
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($user, 'FullControl', 'ContainerInherit, ObjectInherit', 'None', 'Allow')))
        if ($directory.PSObject.Methods['SetAccessControl']) { $directory.SetAccessControl($acl) } else { [IO.FileSystemAclExtensions]::SetAccessControl($directory, $acl) }
        if (-not (Test-CurrentUserOnly $folder)) { throw "$folder could not be made for the current user alone." }
    }
    catch {
        Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction SilentlyContinue
        throw
    }
    return $folder
}

# The AZURE_* variables (the credentials Azure's client reads) out of this process's environment, so the builds it starts
# (npm, dotnet, their plugins and build tasks) do not inherit them. Set-AzureCredentials gives them back around signtool and
# the Inno Setup compiler that runs it; Restore-AzureCredentials, when the script ends, to the shell it was run from.
# They are read and set only through .NET, never as a cmdlet's parameter or output (Set-Item Env:, Get-ChildItem Env:): PowerShell's
# module logging records those.
function Hide-AzureCredentials {
    foreach ($name in @([Environment]::GetEnvironmentVariables('Process').Keys | Where-Object { $_ -like 'AZURE_*' })) {
        $HiddenEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $null, 'Process')
    }
}

function Set-AzureCredentials {
    foreach ($name in @($HiddenEnvironment.Keys)) { [Environment]::SetEnvironmentVariable($name, $HiddenEnvironment[$name], 'Process') }
}

function Remove-AzureCredentials {
    foreach ($name in @($HiddenEnvironment.Keys)) { [Environment]::SetEnvironmentVariable($name, $null, 'Process') }
}

function Restore-AzureCredentials {
    Set-AzureCredentials
    $HiddenEnvironment.Clear()
}

# The folders of the Windows SDKs ("Windows Kits\10"): where Windows Installer registered them, and the default ones.
function Get-WindowsKitsRoots {
    $roots = @()
    foreach ($key in 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows Kits\Installed Roots') {
        $registered = Get-ItemProperty -LiteralPath $key -Name 'KitsRoot10' -ErrorAction SilentlyContinue
        if ($registered -and $registered.PSObject.Properties['KitsRoot10'] -and $registered.KitsRoot10) { $roots += $registered.KitsRoot10 }
    }
    foreach ($programs in ${env:ProgramFiles(x86)}, $env:ProgramFiles) {
        if ($programs) { $roots += Join-Path $programs 'Windows Kits\10' }
    }
    return @($roots | Select-Object -Unique)
}

# signtool.exe (x64) of the newest Windows SDK installed (<root>\bin\<version>\x64, then <root>\bin\x64 of the older
# layout), or -SignTool; throws when there is none.
function Find-SignTool([string[]] $kitsRoots = (Get-WindowsKitsRoots)) {
    if ($SignTool) {
        if ((Split-Path -Leaf $SignTool) -ine 'signtool.exe' -or -not (Test-Path -LiteralPath $SignTool -PathType Leaf)) { throw "-SignTool must name an existing signtool.exe ($SignTool)." }
        return (Resolve-Path -LiteralPath $SignTool).Path
    }
    $found = @()
    foreach ($root in $kitsRoots) {
        $bin = Join-Path $root 'bin'
        $found += [pscustomobject]@{ Version = [version]'0.0.0.0'; Path = Join-Path $bin 'x64\signtool.exe' }
        foreach ($folder in @(Get-ChildItem -LiteralPath $bin -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' })) {
            $found += [pscustomobject]@{ Version = [version]$folder.Name; Path = Join-Path $folder.FullName 'x64\signtool.exe' }
        }
    }
    $found = @($found | Where-Object { Test-Path -LiteralPath $_.Path -PathType Leaf } | Sort-Object Version -Descending)
    if ($found.Count -eq 0) {
        throw "Signing was asked for, but signtool.exe was not found (looked in $($kitsRoots -join ', ')). Install the Windows SDK (its Signing Tools for Desktop Apps feature is enough), or give -SignTool."
    }
    return $found[0].Path
}

# The DLLs signtool.exe imports that the Windows SDK ships beside it, which it loads before Windows' own copies.
$SignToolLibraries = 'mssign32.dll', 'wintrust.dll'

# signtool.exe, and those DLLs when they are beside it, must be validly signed by Microsoft: whoever could swap one would
# sign, or keep from signing, what they like.
function Assert-SignToolSigned([string] $tool) {
    $problem = Get-SignatureProblem $tool $SignToolSigner
    if ($problem) { throw "$tool is not validly signed by $SignToolSigner, signtool.exe's publisher ($problem): nothing is signed with it. Reinstall the Windows SDK (its Signing Tools for Desktop Apps)." }
    $original = (Get-Item -LiteralPath $tool).VersionInfo.OriginalFilename
    if ($original -ine 'signtool.exe') { throw "$tool is signed by $SignToolSigner, but it is not signtool.exe (its original file name is '$original')." }
    foreach ($name in $SignToolLibraries) {
        $library = Join-Path (Split-Path -Parent $tool) $name
        if (-not (Test-Path -LiteralPath $library -PathType Leaf)) { continue }
        $problem = Get-SignatureProblem $library $SignToolSigner
        if ($problem) { throw "$library is not validly signed by $SignToolSigner ($problem): nothing is signed with the signtool.exe that loads it. Reinstall the Windows SDK (its Signing Tools for Desktop Apps)." }
    }
}

# Why a certificate cannot sign the release, or nothing: it must hold its private key (a token's certificate shows up in
# the store with the key on the token), be within its validity and be for code signing.
function Assert-SigningCertificate([System.Security.Cryptography.X509Certificates.X509Certificate2] $certificate) {
    $name = "The signing certificate $($certificate.Thumbprint) ($($certificate.Subject))"
    if (-not $certificate.HasPrivateKey) { throw "$name has no private key in this account's store." }
    $now = [DateTime]::Now
    if ($now -lt $certificate.NotBefore -or $now -gt $certificate.NotAfter) { throw "$name is valid from $($certificate.NotBefore) to $($certificate.NotAfter), not now." }
    $usages = @($certificate.Extensions | Where-Object { $_ -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] } |
            ForEach-Object { $_.EnhancedKeyUsages } | ForEach-Object { $_.Value })
    if ($usages -notcontains $CodeSigningUsage) { throw "$name is not for code signing (enhanced key usage $CodeSigningUsage)." }
}

# The three values Azure Trusted Signing's client reads from its metadata file (Endpoint, CodeSigningAccountName and
# CertificateProfileName, by exactly these names), checked: the endpoint must be one of the service's own
# (https://<region>.codesigning.azure.net), so a metadata file that was changed cannot send the signing request, and the
# credentials with it, anywhere else. Only these values are used: the file is not passed on (New-SigningMetadata writes a
# fresh one).
function Read-TrustedSigningMetadata([string] $path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "-TrustedSigningMetadata must name an existing JSON file ($path)." }
    try { $metadata = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json }
    catch { throw "-TrustedSigningMetadata $path is not JSON ($($_.Exception.Message))." }
    $names = @($metadata.PSObject.Properties | ForEach-Object { $_.Name })
    $values = [ordered]@{}
    foreach ($name in 'Endpoint', 'CodeSigningAccountName', 'CertificateProfileName') {
        if ($names -cnotcontains $name -or $metadata.$name -isnot [string] -or -not $metadata.$name.Trim()) { throw "-TrustedSigningMetadata $path has no text for '$name'." }
        if ($metadata.$name.Length -gt 200 -or $metadata.$name -cnotmatch '^[\x21-\x7E]+( [\x21-\x7E]+)*\z') { throw "-TrustedSigningMetadata $path : '$name' is not plain text of up to 200 characters." }
        $values[$name] = $metadata.$name
    }
    if ($values['Endpoint'] -cnotmatch '^https://[a-z0-9-]+\.codesigning\.azure\.net/?\z') { throw "-TrustedSigningMetadata $path : the Endpoint must be Trusted Signing's own, https://<region>.codesigning.azure.net ($($values['Endpoint']))." }
    return $values
}

# Writes the validated metadata as a new file in a new folder of the current user alone (what signtool is given), and
# returns where it is and exactly what it holds, for Assert-SigningTools to check again.
function New-SigningMetadata($values) {
    $folder = New-SigningFolder
    try {
        $text = ([ordered]@{ Endpoint = $values['Endpoint']; CodeSigningAccountName = $values['CodeSigningAccountName']; CertificateProfileName = $values['CertificateProfileName'] } | ConvertTo-Json)
        $path = Join-Path $folder 'metadata.json'
        [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding($false)))
    }
    catch {
        Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction SilentlyContinue
        throw
    }
    return [pscustomobject]@{ Folder = $folder; Path = $path; Text = $text }
}

# The Trusted Signing client's Azure.CodeSigning.Dlib.dll that -TrustedSigningDlib names, found; its full path.
function Find-TrustedSigningClient {
    if (-not $TrustedSigningDlib) { throw '-TrustedSigningDlib is required with -TrustedSigningMetadata: the path of Azure.CodeSigning.Dlib.dll in a folder only administrators can change (copy the Trusted Signing client tools there, from their per-user install folder).' }
    if ((Split-Path -Leaf $TrustedSigningDlib) -ine 'Azure.CodeSigning.Dlib.dll' -or -not (Test-Path -LiteralPath $TrustedSigningDlib -PathType Leaf)) {
        throw "Azure.CodeSigning.Dlib.dll was not found ($TrustedSigningDlib). Install Microsoft's Trusted Signing client tools, and give -TrustedSigningDlib the one in a folder only administrators can change."
    }
    return (Resolve-Path -LiteralPath $TrustedSigningDlib).Path
}

# signtool loads the client and the .NET libraries beside it, and they sign in to Azure: every program there must be
# validly signed by Microsoft, the client included.
function Assert-TrustedSigningClient([string] $dlib) {
    $folder = Split-Path -Parent $dlib
    foreach ($file in @(Get-ChildItem -LiteralPath $folder -File | Where-Object { $_.Extension -ieq '.dll' -or $_.Extension -ieq '.exe' })) {
        $problem = Get-SignatureProblem $file.FullName $SignToolSigner
        if ($problem) { throw "$($file.FullName) is not validly signed by $SignToolSigner ($problem): Trusted Signing's client is not used with it. Reinstall Microsoft's Trusted Signing client tools." }
    }
}

# Everything that signs, checked again each time before it is used (the builds that ran meanwhile ran other people's code):
# signtool.exe (and the libraries beside it) validly signed by Microsoft, in a folder only administrators can change; the
# same for the Trusted Signing client; and the metadata file it reads, still as this script wrote it, in a folder of the
# current user alone.
function Assert-SigningTools {
    Assert-SignToolSigned $Signing.SignTool
    Assert-AdministratorsOnly (Split-Path -Parent $Signing.SignTool) 'The folder of signtool.exe'
    if ($Signing.Dlib) {
        Assert-TrustedSigningClient $Signing.Dlib
        Assert-AdministratorsOnly (Split-Path -Parent $Signing.Dlib) 'The folder of the Trusted Signing client'
        if (-not (Test-CurrentUserOnly $Signing.MetadataFolder) -or -not (Test-Path -LiteralPath $Signing.MetadataPath -PathType Leaf) -or
            [IO.File]::ReadAllText($Signing.MetadataPath) -cne $Signing.MetadataText) {
            throw "The Trusted Signing metadata signtool is given ($($Signing.MetadataPath)) is not as this script wrote it, or its folder is no longer the current user's alone."
        }
    }
}

# What signing the release will do, from the options, with everything it needs checked before the long builds; or $null
# for an unsigned release (no signing option given, and not -RequireSigning).
function Initialize-Signing {
    $byStore = $GivenSigningOptions -contains 'SigningThumbprint'
    $byAzure = $GivenSigningOptions -contains 'TrustedSigningMetadata'
    if (-not $byStore -and -not $byAzure) {
        if ($GivenSigningOptions.Count -gt 0) { throw "These options only apply to a signed release (-$($GivenSigningOptions -join ', -')): give -SigningThumbprint or -TrustedSigningMetadata too, or none of them for an unsigned one." }
        if ($RequireSigning) { throw '-RequireSigning: this release would be unsigned. Give -SigningThumbprint or -TrustedSigningMetadata.' }
        return $null
    }
    if ($byStore -and $byAzure) { throw 'Give -SigningThumbprint or -TrustedSigningMetadata, not both.' }
    if ($byStore -and ($GivenSigningOptions -contains 'TrustedSigningDlib')) { throw '-TrustedSigningDlib goes with -TrustedSigningMetadata.' }
    $tool = Find-SignTool
    Assert-SignToolSigned $tool
    Assert-AdministratorsOnly (Split-Path -Parent $tool) 'The folder of signtool.exe'
    $arguments = @('sign', '/fd', 'sha256', '/tr', $TimestampUrl, '/td', 'sha256', '/d', $SignedDescription, '/du', $SignedInformationUrl)
    $thumbprint = $null
    $subject = $null
    $dlib = $null
    $metadata = $null
    if ($byStore) {
        $thumbprint = $SigningThumbprint.ToUpperInvariant()
        $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$thumbprint" -ErrorAction SilentlyContinue
        if (-not $certificate) { throw "No certificate with thumbprint $thumbprint is in the current user's Personal store (Cert:\CurrentUser\My). A hardware token's has to be plugged in for it to show." }
        Assert-SigningCertificate $certificate
        $subject = $certificate.Subject
        $arguments += @('/sha1', $thumbprint, '/s', 'My')
        Write-Host "Signing with $subject ($thumbprint), timestamps from $TimestampUrl"
    }
    else {
        $values = Read-TrustedSigningMetadata $TrustedSigningMetadata
        $dlib = Find-TrustedSigningClient
        Assert-TrustedSigningClient $dlib
        Assert-AdministratorsOnly (Split-Path -Parent $dlib) 'The folder of the Trusted Signing client'
        # /dlib came with the Windows SDK 10.0.22621.
        $info = (Get-Item -LiteralPath $tool).VersionInfo
        if ([version]('{0}.{1}.{2}.{3}' -f $info.ProductMajorPart, $info.ProductMinorPart, $info.ProductBuildPart, $info.ProductPrivatePart) -lt [version]'10.0.22621.755') {
            throw "$tool ($($info.ProductVersion)) is too old to sign with Trusted Signing (/dlib needs 10.0.22621.755 or later): install a newer Windows SDK."
        }
        # The last thing that can fail: it makes a folder, which the end of the build removes.
        $metadata = New-SigningMetadata $values
        $arguments += @('/dlib', $dlib, '/dmdf', $metadata.Path)
        Hide-AzureCredentials
        Write-Host "Signing with Azure Trusted Signing (account $($values['CodeSigningAccountName']), profile $($values['CertificateProfileName'])), timestamps from $TimestampUrl"
    }
    return [pscustomobject]@{
        SignTool = $tool; Arguments = [string[]] $arguments; Thumbprint = $thumbprint; Subject = $subject; Dlib = $dlib
        MetadataFolder = $(if ($metadata) { $metadata.Folder }); MetadataPath = $(if ($metadata) { $metadata.Path }); MetadataText = $(if ($metadata) { $metadata.Text })
    }
}

# Runs signtool; throws with its output unless it exits with 0 (its 2, a warning, counts as a failure: a timestamp it
# could not get is one). -WithCredentials gives it Azure's credentials, for the run only.
function Invoke-SignTool([string] $tool, [string[]] $arguments, [string] $failure, [switch] $WithCredentials) {
    $result = & {
        $ErrorActionPreference = 'Continue'
        if ($WithCredentials) { Set-AzureCredentials }
        try {
            $output = @(& $tool @arguments 2>&1 | ForEach-Object { "$_" })
            [pscustomobject]@{ Output = $output; Code = $LASTEXITCODE }
        }
        finally { if ($WithCredentials) { Remove-AzureCredentials } }
    }
    if ($result.Code -ne 0) { throw "$failure (signtool exit code $($result.Code))$([Environment]::NewLine)$($result.Output -join [Environment]::NewLine)" }
}

# Each file must verify with signtool (/pa: the default Authenticode policy, up to a root this PC trusts) and carry a valid
# signature in the file itself, timestamped, of the certificate asked to sign (by its thumbprint, when it is known) and of
# one subject for the whole release.
function Assert-Signed([string[]] $files) {
    foreach ($file in $files) {
        Invoke-SignTool $Signing.SignTool @('verify', '/pa', '/q', $file) "$file does not verify after signing."
        $signature = Get-AuthenticodeSignature -LiteralPath $file
        $subject = if ($signature.SignerCertificate) { "$($signature.SignerCertificate.Subject)" } else { '' }
        if ("$($signature.Status)" -cne 'Valid' -or "$($signature.SignatureType)" -cne 'Authenticode' -or -not $signature.SignerCertificate -or -not $signature.TimeStamperCertificate) {
            throw "$file is not validly signed and timestamped after signing ($($signature.Status), $($signature.SignatureType), $subject)."
        }
        if ($Signing.Thumbprint -and $signature.SignerCertificate.Thumbprint -ne $Signing.Thumbprint) { throw "$file is signed with certificate $($signature.SignerCertificate.Thumbprint), not $($Signing.Thumbprint)." }
        if (-not $Signing.Subject) { $Signing.Subject = $subject }
        elseif ($Signing.Subject -cne $subject) { throw "$file is signed by $subject, not by $($Signing.Subject) like the rest of the release." }
    }
}

# Signs the files in place and verifies them, in batches: a hardware token asks for its PIN once for each signtool run.
function Invoke-Signing([string[]] $files, [string] $what) {
    if ($files.Count -eq 0) { throw "There is nothing to sign for $what." }
    Assert-SigningTools
    for ($first = 0; $first -lt $files.Count; $first += 40) {
        $batch = @($files[$first..([Math]::Min($first + 39, $files.Count - 1))])
        Invoke-SignTool $Signing.SignTool (@($Signing.Arguments) + $batch) "Signing $what failed." -WithCredentials
    }
    Assert-Signed $files
    Write-Host "Signed $what ($($files.Count) file(s)) by $($Signing.Subject)."
}

# The assembly names of a project and of the projects it references, transitively, from the project files (the project's
# own first): the assemblies of a published folder that are Pulsar's own.
function Get-PulsarAssemblies([string] $project) {
    $assemblies = [ordered]@{}
    $pending = New-Object Collections.Generic.Queue[string]
    $pending.Enqueue([IO.Path]::GetFullPath($project))
    while ($pending.Count -gt 0) {
        $path = $pending.Dequeue()
        if ($assemblies.Contains($path)) { continue }
        $xml = New-Object Xml.XmlDocument
        $xml.Load($path)
        $assembly = [IO.Path]::GetFileNameWithoutExtension($path)
        $custom = @($xml.SelectNodes('//AssemblyName') | ForEach-Object { $_.InnerText.Trim() } | Where-Object { $_ })
        if ($custom.Count -gt 0) { $assembly = $custom[0] }
        $assemblies[$path] = $assembly
        foreach ($reference in @($xml.SelectNodes('//ProjectReference'))) {
            $pending.Enqueue([IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $path) $reference.GetAttribute('Include'))))
        }
    }
    return @($assemblies.Values)
}

# The programs Pulsar ships in a published folder, from the project (an explicit list, not a name pattern): the assemblies
# of the project and of the projects it references (<name>.dll, and the satellites in a folder of their own, <name>.resources.dll),
# and the project's apphost (<name>.exe). Each must be there, and no other Pulsar*.exe or Pulsar*.dll may be (a new
# project, or something else the build left, is not signed by accident: add it here on purpose). $extra are relative
# paths of other files this script put in the folder (the agent's setup in the panel's downloads), which are expected
# there and returned when they are.
function Get-PulsarPrograms([string] $folder, [string] $project, [string[]] $extra = @()) {
    $assemblies = @(Get-PulsarAssemblies $project)
    $expected = @($assemblies | ForEach-Object { "$_.dll" }) + @("$($assemblies[0]).exe")
    $root = (Resolve-Path -LiteralPath $folder).Path.TrimEnd('\')
    $programs = @()
    foreach ($file in $expected) {
        $path = Join-Path $root $file
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "$root has no $file to sign." }
        $programs += $path
    }
    foreach ($file in @(Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object { $_.Name -like 'Pulsar*' -and ($_.Extension -ieq '.exe' -or $_.Extension -ieq '.dll') })) {
        $relative = $file.FullName.Substring($root.Length + 1)
        $satellite = $false
        if ($relative -imatch '^[^\\]+\\[^\\]+\z' -and $file.Name -imatch '^(?<assembly>.+)\.resources\.dll\z') { $satellite = $assemblies -contains $Matches['assembly'] }
        if ($programs -contains $file.FullName) { continue }
        if ($satellite) { $programs += $file.FullName; continue }
        if ($extra -contains $relative) { continue }
        throw "$root has a program Pulsar does not list for signing: $relative. A new project, or something a build left? Nothing is signed until it is added to (or removed from) the release on purpose."
    }
    foreach ($file in $extra) {
        if (Test-Path -LiteralPath (Join-Path $root $file) -PathType Leaf) { $programs += Join-Path $root $file }
    }
    return $programs
}

# The panel's and the agent's programs (the MSI's agent too): $ProgramSets, folder and project of each, and the files of
# this script's own the packages carry besides, are re-read and verified signed right before a package is made of them
# (the builds and tools that ran since could have changed one, or left another).
function Assert-ProgramsSigned {
    if (-not $Signing) { return }
    Assert-SigningTools
    $files = @()
    foreach ($set in $ProgramSets) {
        $files += Get-PulsarPrograms $set.Folder $set.Project $set.Extra
    }
    Assert-Signed $files
}

# One word of a sign tool command for Inno Setup: quoted ($q) when it has a space, and with $ doubled ($$).
function Format-InnoToolWord([string] $word) {
    if ($word.Contains('"')) { throw "A quote cannot go into a command for Inno Setup ($word)." }
    $escaped = $word.Replace('$', '$$')
    if ($escaped -match '\s') { return '$q' + $escaped + '$q' }
    return $escaped
}

# The command Inno Setup runs for each file it signs (the setup, the uninstaller; $f is the file): signtool with the
# release's signing arguments. It holds a path and public values, nothing secret.
function Get-InnoSignCommand([string] $tool) {
    return ((@($tool) + @($Signing.Arguments) | ForEach-Object { Format-InnoToolWord $_ }) -join ' ') + ' $f'
}

# Compiles one of scripts\release\setup's setups from the staged release into $Work; its path. A warning fails the
# build like an error. Signed, Inno Setup signs the setup and its uninstaller with the one sign tool the scripts name
# (setup\Common.iss), defined here as signtool in place; any it has from its IDE is left out. It runs with Azure's
# credentials, which it hands to signtool.
function Build-Setup([string] $compiler, [string] $script, [string] $release, [string] $numeric) {
    $arguments = @('--quiet', '--messages-jsonl', "/DVersion=$Version", "/DNumericVersion=$numeric.0", "/DRelease=$release", "/O$Work", (Join-Path $PSScriptRoot "setup\$script"))
    if ($Signing) {
        Assert-SigningTools
        Assert-InnoSetupLocation $compiler
        $arguments = @('/DSignSetups', '/NI', "/Spulsarsign=$(Get-InnoSignCommand $Signing.SignTool)") + $arguments
    }
    # Checked again right before it runs: the web build (npm ci) ran code from the registry since it was found.
    Assert-InnoSetupSigned $compiler
    $lines = & {
        $ErrorActionPreference = 'Continue'
        if ($Signing) { Set-AzureCredentials }
        try { @(& $compiler @arguments 2>&1 | ForEach-Object { "$_" }) }
        finally { if ($Signing) { Remove-AzureCredentials } }
    }
    $code = $LASTEXITCODE
    $messages = @($lines | Where-Object { $_ -match '^\s*\{' } | ForEach-Object { $_ | ConvertFrom-Json })
    $other = @($lines | Where-Object { $_ -notmatch '^\s*\{' -and $_.Trim() })
    foreach ($message in $messages) { Write-Host "${script}: $($message.severity): $($message.message)" }
    if ($code -ne 0 -or $messages.Count -gt 0) {
        throw "$script did not compile cleanly (ISCC exit code $code, $($messages.Count) message(s)).$([Environment]::NewLine)$($other -join [Environment]::NewLine)"
    }
    $name = [IO.Path]::GetFileNameWithoutExtension($script)
    $setup = Join-Path $Work "$name-$Version.exe"
    if (-not (Test-Path -LiteralPath $setup)) { throw "$script compiled, but $setup is missing." }
    if ($Signing) {
        Assert-SigningTools
        Assert-Signed @($setup)
    }
    return $setup
}

# Moves what a finished build made from its work folder into the output folder, replacing the same names of an earlier
# build, all or nothing: only called when everything is there, signed and verified. The earlier files are moved aside first
# (a file something holds open cannot be, which fails here, before any new file is in), then the new ones move in, and the
# earlier ones are deleted last. If anything fails, the new ones that moved in move out again and the earlier ones come back.
# Only when even that fails (a file taken meanwhile) does the error say where the earlier files are: in a .replaced- folder
# in the output folder, left for that.
function Publish-Release([string] $work, [string] $output, [string[]] $names) {
    # Nothing moves unless everything is there.
    foreach ($item in $names) {
        if (-not (Test-Path -LiteralPath (Join-Path $work $item))) { throw "The build did not make $item." }
    }
    $aside = Join-Path $output ('.replaced-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    $null = New-Item -ItemType Directory $aside
    $setAside = @()
    $movedIn = @()
    try {
        foreach ($item in $names) {
            $target = Join-Path $output $item
            if (Test-Path -LiteralPath $target) {
                Move-Item -LiteralPath $target -Destination (Join-Path $aside $item)
                $setAside += $item
            }
        }
        foreach ($item in $names) {
            Move-Item -LiteralPath (Join-Path $work $item) -Destination (Join-Path $output $item)
            $movedIn += $item
        }
    }
    catch {
        $failure = $_.Exception.Message
        $stuck = @()
        foreach ($item in $movedIn) {
            try { Move-Item -LiteralPath (Join-Path $output $item) -Destination (Join-Path $work $item) } catch { $stuck += "$item (new, in $output)" }
        }
        foreach ($item in $setAside) {
            try { Move-Item -LiteralPath (Join-Path $aside $item) -Destination (Join-Path $output $item) } catch { $stuck += "$item (earlier, in $aside)" }
        }
        if ($stuck.Count -eq 0) {
            Remove-FolderPatiently $aside
            $left = if (Test-Path -LiteralPath $aside) { " (the empty $aside could not be deleted: delete it)" } else { '' }
            throw "The release was not published ($failure): the earlier build's files in $output are as they were$left."
        }
        throw "The release was not published ($failure), and these could not be moved back: $($stuck -join '; '). The earlier build's files are in $aside."
    }
    Remove-FolderPatiently $aside
    if (Test-Path -LiteralPath $aside) { Write-Warning "The earlier build's files could not all be deleted: $aside is theirs, delete it." }
}

# Deletes a folder, trying again for a moment: right after files are moved out of it or written in it, an antivirus scan or
# the search indexer can still hold it. What cannot be deleted after that is left, with no error; the callers say so.
function Remove-FolderPatiently([string] $path) {
    for ($attempt = 0; $attempt -lt 20 -and (Test-Path -LiteralPath $path); $attempt++) {
        if ($attempt -gt 0) { Start-Sleep -Milliseconds 100 }
        Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# Removes a build's work folder and what it kept in the folder of the current user alone, however the build ended.
function Remove-BuildWork([string] $work, $signing) {
    if ($work -and (Split-Path -Leaf $work) -like '.build-*') { Remove-FolderPatiently $work }
    if ($signing -and $signing.MetadataFolder -and (Split-Path -Leaf $signing.MetadataFolder) -like 'pulsar-signing-*') { Remove-FolderPatiently $signing.MetadataFolder }
}

function Write-Checksum([string] $path) {
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $(Split-Path $path -Leaf)" | Set-Content "$path.sha256" -Encoding ASCII
    return $hash
}

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$name = "pulsar-$Version-windows-x64"
# The setups' Windows version resource takes numbers only.
$numeric = ($Version -split '[-+]')[0]
if ($numeric -notmatch '^\d+\.\d+\.\d+$') { throw "The version must start with major.minor.patch, not '$Version'." }
New-Item -ItemType Directory -Force $Output | Out-Null
$Output = (Resolve-Path $Output).Path
# Everything is built in a work folder of its own, and moves into $Output only when it is all there, signed and verified, and
# replaces an earlier build's files all or nothing: a build that fails leaves $Output as it was (an earlier build of the same
# version included).
$Work = Join-Path $Output ('.build-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
try {
    # Before the long builds: whether the release can be signed if it is to be (Azure's credentials leave the environment here,
    # before anything is run), and whether the setups can be built.
    $Signing = Initialize-Signing
    $iscc = Find-InnoSetupCompiler
    $null = New-Item -ItemType Directory $Work
    $staging = Join-Path $Work $name
    $msiAgent = Join-Path $Work "$name-msi-agent"
    # The panel's and the agent's programs, by the project each is published from, in the folders they are published to (what
    # is signed, and what is checked again before each package is made of it). The panel's downloads folder also holds
    # the agent's setup once it is built.
    $panelProject = Join-Path $repo 'src\Pulsar.Api\Pulsar.Api.csproj'
    $agentProject = Join-Path $repo 'src\Pulsar.Agent\Pulsar.Agent.csproj'
    $ProgramSets = @(
        @{ Folder = (Join-Path $staging 'panel'); Project = $panelProject; Extra = @('downloads\PulsarAgentSetup.exe') },
        @{ Folder = (Join-Path $staging 'agent'); Project = $agentProject; Extra = @() },
        @{ Folder = $msiAgent; Project = $agentProject; Extra = @() })

    if (-not $SkipWebBuild) {
        Push-Location (Join-Path $repo 'src\Pulsar.Web')
        try {
            # Without install scripts: the lock file has one package with any (fsevents, for macOS), and a package's script
            # would run as this user, with whatever the user can reach, before anything is signed.
            npm ci --ignore-scripts
            if ($LASTEXITCODE -ne 0) { throw 'npm ci failed' }
            npm run build
            if ($LASTEXITCODE -ne 0) { throw 'web build failed' }
        }
        finally { Pop-Location }
    }

    if (-not (Test-Path (Join-Path $repo 'src\Pulsar.Api\wwwroot\index.html'))) { throw 'The web app is not built (src\Pulsar.Api\wwwroot).' }

    # Locked restore: the release gets exactly the dependencies (and content hashes) in the lock files.
    $common = @('-c', 'Release', '-r', 'win-x64', "-p:Version=$Version", '-p:DebugType=none', '-p:RestoreLockedMode=true')
    # Signed: MSBuild's nodes and the compiler server do not stay resident (they would outlive the builds, as this user, with
    # the signing identity within reach).
    if ($Signing) { $common += @('-nodeReuse:false', '-p:UseSharedCompilation=false') }
    # The ZIP's programs need the shared .NET runtimes, at least the newest patch this SDK knows (TargetLatestRuntimePatch
    # writes it into their runtimeconfig.json, so they refuse to start on an older one).
    $zipOptions = $common + @('--no-self-contained', '-p:TargetLatestRuntimePatch=true')
    dotnet publish (Join-Path $repo 'src\Pulsar.Api') @zipOptions -o (Join-Path $staging 'panel')
    if ($LASTEXITCODE -ne 0) { throw 'publish panel failed' }
    dotnet publish (Join-Path $repo 'src\Pulsar.Agent') @zipOptions -o (Join-Path $staging 'agent')
    if ($LASTEXITCODE -ne 0) { throw 'publish agent failed' }
    # The MSI's agent carries its own runtime: Windows Installer runs it as is, with nothing to install .NET first.
    dotnet publish (Join-Path $repo 'src\Pulsar.Agent') @common --self-contained -o $msiAgent
    if ($LASTEXITCODE -ne 0) { throw 'publish agent for the MSI failed' }
    if ($Signing) {
        dotnet build-server shutdown
        if ($LASTEXITCODE -ne 0) { throw 'dotnet build-server shutdown failed' }
    }

    # Development settings never ship.
    Get-ChildItem $staging, $msiAgent -Recurse -Filter 'appsettings.Development.json' | Remove-Item

    # Signed before they are packed into the MSI, the ZIP and the setups (and before the MSI reads their sizes and
    # versions), so the copies in them are the signed ones. Only the programs the projects list, and none unexpected.
    if ($Signing) {
        $programs = @()
        foreach ($set in $ProgramSets) { $programs += Get-PulsarPrograms $set.Folder $set.Project $set.Extra }
        Invoke-Signing $programs 'the programs'
    }

    # The runtimes the ZIP's programs were built for, which Install-Pulsar.ps1 makes sure of (at least these versions).
    $runtimes = [ordered]@{}
    foreach ($config in (Join-Path $staging 'panel\Pulsar.Api.runtimeconfig.json'), (Join-Path $staging 'agent\PulsarAgent.runtimeconfig.json')) {
        $options = (Get-Content -Raw $config | ConvertFrom-Json).runtimeOptions
        $frameworks = @()
        if ($options.PSObject.Properties['framework']) { $frameworks += $options.framework }
        if ($options.PSObject.Properties['frameworks']) { $frameworks += @($options.frameworks) }
        if ($frameworks.Count -eq 0) { throw "$config names no shared framework: the program came out self-contained." }
        foreach ($framework in $frameworks) {
            if (-not $runtimes.Contains($framework.name) -or [version]$runtimes[$framework.name] -lt [version]$framework.version) {
                $runtimes[$framework.name] = $framework.version
            }
        }
    }

    # In Windows PowerShell, whatever runs this script: the MSI is written through the Installer's COM interface, checked there.
    # By full path, like the other Windows programs the release and install scripts run.
    Assert-ProgramsSigned
    & (Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe') -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Build-AgentMsi.ps1') -AgentDir $msiAgent -Version $Version -Output $Work
    if ($LASTEXITCODE -ne 0) { throw 'agent MSI failed' }
    Remove-Item $msiAgent -Recurse -Force
    $ProgramSets = @($ProgramSets | Where-Object { $_.Folder -ne $msiAgent })
    $msi = Join-Path $Work "pulsar-agent-$Version-windows-x64.msi"
    # The package holds the signed programs; it is signed itself before its checksum is written.
    if ($Signing) { Invoke-Signing @($msi) 'the agent MSI' }
    $msiHash = Write-Checksum $msi

    # What the packages carry says it is signed only now that the programs and the MSI are, verified again.
    Assert-ProgramsSigned
    $releaseInfo = [ordered]@{ version = $Version; runtimes = $runtimes; postgresMinimum = $PostgresMinimum }
    if ($Signing) { $releaseInfo['signing'] = [ordered]@{ signed = $true; signer = $Signing.Subject } }
    # An ASCII file: a character of the signer's name outside ASCII (a city, say) is written as a JSON escape.
    [regex]::Replace(($releaseInfo | ConvertTo-Json), '[^\x00-\x7F]', { param($match) '\u{0:x4}' -f [int][char]$match.Value }) | Set-Content (Join-Path $staging 'release.json') -Encoding ASCII

    Copy-Item (Join-Path $repo 'scripts\install\*.ps1') $staging
    Copy-Item (Join-Path $repo 'LICENSE') $staging
    New-Item -ItemType Directory -Force (Join-Path $staging 'docs') | Out-Null
    Copy-Item (Join-Path $repo 'docs\guides\*.md') (Join-Path $staging 'docs')
    $easy = ''
    if ($iscc) { $easy = "$([Environment]::NewLine)The easy way: PulsarSetup-$Version.exe, published next to this ZIP (docs\install.md, `"Easy install`").$([Environment]::NewLine)" }
@"
Pulsar $Version for Windows (x64)
$easy
Install with this ZIP: open PowerShell as administrator in this folder and run
    powershell -ExecutionPolicy Bypass -File .\Install-Pulsar.ps1
This folder must be one only administrators can change (docs\install.md, step 2 shows how to make it): the installer
refuses to run from any other. It downloads and checks what this machine lacks (the .NET $(($runtimes.Keys | ForEach-Object { "$_ $($runtimes[$_])" }) -join ' and ')
runtimes, and PostgreSQL 18 for a panel without one); docs\install.md says how, and how to install offline.
Pulsar is licensed under the GNU AGPL v3 (LICENSE); game manifests and schemas under MIT.
"@ | Set-Content (Join-Path $staging 'README.txt') -Encoding UTF8

    # The agent's setup first: in the panel's downloads folder, the panel serves it at /downloads/PulsarAgentSetup.exe
    # (Install-Pulsar.ps1 copies the folder along with the panel, and keeps it on updates), so the ZIP and PulsarSetup.exe
    # carry it there.
    $agentSetup = $null
    if ($iscc) {
        Assert-ProgramsSigned
        $agentSetup = Build-Setup $iscc 'PulsarAgentSetup.iss' $staging $numeric
        $downloads = New-Item -ItemType Directory -Force (Join-Path $staging 'panel\downloads')
        Copy-Item -LiteralPath $agentSetup -Destination (Join-Path $downloads.FullName 'PulsarAgentSetup.exe')
    }

    # The programs again, and the agent's setup in the downloads folder, right before the ZIP is made of them.
    Assert-ProgramsSigned
    $zip = Join-Path $Work "$name.zip"
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip -CompressionLevel Optimal
    $hash = Write-Checksum $zip

    $setup = $null
    if ($iscc) {
        Assert-ProgramsSigned
        $setup = Build-Setup $iscc 'PulsarSetup.iss' $staging $numeric
        foreach ($built in $setup, $agentSetup) { $null = Write-Checksum $built }
    }

    # All there, signed and verified: now it replaces what an earlier build of this version left in the output folder.
    $published = @($name, "$name.zip", "$name.zip.sha256", (Split-Path $msi -Leaf), "$(Split-Path $msi -Leaf).sha256")
    if ($iscc) { $published += @($setup, $agentSetup | ForEach-Object { Split-Path $_ -Leaf; "$(Split-Path $_ -Leaf).sha256" }) }
    Publish-Release $Work $Output $published
    Write-Host "Release: $(Join-Path $Output "$name.zip")"
    Write-Host "SHA-256: $hash"
    Write-Host "Agent MSI: $(Join-Path $Output (Split-Path $msi -Leaf)) (SHA-256 $msiHash)"
    if ($iscc) {
        foreach ($built in $setup, $agentSetup) {
            $final = Join-Path $Output (Split-Path $built -Leaf)
            Write-Host "Setup: $final ($([Math]::Round((Get-Item -LiteralPath $final).Length / 1MB, 1)) MB, SHA-256 $((Get-FileHash -LiteralPath $final -Algorithm SHA256).Hash.ToLowerInvariant()))"
        }
    }
    if ($Signing) { Write-Host "Signed by $($Signing.Subject): the programs, the agent MSI$(if ($iscc) { ' and both setups' }). The ZIP itself cannot be signed: its checksum is." }
}
finally {
    Remove-BuildWork $Work $Signing
    Restore-AzureCredentials
}
