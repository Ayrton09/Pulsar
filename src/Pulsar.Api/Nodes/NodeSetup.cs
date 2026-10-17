using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Pulsar.Api.Agents;
using Pulsar.Api.Http;
using Pulsar.Api.Security;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;

namespace Pulsar.Api.Nodes;

/// <summary><c>Downloads</c> settings: what the panel serves to the machines it enrolls (Phase 18).</summary>
public sealed class DownloadsOptions
{
    public const string SectionName = "Downloads";

    /// <summary>
    /// The folder <c>PulsarAgentSetup.exe</c> is served from: <c>downloads</c> next to the panel's executable unless set (a
    /// relative path is taken from there too). Only that one file name is ever served from it.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// How many downloads one client address (an IPv6 /64, the loopback as one) may have running at once: the route is
    /// anonymous, and each download is the whole setup.
    /// </summary>
    public int ConcurrentPerAddress { get; set; } = 4;

    /// <summary>How many downloads one client address may start in an hour (more answer 429 until the hour ends).</summary>
    public int PerAddressPerHour { get; set; } = 60;
}

/// <summary>
/// The agent's setup the panel offers for download (<c>GET /downloads/PulsarAgentSetup.exe</c>, docs/architecture/08 T37):
/// one fixed file from one fixed folder, no path from the request.
/// </summary>
internal sealed class AgentSetupDownload(IOptions<DownloadsOptions> options)
{
    public const string FileName = "PulsarAgentSetup.exe";

    public const string RoutePath = "/downloads/" + FileName;

    public string FilePath { get; } = System.IO.Path.Combine(
        System.IO.Path.GetFullPath(options.Value.Path ?? "downloads", AppContext.BaseDirectory), FileName);

    /// <summary>The file is there (a folder of that name is not).</summary>
    public bool Exists => File.Exists(FilePath);

    // The hash of the version of the file last read (its time and size), computed once for it.
    private HashedSetup? _hashed;

