using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Audit;
using Pulsar.Application.Nodes;
using Pulsar.Contracts;
using Pulsar.Domain.Audit;
using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;
using Pulsar.Domain.Servers;

namespace Pulsar.Application.Servers;

/// <summary>
/// The file manager's zips (Pulsar 1.0.11; docs/architecture/07, "Files"): files and folders of a server zipped into a
/// new zip, and a zip written out into a folder. Both are operations the server's node runs as the file manager does
/// (docs/architecture/08, "File manager"), while the game may keep running; the paths are checked here and again there.
/// </summary>
public sealed class ServerArchives(
    IPulsarDbContext db,
    IAgentConnections connections,
    AgentSessionStore sessions,
    ServerOperations operations,
    ServerManagement servers,
    IAuditLog audit,
    IRequestContext request,
    TimeProvider clock)
{
    /// <summary>Zips <paramref name="names"/> of <paramref name="directory"/> into <paramref name="archive"/>, which must not exist.</summary>
    public async Task<Result<OperationView>> CompressAsync(ServerId id, string? directory, IReadOnlyList<string>? names, string archive,
        CancellationToken cancellationToken)
    {
        directory ??= ServerFilePath.Root;
        names ??= [];
        if ((Problem(archive) ?? (directory.Length == 0 ? null : Problem(directory))) is { } problem)
        {
            return problem;
        }

        if (names.Count is 0 or > CompressFilesCommand.MaxNames)
        {
            return ServerErrors.FilePathInvalid($"Between 1 and {CompressFilesCommand.MaxNames} names.");
        }

        foreach (var name in names)
        {
            if (string.IsNullOrEmpty(name) || name.Contains('/', StringComparison.Ordinal))
            {
                return ServerErrors.FilePathInvalid($"'{name}' is not a name in the folder.");
            }

            if (Problem(ServerFilePath.Join(directory, name)) is { } named)
            {
                return named;
            }
        }

        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
        {
            return ServerErrors.FilePathInvalid("A name is given twice.");
        }

        return await RunAsync(id, OperationType.FilesCompress, AgentCapabilities.CompressFiles,
            server => new CompressFilesCommand(server.Id.Value, directory, [.. names], archive), AuditActions.Server.FilesCompressRequested,
            new { directory, names, archive }, cancellationToken);
    }

    /// <summary>Writes <paramref name="archive"/>'s files into <paramref name="destination"/>; existing files are replaced only when <paramref name="overwrite"/>.</summary>
    public async Task<Result<OperationView>> ExtractAsync(ServerId id, string archive, string? destination, bool overwrite,
        CancellationToken cancellationToken)
    {
        destination ??= ServerFilePath.Root;
        if ((Problem(archive) ?? (destination.Length == 0 ? null : Problem(destination))) is { } problem)
        {
            return problem;
        }

        return await RunAsync(id, OperationType.FilesExtract, AgentCapabilities.ExtractArchive,
            server => new ExtractArchiveCommand(server.Id.Value, archive, destination, overwrite), AuditActions.Server.FilesExtractRequested,
            new { archive, destination, overwrite }, cancellationToken);
    }

    private async Task<Result<OperationView>> RunAsync(ServerId id, OperationType type, string capability, Func<Server, AgentCommand> command,
        string action, object metadata, CancellationToken cancellationToken)
    {
        var server = await db.Servers.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (server is null)
        {
            return ServerErrors.NotFound;
        }

        if (connections.Find(server.NodeId) is not { } live || !sessions.IsActive(live.SessionId))
        {
            return NodeErrors.NotConnected;
        }

        if (!live.Capabilities.Contains(capability, StringComparer.Ordinal))
        {
            return ServerErrors.ArchivesUnsupported;
        }

        // The size is known on the node only: refused on a drive already below the floor (the node also keeps room).
        if (live.BelowFloor(DiskUses.Servers) is not null)
        {
            return NodeErrors.DiskLow;
        }

        var operation = Operation.Create(type, server.NodeId, server.Id, request.UserId, OperationRequester.User, clock.GetUtcNow());
        var run = await operations.RunAsync(server, operation, command(server),
            () => audit.Record(action, metadata: metadata).WithServer(server.Id, server.NodeId), cancellationToken);
        return run.IsSuccess ? servers.View(run.Value) : run.Error!;
    }

    /// <summary>A path the file manager takes and may change: not the server's folder itself, nor in Pulsar's own folder.</summary>
    private static DomainError? Problem(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return ServerErrors.FilePathInvalid("A path is required.");
        }

        if (ServerFilePath.Error(path) is { } error)
        {
            return ServerErrors.FilePathInvalid(error);
        }

        // Pulsar's own folder (by its name or its 8.3 short name): changed by Pulsar only, never zipped.
        var first = path.Split('/')[0];
        return first.Equals(".pulsar", StringComparison.OrdinalIgnoreCase)
            || (first.Length > 7 && first.StartsWith("PULSAR~", StringComparison.OrdinalIgnoreCase) && first[7..].All(char.IsAsciiDigit))
            ? ServerErrors.FileRejected(FileErrors.AccessDenied, "Only Pulsar changes its own folder.")
            : null;
    }
}
