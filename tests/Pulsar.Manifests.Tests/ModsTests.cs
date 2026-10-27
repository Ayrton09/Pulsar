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
        Assert.Equal(["mods", "plugins"], ModFiles.Folders(manifest).Keys.Order(StringComparer.Ordinal));
        Assert.All(ModFiles.Folders(manifest).Values, e => Assert.Equal([".jar"], e));
        Assert.True(ManifestFeatures.UsesMods(manifest));
    }

    [Theory]
    [InlineData("source", "nexus", "mods[0].source")]
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
    [InlineData("mods/.sub")]
    [InlineData("a/b/c/d")] // three names deep at most
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
        Assert.Equal(valid, ModFiles.IsPath(path, new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["mods"] = [".jar"],
            ["plugins"] = [".jar"],
        }));
    }

    [Fact]
    public void RustTakesUmodsPluginsIntoTheFolderOfTheAddonItHas()
    {
        var manifest = TestManifests.Load("rust");

        var umod = Assert.Single(manifest.ModList);
        Assert.Equal(("umod", "rust", true, true), (umod.Source, umod.Community, umod.AddonTypes, umod.WhileRunning));
        Assert.Null(umod.VersionVar);
        Assert.Null(umod.TypeVar);
        Assert.False(umod.Versioned);
        Assert.Equal(["carbon", "oxide"], umod.Types.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(("plugin", "oxide/plugins"), (umod.Types["oxide"].ProjectType, umod.Types["oxide"].Folder));
        Assert.Empty(umod.Types["oxide"].LoaderList);
        Assert.Equal([".cs"], ModFiles.Folders(manifest)["carbon/plugins"]);
        Assert.True(ManifestFeatures.UsesUmod(manifest));
        Assert.False(ManifestFeatures.UsesUmod(TestManifests.Load("minecraft")));
    }

    [Theory]
    [InlineData("versionVar", "\"maxPlayers\"", "mods[0].versionVar")] // uMod's plugins are not made per game version
    [InlineData("community", "null", "mods[0].community")]
    [InlineData("community", "\"Rust!\"", "mods[0].community")]
    [InlineData("typeVar", "\"maxPlayers\"", "mods[0].addonTypes")]
    [InlineData("addonTypes", "false", "mods[0].typeVar")]
    public void UmodsCatalogIsTypedByTheAddonAndNamesTheGame(string property, string json, string path)
    {
        var errors = TestManifests.Errors(TestManifests.Change("rust", manifest => manifest["mods"]![0]![property] = JsonNode.Parse(json)));

        Assert.Contains(errors, e => e.Path == path);
    }

    [Theory]
    [InlineData("folder", "\"oxide/../plugins\"")]
    [InlineData("folder", "\"oxide/.hidden\"")]
    [InlineData("folder", "\"a/b/c/d\"")]
    [InlineData("extensions", "[\"cs\"]")]
    [InlineData("extensions", "[]")]
    [InlineData("loaders", "[\"oxide\"]")] // uMod names no loaders
    public void AnAddonTypesFolderAndEndingsAreChecked(string property, string json)
    {
        var errors = TestManifests.Errors(TestManifests.Change("rust", manifest => manifest["mods"]![0]!["types"]!["oxide"]![property] = JsonNode.Parse(json)));

        Assert.Contains(errors, e => e.Path.StartsWith("mods[0].types.oxide.", StringComparison.Ordinal));
    }

    [Fact]
    public void ATypeOfACatalogTypedByAddonsMustBeOneOfTheGamesAddons()
    {
        var errors = TestManifests.Errors(TestManifests.Change("rust", manifest =>
            manifest["mods"]![0]!["types"]!["harmony"] = JsonNode.Parse("""{ "projectType": "plugin", "folder": "HarmonyMods", "extensions": [".dll"] }""")));

        Assert.Contains(errors, e => e.Path == "mods[0].types.harmony");
    }

    [Fact]
    public void AGamesCatalogsAreTypedAlike()
    {
        // The Mods tab shows one type for the server: Modrinth by the type variable and uMod by the addon cannot mix.
        var errors = TestManifests.Errors(TestManifests.Change("minecraft", manifest => manifest["mods"]!.AsArray().Add(JsonNode.Parse(
            """{ "source": "umod", "community": "rust", "addonTypes": true, "types": {} }"""))));

        Assert.Contains(errors, e => e.Path == "mods" && e.Message.Contains("share how", StringComparison.Ordinal));
    }

    [Fact]
    public void ValheimTakesThunderstoresServerPackagesOnTheBepInExItHasFromThunderstore()
    {
        var manifest = TestManifests.Load("valheim");

        var addon = Assert.Single(manifest.AddonList);
        var pack = Assert.IsType<ThunderstoreSource>(addon.Source);
        Assert.Equal(("bepinex", "denikson-BepInExPack_Valheim", "BepInExPack_Valheim", ""), (addon.Key, pack.Package, pack.Folder, addon.Target));
        var thunderstore = Assert.Single(manifest.ModList);
        Assert.Equal(("thunderstore", "valheim", "server-side", true, false), (thunderstore.Source, thunderstore.Community, thunderstore.Category,
            thunderstore.AddonTypes, thunderstore.WhileRunning));
        var bepinex = thunderstore.Types["bepinex"];
        Assert.Equal(("mod", "BepInEx", ModTargetDefinition.BepInExLayout), (bepinex.ProjectType, bepinex.Folder, bepinex.Layout));
        Assert.Empty(ModFiles.Folders(manifest)); // a package is unpacked, not a file written as it is
        Assert.Equal(["BepInEx"], ModFiles.PackageFolders(manifest));
        Assert.True(ManifestFeatures.UsesThunderstore(manifest));
        Assert.False(ManifestFeatures.UsesThunderstore(TestManifests.Load("rust")));
        Assert.Contains("BepInEx/config/**", manifest.Backup!.Include!);
    }

    [Theory]
    [InlineData("addonTypes", "false", "mods[0].addonTypes")] // the packages run on the BepInEx addon
    [InlineData("category", "\"Server Side\"", "mods[0].category")]
    [InlineData("community", "null", "mods[0].community")]
    [InlineData("whileRunning", "true", "mods[0].whileRunning")]
    public void ThunderstoresCatalogNamesItsCommunityAndRunsOnTheAddon(string property, string json, string path)
    {
        var errors = TestManifests.Errors(TestManifests.Change("valheim", manifest => manifest["mods"]![0]![property] = JsonNode.Parse(json)));

        Assert.Contains(errors, e => e.Path == path);
    }

    [Theory]
    [InlineData("layout", "null")]
    [InlineData("layout", "\"r2modman\"")]
    [InlineData("extensions", "[\".dll\"]")]
    [InlineData("loaders", "[\"bepinex\"]")]
    public void ThunderstoresPackagesGoIntoBepInExByItsLayout(string property, string json)
    {
        var errors = TestManifests.Errors(TestManifests.Change("valheim", manifest => manifest["mods"]![0]!["types"]!["bepinex"]![property] = JsonNode.Parse(json)));

        Assert.Contains(errors, e => e.Path.StartsWith("mods[0].types.bepinex.", StringComparison.Ordinal));
    }

    [Fact]
    public void OnlyThunderstoreHasALayoutOrACategory()
    {
        var errors = TestManifests.Errors(TestManifests.Change("rust", manifest =>
        {
            manifest["mods"]![0]!["category"] = "server-side";
            manifest["mods"]![0]!["types"]!["oxide"]!["layout"] = "bepinex";
        }));

        Assert.Contains(errors, e => e.Path == "mods[0].category");
        Assert.Contains(errors, e => e.Path == "mods[0].types.oxide.layout");
    }

    [Theory]
    [InlineData("package", "\"BepInExPack_Valheim\"")] // a namespace and a name
    [InlineData("package", "\"denikson-BepInEx/../x\"")]
    [InlineData("folder", "\"../BepInExPack_Valheim\"")]
    [InlineData("folder", "\"a/b\"")]
    public void AThunderstoreAddonIsAPackageAndOneFolderOfIt(string property, string json)
    {
        var errors = TestManifests.Errors(TestManifests.Change("valheim", manifest => manifest["addons"]![0]!["source"]![property] = JsonNode.Parse(json)));

        Assert.Contains(errors, e => e.Path.StartsWith("addons[0].source.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("BepInEx/plugins/ValheimModding-Jotunn", true)]
    [InlineData("BepInEx/plugins/Advize-PlantEverything", true)]
    [InlineData("BepInEx/plugins/Jotunn", false)] // a namespace and a name
    [InlineData("BepInEx/plugins/ValheimModding-Jotunn/x.dll", false)]
    [InlineData("BepInEx/patchers/ValheimModding-Jotunn", false)]
    [InlineData("Other/plugins/ValheimModding-Jotunn", false)]
    [InlineData("BepInEx/plugins/../plugins/A-B", false)]
    public void APackageGoesIntoItsOwnFolderOfPlugins(string path, bool valid)
    {
        Assert.Equal(valid, ModFiles.IsPackagePath(path, ModFiles.PackageFolders(TestManifests.Load("valheim"))));
    }

    [Fact]
    public void OnlyUmodsPluginsChangeWhileTheGameRuns()
    {
        var errors = TestManifests.Errors(TestManifests.Change("minecraft", manifest => manifest["mods"]![0]!["whileRunning"] = true));

        Assert.Contains(errors, e => e.Path == "mods[0].whileRunning");
    }

    [Theory]
    [InlineData("oxide/plugins/Kits.cs", true)]
    [InlineData("carbon/plugins/Kits.cs", true)]
    [InlineData("oxide/plugins/Kits.jar", false)]
    [InlineData("oxide/plugins/sub/Kits.cs", false)]
    [InlineData("oxide/Kits.cs", false)]
    [InlineData("oxide/plugins/.cs", false)]
    [InlineData("oxide/plugins/../Kits.cs", false)]
    public void APluginIsACsFileRightInItsAddonsFolder(string path, bool valid)
    {
        Assert.Equal(valid, ModFiles.IsPath(path, ModFiles.Folders(TestManifests.Load("rust"))));
    }

    [Fact]
    public void OnlyFilesOfACatalogTheGameLoadsLiveChangeWhileItRuns()
    {
        var rust = TestManifests.Load("rust");

        Assert.True(ModFiles.WhileRunning(rust, ["oxide/plugins/Kits.cs", "carbon/plugins/Gather.cs"]));
        Assert.False(ModFiles.WhileRunning(rust, ["oxide/plugins/Kits.cs", "oxide/Kits.cs"]));
        Assert.False(ModFiles.WhileRunning(TestManifests.Load("minecraft"), ["mods/lithium.jar"]));
    }
}
