using System.Text;
using Pulsar.Agent.Core.Servers;
using Pulsar.Contracts;
using Pulsar.Manifests;

namespace Pulsar.Agent.Core.Tests;

/// <summary>Which Steam branch each SteamCMD step installs (Pulsar 1.0.14): the server's own, for the game's app only.</summary>
public sealed class SteamBranchRuleTests
{
    private const string Manifest = """
        {
          "schemaVersion": 1, "key": "pulsar-test", "version": "1.0.0", "name": "Pulsar test server", "platforms": ["windows-x64"],
          "install": { "steps": [ { "type": "steamcmd", "appId": 896660 }, { "type": "steamcmd", "appId": 1007, "beta": "tools" } ] },
          "update": { "steps": [ { "type": "steamcmd", "appId": 896660, "beta": "public-test" }, { "type": "steamcmd", "appId": 1007, "beta": "tools" } ] },
          "runtime": { "windows-x64": { "executable": "bin/game.exe", "console": { "mode": "pty" } } },
          "readiness": { "type": "logPattern", "pattern": "^ready", "timeoutSeconds": 60 },
          "stop": { "steps": [ { "type": "consoleCommand", "command": "quit" } ], "timeoutSeconds": 15 }
        }
        """;

    [Fact]
    public void TheServersBranchReplacesTheTemplatesForTheGamesAppAlone()
    {
        var manifest = ManifestParser.Parse(Encoding.UTF8.GetBytes(Manifest)).Manifest!;
        var update = manifest.Update!.Steps.OfType<SteamCmdStep>().ToList();
        var spec = new ServerRuntimeSpec("abcd2345", Manifest, new Dictionary<string, string?>(), [], AutoStart: false, RestartOnCrash: false);

        // Without one, each step installs its template's branch, as before.
        Assert.Equal(["public-test", "tools"], update.Select(s => ServerHost.BranchOf(spec, manifest, s)));
        // With one, the game's app (the install's first SteamCMD step) follows it; another app keeps its own. Going back is
        // "public", which SteamCMD needs to hear to leave a branch it installed.
        Assert.Equal(["previous", "tools"], update.Select(s => ServerHost.BranchOf(spec with { SteamBranch = "previous" }, manifest, s)));
        Assert.Equal(["public", "tools"], update.Select(s => ServerHost.BranchOf(spec with { SteamBranch = "public" }, manifest, s)));
    }
}
