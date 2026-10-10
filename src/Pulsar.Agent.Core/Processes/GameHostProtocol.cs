using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pulsar.Contracts;

namespace Pulsar.Agent.Core.Processes;

/// <summary>
/// Where a server's host process is (docs/architecture/06, "Server hosts"): what the agent keeps to find it again after
/// it restarts. The pipe name is random per host, so nobody can create it first; the process id and its start time say
/// which process must be serving it.
/// </summary>
public sealed record GameHostReference(string Pipe, int ProcessId, DateTimeOffset ProcessStartedAt);

/// <summary>What a host says about its game when the agent connects.</summary>
/// <param name="ProcessId">The game's main process; null while no game was started.</param>
/// <param name="ExitCode">Set once the main process exited (perhaps while no agent was connected).</param>
/// <param name="OutputStart">The oldest output the host still keeps (as a character offset since the game started).</param>
/// <param name="OutputEnd">The offset right after the newest output.</param>
public sealed record GameHostStatus(
    Guid ServerId,
    int? ProcessId,
    DateTimeOffset? StartedAt,
    int? ExitCode,
    bool ReachedMemoryLimit,
    bool SupportsInterrupt,
    long OutputStart,
    long OutputEnd);

/// <summary>A <see cref="ProcessLaunchSpec"/> as it travels to a host.</summary>
public sealed record GameHostLaunch(
    Guid ServerId,
    string ServerRoot,
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    ConsoleMode ConsoleMode,
    ConsoleEncoding Encoding,
    string? Account,
    ProcessLimits? Limits)
{
    public static GameHostLaunch From(ProcessLaunchSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return new GameHostLaunch(spec.ServerId, spec.ServerRoot, spec.Executable, spec.Arguments, spec.WorkingDirectory, spec.Environment,
            spec.ConsoleMode, spec.Encoding, spec.Identity is ProcessIdentity.AccountIdentity account ? account.Name : null, spec.Limits);
    }

    public ProcessLaunchSpec ToSpec() => new(ServerId, ServerRoot, Executable, Arguments, WorkingDirectory, Environment, ConsoleMode, Encoding,
        Account is null ? ProcessIdentity.CurrentUser : ProcessIdentity.Account(Account), Limits);

    // The environment can hold a game's settings: never in logs.
    public override string ToString() => $"GameHostLaunch {{ ServerId = {ServerId} }}";
}

/// <summary>
/// The messages between the agent and a server's host process, over the host's named pipe: requests with an id that
/// the reply carries, and events from the host (output, the game's exit). Each frame is a 32-bit little-endian length
/// followed by that many bytes of JSON.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(HostHello), "hello")]
[JsonDerivedType(typeof(HostStart), "start")]
[JsonDerivedType(typeof(HostSubscribe), "subscribe")]
[JsonDerivedType(typeof(HostInput), "input")]
[JsonDerivedType(typeof(HostInterrupt), "interrupt")]
[JsonDerivedType(typeof(HostTerminate), "terminate")]
[JsonDerivedType(typeof(HostLimits), "limits")]
[JsonDerivedType(typeof(HostSample), "sample")]
[JsonDerivedType(typeof(HostRelease), "release")]
[JsonDerivedType(typeof(HostReply), "reply")]
[JsonDerivedType(typeof(HostOutput), "output")]
[JsonDerivedType(typeof(HostOutputLost), "lost")]
[JsonDerivedType(typeof(HostOutputEnded), "ended")]
[JsonDerivedType(typeof(HostExited), "exited")]
public abstract record HostFrame;

public abstract record HostRequest(int Id) : HostFrame;

/// <summary>The first request on every connection; answered with the host's <see cref="GameHostStatus"/>.</summary>
public sealed record HostHello(int Id, int Protocol) : HostRequest(Id);

/// <summary>Starts the game (once per host).</summary>
public sealed record HostStart(int Id, GameHostLaunch Launch) : HostRequest(Id);

/// <summary>Sends the output from <paramref name="From"/> on (what the host still keeps), then as it comes.</summary>
public sealed record HostSubscribe(int Id, long From) : HostRequest(Id);

