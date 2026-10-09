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
    public void TheSteamLoginSteamCmdIsLentIsNobodysToReadOrChange()
    {
        var config = Path.Combine(_root, ".pulsar", "steamcmd", "config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "config.vdf"), "a cached login");

        foreach (var command in new AgentCommand[]
        {
            new SendFileCommand(Guid.NewGuid(), ".pulsar/steamcmd/config/config.vdf", TransferTicket.New(), 1024),
            new SendFileCommand(Guid.NewGuid(), ".PULSAR/SteamCMD/Config/config.vdf", TransferTicket.New(), 1024),
            new SendFileCommand(Guid.NewGuid(), "PULSAR~1/steamcmd/config/config.vdf", TransferTicket.New(), 1024),
            new ListFilesCommand(Guid.NewGuid(), ".pulsar/steamcmd/config"),
            new ListFilesCommand(Guid.NewGuid(), "pulsar~2/steamcmd/config"),
            new MoveFileCommand(Guid.NewGuid(), ".pulsar/steamcmd/config/config.vdf", "mine.vdf"),
            new MoveFileCommand(Guid.NewGuid(), "planted.vdf", ".pulsar/steamcmd/config/config.vdf"),
            new DeleteFileCommand(Guid.NewGuid(), ".pulsar/steamcmd/config/config.vdf"),
        })
        {
            var ack = Execute(command, busy: true);
            Assert.Equal((AckStatus.Rejected, FileErrors.AccessDenied), (ack.Status, ack.ErrorCode));
        }

        // The rest of SteamCMD's folder can be read.
        Assert.Equal(AckStatus.Accepted, Execute(new ListFilesCommand(Guid.NewGuid(), ".pulsar/steamcmd")).Status);
        Assert.False(FileCommands.IsSteamLogin(".pulsar/steamcmd/configs"));
    }

    [Fact]
    public void PulsarsOwnFolderCanBeReadButNotChanged()
    {
        var steamCmd = Path.Combine(_root, ".pulsar", "steamcmd");
        Directory.CreateDirectory(steamCmd);
        File.WriteAllText(Path.Combine(steamCmd, "steamcmd.exe"), "Valve's");
        File.WriteAllText(Path.Combine(_root, "version.dll"), "planted");

        foreach (var command in new AgentCommand[]
        {
            new CreateDirectoryCommand(Guid.NewGuid(), ".pulsar/steamcmd/package"),
            new MoveFileCommand(Guid.NewGuid(), ".pulsar/steamcmd/steamcmd.exe", "steamcmd.exe"),
            new MoveFileCommand(Guid.NewGuid(), "version.dll", ".pulsar/steamcmd/version.dll"),
            new MoveFileCommand(Guid.NewGuid(), ".pulsar", "elsewhere"),
            new DeleteFileCommand(Guid.NewGuid(), ".pulsar/steamcmd/steamcmd.exe"),
            new DeleteFileCommand(Guid.NewGuid(), ".Pulsar"),
            new DeleteFileCommand(Guid.NewGuid(), "PULSAR~1/steamcmd/steamcmd.exe"),
            new MoveFileCommand(Guid.NewGuid(), "version.dll", "PULSAR~1/steamcmd/version.dll"),
            new ReceiveFileCommand(Guid.NewGuid(), ".pulsar/steamcmd/version.dll", TransferTicket.New(), 7, false, null),
            new ReceiveFileCommand(Guid.NewGuid(), ".PULSAR/SteamCMD/steamcmd.exe", TransferTicket.New(), 7, true, null),
        })
        {
            var ack = Execute(command);
            Assert.Equal((AckStatus.Rejected, FileErrors.AccessDenied), (ack.Status, ack.ErrorCode));
        }

        Assert.Equal("Valve's", File.ReadAllText(Path.Combine(steamCmd, "steamcmd.exe")));
        Assert.False(File.Exists(Path.Combine(steamCmd, "version.dll")));
        Assert.Equal(AckStatus.Accepted, Execute(new ListFilesCommand(Guid.NewGuid(), ".pulsar/steamcmd")).Status);
        Assert.Equal(AckStatus.Accepted, Execute(new SendFileCommand(Guid.NewGuid(), ".pulsar/steamcmd/steamcmd.exe", TransferTicket.New(), 1024)).Status);
        Assert.Equal(AckStatus.Accepted, Execute(new CreateDirectoryCommand(Guid.NewGuid(), ".pulsarx")).Status);
    }

    [Theory]
    [InlineData("PULSAR~1", true)]
    [InlineData("pulsar~12/steamcmd", true)]
    [InlineData("AB~3/x.cfg", true)]
    [InlineData("PULSAR~", false)]
    [InlineData("~1", false)]
    [InlineData("TOOLONG~1", false)]
    [InlineData("a.b~1", false)]
    [InlineData("cfg/PULSAR~1", false)]
    public void ShortNamesCouldBePulsarsFolderAndAreTreatedAsIt(string path, bool pulsars)
    {
        // Windows names a folder ".pulsar" PULSAR~1 too (an 8.3 alias) on volumes that keep short names: whichever
        // folder such a name stands for, the file manager cannot tell from the name alone.
        Assert.Equal(pulsars, FileCommands.IsPulsarFolder(path));
        Assert.Equal(pulsars, FileCommands.IsSteamLogin(ServerFilePath.Join(path.Split('/')[0], "steamcmd/config/config.vdf")));
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
