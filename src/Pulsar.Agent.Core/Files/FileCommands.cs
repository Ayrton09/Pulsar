using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Pulsar.Agent.Core.Panel;
using Pulsar.Agent.Core.Processes;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Files;

public sealed class FileCommandOptions
{
    /// <summary>A transfer whose other end sends or takes nothing for this long is abandoned.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How long the end of a received transfer is offered to the panel while the connection is down.</summary>
    public TimeSpan ResultDelivery { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// The file manager's commands (docs/architecture/08, "File manager"): listing, folders, renames and deletions answer
/// in the ack; file content travels over HTTP transfers (docs/architecture/03, "Large transfers"), started here once the
/// agent has checked it can do what was asked and run in the background.
/// </summary>
public sealed partial class FileCommands(IServerFileManager files, PanelChannel panel, FileCommandOptions options, ILogger<FileCommands> logger) : IDisposable
{
    private readonly ConcurrentDictionary<string, Task> _transfers = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();

    public static IReadOnlyList<string> Capabilities { get; } =
    [
        AgentCapabilities.ListFiles,
        AgentCapabilities.CreateDirectory,
        AgentCapabilities.MoveFile,
        AgentCapabilities.DeleteFile,
        AgentCapabilities.SendFile,
        AgentCapabilities.ReceiveFile,
    ];

    /// <summary>The server a file command is about; null for any other command.</summary>
    public static Guid? ServerOf(AgentCommand command) => command switch
    {
        ListFilesCommand c => c.ServerId,
        CreateDirectoryCommand c => c.ServerId,
        MoveFileCommand c => c.ServerId,
        DeleteFileCommand c => c.ServerId,
        SendFileCommand c => c.ServerId,
        ReceiveFileCommand c => c.ServerId,
        _ => null,
    };

    /// <param name="busy">An operation (install, update) is working on the server: nothing may change its files.</param>
    public CommandAck Execute(CommandEnvelope envelope, ProvisionedServer server, bool busy)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var command = envelope.Command;
        // Where SteamCMD finds the Steam login the agent lends it during an install: never anyone's to read (Phase 13).
        if (PathsOf(command).Any(IsSteamLogin))
        {
            return Rejected(envelope, FileErrors.AccessDenied, "Pulsar keeps this folder to itself.");
        }

        // Pulsar's own folder (the server's SteamCMD, addon records): readable, changed by Pulsar only.
        if (command is not (ListFilesCommand or SendFileCommand) && PathsOf(command).Any(IsPulsarFolder))
        {
            return Rejected(envelope, FileErrors.AccessDenied, "Only Pulsar changes this folder.");
        }

        if (busy && command is not (ListFilesCommand or SendFileCommand))
        {
            return new CommandAck(envelope.CommandId, AckStatus.Busy, "server.busy", "An operation is changing the server's files; try again when it ends.");
        }

        try
        {
            switch (command)
            {
                case ListFilesCommand list:
                    return Accepted(envelope, files.List(server, list.Path));
                case CreateDirectoryCommand create:
                    files.CreateDirectory(server, create.Path);
                    return Accepted(envelope);
                case MoveFileCommand move:
                    files.Move(server, move.Path, move.Destination);
                    return Accepted(envelope);
                case DeleteFileCommand delete:
                    files.Delete(server, delete.Path);
                    return Accepted(envelope);
                case SendFileCommand send:
                    return Send(envelope, server, send);
                case ReceiveFileCommand receive:
                    return Receive(envelope, server, receive);
                default:
                    return new CommandAck(envelope.CommandId, AckStatus.UnsupportedCommand, "command.unsupported", null);
            }
        }
        catch (FileManagerException ex)
        {
            return Rejected(envelope, ex.Code, ex.Message);
        }
    }

    /// <summary>
    /// Whether <paramref name="path"/> is in (or is) the server's SteamCMD <c>config</c> folder, whatever its case or
    /// the short name it starts with (see <see cref="IsPulsarFolder"/>). A first check, by name: the file manager also
    /// refuses whatever it opens there, by whichever name or link it got there.
    /// </summary>
    public static bool IsSteamLogin(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var segments = path.Replace('\\', '/').Trim('/').Split('/');
        if (ShortName().IsMatch(segments[0]))
        {
            segments[0] = ".pulsar";
        }

        return ServerFileRules.IsSteamLoginFolder(string.Join('/', segments));
    }

    /// <summary>
    /// Whether <paramref name="path"/> is in (or is) the server's <c>.pulsar</c> folder, whatever its case. A first
    /// segment shaped like an 8.3 short name (<c>PULSAR~1</c>) counts too: on volumes that keep short names it may be
    /// <c>.pulsar</c>'s, and the name alone cannot tell.
    /// </summary>
    public static bool IsPulsarFolder(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var first = path.Replace('\\', '/').Trim('/').Split('/')[0];
        return first.Equals(".pulsar", StringComparison.OrdinalIgnoreCase) || IsShortName(first);
    }

    /// <summary>Whether <paramref name="name"/> (one path segment) is shaped like an 8.3 short name (<c>PULSAR~1</c>).</summary>
    public static bool IsShortName(string name) => ShortName().IsMatch(name);

    [GeneratedRegex("^[^.~]{1,6}~[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ShortName();

    private static IEnumerable<string> PathsOf(AgentCommand command) => command switch
    {
        ListFilesCommand c => [c.Path],
        CreateDirectoryCommand c => [c.Path],
        MoveFileCommand c => [c.Path, c.Destination],
        DeleteFileCommand c => [c.Path],
        SendFileCommand c => [c.Path],
        ReceiveFileCommand c => [c.Path],
        _ => [],
    };

    public void Dispose() => _stopping.Dispose();

    /// <summary>Cancels the transfers still running and waits for them to clean up.</summary>
    public async Task StopAsync()
    {
        await _stopping.CancelAsync();
        await Task.WhenAll(_transfers.Values);
    }

    private CommandAck Send(CommandEnvelope envelope, ProvisionedServer server, SendFileCommand command) =>
        Send(envelope, command.Ticket, command.Path, () => files.OpenRead(server, command.Path, command.MaxSize));

    /// <summary>
    /// Sends what <paramref name="open"/> opens (a server file, or a backup's archive) to the panel in the background, once
    /// the ticket is known to be usable; the ack carries what was opened. Exceptions of <paramref name="open"/> pass through.
    /// </summary>
    /// <param name="label">Names the content in the agent's log.</param>
    public CommandAck Send(CommandEnvelope envelope, string ticket, string label, Func<OpenedFile> open)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(open);
        if (!TransferTicket.IsWellFormed(ticket) || _transfers.ContainsKey(ticket))
        {
            return Rejected(envelope, "files.invalid_ticket", "The transfer ticket is malformed or already used.");
        }

        var opened = open();
        Track(ticket, () => SendAsync(ticket, label, opened));
        return Accepted(envelope, opened.Info);
    }

    private CommandAck Receive(CommandEnvelope envelope, ProvisionedServer server, ReceiveFileCommand command)
    {
        if (!TransferTicket.IsWellFormed(command.Ticket) || _transfers.ContainsKey(command.Ticket))
        {
            return Rejected(envelope, "files.invalid_ticket", "The transfer ticket is malformed or already used.");
        }

        if (command.Size < 0)
        {
            return Rejected(envelope, FileErrors.TransferFailed, "The size cannot be negative.");
        }

        var pending = files.Create(server, command.Path, command.Size, command.Overwrite, command.ExpectedVersion);
        Track(command.Ticket, () => ReceiveAsync(command.Ticket, command.Path, command.Size, pending));
        return Accepted(envelope);
    }

    private void Track(string ticket, Func<Task> transfer)
    {
        var task = Task.Run(transfer);
        _transfers[ticket] = task;
        _ = task.ContinueWith(_ => _transfers.TryRemove(ticket, out var _), TaskScheduler.Default);
    }

    private async Task SendAsync(string ticket, string path, OpenedFile opened)
    {
        using (opened)
        {
            try
            {
                using var http = panel.CreateHttpClient() ?? throw new HttpRequestException("Not connected to the panel.");
                var size = opened.Info.Entry.Size;
                using var idle = new IdleTimeout(options.IdleTimeout, _stopping.Token);
                using var content = new StreamContent(new BoundedStream(opened.Content, size, idle));
                content.Headers.ContentLength = size;
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                using var response = await http.PutAsync(new Uri(AgentProtocol.Transfer(ticket).TrimStart('/'), UriKind.Relative), content, idle.Token);
                if (!response.IsSuccessStatusCode)
                {
                    LogSendRefused(path, (int)response.StatusCode);
                    return;
                }

                LogSent(path, size);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                LogTransferFailed(path, ex.Message);
            }
        }
    }

    private async Task ReceiveAsync(string ticket, string path, long size, PendingFile pending)
    {
        TransferResult result;
        using (pending)
        {
            try
            {
                using var http = panel.CreateHttpClient() ?? throw new HttpRequestException("Not connected to the panel.");
                using var idle = new IdleTimeout(options.IdleTimeout, _stopping.Token);
                using var response = await http.GetAsync(new Uri(AgentProtocol.Transfer(ticket).TrimStart('/'), UriKind.Relative),
                    HttpCompletionOption.ResponseHeadersRead, idle.Token);
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"The panel answered {(int)response.StatusCode}.");
                }

                await using (var body = await response.Content.ReadAsStreamAsync(idle.Token))
                {
                    var buffer = new byte[81920];
                    long received = 0;
                    while (true)
                    {
                        idle.Reset();
                        var read = await body.ReadAsync(buffer, idle.Token);
                        if (read == 0)
                        {
                            break;
                        }

                        received += read;
                        if (received > size)
                        {
                            throw new FileManagerException(FileErrors.TransferFailed, $"More than the {size} bytes announced arrived.");
                        }

                        await pending.Content.WriteAsync(buffer.AsMemory(0, read), idle.Token);
                    }
                }

                var file = pending.Commit();
                LogReceived(path, size);
                result = new TransferResult(ticket, null, null, file);
            }
            catch (FileManagerException ex)
            {
                LogTransferFailed(path, ex.Message);
                result = new TransferResult(ticket, ex.Code, ex.Message, null);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                LogTransferFailed(path, ex.Message);
                result = new TransferResult(ticket, FileErrors.TransferFailed, "The transfer was interrupted.", null);
            }
        }

        await ReportAsync(result);
    }

