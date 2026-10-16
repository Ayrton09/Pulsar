using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pulsar.Api.Tests.Infrastructure;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Servers;
using Pulsar.Contracts;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;

namespace Pulsar.Api.Tests.Servers;

/// <summary>
/// What the SFTP server (Phase 17) asks of <see cref="ServerFiles"/>, through the panel and the real agent core: pages,
/// stat, times, renames that replace, streamed and appended writes, and reads from an offset.
/// </summary>
public sealed class SftpFileTests(PulsarApiFactory factory) : IClassFixture<PulsarApiFactory>
{
    /// <summary>Temporary files of uploads in progress (ServerFileRules.TemporaryPrefix, which the panel does not reference).</summary>
    private const string ServerFileRulesPrefix = ".pulsar-upload-";

    private readonly Dictionary<Guid, string> _shortIds = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task PagesReachEveryEntryAndSftpSeesNeitherPulsarsFolderNorHiddenNames()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "sftp-pages");
        var (server, id) = await InstalledServerAsync(admin, agent, "Pages");
        var directory = ServerDirectory(agent, server);
        Directory.CreateDirectory(Path.Combine(directory, ".pulsar"));
        Directory.CreateDirectory(Path.Combine(directory, "maps"));
        var names = Enumerable.Range(0, FileLimits.MaxListEntries + 10).Select(i => $"m{i:D4}.bsp").ToList();
        foreach (var name in names)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "maps", name), "", Ct);
        }

        await File.WriteAllTextAsync(Path.Combine(directory, "maps", "odd\u202Egnp.exe"), "", Ct);
        await using var scope = factory.Services.CreateAsyncScope();
        var files = scope.ServiceProvider.GetRequiredService<ServerFiles>();

        var seen = new List<string>();
        string? after = null;
        var skipped = 0;
        do
        {
            var page = await files.ListPageAsync(id, "maps", after, Ct);
            Assert.True(page.IsSuccess, page.Error?.Message);
            Assert.False(page.Value.Truncated);
            seen.AddRange(page.Value.Entries.Select(e => e.Name));
            skipped += page.Value.Skipped;
            after = page.Value.Next;
        }
        while (after is not null);

        Assert.Equal(names, seen);
        Assert.Equal(1, skipped);

        var root = await files.ListPageAsync(id, null, null, Ct);
        Assert.DoesNotContain(root.Value.Entries, e => e.Name.Equals(".pulsar", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(root.Value.Entries, e => e.Name == "maps" && e.Kind == FileEntryKind.Directory);

        Assert.Equal(FileErrors.NotFound, (await files.ListPageAsync(id, ".pulsar", null, Ct)).Error!.Code);
        Assert.Equal(FileErrors.NotFound, (await files.StatAsync(id, ".pulsar", Ct)).Error!.Code);
        Assert.Equal(FileErrors.NotFound, (await files.StatAsync(id, "maps/odd\u202Egnp.exe", Ct)).Error!.Code);
        Assert.Equal(FileErrors.InvalidPath, (await files.ListPageAsync(id, "maps", "a/b", Ct)).Error!.Code);
        Assert.Equal(FileErrors.InvalidPath, (await files.ListPageAsync(id, "..", null, Ct)).Error!.Code);

        // The web still lists Pulsar's folder as it always did.
        Assert.Contains((await files.ListAsync(id, null, Ct)).Value.Entries, e => e.Name == ".pulsar");
    }

    [DatabaseFact]
    public async Task StatTimesAndRenamesThatReplace()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "sftp-stat");
        var (server, id) = await InstalledServerAsync(admin, agent, "Stat");
        var directory = ServerDirectory(agent, server);
        await File.WriteAllTextAsync(Path.Combine(directory, "server.cfg"), "old", Ct);
        await File.WriteAllTextAsync(Path.Combine(directory, "server.cfg.filepart"), "new", Ct);
        await using var scope = factory.Services.CreateAsyncScope();
        var files = scope.ServiceProvider.GetRequiredService<ServerFiles>();

        var stat = await files.StatAsync(id, "server.cfg", Ct);
        Assert.Equal(new FileEntry("server.cfg", FileEntryKind.File, 3, stat.Value.Entry.ModifiedAt), stat.Value.Entry);
        Assert.Equal(FileErrors.NotFound, (await files.StatAsync(id, "missing.cfg", Ct)).Error!.Code);
        Assert.Equal(FileErrors.InvalidPath, (await files.StatAsync(id, ServerFilePath.Root, Ct)).Error!.Code);

        var time = new DateTimeOffset(2019, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var touched = await files.SetModifiedAsync(id, "server.cfg", time, Ct);
        Assert.Equal(time, touched.Value.Entry.ModifiedAt);
        Assert.Equal(time.UtcDateTime, File.GetLastWriteTimeUtc(Path.Combine(directory, "server.cfg")));

        Assert.Equal(FileErrors.Exists, (await files.MoveAsync(id, "server.cfg.filepart", "server.cfg", replace: false, Ct)).Error!.Code);
        Assert.True((await files.MoveAsync(id, "server.cfg.filepart", "server.cfg", replace: true, Ct)).IsSuccess);
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(directory, "server.cfg"), Ct));
        Assert.Equal(FileErrors.NotFound, (await files.MoveAsync(id, "server.cfg", ".pulsar/x.cfg", replace: true, Ct)).Error!.Code);

        var moves = (await AuditAsync(server)).Where(a => a.Action == AuditActions.Server.FileMoved).ToList();
        Assert.Contains(moves, a => a.Outcome == AuditOutcome.Success && Flag(a, "replace"));
        Assert.Contains(moves, a => a.Outcome == AuditOutcome.Failure && a.Metadata.Contains(FileErrors.Exists, StringComparison.Ordinal));
    }

    [DatabaseFact]
    public async Task StreamedWritesAppendsAndReadsFromAnOffset()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "sftp-stream");
        var (server, id) = await InstalledServerAsync(admin, agent, "Stream");
        var directory = ServerDirectory(agent, server);
        await using var scope = factory.Services.CreateAsyncScope();
        var files = scope.ServiceProvider.GetRequiredService<ServerFiles>();
        var content = RandomNumberGenerator.GetBytes((3 * 1024 * 1024) + 5);

        var written = await WriteAsync(files, id, "de_test.bsp", content[..(2 * 1024 * 1024)], FileWriteMode.CreateNew);
        Assert.Equal(2 * 1024 * 1024, written.Value.Entry.Size);
        Assert.Equal(FileErrors.Exists, (await WriteAsync(files, id, "de_test.bsp", [1], FileWriteMode.CreateNew)).Error!.Code);

        // A resumed upload: the rest goes at the end of what is there.
        var appended = await WriteAsync(files, id, "de_test.bsp", content[(2 * 1024 * 1024)..], FileWriteMode.Append);
        Assert.Equal(content.Length, appended.Value.Entry.Size);
        Assert.Equal(SHA256.HashData(content), SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(directory, "de_test.bsp"), Ct)));

        // A resumed download: from the offset on, the whole file still described.
        Assert.Equal(content[1000..], await ReadAsync(files, id, "de_test.bsp", 1000, content.Length));
        Assert.Empty(await ReadAsync(files, id, "de_test.bsp", content.Length + 10, content.Length));
        Assert.Equal(ServerErrors.FileOffsetInvalid.Code, (await files.OpenAtAsync(id, "de_test.bsp", -1, Ct)).Error!.Code);

        Assert.True((await WriteAsync(files, id, "de_test.bsp", Encoding.UTF8.GetBytes("small"), FileWriteMode.Replace)).IsSuccess);
        Assert.Equal("small", await File.ReadAllTextAsync(Path.Combine(directory, "de_test.bsp"), Ct));
        Assert.True((await WriteAsync(files, id, "empty.cfg", [], FileWriteMode.CreateNew)).IsSuccess);
        Assert.Equal(0, new FileInfo(Path.Combine(directory, "empty.cfg")).Length);
        Assert.Equal(FileErrors.NotFound, (await WriteAsync(files, id, ".pulsar/x.cfg", [1], FileWriteMode.Replace)).Error!.Code);

        var audited = await AuditAsync(server);
        Assert.Contains(audited, a => a.Action == AuditActions.Server.FileWritten && a.Outcome == AuditOutcome.Success
            && Flag(a, "append") && Number(a, "size") == content.Length - (2 * 1024 * 1024));
        Assert.Contains(audited, a => a.Action == AuditActions.Server.FileDownloaded && Number(a, "offset") == 1000);
        Assert.DoesNotContain(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories),
            f => Path.GetFileName(f).StartsWith(ServerFileRulesPrefix, StringComparison.Ordinal));
    }

    [DatabaseFact]
    public async Task AStreamedWriteThatFailsOrGrowsTooLargeLeavesNoShorterFile()
    {
        using var admin = await factory.CreateAdminClientAsync();
        await using var agent = await ConnectedAgentAsync(admin, "sftp-fail");
        var (server, id) = await InstalledServerAsync(admin, agent, "Fail");
        var directory = ServerDirectory(agent, server);
        await File.WriteAllTextAsync(Path.Combine(directory, "server.cfg"), "kept", Ct);
        await using var scope = factory.Services.CreateAsyncScope();
        var files = scope.ServiceProvider.GetRequiredService<ServerFiles>();

        // The client's connection drops after part of the file: the node never takes it for the whole.
        using (var broken = new BreakingStream(new byte[100_000], breakAfter: 50_000))
        {
            var failed = await files.WriteStreamAsync(id, "server.cfg", broken, FileWriteMode.Replace, Ct);
            Assert.Equal(FileErrors.TransferFailed, failed.Error!.Code);
        }

        Assert.Equal("kept", await File.ReadAllTextAsync(Path.Combine(directory, "server.cfg"), Ct));

        // More than the upload limit: refused once past it.
        var limited = ActivatorUtilities.CreateInstance<ServerFiles>(scope.ServiceProvider, new FileManagerOptions { MaxUploadBytes = 10 });
        using (var tooLarge = new MemoryStream(new byte[11]))
        {
            Assert.Equal(FileErrors.TooLarge, (await limited.WriteStreamAsync(id, "server.cfg", tooLarge, FileWriteMode.Replace, Ct)).Error!.Code);
        }

        using (var exactly = new MemoryStream(new byte[10]))
        {
            Assert.True((await limited.WriteStreamAsync(id, "ten.cfg", exactly, FileWriteMode.CreateNew, Ct)).IsSuccess);
        }

        Assert.Equal("kept", await File.ReadAllTextAsync(Path.Combine(directory, "server.cfg"), Ct));
        await InProcessAgent.EventuallyAsync(() => Task.FromResult(!Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Any(f => Path.GetFileName(f).StartsWith(ServerFileRulesPrefix, StringComparison.Ordinal))), "temporary files removed");
        Assert.Contains(await AuditAsync(server), a => a.Action == AuditActions.Server.FileWritten && a.Outcome == AuditOutcome.Failure
            && a.Metadata.Contains(FileErrors.TooLarge, StringComparison.Ordinal));
    }

    private static async Task<Result<FileDetails>> WriteAsync(ServerFiles files, ServerId id, string path, byte[] content, FileWriteMode mode)
    {
        using var stream = new MemoryStream(content);
        return await files.WriteStreamAsync(id, path, stream, mode, Ct);
    }

    private static async Task<byte[]> ReadAsync(ServerFiles files, ServerId id, string path, long offset, long size)
    {
        var opened = await files.OpenAtAsync(id, path, offset, Ct);
        Assert.True(opened.IsSuccess, opened.Error?.Message);
        using var content = opened.Value;
        Assert.Equal(size, content.File.Entry.Size);
        Assert.Equal(Math.Max(0, size - offset), content.Length);
        using var copy = new MemoryStream();
        await content.Content.CopyToAsync(copy, Ct);
        content.Complete();
        return copy.ToArray();
    }

    private static bool Flag(AuditEntry entry, string name) => Property(entry, name) is { ValueKind: JsonValueKind.True };

    private static long? Number(AuditEntry entry, string name) => Property(entry, name) is { ValueKind: JsonValueKind.Number } value ? value.GetInt64() : null;

    private static JsonElement? Property(AuditEntry entry, string name)
    {
        using var metadata = JsonDocument.Parse(entry.Metadata);
        return metadata.RootElement.TryGetProperty(name, out var value) ? value.Clone() : null;
    }

    private async Task<List<AuditEntry>> AuditAsync(Guid server)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPulsarDbContext>().AuditLog.AsNoTracking()
            .Where(e => e.ServerId == new ServerId(server)).ToListAsync(Ct);
    }

    private string ServerDirectory(InProcessAgent agent, Guid server) => Path.Combine(agent.ServersRoot, _shortIds[server]);

    private async Task<InProcessAgent> ConnectedAgentAsync(PanelClient admin, string name)
    {
        var created = await NodeApi.CreateAsync(admin, name);
        var agent = new InProcessAgent(factory);
        await agent.EnrollAsync(created.Enrollment.Token, created.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, created.Node.Id)).Node.Connected, "agent connected");
        return agent;
    }

    private async Task<(Guid Server, ServerId Id)> InstalledServerAsync(PanelClient admin, InProcessAgent agent, string name)
    {
        var created = await ServerApi.CreateAsync(admin, agent.Config!.NodeId, name);
        _shortIds[created.Server.Id] = created.Server.ShortId;
        await ServerApi.WaitForAsync(admin, created.Server.Id, s => s.Server.InstallState == InstallState.Installed && s.Server.ActiveOperation is null,
            "installed");
        return (created.Server.Id, new ServerId(created.Server.Id));
    }

    /// <summary>Content that fails after some bytes, as an SFTP client's connection that drops.</summary>
    private sealed class BreakingStream(byte[] content, int breakAfter) : MemoryStream(content)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= breakAfter)
            {
                throw new IOException("The connection dropped.");
            }

            return await base.ReadAsync(buffer[..(int)Math.Min(buffer.Length, breakAfter - Position)], cancellationToken);
        }
    }
}

