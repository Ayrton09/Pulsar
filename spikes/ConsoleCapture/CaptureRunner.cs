namespace ConsoleCapture;

/// <summary>Starts a capture session, plays the scheduled input actions and returns the summary.</summary>
internal static class CaptureRunner
{
    public static async Task<CaptureSummary> RunAsync(CaptureOptions options, Recorder recorder)
    {
        recorder.Info($"mode={options.Mode} exe={options.Executable} args=[{string.Join(", ", options.Arguments)}] " +
            $"os={Environment.OSVersion.VersionString} user={Environment.UserName} asOtherUser={options.UserToken != 0} desktop={options.Desktop ?? "(default)"}");
        recorder.Info($"commandLine={CommandLine.Build(options.Executable, options.Arguments)}");

        using var session = CaptureSession.Start(options, recorder);
        recorder.Info($"pid={session.ProcessId} inJob={session.IsInJob}");

        var exitTask = session.WaitForExitAsync();
        foreach (var action in options.Actions.OrderBy(a => a.At))
        {
            var delay = action.At - recorder.Elapsed;
            if (delay > TimeSpan.Zero && await Task.WhenAny(exitTask, Task.Delay(delay)) == exitTask)
            {
                break;
            }

            if (action.Text is { } text)
            {
                recorder.Input(text);
                session.WriteInput(text + options.InputNewline);
            }
            else
            {
                recorder.Event("ctrlc", options.Mode == CaptureMode.Pty ? "writing 0x03 to the pseudo console" : "not supported in pipes mode");
                if (options.Mode == CaptureMode.Pty)
                {
                    session.WriteInput("\u0003");
                }
            }
        }

        var remaining = TimeSpan.FromSeconds(options.DurationSeconds) - recorder.Elapsed;
        if (remaining > TimeSpan.Zero)
        {
            await Task.WhenAny(exitTask, Task.Delay(remaining));
        }

        var summary = await session.FinishAsync(exitTask);
        recorder.Summary(summary);
        return summary;
    }
}
