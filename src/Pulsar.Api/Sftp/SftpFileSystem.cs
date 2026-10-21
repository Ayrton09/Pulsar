using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.DevTunnels.Ssh;
using Microsoft.EntityFrameworkCore;
using Pulsar.Application.Abstractions;
using Pulsar.Application.Access;
using Pulsar.Application.Servers;
using Pulsar.Contracts;
using Pulsar.Domain.Access;
using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;
using Pulsar.Domain.Servers;
using Pulsar.Infrastructure.Identity;

namespace Pulsar.Api.Sftp;

/// <summary>An answer other than OK to an SFTP request: its status code and the message the client shows.</summary>
internal sealed class SftpException(uint code, string message) : Exception(message)
{
    public uint Code { get; } = code;

    public static SftpException NotFound(string message = "No such file or folder.") => new(SftpProtocol.NoSuchFile, message);

    public static SftpException Denied(string message = "You do not have permission to do this.") => new(SftpProtocol.PermissionDenied, message);

    public static SftpException Failure(string message) => new(SftpProtocol.Failure, message);
}

/// <summary>A server as one SFTP connection sees it.</summary>
internal sealed record SftpServer(ServerId Id, NodeId NodeId, string ShortId, string Folder, DateTimeOffset CreatedAt)
{
    public AccessTarget Target => AccessTarget.ForServer(Id, NodeId);
}

/// <summary>
/// What a client path names: the root that lists the user's servers (<see cref="Server"/> null), or a path
/// (<see cref="ServerFilePath"/>) in one server's directory.
/// </summary>
internal sealed record SftpLocation(SftpServer? Server, string Path, bool CanEdit);

/// <summary>
/// One SFTP request's work: a DI scope of its own, acting as the connection's user (audits, transfers and their limits
/// as the web's), with the user's permissions read again.
/// </summary>
internal sealed class SftpOperation(AsyncServiceScope scope, EffectivePermissions permissions) : IAsyncDisposable
{
    public IServiceProvider Services => scope.ServiceProvider;

    public EffectivePermissions Permissions { get; } = permissions;

    public ServerFiles Files => Services.GetRequiredService<ServerFiles>();

    public IPulsarDbContext Db => Services.GetRequiredService<IPulsarDbContext>();

    public ValueTask DisposeAsync() => scope.DisposeAsync();
}

/// <summary>
/// The files one SFTP connection sees (docs/guides/sftp.md). A user signed in as <c>name</c> gets a read-only root with
/// one folder per server whose files they may see (<c>server.files.view</c>), named after the server and its short id;
/// as <c>name.&lt;shortId&gt;</c>, that server's directory is the root. Every request checks the permissions again
/// (<c>server.files.view</c> to read, <c>server.files.edit</c> to change) with the evaluator the web uses, scoped grants
/// included; a server the user may not see does not exist. Paths are then the web file manager's, with what SFTP hides
/// (<see cref="SftpView"/>) not found.
/// </summary>
internal sealed class SftpFileSystem(SftpConnection connection, IServiceScopeFactory scopes, TimeProvider clock)
{
    /// <summary>
    /// How often the user is read again (disabled, deleted, a new security stamp, the SSH key it signed in with deleted):
    /// the session ends then.
    /// </summary>
    private static readonly TimeSpan s_revalidation = TimeSpan.FromSeconds(15);

    private const int MaxFolderNameLength = 64;
    private long _validatedAt = clock.GetTimestamp();

    private SftpIdentity Identity => connection.Identity ?? throw new InvalidOperationException("The connection is not signed in.");

    /// <summary>A path as the client sees it, absolute: '.' and '..' resolved ('..' stops at the root), no empty segments.</summary>
    public static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var segments = new List<string>();
        foreach (var segment in path.Split('/'))
        {
            switch (segment)
            {
                case "" or ".":
                    break;
                case "..":
                    if (segments.Count > 0)
                    {
                        segments.RemoveAt(segments.Count - 1);
                    }

                    break;
                default:
                    segments.Add(segment);
                    break;
            }
        }

