using Pulsar.Domain.Common;

namespace Pulsar.Domain.Access;

/// <summary>Where users may sign in from besides their password (Pulsar 1.0.18).</summary>
public enum SsoKind
{
    /// <summary>Discord's OAuth 2: the account's id from <c>/users/@me</c>.</summary>
    Discord,

    /// <summary>Google's OpenID Connect.</summary>
    Google,

    /// <summary>Any OpenID Connect provider the administrator names (Microsoft Entra, Keycloak, Authentik…).</summary>
    Oidc,
}

public static class SsoErrors
{
    public static DomainError NotFound { get; } = new("sso.not_found", "That sign-in provider is not set up.");

    public static DomainError ClientIdInvalid { get; } = new("sso.client_id_invalid", "A provider turned on needs its client id: one line of up to 256 characters.");

    public static DomainError ClientSecretRequired { get; } = new("sso.client_secret_required", "Turning a provider on needs its client secret.");

    public static DomainError ClientSecretInvalid { get; } = new("sso.client_secret_invalid", "The client secret is one line of up to 512 characters.");

    public static DomainError AuthorityInvalid { get; } =
        new("sso.authority_invalid", "The OpenID Connect issuer is an https address without a query, of up to 255 characters.");

    public static DomainError NameInvalid { get; } = new("sso.name_invalid", "The button's name is one line of up to 32 characters.");
}

/// <summary>
/// One sign-in provider's settings (one row per <see cref="SsoKind"/>): the application the administrator registered
/// with it. Its client secret is encrypted by the application before it reaches this row and never shown again.
/// </summary>
public sealed class SsoProvider
{
    public const int MaxClientIdLength = 256;
    public const int MaxAuthorityLength = 255;
    public const int MaxNameLength = 32;

    private SsoProvider()
    {
        // For EF Core materialization.
        ClientId = null!;
    }

    public SsoKind Kind { get; private set; }

    public bool IsEnabled { get; private set; }

    public string ClientId { get; private set; }

    public byte[]? ClientSecretProtected { get; private set; }

    /// <summary>The issuer of an <see cref="SsoKind.Oidc"/> provider (its discovery document is below it); null for the others.</summary>
    public string? Authority { get; private set; }

    /// <summary>What the sign-in button of an <see cref="SsoKind.Oidc"/> provider says; null for the others.</summary>
    public string? DisplayName { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static SsoProvider Create(SsoKind kind, DateTimeOffset now) => new() { Kind = kind, ClientId = string.Empty, UpdatedAt = now };

    /// <param name="clientSecretProtected">A new secret, encrypted; null keeps the current one.</param>
    public Result Change(bool enabled, string? clientId, byte[]? clientSecretProtected, string? authority, string? displayName, DateTimeOffset now)
    {
        var id = clientId?.Trim() ?? string.Empty;
        if (id.Length > MaxClientIdLength || id.Any(char.IsControl) || (enabled && id.Length == 0))
        {
            return SsoErrors.ClientIdInvalid;
        }

        string? issuer = null;
        string? name = null;
        if (Kind == SsoKind.Oidc)
        {
            issuer = string.IsNullOrWhiteSpace(authority) ? null : authority.Trim();
            if ((enabled || issuer is not null) && !IsAuthority(issuer))
            {
                return SsoErrors.AuthorityInvalid;
            }

            name = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
            if (name is { Length: > MaxNameLength } || (name?.Any(char.IsControl) ?? false))
            {
                return SsoErrors.NameInvalid;
            }
        }

        var secret = clientSecretProtected ?? ClientSecretProtected;
        if (enabled && secret is null)
        {
            return SsoErrors.ClientSecretRequired;
        }

        // The stored secret belongs to one application at one issuer: a new client id, or a new issuer, without a new secret
        // forgets it (the panel would send it to whatever the new issuer names as its token endpoint).
        if (clientSecretProtected is null
            && (!string.Equals(id, ClientId, StringComparison.Ordinal) || !string.Equals(issuer, Authority, StringComparison.Ordinal)))
        {
            if (enabled)
            {
                return SsoErrors.ClientSecretRequired;
            }

            secret = null;
        }

        IsEnabled = enabled;
        ClientId = id;
        ClientSecretProtected = secret;
        Authority = issuer;
        DisplayName = name;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>
    /// Whether the accounts linked to this provider would now be another application's, or another issuer's: an OpenID
    /// Connect subject only means something to its issuer (Discord's and Google's ids are theirs whatever the client).
    /// </summary>
    public bool NamesOtherAccounts(string? clientIdBefore, string? authorityBefore) =>
        Kind == SsoKind.Oidc
        && (!string.Equals(ClientId, clientIdBefore ?? string.Empty, StringComparison.Ordinal)
            || !string.Equals(Authority, authorityBefore, StringComparison.Ordinal));

    /// <summary>An https address with a host, no user, query or fragment (the issuer's discovery document is below it).</summary>
    public static bool IsAuthority(string? value) =>
        value is { Length: > 0 and <= MaxAuthorityLength }
        && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Length > 0
        && uri.UserInfo.Length == 0
        && uri.Query.Length == 0
        && uri.Fragment.Length == 0;
}
