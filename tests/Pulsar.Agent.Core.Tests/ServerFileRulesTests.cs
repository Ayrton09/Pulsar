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