public sealed record HostInput(int Id, string Text) : HostRequest(Id)
{
    // Console commands can carry passwords (rcon_password, say).
    public override string ToString() => $"HostInput {{ Id = {Id} }}";
}

public sealed record HostInterrupt(int Id) : HostRequest(Id);

public sealed record HostTerminate(int Id, int ExitCode) : HostRequest(Id);

public sealed record HostLimits(int Id, ProcessLimits? Limits) : HostRequest(Id);

/// <summary>Answered with the tree's usage (its process count included).</summary>
public sealed record HostSample(int Id) : HostRequest(Id);

/// <summary>Kills whatever is left of the game's tree and ends the host: the agent is done with it.</summary>
public sealed record HostRelease(int Id) : HostRequest(Id);

/// <summary>The answer to a request; <see cref="ErrorCode"/> is set when it failed.</summary>
public sealed record HostReply(int Id, string? ErrorCode = null, string? Error = null, GameHostStatus? Status = null, ProcessUsage? Usage = null)
    : HostFrame;

/// <summary>Console output; <paramref name="Offset"/> is where <paramref name="Text"/> starts.</summary>
public sealed record HostOutput(long Offset, DateTimeOffset At, string Text) : HostFrame;

/// <summary>Output the host no longer keeps (it came while no agent was reading, and more came after it).</summary>
public sealed record HostOutputLost(long From, long To) : HostFrame;

/// <summary>The game's output is closed: nothing follows.</summary>
public sealed record HostOutputEnded(long End) : HostFrame;

/// <summary>The game's main process exited; <paramref name="RemainingProcesses"/> of its tree were still alive then.</summary>
public sealed record HostExited(int ExitCode, bool ReachedMemoryLimit, int RemainingProcesses) : HostFrame;

public static class GameHostProtocol
{
    /// <summary>
    /// Raised when the frames or their meaning change: a host refuses an agent of another version, which then leaves
    /// that host (and its game) running rather than end every game on the node (docs/architecture/06, "Server hosts").
    /// </summary>
    public const int Version = 1;

    /// <summary>No frame is larger (output travels in pieces far smaller than this).</summary>
    public const int MaxFrameBytes = 4 * 1024 * 1024;

    /// <summary>The longest piece of output in one frame.</summary>
    public const int MaxOutputCharacters = 64 * 1024;

    /// <summary>The pipe of a new host: unique and unguessable (docs/architecture/06, "Server hosts").</summary>
    public static string NewPipeName(Guid serverId) =>
        $"pulsar-host-{serverId:N}-{System.Security.Cryptography.RandomNumberGenerator.GetHexString(16, lowercase: true)}";

    private static readonly JsonSerializerOptions s_options = ProtocolJson.Options;

    public static async Task WriteAsync(Stream stream, HostFrame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var json = JsonSerializer.SerializeToUtf8Bytes(frame, s_options);
        if (json.Length > MaxFrameBytes)
        {
            throw new InvalidOperationException($"A {frame.GetType().Name} frame is larger than {MaxFrameBytes} bytes.");
        }

        var buffer = new byte[4 + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, json.Length);
        json.CopyTo(buffer, 4);
        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The next frame; null when the other end closed the connection.</summary>
    /// <exception cref="InvalidDataException">A frame that is too large or not one of the protocol's.</exception>
    public static async Task<HostFrame?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[4];
        if (!await FillAsync(stream, header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxFrameBytes)
        {
            throw new InvalidDataException($"A frame of {length} bytes is not valid.");
        }

        var json = new byte[length];
        if (!await FillAsync(stream, json, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The connection closed in the middle of a frame.");
        }

        try
        {
            return JsonSerializer.Deserialize<HostFrame>(json, s_options) ?? throw new InvalidDataException("An empty frame.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // A frame without a kind fails as NotSupportedException (an abstract type): as invalid as bad JSON.
            throw new InvalidDataException($"A frame is not valid: {ex.Message}", ex);
        }
    }

    private static async Task<bool> FillAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (filled == 0)
                {
                    return false;
                }

                throw new InvalidDataException("The connection closed in the middle of a frame.");
            }

            filled += read;
        }

        return true;
    }
}
