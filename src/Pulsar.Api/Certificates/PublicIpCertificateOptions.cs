using System.Net;
using Pulsar.Domain.Certificates;

namespace Pulsar.Api.Certificates;

/// <summary>
/// <c>PublicIpCertificate</c> settings: the operator's side of the public IP certificate (docs/guides/install.md, "Access
/// from the internet with your public IP"). Whether it is on, the address and the directory are panel settings an
/// administrator changes at runtime (<c>/api/v1/public-ip-certificate</c>); these are where the challenge is answered.
/// </summary>
public sealed class PublicIpCertificateOptions
{
    public const string SectionName = "PublicIpCertificate";

    /// <summary>Let's Encrypt's production directory.</summary>
    public const string ProductionDirectory = "https://acme-v02.api.letsencrypt.org/directory";

    /// <summary>Let's Encrypt's staging directory: the same flow, certificates no browser trusts.</summary>
    public const string StagingDirectory = "https://acme-staging-v02.api.letsencrypt.org/directory";

    /// <summary>
    /// The port the http-01 challenge is answered on while the feature is on (80 unless set). Let's Encrypt always
    /// connects to port 80 of the public address: another port only works when the router forwards its port 80 there.
    /// </summary>
    public int ChallengePort { get; set; } = 80;

    /// <summary>The address the challenge listener binds: an IP address, or <c>*</c> (the default) for every address.</summary>
    public string? ChallengeBindAddress { get; set; }

    /// <summary>The most connections the challenge listener holds at once; one more is closed on arrival.</summary>
    public int MaxChallengeConnections { get; set; } = 32;

    /// <summary>Of those, the most from one client address (an IPv4 address, an IPv6 /64).</summary>
    public int MaxChallengeConnectionsPerAddress { get; set; } = 4;

    /// <summary>Tests: false keeps the renewal (and the challenge listener) from running at all.</summary>
    internal bool Run { get; set; } = true;

    /// <summary>Tests: another directory than Let's Encrypt's (a fake ACME server on the loopback).</summary>
    internal string? DirectoryOverride { get; set; }

    /// <summary>Tests: what the ACME client sends its requests through (a handler trusting the fake server's certificate).</summary>
    internal Func<HttpMessageHandler>? HandlerOverride { get; set; }

    /// <summary>Tests: the only roots an issued chain must lead to (the fake ACME server's CA), instead of the machine's trusted roots.</summary>
    internal System.Security.Cryptography.X509Certificates.X509Certificate2Collection? TrustedRootsOverride { get; set; }

    /// <summary>Tests: a port (0: any free one) for the challenge listener instead of <see cref="ChallengePort"/>.</summary>
    internal int? ChallengePortOverride { get; set; }

    /// <summary>
    /// Tests: loopback connections count as coming from the internet to the certificate's address (Kestrel's tests can only
    /// connect over the loopback).
    /// </summary>
    internal bool TreatLoopbackAsInternet { get; set; }

    /// <summary>Tests: how long an order may take from the challenge to the certificate.</summary>
    internal TimeSpan ValidationTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Tests: the shortest wait between two polls of an authorization or an order (Retry-After raises it).</summary>
    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    internal string DirectoryUrl(AcmeDirectoryKind kind) =>
        DirectoryOverride ?? (kind == AcmeDirectoryKind.Staging ? StagingDirectory : ProductionDirectory);

    internal string? Problem() => this switch
    {
        { ChallengePort: < 1 or > 65535 } => $"{SectionName}:ChallengePort must be between 1 and 65535.",
        _ when ChallengeBindAddress is not null and not "*" && !IPAddress.TryParse(ChallengeBindAddress, out _) =>
            $"{SectionName}:ChallengeBindAddress must be an IP address or *.",
        { MaxChallengeConnections: < 1 or > 10000 } => $"{SectionName}:MaxChallengeConnections must be between 1 and 10000.",
        _ when MaxChallengeConnectionsPerAddress < 1 || MaxChallengeConnectionsPerAddress > MaxChallengeConnections =>
            $"{SectionName}:MaxChallengeConnectionsPerAddress must be at least 1 and at most {SectionName}:MaxChallengeConnections.",
        _ => null,
    };
}

/// <summary>Names the <c>PublicIpCertificate</c> setting that is out of range.</summary>
internal sealed class PublicIpCertificateOptionsValidation : Microsoft.Extensions.Options.IValidateOptions<PublicIpCertificateOptions>
{
    public Microsoft.Extensions.Options.ValidateOptionsResult Validate(string? name, PublicIpCertificateOptions options) =>
        options.Problem() is { } problem
            ? Microsoft.Extensions.Options.ValidateOptionsResult.Fail(problem)
            : Microsoft.Extensions.Options.ValidateOptionsResult.Success;
}
