using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Pulsar.Domain.Certificates;

namespace Pulsar.Api.Certificates;

/// <summary>
/// The http-01 tokens being validated now and what each serves (RFC 8555 section 8.3): only these, and only while their
/// order waits for its challenge. A handful at most (one order at a time asks for one).
/// </summary>
internal sealed class AcmeChallenges
{
    public const int MaxPending = 8;

    private readonly ConcurrentDictionary<string, string> _pending = new(StringComparer.Ordinal);

    /// <summary>How many tokens are served now.</summary>
    public int Count => _pending.Count;

    public bool TryGet(string token, out string keyAuthorization) => _pending.TryGetValue(token, out keyAuthorization!);

    /// <summary>Serves <paramref name="keyAuthorization"/> for <paramref name="token"/> until the result is disposed.</summary>
    public IDisposable Add(string token, string keyAuthorization)
    {
        if (!AcmeClient.IsToken(token) || _pending.Count >= MaxPending || !_pending.TryAdd(token, keyAuthorization))
        {
            throw new AcmeException("The challenge cannot be served (a malformed or repeated token, or too many at once).");
        }

        return new Registration(this, token);
    }

    private sealed class Registration(AcmeChallenges owner, string token) : IDisposable
    {
        public void Dispose() => owner._pending.TryRemove(token, out _);
    }
}

/// <summary>
/// What a chain must lead to: the machine's trusted roots (<see cref="Platform"/>, production), Let's Encrypt's staging
/// roots that nothing trusts (<see cref="Staging"/>), or roots given (<see cref="Only"/>, tests: nothing in any store).
/// </summary>
internal sealed record ChainTrust(bool AllowUntrustedRoot, X509Certificate2Collection? Roots)
{
    public static ChainTrust Platform { get; } = new(false, null);

    public static ChainTrust Staging { get; } = new(true, null);

    public static ChainTrust Only(X509Certificate2Collection roots) => new(false, roots);

    /// <summary>The trust for a directory: production must chain to a trusted root, staging need not.</summary>
    public static ChainTrust For(AcmeDirectoryKind directory, X509Certificate2Collection? rootsOverride) =>
        rootsOverride is not null ? Only(rootsOverride) : directory == AcmeDirectoryKind.Staging ? Staging : Platform;
}

/// <summary>A certificate Let's Encrypt issued: the chain (leaf first), its PEM, and the leaf's private key (PKCS#8).</summary>
internal sealed record IssuedCertificate(IReadOnlyList<X509Certificate2> Chain, string ChainPem, byte[] PrivateKey) : IDisposable
{
    public X509Certificate2 Leaf => Chain[0];

    public void Dispose()
    {
        foreach (var certificate in Chain)
        {
            certificate.Dispose();
        }

        CryptographicOperations.ZeroMemory(PrivateKey);
    }
}

/// <summary>
/// One order, from <c>newOrder</c> to the downloaded chain: the IP address identifier with the <c>shortlived</c> profile,
/// its http-01 challenge served by <see cref="AcmeChallenges"/>, a CSR on a new ECDSA P-256 key with the address as its
/// only name, and the chain checked before anything keeps it (<see cref="Verify"/>).
/// </summary>
internal static class AcmeIssuance
{
    public const int MaxChainCertificates = 4;

    public static async Task<IssuedCertificate> IssueAsync(AcmeClient client, IPAddress address, string? replaces, AcmeChallenges challenges,
        TimeSpan timeout, TimeSpan pollInterval, ChainTrust trust, TimeProvider clock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(address);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            return await IssueCoreAsync(client, address, replaces, challenges, pollInterval, trust, clock, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AcmeException("The ACME server did not validate and issue the certificate in time.", AcmeFailure.Unreachable);
        }
    }

    private static async Task<IssuedCertificate> IssueCoreAsync(AcmeClient client, IPAddress address, string? replaces, AcmeChallenges challenges,
        TimeSpan pollInterval, ChainTrust trust, TimeProvider clock, CancellationToken cancellationToken)
    {
        AcmeOrder order;
        try
        {
            order = await client.NewOrderAsync(address, replaces, cancellationToken);
        }
        catch (AcmeException ex) when (replaces is not null && ex.Failure == AcmeFailure.AlreadyReplaced)
        {
            order = await client.NewOrderAsync(address, null, cancellationToken);
        }

        if (order.Status == "invalid")
        {
            throw new AcmeException($"The ACME server refused the order{Detail(order.Error)}");
        }

        if (order.Authorizations.Count != 1)
        {
            throw new AcmeException("The ACME server asked for more than the one address of the order.");
        }

        await AuthorizeAsync(client, order.Authorizations[0], address, challenges, pollInterval, clock, cancellationToken);

        // Ready once its one authorization is valid (a moment after the challenge, at most).
        order = await PollOrderAsync(client, order, ["ready", "processing", "valid"], pollInterval, clock, cancellationToken);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (order.Status == "ready")
        {
            order = await client.FinalizeAsync(order, CreateSigningRequest(key, address), cancellationToken);
        }
        else
        {
            throw new AcmeException("The ACME server finalized the order before Pulsar sent its key: refused.");
        }

        order = await PollOrderAsync(client, order, ["valid"], pollInterval, clock, cancellationToken);
        var pem = await client.DownloadCertificateAsync(order.Certificate ?? throw new AcmeException("The ACME server's valid order has no certificate."),
            cancellationToken);
        var chain = ParseChain(pem);
        try
        {
            Verify(chain, key, address, clock.GetUtcNow(), trust);
            return new IssuedCertificate(chain, ChainPem(chain), key.ExportPkcs8PrivateKey());
        }
        catch
        {
            foreach (var certificate in chain)
            {
                certificate.Dispose();
            }

            throw;
        }
    }

