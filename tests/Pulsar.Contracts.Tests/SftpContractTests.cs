using System.Text;

namespace Pulsar.Contracts.Tests;

/// <summary>What SFTP adds to the file commands (Phase 17): additive fields older agents and panels never see set.</summary>
public sealed class SftpContractTests
{
    private const string Server = "0192f1a0-0000-7000-8000-000000000001";

    [Fact]
    public void AnOlderPanelsFileCommandsMeanWhatTheyAlwaysMeant()
    {
        var list = ProtocolJson.Deserialize<ListFilesCommand>(Encoding.UTF8.GetBytes($$"""{"serverId":"{{Server}}","path":"cfg"}"""));
        Assert.False(list.Paged);
        Assert.Null(list.After);

        var move = ProtocolJson.Deserialize<MoveFileCommand>(Encoding.UTF8.GetBytes($$"""{"serverId":"{{Server}}","path":"a","destination":"b"}"""));
        Assert.False(move.Replace);

        var send = ProtocolJson.Deserialize<SendFileCommand>(Encoding.UTF8.GetBytes($$"""{"serverId":"{{Server}}","path":"a","ticket":"t","maxSize":5}"""));
        Assert.Equal(0, send.Offset);

        var receive = ProtocolJson.Deserialize<ReceiveFileCommand>(Encoding.UTF8.GetBytes(
            $$"""{"serverId":"{{Server}}","path":"a","ticket":"t","size":5,"overwrite":true,"expectedVersion":null}"""));
        Assert.False(receive.Streamed);
        Assert.False(receive.Append);

        var listing = Assert.IsType<FileListResult>(ProtocolJson.Deserialize<CommandResult>("""{"$type":"files.list","entries":[],"truncated":false}"""u8));
        Assert.Null(listing.Next);
    }

    [Fact]
    public void TheNewFieldsAndCommandsTravel()
    {
        var id = Guid.CreateVersion7();
        AgentCommand[] commands =
        [
            new ListFilesCommand(id, "cfg", Paged: true, After: "b.cfg"),
            new MoveFileCommand(id, "a.filepart", "a", Replace: true),
            new SendFileCommand(id, "a", TransferTicket.New(), long.MaxValue, Offset: 42),
            new ReceiveFileCommand(id, "a", TransferTicket.New(), 1 << 30, false, null, Streamed: true, Append: true),
            new StatFileCommand(id, "a"),
            new SetFileModifiedCommand(id, "a", new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.FromHours(2))),
        ];

        foreach (var command in commands)
        {
            Assert.Equal(command, ProtocolJson.Deserialize<AgentCommand>(ProtocolJson.Serialize(command)));
        }

        CommandResult page = new FileListResult([new FileEntry("a", FileEntryKind.File, 1, DateTimeOffset.UnixEpoch)], false, 0, Next: "a");
        Assert.Equal("a", Assert.IsType<FileListResult>(ProtocolJson.Deserialize<CommandResult>(ProtocolJson.Serialize(page))).Next);
    }

    [Fact]
    public void OnlyAnAgentThatAnnouncesSftpGetsItsCommands()
    {
        Assert.Equal(AgentCapabilities.StatFile, AgentCapabilities.Of(new StatFileCommand(Guid.Empty, "a")));
        Assert.Equal(AgentCapabilities.SetFileModified, AgentCapabilities.Of(new SetFileModifiedCommand(Guid.Empty, "a", DateTimeOffset.UnixEpoch)));
        Assert.Equal("server.files_stat", StatFileCommand.Type);
        Assert.Equal("server.files_set_modified", SetFileModifiedCommand.Type);
        Assert.Equal("server.files_sftp", AgentCapabilities.SftpFiles);
    }

    [Fact]
    public void TheTicketOfAStreamedUploadStaysOutOfLogs()
    {
        var ticket = TransferTicket.New();
        var printed = new ReceiveFileCommand(Guid.Empty, "a", ticket, 5, false, null, Streamed: true, Append: true).ToString();

        Assert.DoesNotContain(ticket, printed, StringComparison.Ordinal);
        Assert.Contains("Streamed = True, Append = True", printed, StringComparison.Ordinal);
        Assert.DoesNotContain(ticket, new SendFileCommand(Guid.Empty, "a", ticket, 5, Offset: 3).ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("server.cfg", false)]
    [InlineData("cfg/.pulsar", false)]
    [InlineData(".pulsarx", false)]
    [InlineData("MODS~1", false)]
    [InlineData(".pulsar", true)]
    [InlineData(".PULSAR/steamcmd/steamcmd.exe", true)]
    [InlineData("PULSAR~1", true)]
    [InlineData("pulsar~12/x", true)]
    [InlineData("odd\u202Egnp.exe", true)]
    [InlineData("maps/zero\u200Bwidth.bsp", true)]
    public void SftpHidesPulsarsFolderAndNamesItCannotShowMarked(string path, bool hidden)
    {
        Assert.Equal(hidden, SftpView.IsHidden(path));
    }

    [Fact]
    public void PagesAreOrderedByNameIgnoringCaseThenExactly()
    {
        string[] names = ["b", "a", "B", "A", "ä", "_x"];

        Assert.Equal(["A", "a", "B", "b", "_x", "ä"], names.Order(FileNameOrder.Comparer));
        Assert.True(FileNameOrder.Compare("README", "Readme") < 0);
        Assert.Equal(0, FileNameOrder.Compare("x", "x"));
    }
}
