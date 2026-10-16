using System.Net;

namespace Pulsar.Api.Sftp;

/// <summary>
/// <c>Sftp</c> settings: where the SFTP server listens and its limits (docs/guides/sftp.md). Whether it runs, its port
/// and the host users are told to connect to are panel settings an administrator changes at runtime
/// (<c>/api/v1/sftp/settings</c>); these are the operator's.
/// </summary>
public sealed class SftpOptions
{
    public const string SectionName = "Sftp";

    /// <summary>
    /// The address to listen on: an IP address, or <c>*</c> for every address. Unset: as the panel's HTTPS endpoints
    /// listen (every address when one of them does, else the addresses they name, the loopback for <c>localhost</c>).
    /// </summary>
    public string? BindAddress { get; set; }

    /// <summary>Connections at once, signed in or not. One more is closed on arrival.</summary>
    public int MaxConnections { get; set; } = 64;

    /// <summary>Of those, connections that have not signed in yet.</summary>
    public int MaxUnauthenticatedConnections { get; set; } = 16;

    /// <summary>Connections from one client address (an IPv4 address, an IPv6 /64, the whole loopback as one).</summary>
    public int MaxConnectionsPerAddress { get; set; } = 8;

    /// <summary>Signed-in sessions of one user.</summary>
    public int MaxSessionsPerUser { get; set; } = 4;

    /// <summary>Password checks one connection may make before it is closed (the per-address limit still applies).</summary>
    public int MaxPasswordAttemptsPerConnection { get; set; } = 3;

    /// <summary>Time from the connection to a completed sign-in.</summary>
    public TimeSpan AuthenticationTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>A signed-in session that sends no SFTP request for this long is closed.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Open files and folders of one SFTP session.</summary>
    public int MaxHandlesPerSession { get; set; } = 32;

    /// <summary>The largest SFTP packet (the client is told through <c>limits@openssh.com</c>); reads and writes stay below it.</summary>
    public int MaxPacketBytes { get; set; } = 256 * 1024;

    /// <summary>
    /// Writes that arrive ahead of the next offset, kept per session until the gap is filled; a gap that does not fit
    /// fails that file's upload.
    /// </summary>
    public int MaxReorderBytes { get; set; } = 1024 * 1024;

    /// <summary>
    /// How far a read may skip ahead of, or go back behind, a file's running download (the skipped bytes are kept) before
    /// the download starts again at the requested offset.
    /// </summary>
    public int ReadWindowBytes { get; set; } = 256 * 1024;

    /// <summary>Tests: false keeps the server from listening at all.</summary>
    internal bool Listen { get; set; } = true;

    /// <summary>Tests: a port (0: any free one) instead of the panel setting.</summary>
    internal int? PortOverride { get; set; }

    internal int MaxDataBytes => MaxPacketBytes - 1024;

    internal static bool IsValidBindAddress(string? value) => value is null or "*" || IPAddress.TryParse(value, out _);

    internal string? Problem() => this switch
    {
        _ when !IsValidBindAddress(BindAddress) => "Sftp:BindAddress must be an IP address or *.",
        { MaxConnections: < 1 } => "Sftp:MaxConnections must be at least 1.",
        _ when MaxUnauthenticatedConnections < 1 || MaxUnauthenticatedConnections > MaxConnections =>
            "Sftp:MaxUnauthenticatedConnections must be at least 1 and at most Sftp:MaxConnections.",
        _ when MaxConnectionsPerAddress < 1 || MaxConnectionsPerAddress > MaxConnections =>
            "Sftp:MaxConnectionsPerAddress must be at least 1 and at most Sftp:MaxConnections.",
        { MaxSessionsPerUser: < 1 } => "Sftp:MaxSessionsPerUser must be at least 1.",
        { MaxPasswordAttemptsPerConnection: < 1 or > 10 } => "Sftp:MaxPasswordAttemptsPerConnection must be between 1 and 10.",
        _ when AuthenticationTimeout <= TimeSpan.Zero || AuthenticationTimeout > TimeSpan.FromMinutes(10) =>
            "Sftp:AuthenticationTimeout must be more than zero and at most 10 minutes.",
        _ when IdleTimeout <= TimeSpan.Zero => "Sftp:IdleTimeout must be more than zero.",
        { MaxHandlesPerSession: < 1 or > 1024 } => "Sftp:MaxHandlesPerSession must be between 1 and 1024.",
        { MaxPacketBytes: < 34 * 1024 or > 1024 * 1024 } => "Sftp:MaxPacketBytes must be between 34816 and 1048576.",
        { MaxReorderBytes: < 0 or > 64 * 1024 * 1024 } => "Sftp:MaxReorderBytes must be between 0 and 64 MiB.",
        { ReadWindowBytes: < 0 or > 64 * 1024 * 1024 } => "Sftp:ReadWindowBytes must be between 0 and 64 MiB.",
        _ => null,
    };
}

/// <summary>Names the <c>Sftp</c> setting that is out of range.</summary>
internal sealed class SftpOptionsValidation : Microsoft.Extensions.Options.IValidateOptions<SftpOptions>
{
    public Microsoft.Extensions.Options.ValidateOptionsResult Validate(string? name, SftpOptions options) =>
        options.Problem() is { } problem
            ? Microsoft.Extensions.Options.ValidateOptionsResult.Fail(problem)
            : Microsoft.Extensions.Options.ValidateOptionsResult.Success;
}
