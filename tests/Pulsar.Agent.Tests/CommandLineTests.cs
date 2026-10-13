using System.Runtime.InteropServices;

namespace Pulsar.Agent.Tests;

public sealed partial class CommandLineTests
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

    [Fact]
    public void ALogFileGetsWhatACommandPrintsToo()
    {
        // The agent MSI runs enroll as a custom action, which has no console.
        var folder = Directory.CreateTempSubdirectory("pulsar-command-log-").FullName;
        var path = Path.Combine(folder, "logs", "enroll.log");
        var console = new StringWriter();
        var errors = new StringWriter();
        var (originalOut, originalError) = (Console.Out, Console.Error);
        Console.SetOut(console);
        Console.SetError(errors);
        try
        {
            using (CommandLog.Open(path, "enroll"))
            {
                Console.WriteLine("Enrolled.");
                Console.Error.WriteLine("A warning.");
            }

            using (CommandLog.Open(path, "enroll"))
            {
                Console.WriteLine("Again.");
            }

            Console.WriteLine("Not logged.");
            Assert.Null(CommandLog.Open(null, "enroll"));
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        var log = File.ReadAllText(path);
        Assert.Contains("PulsarAgent enroll", log, StringComparison.Ordinal);
        Assert.Contains("Enrolled.", log, StringComparison.Ordinal);
        Assert.Contains("A warning.", log, StringComparison.Ordinal);
        Assert.Contains("Again.", log, StringComparison.Ordinal); // appended
        Assert.DoesNotContain("Not logged.", log, StringComparison.Ordinal);
        Assert.Contains("Enrolled.", console.ToString(), StringComparison.Ordinal); // still on the console
        Assert.Contains("A warning.", errors.ToString(), StringComparison.Ordinal);
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void ALogFileThatCannotBeOpenedLeavesTheCommandOnItsConsole()
    {
        var folder = Directory.CreateTempSubdirectory("pulsar-command-log-").FullName; // a folder where the file should be
        var errors = new StringWriter();
        var originalError = Console.Error;
        Console.SetError(errors);
        try
        {
            Assert.Null(CommandLog.Open(folder, "enroll"));
        }
        finally
        {
            Console.SetError(originalError);
            Directory.Delete(folder);
        }

        Assert.Contains($"cannot write the log file {folder}", errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheServiceCanBeAskedToStopItsGameServersToo()
    {
        var line = CommandLine.Parse(["service", "stop", "--stop-servers", "--data-dir", @"D:\Agent"], AgentCommands.Flags);

        line.Allow("data-dir", "stop-servers");
        Assert.Equal(("service", "stop"), (line.Command, line.Subcommand));
        Assert.True(line.Has("stop-servers"));
        Assert.False(CommandLine.Parse(["service", "stop"], AgentCommands.Flags).Has("stop-servers"));
    }

    [Fact]
    public void UnenrollTakesALogFile()
    {
        // The agent MSI's uninstall runs it as a custom action, which has no console.
        var line = CommandLine.Parse(["unenroll", "--log-file", @"C:\ProgramData\Pulsar\Agent\logs\unenroll.log"], AgentCommands.Flags);

        line.Allow("data-dir", "log-file");
        Assert.Equal(@"C:\ProgramData\Pulsar\Agent\logs\unenroll.log", line.Value("log-file"));
    }

    [Theory]
    [InlineData(@"D:\PulsarServers", @"D:\PulsarServers")]
    [InlineData(@"D:\PulsarServers""", @"D:\PulsarServers")] // "D:\PulsarServers\" from an installer: the backslash escaped the quote
    [InlineData(@"D:\Pulsar Servers\", @"D:\Pulsar Servers\")]
    public void FolderOptionsAreFullPathsWithoutAStrayQuote(string value, string expected)
    {
        var line = CommandLine.Parse(["enroll", "--servers-root", value, "--backups-root", value], AgentCommands.Flags);

        Assert.Equal(expected, AgentCommands.DirectoryOption(line, "servers-root"));
        Assert.Equal(expected, AgentCommands.DirectoryOption(line, "backups-root"));
        Assert.Null(AgentCommands.DirectoryOption(CommandLine.Parse(["enroll"], AgentCommands.Flags), "servers-root"));
        Assert.Throws<UsageException>(() => AgentCommands.DirectoryOption(CommandLine.Parse(["enroll", "--servers-root", "\""], AgentCommands.Flags),
            "servers-root"));
    }

    [Theory]
    [InlineData(@"D:\Agent", @"D:\Agent")]
    [InlineData(@"D:\Agent\", @"D:\Agent")]
    [InlineData(@"D:\Agent""", @"D:\Agent")] // "D:\Agent\" quoted: the backslash escaped the quote (services installed that way before)
    [InlineData(@"D:\Agent Data\", @"D:\Agent Data")]
    [InlineData(@"D:\", @"D:\")]
    public void TheStateDirectoryIsAFullPathWithoutATrailingSeparatorOrQuote(string value, string expected)
    {
        Assert.Equal(expected, AgentCommands.Paths(CommandLine.Parse(["run", "--data-dir", value], AgentCommands.Flags)).Root);
    }

    [Theory]
    [InlineData(@"D:\Agent\")]
    [InlineData(@"D:\Agent Data\")]
    [InlineData(@"D:\")]
    public void TheServiceRunsWithTheStateDirectoryItWasInstalledFor(string dataDirectory)
    {
        // enroll --data-dir "D:\Agent\" once installed the service with --data-dir "D:\Agent\", which it read as D:\Agent".
        var paths = AgentCommands.Paths(CommandLine.Parse(["enroll", "--data-dir", dataDirectory], AgentCommands.Flags));
        var commandLine = Windows.AgentService.CommandLineFor(@"C:\Program Files\Pulsar\Agent\PulsarAgent.exe", AgentCommands.ServiceArguments(paths));

        string[] arguments = [.. SplitLikeTheRuntime(commandLine).Skip(1)];
        Assert.Equal(["run", "--data-dir", paths.Root], arguments);
        Assert.Equal(paths.Root, AgentCommands.Paths(CommandLine.Parse(arguments, AgentCommands.Flags)).Root);
        Assert.Equal(paths.Root, AgentCommands.ServiceDataDirectory(commandLine));
    }

    [Fact]
    public async Task AServiceThatDoesNotStopForAReEnrollmentIsSaidAndNothingIsEnrolled()
    {
        using var errors = new StringWriter();

        var stopped = await AgentCommands.StopForReEnrollmentAsync(_ => throw new TimeoutException("The PulsarAgent service did not stop within 30 s."), errors);

        Assert.False(stopped);
        Assert.Contains("did not stop within 30 s", errors.ToString(), StringComparison.Ordinal);
        Assert.Contains("PulsarAgent service start", errors.ToString(), StringComparison.Ordinal);
        Assert.True(await AgentCommands.StopForReEnrollmentAsync(_ => { }, errors));
    }

    /// <summary>The arguments a program gets from a command line (<c>CommandLineToArgvW</c>, as the .NET runtime parses it).</summary>
    private static string[] SplitLikeTheRuntime(string commandLine)
    {
        var argv = CommandLineToArgv(commandLine, out var count);
        try
        {
            return [.. Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!)];
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [LibraryImport("shell32.dll", EntryPoint = "CommandLineToArgvW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CommandLineToArgv(string commandLine, out int count);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr memory);

    [Theory]
    [InlineData(new string[0], "help")]
    [InlineData(new[] { "--help" }, "help")]
    [InlineData(new[] { "-h" }, "h")]
    public void NoCommandMeansHelp(string[] args, string command)
    {
        Assert.Equal(command, CommandLine.Parse(args, AgentCommands.Flags).Command);
    }
}
