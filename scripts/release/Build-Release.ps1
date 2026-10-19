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
# What is signed: the panel's and the agent's programs (Pulsar*.exe and Pulsar*.dll), before they are packed into the ZIP,
# the MSI and the setups, so the packed copies are signed; then the MSI; then both setups, which Inno Setup signs through
# its SignTool directive (the uninstaller too). Third-party libraries and the PowerShell scripts are not signed.
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
    # managed identity), never through this script. One of the two signing options, not both.
    [ValidateNotNullOrEmpty()] [string] $TrustedSigningMetadata,
    # The Trusted Signing client's Azure.CodeSigning.Dlib.dll. Without it, the client tools' per-user install folder.
    [ValidateNotNullOrEmpty()] [string] $TrustedSigningDlib,
    # signtool.exe (x64). Without it, the newest installed Windows SDK's (Windows Kits\10\bin\<version>\x64).
    [ValidateNotNullOrEmpty()] [string] $SignTool,
    # The RFC 3161 timestamp authority: every signature is timestamped, so it stays valid after the certificate expires.
    # Azure Trusted Signing's certificates last days: Microsoft's own, http://timestamp.acs.microsoft.com, goes with them.
    [ValidatePattern('^https?://[A-Za-z0-9.-]+(:\d{1,5})?(/[A-Za-z0-9._~/-]*)?\z')] [string] $TimestampUrl = 'http://timestamp.digicert.com')
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

# Inno Setup 7's ISCC.exe (-InnoSetupCompiler, or an install of it: Program Files first, where only administrators can
# change it, then a per-user install), signed by its publisher; or $null (with a warning) when there is none.
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
# token, and Azure signs in with its own credentials.

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

