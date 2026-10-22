using System.Net;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Pulsar.Agent.Core.Processes;
using Pulsar.Manifests;

namespace Pulsar.Agent.Core.Tests;

/// <summary>The outbound block of the servers' accounts toward the panel: what it lists, and when it is refreshed.</summary>
public sealed class PanelBlockTests : IDisposable
{
    private static readonly ServerAccount s_first = new("abcd2345", ProcessIdentity.Account("pulsar_abcd2345"));
    private static readonly ServerAccount s_second = new("efgh6789", ProcessIdentity.Account("pulsar_efgh6789"));

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pulsar-panel-block-tests", Guid.NewGuid().ToString("N"));
    private readonly SteppingClock _clock = new(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
    private readonly PanelBlockOptions _options = new();
    private readonly RecordingFirewall _firewall = new();
    private readonly CancellationTokenSource _stopping = new();
    private IReadOnlyList<Uri> _panels = [new("https://panel.example.com:8444/"), new("https://panel.example.com:8443/")];

    private string BookFile => Path.Combine(_root, "state", "panel-addresses.json");

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void TheTargetsAreEveryAddressOfThePanelAndOfTheNodeOnThePanelsPorts()
    {
        var targets = PanelTargets.For(
            [new("https://panel.example.com:8444/"), new("https://panel.example.com/"), new("https://198.51.100.7:8443/"), new("https://[2001:db8::7]:8443/")],
            [IPAddress.Parse("203.0.113.10"), IPAddress.Parse("2001:db8::5"), IPAddress.Parse("::ffff:203.0.113.10"), IPAddress.Parse("198.51.100.7"),
                IPAddress.Parse("2001:db8::7")],
            [IPAddress.Parse("192.168.1.20"), IPAddress.Parse("fe80::1%12"), IPAddress.Loopback, IPAddress.IPv6Loopback, IPAddress.Parse("203.0.113.10"),
                IPAddress.Any, IPAddress.IPv6Any])!;

        // Loopback always (the firewall refuses ::1 itself: ::/127 is it and the unspecified address), each address once,
        // IPv4-mapped ones as IPv4, without a scope: then the node's own.
        Assert.Equal("127.0.0.0/8,::/127,203.0.113.10,2001:db8::5,198.51.100.7,2001:db8::7,192.168.1.20,fe80::1", targets.RemoteAddresses);
        Assert.Equal("443,8443,8444", targets.RemotePorts);
    }

    [Fact]
    public void WithoutAPanelThereIsNothingToBlock()
    {
        Assert.Null(PanelTargets.For([], [], [IPAddress.Parse("10.0.0.4")]));
    }

    [Fact]
    public void KnownAddressesSurviveARestartOfTheAgent()
    {
        var book = new PanelAddressBook(BookFile, _clock, NullLogger.Instance);
        Confirm(book, "panel.example.com", IPAddress.Parse("203.0.113.10"), IPAddress.Parse("2001:db8::5"));
        book.Save();

        var again = new PanelAddressBook(BookFile, _clock, NullLogger.Instance);

        Assert.Equal([IPAddress.Parse("203.0.113.10"), IPAddress.Parse("2001:db8::5")], again.Known(["PANEL.example.com"]));
        Assert.Empty(again.Known(["other.example.com"]));
    }

    [Fact]
    public void AnAddressIsBlockedOnlyOnceTwoLookupsFiveMinutesApartAnsweredIt()
    {
        var book = new PanelAddressBook(BookFile, _clock, NullLogger.Instance);
        book.Learn("panel.example.com", [IPAddress.Parse("203.0.113.10")]);
        _clock.Advance(TimeSpan.FromMinutes(4));
        book.Learn("panel.example.com", [IPAddress.Parse("203.0.113.10")]);
        Assert.Empty(book.Known(["panel.example.com"]));

        _clock.Advance(TimeSpan.FromMinutes(1));
        book.Learn("panel.example.com", [IPAddress.Parse("203.0.113.10")]);
        Assert.Equal([IPAddress.Parse("203.0.113.10")], book.Known(["panel.example.com"]));
    }

    [Fact]
    public void AConfirmedAddressStaysBlockedWhenTheClockStepsBack()
    {
        var book = new PanelAddressBook(BookFile, _clock, NullLogger.Instance);
        Confirm(book, "panel.example.com", IPAddress.Parse("203.0.113.10"));
        _clock.Step = TimeSpan.FromHours(-6);

        Assert.Single(book.Known(["panel.example.com"]));
        book.Learn("panel.example.com", [IPAddress.Parse("203.0.113.10")]);
        book.Save();

        Assert.Single(book.Known(["panel.example.com"]));
        Assert.Single(new PanelAddressBook(BookFile, _clock, NullLogger.Instance).Known(["panel.example.com"]));
    }

    [Fact]
    public void AnUnconfirmedAddressIsNotConfirmedByAClockThatStepsBack()
    {
        var book = new PanelAddressBook(BookFile, _clock, NullLogger.Instance);
        book.Learn("panel.example.com", [IPAddress.Parse("203.0.113.10")]);
        _clock.Advance(TimeSpan.FromMinutes(1));
        book.Learn("panel.example.com", [IPAddress.Parse("203.0.113.10")]);
        _clock.Step = TimeSpan.FromHours(-6);
        book.Learn("panel.example.com", [IPAddress.Parse("203.0.113.10")]);

        Assert.Empty(book.Known(["panel.example.com"]));
    }

    [Fact]
    public void AFileFromBeforeTheFlagKeepsWhatItsTimesSaid()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(BookFile)!);
        File.WriteAllText(BookFile, """
            {"panel.example.com":[
              {"Address":"203.0.113.10","LastSeen":"2026-10-14T12:10:00+00:00","FirstSeen":"2026-10-14T12:00:00+00:00"},
              {"Address":"203.0.113.11","LastSeen":"2026-10-14T12:01:00+00:00","FirstSeen":"2026-10-14T12:00:00+00:00"},
              {"Address":"203.0.113.12","LastSeen":"2026-10-14T12:10:00+00:00"}]}
            """);

