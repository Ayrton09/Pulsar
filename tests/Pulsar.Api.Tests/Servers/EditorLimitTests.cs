using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Tests.Servers;

/// <summary>The editor's size limit holds even when a node sends more than it was asked to.</summary>
public sealed class EditorLimitTests(EditorLimitTests.LimitIgnoringFactory factory) : IClassFixture<EditorLimitTests.LimitIgnoringFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task TheEditorRefusesAFileLargerThanItsLimitWhateverTheNodeSends()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var node = await NodeApi.CreateAsync(admin, "editor-limit");
        await using var agent = new InProcessAgent(factory);
        await agent.EnrollAsync(node.Enrollment.Token, node.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, node.Node.Id)).Node.Connected, "agent connected");
        var created = await ServerApi.CreateAsync(admin, node.Node.Id, "Limit");
        await ServerApi.WaitForAsync(admin, created.Server.Id, s => s.Server.InstallState == InstallState.Installed && s.Server.ActiveOperation is null,
            "installed");
        await File.WriteAllBytesAsync(Path.Combine(agent.ServersRoot, created.Server.ShortId, "big.cfg"), new byte[FileLimits.MaxEditableBytes + 1], Ct);

        using var response = await admin.GetAsync($"/api/v1/servers/{created.Server.Id}/files/content?path=big.cfg");

        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.RequestEntityTooLarge, $"{(int)response.StatusCode}: {body}");
        using var problem = JsonDocument.Parse(body);
        Assert.Equal(FileErrors.TooLarge, problem.RootElement.GetProperty("code").GetString());
    }

    /// <summary>The panel, with nodes that are not told the editor's limit (as if they ignored it).</summary>
    public sealed class LimitIgnoringFactory : PulsarApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                var original = services.Single(d => d.ServiceType == typeof(IAgentConnections));
                services.Remove(original);
                services.AddSingleton<IAgentConnections>(sp => new LimitIgnoring((IAgentConnections)original.ImplementationFactory!(sp)));
            });
        }

        private sealed class LimitIgnoring(IAgentConnections inner) : IAgentConnections
        {
            public AgentConnectionInfo? Find(NodeId node) => inner.Find(node);

            public void Disconnect(NodeId node) => inner.Disconnect(node);

            public void DisconnectEnded(NodeId node) => inner.DisconnectEnded(node);

            public Task<int> UpdatePanelKeysAsync(NodeId? node, CancellationToken cancellationToken) => inner.UpdatePanelKeysAsync(node, cancellationToken);

            public Task<CommandAck?> SendAsync(NodeId node, AgentCommand command, UserId? actor, CancellationToken cancellationToken, Guid? operationId = null) =>
                inner.SendAsync(node, command is SendFileCommand send ? send with { MaxSize = long.MaxValue } : command, actor, cancellationToken, operationId);
        }
    }
}
