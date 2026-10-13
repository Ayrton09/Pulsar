using System.Collections.Concurrent;
using Pulsar.Contracts;
using Pulsar.Domain.Common;

namespace Pulsar.Application.Servers;

/// <summary>
/// Meeting point of the two requests of a file transfer (docs/architecture/03, "Large transfers"): the browser's,
/// waiting in <see cref="ServerFiles"/>, and the agent's, which claims the transfer with its single-use ticket. The
/// content is piped from one request to the other as it arrives, never stored on the panel. A ticket works once, only
/// for the node it was issued to, and only within <see cref="ClaimTimeout"/>.
/// </summary>
public sealed class FileTransfers(TimeProvider clock)
{
    public static readonly TimeSpan ClaimTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How long an upload the agent claimed may go without moving before the browser's request gives up.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, Transfer> _pending = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock = clock;

    /// <summary>Content for the agent (an upload): <paramref name="content"/> is the browser's request body.</summary>
    public Upload BeginUpload(NodeId node, Stream content, long size)
    {
        var upload = new Upload(this, TransferTicket.New(), node, content, size);
        _pending[upload.Ticket] = upload;
        return upload;
    }

    /// <summary>Content from the agent (a download or the editor opening a file).</summary>
    public Download BeginDownload(NodeId node)
    {
        var download = new Download(this, TransferTicket.New(), node);
        _pending[download.Ticket] = download;
        return download;
    }

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

    public abstract class Transfer : IDisposable
    {
        private readonly FileTransfers _owner;
        private readonly TaskCompletionSource _claimed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _state; // 0 waiting, 1 claimed, 2 abandoned

        private protected Transfer(FileTransfers owner, string ticket, NodeId node) => (_owner, Ticket, Node) = (owner, ticket, node);

        public string Ticket { get; }

        public NodeId Node { get; }

        /// <summary>Completes when the agent claims the transfer; fails after <see cref="ClaimTimeout"/>.</summary>
        public async Task WaitForClaimAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _claimed.Task.WaitAsync(ClaimTimeout, _owner._clock, cancellationToken);
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
        }

        private protected TimeProvider Clock => _owner._clock;

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

        internal Upload(FileTransfers owner, string ticket, NodeId node, Stream content, long size) : base(owner, ticket, node) =>
            (Content, Size) = (content, size);

        public Stream Content { get; }

        public long Size { get; }

        /// <summary>The agent's report: the file is in place, or why not.</summary>
        public Task<TransferResult> Result => _result.Task;

        /// <summary>Completes once all of the content went to the agent; the agent's report follows shortly.</summary>
        public Task Sent => _sent.Task;

        /// <summary>All of the content went to the agent.</summary>
        public void ContentSent() => _sent.TrySetResult();

        /// <summary>Some more of the content went to the agent.</summary>
        public void Progressed() => Interlocked.Exchange(ref _lastProgress, Clock.GetTimestamp());

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
        public void Fail(string message) => _result.TrySetResult(new TransferResult(Ticket, FileErrors.TransferFailed, message, null));

        internal void Finish(TransferResult result) => _result.TrySetResult(result);

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

        internal Download(FileTransfers owner, string ticket, NodeId node) : base(owner, ticket, node)
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
            _content.TrySetResult((content, length));
            return _done.Task;
        }

        /// <summary>The browser side is done with the content (<paramref name="complete"/>: all of it was sent).</summary>
        public void Finish(bool complete) => _done.TrySetResult(complete);

        protected override void Dispose(bool disposing)
        {
            _content.TrySetCanceled();
            _done.TrySetResult(false);
            base.Dispose(disposing);
        }
    }
}
