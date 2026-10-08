namespace Pulsar.Agent.Tests;

public sealed class CommandLineTests
{
    [Fact]
    public void ParsesTheEnrollCommandThePanelShows()
    {
        var line = CommandLine.Parse(
            ["enroll", "--panel", "https://panel.example.com", "--token", "abc", "--panel-key", "sha256:xyz", "--dev"], AgentCommands.Flags);

        Assert.Equal("enroll", line.Command);
        Assert.Null(line.Subcommand);
        Assert.Equal("https://panel.example.com", line.Required("panel"));
        Assert.Equal("abc", line.Value("token"));
        Assert.True(line.Has("dev"));
        Assert.False(line.Has("no-service"));
    }

    [Fact]
    public void TheEnrollmentTokenCanStayOffTheCommandLine()
    {
        // The process list shows command lines: the token can come from the environment or standard input instead.
        var flags = AgentCommands.Flags;
        var withoutToken = CommandLine.Parse(["enroll", "--panel", "https://p", "--panel-key", "sha256:k"], flags);

        Assert.Equal("from-env", AgentCommands.EnrollmentToken(withoutToken, "from-env", new StringReader("")));
        Assert.Equal("from-stdin", AgentCommands.EnrollmentToken(CommandLine.Parse(["enroll", "--token", "-"], flags), null, new StringReader("from-stdin\r\n")));
        Assert.Equal("abc", AgentCommands.EnrollmentToken(CommandLine.Parse(["enroll", "--token", "abc"], flags), "from-env", new StringReader("")));
        Assert.Throws<UsageException>(() => AgentCommands.EnrollmentToken(withoutToken, null, new StringReader("")));
        Assert.Throws<UsageException>(() => AgentCommands.EnrollmentToken(CommandLine.Parse(["enroll", "--token", "-"], flags), null, new StringReader("")));
    }

    [Theory]
    [InlineData("-O8TH4J_ei1EVnOlLB2Np0cfq1vAXfmMpechf3u3L0Y")]
    [InlineData("--8TH4J_ei1EVnOlLB2Np0cfq1vAXfmMpechf3u3L0Y")]
    public void TokensStartingWithDashesAreValues(string token)
    {
        // Base64url tokens can start with '-' (and, rarely, '--').
        var line = CommandLine.Parse(["enroll", "--token", token, "--panel", "https://p"], AgentCommands.Flags);

        Assert.Equal(token, line.Value("token"));
        Assert.Equal("https://p", line.Value("panel"));
    }

    [Fact]
    public void SubcommandsAreRecognized()
    {
        var line = CommandLine.Parse(["service", "install", "--data-dir", @"D:\Agent"], AgentCommands.Flags);

        Assert.Equal("service", line.Command);
        Assert.Equal("install", line.Subcommand);
        Assert.Equal(@"D:\Agent", line.Value("data-dir"));
    }

    [Theory]
    [InlineData(new[] { "enroll", "--token" }, "--token needs a value.")]
    [InlineData(new[] { "enroll", "stray", "extra" }, "Unexpected argument 'extra'.")]
    [InlineData(new[] { "enroll", "--" }, "Unexpected argument '--'.")]
    public void MalformedArgumentsAreUsageErrors(string[] args, string message)
    {
        var error = Assert.Throws<UsageException>(() => CommandLine.Parse(args, AgentCommands.Flags));

        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void UnknownOptionsAreRejectedInsteadOfIgnored()
    {
        var line = CommandLine.Parse(["status", "--data-dri", @"D:\Agent"], AgentCommands.Flags);

        var error = Assert.Throws<UsageException>(() => line.Allow("check", "data-dir"));

        Assert.Contains("--data-dri", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new string[0], "help")]
    [InlineData(new[] { "--help" }, "help")]
    [InlineData(new[] { "-h" }, "h")]
    public void NoCommandMeansHelp(string[] args, string command)
    {
        Assert.Equal(command, CommandLine.Parse(args, AgentCommands.Flags).Command);
    }
}
