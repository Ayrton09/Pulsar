using Pulsar.Agent.Core.Servers;
using Pulsar.Agent.Windows.Servers;
using Pulsar.Manifests;

namespace Pulsar.Agent.Windows.Tests;

/// <summary>
/// The bundled addons from GitHub itself (about 100 MB): each release found, its SHA-256 checked, its zip unpacked where
/// the game expects it through the agent's own file access, and removed again. Run with PULSAR_NETWORK_TESTS=1.
/// </summary>
public sealed class RealAddonTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pulsar-real-addons-").FullName;

    public void Dispose() => WindowsServerProvisioner.DeleteTree(_root);

    [NetworkFact]
    public async Task MetamodAndCounterStrikeSharpGoIntoCs2()
    {
        var addons = Addons("cs2");
        var gameInfo = Path.Combine(_root, "game", "csgo", "gameinfo.gi");
        Directory.CreateDirectory(Path.GetDirectoryName(gameInfo)!);
        // The lines around the anchor as CS2 ships them.
        File.WriteAllText(gameInfo, "\t\tSearchPaths\r\n\t\t{\r\n\t\t\tGame_LowViolence\tcsgo_lv // Perfect World content override\r\n\t\t\tGame\tcsgo\r\n");

        var metamod = await InstallAsync(addons["metamod"]);
        Assert.Contains(metamod.Added, p => p.StartsWith("game/csgo/addons/metamod/", StringComparison.Ordinal));
        Assert.Contains("game/csgo/addons/metamod.vdf", metamod.Added);
        File.WriteAllText(gameInfo, LineEditor.Apply(File.ReadAllText(gameInfo), addons["metamod"].LineEditList[0]));
        Assert.Contains("\t\t\tGame\tcsgo/addons/metamod\r\n\t\t\tGame\tcsgo\r\n", File.ReadAllText(gameInfo), StringComparison.Ordinal);

        var sharp = await InstallAsync(addons["counterstrikesharp"]);
        Assert.Contains(sharp.Added, p => p.StartsWith("game/csgo/addons/counterstrikesharp/", StringComparison.Ordinal));
        Assert.Empty(sharp.Replaced);

        Installer().Remove(_root, addons["counterstrikesharp"]);
        Installer().Remove(_root, addons["metamod"]);
        Assert.False(Directory.Exists(Path.Combine(_root, "game", "csgo", "addons")));
        Assert.DoesNotContain("metamod", File.ReadAllText(gameInfo), StringComparison.Ordinal);
    }

    [NetworkFact]
    public async Task OxideAndCarbonGoIntoRust()
    {
        var addons = Addons("rust");
        var managed = Directory.CreateDirectory(Path.Combine(_root, "RustDedicated_Data", "Managed")).FullName;
        File.WriteAllText(Path.Combine(managed, "Assembly-CSharp.dll"), "the game's");

        var oxide = await InstallAsync(addons["oxide"]);
        Assert.Contains(oxide.Added, p => p.StartsWith("RustDedicated_Data/Managed/", StringComparison.Ordinal));
        Assert.Contains("RustDedicated_Data/Managed/Assembly-CSharp.dll", oxide.Replaced); // Oxide patches the game's own
        Installer().Remove(_root, addons["oxide"]);
        Assert.True(File.Exists(Path.Combine(managed, "Assembly-CSharp.dll")));

        var carbon = await InstallAsync(addons["carbon"]);
        Assert.Equal("production_build", carbon.Release);
        Assert.Contains(carbon.Added, p => p.StartsWith("carbon/", StringComparison.Ordinal));
        Installer().Remove(_root, addons["carbon"]);
        Assert.False(Directory.Exists(Path.Combine(_root, "carbon")));
    }

    private async Task<AddonRecord> InstallAsync(AddonDefinition addon)
    {
        var started = DateTimeOffset.UtcNow;
        var record = await Installer().InstallAsync(_root, addon, force: false, (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);
        output.WriteLine($"{addon.Key}: {record.Release} {record.Asset} sha256 {record.Sha256} in {(DateTimeOffset.UtcNow - started).TotalSeconds:0.0} s; " +
            $"{record.Added.Count} added, {record.Replaced.Count} replaced; top level: " +
            string.Join(", ", record.Added.Select(p => string.Join('/', p.Split('/').Take(addon.Target.Length == 0 ? 1 : 4))).Distinct()));
        Assert.NotEmpty(record.Added);
        Assert.Equal(64, record.Sha256.Length);
        return record;
    }

    private static AddonInstaller Installer() => new(new WindowsServerFiles(), new InstallOptions().HttpHandler);

    private static Dictionary<string, AddonDefinition> Addons(string game)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Pulsar.slnx")))
        {
            directory = directory.Parent;
        }

        var parsed = ManifestParser.Parse(File.ReadAllBytes(Path.Combine(directory!.FullName, "games", game, "manifest.json")));
        return parsed.Manifest!.AddonList.ToDictionary(a => a.Key);
    }
}
