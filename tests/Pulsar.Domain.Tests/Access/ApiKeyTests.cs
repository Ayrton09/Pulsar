using Pulsar.Domain.Access;
using Pulsar.Domain.Common;

namespace Pulsar.Domain.Tests.Access;

public sealed class ApiKeyTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly UserId s_user = UserId.New();

    [Fact]
    public void AKeyIsShownOnceAndStoredAsItsHash()
    {
        var (key, plain) = ApiKey.Create(s_user, "  Discord bot ", null, null, s_now).Value;

        Assert.StartsWith("pulsar_", plain, StringComparison.Ordinal);
        Assert.True(ApiKey.LooksLikeKey(plain));
        Assert.Equal("Discord bot", key.Name);
        Assert.Equal(plain[..ApiKey.VisiblePrefixLength], key.Prefix);
        Assert.Equal(ApiKey.Hash(plain), key.TokenHash);
        Assert.Null(key.Permissions);
        Assert.True(key.IsUsable(s_now));
        Assert.NotEqual(plain, ApiKey.Create(s_user, "Other", null, null, s_now).Value.PlainValue);
    }

    [Fact]
    public void AKeyStopsAtItsExpiryOrWhenRevoked()
    {
        var expiring = ApiKey.Create(s_user, "CI", null, s_now.AddDays(30), s_now).Value.Key;
        Assert.True(expiring.IsUsable(s_now.AddDays(29)));
        Assert.False(expiring.IsUsable(s_now.AddDays(30)));

        var revoked = ApiKey.Create(s_user, "Old", null, null, s_now).Value.Key;
        revoked.Revoke(s_now.AddHours(1));
        revoked.Revoke(s_now.AddHours(2));
        Assert.Equal(s_now.AddHours(1), revoked.RevokedAt);
        Assert.False(revoked.IsUsable(s_now.AddHours(1)));
    }

    [Theory]
    [InlineData("", "api_key.name_invalid")]
    [InlineData("two\nlines", "api_key.name_invalid")]
    [InlineData("ok", "api_key.permissions_invalid", "server.fly")]
    [InlineData("ok", "api_key.permissions_invalid", "")]
    public void InvalidKeysAreRefused(string name, string code, params string[] permissions)
    {
        var limit = permissions.Length == 1 && permissions[0] == "" ? [] : permissions;
        Assert.Equal(code, ApiKey.Create(s_user, name, limit, null, s_now).Error!.Code);
    }

    [Fact]
    public void AKeyCannotExpireInThePast() =>
        Assert.Equal("api_key.expiry_invalid", ApiKey.Create(s_user, "x", null, s_now, s_now).Error!.Code);

    [Fact]
    public void NarrowedPermissionsKeepOnlyTheAllowedOnesWhereTheUserHasThem()
    {
        var node = NodeId.New();
        var server = ServerId.New();
        var permissions = new EffectivePermissions.Builder()
            .Add(Role.Create("Global", null, PermissionScope.Global, [Permissions.Audit.View, Permissions.Server.View], s_now).Value, AccessTarget.Global)
            .Add(Role.Create("Node", null, PermissionScope.Node, [Permissions.Server.Start, Permissions.Server.Stop], s_now).Value, AccessTarget.ForNode(node))
            .Add(Role.Create("Server", null, PermissionScope.Server, [Permissions.Server.Kill], s_now).Value, AccessTarget.ForServer(server, node))
            .Build();

        var narrowed = permissions.Restrict(new HashSet<string> { Permissions.Server.View, Permissions.Server.Start, Permissions.User.Manage });

        var onServer = AccessTarget.ForServer(server, node);
        Assert.True(narrowed.Has(Permissions.Server.View, onServer));
        Assert.True(narrowed.Has(Permissions.Server.Start, onServer));
        Assert.False(narrowed.Has(Permissions.Server.Stop, onServer));
        Assert.False(narrowed.Has(Permissions.Server.Kill, onServer));
        Assert.False(narrowed.HasGlobal(Permissions.Audit.View));
        Assert.False(narrowed.HasAnywhere(Permissions.User.Manage)); // allowed, but the user does not have it
        Assert.Empty(narrowed.Servers);
    }
}
