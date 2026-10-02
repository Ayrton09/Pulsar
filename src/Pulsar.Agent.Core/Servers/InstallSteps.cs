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
/// Runs a manifest's install steps in a server directory. The agent writes there with its own rights (SYSTEM), and the
/// server's account controls the directory, so every path is checked for junctions and links first: a server that
/// planted one could otherwise make the agent write anywhere on the machine. The server is offline meanwhile, so
/// nothing of it runs to swap a folder in between.
/// </summary>
public sealed class InstallSteps(InstallOptions options)
{
    public void Run(GameManifest manifest, string serverDirectory)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        foreach (var step in manifest.Install?.Steps ?? [])
        {
            switch (step)
            {
                case EnsureDirectoryStep ensure:
                    Directory.CreateDirectory(Resolve(serverDirectory, ensure.Path));
                    break;
                case TestGameServerStep test:
                    CopyTestGame(Resolve(serverDirectory, test.Path));
                    break;
                default:
                    throw new GameProcessException("install.unsupported_step", $"This agent cannot run the install step {step.GetType().Name}.");
            }
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

    private void CopyTestGame(string destination)
    {
        if (!options.TestGameAvailable)
        {
            throw new GameProcessException("install.test_game_unavailable", "This agent was not started with the test game server.");
        }

        var source = Path.GetFullPath(options.TestGameDirectory!);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file).Replace(Path.DirectorySeparatorChar, '/');
            var target = Resolve(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // Never write into an existing file: it could be a hard link to a system file. Deleting removes only this name.
            File.Delete(target);
            File.Copy(file, target);
        }
    }
}