/// <summary>An older agent's node: what SFTP needs is refused before the node is asked, with a code that says why.</summary>
public sealed class SftpCapabilityTests(SftpCapabilityTests.WithoutSftpFactory factory) : IClassFixture<SftpCapabilityTests.WithoutSftpFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task AnAgentWithoutSftpKeepsItsFileManagerButGetsNoSftpCommand()
    {
        using var admin = await factory.CreateAdminClientAsync();
        var node = await NodeApi.CreateAsync(admin, "no-sftp");
        await using var agent = new InProcessAgent(factory);
        await agent.EnrollAsync(node.Enrollment.Token, node.Enrollment.PanelKeyFingerprint);
        await agent.StartAsync();
        await InProcessAgent.EventuallyAsync(async () => (await NodeApi.GetAsync(admin, node.Node.Id)).Node.Connected, "agent connected");
        var created = await ServerApi.CreateAsync(admin, node.Node.Id, "No SFTP");
        await ServerApi.WaitForAsync(admin, created.Server.Id, s => s.Server.InstallState == InstallState.Installed && s.Server.ActiveOperation is null,
            "installed");
        var id = new ServerId(created.Server.Id);
        var directory = Path.Combine(agent.ServersRoot, created.Server.ShortId);
        await File.WriteAllTextAsync(Path.Combine(directory, "a.cfg"), "a", Ct);
        await File.WriteAllTextAsync(Path.Combine(directory, "b.cfg"), "b", Ct);
        await using var scope = factory.Services.CreateAsyncScope();
        var files = scope.ServiceProvider.GetRequiredService<ServerFiles>();
        var unsupported = ServerErrors.FilesSftpUnsupported.Code;

        Assert.Equal(unsupported, (await files.ListPageAsync(id, null, null, Ct)).Error!.Code);
        Assert.Equal(unsupported, (await files.StatAsync(id, "a.cfg", Ct)).Error!.Code);
        Assert.Equal(unsupported, (await files.OpenAtAsync(id, "a.cfg", 0, Ct)).Error!.Code);
        Assert.Equal(unsupported, (await files.MoveAsync(id, "a.cfg", "b.cfg", replace: true, Ct)).Error!.Code);
        Assert.Equal(unsupported, (await files.SetModifiedAsync(id, "a.cfg", DateTimeOffset.UnixEpoch, Ct)).Error!.Code);
        using (var content = new MemoryStream([1]))
        {
            Assert.Equal(unsupported, (await files.WriteStreamAsync(id, "a.cfg", content, FileWriteMode.Append, Ct)).Error!.Code);
        }

        Assert.Equal("b", await File.ReadAllTextAsync(Path.Combine(directory, "b.cfg"), Ct));
        Assert.True((await files.ListAsync(id, null, Ct)).IsSuccess);
        Assert.Equal(FileErrors.Exists, (await files.MoveAsync(id, "a.cfg", "b.cfg", Ct)).Error!.Code);
    }

    /// <summary>The panel, seeing its agents without the SFTP capability.</summary>
    public sealed class WithoutSftpFactory() : WithoutCapabilityFactory(AgentCapabilities.SftpFiles);
}
