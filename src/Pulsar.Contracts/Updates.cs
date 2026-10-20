using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pulsar.Contracts;

/// <summary>
/// Where Pulsar's updates come from (Phase 19, docs/guides/update.md, "One-click updates"): the releases of the project's
/// own GitHub repository and nowhere else. Each release carries <see cref="ManifestName"/>, which names its files with
/// their SHA-256, and <see cref="SignatureName"/>, its signature by an update key (<see cref="UpdateKeys"/>): GitHub only
/// carries them, so whoever could publish a release there cannot make the panel or an agent take a file nobody signed.
/// </summary>
public static partial class UpdateRelease
{
    public const string Repository = "Ayrton09/Pulsar";
    public const string Product = "pulsar";
    public const string ManifestName = "pulsar-update.json";
    public const string SignatureName = "pulsar-update.json.sig";

    /// <summary>What an update key signs: the manifest's bytes, as they are, for this purpose (<see cref="ProtocolCrypto.SignedData"/>).</summary>
    public const string SignaturePurpose = "pulsar-update-manifest-v1";

    public const int MaxManifestBytes = 64 * 1024;
    public const int MaxSignatureBytes = 4 * 1024;
    public const int MaxNotesLength = 16 * 1024;
    public const int MaxFiles = 16;
    public const long MaxFileBytes = 1L << 30;

    /// <summary>The newest release's manifest: GitHub sends the newest release that is not a draft or a pre-release.</summary>
    public static Uri LatestManifest { get; } = new($"https://github.com/{Repository}/releases/latest/download/{ManifestName}");

    /// <summary>The newest release's signature, which must be the manifest's (a release published in between fails the check).</summary>
    public static Uri LatestSignature { get; } = new($"https://github.com/{Repository}/releases/latest/download/{SignatureName}");

    /// <summary>A file of the release of <paramref name="version"/> (a version <see cref="UpdateVersion.TryParse"/> took).</summary>
    public static Uri File(UpdateVersion version, string name) =>
        IsFileName(name) ? new($"https://github.com/{Repository}/releases/download/v{version}/{name}")
            : throw new ArgumentException("Not a release file name.", nameof(name));

    /// <summary>The release's ZIP, which holds the programs and Install-Pulsar.ps1.</summary>
    public static string ZipName(UpdateVersion version) => $"pulsar-{version}-windows-x64.zip";

    /// <summary>
    /// Hosts a download may be sent to: GitHub, then the storage it redirects release files to. Over HTTPS only, and a
    /// redirect anywhere else is refused.
    /// </summary>
    public static IReadOnlySet<string> DownloadHosts { get; } = new HashSet<string>(
        ["github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"], StringComparer.OrdinalIgnoreCase);

    public static bool IsAllowedDownload(Uri uri) =>
        uri is { IsAbsoluteUri: true } && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && DownloadHosts.Contains(uri.Host)
        && string.IsNullOrEmpty(uri.UserInfo);

    /// <summary>A plain file name: letters, digits, dots, dashes and underscores, never a path or a name that starts with a dot.</summary>
    public static bool IsFileName(string? name) => name is not null && FileNamePattern().IsMatch(name);

