using Pulsar.Application.Nodes;
using Pulsar.Contracts;

namespace Pulsar.Api.Tests.Agents;

/// <summary>
/// Text and lists an agent chooses are bounded where they enter the panel, before browsers, the audit log, the panel's
/// log and notifications see them (docs/architecture/08, T32).
/// </summary>
public sealed class AgentInputTests
{
    [Theory]
    [InlineData("files.not_found", "files.not_found")]
    [InlineData("steamcmd.exit_0x6", "steamcmd.exit_0x6")]
    [InlineData("Files.NotFound", AgentInput.InvalidErrorCode)]
    [InlineData("files.not_found\nINF forged line", AgentInput.InvalidErrorCode)]
    [InlineData("files not found", AgentInput.InvalidErrorCode)]
    [InlineData("", AgentInput.InvalidErrorCode)]
    [InlineData(null, null)]
    public void ErrorCodesKeepTheProtocolsShapeOrBecomeAGenericOne(string? code, string? expected)
    {
        Assert.Equal(expected, AgentInput.ErrorCode(code));
    }

    [Fact]
    public void ALongErrorCodeBecomesTheGenericOne()
    {
        Assert.Equal(AgentInput.InvalidErrorCode, AgentInput.ErrorCode(new string('a', AgentInput.MaxErrorCodeLength + 1)));
        Assert.Equal(new string('a', AgentInput.MaxErrorCodeLength), AgentInput.ErrorCode(new string('a', AgentInput.MaxErrorCodeLength)));
    }

    [Fact]
    public void AnAcknowledgementLosesWhatWouldForgeALogLine()
    {
        var ack = AgentInput.Ack(new CommandAck(Guid.NewGuid(), AckStatus.Rejected, "x\r\n[12:00:00 WRN] forged", "line one\r\nline two\u001b[31m" + new string('m', 5000)));

        Assert.Equal(AgentInput.InvalidErrorCode, ack.ErrorCode);
        Assert.DoesNotContain(ack.Message!, char.IsControl);
        Assert.StartsWith("line one", ack.Message, StringComparison.Ordinal);
        Assert.True(ack.Message!.Length <= AgentInput.MaxMessageLength);
    }

    [Fact]
    public void ProgressHasAShortStageWithoutControlOrDirectionCharacters()
    {
        var progress = AgentInput.Progress(new OperationProgress(Guid.NewGuid(), "steam\u202Ecmd\n" + new string('s', 500), 50, null));

        Assert.True(progress.Stage.Length <= AgentInput.MaxStageLength);
        Assert.StartsWith("steamcmd", progress.Stage, StringComparison.Ordinal);
        Assert.Equal(50, progress.Percent);
    }

    [Fact]
    public void ACompletedOperationOrTransferKeepsOnlyACodeOfTheProtocolsShape()
    {
        Assert.Equal(AgentInput.InvalidErrorCode,
            AgentInput.Completed(new OperationCompleted(Guid.NewGuid(), null, OperationOutcome.Failed, "BAD CODE", "message")).ErrorCode);
        Assert.Equal("files.too_large", AgentInput.Transfer(new TransferResult("ticket", "files.too_large", null, null)).ErrorCode);
        Assert.Equal(AgentInput.InvalidErrorCode, AgentInput.Transfer(new TransferResult("ticket", "files.too_large\n", null, null)).ErrorCode);
    }

