using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Pulsar.Agent.Core.Servers;

public sealed class ConsoleLogOptions
{
    /// <summary>A file is rotated once it reaches this size.</summary>
    public long MaxFileBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>Rotated files kept next to the current one (<c>console.1.log</c> is the newest).</summary>
    public int KeepFiles { get; set; } = 4;

    /// <summary>Output is written in batches, at most this often (never one write per piece of output).</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// A server's console history on disk (docs/architecture/03): <c>console-logs\{shortId}\console.log</c> in the agent's
/// data directory, outside the server directory its account controls. One line per console line, prefixed with its
/// UTC time, escape sequences removed; rotated by size. Writing happens in the background: a slow disk drops output
/// here, never in the game.
/// </summary>
public sealed partial class ConsoleLog : IAsyncDisposable
{
    public const string FileName = "console.log";

    private const int QueueCapacity = 4096;

    private readonly string _directory;
    private readonly ConsoleLogOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly Channel<(string Text, DateTimeOffset At)> _queue = Channel.CreateBounded<(string, DateTimeOffset)>(
        new BoundedChannelOptions(QueueCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly ConsoleLines _lines = new();
    private readonly CancellationTokenSource _closing = new();
    private readonly Task _writer;
    private bool _failed;

    public ConsoleLog(string directory, ConsoleLogOptions options, TimeProvider clock, ILogger logger)
    {
        _directory = directory;
        _options = options;
        _clock = clock;
        _logger = logger;
        _writer = Task.Run(WriteAsync);
    }

    public string CurrentFile => Path.Combine(_directory, FileName);

    public void Write(string text, DateTimeOffset at) => _queue.Writer.TryWrite((text, at));

    /// <summary>Writes what is queued (and an unfinished last line) and closes the file.</summary>
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _closing.CancelAsync();
        await _writer;
        _closing.Dispose();
    }

    private async Task WriteAsync()
    {
        var batch = new StringBuilder();
        var reader = _queue.Reader;
        while (await reader.WaitToReadAsync())
        {
            while (reader.TryRead(out var item))
            {
                foreach (var line in _lines.Add(item.Text))
                {
                    AppendLine(batch, line, item.At);
                }
            }

            Flush(batch);
            try
            {
                await Task.Delay(_options.FlushInterval, _clock, _closing.Token);
            }
            catch (OperationCanceledException)
            {
                // Closing: write the rest right away.
            }
        }

        // The rest of an unfinished line.
        foreach (var line in _lines.Add("\n"))
        {
            if (line.Length > 0)
            {
                AppendLine(batch, line, _clock.GetUtcNow());
            }
        }

        Flush(batch);
    }

    private static void AppendLine(StringBuilder batch, string line, DateTimeOffset at) =>
        batch.Append(at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)).Append(' ').Append(line).Append('\n');

    private void Flush(StringBuilder batch)
    {
        if (batch.Length == 0)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_directory);
            var file = CurrentFile;
            if (File.Exists(file) && new FileInfo(file).Length >= _options.MaxFileBytes)
            {
                Rotate(file);
            }

            File.AppendAllText(file, batch.ToString(), new UTF8Encoding(false));
            _failed = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!_failed)
            {
                // Once per failure streak: the console keeps working without its history.
                LogWriteFailed(_directory, ex.Message);
                _failed = true;
            }
        }

        batch.Clear();
    }

    private void Rotate(string file)
    {
        File.Delete(Rotated(_options.KeepFiles));
        for (var i = _options.KeepFiles - 1; i >= 1; i--)
        {
            if (File.Exists(Rotated(i)))
            {
                File.Move(Rotated(i), Rotated(i + 1), overwrite: true);
            }
        }

        File.Move(file, Rotated(1), overwrite: true);
    }

    private string Rotated(int index) => Path.Combine(_directory, $"console.{index}.log");

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cannot write the console log in {Directory}: {Reason}")]
    private partial void LogWriteFailed(string directory, string reason);
}
