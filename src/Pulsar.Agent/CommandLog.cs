using System.Globalization;
using System.Text;

namespace Pulsar.Agent;

/// <summary>
/// <c>--log-file &lt;path&gt;</c>: what a command prints also goes to a file, for runs without a console (the agent MSI
/// runs <c>enroll</c> as a custom action). Appended to, with the time each run starts.
/// </summary>
internal sealed class CommandLog : IDisposable
{
    private readonly TextWriter _originalOut = Console.Out;
    private readonly TextWriter _originalError = Console.Error;
    private readonly StreamWriter _file;

    private CommandLog(string path, string command)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _file = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        _file.WriteLine(string.Create(CultureInfo.InvariantCulture, $"--- {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} PulsarAgent {command}"));
        Console.SetOut(new Tee(_originalOut, _file));
        Console.SetError(new Tee(_originalError, _file));
    }

    /// <summary>Null when no log file was asked for.</summary>
    public static CommandLog? Open(string? path, string command) => path is { Length: > 0 } ? new CommandLog(path, command) : null;

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
