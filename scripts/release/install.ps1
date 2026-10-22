# Installs Pulsar from the newest release of the project's GitHub repository with one command (docs/guides/install.md,
# "With one command"), in Windows PowerShell or PowerShell 7 run as administrator:
#   irm https://github.com/Ayrton09/Pulsar/releases/latest/download/install.ps1 | iex
# It runs the release's setup wizard, PulsarSetup-<version>.exe (the panel, a node or both), or with -Agent the node's,
# PulsarAgentSetup-<version>.exe, only once it has checked it as installed Pulsars check an update:
#  - the release's manifest (pulsar-update.json) must be signed by an update key this script trusts, the same keys every
#    Pulsar build trusts (UpdateKeys.Trusted in src/Pulsar.Contracts/Updates.cs); a release published or changed by anyone
#    without the maintainer's key is refused, whatever GitHub serves;
#  - the setup is downloaded from that release, over HTTPS from GitHub and the hosts it sends release files to, into a new
#    folder in Program Files (only administrators can change it, so nothing else on this PC can swap the file between the
#    check and the run), and must have the size and SHA-256 the signed manifest gives;
#  - only then does it run, from there; the folder is removed afterwards.
# This script itself is what GitHub serves for the newest release (over HTTPS): to read it first, download it, check it,
# and run it with -File (-Agent, and -SetupArguments for the setup, e.g. '/VERYSILENT', '/SUPPRESSMSGBOXES').
# Everything runs in a script block of its own, so that, piped to iex, it leaves nothing in the PowerShell it ran in (no
# variable, function or preference); its parameters reach it through @args, which keeps their names.
& {
    param(
        # Runs PulsarAgentSetup (this PC as a node of a panel elsewhere) instead of PulsarSetup.
        [switch] $Agent,
        # Passed to the setup as they are (its silent options: docs/guides/install.md, "Silent installs with the setups").
        [string[]] $SetupArguments = @())
    $ErrorActionPreference = 'Stop'

    $Repository = 'Ayrton09/Pulsar'
    # The update keys' public keys (ECDSA P-256, SubjectPublicKeyInfo in base64): UpdateKeys.Trusted, the same list.
    $TrustedKeys = @(
        'MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEDoveNpq4aiT1Pmog9P4f2egClTyuGYeYtyaG9S6MVS15A31pequgC+TyKe0E7zDKqY7HXWwZWiY65JZ4vpafXA=='
    )
    $SignaturePurpose = 'pulsar-update-manifest-v1'
    $ManifestName = 'pulsar-update.json'
    $SignatureName = 'pulsar-update.json.sig'
    $MaxManifestBytes = 64KB
    $MaxSignatureBytes = 4KB
    $MaxFileBytes = 1GB
    $DownloadHosts = @('github.com', 'objects.githubusercontent.com', 'release-assets.githubusercontent.com')
    $VersionPattern = '^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\z'
    # A P-256 key's SubjectPublicKeyInfo up to its point: id-ecPublicKey, prime256v1, a 66-byte BIT STRING.
    $P256KeyPrefix = [byte[]](0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x02, 0x01, 0x06, 0x08, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00)
    # The setup's exit code when it is done but a .NET installer asks for a restart (with /RESTARTEXITCODE=3010).
    $RestartExitCode = 3010

    # A trusted key ($base64: its SubjectPublicKeyInfo) as Windows' CNG imports it (an ECS1 blob: its X and Y) and its id
    # ("sha256:" and the SubjectPublicKeyInfo's SHA-256 in base64url, as ProtocolCrypto.Fingerprint makes it).
    function ConvertFrom-UpdateKey([string] $base64) {
        $key = [Convert]::FromBase64String($base64)
        if ($key.Length -ne 91 -or $key[26] -ne 0x04) { throw 'A trusted key is not a P-256 public key.' }
        for ($i = 0; $i -lt $P256KeyPrefix.Length; $i++) {
            if ($key[$i] -ne $P256KeyPrefix[$i]) { throw 'A trusted key is not a P-256 public key.' }
        }
        $blob = New-Object byte[] 72
        [Array]::Copy([byte[]](0x45, 0x43, 0x53, 0x31, 0x20, 0x00, 0x00, 0x00), 0, $blob, 0, 8)
        [Array]::Copy($key, 27, $blob, 8, 64)
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $hash = $sha.ComputeHash($key) } finally { $sha.Dispose() }
        $id = 'sha256:' + [Convert]::ToBase64String($hash).TrimEnd('=').Replace('+', '-').Replace('/', '_')
        return [pscustomobject]@{ Id = $id; Blob = $blob }
    }

    # What an update key signs: the purpose, then the manifest's bytes, each after its length (4 bytes, big-endian), as
    # ProtocolCrypto.SignedData lays them out.
    function Get-SignedData([string] $purpose, [byte[]] $manifest) {
        $stream = New-Object IO.MemoryStream
        foreach ($field in [Text.Encoding]::UTF8.GetBytes($purpose), $manifest) {
            $length = [BitConverter]::GetBytes([int] $field.Length)
            [Array]::Reverse($length)
            $stream.Write($length, 0, 4)
            $stream.Write($field, 0, $field.Length)
        }
        return $stream.ToArray()
    }

    # Throws unless $signatureFile (the .sig: {"keyId", "signature"}) is a trusted key's signature of $manifest.
    function Assert-ManifestSigned([byte[]] $manifest, [byte[]] $signatureFile, [string[]] $keys) {
        $signed = $null
        try { $signed = [Text.Encoding]::UTF8.GetString($signatureFile) | ConvertFrom-Json } catch { $signed = $null }
        if (-not $signed -or $signed.keyId -isnot [string] -or $signed.signature -isnot [string]) { throw 'The release''s signature file is not one: it is not installed.' }
        $key = @($keys | ForEach-Object { ConvertFrom-UpdateKey $_ } | Where-Object { $_.Id -ceq $signed.keyId }) | Select-Object -First 1
        if (-not $key) { throw "The release is signed by $($signed.keyId -replace '[^A-Za-z0-9:_-]', '?'), a key this script does not trust: it is not installed." }
        $signature = $null
        try { $signature = [Convert]::FromBase64String($signed.signature) } catch { $signature = $null }
        $valid = $false
        if ($signature -and $signature.Length -eq 64) {
            $cngKey = [Security.Cryptography.CngKey]::Import($key.Blob, [Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
            $ecdsa = New-Object Security.Cryptography.ECDsaCng $cngKey
            try { $valid = $ecdsa.VerifyData((Get-SignedData $SignaturePurpose $manifest), $signature, [Security.Cryptography.HashAlgorithmName]::SHA256) }
            finally { $ecdsa.Dispose(); $cngKey.Dispose() }
        }
        if (-not $valid) { throw 'The release''s signature does not match its manifest: it is not installed.' }
    }

    # The setup a signed manifest ($manifest, its bytes) names for this script to run: its version, name, size and SHA-256.
    function Get-SetupEntry([byte[]] $manifest, [bool] $agent) {
        $read = [Text.Encoding]::UTF8.GetString($manifest) | ConvertFrom-Json
        if ($read.product -cne 'pulsar' -or $read.version -isnot [string] -or $read.version -cnotmatch $VersionPattern) { throw 'The release''s manifest is not one of Pulsar''s: it is not installed.' }
        $name = if ($agent) { "PulsarAgentSetup-$($read.version).exe" } else { "PulsarSetup-$($read.version).exe" }
        $file = @($read.files | Where-Object { $_.name -ceq $name }) | Select-Object -First 1
        if (-not $file) { throw "Release $($read.version) has no $name." }
        if ($file.size -isnot [long] -and $file.size -isnot [int]) { throw "The manifest gives no size for $name." }
        if ($file.size -le 0 -or $file.size -gt $MaxFileBytes -or $file.sha256 -isnot [string] -or $file.sha256 -cnotmatch '^[0-9a-f]{64}\z') {
            throw "The manifest's entry for $name is not one this script can check."
        }
        return [pscustomobject]@{ Version = $read.version; Name = $name; Size = [long] $file.size; Sha256 = $file.sha256 }
    }

    # Throws unless the downloaded setup ($path) has the size and SHA-256 of its signed entry ($entry, Get-SetupEntry's).
    function Assert-SetupFile([string] $path, $entry) {
        $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        $size = (Get-Item -LiteralPath $path).Length
        if ($size -ne $entry.Size -or $actual -cne $entry.Sha256) {
            throw "$($entry.Name) is not the file the signed manifest names ($size bytes, SHA-256 $actual; not $($entry.Size) bytes, $($entry.Sha256)): it is not run."
        }
    }

    # Whether $uri may be downloaded from: HTTPS on its default port, to GitHub or the hosts it sends release files to.
    function Test-DownloadUri([Uri] $uri) {
        return $uri.IsAbsoluteUri -and $uri.Scheme -ceq 'https' -and $uri.IsDefaultPort -and -not $uri.UserInfo -and $DownloadHosts -contains $uri.Host.ToLowerInvariant()
    }

    # Downloads $url, following GitHub's redirects by hand to the allowed hosts only, into $path (or into memory, with no
    # $path), refusing more than $maxBytes. Returns the bytes when kept in memory.
    function Get-Download([string] $url, [long] $maxBytes, [string] $path) {
        $uri = [Uri] $url
        for ($hop = 0; $hop -le 5; $hop++) {
            if (-not (Test-DownloadUri $uri)) { throw "A download was sent to $($uri.GetLeftPart([UriPartial]::Authority)), which is not GitHub: refused." }
            $request = [Net.HttpWebRequest]::Create($uri)
            $request.AllowAutoRedirect = $false
            $request.UserAgent = 'Pulsar-install'
            $request.Timeout = 60000
            $request.ReadWriteTimeout = 60000
            $response = $null
            try { $response = $request.GetResponse() }
            catch [Net.WebException] {
                if ($_.Exception.Response) { $response = $_.Exception.Response } else { throw "$url could not be downloaded ($($_.Exception.Message))." }
            }
            try {
                $status = [int] $response.StatusCode
                if ($status -ge 300 -and $status -lt 400) {
                    $location = $response.Headers['Location']
                    if (-not $location) { throw "$url redirected nowhere." }
                    $uri = New-Object Uri $uri, $location
                    continue
                }
                if ($status -ne 200) { throw "$url could not be downloaded (HTTP $status)." }
                if ($response.ContentLength -gt $maxBytes) { throw "$url is larger than expected: refused." }
                $source = $response.GetResponseStream()
                $target = if ($path) { [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None) } else { New-Object IO.MemoryStream }
                try {
                    $buffer = New-Object byte[] 81920
                    $total = 0L
                    while (($read = $source.Read($buffer, 0, $buffer.Length)) -gt 0) {
                        $total += $read
                        if ($total -gt $maxBytes) { throw "$url is larger than expected: refused." }
                        $target.Write($buffer, 0, $read)
                    }
                    if (-not $path) { return ,$target.ToArray() }
                    return
                }
                finally { $target.Dispose(); $source.Dispose() }
            }
            finally { $response.Close() }
        }
        throw "$url redirected too many times."
    }

    # A new folder in Program Files for the download, which only administrators can change (it takes Program Files'
    # permissions as it is made).
    function New-DownloadFolder {
        $folder = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) ('PulsarSetup-download-' + [Guid]::NewGuid().ToString('N'))
        return (New-Item -ItemType Directory -Path $folder).FullName
    }

    function Install-Pulsar([bool] $agent, [string[]] $setupArguments) {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        if (-not (New-Object Security.Principal.WindowsPrincipal $identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw 'Run this in PowerShell as administrator (right-click PowerShell, Run as administrator): the setup installs services.'
        }
        if (-not [Environment]::Is64BitOperatingSystem) { throw 'Pulsar needs 64-bit Windows.' }
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

        Write-Host "Checking the newest release of $Repository"
        $manifest = Get-Download "https://github.com/$Repository/releases/latest/download/$ManifestName" $MaxManifestBytes $null
        $signature = Get-Download "https://github.com/$Repository/releases/latest/download/$SignatureName" $MaxSignatureBytes $null
        Assert-ManifestSigned $manifest $signature $TrustedKeys
        $setup = Get-SetupEntry $manifest $agent
        Write-Host "Pulsar $($setup.Version): signed by the project's update key"

        $folder = New-DownloadFolder
        try {
            $file = Join-Path $folder $setup.Name
            Write-Host "Downloading $($setup.Name) ($([Math]::Round($setup.Size / 1MB, 1)) MB)"
            Get-Download "https://github.com/$Repository/releases/download/v$($setup.Version)/$($setup.Name)" $setup.Size $file
            Assert-SetupFile $file $setup
            Write-Host "Running $($setup.Name) (its size and SHA-256 are the signed manifest's)"
            $arguments = @($setupArguments | Where-Object { $_ })
            $process = if ($arguments.Count -gt 0) { Start-Process -FilePath $file -ArgumentList $arguments -Wait -PassThru } else { Start-Process -FilePath $file -Wait -PassThru }
            if ($process.ExitCode -eq $RestartExitCode) { Write-Host "$($setup.Name) is done: restart Windows to finish (a .NET installer asked for it)." -ForegroundColor Yellow }
            elseif ($process.ExitCode -ne 0) { throw "$($setup.Name) ended with exit code $($process.ExitCode) (docs/guides/install.md says what it means)." }
            else { Write-Host "$($setup.Name) is done." -ForegroundColor Green }
        }
        finally {
            Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    Install-Pulsar $Agent.IsPresent $SetupArguments
} @args
