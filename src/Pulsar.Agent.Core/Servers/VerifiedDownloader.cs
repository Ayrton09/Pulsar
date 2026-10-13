using System.Security.Cryptography;
using System.Text.Json;
using Pulsar.Agent.Core.Processes;

namespace Pulsar.Agent.Core.Servers;

/// <summary>
/// Downloads for install steps and addons (docs/architecture/04): HTTPS only, from a fixed list of hosts checked at
/// every redirect (followed by hand), with size limits, a stall timeout and a checksum the publisher lists. Errors are
/// <see cref="GameProcessException"/>s with codes under <c>&lt;prefix&gt;.</c> (<c>minecraft.untrusted_host</c>).
/// </summary>
public sealed class VerifiedDownloader(HttpClient client, IReadOnlySet<string> hosts, string codePrefix, string what)
{
    public const long MaximumMetadata = 8 * 1024 * 1024;
    public const long MaximumDownload = 512L * 1024 * 1024;
    private const int MaximumRedirects = 5;

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    /// <summary>A download that stops sending for this long is abandoned.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long a host may take to answer a request (its headers): the callers' clients have no timeout, since a download
    /// takes as long as it keeps coming (<see cref="StallTimeout"/>).
    /// </summary>
    public TimeSpan HeadersTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>A small JSON document (checked against <paramref name="sha1"/> when the publisher lists one).</summary>
    public async Task<T> GetJsonAsync<T>(Uri uri, string? sha1, CancellationToken cancellationToken)
    {
        await using var body = await DownloadAsync(uri, sha1 is null ? null : HashAlgorithmName.SHA1, sha1, _ => Task.CompletedTask,
            cancellationToken, MaximumMetadata);
        return await JsonSerializer.DeserializeAsync<T>(body, s_json, cancellationToken)
            ?? throw Failed("download_failed", $"{uri.Host} sent no data.");
    }

    /// <summary>The body in a temporary file (deleted when closed), checked against <paramref name="expected"/> (hex).</summary>
    public async Task<FileStream> DownloadAsync(Uri uri, HashAlgorithmName? algorithm, string? expected, Func<double, Task> progress,
        CancellationToken cancellationToken, long maximum = MaximumDownload)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(progress);
        using var response = await SendAsync(uri, cancellationToken);
        var length = response.Content.Headers.ContentLength;
        if (length > maximum)
        {
            throw Failed("download_failed", $"The download from {uri.Host} is larger than expected.");
        }

        var file = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            using var hash = algorithm is { } name ? IncrementalHash.CreateHash(name) : null;
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[81920];
            long total = 0;
            var reported = -1;
            while (true)
            {
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                stall.CancelAfter(StallTimeout);
                var read = await body.ReadAsync(buffer, stall.Token);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > maximum)
                {
                    throw Failed("download_failed", $"The download from {uri.Host} is larger than expected.");
                }

                hash?.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                if (length > 0 && (int)(total * 100 / length.Value) is var percent && percent != reported)
                {
                    reported = percent;
                    await progress(percent);
                }
            }

            if (hash is not null && !Convert.ToHexString(hash.GetHashAndReset()).Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                throw Failed("checksum_mismatch", $"The download from {uri.Host} does not match its published checksum.");
            }

            file.Position = 0;
            return file;
        }
        catch
        {
            await file.DisposeAsync();
            throw;
        }
    }

    /// <summary>Follows redirects by hand, so every hop is checked against the allowed hosts.</summary>
    public async Task<HttpResponseMessage> SendAsync(Uri uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        for (var hop = 0; hop <= MaximumRedirects; hop++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || !hosts.Contains(uri.Host))
            {
                throw Failed("untrusted_host", $"Pulsar does not download {what} from {uri.GetLeftPart(UriPartial.Authority)}.");
            }

            HttpResponseMessage response;
            using (var headers = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                headers.CancelAfter(HeadersTimeout);
                try
                {
                    response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, headers.Token);
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new GameProcessException($"{codePrefix}.download_failed", $"{uri.Host} did not answer within {HeadersTimeout.TotalSeconds:0} seconds.",
                        ex);
                }
            }

            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                response.Dispose();
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                response.Dispose();
                throw Failed("download_failed", $"{uri.Host} answered {status}.");
            }

            return response;
        }

        throw Failed("download_failed", "Too many redirects.");
    }

    public static Uri Https(string url, string codePrefix) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        ? uri
        : throw new GameProcessException($"{codePrefix}.download_failed", $"'{url}' is not a download address.");

    private GameProcessException Failed(string code, string message) => new($"{codePrefix}.{code}", message);
}
