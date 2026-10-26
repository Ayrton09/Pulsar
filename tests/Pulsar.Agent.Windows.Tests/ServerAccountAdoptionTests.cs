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

    [WindowsFact]
    public void OnlyAnAccountPulsarMadeInNoOtherGroupIsTakenOver()
    {
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var guests = new SecurityIdentifier(WellKnownSidType.BuiltinGuestsSid, null);

        // Pulsar's own accounts are in no group (NetUserAdd adds them to none), or in Users if someone added them.
        Assert.Null(WindowsServerAccounts.RefusalToAdopt([], Description, Description));
        Assert.Null(WindowsServerAccounts.RefusalToAdopt([users], Description, Description));

        Assert.NotNull(WindowsServerAccounts.RefusalToAdopt([administrators], Description, Description));
        Assert.NotNull(WindowsServerAccounts.RefusalToAdopt([users, administrators], Description, Description));
        Assert.NotNull(WindowsServerAccounts.RefusalToAdopt([guests], Description, Description));
        Assert.NotNull(WindowsServerAccounts.RefusalToAdopt([null], Description, Description)); // a group whose SID is unknown
        Assert.NotNull(WindowsServerAccounts.RefusalToAdopt([], "Made by hand", Description));
        Assert.NotNull(WindowsServerAccounts.RefusalToAdopt([], null, Description));
    }
}
