using System.Net;
using Pulsar.Api.Sftp;
using Pulsar.Domain.Common;

namespace Pulsar.Api.Tests.Sftp;

/// <summary>What one SFTP connection acts as.</summary>
public sealed class SftpConnectionTests
{
    [Fact]
    public void TheClientVersionDoesNotUndoTheSignIn()
    {
        using var connection = new SftpConnection(IPAddress.Loopback, null, 0);
        var user = UserId.New();
        connection.SignIn(new SftpIdentity(user, "alice", null, null));

        connection.SetClientVersion("SSH-2.0-OpenSSH_9.5");

        Assert.Equal(user, connection.Context.UserId);
        Assert.Equal("alice · SFTP", connection.Context.Actor.Display);
        Assert.Equal("SFTP SSH-2.0-OpenSSH_9.5", connection.Context.RequestInfo.UserAgent);
        Assert.Equal(connection.CorrelationId, connection.Context.RequestInfo.CorrelationId);
    }
}
