using Pulsar.Domain.Access;
using Pulsar.Domain.Common;

namespace Pulsar.Domain.Tests.Access;

public sealed class SshKeyTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly UserId s_user = UserId.New();

    [Fact]
    public void AKeyKeepsItsBlobTypeAndFingerprintUnderTheNameGiven()
    {
        var parsed = Parse("laptop@home");
        var key = SshKey.Create(s_user, "  Work laptop ", parsed, s_now).Value;

        Assert.Equal("Work laptop", key.Name);
        Assert.Equal((s_user, SshPublicKey.Ed25519, parsed.Fingerprint, s_now), (key.UserId, key.Type, key.Fingerprint, key.CreatedAt));
        Assert.Equal(parsed.Blob, key.PublicKey);
        Assert.Null(key.LastUsedAt);
        Assert.Equal(7, key.Id.Value.Version);
    }

    [Fact]
    public void WithoutANameTheCommentIsTheName()
    {
        Assert.Equal("laptop@home", SshKey.Create(s_user, null, Parse(" laptop@home "), s_now).Value.Name);
        Assert.Equal("ed25519-key-20261019", SshKey.Create(s_user, "  ", Parse("ed25519-key-20261019"), s_now).Value.Name);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("two\nlines", null)]
    [InlineData("hidden‮exe.cfg", null)]
    [InlineData(null, "a comment that is far too long to be the name of a key: more than sixty-four characters")]
    [InlineData(null, "rtl‮comment")]
    public void ANameMustBeOneShortDisplayableLine(string? name, string? comment)
    {
        Assert.Equal(AccessErrors.SshKeyNameInvalid, SshKey.Create(s_user, name, Parse(comment), s_now).Error);
    }

    private static SshPublicKey Parse(string? comment)
    {
        var blob = SshTestKeys.Ed25519Blob(SshTestKeys.NewEd25519().PublicKey);
        return SshPublicKey.Parse($"{SshPublicKey.Ed25519} {Convert.ToBase64String(blob)} {comment}").Value;
    }
}