# A private copy of signtool.exe (with those DLLs), in a new folder of its own and checked there: what runs is what was
# checked, and no other DLL left beside the SDK's copy is loaded with it (Windows looks beside the program first). The
# rest it uses is Windows'. The Trusted Signing client, which signtool loads from where it is, is checked again too.
# Remove-SignToolCopy removes the folder.
function Copy-SignTool {
    $folder = Join-Path ([IO.Path]::GetTempPath()) ('pulsar-signtool-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory $folder | Out-Null
    try {
        $copy = Join-Path $folder 'signtool.exe'
        Copy-Item -LiteralPath $Signing.SignTool -Destination $copy
        foreach ($name in $SignToolLibraries) {
            $library = Join-Path (Split-Path -Parent $Signing.SignTool) $name
            if (Test-Path -LiteralPath $library -PathType Leaf) { Copy-Item -LiteralPath $library -Destination (Join-Path $folder $name) }
        }
        Assert-SignToolSigned $copy
        if ($Signing.Dlib) { Assert-TrustedSigningClient $Signing.Dlib }
        return $copy
    }
    catch {
        Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction SilentlyContinue
        throw
    }
}

function Remove-SignToolCopy([string] $copy) {
    if (-not $copy) { return }
    $folder = Split-Path -Parent $copy
    if ((Split-Path -Leaf $folder) -like 'pulsar-signtool-*') { Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction SilentlyContinue }
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

# The metadata file Azure Trusted Signing's client reads (docs: Endpoint, CodeSigningAccountName and CertificateProfileName,
# by exactly these names), checked for what a typo would leave out; its full path.
function Read-TrustedSigningMetadata([string] $path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "-TrustedSigningMetadata must name an existing JSON file ($path)." }
    try { $metadata = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json }
    catch { throw "-TrustedSigningMetadata $path is not JSON ($($_.Exception.Message))." }
    $names = @($metadata.PSObject.Properties | ForEach-Object { $_.Name })
    foreach ($name in 'Endpoint', 'CodeSigningAccountName', 'CertificateProfileName') {
        if ($names -cnotcontains $name -or $metadata.$name -isnot [string] -or -not $metadata.$name.Trim()) { throw "-TrustedSigningMetadata $path has no text for '$name'." }
    }
    if ($metadata.Endpoint -cnotmatch '^https://[A-Za-z0-9.-]+/?$') { throw "-TrustedSigningMetadata $path : the Endpoint must be an https address ($($metadata.Endpoint))." }
    return (Resolve-Path -LiteralPath $path).Path
}

# The Trusted Signing client's Azure.CodeSigning.Dlib.dll (-TrustedSigningDlib, or the client tools' per-user install
# folder), found; its full path.
function Find-TrustedSigningClient {
    $dlib = $TrustedSigningDlib
    if (-not $dlib) {
        if (-not $env:LOCALAPPDATA) { throw 'Give -TrustedSigningDlib: this account has no LOCALAPPDATA to look for the Trusted Signing client tools in.' }
        $dlib = Join-Path $env:LOCALAPPDATA 'Microsoft\MicrosoftTrustedSigningClientTools\Azure.CodeSigning.Dlib.dll'
    }
    if ((Split-Path -Leaf $dlib) -ine 'Azure.CodeSigning.Dlib.dll' -or -not (Test-Path -LiteralPath $dlib -PathType Leaf)) {
        throw "Azure.CodeSigning.Dlib.dll was not found ($dlib). Install Microsoft's Trusted Signing client tools, or give -TrustedSigningDlib."
    }
    return (Resolve-Path -LiteralPath $dlib).Path
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

# What signing the release will do, from the options, with everything it needs checked before the long builds; or $null
# for an unsigned release (no signing option given).
function Initialize-Signing {
    $byStore = $GivenSigningOptions -contains 'SigningThumbprint'
    $byAzure = $GivenSigningOptions -contains 'TrustedSigningMetadata'
    if (-not $byStore -and -not $byAzure) {
        if ($GivenSigningOptions.Count -gt 0) { throw "These options only apply to a signed release (-$($GivenSigningOptions -join ', -')): give -SigningThumbprint or -TrustedSigningMetadata too, or none of them for an unsigned one." }
        return $null
    }
    if ($byStore -and $byAzure) { throw 'Give -SigningThumbprint or -TrustedSigningMetadata, not both.' }
    if ($byStore -and ($GivenSigningOptions -contains 'TrustedSigningDlib')) { throw '-TrustedSigningDlib goes with -TrustedSigningMetadata.' }
    $tool = Find-SignTool
    Assert-SignToolSigned $tool
    $arguments = @('sign', '/fd', 'sha256', '/tr', $TimestampUrl, '/td', 'sha256', '/d', $SignedDescription, '/du', $SignedInformationUrl)
    $thumbprint = $null
    $subject = $null
    $dlib = $null
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
        $metadata = Read-TrustedSigningMetadata $TrustedSigningMetadata
        $dlib = Find-TrustedSigningClient
        Assert-TrustedSigningClient $dlib
        # /dlib came with the Windows SDK 10.0.22621.
        $info = (Get-Item -LiteralPath $tool).VersionInfo
        if ([version]('{0}.{1}.{2}.{3}' -f $info.ProductMajorPart, $info.ProductMinorPart, $info.ProductBuildPart, $info.ProductPrivatePart) -lt [version]'10.0.22621.755') {
            throw "$tool ($($info.ProductVersion)) is too old to sign with Trusted Signing (/dlib needs 10.0.22621.755 or later): install a newer Windows SDK."
        }
        $arguments += @('/dlib', $dlib, '/dmdf', $metadata)
        Write-Host "Signing with Azure Trusted Signing ($metadata), timestamps from $TimestampUrl"
    }
    return [pscustomobject]@{ SignTool = $tool; Arguments = [string[]] $arguments; Thumbprint = $thumbprint; Subject = $subject; Dlib = $dlib }
}

# Runs signtool; throws with its output unless it exits with 0 (its 2, a warning, counts as a failure: a timestamp it
# could not get is one).
function Invoke-SignTool([string] $tool, [string[]] $arguments, [string] $failure) {
    $lines = & {
        $ErrorActionPreference = 'Continue'
        @(& $tool @arguments 2>&1 | ForEach-Object { "$_" })
    }
    $code = $LASTEXITCODE
    if ($code -ne 0) { throw "$failure (signtool exit code $code)$([Environment]::NewLine)$($lines -join [Environment]::NewLine)" }
}

# Each file must verify with signtool (/pa: the default Authenticode policy, up to a root this PC trusts) and carry a valid
# signature in the file itself, timestamped, of the certificate asked to sign (by its thumbprint, when it is known) and of
# one subject for the whole release.
function Assert-Signed([string[]] $files, [string] $tool) {
    foreach ($file in $files) {
        Invoke-SignTool $tool @('verify', '/pa', '/q', $file) "$file does not verify after signing."
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
    $copy = Copy-SignTool
    try {
        for ($first = 0; $first -lt $files.Count; $first += 40) {
            $batch = @($files[$first..([Math]::Min($first + 39, $files.Count - 1))])
            Invoke-SignTool $copy (@($Signing.Arguments) + $batch) "Signing $what failed."
        }
        Assert-Signed $files $copy
    }
    finally { Remove-SignToolCopy $copy }
    Write-Host "Signed $what ($($files.Count) file(s)) by $($Signing.Subject)."
}

# The programs Pulsar ships in a published folder: its own Pulsar*.exe and Pulsar*.dll, not the libraries it uses. $required
# must be among them (a filter that matched nothing would sign nothing, and the release would still go out).
function Get-PulsarPrograms([string] $folder, [string[]] $required) {
    $programs = @(Get-ChildItem -LiteralPath $folder -Recurse -File | Where-Object { $_.Name -like 'Pulsar*' -and ($_.Extension -ieq '.exe' -or $_.Extension -ieq '.dll') } | ForEach-Object { $_.FullName })
    foreach ($name in $required) {
        if (@($programs | Where-Object { (Split-Path -Leaf $_) -ieq $name }).Count -eq 0) { throw "$folder has no $name to sign." }
    }
    return $programs
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

# Compiles one of scripts\release\setup's setups from the staged release into $Output; its path. A warning fails the
# build like an error. Signed, Inno Setup signs the setup and its uninstaller with the one sign tool the scripts name
# (setup\Common.iss), defined here from a private copy of signtool.exe; any it has from its IDE is left out.
function Build-Setup([string] $compiler, [string] $script, [string] $release, [string] $numeric) {
    $arguments = @('--quiet', '--messages-jsonl', "/DVersion=$Version", "/DNumericVersion=$numeric.0", "/DRelease=$release", "/O$Output", (Join-Path $PSScriptRoot "setup\$script"))
    $signTool = $null
    if ($Signing) {
        $signTool = Copy-SignTool
        $arguments = @('/DSignSetups', '/NI', "/Spulsarsign=$(Get-InnoSignCommand $signTool)") + $arguments
    }
    try {
        # Checked again right before it runs: the web build (npm ci) ran code from the registry since it was found.
        Assert-InnoSetupSigned $compiler
        $lines = & {
            $ErrorActionPreference = 'Continue'
            @(& $compiler @arguments 2>&1 | ForEach-Object { "$_" })
        }
        $code = $LASTEXITCODE
        $messages = @($lines | Where-Object { $_ -match '^\s*\{' } | ForEach-Object { $_ | ConvertFrom-Json })
        $other = @($lines | Where-Object { $_ -notmatch '^\s*\{' -and $_.Trim() })
        foreach ($message in $messages) { Write-Host "${script}: $($message.severity): $($message.message)" }
        if ($code -ne 0 -or $messages.Count -gt 0) {
            throw "$script did not compile cleanly (ISCC exit code $code, $($messages.Count) message(s)).$([Environment]::NewLine)$($other -join [Environment]::NewLine)"
        }
        $name = [IO.Path]::GetFileNameWithoutExtension($script)
        $setup = Join-Path $Output "$name-$Version.exe"
        if (-not (Test-Path -LiteralPath $setup)) { throw "$script compiled, but $setup is missing." }
        if ($Signing) { Assert-Signed @($setup) $signTool }
    }
    finally { Remove-SignToolCopy $signTool }
    return $setup
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
# Before the long builds: whether the setups can be built, and whether the release can be signed if it is to be.
$iscc = Find-InnoSetupCompiler
$Signing = Initialize-Signing
$staging = Join-Path $Output $name
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
$msiAgent = Join-Path $Output "$name-msi-agent"
if (Test-Path $msiAgent) { Remove-Item $msiAgent -Recurse -Force }

if (-not $SkipWebBuild) {
    Push-Location (Join-Path $repo 'src\Pulsar.Web')
    try {
        npm ci
        if ($LASTEXITCODE -ne 0) { throw 'npm ci failed' }
        npm run build
        if ($LASTEXITCODE -ne 0) { throw 'web build failed' }
    }
    finally { Pop-Location }
}

if (-not (Test-Path (Join-Path $repo 'src\Pulsar.Api\wwwroot\index.html'))) { throw 'The web app is not built (src\Pulsar.Api\wwwroot).' }

# Locked restore: the release gets exactly the dependencies (and content hashes) in the lock files.
$common = @('-c', 'Release', '-r', 'win-x64', "-p:Version=$Version", '-p:DebugType=none', '-p:RestoreLockedMode=true')
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

# Development settings never ship.
Get-ChildItem $staging, $msiAgent -Recurse -Filter 'appsettings.Development.json' | Remove-Item

# Signed before they are packed into the MSI, the ZIP and the setups (and before the MSI reads their sizes and
# versions), so the copies in them are the signed ones.
if ($Signing) {
    $programs = @(Get-PulsarPrograms (Join-Path $staging 'panel') @('Pulsar.Api.exe', 'Pulsar.Api.dll')) +
        @(Get-PulsarPrograms (Join-Path $staging 'agent') @('PulsarAgent.exe', 'PulsarAgent.dll')) +
        @(Get-PulsarPrograms $msiAgent @('PulsarAgent.exe', 'PulsarAgent.dll'))
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
$releaseInfo = [ordered]@{ version = $Version; runtimes = $runtimes; postgresMinimum = $PostgresMinimum }
# A signed build says so, and by whom (the programs are signed by now; the MSI and the setups are signed next, or the build fails).
if ($Signing) { $releaseInfo['signing'] = [ordered]@{ signed = $true; signer = $Signing.Subject } }
# An ASCII file: a character of the signer's name outside ASCII (a city, say) is written as a JSON escape.
[regex]::Replace(($releaseInfo | ConvertTo-Json), '[^\x00-\x7F]', { param($match) '\u{0:x4}' -f [int][char]$match.Value }) | Set-Content (Join-Path $staging 'release.json') -Encoding ASCII

# In Windows PowerShell, whatever runs this script: the MSI is written through the Installer's COM interface, checked there.
# By full path, like the other Windows programs the release and install scripts run.
& (Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe') -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Build-AgentMsi.ps1') -AgentDir $msiAgent -Version $Version -Output $Output
if ($LASTEXITCODE -ne 0) { throw 'agent MSI failed' }
Remove-Item $msiAgent -Recurse -Force
$msi = Join-Path $Output "pulsar-agent-$Version-windows-x64.msi"
# The package holds the signed programs; it is signed itself before its checksum is written.
if ($Signing) { Invoke-Signing @($msi) 'the agent MSI' }
$msiHash = Write-Checksum $msi

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
    $agentSetup = Build-Setup $iscc 'PulsarAgentSetup.iss' $staging $numeric
    $downloads = New-Item -ItemType Directory -Force (Join-Path $staging 'panel\downloads')
    Copy-Item -LiteralPath $agentSetup -Destination (Join-Path $downloads.FullName 'PulsarAgentSetup.exe')
}

$zip = Join-Path $Output "$name.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip -CompressionLevel Optimal
$hash = Write-Checksum $zip
Write-Host "Release: $zip"
Write-Host "SHA-256: $hash"
Write-Host "Agent MSI: $msi (SHA-256 $msiHash)"

if ($iscc) {
    $setup = Build-Setup $iscc 'PulsarSetup.iss' $staging $numeric
    foreach ($built in $setup, $agentSetup) {
        $builtHash = Write-Checksum $built
        Write-Host "Setup: $built ($([Math]::Round((Get-Item -LiteralPath $built).Length / 1MB, 1)) MB, SHA-256 $builtHash)"
    }
}
if ($Signing) { Write-Host "Signed by $($Signing.Subject): the programs, the agent MSI$(if ($iscc) { ' and both setups' }). The ZIP itself cannot be signed: its checksum is." }