        return "/" + string.Join('/', segments);
    }

    /// <summary>
    /// A server's folder in the root: its name made safe for a file name (letters, digits, '-', '_' and '.', spaces as
    /// '_') and its short id, which keeps it unique: <c>My_Server-abcd2345</c>.
    /// </summary>
    public static string FolderName(string serverName, string shortId)
    {
        ArgumentNullException.ThrowIfNull(serverName);
        var safe = new StringBuilder();
        foreach (var c in serverName.Normalize(NormalizationForm.FormC))
        {
            if (safe.Length >= MaxFolderNameLength)
            {
                break;
            }

            if ((char.IsLetterOrDigit(c) && !char.IsSurrogate(c)) || c is '-' or '_' or '.')
            {
                safe.Append(c);
            }
            else if ((char.IsWhiteSpace(c) || c is '/' or '\\' or ':') && safe.Length > 0 && safe[^1] != '_')
            {
                safe.Append('_');
            }
        }

        var trimmed = safe.ToString().Trim('_', '.', '-');
        return (trimmed.Length == 0 ? "server" : trimmed) + "-" + shortId;
    }

    /// <summary>A DI scope acting as the connection's user (for work that outlives one request: an upload).</summary>
    public AsyncServiceScope CreateScope()
    {
        var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<RequestContextOverride>().Context = connection.Context;
        return scope;
    }

    /// <summary>Starts a request's work; ends the connection when its user may no longer sign in.</summary>
    public async Task<SftpOperation> BeginAsync(CancellationToken cancellationToken)
    {
        var identity = Identity;
        var scope = CreateScope();
        try
        {
            if (clock.GetElapsedTime(Interlocked.Read(ref _validatedAt)) >= s_revalidation)
            {
                await RevalidateAsync(scope.ServiceProvider, identity);
                Interlocked.Exchange(ref _validatedAt, clock.GetTimestamp());
            }

            var permissions = await scope.ServiceProvider.GetRequiredService<IPermissionEvaluator>().GetAsync(identity.UserId, cancellationToken);
            return new SftpOperation(scope, permissions);
        }
        catch
        {
            await scope.DisposeAsync();
            throw;
        }
    }

    /// <summary>What <paramref name="clientPath"/> names, checked for reading, or for a change when <paramref name="write"/>.</summary>
    public async Task<SftpLocation> ResolveAsync(SftpOperation operation, string clientPath, bool write, CancellationToken cancellationToken)
    {
        var segments = Normalize(clientPath).Split('/', StringSplitOptions.RemoveEmptyEntries);
        SftpServer? server;
        string path;
        if (Identity.ServerShortId is { } shortId)
        {
            server = await FindAsync(operation, shortId, folder: null, cancellationToken);
            path = string.Join('/', segments);
        }
        else if (segments.Length == 0)
        {
            if (write)
            {
                throw SftpException.Denied("The root lists your servers and cannot be changed.");
            }

            return new SftpLocation(null, ServerFilePath.Root, CanEdit: false);
        }
        else
        {
            var folder = segments[0];
            var dash = folder.LastIndexOf('-');
            server = dash > 0 && Server.IsShortId(folder[(dash + 1)..]) ? await FindAsync(operation, folder[(dash + 1)..], folder, cancellationToken) : null;
            path = string.Join('/', segments.Skip(1));
        }

        if (server is null || !operation.Permissions.Has(Permissions.Server.FilesView, server.Target))
        {
            // Something new at the top of the root (a folder made, a file uploaded) is a change to the root.
            throw write && Identity.ServerShortId is null && segments.Length == 1
                ? SftpException.Denied("The root lists your servers and cannot be changed.")
                : SftpException.NotFound();
        }

        var canEdit = operation.Permissions.Has(Permissions.Server.FilesEdit, server.Target);
        if (write && !canEdit)
        {
            throw SftpException.Denied();
        }

        if (ServerFilePath.Error(path, allowRoot: true) is { } error)
        {
            throw write ? SftpException.Failure(error) : SftpException.NotFound(error);
        }

        if (SftpView.IsHidden(path))
        {
            throw SftpException.NotFound();
        }

        return new SftpLocation(server, path, canEdit);
    }

    /// <summary>Checks the permission again for a file opened earlier.</summary>
    public static void Require(SftpOperation operation, SftpServer server, bool write)
    {
        if (!operation.Permissions.Has(Permissions.Server.FilesView, server.Target))
        {
            throw SftpException.NotFound();
        }

        if (write && !operation.Permissions.Has(Permissions.Server.FilesEdit, server.Target))
        {
            throw SftpException.Denied();
        }
    }

    /// <summary>The root's folders: every server whose files the user may see, by folder name.</summary>
    public static async Task<IReadOnlyList<(SftpServer Server, bool CanEdit)>> RootAsync(SftpOperation operation, CancellationToken cancellationToken)
    {
        var query = operation.Db.Servers.AsNoTracking();
        if (!operation.Permissions.HasGlobal(Permissions.Server.FilesView))
        {
            // Only the servers it may apply to are read (a user with one server on a panel of thousands).
            var nodes = operation.Permissions.NodesWith(Permissions.Server.FilesView).ToList();
            var granted = operation.Permissions.ServersWith(Permissions.Server.FilesView).ToList();
            query = query.Where(s => nodes.Contains(s.NodeId) || granted.Contains(s.Id));
        }

        var servers = await query
            .Select(s => new { s.Id, s.NodeId, s.ShortId, s.Name, s.CreatedAt })
            .ToListAsync(cancellationToken);
        return [.. servers
            .Select(s => new SftpServer(s.Id, s.NodeId, s.ShortId, FolderName(s.Name, s.ShortId), s.CreatedAt))
            .Where(s => operation.Permissions.Has(Permissions.Server.FilesView, s.Target))
            .OrderBy(s => s.Folder, FileNameOrder.Comparer)
            .Select(s => (s, operation.Permissions.Has(Permissions.Server.FilesEdit, s.Target)))];
    }

    /// <summary>Maps what the panel answered to an SFTP status.</summary>
    public static SftpException Map(DomainError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Code switch
        {
            FileErrors.NotFound or "server.not_found" => SftpException.NotFound(error.Message),
            FileErrors.AccessDenied or FileErrors.OutsideServer => SftpException.Denied(error.Message),
            _ => SftpException.Failure(error.Message),
        };
    }

    private static async Task<SftpServer?> FindAsync(SftpOperation operation, string shortId, string? folder, CancellationToken cancellationToken)
    {
        var found = await operation.Db.Servers.AsNoTracking()
            .Where(s => s.ShortId == shortId)
            .Select(s => new { s.Id, s.NodeId, s.ShortId, s.Name, s.CreatedAt })
            .SingleOrDefaultAsync(cancellationToken);
        if (found is null)
        {
            return null;
        }

        var server = new SftpServer(found.Id, found.NodeId, found.ShortId, FolderName(found.Name, found.ShortId), found.CreatedAt);

        // A folder is found by its whole current name (as listed), never by its short id alone.
        return folder is null || string.Equals(folder, server.Folder, StringComparison.OrdinalIgnoreCase) ? server : null;
    }

    private async Task RevalidateAsync(IServiceProvider services, SftpIdentity identity)
    {
        var users = services.GetRequiredService<UserManager<PulsarUser>>();
        var user = await users.FindByIdAsync(identity.UserId.Value.ToString());
        var stillValid = user is { IsDisabled: false } && string.Equals(user.SecurityStamp, identity.SecurityStamp, StringComparison.Ordinal)
            && (user.TwoFactorEnabled
                || !(await services.GetRequiredService<IPermissionEvaluator>().GetAsync(user.Id, CancellationToken.None)).RequiresTwoFactor)
            && (identity.Key is not { } key || await services.GetRequiredService<SshKeys>().ExistsAsync(identity.UserId, key, CancellationToken.None));
        if (!stillValid)
        {
            connection.Close(SshDisconnectReason.ByApplication, "Your panel session ended. Sign in again.");
            throw SftpException.Failure("Your panel session ended.");
        }
    }
}

