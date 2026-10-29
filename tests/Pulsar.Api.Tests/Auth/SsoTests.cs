using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Access;
using Pulsar.Api.Auth;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Api.Tests.Sftp;
using Pulsar.Application.Access;
using Pulsar.Domain.Access;
using Pulsar.Domain.Audit;
using Pulsar.Infrastructure.Identity;
using Pulsar.Infrastructure.Persistence;

namespace Pulsar.Api.Tests.Auth;

/// <summary>
/// An OpenID Connect provider (https://idp.test) and Discord, as the panel's HTTP client meets them: discovery, keys,
/// a token endpoint that checks the client, the redirect and the PKCE verifier, and signs its ID tokens with its own key.
/// </summary>
public sealed class FakeIdentityProviders : HttpMessageHandler
{
    public const string Issuer = "https://idp.test";
    public const string ClientId = "pulsar-client";
    public const string ClientSecret = "pulsar-secret";

    private readonly RSA _key = RSA.Create(2048);
    private readonly ConcurrentDictionary<string, Issued> _codes = new(StringComparer.Ordinal);

    /// <summary>What the next ID token says differently (another audience, issuer, nonce…), or how it is signed.</summary>
    public Func<Dictionary<string, object>, Dictionary<string, object>> Tamper { get; set; } = claims => claims;

    public bool WrongSignature { get; set; }

    /// <summary>Runs once, while the panel waits for the next code to be redeemed.</summary>
    public Func<Task>? WhileRedeeming { get; set; }

