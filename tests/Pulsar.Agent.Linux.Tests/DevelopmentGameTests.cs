using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Pulsar.Agent.Core.Processes;

namespace Pulsar.Agent.Linux.Tests;

/// <summary>Tests of what a development agent (<c>--dev</c>, never root) does: run on Linux as a user other than root.</summary>
public sealed class LinuxUserFactAttribute : FactAttribute
{
    public LinuxUserFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = "Run on Linux as a user other than root, as a development agent runs.";
        SkipUnless = nameof(IsUser);
        SkipType = typeof(LinuxUserFactAttribute);
    }

    public static bool IsUser => OperatingSystem.IsLinux() && Libc.geteuid() != 0;
}

/// <summary>
/// A development agent runs games as its own user, through <c>game-exec</c> as the service does: there is no user to
/// change to, and game-exec must not try (setgroups needs root's rights).
/// </summary>
public sealed class DevelopmentGameTests
{
    private static readonly TimeSpan s_wait = TimeSpan.FromSeconds(30);

    [LinuxUserFact]
    public async Task ADevelopmentAgentRunsAGameAsItsOwnUser()
    {
        var root = Path.Combine(LinuxOnly.NewFolder(), "server");
        var bin = Path.Combine(root, "bin");
        Directory.CreateDirectory(bin);
        foreach (var file in Directory.EnumerateFiles(TestGame.Directory))
        {
            File.Copy(file, Path.Combine(bin, Path.GetFileName(file)));
        }

        File.SetUnixFileMode(Path.Combine(bin, "Pulsar.TestGameServer"), (UnixFileMode)0x1ED);
        var manager = new LinuxGameProcessManager(new LinuxServerAccounts(NullLogger<LinuxServerAccounts>.Instance), TestGame.Agent, TimeProvider.System,
            NullLogger<LinuxGameProcessManager>.Instance, allowCurrentUser: true);
        var spec = new ProcessLaunchSpec(Guid.NewGuid(), root, "bin/Pulsar.TestGameServer", ["--ready-after", "0"], "",
            new Dictionary<string, string> { ["DOTNET_ROOT"] = TestGame.DotnetRoot }, ConsoleMode.Pipes, ConsoleEncoding.Utf8, ProcessIdentity.CurrentUser);

        try
        {
            await using var game = await manager.StartAsync(spec, TestContext.Current.CancellationToken);
            var output = new StringBuilder();
            await WaitForAsync(game, output, "Test server ready");
            await game.WriteInputAsync("whoami\n", TestContext.Current.CancellationToken);
            await WaitForAsync(game, output, $"user={Environment.UserName}");

            await game.WriteInputAsync("quit\n", TestContext.Current.CancellationToken);
            Assert.Equal(0, await game.Exited.WaitAsync(s_wait, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(root)!, recursive: true);
        }
    }

    private static async Task WaitForAsync(IGameProcess game, StringBuilder output, string text)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(s_wait);
        while (!output.ToString().Contains(text, StringComparison.Ordinal))
        {
            try
            {
                output.Append((await game.Output.ReadAsync(timeout.Token)).Text);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException)
            {
                Assert.Fail($"'{text}' did not show; the game printed: {output}");
            }
        }
    }
}
