using Pulsar.Domain.Access;
using Pulsar.Domain.Common;

namespace Pulsar.Domain.Tests.Access;

public sealed class UserQuotaTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AServerFitsOnTheQuotasNodesWithinEachTotal()
    {
        var node = NodeId.New();
        var quota = UserQuota.Create(UserId.New(), 2, 2048, 100, [node, node], s_now).Value;
        Assert.Equal([node], quota.NodeIds);

        Assert.True(quota.Fits(node, 1, 1024, 50, 1024, 50).IsSuccess);
        Assert.Equal(AccessErrors.QuotaNodeNotAllowed, quota.Fits(NodeId.New(), 0, 0, 0, 128, 1).Error);
        Assert.Equal(AccessErrors.QuotaServers, quota.Fits(node, 2, 0, 0, 128, 1).Error);
        Assert.Equal(AccessErrors.QuotaMemory, quota.Fits(node, 1, 1024, 0, 1025, 1).Error);
        Assert.Equal(AccessErrors.QuotaCpu, quota.Fits(node, 1, 0, 50, 128, 51).Error);
        // Lowered below what is used: nothing more fits, nothing is taken away.
        Assert.True(quota.Set(0, 0, 0, [node], s_now).IsSuccess);
        Assert.Equal(AccessErrors.QuotaServers, quota.Fits(node, 1, 1024, 50, 128, 1).Error);
    }

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(UserQuota.MaxServersLimit + 1, 0, 0)]
    [InlineData(1, -1, 0)]
    [InlineData(1, 0, UserQuota.MaxCpuPercentLimit + 1)]
    public void AQuotaOutOfBoundsIsRefused(int servers, int memory, int cpu)
    {
        Assert.Equal(AccessErrors.QuotaInvalid, UserQuota.Create(UserId.New(), servers, memory, cpu, [], s_now).Error);
    }

    [Fact]
    public void AnOwnerRunsAndDeletesTheirServerButNeitherCapsNorSuspendsIt()
    {
        Assert.Contains(Permissions.Server.Start, (IReadOnlySet<string>)PermissionCatalog.OwnerKeys);
        Assert.Contains(Permissions.Server.FilesEdit, (IReadOnlySet<string>)PermissionCatalog.OwnerKeys);
        Assert.Contains(Permissions.Server.Delete, (IReadOnlySet<string>)PermissionCatalog.OwnerKeys);
        Assert.DoesNotContain(Permissions.Server.LimitsEdit, (IReadOnlySet<string>)PermissionCatalog.OwnerKeys);
        Assert.DoesNotContain(Permissions.Server.SettingsEdit, (IReadOnlySet<string>)PermissionCatalog.OwnerKeys);
        Assert.DoesNotContain(Permissions.Server.Suspend, (IReadOnlySet<string>)PermissionCatalog.OwnerKeys);
        Assert.DoesNotContain(Permissions.Server.Create, (IReadOnlySet<string>)PermissionCatalog.OwnerKeys);
        Assert.All(PermissionCatalog.OwnerKeys, key => Assert.StartsWith("server.", key, StringComparison.Ordinal));

        var server = ServerId.New();
        var permissions = new EffectivePermissions.Builder().AddOwned(server).Build();
        Assert.True(permissions.Has(Permissions.Server.Delete, AccessTarget.ForServer(server, NodeId.New())));
        Assert.False(permissions.Has(Permissions.Server.Delete, AccessTarget.ForServer(ServerId.New(), NodeId.New())));
    }
}
