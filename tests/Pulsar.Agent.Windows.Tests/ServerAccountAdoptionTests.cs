using System.Security.Principal;
using Pulsar.Agent.Windows.Servers;

namespace Pulsar.Agent.Windows.Tests;

/// <summary>
/// An account that already has a server's name is taken over only when Pulsar made it and it is in no local group but
/// Users: the server's processes keep the account's groups (their token drops privileges only).
/// </summary>
public sealed class ServerAccountAdoptionTests
{
    private const string Description = "Pulsar game server k3x7m2qa (managed by the Pulsar agent)";

    private static readonly SecurityIdentifier s_users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier s_administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier s_guests = new(WellKnownSidType.BuiltinGuestsSid, null);

    [Fact]
    public void OnlyAnAccountPulsarMadeInNoOtherGroupIsTakenOver()
    {
        // Pulsar's own accounts are in no group (NetUserAdd adds them to none), or in Users if someone added them.
        Assert.Null(WindowsServerAccounts.RefusalToAdopt([], Description, Description));
        Assert.Null(WindowsServerAccounts.RefusalToAdopt([s_users], Description, Description));

        Assert.NotNull(WindowsServerAccounts.RefusalToAdopt([s_administrators], Description, Description));
        Assert.NotNull(WindowsServerAccounts.RefusalToAdopt([s_users, s_administrators], Description, Description));
        Assert.NotNull(WindowsServerAccounts.RefusalToAdopt([s_guests], Description, Description));
        Assert.NotNull(WindowsServerAccounts.RefusalToAdopt([null], Description, Description)); // a group whose SID is unknown
        Assert.NotNull(WindowsServerAccounts.RefusalToAdopt([], "Made by hand", Description));
        Assert.NotNull(WindowsServerAccounts.RefusalToAdopt([], null, Description));
    }
}
