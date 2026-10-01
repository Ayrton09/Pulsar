// Spike 1 — console capture with ConPTY vs redirected pipes, inside a Job Object.
// Throwaway code: see spikes/README.md. Findings go to docs/spikes/.
//
// Usage:
//   ConsoleCapture --mode pty|pipes --exe <path> [--cwd <dir>] [--out <file.jsonl>] [--duration <sec>]
//                  [--send <sec>:<text>]... [--ctrlc <sec>] [--encoding utf-8|oem] [--usestdhandles]
//                  [--newline crlf|lf] [-- <arguments...>]
using System.Text;
using System.Text.Json;
using ConsoleCapture;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

CaptureOptions options;
try
{
    options = CaptureOptions.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

var outPath = options.OutputPath ?? Path.Combine(
    Environment.CurrentDirectory,
    $"capture-{Path.GetFileNameWithoutExtension(options.Executable)}-{options.Mode.ToString().ToLowerInvariant()}-{DateTime.UtcNow:yyyyMMddHHmmss}.jsonl");

using var recorder = new Recorder(outPath, options.Mode == CaptureMode.Pty ? Encoding.UTF8 : options.PipeEncoding);
var summary = await CaptureRunner.RunAsync(options, recorder);

Console.WriteLine();
Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"capture written to {outPath}");
return 0;
