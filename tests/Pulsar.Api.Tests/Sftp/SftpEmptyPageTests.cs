using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;
using Pulsar.Domain.Access;
using Pulsar.Domain.Common;
using static Pulsar.Api.Tests.Sftp.SftpTesting;

namespace Pulsar.Api.Tests.Sftp;

/// <summary>
/// A page of a listing the panel keeps nothing of (names SFTP hides, from a node that did not leave them out itself) is
/// not the end of the folder: the next one is read.
/// </summary>
public sealed class SftpEmptyPageTests(SftpEmptyPageTests.EmptyPagesFactory factory) : IClassFixture<SftpEmptyPageTests.EmptyPagesFactory>
{
    [DatabaseFact]
    public async Task AListingGoesOnPastPagesThePanelKeepsNothingOf()
    {
        var scenario = await AccessScenario.CreateAsync(factory);
        await using var agent = await ConnectedAgentAsync(factory, scenario.Admin, "sftp-empty-pages");
        var server = await InstalledAsync(scenario.Admin, agent, "Empty pages");
        Directory.CreateDirectory(Path.Combine(server.Directory, EmptyPagesFactory.Folder));
        await File.WriteAllTextAsync(Path.Combine(server.Directory, EmptyPagesFactory.Folder, "b.cfg"), "x", Ct);
        var role = await ServerRoleAsync(scenario, "sftp-empty-pager", Permissions.Server.View, Permissions.Server.FilesView);
        var mia = await scenario.CreateUserAsync("sftp-mia");
        await GrantAsync(scenario.Admin, mia.Id, server.Id, role.Id);
        using var client = await ConnectedAsync(await factory.PortAsync(), $"sftp-mia.{server.ShortId}");

        var names = await Run(() => client.ListDirectory($"/{EmptyPagesFactory.Folder}").Select(f => f.Name).ToList());

        Assert.Contains("b.cfg", names);
        Assert.DoesNotContain(names, n => n.Contains('‮', StringComparison.Ordinal));
    }

    /// <summary>The panel, its nodes answering the first two pages of <see cref="Folder"/> with one hidden name each.</summary>
    public sealed class EmptyPagesFactory : SftpApiFactory
    {
        public const string Folder = "pages";

        private static readonly string[] s_hidden = ["a‮1", "a‮2"];

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                var original = services.Single(d => d.ServiceType == typeof(IAgentConnections));
                services.Remove(original);
                services.AddSingleton<IAgentConnections>(sp => new InterceptedConnections((IAgentConnections)original.ImplementationFactory!(sp),
                    (command, next, token) => command is ListFilesCommand { Paged: true, Path: Folder } list && PageOf(list.After) is var page and >= 0
                        ? Task.FromResult<CommandAck?>(new CommandAck(Guid.NewGuid(), AckStatus.Accepted, null, null,
                            new FileListResult([new FileEntry(s_hidden[page], FileEntryKind.File, 1, DateTimeOffset.UtcNow)], false, 0, s_hidden[page])))
                        : next(token)));
            });
        }

        // The first page, then the one after its hidden name; the node's own answer after that.
        private static int PageOf(string? after) => after is null ? 0 : after == s_hidden[0] ? 1 : -1;
    }
}
