using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Audit;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;

namespace Pulsar.Application.Access;

/// <summary>Encrypts sign-in providers' client secrets (ASP.NET Core Data Protection in Infrastructure).</summary>
public interface ISsoSecretProtector
{
    byte[] Protect(string secret);

    string Unprotect(byte[] protectedSecret);
}

/// <summary>What the panel needs to send a user to a provider and to redeem what comes back.</summary>
public sealed record SsoSettings(SsoKind Kind, string ClientId, string ClientSecret, string? Authority);

/// <summary>The account a provider vouched for: its own stable id for it, and a name to show next to the link.</summary>
public sealed record SsoIdentity(string Subject, string? Name);

/// <summary>A provider that could not be reached, refused the code, or answered something that does not hold up.</summary>
public sealed class SsoException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary><c>sso.provider_unavailable</c>, <c>sso.denied</c> or <c>sso.token_invalid</c>.</summary>
    public string Code { get; } = code;
}

/// <summary>Talks to the providers (Infrastructure): their authorization address, then the code for the account.</summary>
public interface ISsoClient
{
    /// <summary>Where to send the browser: the authorization code flow with PKCE (S256), a state and, for OpenID Connect, a nonce.</summary>
    Task<Uri> AuthorizeAsync(SsoSettings settings, string redirectUri, string state, string codeChallenge, string nonce, CancellationToken cancellationToken);

    /// <summary>
    /// Redeems the code at the provider's token endpoint and returns the account: Discord's from its API, an OpenID
    /// Connect provider's from the ID token, checked (signature against the provider's keys, issuer, audience, time, nonce).
    /// </summary>
    Task<SsoIdentity> RedeemAsync(SsoSettings settings, string code, string codeVerifier, string redirectUri, string nonce,
        CancellationToken cancellationToken);
}

/// <param name="HasSecret">A client secret is stored (it is never shown).</param>
/// <param name="CallbackPath">Where the provider sends users back, below the panel's address: to register with the provider.</param>
public sealed record SsoProviderView(SsoKind Kind, bool Enabled, string ClientId, bool HasSecret, string? Authority, string? DisplayName,
    string CallbackPath, DateTimeOffset? UpdatedAt);

/// <param name="LinksStale">
/// The OpenID Connect issuer or client changed: the accounts linked to it are another issuer's subjects now, and must
/// go before anyone signs in with them (the caller removes them in the same transaction).
/// </param>
public sealed record SsoProviderUpdate(SsoProviderView View, bool LinksStale);

/// <param name="ClientSecret">Null or empty keeps the stored one.</param>
public sealed record SsoProviderDefinition(bool Enabled, string? ClientId, string? ClientSecret, string? Authority, string? DisplayName);

/// <summary>A provider users may sign in with, as the sign-in page shows it.</summary>
public sealed record SsoOption(SsoKind Kind, string Name);

/// <summary>
/// The sign-in providers (Pulsar 1.0.18): Discord, Google and one OpenID Connect provider, each the administrator's
/// application. Only accounts that already exist sign in with them, once their owner linked them.
/// </summary>
public sealed class SsoProviders(IPulsarDbContext db, ISsoSecretProtector secrets, IAuditLog audit, TimeProvider clock)
{
    public static string CallbackPath(SsoKind kind) => $"/api/v1/auth/sso/{Slug(kind)}/callback";

    public static string Slug(SsoKind kind) => kind.ToString().ToLowerInvariant();

    public static SsoKind? Parse(string? slug) =>
        Enum.GetValues<SsoKind>().Cast<SsoKind?>().FirstOrDefault(k => string.Equals(Slug(k!.Value), slug, StringComparison.Ordinal));

    public async Task<IReadOnlyList<SsoProviderView>> ListAsync(CancellationToken cancellationToken)
    {
        var stored = await db.SsoProviders.AsNoTracking().ToListAsync(cancellationToken);
        return [.. Enum.GetValues<SsoKind>().Select(kind => stored.SingleOrDefault(p => p.Kind == kind) is { } p
            ? View(p)
            : new SsoProviderView(kind, false, string.Empty, false, null, null, CallbackPath(kind), null))];
    }

    public async Task<Result<SsoProviderUpdate>> UpdateAsync(SsoKind kind, SsoProviderDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var now = clock.GetUtcNow();
        var provider = await db.SsoProviders.SingleOrDefaultAsync(p => p.Kind == kind, cancellationToken);
        if (provider is null)
        {
            provider = SsoProvider.Create(kind, now);
            db.SsoProviders.Add(provider);
        }

        var secret = string.IsNullOrEmpty(definition.ClientSecret) ? null : definition.ClientSecret.Trim();
        if (secret is { Length: > 512 } || (secret?.Any(char.IsControl) ?? false))
        {
            return SsoErrors.ClientSecretInvalid;
        }

        var (clientIdBefore, authorityBefore) = (provider.ClientId, provider.Authority);
        var changed = provider.Change(definition.Enabled, definition.ClientId, secret is null ? null : secrets.Protect(secret), definition.Authority,
            definition.DisplayName, now);
        if (!changed.IsSuccess)
        {
            return changed.Error!;
        }

        var stale = provider.NamesOtherAccounts(clientIdBefore, authorityBefore);
        audit.Record(AuditActions.Sso.ProviderUpdated, metadata: new
        {
            kind = Slug(kind),
            provider.IsEnabled,
            provider.ClientId,
            provider.Authority,
            provider.DisplayName,
            secretChanged = secret is not null,
            linksRemoved = stale,
        });
        await db.SaveChangesAsync(cancellationToken);
        return new SsoProviderUpdate(View(provider), stale);
    }

    /// <summary>The providers turned on whose secret can still be read, for the sign-in page (anyone may ask).</summary>
    public async Task<IReadOnlyList<SsoOption>> EnabledAsync(CancellationToken cancellationToken) =>
        [.. (await db.SsoProviders.AsNoTracking().Where(p => p.IsEnabled).ToListAsync(cancellationToken))
            .Where(p => p.ClientSecretProtected is { } secret && Readable(secret))
            .OrderBy(p => p.Kind)
            .Select(p => new SsoOption(p.Kind, NameOf(p)))];

    private bool Readable(byte[] protectedSecret)
    {
        try
        {
            secrets.Unprotect(protectedSecret);
            return true;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false; // the key ring that encrypted it is gone: no button that cannot work
        }
    }

    /// <summary>What to talk to the provider with; null when it is not turned on (or its secret can no longer be read).</summary>
    public async Task<SsoSettings?> SettingsAsync(SsoKind kind, CancellationToken cancellationToken)
    {
        var provider = await db.SsoProviders.AsNoTracking().SingleOrDefaultAsync(p => p.Kind == kind && p.IsEnabled, cancellationToken);
        if (provider?.ClientSecretProtected is not { } protectedSecret)
        {
            return null;
        }

        try
        {
            return new SsoSettings(kind, provider.ClientId, secrets.Unprotect(protectedSecret), provider.Authority);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null; // the key ring that encrypted it is gone: the secret must be set again
        }
    }

    public static string NameOf(SsoProvider provider) => provider.Kind switch
    {
        SsoKind.Discord => "Discord",
        SsoKind.Google => "Google",
        _ => provider.DisplayName ?? "OpenID Connect",
    };

    private static SsoProviderView View(SsoProvider p) => new(p.Kind, p.IsEnabled, p.ClientId, p.ClientSecretProtected is not null, p.Authority,
        p.DisplayName, CallbackPath(p.Kind), p.UpdatedAt);
}
