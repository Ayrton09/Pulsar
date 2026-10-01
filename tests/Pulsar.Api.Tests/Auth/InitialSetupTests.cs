using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Pulsar.Api.Auth;

namespace Pulsar.Api.Tests.Auth;

public sealed class InitialSetupTests
{
    [Fact]
    public void RandomTokenIsValidUntilSetupCompletes()
    {
        var setup = Create(token: null);

        var token = setup.IssueToken();

        Assert.True(token.Length >= 32);
        Assert.True(setup.IsValid(token));
        Assert.False(setup.IsValid(token + "x"));
        setup.Complete();
        Assert.False(setup.IsValid(token));
    }

    [Fact]
    public void ConfiguredTokenIsUsedInsteadOfARandomOne()
    {
        const string configured = "configured-setup-token-0123456789-abcdef";
        var setup = Create(configured);

        Assert.Equal(configured, setup.IssueToken());
        Assert.True(setup.IsValid(configured));
    }

    [Fact]
    public void ShortConfiguredTokensAreRejectedAtStartup()
    {
        var setup = Create("too-short");

        var ex = Assert.Throws<PanelStartupException>(() => setup.IssueToken());

        Assert.Contains(InitialSetup.ConfiguredTokenKey, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("too-short", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingIsValidBeforeATokenIsIssued()
    {
        Assert.False(Create(token: null).IsValid(string.Empty));
    }

    private static InitialSetup Create(string? token) => new(
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [InitialSetup.ConfiguredTokenKey] = token })
            .Build(),
        NullLogger<InitialSetup>.Instance);
}
