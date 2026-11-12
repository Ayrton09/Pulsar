using System.ComponentModel;
using Microsoft.Extensions.Logging.Abstractions;
using Pulsar.Agent.Core.Processes;

namespace Pulsar.Agent.Core.Tests;

/// <summary>
/// The web ports of a game that listens with Windows' HTTP Server API (Pulsar 1.0.35): reserved for the server's account
/// before every start, released when it no longer has them, and nobody else's reservation ever touched.
/// </summary>
public sealed class HttpUrlReservationsTests : IDisposable
{
    private const string GameSid = "S-1-5-21-1-2-3-1001";
    private const string OtherSid = "S-1-5-21-1-2-3-1002";

    private readonly string _root = Directory.CreateTempSubdirectory("pulsar-urls-").FullName;
    private readonly FakeAcls _acls = new();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void TheServersWebPortsAreReservedForItsAccountOnce()
    {
        var urls = Reservations();

        Assert.Empty(urls.Reserve("abcd1234", [7669], Account("game")));
        Assert.Empty(urls.Reserve("abcd1234", [7669], Account("game")));

        Assert.Equal("D:(A;;GX;;;S-1-5-21-1-2-3-1001)", _acls.Reserved["http://+:7669/"]);
        Assert.Equal(1, _acls.Added);
    }

    [Fact]
    public void AnotherPortReleasesTheOldOne()
    {
        var urls = Reservations();
        urls.Reserve("abcd1234", [7669], Account("game"));

        urls.Reserve("abcd1234", [7679], Account("game"));

        Assert.Equal(["http://+:7679/"], _acls.Reserved.Keys);
    }

    [Fact]
    public void SomeoneElsesReservationIsLeftAndTheConsoleSaysWhy()
    {
        _acls.Reserved["http://+:7669/"] = "D:(A;;GX;;;WD)";
        var urls = Reservations();

        var problems = urls.Reserve("abcd1234", [7669], Account("game"));
        urls.Remove("abcd1234");

        Assert.Contains("netsh http delete urlacl url=http://+:7669/", Assert.Single(problems), StringComparison.Ordinal);
        Assert.Equal("D:(A;;GX;;;WD)", _acls.Reserved["http://+:7669/"]);
    }

    [Theory]
    [InlineData("http://other.example:7669/", 7669)]
    [InlineData("http://127.0.0.2:7669/", 7669)]
    [InlineData("http://[::1]:7669/", 7669)]
    [InlineData("http://*:7669/app/", 7669)]
    [InlineData("http://+:7669/other/", 7669)]
    [InlineData("http://other.example:80/", 80)]
    public void AnotherAccountsDifferentPrefixOnTheSamePortIsNotShadowed(string other, int port)
    {
        var descriptor = HttpUrlReservations.Descriptor(OtherSid);
        _acls.Reserved[other] = descriptor;

        var problems = Reservations().Reserve("abcd1234", [port], Account("game"));

        Assert.Contains("another account's reserved web namespace", Assert.Single(problems), StringComparison.Ordinal);
        Assert.Equal(0, _acls.Added);
        Assert.Equal(descriptor, Assert.Single(_acls.Reserved).Value);
    }

    [Theory]
    [InlineData("http://other.example:7670/", OtherSid)]
    [InlineData("https://other.example:7669/", OtherSid)]
    [InlineData("http://other.example:7669/", GameSid)]
    public void ASeparateHttpPortSchemeOrTheSameAccountCanCoexist(string other, string sid)
    {
        _acls.Reserved[other] = HttpUrlReservations.Descriptor(sid);

        Assert.Empty(Reservations().Reserve("abcd1234", [7669], Account("game")));

        Assert.Equal(1, _acls.Added);
        Assert.Equal(2, _acls.Reserved.Count);
    }

    [Fact]
    public void AnExistingRecordedWildcardIsReleasedWhenAnotherAccountsPrefixAppears()
    {
        var urls = Reservations();
        urls.Reserve("abcd1234", [7669], Account("game"));
        _acls.Reserved["http://other.example:7669/"] = HttpUrlReservations.Descriptor(OtherSid);

        Assert.Single(urls.Reserve("abcd1234", [7669], Account("game")));
        urls.Remove("abcd1234");

        Assert.Equal("http://other.example:7669/", Assert.Single(_acls.Reserved).Key);
        Assert.Equal(1, _acls.Added);
    }

    [Fact]
    public void AConflictingWildcardChangedBySomeoneElseIsNeverReleased()
    {
        var urls = Reservations();
        urls.Reserve("abcd1234", [7669], Account("game"));
        _acls.Reserved["http://+:7669/"] = HttpUrlReservations.Descriptor(OtherSid);
        _acls.Reserved["http://other.example:7669/"] = HttpUrlReservations.Descriptor(OtherSid);

        Assert.Single(urls.Reserve("abcd1234", [7669], Account("game")));
        urls.Remove("abcd1234");

        Assert.Equal(2, _acls.Reserved.Count);
        Assert.All(_acls.Reserved.Values, value => Assert.Equal(HttpUrlReservations.Descriptor(OtherSid), value));
    }