    private static async Task AuthorizeAsync(AcmeClient client, Uri url, IPAddress address, AcmeChallenges challenges, TimeSpan pollInterval,
        TimeProvider clock, CancellationToken cancellationToken)
    {
        var authorization = await client.GetAuthorizationAsync(url, cancellationToken);
        CheckIdentifier(authorization, address);
        if (authorization.Status == "valid")
        {
            return; // validated within the last hours (the profile reuses authorizations for 7 hours)
        }

        if (authorization.Status != "pending")
        {
            throw new AcmeException($"The ACME server's authorization of {address} is {authorization.Status}.");
        }

        var challenge = authorization.Challenges.FirstOrDefault(c => c.Type == "http-01")
            ?? throw new AcmeException("The ACME server offers no http-01 challenge for this address.");
        using (challenges.Add(challenge.Token, client.KeyAuthorization(challenge.Token)))
        {
            await client.RespondAsync(challenge, cancellationToken);
            while (true)
            {
                await Task.Delay(pollInterval, clock, cancellationToken);
                authorization = await client.GetAuthorizationAsync(url, cancellationToken);
                CheckIdentifier(authorization, address);
                if (authorization.Status == "valid")
                {
                    return;
                }

                if (authorization.Status != "pending")
                {
                    var failed = authorization.Challenges.FirstOrDefault(c => c.Token == challenge.Token)?.Error;
                    throw new AcmeException($"Let's Encrypt could not check port 80 of {address}{Detail(failed)}");
                }
            }
        }
    }

    private static async Task<AcmeOrder> PollOrderAsync(AcmeClient client, AcmeOrder order, string[] wanted, TimeSpan pollInterval, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        while (!wanted.Contains(order.Status))
        {
            if (order.Status == "invalid")
            {
                throw new AcmeException($"The ACME server gave up on the order{Detail(order.Error)}");
            }

            await Task.Delay(pollInterval, clock, cancellationToken);
            order = await client.GetOrderAsync(order.Url, cancellationToken);
        }

        return order;
    }

    private static void CheckIdentifier(AcmeAuthorization authorization, IPAddress address)
    {
        if (authorization.IdentifierType != "ip" || !IPAddress.TryParse(authorization.IdentifierValue, out var named)
            || !PublicAddresses.Normalize(named).Equals(PublicAddresses.Normalize(address)))
        {
            throw new AcmeException("The ACME server's authorization is for another identifier than the order's address.");
        }
    }

    private static string Detail(string? error) => error is null ? "." : ": " + error;