    /// <summary>The file to send, or null when it is not there or cannot be read now.</summary>
    public FileStream? Open()
    {
        try
        {
            return new FileStream(FilePath, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The file's SHA-256 (lowercase hex), shown in Add node for whoever downloads it to compare (docs/architecture/08,
    /// T37): read once for each version of the file (its time and size, from the handle it is read through, which keeps
    /// it from being written meanwhile), or null when it is not there or cannot be read now.
    /// </summary>
    public async ValueTask<string?> Sha256Async(CancellationToken cancellationToken)
    {
        await using var file = Open();
        if (file is null)
        {
            return null;
        }

        var version = (File.GetLastWriteTimeUtc(file.SafeFileHandle), file.Length);
        if (Volatile.Read(ref _hashed) is { } hashed && hashed.Version == version)
        {
            return hashed.Sha256;
        }

        try
        {
            var sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken));
            Volatile.Write(ref _hashed, new HashedSetup(version, sha256));
            return sha256;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private sealed record HashedSetup((DateTime LastWrite, long Length) Version, string Sha256);
}

/// <summary>
/// <see cref="INodeSetup"/> from the current request: the origin it reached the panel by (what the browser shows, and what
/// the PowerShell command uses), and the certificate Kestrel served on its connection.
/// </summary>
internal sealed class HttpNodeSetup(IHttpContextAccessor accessor, AgentSetupDownload download) : INodeSetup
{
    public PanelAddress? RequestAddress => accessor.HttpContext is { } http ? AddressOf(http) : null;

    public ValueTask<string?> AgentSetupSha256Async(CancellationToken cancellationToken) => download.Sha256Async(cancellationToken);

    /// <summary>
    /// The origin <paramref name="http"/> reached the panel by, exactly as agents check it (<see cref="AgentEndpoints.PanelOrigin"/>),
    /// with the certificate the panel served it; null when an agent could not use that origin, or it is not written as an
    /// agent would write it (a default port, which browsers leave out): rewriting it could name another host.
    /// </summary>
    internal static PanelAddress? AddressOf(HttpContext http)
    {
        var url = AgentEndpoints.PanelOrigin(http.Request);
        return NodeCode.IsValidUrl(url) ? new PanelAddress(url, ServedCertificateHash(http)) : null;
    }

    /// <summary>
    /// The hash of the certificate Kestrel presented in this connection's TLS handshake, when the client saw it
    /// (<see cref="ServedCertificate"/>): none over plain HTTP, over HTTP/3, or through a proxy that says so. A proxy that
    /// re-encrypts without saying so gives Kestrel's certificate, which the node's setup then finds is not the one it is
    /// served, and refuses. None either for the public IP certificate (Let's Encrypt): publicly trusted and replaced every
    /// few days, it is checked as any site's is, never pinned (docs/architecture/08, T40).
    /// </summary>
    private static string? ServedCertificateHash(HttpContext http) =>
        ServedCertificate.Der(http) is { } der ? NodeCode.CertificateHash(der) : null;
}

internal static class DownloadEndpoints
{
    /// <summary>
    /// <c>GET /downloads/PulsarAgentSetup.exe</c>, anonymous: the public installer, with no secret inside, for a machine
    /// that is not signed in to the panel (docs/architecture/08, T37). The node code, which is the secret, comes separately.
    /// </summary>
    public static IEndpointRouteBuilder MapDownloadEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(AgentSetupDownload.RoutePath, Results<FileStreamHttpResult, ProblemHttpResult> (AgentSetupDownload setup, HttpResponse response) =>
            {
                if (setup.Open() is not { } file)
                {
                    return ApiProblems.Create(StatusCodes.Status404NotFound, "downloads.not_found",
                        "This panel has no agent setup to download.", "Copy PulsarAgentSetup.exe from the Pulsar release to the machine instead.");
                }

                // Checked again at every download: a panel update brings a new setup.
                response.Headers.CacheControl = "no-cache";
                return TypedResults.File(file, "application/octet-stream", AgentSetupDownload.FileName,
                    lastModified: File.GetLastWriteTimeUtc(file.SafeFileHandle));
            })
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicy)
            .ExcludeFromDescription();
        return app;
    }

    /// <summary>The rate limit policy of the download route (<see cref="CreateLimiter"/>, one per client address).</summary>
    public const string RateLimitPolicy = "downloads";

    /// <summary>
    /// One client address's budget for the anonymous download (<see cref="DownloadsOptions"/>): so many at once, held for
    /// as long as the file is being sent, and so many an hour. Whatever is over either answers 429 at once.
    /// </summary>
    public static RateLimiter CreateLimiter(DownloadsOptions options) => CreateLimiter(options, TimeSpan.FromHours(1));

    /// <summary>The same with another window than the hour (tests roll it over in milliseconds).</summary>
    internal static RateLimiter CreateLimiter(DownloadsOptions options, TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(options);
        return CreateLimiter(
            new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = options.ConcurrentPerAddress, QueueLimit = 0 }),
            new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions { PermitLimit = options.PerAddressPerHour, Window = window, QueueLimit = 0 }));
    }

    /// <summary>The same over limiters made by the caller; disposing the result disposes both.</summary>
    internal static RateLimiter CreateLimiter(ConcurrencyLimiter concurrency, FixedWindowRateLimiter window) => new DownloadLimiter(concurrency, window);

    /// <summary>
    /// The concurrency cap and the hourly window chained. A chain reports the idle time of its first limiter, which is idle
    /// as soon as a download ends; ASP.NET Core drops a partition idle for 10 seconds, and the hour's count with it. This
    /// one is idle only when both limiters are: no download holds a place (a slow reader can hold one for hours, past
    /// several windows, and a partition dropped then would give its address a fresh cap), and the window holds no count.
    /// A chain also does not dispose its limiters, and ASP.NET Core evicts with DisposeAsync, which would only reach
    /// <c>Dispose(false)</c> of the base: both are disposed here, or the window's timer stays rooted for every address.
    /// </summary>
    private sealed class DownloadLimiter : RateLimiter
    {
        private readonly RateLimiter _chain;
        private readonly ConcurrencyLimiter _concurrency;
        private readonly FixedWindowRateLimiter _hour;

        public DownloadLimiter(ConcurrencyLimiter concurrency, FixedWindowRateLimiter hour)
        {
            _concurrency = concurrency;
            _hour = hour;
            _chain = CreateChained(concurrency, hour);
        }

        public override TimeSpan? IdleDuration
        {
            get
            {
                // Null while a place is held or the window holds a count; otherwise the shorter of the two idle times,
                // so the partition stays at least 10 seconds after the last download ended too.
                if (_concurrency.IdleDuration is not { } held || _hour.IdleDuration is not { } counted)
                {
                    return null;
                }

                return held < counted ? held : counted;
            }
        }

        public override RateLimiterStatistics? GetStatistics() => _chain.GetStatistics();

        protected override RateLimitLease AttemptAcquireCore(int permitCount) => _chain.AttemptAcquire(permitCount);

        protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken) =>
            _chain.AcquireAsync(permitCount, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _chain.Dispose();
                _concurrency.Dispose();
                _hour.Dispose();
            }

            base.Dispose(disposing);
        }

        protected override ValueTask DisposeAsyncCore()
        {
            Dispose(true);
            return base.DisposeAsyncCore();
        }
    }
}