    [Fact]
    public void AConflictingWildcardWhoseReleaseFailsStaysRecordedForCleanup()
    {
        var urls = Reservations();
        urls.Reserve("abcd1234", [7669], Account("game"));
        _acls.Reserved["http://other.example:7669/"] = HttpUrlReservations.Descriptor(OtherSid);
        _acls.DeleteFailure = new Win32Exception(5, "Access is denied");

        Assert.Single(urls.Reserve("abcd1234", [7669], Account("game")));
        Assert.Equal(2, _acls.Reserved.Count);
        _acls.DeleteFailure = null;
        urls.Remove("abcd1234");

        Assert.Equal("http://other.example:7669/", Assert.Single(_acls.Reserved).Key);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnIncompleteReservationListDoesNotLeaveOrGrantARecordedWildcard(bool alreadyReserved)
    {
        var urls = Reservations();
        if (alreadyReserved)
        {
            urls.Reserve("abcd1234", [7669], Account("game"));
        }

        _acls.ListFailure = new IOException("incomplete enumeration");

        Assert.Contains("could not be checked", Assert.Single(urls.Reserve("abcd1234", [7669], Account("game"))), StringComparison.Ordinal);
        Assert.Empty(_acls.Reserved);
        Assert.Equal(alreadyReserved ? 1 : 0, _acls.Added);
    }

    [Fact]
    public void AReservationOfAServerDeletedMeanwhileGoesToTheOneThatHasThePortNow()
    {
        var urls = Reservations(isServer: shortId => shortId != "dead0000");
        urls.Reserve("dead0000", [7669], Account("old"));

        Assert.Empty(urls.Reserve("abcd1234", [7669], Account("game")));
        urls.Remove("dead0000");

        Assert.Equal("D:(A;;GX;;;S-1-5-21-1-2-3-1001)", _acls.Reserved["http://+:7669/"]);
    }

    [Fact]
    public void ALiveServersReservationIsNeverTakenOver()
    {
        // Two servers of one node with the same port on two of its addresses: both games ask for http://+:7669/.
        var urls = Reservations(isServer: _ => true);
        urls.Reserve("first000", [7669], Account("old"));

        var problems = urls.Reserve("abcd1234", [7669], Account("game"));

        Assert.Contains("another game server of this machine", Assert.Single(problems), StringComparison.Ordinal);
        Assert.Equal("D:(A;;GX;;;S-1-5-21-1-2-3-1002)", _acls.Reserved["http://+:7669/"]);
        urls.Remove("abcd1234");
        Assert.Single(_acls.Reserved);
    }

    [Fact]
    public void AnAccountWithANewIdentifierGetsItsOwnReservationBack()
    {
        Reservations().Reserve("abcd1234", [7669], Account("game"));

        // The server's account was made again (another security identifier): the agent's reservation for the old one is its own.
        var renewed = new HttpUrlReservations(_acls, _ => "S-1-5-21-1-2-3-2000", Path.Combine(_root, "http-urls.json"),
            NullLogger<HttpUrlReservations>.Instance);

        Assert.Empty(renewed.Reserve("abcd1234", [7669], Account("game")));
        Assert.Equal("D:(A;;GX;;;S-1-5-21-1-2-3-2000)", _acls.Reserved["http://+:7669/"]);
    }

    [Fact]
    public void ADevelopmentAgentsOwnUsersReservationIsUsedButNeverTakenAsTheAgents()
    {
        // A development agent's servers run as its own user, for whom the developer may have reserved the URL by hand.
        _acls.Reserved["http://+:7669/"] = "D:(A;;GX;;;S-1-5-21-1-2-3-1002)";
        var urls = Reservations();

        Assert.Empty(urls.Reserve("abcd1234", [7669], ProcessIdentity.CurrentUser));
        urls.Remove("abcd1234");

        Assert.Single(_acls.Reserved);
    }

    [Fact]
    public void APortChangeWhoseOldReservationTheSystemRefusesToReleaseIsReleasedLater()
    {
        var urls = Reservations();
        urls.Reserve("abcd1234", [7669], Account("game"));
        _acls.DeleteFailure = new Win32Exception(1, "Incorrect function");

        urls.Reserve("abcd1234", [7679], Account("game"));
        _acls.DeleteFailure = null;
        urls.Remove("abcd1234");

        Assert.Empty(_acls.Reserved);
    }

    [Fact]
    public void AReleaseTheSystemRefusesStaysRecordedForNextTime()
    {
        var urls = Reservations();
        urls.Reserve("abcd1234", [7669], Account("game"));
        _acls.DeleteFailure = new Win32Exception(1, "Incorrect function");

        urls.Remove("abcd1234");
        _acls.DeleteFailure = null;
        urls.Remove("abcd1234");

        Assert.Empty(_acls.Reserved);
    }

    [Fact]
    public void RemovingReleasesOnlyWhatTheAgentMadeAsItMadeIt()
    {
        var urls = Reservations();
        urls.Reserve("abcd1234", [7669, 7670], Account("game"));
        _acls.Reserved["http://+:7670/"] = "D:(A;;GX;;;WD)"; // changed by someone since

        urls.Remove("abcd1234");

        Assert.Equal(["http://+:7670/"], _acls.Reserved.Keys);
    }

    [Fact]
    public void UnenrollingReleasesEveryServersReservationsAndTheRecordIsKeptAcrossRuns()
    {
        Reservations().Reserve("abcd1234", [7669], Account("game"));
        Reservations().Reserve("dead0000", [7679], Account("old"));

        Assert.Equal(2, Reservations().RemoveAll());
        Assert.Empty(_acls.Reserved);
    }

    [Fact]
    public void AReservationRefusedTellsTheConsoleAndTheStartGoesOn()
    {
        _acls.Failure = new Win32Exception(5, "Access is denied");
        var urls = Reservations();

        var problems = urls.Reserve("abcd1234", [7669], Account("game"));

        Assert.Contains("could not be reserved", Assert.Single(problems), StringComparison.Ordinal);
        Assert.Empty(_acls.Reserved);
    }

    [Fact]
    public void AReservationTheSystemWritesBackWithAnAliasIsStillTheAgents()
    {
        // Windows writes a well-known account back by its alias (the machine's own Administrator is "LA"), not by the
        // security identifier it was given: the agent compares the form the system writes.
        _acls.Aliases[GameSid] = "LA";
        var urls = Reservations();

        Assert.Empty(urls.Reserve("abcd1234", [7669], Account("game")));
        Assert.Empty(urls.Reserve("abcd1234", [7669], Account("game")));
        Assert.Equal(1, _acls.Added);
        Assert.Equal("D:(A;;GX;;;LA)", _acls.Reserved["http://+:7669/"]);

        urls.Remove("abcd1234");
        Assert.Empty(_acls.Reserved);
    }

    [Fact]
    public void AnUnreadableRecordReleasesNothingAndIsSetAside()
    {
        _acls.Reserved["http://+:7669/"] = "D:(A;;GX;;;S-1-5-21-1-2-3-1001)";
        File.WriteAllText(Path.Combine(_root, "http-urls.json"), "{ not json");

        Assert.Equal(0, Reservations().RemoveAll());
        Assert.Single(_acls.Reserved);
        Assert.Single(Directory.GetFiles(_root, "http-urls.json.unreadable-*"));

        // The same server's next start finds its own reservation by its descriptor, and records it again.
        Assert.Empty(Reservations().Reserve("abcd1234", [7669], Account("game")));
        Reservations().Remove("abcd1234");
        Assert.Empty(_acls.Reserved);
    }

    private HttpUrlReservations Reservations(Func<string, bool>? isServer = null) => new(_acls, identity => identity switch
    {
        ProcessIdentity.AccountIdentity { Name: "game" } => GameSid,
        _ => OtherSid,
    }, Path.Combine(_root, "http-urls.json"), NullLogger<HttpUrlReservations>.Instance, isServer);

    private static ProcessIdentity Account(string name) => ProcessIdentity.Account(name);

    private sealed class FakeAcls : IHttpUrlAcls
    {
        public Dictionary<string, string> Reserved { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int Added { get; private set; }

        public Exception? Failure { get; set; }

        public Exception? DeleteFailure { get; set; }

        public Exception? ListFailure { get; set; }

        // Security identifiers the system writes back by an alias.
        public Dictionary<string, string> Aliases { get; } = new(StringComparer.Ordinal);

        public string? Find(string url) => Reserved.GetValueOrDefault(url);

        public IReadOnlyList<(string Url, string Descriptor)> List()
        {
            if (ListFailure is not null)
            {
                throw ListFailure;
            }

            return [.. Reserved.Select(entry => (entry.Key, entry.Value))];
        }

        public string Canonical(string securityDescriptor) =>
            Aliases.Aggregate(securityDescriptor, (d, alias) => d.Replace($";;;{alias.Key})", $";;;{alias.Value})", StringComparison.OrdinalIgnoreCase));

        public void Add(string url, string securityDescriptor)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            Reserved.Add(url, Canonical(securityDescriptor));
            Added++;
        }

        public void Delete(string url)
        {
            if (DeleteFailure is not null)
            {
                throw DeleteFailure;
            }

            Reserved.Remove(url);
        }
    }
}
