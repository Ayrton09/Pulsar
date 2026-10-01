using System.Reflection;
using Pulsar.Domain.Access;

namespace Pulsar.Domain.Tests.Access;

public sealed class PermissionCatalogTests
{
    [Fact]
    public void EveryPermissionConstantIsInTheCatalogAndViceVersa()
    {
        var constants = typeof(Permissions)
            .GetNestedTypes()
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(constants.Order(StringComparer.Ordinal), PermissionCatalog.AllKeys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void KeysAreLowercaseDottedIdentifiers()
    {
        Assert.All(PermissionCatalog.AllKeys, key => Assert.Matches("^[a-z]+(\\.[a-z_]+)+$", key));
    }

    [Theory]
    [InlineData(Permissions.Server.Start, PermissionScope.Server, true)]
    [InlineData(Permissions.Server.Start, PermissionScope.Node, true)]
    [InlineData(Permissions.Server.Start, PermissionScope.Global, true)]
    [InlineData(Permissions.Server.Create, PermissionScope.Server, false)]
    [InlineData(Permissions.Server.Delete, PermissionScope.Server, false)]
    [InlineData(Permissions.Server.Create, PermissionScope.Node, true)]
    [InlineData(Permissions.Node.Manage, PermissionScope.Server, false)]
    [InlineData(Permissions.Node.Manage, PermissionScope.Node, true)]
    [InlineData(Permissions.User.Manage, PermissionScope.Node, false)]
    [InlineData(Permissions.Role.Manage, PermissionScope.Global, true)]
    [InlineData("server.unknown", PermissionScope.Global, false)]
    public void GrantableScopesFollowTheDesign(string key, PermissionScope scope, bool expected)
    {
        Assert.Equal(expected, PermissionCatalog.IsGrantableIn(key, scope));
    }

    [Fact]
    public void EveryPermissionIsGrantableGlobally()
    {
        Assert.All(PermissionCatalog.All, d => Assert.Contains(PermissionScope.Global, d.GrantableScopes));
    }
}
