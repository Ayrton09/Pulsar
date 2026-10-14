using Pulsar.Domain.Common;
using Pulsar.Domain.Nodes;

namespace Pulsar.Domain.Tests.Nodes;

public sealed class NodeCredentialTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AKeysLastUseNeverMovesBack()
    {
        // A clock that stepped back, or a sign-in that took long: the latest use recorded stays (the rotation fence
        // compares it with a pending key's challenge).
        var credential = NodeCredential.Enrolled(new NodeId(Guid.NewGuid()), [1, 2, 3], "sha256:test", NodeKeyStorage.Software, s_now);

        credential.Used(s_now.AddMinutes(5));
        credential.Used(s_now.AddMinutes(1));

        Assert.Equal(s_now.AddMinutes(5), credential.LastUsedAt);
        credential.Used(s_now.AddMinutes(6));
        Assert.Equal(s_now.AddMinutes(6), credential.LastUsedAt);
    }
}
