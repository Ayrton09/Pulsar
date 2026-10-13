using System.IO.Pipes;
using Microsoft.Extensions.Logging;

namespace Pulsar.Agent.Core.Processes;

/// <summary>Starts and finds the host processes of game servers (the Windows implementation runs <c>PulsarAgent host</c>).</summary>
public interface IGameHostLauncher
{
    /// <summary>Starts a host for <paramref name="serverId"/> that will serve <paramref name="pipe"/>.</summary>
    GameHostReference Launch(Guid serverId, string pipe);

    /// <summary>Whether the host process is still running (the same process: its id may have been reused since).</summary>
    /// <exception cref="GameProcessException">
    /// <c>host.unverifiable</c>: the process could not be opened to find out (access denied, say). That is not "gone": a
    /// live game may be behind it, so the caller keeps its record and does not start the server beside it.
    /// </exception>
    bool IsAlive(GameHostReference host);

    /// <summary>
    /// Kills the host process; its game dies with its Job. True when the host is gone afterwards (it was, or it ended
    /// within a bounded wait); false when it could not be ended or checked: the caller keeps its record.
    /// </summary>
    bool Kill(GameHostReference host);

    /// <summary>Whether <paramref name="pipe"/> (connected) is served by the host process itself.</summary>
    bool Serves(PipeStream pipe, GameHostReference host);

    /// <summary>Removes copies of the program no host runs from anymore (<paramref name="running"/> are the hosts left).</summary>
    void CleanUpPrograms(IEnumerable<GameHostReference> running)
    {
    }
}

/// <summary>
/// Where the agent keeps its host records (docs/architecture/06, "Server hosts"): a host is recorded as soon as it is
/// started, before its game, so an agent that dies meanwhile leaves no game its next run cannot find.
/// </summary>
public interface IGameHostRecords
{
    /// <summary>A host was started for <paramref name="serverId"/>; its game is not started yet.</summary>
    void HostLaunched(Guid serverId, GameHostReference host);

    /// <summary>The host was ended because its game could not be started.</summary>
    void HostAbandoned(Guid serverId, GameHostReference host);
}

