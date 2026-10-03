using Microsoft.Extensions.Logging.Abstractions;
using Pulsar.Agent.Core.Files;
using Pulsar.Agent.Core.Panel;
using Pulsar.Agent.Core.Processes;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Tests;

/// <summary>The file manager's commands on the agent: answers in the ack, refusals with their codes, transfers checked first.</summary>
public sealed class FileCommandsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pulsar-file-commands-").FullName;
    private readonly ProvisionedServer _server;
    private readonly FileCommands _commands = new(new DirectServerFileManager(), new PanelChannel(), new FileCommandOptions(),
        NullLogger<FileCommands>.Instance);

    public FileCommandsTests() => _server = new ProvisionedServer(_root, ProcessIdentity.CurrentUser);

    public void Dispose()
    {
        _commands.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void AListingTravelsInTheAck()
    {
        File.WriteAllText(Path.Combine(_root, "server.cfg"), "x");

        var ack = Execute(new ListFilesCommand(Guid.NewGuid(), ServerFilePath.Root));

        Assert.Equal(AckStatus.Accepted, ack.Status);
        var listing = Assert.IsType<FileListResult>(ack.Result);
        Assert.Equal("server.cfg", Assert.Single(listing.Entries).Name);
    }

    [Fact]
    public void RefusalsCarryTheFileManagersCode()
    {
        var ack = Execute(new DeleteFileCommand(Guid.NewGuid(), "missing.cfg"));
        Assert.Equal((AckStatus.Rejected, FileErrors.NotFound), (ack.Status, ack.ErrorCode));

        ack = Execute(new MoveFileCommand(Guid.NewGuid(), "../outside", "inside"));
        Assert.Equal((AckStatus.Rejected, FileErrors.InvalidPath), (ack.Status, ack.ErrorCode));
    }

    [Fact]
    public void WhileAnOperationRunsFilesCanBeReadButNotChanged()
    {
        File.WriteAllText(Path.Combine(_root, "server.cfg"), "x");

        Assert.Equal(AckStatus.Accepted, Execute(new ListFilesCommand(Guid.NewGuid(), ServerFilePath.Root), busy: true).Status);
        Assert.Equal(AckStatus.Busy, Execute(new CreateDirectoryCommand(Guid.NewGuid(), "cfg"), busy: true).Status);
        Assert.Equal(AckStatus.Busy, Execute(new DeleteFileCommand(Guid.NewGuid(), "server.cfg"), busy: true).Status);
        Assert.Equal(AckStatus.Busy, Execute(new ReceiveFileCommand(Guid.NewGuid(), "a.cfg", TransferTicket.New(), 1, false, null), busy: true).Status);
        Assert.True(File.Exists(Path.Combine(_root, "server.cfg")));
        Assert.False(Directory.Exists(Path.Combine(_root, "cfg")));
    }

    [Fact]
    public void ATransferStartsOnlyWhenTheAgentCanDoWhatItAsks()
    {
        File.WriteAllText(Path.Combine(_root, "server.cfg"), "hostname x");

        Assert.Equal("files.invalid_ticket", Execute(new SendFileCommand(Guid.NewGuid(), "server.cfg", "short", 100)).ErrorCode);
        Assert.Equal(FileErrors.TooLarge, Execute(new SendFileCommand(Guid.NewGuid(), "server.cfg", TransferTicket.New(), 5)).ErrorCode);
        Assert.Equal(FileErrors.Exists, Execute(new ReceiveFileCommand(Guid.NewGuid(), "server.cfg", TransferTicket.New(), 1, false, null)).ErrorCode);

        // Accepted with what is about to be sent; with no panel connection the transfer then just ends.
        var ack = Execute(new SendFileCommand(Guid.NewGuid(), "server.cfg", TransferTicket.New(), 100));
        Assert.Equal(AckStatus.Accepted, ack.Status);
        Assert.Equal(10, Assert.IsType<FileEntryResult>(ack.Result).Entry.Size);
    }

    [Fact]
    public async Task AReceiveThatCannotReachThePanelLeavesNothingBehind()
    {
        var ack = Execute(new ReceiveFileCommand(Guid.NewGuid(), "new.cfg", TransferTicket.New(), 3, false, null));
        Assert.Equal(AckStatus.Accepted, ack.Status);

        await _commands.StopAsync();

        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    private CommandAck Execute(AgentCommand command, bool busy = false) =>
        _commands.Execute(new CommandEnvelope(AgentProtocol.CurrentVersion, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddSeconds(30), null, null, command), _server, busy);
}
