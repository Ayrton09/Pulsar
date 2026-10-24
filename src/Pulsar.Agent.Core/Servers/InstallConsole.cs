using System.Globalization;
using Pulsar.Agent.Core.Steam;

namespace Pulsar.Agent.Core.Servers;

/// <summary>
/// The progress of what the agent downloads itself (Minecraft, addons), also noted in the server's console, as SteamCMD's
/// own lines are: every 10 percent of a download, so the console shows it moving without a line per read, and when it
/// is checked and its files are installed.
/// </summary>
public sealed class InstallConsole(ServerConsole console, TimeProvider clock, Func<string, double?, Task> progress)
{
    private string? _stage;
    private int _tenths = -1;

    public Task ReportAsync(string stage, double? percent)
    {
        if (stage != _stage)
        {
            // Installing after a download: its files are written (a step that only writes a file says so itself).
            if (stage == InstallStages.Verifying)
            {
                console.Note("Checking the download", clock.GetUtcNow());
            }
            else if (stage == InstallStages.Installing && _stage is InstallStages.Downloading or InstallStages.Verifying)
            {
                console.Note("Installing the files", clock.GetUtcNow());
            }

            _stage = stage;
            _tenths = -1;
        }

        if (stage == InstallStages.Downloading && percent is { } done && (int)(done / 10) > _tenths)
        {
            _tenths = (int)(done / 10);
            console.Note(string.Create(CultureInfo.InvariantCulture, $"Downloading: {done:0} %"), clock.GetUtcNow());
        }

        return progress(stage, percent);
    }
}