/// <summary>
/// Game processes that outlive the agent (docs/architecture/06, "Server hosts"): each one is started by a host process
/// of its own, and found again after the agent restarts. What has nothing to do with the game's process (killing the
/// server account's stray processes) is done by the local manager.
/// </summary>
public sealed partial class GameHostManager(IGameProcessManager local, IGameHostLauncher launcher, ILogger<GameHostManager> logger)
    : IGameProcessManager
{
    /// <summary>The code of the <see cref="GameProcessException"/> a launcher throws when it cannot tell whether a host runs.</summary>
    public const string HostUnverifiable = "host.unverifiable";

    /// <summary>How long a new host gets to open its pipe and answer.</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long an existing host gets to open its pipe, and then to answer, when the agent comes back. Generous: one that
    /// does not answer is killed with its game, and a machine that is starting can be slow.
    /// </summary>
    public TimeSpan ReattachTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a request to a running host may take (a release waits for the game's tree to end).</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a host gets to kill its game's tree; then the host is killed, and the game with it. A kill is acknowledged
    /// after that at most, well within the panel's 10 s wait for the ack (killing a Job takes no time).
    /// </summary>
    public TimeSpan TerminateTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Told about each host as soon as it is started (set by the agent's server host).</summary>
    public IGameHostRecords? Records { get; set; }

    internal ILogger Logger => logger;

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

        // Before the game: starting it can take seconds (an account's first logon), and an agent that dies meanwhile
        // must find the host, and the game it may have started, when it comes back.
        Records?.HostLaunched(spec.ServerId, host);
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
                await process.DisposeAsync().ConfigureAwait(false); // released (killed when it does not answer)
            }
            else if (!End(host))
            {
                // Could not be ended (or checked): its record stays, so the next run of the agent finds it.
                throw;
            }

            Records?.HostAbandoned(spec.ServerId, host);
            throw;
        }
    }

    /// <summary>
    /// The host a previous run of the agent left for a server, with its output from <paramref name="outputFrom"/> on;
    /// null when it is gone or has no game. A host that is alive but does not answer is killed (with its game): it must
    /// never run beside a new start of the same server.
    /// </summary>
    /// <exception cref="GameProcessException">
    /// <c>host.incompatible</c>: the host runs another version of the protocol (an update changed it). It is left running
    /// with its game: killing it would end every game on the node, and the caller must not start the server beside it.
    /// </exception>
    /// <exception cref="GameProcessException">
    /// <see cref="HostUnverifiable"/>: the host could not be checked (not the same as gone). Like an incompatible host it is
    /// left alone, and the caller must not start the server beside it.
    /// </exception>
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
            process = await HostedGameProcess.ConnectAsync(this, host, ReattachTimeout, ReattachTimeout, cancellationToken).ConfigureAwait(false);
            if (process.Status.ProcessId is null)
            {
                await process.DisposeAsync().ConfigureAwait(false);
                return null;
            }

            await process.SubscribeAsync(outputFrom, cancellationToken).ConfigureAwait(false);
            LogReattached(process.ServerId, host.ProcessId, process.ProcessId);
            return process;
        }
        catch (GameProcessException ex) when (ex.Code == "host.incompatible")
        {
            LogIncompatible(host.ProcessId, ex.Message);
            throw;
        }
        catch (GameProcessException ex)
        {
            LogReattachFailed(host.ProcessId, ex.Code, ex.Message);
            if (process is not null)
            {
                await process.DisposeAsync().ConfigureAwait(false);
            }

            if (!End(host))
            {
                // A host that does not answer and cannot be ended: its game may run on, so it is not "gone".
                throw new GameProcessException(HostUnverifiable, $"The host process {host.ProcessId} does not answer and could not be ended.", ex);
            }

            return null;
        }
    }

    public int TerminateStrayProcesses(ProcessIdentity identity) => local.TerminateStrayProcesses(identity);

    /// <summary>Once the hosts of a previous run are taken over: older copies of the program nobody runs from can go.</summary>
    public void CleanUpPrograms(IEnumerable<GameHostReference> running) => launcher.CleanUpPrograms(running);

    /// <summary>Kills a host nobody will take over (its server is gone, say); its game dies with it. False when it was gone already.</summary>
    public bool Kill(GameHostReference host)
    {
        ArgumentNullException.ThrowIfNull(host);
        try
        {
            return launcher.IsAlive(host) && launcher.Kill(host);
        }
        catch (GameProcessException ex) when (ex.Code == HostUnverifiable)
        {
            LogUnverifiable(host.ProcessId, ex.Message);
            return false;
        }
    }

    /// <summary>Whether the host is gone, checked without touching it; false while it runs or cannot be checked.</summary>
    public bool IsGone(GameHostReference host)
    {
        ArgumentNullException.ThrowIfNull(host);
        try
        {
            return !launcher.IsAlive(host);
        }
        catch (GameProcessException ex) when (ex.Code == HostUnverifiable)
        {
            return false;
        }
    }

    /// <summary>
    /// Ends a host nobody will take over and says whether it is gone now: true when it was gone already or ended within
    /// the launcher's bounded wait; false when it could not be checked or ended, and the caller keeps its record.
    /// </summary>
    public bool End(GameHostReference host)
    {
        ArgumentNullException.ThrowIfNull(host);
        try
        {
            return !launcher.IsAlive(host) || launcher.Kill(host);
        }
        catch (GameProcessException ex) when (ex.Code == HostUnverifiable)
        {
            LogUnverifiable(host.ProcessId, ex.Message);
            return false;
        }
    }

    internal bool Serves(PipeStream pipe, GameHostReference host) => launcher.Serves(pipe, host);

    /// <summary>Whether the host may still be running: a host that cannot be checked is taken to be.</summary>
    private bool MayBeAlive(GameHostReference host)
    {
        try
        {
            return launcher.IsAlive(host);
        }
        catch (GameProcessException ex) when (ex.Code == HostUnverifiable)
        {
            LogUnverifiable(host.ProcessId, ex.Message);
            return true;
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
                return await HostedGameProcess.ConnectAsync(this, host, TimeSpan.FromMilliseconds(500), StartTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (GameProcessException ex) when (ex.Code == "host.unreachable" && DateTimeOffset.UtcNow < deadline && MayBeAlive(host))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
            catch (GameProcessException ex) when (ex.Code is "host.unreachable" or "host.incompatible")
            {
                throw new GameProcessException("host.start_failed", MayBeAlive(host)
                    ? "The server's host process did not open its pipe or answer in time."
                    : "The server's host process ended right after it started (see the agent's logs folder).", ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Server {ServerId}: found its game (process {ProcessId}) in its host process {HostProcessId}")]
    private partial void LogReattached(Guid serverId, int hostProcessId, int processId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The host process {HostProcessId} of a server does not answer ({Code}: {Reason}); killing it")]
    private partial void LogReattachFailed(int hostProcessId, string code, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The host process {HostProcessId} of a server could not be checked or ended ({Reason}): its record is kept")]
    private partial void LogUnverifiable(int hostProcessId, string reason);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The host process {HostProcessId} of a server runs another version of the agent ({Reason}): left running with its game, which this agent cannot manage")]
    private partial void LogIncompatible(int hostProcessId, string reason);
}