    [Fact]
    public void AMegabyteMessageCostsNoMoreThanWhatIsKept()
    {
        // A line break up front, so it is not kept as it is: cleaned rune by rune, and cut once the message is full.
        var message = "\n" + string.Concat(Enumerable.Repeat("message \U0001F600 ", 1024 * 1024 / 11));
        _ = AgentInput.Text(message, AgentInput.MaxMessageLength);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var text = AgentInput.Text(message, AgentInput.MaxMessageLength);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 16 * 1024, $"{allocated} bytes allocated");
        Assert.StartsWith("message \U0001F600 message", text, StringComparison.Ordinal);
        Assert.True(text.Length <= AgentInput.MaxMessageLength);
        Assert.False(char.IsHighSurrogate(text[^1]));
    }

    [Fact]
    public void TextWithNothingToRemoveIsKeptAsItIs()
    {
        const string message = "The server could not start: port 27015 is in use (ñandú).";

        Assert.Same(message, AgentInput.Text(message, AgentInput.MaxMessageLength));
        Assert.Equal("�x", AgentInput.Text("\uD800x", AgentInput.MaxMessageLength)); // half of a pair is not text
        Assert.Equal(string.Empty, AgentInput.Text(null, 10));
    }

    [Fact]
    public void AnAddonReleaseIsACleanShortTag()
    {
        Assert.Equal("v1.0.376", AgentInput.AddonRelease("v1.0.376"));
        Assert.Equal("v1.0forged", AgentInput.AddonRelease("v1.0\r\n\U0000202Eforged\U0000200B"));
        Assert.Equal(128, AgentInput.AddonRelease(new string('r', 5000))!.Length);
        Assert.Null(AgentInput.AddonRelease(null));
    }

    [Fact]
    public void AnAddonsResultKeepsCleanReleasesAndOperationsCarryThemThrough()
    {
        var result = new AddonsResult([new AddonRelease("oxide", "2.0\n.1\U0000200B"), new AddonRelease("carbon", null), null!]);

        var completed = AgentInput.Completed(new OperationCompleted(Guid.NewGuid(), null, OperationOutcome.Succeeded, null, null, result));

        var addons = Assert.IsType<AddonsResult>(completed.Result).Addons;
        Assert.Equal(2, addons.Count);
        Assert.Equal(new AddonRelease("oxide", "2.0.1"), addons[0]);
        Assert.Equal(new AddonRelease("carbon", null), addons[1]);
    }

    [Theory]
    [InlineData("server.cfg", true)]
    [InlineData("nombre \U0000202Eraro.cfg", true)] // format characters are real names the panel can address
    [InlineData("a/b.cfg", false)]
    [InlineData("a\\b.cfg", false)]
    [InlineData("a:b", false)]
    [InlineData("nul.txt", false)]
    [InlineData("..", false)]
    [InlineData("bad\nname", false)]
    [InlineData("trailing.", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void AFileNameIsOneSegmentThePanelCouldAddress(string? name, bool expected)
    {
        Assert.Equal(expected, AgentInput.IsFileName(name));
    }

    [Fact]
    public void AListingSkipsEntriesWhoseNamesThePanelCannotAddressAndCountsThem()
    {
        var modified = DateTimeOffset.UnixEpoch;
        var listing = new FileListResult(
        [
            new FileEntry("server.cfg", FileEntryKind.File, 1, modified),
            new FileEntry("a/../../b", FileEntryKind.File, 1, modified),
            new FileEntry("forged\nline", FileEntryKind.Directory, 0, modified),
            new FileEntry("odd \U0000202Ename", FileEntryKind.File, 1, modified),
            new FileEntry("negative", FileEntryKind.File, -1, modified),
            new FileEntry("kind", (FileEntryKind)99, 1, modified),
            null!,
        ], false);

        var cleaned = Assert.IsType<FileListResult>(AgentInput.Result(listing));

        Assert.Equal(["server.cfg", "odd \U0000202Ename"], cleaned.Entries.Select(e => e.Name));
        Assert.Equal(5, cleaned.Skipped);
        Assert.False(cleaned.Truncated); // the cap is not what hid them
        var clean = Assert.IsType<FileListResult>(AgentInput.Result(new FileListResult(listing.Entries.Take(1).ToList(), false, Skipped: 999)));
        Assert.Equal(0, clean.Skipped); // the count is the panel's, not the agent's
        Assert.False(clean.Truncated);
    }

    [Fact]
    public void AListingHasAtMostTheEntriesTheProtocolAllows()
    {
        var entries = Enumerable.Range(0, FileLimits.MaxListEntries + 50)
            .Select(i => new FileEntry($"f{i}", FileEntryKind.File, 1, DateTimeOffset.UnixEpoch)).ToList();

        var cleaned = Assert.IsType<FileListResult>(AgentInput.Result(new FileListResult(entries, false)));

        Assert.Equal(FileLimits.MaxListEntries, cleaned.Entries.Count);
        Assert.True(cleaned.Truncated);
        Assert.Equal(0, cleaned.Skipped);
    }

    [Theory]
    [InlineData("0-0", true)]
    [InlineData("12-8dcb9a1f2e3d4c5", true)]
    [InlineData("12-8DCB", false)]
    [InlineData("12", false)]
    [InlineData("1\"-2", false)]
    [InlineData("1-2\n", false)]
    [InlineData("1-2\U0000200B", false)]
    [InlineData("\U0000FF11-2", false)]
    [InlineData("-2", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void AFileVersionIsTheShapeTheAgentMakesAndSafeForAHeader(string? version, bool expected)
    {
        Assert.Equal(expected, FileVersion.IsWellFormed(version));
    }

    [Fact]
    public void AnAcknowledgementOnlyCarriesAFileThePanelCanUse()
    {
        var entry = new FileEntry("a.cfg", FileEntryKind.File, 1, DateTimeOffset.UnixEpoch);
        CommandAck Ack(CommandResult result) => AgentInput.Ack(new CommandAck(Guid.NewGuid(), AckStatus.Accepted, null, null, result));

        Assert.IsType<FileEntryResult>(Ack(new FileEntryResult(entry, "1-2")).Result);
        Assert.Null(Ack(new FileEntryResult(entry, "1\"; x=\"2")).Result); // would break out of the ETag's quotes
        Assert.Null(Ack(new FileEntryResult(entry, "1-2\r\nSet-Cookie: x=y")).Result);
        Assert.Null(Ack(new FileEntryResult(entry with { Name = "../b" }, "1-2")).Result);
        Assert.Null(Ack(new FileEntryResult(null!, "1-2")).Result);
    }

    [Fact]
    public void ATransferThatReportsAFileThePanelCannotUseFailsInsteadOfPassingItOn()
    {
        var entry = new FileEntry("a.cfg", FileEntryKind.File, 1, DateTimeOffset.UnixEpoch);
        var good = new FileEntryResult(entry, "1-2");

        Assert.Same(good, AgentInput.Transfer(new TransferResult("t", null, null, good)).File);

        var bad = AgentInput.Transfer(new TransferResult("t", null, null, new FileEntryResult(entry, "1\"2")));
        Assert.Null(bad.File);
        Assert.Equal(FileErrors.Failed, bad.ErrorCode);
        Assert.NotNull(bad.Message);

        var named = AgentInput.Transfer(new TransferResult("t", null, null, new FileEntryResult(entry with { Name = "x\ny" }, "1-2")));
        Assert.Null(named.File);
        Assert.Equal(FileErrors.Failed, named.ErrorCode);

        // A failure keeps its own code; whatever file it carries is dropped when unusable.
        var failed = AgentInput.Transfer(new TransferResult("t", FileErrors.Changed, "changed", new FileEntryResult(entry, "bad")));
        Assert.Null(failed.File);
        Assert.Equal(FileErrors.Changed, failed.ErrorCode);
    }

    [Fact]
    public void NodeMetricsListAFewDrivesWithShortNames()
    {
        var disks = Enumerable.Range(0, 100)
            .Select(i => new DiskUsage($"D:\\{i}\u0007\n" + new string('x', 300), 100, 50, ["servers", "backups", "x\nforged", .. Enumerable.Repeat("more", 50)]))
            .ToList();

        var metrics = AgentInput.Metrics(new NodeMetrics(DateTimeOffset.UtcNow, 5, 1, 2, TimeSpan.FromHours(1), disks));

        Assert.Equal(AgentInput.MaxDisks, metrics.Disks!.Count);
        Assert.All(metrics.Disks, d =>
        {
            Assert.True(d.Root.Length <= AgentInput.MaxDiskRootLength);
            Assert.DoesNotContain(d.Root, char.IsControl);
            Assert.True(d.Uses.Count <= AgentInput.MaxDiskUses);
            Assert.All(d.Uses, u => Assert.DoesNotContain(u, char.IsControl));
        });
        Assert.Equal("D:\\0", metrics.Disks[0].Root[..4]);
        Assert.Null(AgentInput.Metrics(new NodeMetrics(DateTimeOffset.UtcNow, 5, 1, 2, TimeSpan.FromHours(1))).Disks);
    }
}