    /// <summary>The user signs in at the provider: a code for the panel's callback, bound to what the panel asked.</summary>
    public string Approve(Uri authorize, string subject)
    {
        var query = QueryHelpers.ParseQuery(authorize.Query);
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        string Value(string name) => query.TryGetValue(name, out var value) ? value.ToString() : string.Empty;
        _codes[code] = new Issued(subject, Value("nonce"), Value("code_challenge"), Value("redirect_uri"), Value("client_id"));
        return code;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        if (url == $"{Issuer}/.well-known/openid-configuration")
        {
            return Json(new
            {
                issuer = Issuer,
                authorization_endpoint = $"{Issuer}/authorize",
                token_endpoint = $"{Issuer}/token",
                jwks_uri = $"{Issuer}/jwks",
            });
        }

        if (url == $"{Issuer}/jwks")
        {
            var key = _key.ExportParameters(false);
            return Json(new
            {
                keys = new[]
                {
                    new { kty = "RSA", kid = "k1", use = "sig", alg = "RS256", n = WebEncoders.Base64UrlEncode(key.Modulus!), e = WebEncoders.Base64UrlEncode(key.Exponent!) },
                },
            });
        }

        if (url is $"{Issuer}/token" or "https://discord.com/api/oauth2/token")
        {
            if (WhileRedeeming is { } meanwhile)
            {
                WhileRedeeming = null;
                await meanwhile();
            }

            var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
            // Discord takes the secret in the form; this OpenID Connect provider (no auth methods listed) as HTTP Basic.
            var secret = url.StartsWith("https://discord.com", StringComparison.Ordinal)
                ? form["client_secret"].ToString()
                : request.Headers.Authorization is { Scheme: "Basic", Parameter: { } basic }
                    ? Uri.UnescapeDataString(Encoding.UTF8.GetString(Convert.FromBase64String(basic)).Split(':')[1])
                    : null;
            if (!_codes.TryRemove(form["code"].ToString(), out var issued) || secret != ClientSecret || form["client_id"] != issued.ClientId
                || form["redirect_uri"] != issued.RedirectUri
                || WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"].ToString()))) != issued.Challenge)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":"invalid_grant"}""") };
            }

            if (url.StartsWith("https://discord.com", StringComparison.Ordinal))
            {
                return Json(new { access_token = $"discord-{issued.Subject}", token_type = "Bearer" });
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var claims = Tamper(new Dictionary<string, object>
            {
                ["iss"] = Issuer,
                ["aud"] = ClientId,
                ["sub"] = issued.Subject,
                ["email"] = $"{issued.Subject}@example.com",
                ["iat"] = now,
                ["exp"] = now + 300,
                ["nonce"] = issued.Nonce,
            });
            return Json(new { access_token = "at", id_token = Sign(claims) });
        }

        if (url == "https://discord.com/api/users/@me")
        {
            var subject = request.Headers.Authorization?.Parameter?["discord-".Length..];
            return Json(new { id = subject, username = "discord-user", global_name = "Discord User" });
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _key.Dispose();
        }

        base.Dispose(disposing);
    }

    private string Sign(Dictionary<string, object> claims)
    {
        var header = WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", kid = "k1", typ = "JWT" }));
        var payload = WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(claims));
        using var other = RSA.Create(2048);
        var signature = (WrongSignature ? other : _key).SignData(Encoding.ASCII.GetBytes($"{header}.{payload}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{payload}.{WebEncoders.Base64UrlEncode(signature)}";
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private sealed record Issued(string Subject, string Nonce, string Challenge, string RedirectUri, string ClientId);
}

public sealed class SsoApiFactory : PulsarApiFactory
{
    public FakeIdentityProviders Providers { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services => services.AddHttpClient("Pulsar.Sso").ConfigurePrimaryHttpMessageHandler(() => Providers));
    }
}

/// <summary>Signing in with a provider's account (1.0.18): linked with the password first, the panel's second factor still asked.</summary>
public sealed class SsoTests(SsoApiFactory factory) : IClassFixture<SsoApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task ALinkedAccountSignsInAndOnlyItsOwnersAccount()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await ConfigureOidcAsync(scenario.Admin);
        await scenario.CreateUserAsync("sso-owner");

        // Not linked yet: no way in, and the provider's id is never shown.
        using (var anonymous = await PanelClient.CreateAsync(factory))
        {
            Assert.Equal("/login?sso_error=not_linked", await SignInAsync(anonymous, "oidc", "subject-1"));
        }

        using (var owner = await scenario.SignInAsync("sso-owner"))
        {
            // Linking takes the password: a session alone does not link an account.
            using (var wrong = await owner.PostAsync("/api/v1/account/sso/oidc/link", new SsoLinkRequest("not the password", null)))
            {
                await SetupTests.AssertProblemAsync(wrong, HttpStatusCode.BadRequest, "auth.wrong_current_password");
            }

            Assert.Equal("/account?sso=linked", await LinkAsync(owner, "oidc", "subject-1"));
            var links = await GetJsonAsync<List<SsoLinkView>>(owner, "/api/v1/account/sso");
            Assert.Equal([new SsoLinkView(SsoKind.Oidc, "subject-1@example.com")], links);
        }

        using var browser = await PanelClient.CreateAsync(factory);
        Assert.Equal("/", await SignInAsync(browser, "oidc", "subject-1"));
        var me = await GetJsonAsync<CurrentUserResponse>(browser, "/api/v1/auth/me");
        Assert.Equal("sso-owner", me.UserName);

        await using var scope = factory.Services.CreateAsyncScope();
        var audit = await scope.ServiceProvider.GetRequiredService<PulsarDbContext>().AuditLog.AsNoTracking()
            .Where(e => e.Action == AuditActions.Auth.SsoLinked || e.Action == AuditActions.Auth.Login).ToListAsync(Ct);
        Assert.Contains(audit, e => e.Action == AuditActions.Auth.SsoLinked && e.Outcome == AuditOutcome.Success && e.ActorDisplay == "sso-owner");
        Assert.Contains(audit, e => e.Action == AuditActions.Auth.Login && e.ActorDisplay == "sso-owner" && e.Metadata!.Contains("oidc", StringComparison.Ordinal));
    }

    [DatabaseFact]
    public async Task ThePanelsSecondFactorIsStillAsked()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await ConfigureOidcAsync(scenario.Admin);
        await scenario.CreateUserAsync("sso-two-factor");
        using (var owner = await scenario.SignInAsync("sso-two-factor"))
        {
            Assert.Equal("/account?sso=linked", await LinkAsync(owner, "oidc", "subject-2fa"));
        }

        // Turned on after linking (its code is the step before now): the provider still does not stand in for it.
        var (key, _) = await SftpTesting.EnableTwoFactorAsync(scenario, "sso-two-factor");

        using var browser = await PanelClient.CreateAsync(factory);
        Assert.Equal("/login?sso=two_factor", await SignInAsync(browser, "oidc", "subject-2fa"));
        using (var notYet = await browser.GetAsync("/api/v1/auth/me"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, notYet.StatusCode);
        }

        await browser.GetSessionAsync();
        using var code = await browser.PostAsync("/api/v1/auth/login/two-factor",
            new TwoFactorLoginRequest(Totp.Compute(key, DateTimeOffset.UtcNow), null));
        await AccessScenario.EnsureSuccessAsync(code);
        Assert.Equal("sso-two-factor", (await GetJsonAsync<CurrentUserResponse>(browser, "/api/v1/auth/me")).UserName);
    }

    [DatabaseFact]
    public async Task AnIdTokenThatDoesNotHoldUpSignsNobodyIn()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await ConfigureOidcAsync(scenario.Admin);
        await scenario.CreateUserAsync("sso-checked");
        using (var owner = await scenario.SignInAsync("sso-checked"))
        {
            Assert.Equal("/account?sso=linked", await LinkAsync(owner, "oidc", "subject-checked"));
        }

        Func<Dictionary<string, object>, Dictionary<string, object>>[] tampered =
        [
            claims => new(claims) { ["aud"] = "another-client" },
            claims => new(claims) { ["iss"] = "https://evil.test" },
            claims => new(claims) { ["nonce"] = "replayed" },
            claims => new(claims) { ["exp"] = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds() },
        ];
        try
        {
            foreach (var tamper in tampered)
            {
                factory.Providers.Tamper = tamper;
                using var browser = await PanelClient.CreateAsync(factory);
                Assert.Equal("/login?sso_error=token_invalid", await SignInAsync(browser, "oidc", "subject-checked"));
            }

            factory.Providers.Tamper = claims => claims;
            factory.Providers.WrongSignature = true;
            using (var browser = await PanelClient.CreateAsync(factory))
            {
                Assert.Equal("/login?sso_error=token_invalid", await SignInAsync(browser, "oidc", "subject-checked"));
            }
        }
        finally
        {
            factory.Providers.Tamper = claims => claims;
            factory.Providers.WrongSignature = false;
        }

        // A callback without the flow's cookie (another browser's code), or with another state: nothing.
        using var stranger = await PanelClient.CreateAsync(factory);
        using var started = await PanelClient.CreateAsync(factory);
        var authorize = await StartAsync(started, "/api/v1/auth/sso/oidc/login");
        var code = factory.Providers.Approve(authorize, "subject-checked");
        var state = QueryHelpers.ParseQuery(authorize.Query)["state"];
        Assert.Equal("/login?sso_error=expired", await CallbackAsync(stranger, "oidc", code, state!));
        Assert.Equal("/login?sso_error=expired", await CallbackAsync(started, "oidc", code, "another-state"));
        // The flow is single use: the cookie went with the first callback.
        Assert.Equal("/login?sso_error=expired", await CallbackAsync(started, "oidc", code, state!));
    }

    [DatabaseFact]
    public async Task DiscordAccountsLinkAndOneAccountBelongsToOneUser()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        using (var put = await scenario.Admin.PutAsync("/api/v1/sso-providers/discord",
                   new SsoProviderRequest(true, FakeIdentityProviders.ClientId, FakeIdentityProviders.ClientSecret, null, null)))
        {
            await AccessScenario.EnsureSuccessAsync(put);
        }

        await scenario.CreateUserAsync("discord-first");
        await scenario.CreateUserAsync("discord-second");
        using (var first = await scenario.SignInAsync("discord-first"))
        {
            Assert.Equal("/account?sso=linked", await LinkAsync(first, "discord", "123456789"));
            Assert.Equal([new SsoLinkView(SsoKind.Discord, "Discord User")], await GetJsonAsync<List<SsoLinkView>>(first, "/api/v1/account/sso"));
        }

        using (var second = await scenario.SignInAsync("discord-second"))
        {
            Assert.Equal("/account?sso_error=linked_elsewhere", await LinkAsync(second, "discord", "123456789"));
            Assert.Empty(await GetJsonAsync<List<SsoLinkView>>(second, "/api/v1/account/sso"));
        }

        using var browser = await PanelClient.CreateAsync(factory);
        Assert.Equal("/", await SignInAsync(browser, "discord", "123456789"));
        Assert.Equal("discord-first", (await GetJsonAsync<CurrentUserResponse>(browser, "/api/v1/auth/me")).UserName);

        // Unlinked by its owner: no way in with it any more, and the session that unlinked stays.
        await browser.GetSessionAsync(); // the page loads the session, and its antiforgery token, after the redirect
        using (var unlink = await browser.DeleteAsync("/api/v1/account/sso/discord"))
        {
            Assert.Equal(HttpStatusCode.NoContent, unlink.StatusCode);
        }

        Assert.Empty(await GetJsonAsync<List<SsoLinkView>>(browser, "/api/v1/account/sso"));
        using var again = await PanelClient.CreateAsync(factory);
        Assert.Equal("/login?sso_error=not_linked", await SignInAsync(again, "discord", "123456789"));
    }

    [DatabaseFact]
    public async Task ProvidersAreSettingsWhoseSecretNeverComesBack()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await ConfigureOidcAsync(scenario.Admin);
        var listed = await GetJsonAsync<List<SsoProviderView>>(scenario.Admin, "/api/v1/sso-providers");
        var oidc = listed.Single(p => p.Kind == SsoKind.Oidc);
        Assert.Equal((true, FakeIdentityProviders.ClientId, true, "/api/v1/auth/sso/oidc/callback"), (oidc.Enabled, oidc.ClientId, oidc.HasSecret, oidc.CallbackPath));
        using (var raw = await scenario.Admin.GetAsync("/api/v1/sso-providers"))
        {
            Assert.DoesNotContain(FakeIdentityProviders.ClientSecret, await raw.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        // The sign-in page lists what is on, to anyone.
        using var anonymous = await PanelClient.CreateAsync(factory);
        Assert.Contains(new SsoOption(SsoKind.Oidc, "Company"), await GetJsonAsync<List<SsoOption>>(anonymous, "/api/v1/auth/sso"));

        // Settings are settings.manage's; an http issuer is refused; a new client id needs its secret.
        using var user = await scenario.SignedInWithAsync("sso-not-admin", Permissions.User.View);
        using (var forbidden = await user.PutAsync("/api/v1/sso-providers/oidc", new SsoProviderRequest(false, null, null, null, null)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        using (var http = await scenario.Admin.PutAsync("/api/v1/sso-providers/oidc",
                   new SsoProviderRequest(true, FakeIdentityProviders.ClientId, null, "http://idp.test", "Company")))
        {
            await SetupTests.AssertProblemAsync(http, HttpStatusCode.BadRequest, SsoErrors.AuthorityInvalid.Code);
        }

        using (var newClient = await scenario.Admin.PutAsync("/api/v1/sso-providers/oidc",
                   new SsoProviderRequest(true, "another-client", null, FakeIdentityProviders.Issuer, "Company")))
        {
            await SetupTests.AssertProblemAsync(newClient, HttpStatusCode.BadRequest, SsoErrors.ClientSecretRequired.Code);
        }

        // An administrator removes a user's links (a provider account taken over).
        await scenario.CreateUserAsync("sso-removed");
        using (var owner = await scenario.SignInAsync("sso-removed"))
        {
            Assert.Equal("/account?sso=linked", await LinkAsync(owner, "oidc", "subject-removed"));
        }

        var id = (await GetJsonAsync<List<UserResponse>>(scenario.Admin, "/api/v1/users")).Single(u => u.UserName == "sso-removed").Id;
        using (var removed = await scenario.Admin.DeleteAsync($"/api/v1/users/{id}/sso-links"))
        {
            await AccessScenario.EnsureSuccessAsync(removed);
            Assert.Empty((await removed.Content.ReadFromJsonAsync<UserResponse>(AccessScenario.Json, Ct))!.SsoLinks!);
        }

        using var browser = await PanelClient.CreateAsync(factory);
        Assert.Equal("/login?sso_error=not_linked", await SignInAsync(browser, "oidc", "subject-removed"));
    }

    [DatabaseFact]
    public async Task AnotherIssuerTakesTheLinksToTheOldOneAway()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await ConfigureOidcAsync(scenario.Admin);
        var created = await scenario.CreateUserAsync("sso-issuer");
        using var owner = await scenario.SignInAsync("sso-issuer");
        Assert.Equal("/account?sso=linked", await LinkAsync(owner, "oidc", "subject-issuer"));
        var stamp = await StampAsync(created.Id);

        // A subject means something to its issuer only: pointed elsewhere (with that issuer's secret), the links go.
        using (var elsewhere = await scenario.Admin.PutAsync("/api/v1/sso-providers/oidc",
                   new SsoProviderRequest(true, FakeIdentityProviders.ClientId, null, "https://other.test", "Company")))
        {
            await SetupTests.AssertProblemAsync(elsewhere, HttpStatusCode.BadRequest, SsoErrors.ClientSecretRequired.Code);
        }

        using (var moved = await scenario.Admin.PutAsync("/api/v1/sso-providers/oidc",
                   new SsoProviderRequest(true, FakeIdentityProviders.ClientId, "other-secret", "https://other.test", "Company")))
        {
            await AccessScenario.EnsureSuccessAsync(moved);
        }

        await ConfigureOidcAsync(scenario.Admin);
        using var signedInAgain = await scenario.SignInAsync("sso-issuer");
        Assert.Empty(await GetJsonAsync<List<SsoLinkView>>(signedInAgain, "/api/v1/account/sso"));
        using var browser = await PanelClient.CreateAsync(factory);
        Assert.Equal("/login?sso_error=not_linked", await SignInAsync(browser, "oidc", "subject-issuer"));
        // The owner's sessions end with the links (at their next check of the security stamp, within a minute).
        Assert.NotEqual(stamp, await StampAsync(created.Id));
    }

    [DatabaseFact]
    public async Task ALinkRedeemedWhileTheIssuerChangesIsNotSaved()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await ConfigureOidcAsync(scenario.Admin);
        var created = await scenario.CreateUserAsync("sso-race");
        using var owner = await scenario.SignInAsync("sso-race");

        // The code is the old issuer's: by the time it is redeemed, the provider is another issuer, whose subjects these are not.
        factory.Providers.WhileRedeeming = async () =>
        {
            using var moved = await scenario.Admin.PutAsync("/api/v1/sso-providers/oidc",
                new SsoProviderRequest(true, FakeIdentityProviders.ClientId, "other-secret", "https://other.test", "Company"));
            await AccessScenario.EnsureSuccessAsync(moved);
        };
        Assert.Equal("/account?sso_error=expired", await LinkAsync(owner, "oidc", "subject-race"));

        await using var scope = factory.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<PulsarDbContext>().UserLogins
            .AnyAsync(l => l.UserId == new Pulsar.Domain.Common.UserId(created.Id), Ct));
        await ConfigureOidcAsync(scenario.Admin);
    }

    private async Task<string?> StampAsync(Guid userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PulsarDbContext>().Users.AsNoTracking()
            .Where(u => u.Id == new Pulsar.Domain.Common.UserId(userId)).Select(u => u.SecurityStamp).SingleAsync(Ct);
    }

    private static async Task ConfigureOidcAsync(PanelClient admin)
    {
        using var put = await admin.PutAsync("/api/v1/sso-providers/oidc",
            new SsoProviderRequest(true, FakeIdentityProviders.ClientId, FakeIdentityProviders.ClientSecret, FakeIdentityProviders.Issuer, "Company"));
        await AccessScenario.EnsureSuccessAsync(put);
    }

    /// <summary>The sign-in page's button, the provider, the callback: where the panel sends the browser in the end.</summary>
    private async Task<string> SignInAsync(PanelClient browser, string provider, string subject)
    {
        var authorize = await StartAsync(browser, $"/api/v1/auth/sso/{provider}/login");
        return await CallbackAsync(browser, provider, factory.Providers.Approve(authorize, subject), QueryHelpers.ParseQuery(authorize.Query)["state"]!);
    }

    private async Task<string> LinkAsync(PanelClient owner, string provider, string subject, string? code = null)
    {
        var authorize = await StartAsync(owner, $"/api/v1/account/sso/{provider}/link", new SsoLinkRequest(AccessScenario.Password, code));
        return await CallbackAsync(owner, provider, factory.Providers.Approve(authorize, subject), QueryHelpers.ParseQuery(authorize.Query)["state"]!);
    }

    private static async Task<Uri> StartAsync(PanelClient browser, string path, object? body = null)
    {
        using var response = body is null ? await browser.PostAsync(path) : await browser.PostAsync(path, body);
        await AccessScenario.EnsureSuccessAsync(response);
        var url = new Uri((await response.Content.ReadFromJsonAsync<SsoStartResponse>(AccessScenario.Json, Ct))!.Url);
        var query = QueryHelpers.ParseQuery(url.Query);
        Assert.Equal(("code", "S256"), (query["response_type"].ToString(), query["code_challenge_method"].ToString()));
        Assert.EndsWith($"/callback", query["redirect_uri"].ToString(), StringComparison.Ordinal);
        return url;
    }

    private static async Task<string> CallbackAsync(PanelClient browser, string provider, string code, string state)
    {
        using var response = await browser.GetAsync($"/api/v1/auth/sso/{provider}/callback?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return response.Headers.Location!.OriginalString;
    }

    private static async Task<T> GetJsonAsync<T>(PanelClient client, string path)
    {
        using var response = await client.GetAsync(path);
        await AccessScenario.EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<T>(AccessScenario.Json, Ct))!;
    }
}