    /// <summary>A CSR (PKCS#10) with an empty subject and the address as its only Subject Alternative Name.</summary>
    internal static byte[] CreateSigningRequest(ECDsa key, IPAddress address)
    {
        var request = new CertificateRequest(new X500DistinguishedName(string.Empty), key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(address);
        request.CertificateExtensions.Add(names.Build(critical: true));
        return request.CreateSigningRequest();
    }

    /// <summary>
    /// The PEM chain read strictly: only <c>CERTIFICATE</c> blocks, nothing but whitespace around them, one to four of them.
    /// </summary>
    internal static List<X509Certificate2> ParseChain(string pem)
    {
        ArgumentNullException.ThrowIfNull(pem);
        var chain = new List<X509Certificate2>();
        try
        {
            var rest = pem.AsSpan();
            while (!rest.Trim().IsEmpty)
            {
                if (!PemEncoding.TryFind(rest, out var fields) || !rest[..fields.Location.Start].IsWhiteSpace()
                    || !rest[fields.Label].SequenceEqual("CERTIFICATE") || chain.Count == MaxChainCertificates)
                {
                    throw new AcmeException("The ACME server's certificate chain is not one to four PEM certificates.");
                }

                var der = new byte[fields.DecodedDataLength];
                if (!Convert.TryFromBase64Chars(rest[fields.Base64Data], der, out var written) || written != der.Length)
                {
                    throw new AcmeException("The ACME server's certificate chain is not valid PEM.");
                }

                chain.Add(X509CertificateLoader.LoadCertificate(der));
                rest = rest[fields.Location.End..];
            }

            return chain.Count > 0 ? chain : throw new AcmeException("The ACME server sent no certificate.");
        }
        catch (CryptographicException ex)
        {
            foreach (var certificate in chain)
            {
                certificate.Dispose();
            }

            throw new AcmeException("The ACME server's certificate chain holds something that is not a certificate.", inner: ex);
        }
        catch
        {
            foreach (var certificate in chain)
            {
                certificate.Dispose();
            }

            throw;
        }
    }

    /// <summary>
    /// What the panel accepts to serve: a leaf on the key the CSR had, for the address and nothing else (one IP address, no
    /// DNS name), for server authentication, no certificate authority, valid now; and signed by the next certificate of the
    /// chain, which must be there (browsers fetch no missing intermediate). The chain is built from what was sent and the
    /// machine's stores only: nothing is downloaded and revocation is not checked. It must lead to a trusted root, unless
    /// <paramref name="trust"/> allows an untrusted one (Let's Encrypt's staging); the leaf-to-intermediate signature is
    /// checked either way.
    /// </summary>
    internal static void Verify(IReadOnlyList<X509Certificate2> chain, ECDsa key, IPAddress address, DateTimeOffset now, ChainTrust? trust = null)
    {
        trust ??= ChainTrust.Platform;
        var leaf = chain[0];
        using (var leafKey = leaf.GetECDsaPublicKey())
        {
            if (leafKey is null || !leafKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(key.ExportSubjectPublicKeyInfo()))
            {
                throw new AcmeException("The ACME server's certificate is not on the key Pulsar sent.");
            }
        }

        var names = leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().ToList();
        var addresses = names.SelectMany(n => n.EnumerateIPAddresses()).Select(PublicAddresses.Normalize).ToList();
        if (names.Count != 1 || names[0].EnumerateDnsNames().Any() || addresses.Count != 1 || !addresses[0].Equals(PublicAddresses.Normalize(address)))
        {
            throw new AcmeException($"The ACME server's certificate does not name {address} alone.");
        }

        if (leaf.Extensions.OfType<X509BasicConstraintsExtension>().Any(b => b.CertificateAuthority)
            || leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(e => !e.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>().Any(o => o.Value == "1.3.6.1.5.5.7.3.1")))
        {
            throw new AcmeException("The ACME server's certificate is not a web server's.");
        }

        if (now < new DateTimeOffset(leaf.NotBefore.ToUniversalTime(), TimeSpan.Zero) - TimeSpan.FromMinutes(5)
            || now >= new DateTimeOffset(leaf.NotAfter.ToUniversalTime(), TimeSpan.Zero))
        {
            throw new AcmeException("The ACME server's certificate is not valid now (check this PC's clock).");
        }

        if (chain.Count < 2)
        {
            throw new AcmeException("The ACME server sent the certificate without its intermediate.");
        }

        using var built = new X509Chain();
        built.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        built.ChainPolicy.DisableCertificateDownloads = true;
        built.ChainPolicy.VerificationTime = now.UtcDateTime;
        built.ChainPolicy.VerificationTimeIgnored = false;
        if (trust.Roots is { } roots)
        {
            built.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            built.ChainPolicy.CustomTrustStore.AddRange(roots);
        }

        foreach (var certificate in chain.Skip(1))
        {
            built.ChainPolicy.ExtraStore.Add(certificate);
        }
        built.Build(leaf);
        var tolerated = trust.AllowUntrustedRoot ? X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.PartialChain : X509ChainStatusFlags.NoError;
        if (built.ChainElements.Count < 2 || !built.ChainElements[1].Certificate.RawDataMemory.Span.SequenceEqual(chain[1].RawDataMemory.Span)
            || built.ChainElements.Any(e => e.ChainElementStatus.Any(s => (s.Status & ~tolerated) != 0))
            || (!trust.AllowUntrustedRoot && built.ChainStatus.Any(s => s.Status != X509ChainStatusFlags.NoError)))
        {
            throw new AcmeException(trust.AllowUntrustedRoot
                ? "The ACME server's certificate is not signed by the intermediate it came with."
                : "The ACME server's certificate is not signed by the intermediate it came with, or does not lead to a root this PC trusts.");
        }
    }

    /// <summary>The chain as PEM, written here (not the server's text): what is stored and served.</summary>
    internal static string ChainPem(IReadOnlyList<X509Certificate2> chain)
    {
        var text = new StringBuilder();
        foreach (var certificate in chain)
        {
            text.Append(PemEncoding.Write("CERTIFICATE", certificate.RawData)).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>ARI's identifier of a certificate (RFC 9773): its authority key identifier and serial number, base64url.</summary>
    internal static string? RenewalInfoId(X509Certificate2 certificate)
    {
        var authority = certificate.Extensions.OfType<X509AuthorityKeyIdentifierExtension>().FirstOrDefault()?.KeyIdentifier;
        return authority is { Length: > 0 } id
            ? Base64Url.EncodeToString(id.Span) + "." + Base64Url.EncodeToString(certificate.SerialNumberBytes.Span)
            : null;
    }
}
