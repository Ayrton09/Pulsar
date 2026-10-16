using Pulsar.Agent.Core.Files;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Tests;

public sealed class ServerFileRulesTests
{
    [Fact]
    public void AListingKeepsTheFirstEntriesInOrderWithoutHoldingTheWholeFolder()
    {
        var count = FileLimits.MaxListEntries * 3;
        var entries = new CountingEntries(Enumerable.Range(0, count)
            .Select(i => (i * 7919) % count)
            .Select(i => new FileEntry($"file-{i:D6}.txt", i % 1000 == 999 ? FileEntryKind.Directory : FileEntryKind.File, 1, DateTimeOffset.UnixEpoch)), watchMemory: true);

        var listing = ServerFileRules.Listing(entries);

        Assert.True(listing.Truncated);
        Assert.Equal(FileLimits.MaxListEntries, listing.Entries.Count);
        var directories = Enumerable.Range(0, count).Where(i => i % 1000 == 999).Select(i => $"file-{i:D6}.txt").ToList();
        var files = Enumerable.Range(0, count).Where(i => i % 1000 != 999).Select(i => $"file-{i:D6}.txt");
        Assert.Equal(directories.Concat(files).Take(FileLimits.MaxListEntries), listing.Entries.Select(e => e.Name));
        Assert.InRange(entries.MostHeld, 0, FileLimits.MaxListEntries + 10);
    }

    [Fact]
    public void AListingStopsReadingAFolderBeyondTheScanLimit()
    {
        var entries = new CountingEntries(Enumerable.Range(0, ServerFileRules.MaxListScan + 1000)
            .Select(i => new FileEntry($"f{i}", FileEntryKind.File, 0, DateTimeOffset.UnixEpoch)));

        var listing = ServerFileRules.Listing(entries);

        Assert.True(listing.Truncated);
        Assert.Equal(FileLimits.MaxListEntries, listing.Entries.Count);
        Assert.Equal(ServerFileRules.MaxListScan + 1, entries.Read);
    }

    [Fact]
    public void ASmallFolderIsListedWhole()
    {
        var listing = ServerFileRules.Listing([
            new FileEntry("b.txt", FileEntryKind.File, 1, DateTimeOffset.UnixEpoch),
            new FileEntry("A.txt", FileEntryKind.File, 1, DateTimeOffset.UnixEpoch),
            new FileEntry("cfg", FileEntryKind.Directory, 0, DateTimeOffset.UnixEpoch),
        ]);

        Assert.False(listing.Truncated);
        Assert.Equal(["cfg", "A.txt", "b.txt"], listing.Entries.Select(e => e.Name));
    }

    [Fact]
    public void NamesThePanelRefusesAreCountedAndDoNotTakeTheCapsPlaces()
    {
        // 2010 names Windows allows through extended paths but ServerFilePath refuses, sorted before the real files.
        var refused = Enumerable.Range(0, FileLimits.MaxListEntries + 10).Select(i => new FileEntry($"a{i:D4}.", FileEntryKind.File, 1, DateTimeOffset.UnixEpoch));
        var entries = refused.Append(new FileEntry("zreal.cfg", FileEntryKind.File, 1, DateTimeOffset.UnixEpoch))
            .Append(new FileEntry("nul.txt", FileEntryKind.File, 1, DateTimeOffset.UnixEpoch))
            .Append(new FileEntry("bad" + (char)0xD800 + "name.cfg", FileEntryKind.File, 1, DateTimeOffset.UnixEpoch))
            .Append(new FileEntry("ctrl" + (char)1, FileEntryKind.File, 1, DateTimeOffset.UnixEpoch));

        var listing = ServerFileRules.Listing(entries);

        Assert.Equal(["zreal.cfg"], listing.Entries.Select(e => e.Name));
        Assert.Equal(FileLimits.MaxListEntries + 10 + 3, listing.Skipped);
        Assert.False(listing.Truncated);
    }

