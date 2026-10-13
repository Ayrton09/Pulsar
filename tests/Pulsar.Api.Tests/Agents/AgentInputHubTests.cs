using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Notifications;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Api.Tests.Notifications;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Nodes;
using Pulsar.Application.Servers;
using Pulsar.Contracts;
using Pulsar.Domain.Common;
using Pulsar.Domain.Notifications;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Tests.Agents;

/// <summary>
/// Each hub method that takes text from an agent cleans it before the panel keeps, logs or shows it (docs/architecture/08,
/// T32): one test per method, so a method that stops doing so is caught.
/// </summary>
public sealed class AgentInputHubTests(NotificationApiFactory factory) : IClassFixture<NotificationApiFactory>
{
    /// <summary>A forged log line, a terminal escape, a right-to-left override and a line separator.</summary>
    private const string Dirty = "first line\r\n[12:00:00 WRN] forged\u001b[31m \u202Egnp.exe\u2028end";

    private const string Clean = "first line[12:00:00 WRN] forged[31m gnp.exeend";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task WhatAHelloSaysAboutTheMachineIsCleaned()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var created = await NodeApi.CreateAsync(admin, "input-hello");
        await using var agent = new TestAgent(factory)
        {
            HelloInventory = new NodeInventory("TEST-NODE\u0007", Dirty + new string('d', 5000), "26200\n.1", "CPU\u202E", 8, 1024, "D:\\Servers\r\nforged"),
            AgentVersion = "1.0.0\r\nforged",
        };
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);

        await agent.ConnectAsync((await agent.AuthenticateAsync()).AccessToken);

        var inventory = (await NodeApi.GetAsync(admin, agent.NodeId)).Node.Inventory!;
        Assert.Equal("TEST-NODE", inventory.Hostname);
        Assert.StartsWith(Clean, inventory.OsDescription, StringComparison.Ordinal);
        Assert.Equal(255, inventory.OsDescription.Length);
        Assert.Equal("26200.1", inventory.OsBuild);
        Assert.Equal("CPU", inventory.CpuModel);
        Assert.Equal("D:\\Serversforged", inventory.ServersRoot);
        Assert.Equal("1.0.0forged", inventory.AgentVersion);
    }

    [DatabaseFact]
    public async Task AnOperationsMessageIsCleaned()
    {
        await using var agent = await ConnectedTestAgentAsync("input-operation");
        var node = new NodeId(agent.NodeId);
        var operation = Operation.Create(OperationType.ServerInstall, node, null, null, OperationRequester.System, DateTimeOffset.UtcNow);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IPulsarDbContext>();
            db.Operations.Add(operation);
            await db.SaveChangesAsync(Ct);
        }

        await agent.Connection!.InvokeAsync(nameof(IAgentHub.OperationCompleted),
            new OperationCompleted(operation.Id.Value, null, OperationOutcome.Failed, "install.failed", Dirty + new string('m', 5000)), Ct);

        await using var check = factory.Services.CreateAsyncScope();
        var completed = await check.ServiceProvider.GetRequiredService<IPulsarDbContext>().Operations.AsNoTracking()
            .SingleAsync(o => o.Id == operation.Id, Ct);
        Assert.StartsWith(Clean, completed.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(AgentInput.MaxMessageLength, completed.ErrorMessage!.Length);
    }

    [DatabaseFact]
    public async Task ATransfersMessageIsCleanedBeforeTheBrowserGetsIt()
    {
        await using var agent = await ConnectedTestAgentAsync("input-transfer");
        var transfers = factory.Services.GetRequiredService<FileTransfers>();
        using var content = new MemoryStream();
        using var upload = transfers.BeginUpload(new NodeId(agent.NodeId), content, 0);
        Assert.NotNull(transfers.ClaimUpload(upload.Ticket, new NodeId(agent.NodeId)));

        await agent.Connection!.InvokeAsync(nameof(IAgentHub.TransferCompleted),
            new TransferResult(upload.Ticket, FileErrors.Failed, Dirty + new string('m', 5000), null), Ct);

        var result = await upload.Result.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.StartsWith(Clean, result.Message, StringComparison.Ordinal);
        Assert.Equal(AgentInput.MaxMessageLength, result.Message!.Length);
    }

    [DatabaseFact]
    public async Task TheDetailOfAFailedStartIsCleanedBeforeItIsNotified()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var created = await NodeApi.CreateAsync(admin, "input-state");
        await using var agent = new InProcessAgent(factory);
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, created.Node.Id)).Node.Connected, "agent connected");
        var server = (await ServerApi.CreateAsync(admin, created.Node.Id, "Input state")).Server.Id;
        await ServerApi.WaitForAsync(admin, server, s => s.Server.InstallState == InstallState.Installed && s.Server.ActiveOperation is null, "installed");
        using (var response = await admin.PostAsync("/api/v1/notifications/channels", new NotificationChannelRequest("input-state", NotificationKind.Email, true,
            [NotificationEvent.ServerStartFailed], [server], "en", null, ["ops@example.com"])))
        {
            await AccessScenario.EnsureSuccessAsync(response);
        }

        long sequence;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            sequence = (await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().Servers.AsNoTracking()
                .SingleAsync(s => s.Id == new ServerId(server), Ct)).StateSequence;
        }

        Assert.True(await agent.Channel!.DeliverAsync(nameof(IAgentHub.ServerStateChanged), new ServerStateChanged(server, RuntimeState.Offline,
            RuntimeState.Starting, DateTimeOffset.UtcNow, sequence + 1, 1, StateChangeReason.StartFailed, Dirty + new string('m', 5000)), Ct));

        await InProcessAgent.EventuallyAsync(() => Task.FromResult(factory.Email.Sent.Any(m => m.Channel == "input-state")), "start failure notified");
        var detail = Assert.Single(factory.Email.Sent.Single(m => m.Channel == "input-state").Message.Fields, f => f.Name == "Error").Value;
        Assert.StartsWith(Clean, detail, StringComparison.Ordinal);
        Assert.True(detail.Length <= AgentInput.MaxMessageLength);
    }

    private async Task<TestAgent> ConnectedTestAgentAsync(string name)
    {
        using var admin = await factory.CreateAdminClientAsync();
        var created = await NodeApi.CreateAsync(admin, name);
        var agent = new TestAgent(factory);
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.ConnectAsync((await agent.AuthenticateAsync()).AccessToken);
        return agent;
    }
}
