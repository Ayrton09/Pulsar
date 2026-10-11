using Pulsar.Domain.Common;
using Pulsar.Domain.Databases;
using Pulsar.Domain.Servers;

namespace Pulsar.Domain.Tests.Databases;

public sealed class GameDatabaseTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] s_password = [1, 2, 3];

    [Fact]
    public void AHostKeepsItsPasswordWhenNotChangedAndServesItsNodesOnly()
    {
        var node = NodeId.New();
        var host = Host(nodes: [node.Value]);

        Assert.True(host.Change("Main", " DB.lan ", 3306, "root", null, " 127.0.0.1 ", 3307, null, [node.Value], s_now).IsSuccess);
        Assert.Equal(s_password, host.PasswordProtected);
        Assert.Equal(("127.0.0.1", 3307), host.AddressForServers);
        Assert.Equal(DatabaseHost.AnyHost, host.UserHost);
        Assert.True(host.Serves(node));
        Assert.False(host.Serves(NodeId.New()));
        Assert.True(Host().Serves(NodeId.New())); // no nodes: every node
        Assert.Equal(DatabaseErrors.HostPasswordRequired,
            DatabaseHost.Create("x", "db.lan", 3306, "root", null, null, null, null, [], s_now).Error);
    }

    [Theory]
    [InlineData("attacker.example", 3306, "root")]
    [InlineData("db.lan", 3307, "root")]
    [InlineData("db.lan", 3306, "admin")]
    [InlineData("db.lan", 3306, "Root")]
    public void TheSavedPasswordIsNeverSentToAnotherAccountOrServer(string address, int port, string user)
    {
        var host = Host();
        Assert.Equal(DatabaseErrors.HostPasswordRequired, host.Change("Main", address, port, user, null, null, null, null, [], s_now).Error);
        Assert.Equal(("db.lan", 3306, "root"), (host.Host, host.Port, host.UserName));

        // With its password, it may move.
        byte[] other = [4, 5, 6];
        Assert.True(host.Change("Main", address, port, user, other, null, null, null, [], s_now).IsSuccess);
        Assert.Equal(other, host.PasswordProtected);
    }

    [Theory]
    [InlineData("db.lan", 3306, "root", "%", null)]
    [InlineData("10.0.0.5", 3306, "root", "10.0.0.%", null)]
    [InlineData("db.lan:3306", 3306, "root", "%", "database_host.address_invalid")]
    [InlineData("http://db.lan", 3306, "root", "%", "database_host.address_invalid")]
    [InlineData("db.lan", 0, "root", "%", "database_host.port_invalid")]
    [InlineData("db.lan", 3306, "ro'ot", "%", "database_host.user_invalid")]
    [InlineData("db.lan", 3306, "root", "x' OR 1", "database_host.user_host_invalid")]
    [InlineData("db.lan", 3306, "root", "a b", "database_host.user_host_invalid")]
    public void HostSettingsAreChecked(string address, int port, string user, string userHost, string? error)
    {
        var result = DatabaseHost.Create("Main", address, port, user, s_password, null, null, userHost, [], s_now);
        Assert.Equal(error, result.IsSuccess ? null : result.Error!.Code);
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("localhost", false)]
    [InlineData("10.0.0.5", false)]
    [InlineData("172.20.1.1", false)]
    [InlineData("192.168.1.20", false)]
    [InlineData("169.254.10.1", false)]
    [InlineData("::1", false)]
    [InlineData("fd12:3456::1", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("203.0.113.7", true)]
    [InlineData("db.example.com", true)]
    public void TlsIsRequiredByDefaultOutsideTheLocalNetwork(string address, bool required)
    {
        var host = DatabaseHost.Create("Main", address, 3306, "root", s_password, null, null, null, [], s_now).Value;
        Assert.Equal(required, host.RequireTls);

        // Chosen explicitly, it is kept; an update that does not say keeps the current choice.
        host = DatabaseHost.Create("Main", address, 3306, "root", s_password, null, null, null, [], s_now, requireTls: !required).Value;
        Assert.Equal(!required, host.RequireTls);
        Assert.True(host.Change("Main", "203.0.113.8", 3306, "root", s_password, null, null, null, [], s_now).IsSuccess);
        Assert.Equal(!required, host.RequireTls);
        Assert.True(host.Change("Main", "203.0.113.8", 3306, "root", null, null, null, null, [], s_now, requireTls: required).IsSuccess);
        Assert.Equal(required, host.RequireTls);
    }

    [Theory]
    [InlineData("main", "sk3x7m2qa_main")]
    [InlineData("logs_2026", "sk3x7m2qa_logs_2026")]
    [InlineData("Main", null)]
    [InlineData("main;drop", null)]
    [InlineData("a`b", null)]
    [InlineData("", null)]
    [InlineData("abcdefghijklmnopqrstu", null)] // 21 characters
    public void DatabaseNamesAreTheServersPrefixAndACheckedSuffix(string suffix, string? expected)
    {
        Assert.Equal(expected, ServerDatabase.NameFor("k3x7m2qa", suffix));
        // MySQL user names have at most 32 characters.
        Assert.True((ServerDatabase.NameFor("k3x7m2qa", new string('a', ServerDatabase.MaxSuffixLength)) ?? "").Length <= 32);
    }

    [Fact]
    public void PasswordsAreLongRandomLettersAndDigits()
    {
        var passwords = Enumerable.Range(0, 50).Select(_ => ServerDatabase.NewPassword()).ToList();
        Assert.All(passwords, p => Assert.Matches("^[A-Za-z0-9]{24}$", p));
        Assert.Equal(passwords.Count, passwords.Distinct().Count());
    }

    [Fact]
    public void TheDatabaseLimitIsZeroToFifty()
    {
        var server = Server.Create(NodeId.New(), TemplateId.New(), "Retakes", null, "k3x7m2qa", s_now).Value;
        Assert.Equal(Server.DefaultDatabaseLimit, server.DatabaseLimit);
        Assert.True(server.SetDatabaseLimit(0, s_now).IsSuccess);
        Assert.Equal(ServerErrors.DatabaseLimitInvalid, server.SetDatabaseLimit(51, s_now).Error);
        Assert.Equal(ServerErrors.DatabaseLimitInvalid, server.SetDatabaseLimit(-1, s_now).Error);
    }

    private static DatabaseHost Host(IReadOnlyCollection<Guid>? nodes = null) =>
        DatabaseHost.Create("Main", "db.lan", 3306, "root", s_password, null, null, null, nodes ?? [], s_now).Value;
}
