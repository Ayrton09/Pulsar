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

    /// <summary>Sends the downloads of install steps such as <see cref="MinecraftServerStep"/>; it must not follow redirects.</summary>
    public HttpMessageHandler HttpHandler { get; init; } = new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(30),
    };

    /// <summary>
    /// Checks who signed a downloaded executable (Windows: Authenticode). Null where nothing can: the agent then does not
    /// install what only a signature vouches for (<see cref="MinecraftBedrockStep"/>).
    /// </summary>
    public ICodeSignatures? CodeSignatures { get; init; }

    /// <summary>An installer the agent runs (Forge's, NeoForge's) that prints nothing for this long is stopped.</summary>
    public TimeSpan InstallerStallTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>An installer the agent runs is stopped after this long, whatever it prints.</summary>
    public TimeSpan InstallerTimeout { get; init; } = TimeSpan.FromMinutes(30);
}

/// <summary>The server the steps install, for what runs as its account (Forge's and NeoForge's installers).</summary>
/// <param name="Output">Where what such a program prints goes (the server's console).</param>
public sealed record InstallContext(Guid ServerId, ProcessIdentity Identity, Action<string>? Output);

/// <summary>Who signed an executable.</summary>
public interface ICodeSignatures
{
    /// <summary>
    /// Whether <paramref name="path"/> has a valid code signature (a chain to a trusted root, the file unmodified) whose
    /// signer's organization is <paramref name="organization"/>.
    /// </summary>
    bool IsSignedBy(string path, string organization);
}

/// <summary>
/// Runs the install steps the agent itself performs in a server directory (SteamCMD steps run as the server's account,
/// see <see cref="Steam.SteamCmdRunner"/>). The agent writes there with its own rights, and the server's account controls
/// the directory, so every write goes through <see cref="IServerFiles"/>: never through a junction or link, never into
/// an existing file.
/// </summary>
/// <param name="processes">Starts what runs as the server's account (Forge's and NeoForge's installers); without it, nothing does.</param>
public sealed class InstallSteps(InstallOptions options, IServerFiles files, IGameProcessManager? processes = null)
{
    /// <summary>Runs one step that is not a SteamCMD step, reporting its progress.</summary>
    /// <param name="variables">The server's variable values, defaults included.</param>
    public Task<string?> RunAsync(InstallStep step, string serverDirectory, IReadOnlyDictionary<string, string?> variables,
        Func<string, double?, Task> progress, CancellationToken cancellationToken) =>
        RunAsync(step, serverDirectory, variables, progress, context: null, cancellationToken);

    /// <summary>Runs one step that is not a SteamCMD step, reporting its progress.</summary>
    /// <param name="variables">The server's variable values, defaults included.</param>
    /// <param name="context">The server, for what runs as its account; without it, steps that need that fail.</param>
    /// <returns>The game's version the step installed, when it knows it (Minecraft's, which "latest" stands for).</returns>
    public async Task<string?> RunAsync(InstallStep step, string serverDirectory, IReadOnlyDictionary<string, string?> variables,
        Func<string, double?, Task> progress, InstallContext? context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentNullException.ThrowIfNull(progress);
        if (step is MinecraftServerStep minecraft)
        {
            return await new MinecraftInstaller(files, options.HttpHandler, JavaFor(context, serverDirectory))
                .InstallAsync(serverDirectory, Choice(minecraft, variables), progress, cancellationToken);
        }

        if (step is MinecraftBedrockStep bedrock)
        {
            var version = bedrock.VersionVar is { } key ? variables.GetValueOrDefault(key) : null;
            var signatures = options.CodeSignatures
                ?? throw new GameProcessException("bedrock.untrusted", "This agent cannot check code signatures, so it does not install the Bedrock server.");
            await new MinecraftBedrockInstaller(files, options.HttpHandler, signatures).InstallAsync(serverDirectory, version, progress, cancellationToken);
            return null;
        }

        await progress(Steam.InstallStages.Installing, null);
        await Task.Run(() => Run(step, serverDirectory), cancellationToken);
        return null;
    }