    [Fact]
    public void ALoneSurrogateNameIsNeverSentBecauseJsonWouldTurnItIntoAnotherName()
    {
        var listing = ServerFileRules.Listing([
            new FileEntry("bad" + (char)0xD800 + "name.cfg", FileEntryKind.File, 1, DateTimeOffset.UnixEpoch),
            new FileEntry("q�z.cfg", FileEntryKind.File, 1, DateTimeOffset.UnixEpoch),
        ]);

        var sent = ProtocolJson.Deserialize<CommandAck>(ProtocolJson.Serialize(new CommandAck(Guid.NewGuid(), AckStatus.Accepted, null, null, listing)));

        var names = Assert.IsType<FileListResult>(sent.Result).Entries.Select(e => e.Name);
        Assert.Equal(["q�z.cfg"], names); // the name that really has U+FFFD; the broken one is counted, not sent
        Assert.Equal(1, listing.Skipped);
    }

    [Fact]
    public void PagesReachEveryEntryOnceInNameOrderWithoutHoldingTheWholeFolder()
    {
        var count = (FileLimits.MaxListEntries * 2) + 17;
        var names = Enumerable.Range(0, count).Select(i => $"f{(i * 7919) % count:D5}").ToList();
        var all = names.Select((n, i) => new FileEntry(n, i % 10 == 0 ? FileEntryKind.Directory : FileEntryKind.File, 1, DateTimeOffset.UnixEpoch)).ToList();

        var seen = new List<string>();
        string? after = null;
        var pages = 0;
        do
        {
            var entries = new CountingEntries(all.Select(e => e with { }), watchMemory: pages == 0);
            var page = ServerFileRules.Page(entries, ServerFilePath.Root, after);
            Assert.False(page.Truncated);
            Assert.InRange(page.Entries.Count, 1, FileLimits.MaxListEntries);
            Assert.Equal(page.Next is null ? null : page.Entries[^1].Name, page.Next);
            if (pages == 0)
            {
                Assert.InRange(entries.MostHeld, 0, FileLimits.MaxListEntries + 10);
            }

            seen.AddRange(page.Entries.Select(e => e.Name));
            after = page.Next;
            pages++;
        }
        while (after is not null);

        // Directories and files together, by name alone: a page's last name says where the next one starts.
        Assert.Equal(3, pages);
        Assert.Equal(names.Order(StringComparer.Ordinal), seen);
    }

    [Fact]
    public void NamesThatDifferOnlyInCaseHaveAFixedPlaceInThePages()
    {
        string[] names = ["b", "README", "a", "Readme", "readme", "A"];
        FileEntry[] entries = [.. names.Select(n => new FileEntry(n, FileEntryKind.File, 0, DateTimeOffset.UnixEpoch))];

        Assert.Equal(["A", "a", "b", "README", "Readme", "readme"], ServerFileRules.Page(entries, ServerFilePath.Root, null).Entries.Select(e => e.Name));
        Assert.Equal(["Readme", "readme"], ServerFileRules.Page(entries, ServerFilePath.Root, "README").Entries.Select(e => e.Name));
        Assert.Empty(ServerFileRules.Page(entries, ServerFilePath.Root, "readme").Entries);
    }

    [Fact]
    public void PagesLeaveOutWhatSftpHidesAndCountTheUnusableNamesOnce()
    {
        FileEntry[] entries =
        [
            new(".pulsar", FileEntryKind.Directory, 0, DateTimeOffset.UnixEpoch),
            new("server.cfg", FileEntryKind.File, 1, DateTimeOffset.UnixEpoch),
            new("odd\u202Egnp.exe", FileEntryKind.File, 1, DateTimeOffset.UnixEpoch),
            new("nul.txt", FileEntryKind.File, 1, DateTimeOffset.UnixEpoch),
            new("zero\u200Bwidth", FileEntryKind.File, 1, DateTimeOffset.UnixEpoch),
        ];

        var root = ServerFileRules.Page(entries, ServerFilePath.Root, null);
        Assert.Equal(["server.cfg"], root.Entries.Select(e => e.Name));
        Assert.Equal(3, root.Skipped); // the format characters and the device name; Pulsar's own folder is simply not there
        Assert.Null(root.Next);
        Assert.Equal(0, ServerFileRules.Page(entries, ServerFilePath.Root, "a").Skipped);

        // Below the server directory a folder named .pulsar is the server's own.
        Assert.Equal([".pulsar", "server.cfg"], ServerFileRules.Page(entries, "cfg", null).Entries.Select(e => e.Name));
    }

