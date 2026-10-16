using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Pulsar.Agent.Core.Processes;

/// <summary>When <see cref="PanelBlockKeeper"/> looks again.</summary>
public sealed class PanelBlockOptions
{
    /// <summary>How often the panel's names are looked up again and every account's block rewritten if it changed.</summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How often the agent's configuration is compared with the one the block was made for (a move to the agents' address).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long one name's lookup may take: past it, the addresses known for it stay.</summary>
    public TimeSpan ResolveTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The least time between two refreshes asked for by changes of the machine's network addresses (which whoever sends
    /// router advertisements on its network can cause at will): the requests in between wait for it, as one.
    /// </summary>
    public TimeSpan MinRequestInterval { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Keeps the outbound block of every server's account (docs/architecture/06, "Windows Firewall") up to date, as a property
/// of the accounts rather than of a start: made for all of them when the agent starts (games it takes over included) with
/// what is known, without a lookup; then refreshed in the background when the agent's panel addresses change (a move to
/// the agents' address), when the machine's network addresses change (at most every
/// <see cref="PanelBlockOptions.MinRequestInterval"/>), and every few minutes. Lookups are bounded, and the addresses a
/// name had stay listed when it does not resolve (<see cref="PanelAddressBook"/>). Nothing here fails a start: failures
/// are logged, and the next refresh tries again.
/// </summary>
/// <param name="panels">The agent's panel addresses as it uses them now (its configuration in memory): the panel and its fallback.</param>
public sealed partial class PanelBlockKeeper(
    IServerFirewall firewall,
    Func<IReadOnlyList<Uri>> panels,
    PanelAddressBook book,
    PanelBlockOptions options,
    TimeProvider clock,
    ILogger<PanelBlockKeeper> logger,
    Func<string, CancellationToken, Task<IReadOnlyList<IPAddress>>>? resolve = null,
    Func<IEnumerable<IPAddress>>? ownAddresses = null)
{
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<IPAddress>>> _resolve =
        resolve ?? (async (host, cancellationToken) => await Dns.GetHostAddressesAsync(host, cancellationToken));

    private Func<IReadOnlyCollection<ServerAccount>> _servers = () => [];
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IReadOnlyList<Uri> _refreshed = [];
    private DateTimeOffset _nextRefresh = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRequested = DateTimeOffset.MinValue;
    private volatile PanelTargets? _current;
    private Task _loop = Task.CompletedTask;

    /// <summary>The targets the accounts were last blocked from (null before the first time, or when the firewall is not managed).</summary>
    public PanelTargets? Current => _current;

    /// <summary>The background refreshes; ends when the token given to <see cref="Start"/> is cancelled.</summary>
    public Task Completion => _loop;

    /// <summary>
    /// Blocks the accounts of <paramref name="servers"/> (read again at each refresh) at once, with the addresses known
    /// from earlier runs, then keeps them refreshed until <paramref name="stopping"/>.
    /// </summary>
    public void Start(Func<IReadOnlyCollection<ServerAccount>> servers, CancellationToken stopping)
    {
        ArgumentNullException.ThrowIfNull(servers);
        if (!firewall.Enabled)
        {
            return;
        }

        _servers = servers;
        try
        {
            Apply(Panels());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogRefreshFailed(ex.Message);
        }

        _loop = Task.Run(() => RunAsync(stopping), CancellationToken.None);
    }

    /// <summary>Asks for a refresh now (the machine's network addresses changed).</summary>
    public void Request() => Volatile.Read(ref _wake).TrySetResult();

    /// <summary>
    /// Blocks a new server's account with the current targets, without waiting for the next refresh (also when a server
    /// with its short id was deleted earlier: see <see cref="IServerFirewall.Provisioned"/>).
    /// </summary>
    public void Ensure(ServerAccount server)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (!firewall.Enabled)
        {
            return;
        }

        firewall.Provisioned(server.ShortId);
        if (_current is { } targets)
        {
            Block([server], targets);
        }
    }

    private async Task RunAsync(CancellationToken stopping)
    {
        var requested = false;
        try
        {
            while (true)
            {
                // Swapped before looking, so a request made while refreshing is not lost; one made too soon after the
                // last waits for its turn (looked at again at each poll).
                requested |= Interlocked.Exchange(ref _wake, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task.IsCompleted;
                var current = Panels();
                var now = clock.GetUtcNow();
                if ((requested && now - _lastRequested >= options.MinRequestInterval) || !current.SequenceEqual(_refreshed) || now >= _nextRefresh)
                {
                    if (requested)
                    {
                        _lastRequested = now;
                        requested = false;
                    }

                    try
                    {
                        await RefreshAsync(current, stopping);
                    }
                    catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
                    {
                        // Never the end of the refreshes: the next one tries again.
                        LogRefreshFailed(ex.Message);
                    }
                }

                using var waiting = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                await Task.WhenAny(Volatile.Read(ref _wake).Task, Task.Delay(options.PollInterval, clock, waiting.Token));
                await waiting.CancelAsync();
                stopping.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshAsync(IReadOnlyList<Uri> current, CancellationToken stopping)
    {
        _refreshed = current;
        _nextRefresh = clock.GetUtcNow() + options.RefreshInterval;
        var hosts = Hosts(current);
        foreach (var host in hosts.Where(host => !IPAddress.TryParse(host, out _)))
        {
            using var timeout = new CancellationTokenSource(options.ResolveTimeout, clock);
            using var lookup = CancellationTokenSource.CreateLinkedTokenSource(stopping, timeout.Token);
            try
            {
                // WaitAsync as well: a lookup that does not honour its cancellation is left behind, not waited for.
                book.Learn(host, await _resolve(host, lookup.Token).WaitAsync(lookup.Token));
            }
            catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
            {
                LogLookupTimedOut(host, options.ResolveTimeout);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
            {
                LogLookupFailed(host, ex.Message);
            }
        }

        book.Retain(hosts);
        try
        {
            book.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogBookNotSaved(ex.Message);
        }

        Apply(current);
    }

    private void Apply(IReadOnlyList<Uri> current)
    {
        var known = Hosts(current).SelectMany(host => IPAddress.TryParse(host, out var literal) ? [literal] : book.Known([host]));
        if (PanelTargets.For(current, known, OwnAddresses()) is not { } targets)
        {
            return;
        }

        if (!targets.Equals(_current))
        {
            LogTargets(targets.RemoteAddresses, targets.RemotePorts);
        }

        _current = targets;
        Block(null, targets);
    }

    /// <summary>Every server (<paramref name="servers"/> null) or the given ones; a failure waits for the next refresh.</summary>
    private void Block(IReadOnlyCollection<ServerAccount>? servers, PanelTargets targets)
    {
        try
        {
            firewall.BlockPanel(servers ?? _servers(), targets);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogBlockFailed(ex.Message);
        }
    }

    private IReadOnlyList<Uri> Panels()
    {
        try
        {
            return panels();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogPanelsUnreadable(ex.Message);
            return _refreshed;
        }
    }

    private List<IPAddress> OwnAddresses()
    {
        try
        {
            return ownAddresses is null
                ? [.. NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)]
                : [.. ownAddresses()];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogOwnAddressesUnreadable(ex.Message);
            return [];
        }
    }

    private static List<string> Hosts(IReadOnlyList<Uri> current) =>
        [.. current.Select(panel => panel.IdnHost).Distinct(StringComparer.OrdinalIgnoreCase)];

    [LoggerMessage(Level = LogLevel.Information, Message = "Game servers' accounts are kept from the panel at {Addresses} on TCP {Ports}")]
    private partial void LogTargets(string addresses, string ports);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The panel's name {Host} did not resolve within {Timeout}: the addresses known for it stay blocked")]
    private partial void LogLookupTimedOut(string host, TimeSpan timeout);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The panel's name {Host} could not be resolved ({Reason}): the addresses known for it stay blocked")]
    private partial void LogLookupFailed(string host, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The panel's known addresses could not be saved: {Reason}")]
    private partial void LogBookNotSaved(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The firewall rules that keep game servers from the panel could not be updated: {Reason}")]
    private partial void LogBlockFailed(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The agent's panel addresses could not be read: {Reason}")]
    private partial void LogPanelsUnreadable(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "This machine's network addresses could not be read: {Reason}")]
    private partial void LogOwnAddressesUnreadable(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The block that keeps game servers from the panel could not be refreshed: {Reason}")]
    private partial void LogRefreshFailed(string reason);
}

/// <summary>
/// The addresses each of the panel's names has resolved to, kept in the agent's state directory (readable only by SYSTEM
/// and Administrators) so that a restart, or a name that stops resolving, does not take them out of the block. Whoever can
/// forge a lookup's answer chooses what it adds (docs/architecture/08, T31), so: one answer adds at most
/// <see cref="MaxAddressesPerAnswer"/> addresses; an address is blocked only once confirmed, answered by two lookups at
/// least <see cref="ConfirmAfter"/> apart, and one that is not goes after <see cref="UnconfirmedRetention"/>; a confirmed
/// one goes once its name has resolved without it for <see cref="Retention"/>. At most <see cref="MaxAddressesPerName"/>
/// confirmed addresses are kept (a name behind a large CDN): those seen longest ago make room for newly confirmed ones,
/// never for unconfirmed ones (at most <see cref="MaxAddressesPerAnswer"/> more).
/// </summary>
public sealed partial class PanelAddressBook(string file, TimeProvider clock, ILogger logger)
{
    public const int MaxAddressesPerName = 64;

    public const int MaxAddressesPerAnswer = 16;

    public static TimeSpan ConfirmAfter { get; } = TimeSpan.FromMinutes(5);

    private readonly Lock _lock = new();
    private Dictionary<string, List<Seen>>? _names;

    public static TimeSpan Retention { get; } = TimeSpan.FromDays(30);

    public static TimeSpan UnconfirmedRetention { get; } = TimeSpan.FromHours(1);

    /// <summary>Every confirmed address of <paramref name="hosts"/>, oldest first.</summary>
    public IReadOnlyList<IPAddress> Known(IEnumerable<string> hosts)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        lock (_lock)
        {
            var names = Names();
            return [.. hosts.SelectMany(host => names.TryGetValue(Key(host), out var seen) ? seen : []).Where(s => s.Confirmed)
                .Select(s => IPAddress.TryParse(s.Address, out var address) ? address : null).OfType<IPAddress>()];
        }
    }

    /// <summary>What a lookup of <paramref name="host"/> answered; an empty answer changes nothing.</summary>
    public void Learn(string host, IEnumerable<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(addresses);
        var answered = addresses.Select(a => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).Select(a => a.ToString()).Distinct(StringComparer.Ordinal).ToList();
        if (answered.Count == 0)
        {
            return;
        }

        if (answered.Count > MaxAddressesPerAnswer)
        {
            LogOversizedAnswer(host, answered.Count, MaxAddressesPerAnswer);
            answered = answered[..MaxAddressesPerAnswer];
        }

        var now = clock.GetUtcNow();
        lock (_lock)
        {
            var names = Names();
            var seen = names.TryGetValue(Key(host), out var known) ? known : names[Key(host)] = [];
            foreach (var address in answered)
            {
                var index = seen.FindIndex(s => s.Address == address);
                if (index >= 0)
                {
                    seen[index] = seen[index] with { LastSeen = now };
                }
                else
                {
                    seen.Add(new Seen(address, now, now));
                }
            }

            seen.RemoveAll(s => now - s.LastSeen > (s.Confirmed ? Retention : UnconfirmedRetention));
            Evict(seen, confirmed: false, MaxAddressesPerAnswer);
            Evict(seen, confirmed: true, MaxAddressesPerName);
        }
    }

    /// <summary>Forgets the names the agent no longer uses.</summary>
    public void Retain(IEnumerable<string> hosts)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        var keep = hosts.Select(Key).ToHashSet(StringComparer.Ordinal);
        lock (_lock)
        {
            var names = Names();
            foreach (var name in names.Keys.Where(name => !keep.Contains(name)).ToList())
            {
                names.Remove(name);
            }
        }
    }

    /// <summary>Writes the book next to its file and swaps it in: never half written.</summary>
    public void Save()
    {
        byte[] content;
        lock (_lock)
        {
            content = JsonSerializer.SerializeToUtf8Bytes(Names());
        }

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, file, overwrite: true);
    }

    private static string Key(string host) => host.ToLowerInvariant();

    /// <summary>Keeps the <paramref name="max"/> confirmed (or unconfirmed) addresses seen last.</summary>
    private static void Evict(List<Seen> seen, bool confirmed, int max)
    {
        while (seen.Count(s => s.Confirmed == confirmed) > max)
        {
            seen.Remove(seen.Where(s => s.Confirmed == confirmed).MinBy(s => s.LastSeen)!);
        }
    }

    private Dictionary<string, List<Seen>> Names()
    {
        if (_names is not null)
        {
            return _names;
        }

        _names = new Dictionary<string, List<Seen>>(StringComparer.Ordinal);
        if (!File.Exists(file))
        {
            return _names;
        }

        try
        {
            using var stream = File.OpenRead(file);
            foreach (var (name, seen) in JsonSerializer.Deserialize<Dictionary<string, List<Seen>?>>(stream) ?? [])
            {
                _names[Key(name)] = [.. (seen ?? []).Where(s => s is not null && IPAddress.TryParse(s.Address, out _))];
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Whatever is in it: never a failed start.
            _names.Clear();
            LogUnreadable(file, ex.Message);
        }

        return _names;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The panel's known addresses in {File} could not be read ({Reason}): starting from none")]
    private partial void LogUnreadable(string file, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The panel's name {Host} resolved to {Count} addresses: only the first {Max} are taken")]
    private partial void LogOversizedAnswer(string host, int count, int max);

    /// <param name="FirstSeen">When a lookup first answered it (since it was last forgotten).</param>
    private sealed record Seen(string Address, DateTimeOffset LastSeen, DateTimeOffset FirstSeen)
    {
        public bool Confirmed => LastSeen - FirstSeen >= ConfirmAfter;
    }
}
