using System.Text.Json.Nodes;

namespace Pulsar.Manifests.Tests;

/// <summary>Mod catalogs in manifests (Pulsar 1.0.9): what each server type takes from Modrinth, and where it goes.</summary>
public sealed class ModsTests
{
    private static readonly string[] s_modTypes = ["fabric", "forge", "neoforge"];

    [Fact]
    public void MinecraftTakesPluginsOnPaperAndPurpurAndModsOnTheModLoaders()
    {
        var manifest = TestManifests.Load("minecraft");

        Assert.Equal(["modrinth", "curseforge"], manifest.ModList.Select(m => m.Source));
        var modrinth = manifest.ModList[0];
        Assert.Equal(("modrinth", "version", "serverType"), (modrinth.Source, modrinth.VersionVar, modrinth.TypeVar));
        var paper = modrinth.Types["paper"];
        Assert.Equal(("plugin", "plugins"), (paper.ProjectType, paper.Folder));
        Assert.Equal(["paper", "spigot", "bukkit"], paper.Loaders);
        Assert.Equal(["fabric", "forge", "neoforge", "paper", "purpur"], modrinth.Types.Keys.Order(StringComparer.Ordinal));
        Assert.All(modrinth.Types.Where(t => t.Key is "fabric" or "forge" or "neoforge"), t => Assert.Equal(("mod", "mods"), (t.Value.ProjectType, t.Value.Folder)));
        Assert.DoesNotContain("vanilla", modrinth.Types.Keys); // Mojang's own server takes neither
        Assert.Equal(["mods", "plugins"], ModFiles.Folders(manifest).Order(StringComparer.Ordinal));
        Assert.True(ManifestFeatures.UsesMods(manifest));
    }

    [Theory]
    [InlineData("source", "thunderstore", "mods[0].source")]
    [InlineData("source", "curseforge", "mods")] // each catalog once
    [InlineData("typeVar", "difficulty", "mods[0].types.paper")]
    [InlineData("typeVar", "motd", "mods[0].typeVar")]
    [InlineData("versionVar", "nope", "mods[0].versionVar")]
    public void ACatalogNamesWhatItKnows(string property, string value, string path)
    {
        var errors = TestManifests.Errors(TestManifests.Change("minecraft", manifest => manifest["mods"]![0]![property] = value));

        Assert.Contains(errors, e => e.Path == path);
    }

    [Theory]
    [InlineData(".pulsar")]
    [InlineData("mods/sub")]
    [InlineData("../mods")]
    [InlineData("")]
    public void FilesGoIntoAFolderOfTheirOwn(string folder)
    {
        var errors = TestManifests.Errors(TestManifests.Change("minecraft", manifest => manifest["mods"]![0]!["types"]!["fabric"]!["folder"] = folder));

        Assert.Contains(errors, e => e.Path.StartsWith("mods[0].types.fabric.folder", StringComparison.Ordinal) || e.Path.StartsWith("mods[0]", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadersAndProjectTypesAreTheCatalogsWords()
    {
        var errors = TestManifests.Errors(TestManifests.Change("minecraft", manifest =>
        {
            manifest["mods"]![0]!["types"]!["fabric"]!["loaders"] = new JsonArray("Fabric Loader");
            manifest["mods"]![0]!["types"]!["forge"]!["projectType"] = "modpack";
        }));

        Assert.Contains(errors, e => e.Path == "mods[0].types.fabric.loaders");
        Assert.Contains(errors, e => e.Path == "mods[0].types.forge.projectType");
    }

    [Fact]
    public void MinecraftTakesModpacksSettingItsTypeVersionAndLoader()
    {
        var manifest = TestManifests.Load("minecraft");

        Assert.All(manifest.ModList, m => Assert.Equal("loaderVersion", m.Modpacks?.LoaderVar));
        Assert.True(ManifestFeatures.UsesModpacks(manifest));
        Assert.False(ManifestFeatures.UsesModpacks(TestManifests.Load("cs2")));
    }

    [Theory]
    [InlineData("nope")] // no such variable
    [InlineData("serverType")] // an enum, and the type's own
    [InlineData("version")] // the game version's
    public void AModpacksLoaderVariableIsAVersionOfItsOwn(string variable)
    {
        var errors = TestManifests.Errors(TestManifests.Change("minecraft", manifest => manifest["mods"]![0]!["modpacks"]!["loaderVar"] = variable));

        Assert.Contains(errors, e => e.Path == "mods[0].modpacks.loaderVar" || e.Path.StartsWith("variables", StringComparison.Ordinal));
    }

    [Fact]
    public void ModpacksAreMadeOfMods()
    {
        var errors = TestManifests.Errors(TestManifests.Change("minecraft", manifest =>
        {
            foreach (var type in s_modTypes)
            {
                manifest["mods"]![0]!["types"]!.AsObject().Remove(type);
            }
        }));

        Assert.Contains(errors, e => e.Path == "mods[0].modpacks");
    }

    [Theory]
    [InlineData("mods/lithium-fabric-0.18.1+mc1.21.8.jar", true)]
    [InlineData("plugins/LuckPerms-Bukkit-5.5.17.jar", true)]
    [InlineData("mods/[1.21] Some Mod (fabric).jar", true)]
    [InlineData("mods/lithium.zip", false)]
    [InlineData("mods/sub/lithium.jar", false)]
    [InlineData("config/lithium.jar", false)]
    [InlineData("mods/../server.jar", false)]
    [InlineData("mods/.hidden.jar", false)]
    [InlineData("mods/con.jar", false)]
    [InlineData("mods/a;b.jar", false)]
    [InlineData("mods/.jar", false)]
    public void AModFileIsAJarRightInOneOfTheFolders(string path, bool valid)
    {
        Assert.Equal(valid, ModFiles.IsPath(path, new HashSet<string>(StringComparer.Ordinal) { "mods", "plugins" }));
    }
}
