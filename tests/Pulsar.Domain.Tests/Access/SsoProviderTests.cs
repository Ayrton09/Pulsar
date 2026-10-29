using Pulsar.Domain.Access;

namespace Pulsar.Domain.Tests.Access;

public sealed class SsoProviderTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AProviderIsTurnedOnOnlyWithItsClientIdAndSecret()
    {
        var discord = SsoProvider.Create(SsoKind.Discord, s_now);
        Assert.Equal(SsoErrors.ClientIdInvalid, discord.Change(true, " ", [1], null, null, s_now).Error);
        Assert.Equal(SsoErrors.ClientSecretRequired, discord.Change(true, "client", null, null, null, s_now).Error);
        Assert.Equal(SsoErrors.ClientIdInvalid, discord.Change(false, "line\nbreak", null, null, null, s_now).Error);

        Assert.True(discord.Change(true, " client ", [1], "https://ignored.example", "Ignored", s_now).IsSuccess);
        Assert.Equal(("client", (string?)null, (string?)null), (discord.ClientId, discord.Authority, discord.DisplayName));

        // The secret stays while the client stays; another client id needs its own secret.
        Assert.True(discord.Change(true, "client", null, null, null, s_now).IsSuccess);
        Assert.Equal([1], discord.ClientSecretProtected);
        Assert.Equal(SsoErrors.ClientSecretRequired, discord.Change(true, "another", null, null, null, s_now).Error);
        Assert.True(discord.Change(false, "another", null, null, null, s_now).IsSuccess);
        Assert.Null(discord.ClientSecretProtected);
    }

    [Theory]
    [InlineData("http://idp.example")]
    [InlineData("https://idp.example/?tenant=1")]
    [InlineData("https://user@idp.example")]
    [InlineData("idp.example")]
    public void AnOpenIdConnectIssuerIsAnHttpsAddressWithoutQuery(string authority)
    {
        var oidc = SsoProvider.Create(SsoKind.Oidc, s_now);
        Assert.Equal(SsoErrors.AuthorityInvalid, oidc.Change(true, "client", [1], authority, "Company", s_now).Error);
        Assert.True(oidc.Change(true, "client", [1], "https://idp.example/realms/games", "Company", s_now).IsSuccess);
        Assert.Equal(SsoErrors.NameInvalid, oidc.Change(true, "client", null, "https://idp.example", new string('x', 33), s_now).Error);
    }
}