    /// <summary>Whether an update check can ask what the step would install: SteamCMD's and Minecraft's (Pulsar 1.0.9).</summary>
    public static bool IsCheckable(InstallStep step) => step is SteamCmdStep or MinecraftServerStep or MinecraftBedrockStep;

    /// <summary>
    /// Whether one of Minecraft's steps would install something new (an update check, Pulsar 1.0.9); null for a step it
    /// cannot ask (SteamCMD's are asked through <see cref="Steam.SteamCmdRunner"/>).
    /// </summary>
    /// <param name="variables">The server's variable values, defaults included.</param>
    /// <exception cref="GameProcessException">The publisher could not be asked.</exception>
    public async Task<GameBuild?> CheckAsync(InstallStep step, string serverDirectory, IReadOnlyDictionary<string, string?> variables,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(variables);
        return step switch
        {
            MinecraftServerStep minecraft => await new MinecraftInstaller(files, options.HttpHandler)
                .CheckAsync(serverDirectory, Choice(minecraft, variables), cancellationToken),
            // A check downloads nothing, so it has no signature to verify.
            MinecraftBedrockStep bedrock => await new MinecraftBedrockInstaller(files, options.HttpHandler, options.CodeSignatures ?? Unsigned.Instance)
                .CheckAsync(serverDirectory, bedrock.VersionVar is { } key ? variables.GetValueOrDefault(key) : null, cancellationToken),
            _ => null,
        };
    }