        var book = new PanelAddressBook(BookFile, _clock, NullLogger.Instance);

        Assert.Equal([IPAddress.Parse("203.0.113.10"), IPAddress.Parse("203.0.113.12")], book.Known(["panel.example.com"]));
    }

    [Fact]
    public void AnAddressGoesOnlyOnceTheNameHasResolvedWithoutItForAMonth()
    {
        var book = new PanelAddressBook(BookFile, _clock, NullLogger.Instance);
        Confirm(book, "panel.example.com", IPAddress.Parse("203.0.113.10"));
        _clock.Advance(TimeSpan.FromDays(20));
        Confirm(book, "panel.example.com", IPAddress.Parse("203.0.113.11"));
        Assert.Equal([IPAddress.Parse("203.0.113.10"), IPAddress.Parse("203.0.113.11")], book.Known(["panel.example.com"])); // the union

        // Months without an answer (the name does not resolve) forget nothing.
        _clock.Advance(TimeSpan.FromDays(90));
        book.Learn("panel.example.com", []);
        Assert.Equal(2, book.Known(["panel.example.com"]).Count);

        book.Learn("panel.example.com", [IPAddress.Parse("203.0.113.11")]);
        Assert.Equal([IPAddress.Parse("203.0.113.11")], book.Known(["panel.example.com"]));
    }

    [Fact]
    public void ANameNoLongerConfiguredIsForgottenAndManyAddressesKeepTheNewest()
    {
        var book = new PanelAddressBook(BookFile, _clock, NullLogger.Instance);
        Confirm(book, "old.example.com", IPAddress.Parse("198.51.100.1"));
        for (var i = 0; i < PanelAddressBook.MaxAddressesPerName + 10; i++)
        {
            Confirm(book, "panel.example.com", new IPAddress([10, 0, (byte)(i / 256), (byte)(i % 256)]));
        }

        book.Retain(["panel.example.com"]);

        Assert.Empty(book.Known(["old.example.com"]));
        var known = book.Known(["panel.example.com"]);
        Assert.Equal(PanelAddressBook.MaxAddressesPerName, known.Count);
        Assert.DoesNotContain(new IPAddress([10, 0, 0, 0]), known);
        Assert.Contains(new IPAddress([10, 0, 0, (byte)(PanelAddressBook.MaxAddressesPerName + 9)]), known);
    }

    [Fact]
    public void AForgedAnswerNeitherEvictsThePanelsAddressesNorBlocksItsOwnForLong()
    {
        var real = IPAddress.Parse("203.0.113.10");
        var book = new PanelAddressBook(BookFile, _clock, NullLogger.Instance);
        Confirm(book, "panel.example.com", real);

        // One forged answer with 70 addresses the attacker chose (say, a game's authentication servers), not the panel's.
        var forged = Enumerable.Range(1, 70).Select(i => new IPAddress([198, 51, 100, (byte)i])).ToList();
        _clock.Advance(TimeSpan.FromMinutes(5));
        book.Learn("panel.example.com", forged);
        Assert.Equal([real], book.Known(["panel.example.com"])); // none confirmed, the real one kept

        // Not answered again within the hour (the real answers come instead), they are forgotten.
        for (var i = 0; i < 13; i++)
        {
            _clock.Advance(TimeSpan.FromMinutes(5));
            book.Learn("panel.example.com", [real]);
        }

        book.Learn("panel.example.com", forged);
        Assert.Equal([real], book.Known(["panel.example.com"]));

        // Two forged answers five minutes apart do get in: 16 addresses at most, next to the real one (doc 08, T31).
        _clock.Advance(TimeSpan.FromMinutes(5));
        book.Learn("panel.example.com", forged);
        var known = book.Known(["panel.example.com"]);
        Assert.Equal(PanelAddressBook.MaxAddressesPerAnswer + 1, known.Count);
        Assert.Contains(real, known);
    }

    [Fact]
    public void ConfirmedAddressesAreNeverEvictedForUnconfirmedOnes()
    {
        var book = new PanelAddressBook(BookFile, _clock, NullLogger.Instance);
        var confirmed = Enumerable.Range(0, PanelAddressBook.MaxAddressesPerName).Select(i => new IPAddress([10, 0, 0, (byte)i])).ToArray();
        foreach (var batch in confirmed.Chunk(PanelAddressBook.MaxAddressesPerAnswer))
        {
            Confirm(book, "panel.example.com", batch);
        }

        var newer = Enumerable.Range(1, PanelAddressBook.MaxAddressesPerAnswer).Select(i => new IPAddress([198, 51, 100, (byte)i])).ToArray();
        _clock.Advance(TimeSpan.FromMinutes(5));
        book.Learn("panel.example.com", newer);
        Assert.Equal(confirmed, book.Known(["panel.example.com"]));

        // Once confirmed, they take the place of the confirmed ones seen longest ago.
        _clock.Advance(TimeSpan.FromMinutes(5));
        book.Learn("panel.example.com", newer);
        var known = book.Known(["panel.example.com"]);
        Assert.Equal(PanelAddressBook.MaxAddressesPerName, known.Count);
        Assert.All(newer, address => Assert.Contains(address, known));
        Assert.DoesNotContain(confirmed[0], known);
        Assert.Contains(confirmed[PanelAddressBook.MaxAddressesPerAnswer], known);
    }

    [Fact]
    public void ABookWithANameWithoutAddressesIsReadAsTheOthers()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(BookFile)!);
        File.WriteAllText(BookFile, "{\"panel.example.com\": null, \"other.example.com\": [null, {\"Address\": null}]}");

        var book = new PanelAddressBook(BookFile, _clock, NullLogger.Instance);

        Assert.Empty(book.Known(["panel.example.com", "other.example.com"]));
        Confirm(book, "panel.example.com", IPAddress.Parse("203.0.113.10"));
        Assert.Single(book.Known(["panel.example.com"]));
    }

    [Fact]
    public void AnUnreadableBookStartsEmpty()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(BookFile)!);
        File.WriteAllText(BookFile, "{ not json");

        var book = new PanelAddressBook(BookFile, _clock, NullLogger.Instance);

        Assert.Empty(book.Known(["panel.example.com"]));
        Confirm(book, "panel.example.com", IPAddress.Parse("203.0.113.10"));
        book.Save();
        Assert.Single(new PanelAddressBook(BookFile, _clock, NullLogger.Instance).Known(["panel.example.com"]));
    }

    [Fact]
    public async Task EveryAccountIsBlockedAtStartWithWhatIsKnownBeforeAnyLookupThenWithTheUnionOfOldAndNew()
    {
        Remember("panel.example.com", "203.0.113.10");
        var resolver = new Resolver(_ => [IPAddress.Parse("198.51.100.9")]);
        var keeper = Keeper(resolver);

        keeper.Start(() => [s_first, s_second], _stopping.Token);

        // At once, before Start returns: no lookup on the way to the servers' starts.
        Assert.True(_firewall.Blocks.Reader.TryRead(out var first));
        Assert.Equal([s_first, s_second], first.Servers);
        Assert.Equal("127.0.0.0/8,::/127,203.0.113.10,10.0.0.4", first.Targets.RemoteAddresses);
        Assert.Equal("8443,8444", first.Targets.RemotePorts);

        // The new address joins once the next lookup, five minutes later, answers it again.
        Assert.Equal("127.0.0.0/8,::/127,203.0.113.10,10.0.0.4", (await NextAsync()).Targets.RemoteAddresses);
        Assert.Equal(["panel.example.com"], resolver.Hosts); // one lookup for both addresses
        var refreshed = await AdvanceUntilNextAsync(_options.PollInterval);
        Assert.Equal("127.0.0.0/8,::/127,203.0.113.10,198.51.100.9,10.0.0.4", refreshed.Targets.RemoteAddresses);
        Assert.Same(refreshed.Targets, keeper.Current);
        Assert.Equal([IPAddress.Parse("203.0.113.10"), IPAddress.Parse("198.51.100.9")],
            new PanelAddressBook(BookFile, _clock, NullLogger.Instance).Known(["panel.example.com"]));
    }

    [Fact]
    public async Task ALookupThatHangsIsGivenUpAndTheLastGoodAddressesStay()
    {
        Remember("panel.example.com", "203.0.113.10");
        var resolver = new Resolver(_ => throw new InvalidOperationException("unused")) { Hang = true };
        var keeper = Keeper(resolver);

        keeper.Start(() => [s_first], _stopping.Token);
        Assert.True(_firewall.Blocks.Reader.TryRead(out _));
        await resolver.Called.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var refreshed = await AdvanceUntilNextAsync(_options.ResolveTimeout);

        Assert.Equal("127.0.0.0/8,::/127,203.0.113.10,10.0.0.4", refreshed.Targets.RemoteAddresses);
    }

    [Fact]
    public async Task AFailedLookupKeepsTheLastGoodAddresses()
    {
        Remember("panel.example.com", "203.0.113.10");
        var resolver = new Resolver(host => throw new ArgumentException($"{host} is too long"));
        var keeper = Keeper(resolver);

        keeper.Start(() => [s_first], _stopping.Token);
        Assert.True(_firewall.Blocks.Reader.TryRead(out _));
        var refreshed = await NextAsync();

        Assert.Equal("127.0.0.0/8,::/127,203.0.113.10,10.0.0.4", refreshed.Targets.RemoteAddresses);
    }

    [Fact]
    public async Task ANetworkChangeAMoveAndTheTimerRefreshTheBlock()
    {
        var resolver = new Resolver(_ => [IPAddress.Parse("203.0.113.10")]);
        var keeper = Keeper(resolver);
        keeper.Start(() => [s_first], _stopping.Token);
        Assert.True(_firewall.Blocks.Reader.TryRead(out _));
        await NextAsync();

        keeper.Request(); // a network change
        await NextAsync();
        Assert.Equal(2, resolver.Hosts.Count);

        // Another one right after (rogue router advertisements): coalesced, and looked at 30 seconds after the first.
        var requestedAt = _clock.GetUtcNow();
        keeper.Request();
        keeper.Request();
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.False(_firewall.Blocks.Reader.TryRead(out _));
        await AdvanceUntilNextAsync(_options.PollInterval);
        Assert.Equal(3, resolver.Hosts.Count);
        Assert.True(_clock.GetUtcNow() - requestedAt >= _options.MinRequestInterval);

        // The agent moved to another address: noticed at the next look at its configuration, without waiting for the timer.
        _panels = [new("https://agents.example.com:9443/"), new("https://panel.example.com:8443/")];
        var moved = await AdvanceUntilNextAsync(_options.PollInterval);
        Assert.Equal("8443,9443", moved.Targets.RemotePorts);
        Assert.Contains("agents.example.com", resolver.Hosts);

        var count = resolver.Hosts.Count;
        await AdvanceUntilNextAsync(_options.PollInterval);
        Assert.True(resolver.Hosts.Count > count);
        Assert.True(_clock.GetUtcNow() - new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero) >= _options.RefreshInterval);
    }

    [Fact]
    public async Task TheRefreshesKeepTheirPeriodWhenTheClockStepsBack()
    {
        var resolver = new Resolver(_ => [IPAddress.Parse("203.0.113.10")]);
        var keeper = Keeper(resolver);
        keeper.Start(() => [s_first], _stopping.Token);
        Assert.True(_firewall.Blocks.Reader.TryRead(out _));
        await NextAsync();
        var refreshedAt = _clock.GetTimestamp();

        _clock.Step = TimeSpan.FromHours(-6);
        await AdvanceUntilNextAsync(_options.PollInterval);

        // Its period, not the 6 hours the wall clock stepped back nor at once. The clock moves on in steps while the keeper
        // works, so on a slow machine (a CI runner) the refresh is seen some steps late.
        Assert.InRange(_clock.GetElapsedTime(refreshedAt), _options.RefreshInterval, 2 * _options.RefreshInterval);
    }

    [Fact]
    public async Task AFirewallThatFailsDoesNotStopTheRefreshes()
    {
        Remember("panel.example.com", "203.0.113.10");
        var resolver = new Resolver(_ => [IPAddress.Parse("203.0.113.10")]);
        var keeper = Keeper(resolver);
        _firewall.Failures = 1;

        keeper.Start(() => [s_first], _stopping.Token); // fails: logged, and the refresh that follows tries again

        Assert.False(_firewall.Blocks.Reader.TryRead(out _));
        Assert.Equal("127.0.0.0/8,::/127,203.0.113.10,10.0.0.4", (await NextAsync()).Targets.RemoteAddresses);
    }

    [Fact]
    public async Task ANewServerIsBlockedWithTheCurrentAddresses()
    {
        var keeper = Keeper(new Resolver(_ => [IPAddress.Parse("203.0.113.10")]));
        keeper.Start(() => [s_first], _stopping.Token);
        Assert.True(_firewall.Blocks.Reader.TryRead(out _));
        var current = (await NextAsync()).Targets;

        keeper.Ensure(s_second);

        Assert.True(_firewall.Blocks.Reader.TryRead(out var ensured));
        Assert.Equal([s_second], ensured.Servers);
        Assert.Same(current, ensured.Targets);
        Assert.Equal([s_second.ShortId], _firewall.Provisioned); // blocked even if a server of that id was deleted earlier
    }

    [Fact]
    public async Task AnUnexpectedFailureNeitherFailsTheStartNorEndsTheRefreshes()
    {
        _panels = [new("panel/relative", UriKind.Relative)]; // no host to read: reading it throws
        var reads = 0;
        var looked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var keeper = new PanelBlockKeeper(_firewall, () =>
            {
                if (Interlocked.Increment(ref reads) == 2)
                {
                    looked.TrySetResult();
                }

                return _panels;
            }, new PanelAddressBook(BookFile, _clock, NullLogger.Instance), _options, _clock, NullLogger<PanelBlockKeeper>.Instance,
            new Resolver(_ => []).ResolveAsync, () => [IPAddress.Parse("10.0.0.4")]);

        keeper.Start(() => [s_first], _stopping.Token); // logged, as the first refresh's failure is
        await looked.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        _panels = [new("https://198.51.100.7:8443/")];

        var refreshed = await AdvanceUntilNextAsync(_options.PollInterval);
        Assert.Equal("127.0.0.0/8,::/127,198.51.100.7,10.0.0.4", refreshed.Targets.RemoteAddresses);
        Assert.False(keeper.Completion.IsCompleted);
    }

    [Fact]
    public void WithoutManagingTheFirewallNothingIsBlockedOrLookedUp()
    {
        var resolver = new Resolver(_ => [IPAddress.Parse("203.0.113.10")]);
        _firewall.Enabled = false;
        var keeper = Keeper(resolver);

        keeper.Start(() => [s_first], _stopping.Token);
        keeper.Request();
        keeper.Ensure(s_second);

        Assert.False(_firewall.Blocks.Reader.TryRead(out _));
        Assert.Null(keeper.Current);
        Assert.Empty(resolver.Hosts);
    }

    private PanelBlockKeeper Keeper(Resolver resolver) =>
        new(_firewall, () => _panels, new PanelAddressBook(BookFile, _clock, NullLogger.Instance), _options, _clock,
            NullLogger<PanelBlockKeeper>.Instance, resolver.ResolveAsync, () => [IPAddress.Parse("10.0.0.4"), IPAddress.Loopback]);

    private void Remember(string host, string address)
    {
        var book = new PanelAddressBook(BookFile, _clock, NullLogger.Instance);
        Confirm(book, host, IPAddress.Parse(address));
        book.Save();
    }

    /// <summary>Two lookups five minutes apart answer the addresses: they are blocked from then on.</summary>
    private void Confirm(PanelAddressBook book, string host, params IPAddress[] addresses)
    {
        book.Learn(host, addresses);
        _clock.Advance(PanelAddressBook.ConfirmAfter);
        book.Learn(host, addresses);
    }

    private async Task<(IReadOnlyCollection<ServerAccount> Servers, PanelTargets Targets)> NextAsync() =>
        await _firewall.Blocks.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    /// <summary>Moves the clock in steps (the keeper may not be waiting yet) until the block is applied again.</summary>
    private async Task<(IReadOnlyCollection<ServerAccount> Servers, PanelTargets Targets)> AdvanceUntilNextAsync(TimeSpan step)
    {
        var next = _firewall.Blocks.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask();
        for (var i = 0; i < 200; i++)
        {
            _clock.Advance(step);
            if (await Task.WhenAny(next, Task.Delay(50, TestContext.Current.CancellationToken)) == next)
            {
                return await next;
            }
        }

        throw new TimeoutException("The block was not applied again.");
    }

    /// <summary>A fake clock whose wall time can be stepped without the monotonic one or its timers moving (a wrong RTC, a VM resumed).</summary>
    private sealed class SteppingClock(DateTimeOffset start) : TimeProvider
    {
        private readonly FakeTimeProvider _inner = new(start);

        public TimeSpan Step { get; set; }

        public override long TimestampFrequency => _inner.TimestampFrequency;

        public void Advance(TimeSpan delta) => _inner.Advance(delta);

        public override DateTimeOffset GetUtcNow() => _inner.GetUtcNow() + Step;

        public override long GetTimestamp() => _inner.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            _inner.CreateTimer(callback, state, dueTime, period);
    }

    private sealed class RecordingFirewall : IServerFirewall
    {
        public bool Enabled { get; set; } = true;

        public int Failures { get; set; }

        public List<string> Provisioned { get; } = [];

        public Channel<(IReadOnlyCollection<ServerAccount> Servers, PanelTargets Targets)> Blocks { get; } =
            Channel.CreateUnbounded<(IReadOnlyCollection<ServerAccount>, PanelTargets)>();

        public void Allow(string shortId, string executablePath, IReadOnlyList<BoundPort> ports, ProcessIdentity identity)
        {
        }

        public void BlockPanel(IReadOnlyCollection<ServerAccount> servers, PanelTargets targets)
        {
            if (Failures > 0)
            {
                Failures--;
                throw new InvalidOperationException("The firewall service is not running.");
            }

            Blocks.Writer.TryWrite(([.. servers], targets));
        }

        void IServerFirewall.Provisioned(string shortId) => Provisioned.Add(shortId);

        public void Remove(string shortId)
        {
        }
    }

    private sealed class Resolver(Func<string, IReadOnlyList<IPAddress>> answer)
    {
        private readonly Lock _lock = new();
        private readonly List<string> _hosts = [];

        public bool Hang { get; init; }

        public TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<string> Hosts
        {
            get
            {
                lock (_lock)
                {
                    return [.. _hosts];
                }
            }
        }

        public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _hosts.Add(host);
            }

            Called.TrySetResult();
            if (Hang)
            {
                await Task.Delay(Timeout.Infinite, CancellationToken.None); // a lookup that ignores its cancellation
            }

            return answer(host);
        }
    }
}
