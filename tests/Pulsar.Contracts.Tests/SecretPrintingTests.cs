namespace Pulsar.Contracts.Tests;

/// <summary>Records print every member: tickets and tokens must never reach a log through ToString.</summary>
public sealed class SecretPrintingTests
{
    private const string Secret = "s3cr3t-T1CK3T-value";

    public static TheoryData<object> WithSecrets =>
    [
        new SendFileCommand(Guid.Empty, "server.cfg", Secret, 1024),
        new ReceiveFileCommand(Guid.Empty, "server.cfg", Secret, 10, false, null),
        new SendBackupCommand(Guid.Empty, Guid.Empty, Secret),
        new EnrollRequest(Secret, [1, 2], "software", "1.0.0", new NodeInventory("host", "Windows", "26100", null, 8, 1, null), [3]),
        new TokenResponse(Secret, Guid.Empty, DateTimeOffset.UnixEpoch),
    ];

    [Theory]
    [MemberData(nameof(WithSecrets))]
    public void TheSecretIsNotPrinted(object record)
    {
        var printed = record.ToString()!;

        Assert.DoesNotContain(Secret, printed, StringComparison.Ordinal);
        Assert.Contains("***", printed, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOtherMembersAreStillPrinted()
    {
        Assert.Contains("Path = server.cfg", new SendFileCommand(Guid.Empty, "server.cfg", Secret, 1024).ToString(), StringComparison.Ordinal);
        Assert.Contains("AgentVersion = 1.0.0",
            new EnrollRequest(Secret, [1], "software", "1.0.0", new NodeInventory("host", "Windows", "26100", null, 8, 1, null), [3]).ToString(),
            StringComparison.Ordinal);
    }
}
