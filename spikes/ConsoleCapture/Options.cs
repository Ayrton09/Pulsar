using System.Globalization;
using System.Text;

namespace ConsoleCapture;

internal enum CaptureMode { Pty, Pipes }

internal sealed record ScheduledAction(TimeSpan At, string? Text, bool Snapshot = false);

internal sealed record CaptureOptions(
    CaptureMode Mode,
    string Executable,
    string? WorkingDirectory,
    string? OutputPath,
    int DurationSeconds,
    IReadOnlyList<ScheduledAction> Actions,
    IReadOnlyList<string> Arguments,
    Encoding PipeEncoding,
    bool UseStdHandlesInPty,
    string InputNewline)
{
    /// <summary>Primary token to launch with (CreateProcessAsUser); 0 = CreateProcess as the caller.</summary>
    public nint UserToken { get; init; }

    /// <summary>Environment block from CreateEnvironmentBlock; 0 = inherit the caller's environment.</summary>
    public nint EnvironmentBlock { get; init; }

    /// <summary>"winsta\desktop" for STARTUPINFO.lpDesktop; null = default.</summary>
    public string? Desktop { get; init; }

    /// <summary>Clear this process's standard handles before launching, like a Windows service (which has none).</summary>
    public bool DetachStdHandles { get; init; }

    public static CaptureOptions Parse(string[] args)
    {
        CaptureMode? mode = null;
        string? exe = null, cwd = null, output = null;
        var duration = 30;
        var actions = new List<ScheduledAction>();
        var arguments = new List<string>();
        Encoding encoding = Encoding.UTF8;
        var useStdHandles = false;
        var newline = "\r\n";
        var detach = false;

        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value for {args[i]}");

            switch (args[i])
            {
                case "--mode": mode = Enum.Parse<CaptureMode>(Next(), ignoreCase: true); break;
                case "--exe": exe = Path.GetFullPath(Next()); break;
                case "--cwd": cwd = Path.GetFullPath(Next()); break;
                case "--out": output = Path.GetFullPath(Next()); break;
                case "--duration": duration = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--newline": newline = Next() == "lf" ? "\n" : "\r\n"; break;
                case "--usestdhandles": useStdHandles = true; break;
                case "--detach-std": detach = true; break;
                case "--encoding":
                    var name = Next();
                    encoding = name == "oem"
                        ? Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage)
                        : Encoding.GetEncoding(name);
                    break;
                case "--send":
                    var value = Next();
                    var colon = value.IndexOf(':', StringComparison.Ordinal);
                    if (colon <= 0) { throw new ArgumentException("--send expects <seconds>:<text>"); }
                    actions.Add(new ScheduledAction(
                        TimeSpan.FromSeconds(double.Parse(value[..colon], CultureInfo.InvariantCulture)), value[(colon + 1)..]));
                    break;
                case "--snapshot":
                    actions.Add(new ScheduledAction(TimeSpan.FromSeconds(double.Parse(Next(), CultureInfo.InvariantCulture)), null, Snapshot: true));
                    break;
                case "--ctrlc":
                    actions.Add(new ScheduledAction(TimeSpan.FromSeconds(double.Parse(Next(), CultureInfo.InvariantCulture)), null));
                    break;
                case "--":
                    arguments.AddRange(args[(i + 1)..]);
                    i = args.Length;
                    break;
                default: throw new ArgumentException($"Unknown option {args[i]}");
            }
        }

        if (mode is null || exe is null)
        {
            throw new ArgumentException("Usage: ConsoleCapture --mode pty|pipes --exe <path> [options] [-- args]");
        }

        return new CaptureOptions(mode.Value, exe, cwd, output, duration, actions, arguments, encoding, useStdHandles, newline)
        {
            DetachStdHandles = detach,
        };
    }
}

internal static class CommandLine
{
    public static string Build(string executable, IEnumerable<string> arguments) =>
        string.Join(' ', new[] { Quote(executable) }.Concat(arguments.Select(Quote)));

    // Quoting compatible with CommandLineToArgvW / the MSVCRT parser.
    public static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            return argument;
        }

        var builder = new StringBuilder("\"");
        for (var i = 0; i < argument.Length; i++)
        {
            var backslashes = 0;
            while (i < argument.Length && argument[i] == '\\')
            {
                backslashes++;
                i++;
            }

            if (i == argument.Length)
            {
                builder.Append('\\', backslashes * 2);
                break;
            }

            if (argument[i] == '"')
            {
                builder.Append('\\', (backslashes * 2) + 1).Append('"');
            }
            else
            {
                builder.Append('\\', backslashes).Append(argument[i]);
            }
        }

        return builder.Append('"').ToString();
    }
}
