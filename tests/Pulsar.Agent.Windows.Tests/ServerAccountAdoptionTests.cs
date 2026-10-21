using Pulsar.Agent.Windows.Servers;
using Windows.Win32.NetworkManagement.NetManagement;

namespace Pulsar.Agent.Windows.Tests;

/// <summary>
/// An account that already has a server's name is taken over only when Pulsar made it and it is no administrator's:
/// the server's processes keep the account's groups (their token drops privileges only).
/// </summary>
public sealed class ServerAccountAdoptionTests
{
    private const string Description = "Pulsar game server k3x7m2qa (managed by the Pulsar agent)";

    [Fact]
    public void OnlyAnAccountPulsarMadeAndNoAdministratorsIsTakenOver()
    {
        Assert.Null(WindowsServerAccounts.RefusalToAdopt(USER_PRIV.USER_PRIV_USER, Description, Description));

        Assert.NotNull(WindowsServerAccounts.RefusalToAdopt(USER_PRIV.USER_PRIV_ADMIN, Description, Description));
        Assert.NotNull(WindowsServerAccounts.RefusalToAdopt(USER_PRIV.USER_PRIV_GUEST, Description, Description));
        Assert.NotNull(WindowsServerAccounts.RefusalToAdopt(USER_PRIV.USER_PRIV_USER, "Made by hand", Description));
        Assert.NotNull(WindowsServerAccounts.RefusalToAdopt(USER_PRIV.USER_PRIV_USER, null, Description));
    }
}
