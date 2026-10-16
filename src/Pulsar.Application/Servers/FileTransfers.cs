using System.Collections.Concurrent;
using Pulsar.Contracts;
using Pulsar.Domain.Common;

namespace Pulsar.Application.Servers;

/// <summary>
/// How many transfers run at once (docs/architecture/03, "Large transfers"): each one is a connection of its node to the
/// panel, and a node holds only so many (<c>Agents:MaxConnectionsPerNode</c> on the agents' endpoint). Transfers of a node
/// take <paramref name="PerNode"/> of them at most, the rest staying for its hub and its rotation, and one user's transfers
/// on a node <paramref name="PerUser"/>; a user's second and third only while two places stay free for users with none
/// running, so that it takes several users to fill a node.
/// </summary>
public sealed record FileTransferLimits(int PerNode, int PerUser)
{
    /// <summary>For the default 16 connections a node may hold.</summary>
    public static FileTransferLimits Default { get; } = ForNodeConnections(16);

    /// <summary>Half of the <paramref name="connections"/> a node may hold, and three of those for one user.</summary>
    public static FileTransferLimits ForNodeConnections(int connections)
    {
        var perNode = Math.Max(1, connections / 2);
        return new FileTransferLimits(perNode, Math.Min(3, perNode));
    }
}

/// <summary>
/// Meeting point of the two requests of a file transfer (docs/architecture/03, "Large transfers"): the browser's,
/// waiting in <see cref="ServerFiles"/>, and the agent's, which claims the transfer with its single-use ticket. The
/// content is piped from one request to the other as it arrives, never stored on the panel. A ticket works once, only
/// for the node it was issued to, and only within <see cref="ClaimTimeout"/>. A transfer holds its place in its node's and
/// its user's <see cref="FileTransferLimits"/> from its start until it is disposed; one over them is not started. A
/// transfer outlives neither its user's sessions nor the API key it was started with (<see cref="EndUser"/>,
/// <see cref="EndApiKey"/>), nor <see cref="MaxDuration"/> for its size: then it is aborted (<see cref="Transfer.Aborted"/>).
/// </summary>
public sealed class FileTransfers(TimeProvider clock, FileTransferLimits? limits = null)
{
    public static readonly TimeSpan ClaimTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How long an upload the agent claimed may go without moving before the browser's request gives up.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The least a transfer may last whatever its size: a small file over a slow link, an agent busy for a moment.
    /// </summary>
    public static readonly TimeSpan MinimumDuration = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The slowest average a transfer may have (512 kbit/s): a 1 GiB upload may take 4.5 hours, not the weeks Kestrel's
    /// least data rate (240 bytes a second) would allow while it holds one of its node's few places.
    /// </summary>
    public const long MinimumBytesPerSecond = 64 * 1024;

    /// <summary>A user with transfers running starts another only while this many places of the node stay free after it.</summary>
    private const int KeptForOthers = 2;

    private readonly ConcurrentDictionary<string, Transfer> _pending = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock = clock;
    private readonly FileTransferLimits _limits = limits ?? FileTransferLimits.Default;
    private readonly Lock _lock = new();
    private readonly Dictionary<NodeId, int> _byNode = [];
    private readonly Dictionary<(NodeId, UserId), int> _byUser = [];
    private readonly HashSet<Transfer> _running = [];

    public FileTransferLimits Limits => _limits;

    /// <summary>
    /// Content for the agent (an upload) for <paramref name="user"/> (null: the panel itself), through the API key
    /// <paramref name="apiKey"/> if any: <paramref name="content"/> is the browser's request body. Null when the node or
    /// the user has as many transfers running as they may.
    /// </summary>
    public Upload? BeginUpload(NodeId node, UserId? user, Stream content, long size, Guid? apiKey = null)
    {
        var upload = Start(new Upload(this, TransferTicket.New(), node, user, apiKey, content, size, streamed: false));
        upload?.Bound(size);
        return upload;
    }

