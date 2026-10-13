using Pulsar.Domain.Servers;

namespace Pulsar.Domain.Tests.Servers;

public sealed class SteamAccountTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ADifferentSteamUserNeedsAPasswordOfItsOwn()
    {
        byte[] password = [1, 2, 3];
        var account = SteamAccount.Create("Servers", "pulsar_servers", password, s_now).Value!;

        // The saved password belongs to one Steam account: never sent to another one.
        Assert.Equal(SteamAccountErrors.PasswordRequired, account.Change("Servers", "someone_else", null, s_now).Error);
        Assert.Equal("pulsar_servers", account.UserName);

        // Renaming the entry or keeping the user keeps the password; a new one comes with a new user.
        Assert.True(account.Change("Renamed", "pulsar_servers", null, s_now).IsSuccess);
        Assert.Equal(password, account.PasswordProtected);
        byte[] other = [4, 5, 6];
        Assert.True(account.Change("Renamed", "someone_else", other, s_now).IsSuccess);
        Assert.Equal(other, account.PasswordProtected);
    }
}
