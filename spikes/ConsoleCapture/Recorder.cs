using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ConsoleCapture;

internal sealed record CaptureSummary(
    string Mode,
    double? FirstOutputMs,
    int Chunks,
    long Bytes,
    int EscapeBytes,
    uint? ExitCode,
    double? ExitMs,
    bool KilledByCapture,
    uint JobTotalProcesses,
    uint JobActiveProcessesAtEnd,
    double JobCpuSeconds,
    double? ClosePseudoConsoleMs);

/// <summary>Writes one JSON object per line and echoes a readable version to the console.</summary>
internal sealed class Recorder : IDisposable
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly StreamWriter _writer;
    private readonly Decoder _decoder;
    private readonly Lock _gate = new();
    private double? _firstOutputMs;
    private int _chunks;
    private long _bytes;
    private int _escapeBytes;

    public Recorder(string path, Encoding encoding)
    {
        _writer = new StreamWriter(path, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        _decoder = encoding.GetDecoder();
    }

    public TimeSpan Elapsed => _clock.Elapsed;

    public double? FirstOutputMs => _firstOutputMs;
    public int Chunks => _chunks;
    public long Bytes => _bytes;
    public int EscapeBytes => _escapeBytes;

    public void Output(string stream, ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            _firstOutputMs ??= _clock.Elapsed.TotalMilliseconds;
            _chunks++;
            _bytes += data.Length;
            foreach (var b in data)
            {
                if (b == 0x1B) { _escapeBytes++; }
            }

            var chars = new char[_decoder.GetCharCount(data, flush: false)];
            _decoder.GetChars(data, chars, flush: false);
            var text = new string(chars);

            Write(new { t = Ms(), kind = stream, len = data.Length, b64 = Convert.ToBase64String(data), text });
            Console.Write(Visible(text));
        }
    }

    public void Input(string text) => Log("in", text, $"\n>>> {text}\n");

    public void Event(string kind, string message) => Log(kind, message, $"\n[{kind}] {message}\n");

    public void Info(string message) => Log("info", message, $"[info] {message}\n");

    public void Summary(CaptureSummary summary)
    {
        lock (_gate)
        {
            Write(new { t = Ms(), kind = "summary", summary });
        }
    }

    public void Dispose() => _writer.Dispose();

    private void Log(string kind, string text, string echo)
    {
        lock (_gate)
        {
            Write(new { t = Ms(), kind, text });
            Console.Write(echo);
        }
    }

    private double Ms() => Math.Round(_clock.Elapsed.TotalMilliseconds, 1);

    private void Write(object record) => _writer.WriteLine(JsonSerializer.Serialize(record));

    // Makes escape sequences visible in the echo while keeping line breaks readable.
    private static string Visible(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            builder.Append(c switch
            {
                '\u001b' => "␛",
                '\r' => "␍",
                '\n' => "\n",
                '\t' => "\t",
                < ' ' => $"^{(char)(c + 64)}",
                _ => c.ToString(),
            });
        }

        return builder.ToString();
    }
}
