using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;
using Pulsar.Domain.Servers;

namespace Pulsar.Domain.Tests.Servers;

public sealed class ServerTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    // Names go into notification titles and email subjects: no line breaks or other control characters.
    [Theory]
    [InlineData("Retakes\nBcc: someone")]
    [InlineData("Retakes\u0007")]
    public void ServerAndNodeNamesHaveNoControlCharacters(string name)
    {
        Assert.Equal(ServerErrors.NameInvalid, Server.Create(NodeId.New(), TemplateId.New(), name, null, Server.NewShortId(), s_now).Error);
        Assert.Equal(NodeErrors.NameInvalid, Node.Create(name, null, null, s_now).Error);

        var node = Node.Create("Buenos Aires", null, null, s_now).Value!;
        Assert.Equal(NodeErrors.NameInvalid, node.Update(name, null, null, s_now).Error);
    }

    [Theory]
    [InlineData(OperationStatus.Succeeded, InstallState.Installed)]
    [InlineData(OperationStatus.Failed, InstallState.Failed)]
    [InlineData(OperationStatus.Interrupted, InstallState.Failed)]
    [InlineData(OperationStatus.Cancelled, InstallState.NotInstalled)] // stopped on purpose: nothing failed
    public void AnInstallsOutcomeSetsTheInstallState(OperationStatus outcome, InstallState expected)
    {
        var server = NewServer();
        var install = Operation.Create(OperationType.ServerInstall, server.NodeId, server.Id, null, OperationRequester.User, s_now);
        Assert.True(server.BeginOperation(install, s_now).IsSuccess);
        Assert.Equal(InstallState.Installing, server.InstallState);

        install.Complete(outcome, null, null, s_now);
        server.OperationCompleted(install, s_now);

        Assert.Equal(expected, server.InstallState);
        Assert.Null(server.ActiveOperationId);
    }

    [Fact]
    public void AnUpdateNeedsAnInstalledStoppedServerAndKeepsItInstalledWhateverHappens()
    {
        var server = NewServer();
        var update = Operation.Create(OperationType.ServerUpdate, server.NodeId, server.Id, null, OperationRequester.User, s_now);
        Assert.Equal(ServerErrors.NotInstalled, server.BeginOperation(update, s_now).Error);

        Install(server);
        server.Observe(ServerRuntimeState.Running, ServerStateReason.Ready, null, 1, s_now, snapshot: false);
        Assert.Equal(ServerErrors.MustBeStopped, server.BeginOperation(update, s_now).Error);

        server.Observe(ServerRuntimeState.Offline, ServerStateReason.Requested, 0, 2, s_now, snapshot: false);
        Assert.True(server.BeginOperation(update, s_now).IsSuccess);
        update.Complete(OperationStatus.Cancelled, "operation.cancelled", null, s_now);
        server.OperationCompleted(update, s_now);
        Assert.Equal(InstallState.Installed, server.InstallState);
    }

    [Fact]
    public void ExtraArgumentsAreSingleBoundedLines()
    {
        var server = NewServer();

        Assert.True(server.SetStartup(["-tickrate", "128"], autoStart: true, restartOnCrash: false, s_now).IsSuccess);
        Assert.Equal(["-tickrate", "128"], server.ExtraArguments);
        Assert.True(server.AutoStart);
        Assert.Equal(ServerErrors.ExtraArgumentsInvalid, server.SetStartup(["a\nb"], false, true, s_now).Error);
        Assert.Equal(ServerErrors.ExtraArgumentsInvalid, server.SetStartup([""], false, true, s_now).Error);
        Assert.Equal(ServerErrors.ExtraArgumentsInvalid, server.SetStartup([.. Enumerable.Repeat("-x", 33)], false, true, s_now).Error);
    }

    [Fact]
    public void VariablesAreSetReplacedAndRemoved()
    {
        var server = NewServer();

        server.SetVariable("map", "de_dust2", null, s_now);
        server.SetSecretVariable("gslt", [1, 2, 3], null, s_now);
        server.SetVariable("map", "de_inferno", null, s_now);

        Assert.Equal("de_inferno", server.Variables.Single(v => v.Key == "map").Value);
        var secret = server.Variables.Single(v => v.Key == "gslt");
        Assert.True(secret.IsSecret);
        Assert.Null(secret.Value);

        server.SetSecretVariable("gslt", null, null, s_now);
        server.SetVariable("map", null, null, s_now);
        Assert.Empty(server.Variables);
    }

    [Theory]
    [InlineData("0.0.0.0", 27015, "10.0.0.1", 27015, true)]
    [InlineData("10.0.0.1", 27015, "0.0.0.0", 27015, true)]
    [InlineData("10.0.0.1", 27015, "10.0.0.1", 27015, true)]
    [InlineData("10.0.0.1", 27015, "10.0.0.2", 27015, false)]
    [InlineData("0.0.0.0", 27015, "0.0.0.0", 27016, false)]
    public void AllocationsOverlapOnTheSamePortWhenOneListensEverywhere(string first, int firstPort, string second, int secondPort, bool overlaps)
    {
        var allocation = NodeAllocation.Create(NodeId.New(), first, firstPort, null, s_now).Value;

        Assert.Equal(overlaps, allocation.Overlaps(second, secondPort));
    }

    [Theory]
    [InlineData("::1")]
    [InlineData("300.1.1.1")]
    [InlineData("host.example")]
    public void AllocationsAreIpv4(string address)
    {
        Assert.Equal(NodeErrors.AllocationAddressInvalid, NodeAllocation.Create(NodeId.New(), address, 27015, null, s_now).Error);
    }

    private static Server NewServer() =>
        Server.Create(NodeId.New(), TemplateId.New(), "Test", null, Server.NewShortId(), s_now).Value;

    private static void Install(Server server)
    {
        var install = Operation.Create(OperationType.ServerInstall, server.NodeId, server.Id, null, OperationRequester.User, s_now);
        server.BeginOperation(install, s_now);
        install.Complete(OperationStatus.Succeeded, null, null, s_now);
        server.OperationCompleted(install, s_now);
    }
}