    /// <summary>What a step that is not a SteamCMD step does, for the server's console.</summary>
    public static string Describe(InstallStep step, IReadOnlyDictionary<string, string?> variables)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(variables);
        return step switch
        {
            MinecraftServerStep minecraft => Choice(minecraft, variables) switch
            {
                { TypeOrVanilla: MinecraftServerStep.Vanilla, Version: { Length: > 0 } version } when version != MinecraftServerStep.Latest =>
                    $"Downloading the Minecraft {version} server from Mojang, then its Java runtime",
                { TypeOrVanilla: MinecraftServerStep.Vanilla } => "Downloading the latest Minecraft server from Mojang, then its Java runtime",
                var choice => $"Installing a {TypeName(choice.TypeOrVanilla)} server for Minecraft "
                    + $"{(choice.Version is { Length: > 0 } version && version != MinecraftServerStep.Latest ? version : "(the latest it supports)")}"
                    + (choice.Loader is { Length: > 0 } loader ? $", loader {loader}" : "") + ", with its Java runtime",
            },
            MinecraftBedrockStep bedrock => bedrock.VersionVar is { } key && variables.GetValueOrDefault(key) is { Length: > 0 } version
                && version != MinecraftBedrockStep.Latest
                    ? $"Getting the Minecraft Bedrock {version} server from Mojang, unless it is already installed"
                    : "Getting the latest Minecraft Bedrock server from Mojang, unless it is already installed",
            EnsureDirectoryStep ensure => $"Creating the folder {ensure.Path}",
            WriteFileIfMissingStep write => $"Writing {write.Path} if it is missing",
            CopyFileIfMissingStep copy => $"Copying {copy.From} to {copy.To} if it is missing",
            TestGameServerStep => "Copying Pulsar's test game server",
            _ => step.GetType().Name,
        };
    }

    /// <summary>What a Minecraft step installs, from the server's variables.</summary>
    private static MinecraftChoice Choice(MinecraftServerStep step, IReadOnlyDictionary<string, string?> variables) =>
        new(step.VersionVar is { } version ? variables.GetValueOrDefault(version) : null,
            step.TypeVar is { } type ? variables.GetValueOrDefault(type) : null,
            step.LoaderVar is { } loader ? variables.GetValueOrDefault(loader) : null);

    private static string TypeName(string type) => type switch
    {
        MinecraftServerStep.Paper => "Paper",
        MinecraftServerStep.Purpur => "Purpur",
        MinecraftServerStep.Fabric => "Fabric",
        MinecraftServerStep.Forge => "Forge",
        MinecraftServerStep.NeoForge => "NeoForge",
        _ => type,
    };

    /// <summary>What runs the server's Java runtime as its account (Forge's and NeoForge's installers); null when nothing can.</summary>
    public JavaRunner? JavaFor(InstallContext? context, string serverDirectory) =>
        context is null || processes is null ? null : (arguments, cancellationToken) => RunJavaAsync(context, serverDirectory, arguments, cancellationToken);

    /// <summary>
    /// The server's Java runtime, as its account, in its folder (an installer the publisher's checksum vouched for): what it
    /// prints goes to the console; one that stalls or runs too long is stopped.
    /// </summary>
    private async Task RunJavaAsync(InstallContext context, string serverDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var spec = new ProcessLaunchSpec(context.ServerId, serverDirectory, "java/bin/java.exe", arguments, "", new Dictionary<string, string>(),
            ConsoleMode.Pipes, ConsoleEncoding.Utf8, context.Identity);
        var process = await processes!.StartAsync(spec, cancellationToken);
        using var stall = new CancellationTokenSource(options.InstallerStallTimeout);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stall.Token);
        deadline.CancelAfter(options.InstallerTimeout);
        var pump = Task.Run(async () =>
        {
            await foreach (var chunk in process.Output.ReadAllAsync(CancellationToken.None))
            {
                if (!stall.IsCancellationRequested)
                {
                    stall.CancelAfter(options.InstallerStallTimeout);
                }

                context.Output?.Invoke(chunk.Text);
            }
        }, CancellationToken.None);
        int exitCode;
        try
        {
            exitCode = await process.Exited.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            process.TerminateTree(1);
            await process.DisposeAsync();
            await DrainedAsync(pump);
            cancellationToken.ThrowIfCancellationRequested();
            throw new GameProcessException("minecraft.installer_stalled", "The installer stopped making progress, or ran too long; the console has what it printed.");
        }

        await process.DisposeAsync();
        await DrainedAsync(pump);
        if (exitCode != 0)
        {
            throw new GameProcessException("minecraft.installer_failed",
                $"The installer exited with code {exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)}; the console has what it printed.");
        }
    }

    /// <summary>
    /// Waits for what the installer printed last, but not for long: a process it left behind outside its Job could keep
    /// the output open, and the install would never end.
    /// </summary>
    private static async Task DrainedAsync(Task pump)
    {
        try
        {
            await pump.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            // The rest of its output is not shown.
        }
    }

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
            case CopyFileIfMissingStep copy:
                CopyIfMissing(serverDirectory, copy);
                break;
            case TestGameServerStep test:
                CopyTestGame(serverDirectory, test.Path);
                break;
            default:
                throw new GameProcessException("install.unsupported_step", $"This agent cannot run the install step {step.GetType().Name}.");
        }
    }

    /// <summary>Largest file <see cref="CopyFileIfMissingStep"/> copies (settings files, not game data).</summary>
    public const long MaxCopySize = 16L * 1024 * 1024;

    private void CopyIfMissing(string serverDirectory, CopyFileIfMissingStep copy)
    {
        using (var existing = files.OpenRead(serverDirectory, copy.To))
        {
            if (existing is not null)
            {
                return;
            }
        }

        using var source = files.OpenRead(serverDirectory, copy.From)
            ?? throw new GameProcessException("install.copy_source_missing", $"'{copy.From}' does not exist; the game's install did not create it.");
        using var content = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = source.Read(buffer)) > 0)
        {
            if (content.Length + read > MaxCopySize)
            {
                throw new GameProcessException("install.copy_too_large", $"'{copy.From}' is larger than {MaxCopySize / 1024 / 1024} MiB.");
            }

            content.Write(buffer, 0, read);
        }

        content.Position = 0;
        files.Write(serverDirectory, copy.To, content);
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

/// <summary>What an update check found for one install step (Pulsar 1.0.9).</summary>
/// <param name="Installed">What the server has (a Steam build id, a Minecraft version); null when it cannot be told.</param>
/// <param name="Latest">What the step would install now.</param>
/// <param name="Available">The step would install something the server does not have.</param>
public sealed record GameBuild(string? Installed, string Latest, bool Available);

/// <summary>Vouches for nothing: for code that downloads no executable.</summary>
internal sealed class Unsigned : ICodeSignatures
{
    public static Unsigned Instance { get; } = new();

    public bool IsSignedBy(string path, string organization) => false;
}
