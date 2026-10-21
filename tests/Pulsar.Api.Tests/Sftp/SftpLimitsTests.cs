using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Sftp;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Servers;
using Pulsar.Domain.Access;
using Pulsar.Domain.Common;
using Renci.SshNet.Common;
using static Pulsar.Api.Tests.Sftp.SftpTesting;

namespace Pulsar.Api.Tests.Sftp;

/// <summary>The SFTP server's bounds: sessions per user, connections per address, the sign-in deadline, idle sessions, open files.</summary>
public sealed class SftpLimitsTests(SftpLimitsTests.SmallLimitsFactory factory) : IClassFixture<SftpLimitsTests.SmallLimitsFactory>
{
    [DatabaseFact]
    public async Task AUserHoldsAFewSessionsAndIdleOnesEnd()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await scenario.CreateUserAsync("limits-user");
        var port = await factory.PortAsync();
        using var first = await ConnectedAsync(port, "limits-user");
        using var second = await ConnectedAsync(port, "limits-user");
        var refused = await FailsAsync(Client(port, "limits-user", AccessScenario.Password));
        Assert.IsAssignableFrom<SshException>(refused);

        // Neither sends anything: both end once idle for longer than allowed.
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(!first.IsConnected && !second.IsConnected), "idle sessions ended");
        using var third = await ConnectedAsync(port, "limits-user");
        Assert.True(third.IsConnected);
    }

    [DatabaseFact]
    public async Task ConnectionsPerAddressAndTheSignInDeadlineAreBounded()
    {
        _ = await AccessScenario.CreateAsync(factory);
        var port = await factory.PortAsync();
        var held = new List<TcpClient>();
        try
        {
            for (var i = 0; i < SmallLimitsFactory.PerAddress; i++)
            {
                var tcp = new TcpClient();
                await tcp.ConnectAsync("127.0.0.1", port, Ct);
                Assert.StartsWith("SSH-2.0-", await ReadLineAsync(tcp), StringComparison.Ordinal);
                held.Add(tcp);
            }

            // One more from the same address is closed on arrival, before any SSH.
            using (var refused = new TcpClient())
            {
                await refused.ConnectAsync("127.0.0.1", port, Ct);
                Assert.Null(await ReadLineAsync(refused));
            }

            // Nobody signs in: they are closed at the deadline.
            var waited = Stopwatch.StartNew();
            foreach (var tcp in held)
            {
                await ClosedAsync(tcp);
            }

            Assert.InRange(waited.Elapsed, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(20));
        }
        finally
        {
            held.ForEach(t => t.Dispose());
        }
    }

    [DatabaseFact]
    public async Task ASessionHoldsAFewOpenFiles()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await using var agent = await ConnectedAgentAsync(factory, scenario.Admin, "limits-files");
        var server = await InstalledAsync(scenario.Admin, agent, "Limits");
        for (var i = 0; i <= SmallLimitsFactory.Handles; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(server.Directory, $"f{i}.cfg"), "x", Ct);
        }

        var role = await ServerRoleAsync(scenario, "limits-view", Permissions.Server.View, Permissions.Server.FilesView);
        var user = await scenario.CreateUserAsync("limits-files-user");
        await GrantAsync(scenario.Admin, user.Id, server.Id, role.Id);
        using var client = await ConnectedAsync(await factory.PortAsync(), $"limits-files-user.{server.ShortId}");
        var open = new List<Stream>();
        try
        {
            for (var i = 0; i < SmallLimitsFactory.Handles; i++)
            {
                var name = $"/f{i}.cfg";
                open.Add(await Run(() => (Stream)client.OpenRead(name)));
            }

            var failure = await Assert.ThrowsAnyAsync<SshException>(() => Run(() => client.OpenRead($"/f{SmallLimitsFactory.Handles}.cfg")));
            Assert.Contains("Too many open files", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            open.ForEach(s => s.Dispose());
        }

        Assert.Equal("x", await Run(() => client.ReadAllText($"/f{SmallLimitsFactory.Handles}.cfg")));
    }

    [DatabaseFact]
    public async Task AFileOpenButNotBeingReadHoldsNoneOfItsNodesTransferPlaces()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await using var agent = await ConnectedAgentAsync(factory, scenario.Admin, "limits-reads");
        var server = await InstalledAsync(scenario.Admin, agent, "Reads");
        var content = RandomNumberGenerator.GetBytes(2 * 1024 * 1024);
        await File.WriteAllBytesAsync(Path.Combine(server.Directory, "big.bin"), content, Ct);
        await File.WriteAllTextAsync(Path.Combine(server.Directory, "small.cfg"), "x", Ct);
        var role = await ServerRoleAsync(scenario, "limits-reads-view", Permissions.Server.View, Permissions.Server.FilesView);
        var user = await scenario.CreateUserAsync("limits-reads-user");
        await GrantAsync(scenario.Admin, user.Id, server.Id, role.Id);
        var transfers = factory.Services.GetRequiredService<FileTransfers>();
        int Places() => FreePlaces(transfers, new NodeId(agent.Config!.NodeId), new UserId(user.Id));
        using var client = await ConnectedAsync(await factory.PortAsync(), $"limits-reads-user.{server.ShortId}");

        // Read to its end and left open: its download from the node is over.
        using var small = await Run(() => client.OpenRead("/small.cfg"));
        Assert.Equal((int)'x', await Run(() => small.ReadByte()));
        Assert.Equal(-1, await Run(() => small.ReadByte()));
        Assert.Equal(transfers.Limits.PerUser, Places());

        // Read in part and left: its download ends once nothing read it for a while, and reading goes on where it was.
        using var big = await Run(() => client.OpenRead("/big.bin"));
        var start = new byte[64 * 1024];
        await Run(() => big.ReadExactly(start));
        Assert.Equal(transfers.Limits.PerUser - 1, Places());
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(Places() == transfers.Limits.PerUser), "the idle download ended");
        using var rest = new MemoryStream();
        await Run(() => big.CopyTo(rest));
        Assert.Equal(content, start.Concat(rest.ToArray()).ToArray());
    }

    [DatabaseFact]
    public async Task AConnectionHoldsAFewChannelsThatMustStartSftpSoon()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await scenario.CreateUserAsync("limits-channels");
        var (session, tcp) = await RawSessionAsync(await factory.PortAsync(), "limits-channels", AccessScenario.Password);
        using (tcp)
        using (session)
        {
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Closed += (_, _) => ended.TrySetResult();

            // A channel that never asks for SFTP is closed soon.
            var idle = await session.OpenChannelAsync(Ct);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            idle.Closed += (_, _) => closed.TrySetResult();
            await closed.Task.WaitAsync(SftpApiFactory.Timeout, Ct);

            // A few open at once; one more ends the connection.
            for (var i = 0; i < SftpHost.MaxChannelsPerConnection; i++)
            {
                await session.OpenChannelAsync(Ct).WaitAsync(SftpApiFactory.Timeout, Ct);
            }

            await Assert.ThrowsAnyAsync<Exception>(() => session.OpenChannelAsync(Ct).WaitAsync(SftpApiFactory.Timeout, Ct));
            await ended.Task.WaitAsync(SftpApiFactory.Timeout, Ct);
        }
    }

    /// <summary>How many transfers <paramref name="user"/> could start on <paramref name="node"/> now (each is given back at once).</summary>
    private static int FreePlaces(FileTransfers transfers, NodeId node, UserId user)
    {
        var started = new List<FileTransfers.Download>();
        try
        {
            while (started.Count < 16 && transfers.BeginDownload(node, user) is { } download)
            {
                started.Add(download);
            }

            return started.Count;
        }
        finally
        {
            started.ForEach(d => d.Dispose());
        }
    }

    /// <summary>Reads (and drops) what the server sends until it closes the connection.</summary>
    private static async Task ClosedAsync(TcpClient tcp)
    {
        var buffer = new byte[4096];
        try
        {
            while (await tcp.GetStream().ReadAsync(buffer, Ct).AsTask().WaitAsync(SftpApiFactory.Timeout, Ct) > 0)
            {
            }
        }
        catch (IOException)
        {
            // Reset rather than closed: closed all the same.
        }
    }

    /// <summary>The first line the server sends, or null when it closed the connection without one.</summary>
    private static async Task<string?> ReadLineAsync(TcpClient tcp)
    {
        var stream = tcp.GetStream();
        var line = new StringBuilder();
        var buffer = new byte[1];
        try
        {
            while (true)
            {
                var n = await stream.ReadAsync(buffer, Ct).AsTask().WaitAsync(SftpApiFactory.Timeout, Ct);
                if (n == 0)
                {
                    return line.Length == 0 ? null : line.ToString();
                }

                if (buffer[0] == '\n')
                {
                    return line.ToString().TrimEnd('\r');
                }

                line.Append((char)buffer[0]);
            }
        }
        catch (IOException)
        {
            return line.Length == 0 ? null : line.ToString();
        }
    }

    public sealed class SmallLimitsFactory : SftpApiFactory
    {
        public const int PerAddress = 3;
        public const int Handles = 3;

        protected override void ConfigureSftp(SftpOptions options)
        {
            options.MaxSessionsPerUser = 2;
            options.MaxConnectionsPerAddress = PerAddress;
            options.AuthenticationTimeout = TimeSpan.FromSeconds(3);
            options.IdleTimeout = TimeSpan.FromSeconds(4);
            options.MaxHandlesPerSession = Handles;
            options.SubsystemTimeout = TimeSpan.FromSeconds(1);
            options.ReadIdleRelease = TimeSpan.FromSeconds(1);
        }
    }
}