    /// <summary>The panel is waiting for this result to answer the browser; offered again while the connection is down.</summary>
    private async Task ReportAsync(TransferResult result)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        deadline.CancelAfter(options.ResultDelivery);
        try
        {
            while (!await panel.DeliverAsync(nameof(IAgentHub.TransferCompleted), result, deadline.Token))
            {
                await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // The panel has given up on it by now (or the agent is stopping); the file is in place or not either way.
        }
    }

    private static CommandAck Accepted(CommandEnvelope envelope, CommandResult? result = null) =>
        new(envelope.CommandId, AckStatus.Accepted, null, null, result);

    private static CommandAck Rejected(CommandEnvelope envelope, string code, string message) =>
        new(envelope.CommandId, AckStatus.Rejected, code, message);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sent {Path} ({Bytes} bytes) to the panel")]
    private partial void LogSent(string path, long bytes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Received {Path} ({Bytes} bytes) from the panel")]
    private partial void LogReceived(string path, long bytes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The panel refused the content of {Path} ({Status})")]
    private partial void LogSendRefused(string path, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Transfer of {Path} failed: {Reason}")]
    private partial void LogTransferFailed(string path, string reason);

    /// <summary>A cancellation that fires when nothing moved for a while (reset at every read or write).</summary>
    private sealed class IdleTimeout : IDisposable
    {
        private readonly TimeSpan _timeout;
        private readonly CancellationTokenSource _source;

        public IdleTimeout(TimeSpan timeout, CancellationToken stopping)
        {
            _timeout = timeout;
            _source = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            _source.CancelAfter(timeout);
        }

        public CancellationToken Token => _source.Token;

        public void Reset() => _source.CancelAfter(_timeout);

        public void Dispose() => _source.Dispose();
    }

    /// <summary>Reads at most the announced size (a log the game is writing grows while it is sent).</summary>
    private sealed class BoundedStream(Stream inner, long length, IdleTimeout idle) : Stream
    {
        private readonly long _length = length;
        private long _remaining = length;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _length;

        public override long Position
        {
            get => _length - _remaining;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            idle.Reset();
            var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
            _remaining -= read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            idle.Reset();
            var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken);
            _remaining -= read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
