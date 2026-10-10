using System.IO.Pipes;
using Microsoft.Extensions.Logging;

namespace Pulsar.Agent.Core.Processes;

/// <summary>Starts and finds the host processes of game servers (the Windows implementation runs <c>PulsarAgent host</c>).</summary>
public interface IGameHostLauncher
{
    /// <summary>Starts a host for <paramref name="serverId"/> that will serve <paramref name="pipe"/>.</summary>
    GameHostReference Launch(Guid serverId, string pipe);

    /// <summary>Whether the host process is still running (the same process: its id may have been reused since).</summary>
    bool IsAlive(GameHostReference host);

    /// <summary>Kills the host process; its game dies with its Job.</summary>
    void Kill(GameHostReference host);

    /// <summary>Whether <paramref name="pipe"/> (connected) is served by the host process itself.</summary>
    bool Serves(PipeStream pipe, GameHostReference host);

    /// <summary>Removes copies of the program no host runs from anymore (<paramref name="running"/> are the hosts left).</summary>
    void CleanUpPrograms(IEnumerable<GameHostReference> running)
    {
    }
}

/// <summary>
/// Game processes that outlive the agent (docs/architecture/06, "Server hosts"): each one is started by a host process
/// of its own, and found again after the agent restarts. What has nothing to do with the game's process (killing the
/// server account's stray processes) is done by the local manager.
/// </summary>
public sealed partial class GameHostManager(IGameProcessManager local, IGameHostLauncher launcher, ILogger<GameHostManager> logger)
    : IGameProcessManager
{
    /// <summary>How long a new host gets to open its pipe.</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long an existing host gets to answer when the agent comes back. Generous: one that does not answer is killed
    /// with its game, and a machine that is starting can be slow.
    /// </summary>
    public TimeSpan ReattachTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public async Task<IGameProcess> StartAsync(ProcessLaunchSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        GameHostReference host;
        try
        {
            host = launcher.Launch(spec.ServerId, GameHostProtocol.NewPipeName(spec.ServerId));
        }
        catch (Exception ex) when (ex is not (GameProcessException or OperationCanceledException or OutOfMemoryException))
        {
            throw new GameProcessException("host.start_failed", $"The server's host process could not be started: {ex.Message}", ex);
        }

        HostedGameProcess? process = null;
        try
        {
            process = await ConnectNewAsync(host, cancellationToken).ConfigureAwait(false);
            await process.StartGameAsync(GameHostLaunch.From(spec), cancellationToken).ConfigureAwait(false);
            await process.SubscribeAsync(0, cancellationToken).ConfigureAwait(false);
            return process;
        }
        catch
        {
            if (process is not null)
            {
                await process.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                launcher.Kill(host);
            }

            throw;
        }
    }

    /// <summary>
    /// The host a previous run of the agent left for a server, with its output from <paramref name="outputFrom"/> on;
    /// null when it is gone or has no game. A host that is alive but does not answer is killed (with its game): it must
    /// never run beside a new start of the same server.
    /// </summary>
    public async Task<IHostedGameProcess?> ReattachAsync(GameHostReference host, long outputFrom, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (!launcher.IsAlive(host))
        {
            return null;
        }

        HostedGameProcess? process = null;
        try
        {
            process = await HostedGameProcess.ConnectAsync(host, launcher.Serves, ReattachTimeout, logger, cancellationToken).ConfigureAwait(false);
            if (process.Status.ProcessId is null)
            {
                await process.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            await process.SubscribeAsync(outputFrom, cancellationToken).ConfigureAwait(false);
            LogReattached(process.ServerId, host.ProcessId, process.ProcessId);
            return process;
        }
        catch (GameProcessException ex)
        {
            LogReattachFailed(host.ProcessId, ex.Code, ex.Message);
            if (process is not null)
            {
                await process.DisposeAsync().ConfigureAwait(false);
            }

            launcher.Kill(host);
            return null;
        }
    }

    public int TerminateStrayProcesses(ProcessIdentity identity) => local.TerminateStrayProcesses(identity);

    /// <summary>Once the hosts of a previous run are taken over: older copies of the program nobody runs from can go.</summary>
    public void CleanUpPrograms(IEnumerable<GameHostReference> running) => launcher.CleanUpPrograms(running);

    /// <summary>Kills a host nobody will take over (its server is gone); its game dies with it.</summary>
    public void Kill(GameHostReference host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (launcher.IsAlive(host))
        {
            launcher.Kill(host);
        }
    }

    /// <summary>A host just started: its pipe appears once it is up.</summary>
    private async Task<HostedGameProcess> ConnectNewAsync(GameHostReference host, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + StartTimeout;
        while (true)
        {
            try
            {
                return await HostedGameProcess.ConnectAsync(host, launcher.Serves, TimeSpan.FromMilliseconds(500), logger, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (GameProcessException ex) when (ex.Code == "host.unreachable" && DateTimeOffset.UtcNow < deadline && launcher.IsAlive(host))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
            catch (GameProcessException ex) when (ex.Code == "host.unreachable")
            {
                throw new GameProcessException("host.start_failed", launcher.IsAlive(host)
                    ? "The server's host process did not open its pipe in time."
                    : "The server's host process ended right after it started (see the agent's logs folder).", ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Server {ServerId}: found its game (process {ProcessId}) in its host process {HostProcessId}")]
    private partial void LogReattached(Guid serverId, int hostProcessId, int processId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The host process {HostProcessId} of a server does not answer ({Code}: {Reason}); killing it")]
    private partial void LogReattachFailed(int hostProcessId, string code, string reason);
}
