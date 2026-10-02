using Pulsar.Agent.Core.Processes;
using Pulsar.Manifests;

namespace Pulsar.Agent.Core.Servers;

public sealed class InstallOptions
{
    /// <summary>
    /// Build output of Pulsar's test game server. Only set in development and CI: it enables the <c>pulsar-test</c>
    /// manifest (its <c>testGameServer</c> step copies these files into the server directory).
    /// </summary>
    public string? TestGameDirectory { get; set; }

    public bool TestGameAvailable => TestGameDirectory is { } directory && Directory.Exists(directory);
}

/// <summary>
/// Runs the install steps the agent itself performs in a server directory (SteamCMD steps run as the server's account,
/// see <see cref="Steam.SteamCmdRunner"/>). The agent writes there with its own rights, and the server's account controls
/// the directory, so every write goes through <see cref="IServerFiles"/>: never through a junction or link, never into
/// an existing file.
/// </summary>
public sealed class InstallSteps(InstallOptions options, IServerFiles files)
{
    /// <summary>Runs one step that is not a SteamCMD step.</summary>
    public void Run(InstallStep step, string serverDirectory)
    {
        ArgumentNullException.ThrowIfNull(step);
        switch (step)
        {
            case EnsureDirectoryStep ensure:
                files.CreateDirectory(serverDirectory, ensure.Path);
                break;
            case WriteFileIfMissingStep write:
                using (var existing = files.OpenRead(serverDirectory, write.Path))
                {
                    if (existing is null)
                    {
                        using var content = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(write.Content));
                        files.Write(serverDirectory, write.Path, content);
                    }
                }

                break;
            case TestGameServerStep test:
                CopyTestGame(serverDirectory, test.Path);
                break;
            default:
                throw new GameProcessException("install.unsupported_step", $"This agent cannot run the install step {step.GetType().Name}.");
        }
    }

    /// <summary>A path inside the server directory whose existing components are real directories, never links.</summary>
    public static string Resolve(string serverDirectory, string relativePath)
    {
        if (!ManifestValidator.IsValidRelativePath(relativePath))
        {
            throw new GameProcessException("install.invalid_path", $"'{relativePath}' is not a valid path inside the server directory.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(serverDirectory));
        var current = root;
        foreach (var segment in relativePath.Split('/'))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new GameProcessException("install.reparse_point",
                    $"'{Path.GetRelativePath(root, current)}' is a junction or link; the agent does not write through one.");
            }
        }

        return current;
    }

    private void CopyTestGame(string serverDirectory, string destination)
    {
        if (!options.TestGameAvailable)
        {
            throw new GameProcessException("install.test_game_unavailable", "This agent was not started with the test game server.");
        }

        var source = Path.GetFullPath(options.TestGameDirectory!);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file).Replace(Path.DirectorySeparatorChar, '/');
            using var content = File.OpenRead(file);
            files.Write(serverDirectory, $"{destination}/{relative}", content);
        }
    }
}
