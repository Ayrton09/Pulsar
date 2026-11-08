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

    [Fact]
    public void AReservationOfAServerDeletedMeanwhileGoesToTheOneThatHasThePortNow()
    {
        var urls = Reservations();
        urls.Reserve("dead0000", [7669], Account("old"));

        Assert.Empty(urls.Reserve("abcd1234", [7669], Account("game")));
        urls.Remove("dead0000");

        Assert.Equal("D:(A;;GX;;;S-1-5-21-1-2-3-1001)", _acls.Reserved["http://+:7669/"]);
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
    public void AnUnreadableRecordReleasesNothing()
    {
        _acls.Reserved["http://+:7669/"] = "D:(A;;GX;;;S-1-5-21-1-2-3-1001)";
        File.WriteAllText(Path.Combine(_root, "http-urls.json"), "{ not json");

        Assert.Equal(0, Reservations().RemoveAll());
        Assert.Single(_acls.Reserved);
    }

    private HttpUrlReservations Reservations() => new(_acls, identity => identity switch
    {
        ProcessIdentity.AccountIdentity { Name: "game" } => GameSid,
        _ => OtherSid,
    }, Path.Combine(_root, "http-urls.json"), NullLogger<HttpUrlReservations>.Instance);

    private static ProcessIdentity Account(string name) => ProcessIdentity.Account(name);

    private sealed class FakeAcls : IHttpUrlAcls
    {
        public Dictionary<string, string> Reserved { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int Added { get; private set; }

        public Exception? Failure { get; set; }

        public string? Find(string url) => Reserved.GetValueOrDefault(url);

        public void Add(string url, string securityDescriptor)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            Reserved.Add(url, securityDescriptor);
            Added++;
        }

        public void Delete(string url) => Reserved.Remove(url);
    }
}