    [GeneratedRegex(@"\A[A-Za-z0-9_-][A-Za-z0-9._-]{0,99}\z", RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();
}

/// <summary>A release's own description (<see cref="UpdateRelease.ManifestName"/>), signed by an update key.</summary>
/// <param name="MinimumUpdateFrom">The oldest installed version that may update to this one directly; older ones first take
/// the releases in between, or the setups.</param>
/// <param name="Notes">What changed, as plain text (never markup), for the administrator who decides to update.</param>
public sealed record UpdateManifest(string Product, string Version, string MinimumUpdateFrom, DateTimeOffset Published, string? Notes,
    IReadOnlyList<UpdateFile> Files)
{
    /// <summary>The file named <paramref name="name"/>, or null.</summary>
    public UpdateFile? Find(string name) => Files.FirstOrDefault(f => f.Name == name);
}

/// <param name="Sha256">Lower-case hexadecimal.</param>
public sealed record UpdateFile(string Name, long Size, string Sha256);

/// <summary>The signature file (<see cref="UpdateRelease.SignatureName"/>): the key's id (its fingerprint) and the signature, in base64.</summary>
public sealed record UpdateSignature(string KeyId, string Signature);

/// <summary>A release version, <c>major.minor.patch</c>, with no pre-release or build part: what releases are numbered by.</summary>
public readonly partial record struct UpdateVersion(int Major, int Minor, int Patch) : IComparable<UpdateVersion>
{
    /// <summary>A release's version exactly (<c>1.2.3</c>, no leading zeros, each part at most 9 digits).</summary>
    public static bool TryParse(string? text, out UpdateVersion version)
    {
        version = default;
        var match = text is null ? null : ExactPattern().Match(text);
        if (match is not { Success: true })
        {
            return false;
        }

        version = new UpdateVersion(Number(match.Groups[1].Value), Number(match.Groups[2].Value), Number(match.Groups[3].Value));
        return true;
    }

    /// <summary>
    /// An installed program's version as .NET records it (<c>1.2.3</c>, <c>1.2.3+commit</c>, <c>1.2.3-dev</c>): its
    /// release part, and whether it is a pre-release (older than that release). Null when it is not one.
    /// </summary>
    public static (UpdateVersion Version, bool PreRelease)? Installed(string? text)
    {
        var withoutBuild = text?.Split('+', 2)[0];
        var parts = withoutBuild?.Split('-', 2);
        return parts is not null && TryParse(parts[0], out var version) ? (version, parts.Length == 2) : null;
    }

    /// <summary>Whether a release of this version is newer than an installed program's version (a pre-release of the same one included).</summary>
    public bool IsNewerThan((UpdateVersion Version, bool PreRelease) installed) =>
        CompareTo(installed.Version) > 0 || (CompareTo(installed.Version) == 0 && installed.PreRelease);

    public int CompareTo(UpdateVersion other) =>
        Major != other.Major ? Major.CompareTo(other.Major) : Minor != other.Minor ? Minor.CompareTo(other.Minor) : Patch.CompareTo(other.Patch);

    public static bool operator <(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(UpdateVersion left, UpdateVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    private static int Number(string digits) => int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\A(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExactPattern();
}

/// <summary>
/// The public keys whose signatures make a release an update (ECDSA P-256, SubjectPublicKeyInfo in base64). Their private
/// keys stay with the project's maintainer, off GitHub (docs/guides/releasing.md): a key is added here, and so trusted by
/// the releases built from then on, before it signs; one that is retired stays until every installation took a release
/// signed by its successor.
/// </summary>
public static class UpdateKeys
{
    public static IReadOnlyList<string> Trusted { get; } = [];
}

/// <summary>
/// Checks a release's manifest against its signature and the trusted keys, then reads it strictly. What it returns is
/// signed: the version, the files and their SHA-256. A manifest that does not verify is never parsed.
/// </summary>
public sealed class UpdateVerifier
{
    private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);

    /// <param name="trustedKeys">SubjectPublicKeyInfo of each trusted key, in base64; malformed ones are ignored.</param>
    public UpdateVerifier(IEnumerable<string> trustedKeys)
    {
        ArgumentNullException.ThrowIfNull(trustedKeys);
        foreach (var text in trustedKeys)
        {
            try
            {
                var key = Convert.FromBase64String(text);
                using var _ = ProtocolCrypto.ImportPublicKey(key);
                _keys[ProtocolCrypto.Fingerprint(key)] = key;
            }
            catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException)
            {
            }
        }
    }

    /// <summary>The keys of this build (<see cref="UpdateKeys.Trusted"/>).</summary>
    public static UpdateVerifier Default { get; } = new(UpdateKeys.Trusted);

    /// <summary>Whether any key is trusted: a build without one takes no update.</summary>
    public bool HasKeys => _keys.Count > 0;

    /// <summary>The manifest, or why it is not one to take: <c>update.unsigned</c>, <c>update.unknown_key</c>,
    /// <c>update.bad_signature</c> or <c>update.invalid_manifest</c>.</summary>
    public UpdateCheck Verify(ReadOnlySpan<byte> manifest, ReadOnlySpan<byte> signature)
    {
        if (manifest.Length is 0 or > UpdateRelease.MaxManifestBytes || signature.Length is 0 or > UpdateRelease.MaxSignatureBytes)
        {
            return UpdateCheck.Refused("update.invalid_manifest", "The manifest or its signature is empty or too large.");
        }

        UpdateSignature? signed;
        try
        {
            signed = JsonSerializer.Deserialize<UpdateSignature>(signature, ProtocolJson.Options);
        }
        catch (JsonException)
        {
            signed = null;
        }

        if (signed?.KeyId is null || signed.Signature is null)
        {
            return UpdateCheck.Refused("update.unsigned", "The signature file is not one.");
        }

        if (!_keys.TryGetValue(signed.KeyId, out var key))
        {
            return UpdateCheck.Refused("update.unknown_key", $"It is signed by {Shown(signed.KeyId)}, a key this version of Pulsar does not trust.");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(signed.Signature);
        }
        catch (FormatException)
        {
            return UpdateCheck.Refused("update.bad_signature", "The signature is not base64.");
        }

        if (!ProtocolCrypto.Verify(key, ProtocolCrypto.SignedData(UpdateRelease.SignaturePurpose, manifest.ToArray()), bytes))
        {
            return UpdateCheck.Refused("update.bad_signature", "The signature does not match the manifest.");
        }

        return Read(manifest) is { } read ? UpdateCheck.Valid(read, signed.KeyId) : UpdateCheck.Refused("update.invalid_manifest", "The manifest is signed but malformed.");
    }

    /// <summary>The manifest, read strictly, or null: before signing a release too (a malformed one is never signed).</summary>
    public static UpdateManifest? Read(ReadOnlySpan<byte> manifest)
    {
        UpdateManifest? read;
        try
        {
            read = JsonSerializer.Deserialize<UpdateManifest>(manifest, ProtocolJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }

        if (read is null || read.Product != UpdateRelease.Product || read.Published == default || !UpdateVersion.TryParse(read.Version, out var version)
            || !UpdateVersion.TryParse(read.MinimumUpdateFrom, out var minimum) || minimum > version
            || read.Notes is { Length: > UpdateRelease.MaxNotesLength } || read.Files is not { Count: > 0 and <= UpdateRelease.MaxFiles })
        {
            return null;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in read.Files)
        {
            if (file is null || !UpdateRelease.IsFileName(file.Name) || !names.Add(file.Name) || file.Size is <= 0 or > UpdateRelease.MaxFileBytes
                || file.Sha256 is not { Length: 64 } || !file.Sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
                || file.Name == UpdateRelease.ManifestName || file.Name == UpdateRelease.SignatureName)
            {
                return null;
            }
        }

        return read.Find(UpdateRelease.ZipName(version)) is null ? null : read;
    }

    private static string Shown(string keyId) => keyId.Length <= 60 ? keyId : keyId[..60] + "…";
}

/// <summary>The outcome of <see cref="UpdateVerifier.Verify"/>: a signed manifest, or the error code and why.</summary>
public sealed record UpdateCheck(UpdateManifest? Manifest, string? KeyId, string? ErrorCode, string? Error)
{
    public static UpdateCheck Valid(UpdateManifest manifest, string keyId) => new(manifest, keyId, null, null);

    public static UpdateCheck Refused(string code, string why) => new(null, null, code, why);
}

/// <summary>
/// Downloads from a release (<see cref="UpdateRelease"/>), for the panel and the agents alike: over HTTPS, following
/// GitHub's redirects by hand to the hosts <see cref="UpdateRelease.DownloadHosts"/> allows and nowhere else, never more
/// than the caller expects. The client must not follow redirects itself (<see cref="CreateHandler"/>).
/// </summary>
public static class UpdateDownloads
{
    private const int MaxRedirects = 5;

    /// <summary>A handler for these downloads: no redirects of its own, no cookies, a bounded connection time.</summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(30),
        AutomaticDecompression = System.Net.DecompressionMethods.None,
    };

    /// <summary>The whole answer at <paramref name="uri"/>, at most <paramref name="maxBytes"/>.</summary>
    public static async Task<byte[]> GetBytesAsync(HttpClient client, Uri uri, int maxBytes, CancellationToken cancellationToken)
    {
        using var response = await GetAsync(client, uri, cancellationToken);
        if (response.Content.Headers.ContentLength > maxBytes)
        {
            throw new UpdateDownloadException($"{uri.AbsolutePath} is larger than {maxBytes} bytes.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new UpdateDownloadException($"{uri.AbsolutePath} is larger than {maxBytes} bytes.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Writes the file at <paramref name="uri"/> to <paramref name="path"/> (a new file), which must be exactly
    /// <see cref="UpdateFile.Size"/> bytes with its <see cref="UpdateFile.Sha256"/>: anything else is deleted and throws.
    /// </summary>
    public static async Task DownloadAsync(HttpClient client, Uri uri, UpdateFile file, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        try
        {
            using var response = await GetAsync(client, uri, cancellationToken);
            if (response.Content.Headers.ContentLength is { } length && length != file.Size)
            {
                throw new UpdateDownloadException($"{file.Name} is {length} bytes, not the {file.Size} its release says.");
            }

            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var chunk = new byte[81920];
                long total = 0;
                int read;
                while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > file.Size)
                    {
                        throw new UpdateDownloadException($"{file.Name} is larger than the {file.Size} bytes its release says.");
                    }

                    hash.AppendData(chunk, 0, read);
                    await output.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
                }

                if (total != file.Size)
                {
                    throw new UpdateDownloadException($"{file.Name} is {total} bytes, not the {file.Size} its release says.");
                }
            }

            if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), file.Sha256, StringComparison.Ordinal))
            {
                throw new UpdateDownloadException($"{file.Name} does not have the SHA-256 its release says.");
            }
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, Uri uri, CancellationToken cancellationToken)
    {
        for (var hop = 0; ; hop++)
        {
            if (!UpdateRelease.IsAllowedDownload(uri))
            {
                throw new UpdateDownloadException($"Refused to download from {uri.GetLeftPart(UriPartial.Authority)}: updates come from GitHub only.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null || hop >= MaxRedirects)
                {
                    throw new UpdateDownloadException($"GitHub redirected {uri.AbsolutePath} too often or nowhere.");
                }

                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                response.Dispose();
                throw new UpdateDownloadException(status == 404
                    ? $"{uri.AbsolutePath} is not on GitHub (no release, or the repository is private)."
                    : $"GitHub answered {status} for {uri.AbsolutePath}.");
            }

            return response;
        }
    }
}

/// <summary>A download from a release that failed or was refused; its message says why, for the administrator.</summary>
public sealed class UpdateDownloadException : Exception
{
    public UpdateDownloadException(string message)
        : base(message)
    {
    }

    public UpdateDownloadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public UpdateDownloadException()
    {
    }
}