/// <summary>How SFTP shows entries: <c>ls -l</c> long names and attributes.</summary>
internal static class SftpEntries
{
    public static void WriteAttributes(SftpWriter writer, FileEntryKind kind, long size, DateTimeOffset modified, bool writable)
    {
        var time = Seconds(modified);
        writer.UInt32(SftpProtocol.AttrSize | SftpProtocol.AttrPermissions | SftpProtocol.AttrAcModTime)
            .UInt64(kind == FileEntryKind.File ? (ulong)Math.Max(0, size) : 0)
            .UInt32(Mode(kind, writable))
            .UInt32(time)
            .UInt32(time);
    }

    public static string LongName(string name, FileEntryKind kind, long size, DateTimeOffset modified, bool writable)
    {
        var mode = Mode(kind, writable);
        var permissions = new StringBuilder(10);
        permissions.Append(kind switch { FileEntryKind.Directory => 'd', FileEntryKind.Link => 'l', _ => '-' });
        for (var shift = 6; shift >= 0; shift -= 3)
        {
            var bits = (mode >> shift) & 7;
            permissions.Append((bits & 4) != 0 ? 'r' : '-').Append((bits & 2) != 0 ? 'w' : '-').Append((bits & 1) != 0 ? 'x' : '-');
        }

        var utc = modified.UtcDateTime;
        var when = utc > DateTime.UtcNow.AddMonths(-6)
            ? utc.ToString("MMM dd HH:mm", CultureInfo.InvariantCulture)
            : utc.ToString("MMM dd  yyyy", CultureInfo.InvariantCulture);
        return string.Create(CultureInfo.InvariantCulture,
            $"{permissions}    1 pulsar   pulsar   {(kind == FileEntryKind.File ? size : 0),10} {when} {name}");
    }

    public static uint Seconds(DateTimeOffset time) => (uint)Math.Clamp(time.ToUnixTimeSeconds(), 0, uint.MaxValue);

    private static uint Mode(FileEntryKind kind, bool writable) => kind switch
    {
        FileEntryKind.Directory => 0x4000u | (writable ? 0x1EDu : 0x16Du), // drwxr-xr-x / dr-xr-xr-x
        FileEntryKind.Link => 0xA000u | 0x1FFu, // lrwxrwxrwx: listed, never followed
        _ => 0x8000u | (writable ? 0x1A4u : 0x124u), // -rw-r--r-- / -r--r--r--
    };
}
