using Pulsar.Contracts;

namespace Pulsar.Agent.Tests;

/// <summary><c>enroll --code</c>: the node code gives the panel, the token, the key and the certificate at once.</summary>
public sealed class NodeCodeEnrollTests
{
    private static readonly string s_token = System.Buffers.Text.Base64Url.EncodeToString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    private static readonly string s_key = "sha256:" + System.Buffers.Text.Base64Url.EncodeToString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    private static readonly string s_certificate = new('a', 64);

    private static string Code() => NodeCode.Create("https://panel.example.com:8443", s_token, s_key, s_certificate);

    private static CommandLine Line(params string[] args) => CommandLine.Parse(["enroll", .. args], AgentCommands.Flags);

    [Fact]
    public void TheCodeIsReadFromTheStandardInputTheEnvironmentOrTheOption()
    {
        var fromInput = AgentCommands.Code(Line("--code", "-"), null, new StringReader(Code() + "\r\n"));
        var fromEnvironment = AgentCommands.Code(Line(), Code(), new StringReader(""));
        var fromOption = AgentCommands.Code(Line("--code", Code()), null, new StringReader(""));

        foreach (var code in new[] { fromInput, fromEnvironment, fromOption })
        {
            Assert.NotNull(code);
            Assert.Equal(("https://panel.example.com:8443", s_token, s_key, s_certificate), (code.Url, code.Token, code.PanelKey, code.CertificateSha256));
        }
    }

    [Fact]
    public void WithoutACodeTheOtherOptionsAreUsed()
    {
        Assert.Null(AgentCommands.Code(Line("--panel", "https://panel", "--token", "x", "--panel-key", s_key), null, new StringReader("")));
        Assert.Null(AgentCommands.Code(Line(), "   ", new StringReader("")));
    }

    [Fact]
    public void ACodeWithAnOptionItGivesIsRefused()
    {
        Assert.Throws<UsageException>(() => AgentCommands.Code(Line("--code", Code(), "--panel", "https://other"), null, new StringReader("")));
        Assert.Throws<UsageException>(() => AgentCommands.Code(Line("--panel-key", s_key), Code(), new StringReader("")));
    }

    [Fact]
    public void SomethingElseThanACodeIsRefused()
    {
        var refused = Assert.Throws<UsageException>(() => AgentCommands.Code(Line("--code", "-"), null, new StringReader("not a code\n")));
        Assert.Contains(NodeCode.Prefix, refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not a code", refused.Message, StringComparison.Ordinal);
    }
}