    [Fact]
    public void APageStopsReadingAFolderBeyondTheScanLimit()
    {
        var entries = new CountingEntries(Enumerable.Range(0, ServerFileRules.MaxListScan + 1000)
            .Select(i => new FileEntry($"f{i:D7}", FileEntryKind.File, 0, DateTimeOffset.UnixEpoch)));

        var page = ServerFileRules.Page(entries, ServerFilePath.Root, null);

        Assert.True(page.Truncated);
        Assert.Equal(FileLimits.MaxListEntries, page.Entries.Count);
        Assert.Equal("f0001999", page.Next);
        Assert.Equal(ServerFileRules.MaxListScan + 1, entries.Read);
    }

    [Fact]
    public void APageOfLongNamesIsCutWhereItsMessageWouldOutgrowTheHubAndGoesOnFromThere()
    {
        var entries = Enumerable.Range(0, FileLimits.MaxListEntries)
            .Select(i => new FileEntry($"{i:D4}" + new string('一', 250), FileEntryKind.File, 0, DateTimeOffset.UnixEpoch)).ToList();

        var first = ServerFileRules.Page(entries, ServerFilePath.Root, null);
        Assert.InRange(first.Entries.Count, 1, FileLimits.MaxListEntries - 1);
        Assert.Equal(first.Entries[^1].Name, first.Next);

        var second = ServerFileRules.Page(entries, ServerFilePath.Root, first.Next);
        Assert.Equal(entries[first.Entries.Count].Name, second.Entries[0].Name);
    }

    [Theory]
    [InlineData(FileEntryKind.File, null, true)]
    [InlineData(FileEntryKind.File, FileEntryKind.File, true)]
    [InlineData(FileEntryKind.Directory, null, true)]
    [InlineData(FileEntryKind.Directory, FileEntryKind.File, false)]
    [InlineData(FileEntryKind.Link, FileEntryKind.File, false)]
    [InlineData(FileEntryKind.File, FileEntryKind.Directory, false)]
    [InlineData(FileEntryKind.File, FileEntryKind.Link, false)]
    public void OnlyAFileReplacesAFile(FileEntryKind source, FileEntryKind? existing, bool allowed)
    {
        void Check() => ServerFileRules.CheckReplace("target", source, existing);

        if (allowed)
        {
            Check();
        }
        else
        {
            Assert.Throws<FileManagerException>(Check);
        }
    }

    [Fact]
    public void FileTimesAreClampedToWhatAFileSystemKeeps()
    {
        Assert.Equal(1, ServerFileRules.FileTime(DateTimeOffset.MinValue));
        Assert.Equal(1, ServerFileRules.FileTime(new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal(DateTime.MaxValue.ToFileTimeUtc(), ServerFileRules.FileTime(DateTimeOffset.MaxValue));
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(now.UtcDateTime.ToFileTimeUtc(), ServerFileRules.FileTime(now));
    }

    /// <summary>Counts the entries read, and how many a listing could still hold at most (read, not yet dropped).</summary>
    private sealed class CountingEntries(IEnumerable<FileEntry> entries, bool watchMemory = false) : IEnumerable<FileEntry>
    {
        private readonly List<WeakReference<FileEntry>> _handedOut = [];

        public int Read { get; private set; }

        public int MostHeld { get; private set; }

        public IEnumerator<FileEntry> GetEnumerator()
        {
            foreach (var entry in entries)
            {
                Read++;
                if (watchMemory)
                {
                    if (Read % 1000 == 0)
                    {
                        GC.Collect();
                        _handedOut.RemoveAll(r => !r.TryGetTarget(out _));
                        MostHeld = Math.Max(MostHeld, _handedOut.Count);
                    }

                    _handedOut.Add(new WeakReference<FileEntry>(entry));
                }

                yield return entry;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