    /// <summary>
    /// Like <see cref="BeginUpload"/>, for content whose length is not known in advance (an SFTP client's writes): it ends
    /// where <paramref name="content"/> ends, at most <paramref name="maxSize"/> bytes (more fails it), and lasts at most
    /// <see cref="MaxDuration"/> of <paramref name="maxSize"/>, as an upload of that size would.
    /// </summary>
    public Upload? BeginStreamedUpload(NodeId node, UserId? user, Stream content, long maxSize, Guid? apiKey = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxSize);
        var upload = Start(new Upload(this, TransferTicket.New(), node, user, apiKey, content, maxSize, streamed: true));
        upload?.Bound(maxSize);
        return upload;
    }

    /// <summary>Content from the agent (a download, the editor opening a file, a backup); null as for an upload.</summary>
    public Download? BeginDownload(NodeId node, UserId? user, Guid? apiKey = null) =>
        Start(new Download(this, TransferTicket.New(), node, user, apiKey));

    /// <summary>How long a transfer of <paramref name="size"/> bytes may last: its size at the least rate, five minutes at least.</summary>
    public static TimeSpan MaxDuration(long size)
    {
        var duration = TimeSpan.FromSeconds((double)Math.Max(0, size) / MinimumBytesPerSecond);
        return duration > MinimumDuration ? duration : MinimumDuration;
    }

    /// <summary>The transfers running on <paramref name="node"/>.</summary>
    public int Running(NodeId node)
    {
        lock (_lock)
        {
            return _byNode.GetValueOrDefault(node);
        }
    }

    /// <summary>The user's sessions ended (signed out, disabled, deleted, a new password or roles): their transfers end.</summary>
    public void EndUser(UserId user) => End(t => t.User == user);

    /// <summary>A role changed: any user's permissions may have; every user's transfers end (the panel's own go on).</summary>
    public void EndAll() => End(t => t.User is not null);

    /// <summary>The API key was revoked: the transfers started with it end.</summary>
    public void EndApiKey(Guid apiKey) => End(t => t.ApiKey == apiKey);

    /// <summary>All of the user's API keys were revoked: the transfers started with any of them end.</summary>
    public void EndApiKeys(UserId user) => End(t => t.User == user && t.ApiKey is not null);

    /// <summary>The agent's <c>GET</c>: the upload it was told about, or null (unknown, used, expired or another node's).</summary>
    public Upload? ClaimUpload(string ticket, NodeId node) => Claim(ticket, node) as Upload;

    /// <summary>The agent's <c>PUT</c>: the download waiting for <paramref name="content"/>, or null.</summary>
    public Download? ClaimDownload(string ticket, NodeId node) => Claim(ticket, node) as Download;

    /// <summary>The agent's report on an upload it received; ignored unless it is that node's upload.</summary>
    public void Complete(NodeId node, TransferResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Ticket is { } ticket && _pending.TryGetValue(ticket, out var transfer) && transfer.Node == node && transfer is Upload upload)
        {
            upload.Finish(result);
        }
    }

    private Transfer? Claim(string ticket, NodeId node)
    {
        // Removed whether or not it matches: a ticket presented by the wrong node is burnt.
        if (!_pending.TryRemove(ticket, out var transfer))
        {
            return null;
        }

        if (transfer.Node != node || !transfer.TryClaim())
        {
            return null;
        }

        // Uploads stay reachable for the agent's report (Complete) until they end.
        if (transfer is Upload upload)
        {
            _pending[ticket] = upload;
        }

        return transfer;
    }

    private void Forget(Transfer transfer) => _pending.TryRemove(new KeyValuePair<string, Transfer>(transfer.Ticket, transfer));

    private T? Start<T>(T transfer) where T : Transfer
    {
        lock (_lock)
        {
            var node = transfer.Node;
            var ofNode = _byNode.GetValueOrDefault(node);
            var ofUser = transfer.User is { } id ? _byUser.GetValueOrDefault((node, id)) : 0;
            if (ofNode >= _limits.PerNode || ofUser >= _limits.PerUser || (ofUser > 0 && _limits.PerNode - ofNode - 1 < KeptForOthers))
            {
                return null;
            }

            _byNode[node] = ofNode + 1;
            if (transfer.User is { } taker)
            {
                _byUser[(node, taker)] = ofUser + 1;
            }

            _running.Add(transfer);
        }

        _pending[transfer.Ticket] = transfer;
        return transfer;
    }

    private void GiveBack(Transfer transfer)
    {
        lock (_lock)
        {
            _running.Remove(transfer);
            Decrement(_byNode, transfer.Node);
            if (transfer.User is { } id)
            {
                Decrement(_byUser, (transfer.Node, id));
            }
        }
    }

    private void End(Func<Transfer, bool> which)
    {
        Transfer[] ending;
        lock (_lock)
        {
            ending = [.. _running.Where(which)];
        }

        foreach (var transfer in ending)
        {
            transfer.Abort();
        }
    }

    private static void Decrement<TKey>(Dictionary<TKey, int> counts, TKey key) where TKey : notnull
    {
        if (counts[key] == 1)
        {
            counts.Remove(key);
        }
        else
        {
            counts[key]--;
        }
    }

    public abstract class Transfer : IDisposable
    {
        private readonly FileTransfers _owner;
        private readonly TaskCompletionSource _claimed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _aborted = new();
        private readonly Lock _lock = new();
        private ITimer? _deadline;
        private int _state; // 0 waiting, 1 claimed, 2 abandoned
        private int _ended;

        private protected Transfer(FileTransfers owner, string ticket, NodeId node, UserId? user, Guid? apiKey)
        {
            (_owner, Ticket, Node, User, ApiKey) = (owner, ticket, node, user, apiKey);
            Aborted = _aborted.Token;
        }

        public string Ticket { get; }

        public NodeId Node { get; }

        /// <summary>Whose transfer it is (null: the panel's own).</summary>
        public UserId? User { get; }

        /// <summary>The API key it was started with, if any.</summary>
        public Guid? ApiKey { get; }

        /// <summary>
        /// Cancelled when the transfer was ended before it was done (its user's sessions or its API key ended, or it took
        /// longer than <see cref="MaxDuration"/>): both of its requests stop, and the node never gets the rest.
        /// </summary>
        public CancellationToken Aborted { get; }

        /// <summary>Completes when the agent claims the transfer; fails after <see cref="ClaimTimeout"/>.</summary>
        public async Task WaitForClaimAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Aborted);
                await _claimed.Task.WaitAsync(ClaimTimeout, _owner._clock, waiting.Token);
            }
            catch (TimeoutException)
            {
                if (Interlocked.CompareExchange(ref _state, 2, 0) == 0)
                {
                    _owner.Forget(this);
                    throw;
                }

                // Claimed just as the time ran out.
            }
        }

        /// <summary>Ends the transfer for both sides (the browser left, or it is over).</summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            Interlocked.CompareExchange(ref _state, 2, 0);
            _owner.Forget(this);
            if (Interlocked.Exchange(ref _ended, 1) == 0)
            {
                lock (_lock)
                {
                    _deadline?.Dispose();
                }

                _owner.GiveBack(this);
            }
        }

        private protected TimeProvider Clock => _owner._clock;

        /// <summary>Ends the transfer before it is done; its requests stop and it gives its place back once disposed.</summary>
        internal void Abort()
        {
            if (Interlocked.CompareExchange(ref _state, 2, 0) == 0)
            {
                _owner.Forget(this);
            }

            _aborted.Cancel();
            Aborting();
        }

        /// <summary>From now on the transfer of <paramref name="size"/> bytes has <see cref="MaxDuration"/>.</summary>
        internal void Bound(long size)
        {
            lock (_lock)
            {
                if (_deadline is null && Volatile.Read(ref _ended) == 0)
                {
                    _deadline = _owner._clock.CreateTimer(static state => ((Transfer)state!).Abort(), this, MaxDuration(size), Timeout.InfiniteTimeSpan);
                }
            }
        }

        private protected abstract void Aborting();

        internal bool TryClaim()
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            {
                return false;
            }

            Claimed();
            _claimed.TrySetResult();
            return true;
        }

        private protected virtual void Claimed()
        {
        }
    }

    /// <summary>Browser → agent. The agent reads <see cref="Content"/>, then reports with <see cref="Complete"/>.</summary>
    public sealed class Upload : Transfer
    {
        private readonly TaskCompletionSource<TransferResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _lastProgress;
        private long _bytesSent;

        internal Upload(FileTransfers owner, string ticket, NodeId node, UserId? user, Guid? apiKey, Stream content, long size, bool streamed)
            : base(owner, ticket, node, user, apiKey) =>
            (Content, Size, Streamed) = (content, size, streamed);

        public Stream Content { get; }

        /// <summary>The content's length; the most it may hold when <see cref="Streamed"/>.</summary>
        public long Size { get; }

        /// <summary>
        /// The length is not known in advance (<see cref="BeginStreamedUpload"/>): the content goes to the agent until it
        /// ends, without a length, and only a clean end tells the agent it is complete.
        /// </summary>
        public bool Streamed { get; }

        /// <summary>The agent's report: the file is in place, or why not.</summary>
        public Task<TransferResult> Result => _result.Task;

        /// <summary>Completes once all of the content went to the agent; the agent's report follows shortly.</summary>
        public Task Sent => _sent.Task;

        /// <summary>All of the content went to the agent.</summary>
        public void ContentSent() => _sent.TrySetResult();

        /// <summary>How many bytes of the content went to the agent so far.</summary>
        public long BytesSent => Interlocked.Read(ref _bytesSent);

        /// <summary>Some more of the content (<paramref name="bytes"/> of it) went to the agent.</summary>
        public void Progressed(long bytes = 0)
        {
            Interlocked.Add(ref _bytesSent, bytes);
            Interlocked.Exchange(ref _lastProgress, Clock.GetTimestamp());
        }

        /// <summary>
        /// Completes when all of the content went to the agent or it reported; throws <see cref="TimeoutException"/> when
        /// nothing moved for <see cref="IdleTimeout"/> (an agent that stopped reading). A slow upload that moves is waited
        /// for however long it takes.
        /// </summary>
        public async Task WaitUntilSentAsync(CancellationToken cancellationToken)
        {
            var done = Task.WhenAny(Sent, Result);
            while (true)
            {
                var idle = Clock.GetElapsedTime(Interlocked.Read(ref _lastProgress));
                if (idle >= IdleTimeout)
                {
                    throw new TimeoutException("The upload stopped moving.");
                }

                try
                {
                    await done.WaitAsync(IdleTimeout - idle, Clock, cancellationToken);
                    return;
                }
                catch (TimeoutException)
                {
                    // Checked again against the latest progress.
                }
            }
        }

        private protected override void Claimed() => Progressed();

        /// <summary>The agent could not take the content (the browser stopped sending, say): the transfer failed.</summary>
        /// <param name="code">One of <see cref="FileErrors"/>: <c>files.too_large</c> for a streamed upload past its limit.</param>
        public void Fail(string message, string code = FileErrors.TransferFailed) => _result.TrySetResult(new TransferResult(Ticket, code, message, null));

        internal void Finish(TransferResult result) => _result.TrySetResult(result);

        private protected override void Aborting() =>
            Fail("The transfer was ended: its user's sessions or API key ended, or it took longer than its size allows.");

        protected override void Dispose(bool disposing)
        {
            _result.TrySetCanceled();
            _sent.TrySetCanceled();
            base.Dispose(disposing);
        }
    }

    /// <summary>Agent → browser. The agent's request body arrives through <see cref="Deliver"/>.</summary>
    public sealed class Download : Transfer
    {
        private readonly TaskCompletionSource<(Stream Content, long Length)> _content = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Download(FileTransfers owner, string ticket, NodeId node, UserId? user, Guid? apiKey) : base(owner, ticket, node, user, apiKey)
        {
        }

        /// <summary>The agent's content and its length, once it sends them.</summary>
        public Task<(Stream Content, long Length)> Content => _content.Task;

        /// <summary>
        /// The agent's content, which comes with the request that claimed the transfer; throws
        /// <see cref="TimeoutException"/> when an agent claimed it but sent nothing within <see cref="ClaimTimeout"/>.
        /// </summary>
        public Task<(Stream Content, long Length)> WaitForContentAsync(CancellationToken cancellationToken) =>
            _content.Task.WaitAsync(ClaimTimeout, Clock, cancellationToken);

        /// <summary>
        /// Hands the agent's request body over and waits until the browser side is done with it: true when all of it
        /// reached the browser. The agent's request must stay open until then.
        /// </summary>
        public Task<bool> DeliverAsync(Stream content, long length)
        {
            Bound(length);
            _content.TrySetResult((content, length));
            return _done.Task;
        }

        /// <summary>The browser side is done with the content (<paramref name="complete"/>: all of it was sent).</summary>
        public void Finish(bool complete) => _done.TrySetResult(complete);

        private protected override void Aborting()
        {
            _content.TrySetCanceled();
            _done.TrySetResult(false);
        }

        protected override void Dispose(bool disposing)
        {
            _content.TrySetCanceled();
            _done.TrySetResult(false);
            base.Dispose(disposing);
        }
    }
}
