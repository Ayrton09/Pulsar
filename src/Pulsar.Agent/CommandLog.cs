using System.Globalization;
using System.Text;

namespace Pulsar.Agent;

/// <summary>
/// <c>--log-file &lt;path&gt;</c>: what a command prints also goes to a file, for runs without a console (the agent MSI
/// runs <c>enroll</c> and <c>unenroll</c> as custom actions). Appended to, with the time each run starts.
/// </summary>
internal sealed class CommandLog : IDisposable
{
    private readonly TextWriter _originalOut = Console.Out;
    private readonly TextWriter _originalError = Console.Error;
    private readonly StreamWriter _file;

    private CommandLog(StreamWriter file, string command)
    {
        _file = file;
        _file.WriteLine(string.Create(CultureInfo.InvariantCulture, $"--- {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} PulsarAgent {command}"));
        Console.SetOut(new Tee(_originalOut, _file));
        Console.SetError(new Tee(_originalError, _file));
    }

    /// <summary>
    /// Null when no log file was asked for, or when it cannot be opened: the command still runs (an enrollment must not
    /// fail for its log), and says so on its console.
    /// </summary>
    public static CommandLog? Open(string? path, string command)
    {
        if (path is not { Length: > 0 })
        {
            return null;
        }

        StreamWriter file;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            file = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"Warning: cannot write the log file {path} ({ex.Message}); going on without it.");
            return null;
        }

        try
        {
            return new CommandLog(file, command);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalError);
        _file.Dispose();
    }

    private sealed class Tee(TextWriter console, TextWriter file) : TextWriter
    {
        public override Encoding Encoding => console.Encoding;

        public override void Write(char value)
        {
            console.Write(value);
            file.Write(value);
        }

        public override void Write(string? value)
        {
            console.Write(value);
            file.Write(value);
        }

        public override void WriteLine(string? value)
        {
            console.WriteLine(value);
            file.WriteLine(value);
        }

        public override Task WriteAsync(string? value)
        {
            Write(value);
            return Task.CompletedTask;
        }

        public override Task WriteLineAsync(string? value)
        {
            WriteLine(value);
            return Task.CompletedTask;
        }

        public override void Flush()
        {
            console.Flush();
            file.Flush();
        }
    }
}
