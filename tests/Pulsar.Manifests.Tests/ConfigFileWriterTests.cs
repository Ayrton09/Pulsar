using System.Text.Json.Nodes;
namespace Pulsar.Manifests.Tests;

public sealed class ConfigFileWriterTests
{
    [Fact]
    public void PatchChangesOnlyTheDeclaredKeys()
    {
        const string existing = """
            // my server
            hostname "Old name"
            sv_cheats 0
            HOSTNAME "duplicate"
            mp_timelimit 30 // minutes
            "sv_password" "old"

            """;
        var file = Cfg(ConfigFileMode.Patch, ("hostname", "New name"), ("sv_password", ""), ("sv_region", "3"));

        var text = ConfigFileWriter.Apply(file, existing.Replace("\r\n", "\n", StringComparison.Ordinal));

        Assert.Equal("""
            // my server
            hostname "New name"
            sv_cheats 0
            mp_timelimit 30 // minutes
            sv_region "3"

            """.Replace("\r\n", "\n", StringComparison.Ordinal), text);
    }

    [Fact]
    public void PatchKeepsTheFilesLineEndingsAndCreatesMissingFiles()
    {
        var file = Cfg(ConfigFileMode.Patch, ("hostname", "x"));

        Assert.Equal("a 1\r\nhostname \"x\"\r\n", ConfigFileWriter.Apply(file, "a 1\r\n"));
        Assert.Equal("hostname \"x\"\n", ConfigFileWriter.Apply(file, null));
    }

    [Fact]
    public void ManagedFilesAreWrittenWhole()
    {
        var file = Cfg(ConfigFileMode.Managed, ("hostname", "x"), ("sv_password", ""));

        var text = ConfigFileWriter.Apply(file, "hostname \"y\"\nsv_cheats 1\n");

        Assert.Equal("// Written by Pulsar before every start: changes made here are lost.\nhostname \"x\"\n", text);
    }

    [Fact]
    public void PropertiesEscapeBackslashesAndKeepComments()
    {
        var file = new RenderedConfigFile("server.properties", ConfigFileFormat.Properties, ConfigFileMode.Patch,
            [new ConfigEntry("motd", @"C:\ok", Secret: false), new ConfigEntry("server-port", "25565", Secret: false)]);

        var text = ConfigFileWriter.Apply(file, "#Minecraft server properties\nmotd=A Minecraft Server\nserver-port : 1\npvp=true\n");

        Assert.Equal("#Minecraft server properties\nmotd=C:\\\\ok\nserver-port=25565\npvp=true\n", text);
    }

    [Theory]
    [InlineData(ConfigFileFormat.SourceCfg, "a\"b")]
    [InlineData(ConfigFileFormat.SourceCfg, "a;b")]
    [InlineData(ConfigFileFormat.SourceCfg, "a\nb")]
    [InlineData(ConfigFileFormat.Properties, "a\nb")]
    [InlineData(ConfigFileFormat.Properties, "a\rb")]
    [InlineData(ConfigFileFormat.Properties, "a\u0000b")]
    public void ValuesThatBreakOutAreRefused(ConfigFileFormat format, string value)
    {
        var file = new RenderedConfigFile("x", format, ConfigFileMode.Patch, [new ConfigEntry("key", value, Secret: false)]);

        Assert.NotNull(ConfigFileWriter.ValueError(format, value));
        Assert.Throws<LaunchPlanException>(() => ConfigFileWriter.Apply(file, null));
    }

    [Theory]
    [InlineData("hostname \"x\"", "hostname")]
    [InlineData("  \"hostname\" \"x\"", "hostname")]
    [InlineData("exec banned.cfg;sv_cheats 0", "exec")]
    [InlineData("// hostname \"x\"", null)]
    [InlineData("   ", null)]
    public void FindsTheKeyOfASourceLine(string line, string? key)
    {
        Assert.Equal(key, ConfigFileWriter.KeyOf(ConfigFileFormat.SourceCfg, line));
    }

    [Fact]
    public void AnUnrealStructKeepsTheMembersPulsarDoesNotDeclare()
    {
        // Palworld's PalWorldSettings.ini: every setting is a member of one OptionSettings line.
        const string existing = """
            [/Script/Engine.Something]
            OptionSettings=(Untouched=1)

            [/Script/Pal.PalGameWorldSettings]
            OptionSettings=(Difficulty=None,ServerName="Old, name",ExpRate=2.000000,BanListURL="https://x/banlist.txt",ServerPassword="old",ServerName="again")

            """;
        var file = Unreal(ConfigFileMode.Patch, "OptionSettings",
            ("ServerName", "Pulsar Palworld"), ("ServerPassword", ""), ("RCONEnabled", "True"), ("RCONPort", "8211"), ("AdminPassword", "123"));

        var text = ConfigFileWriter.Apply(file, existing.Replace("\r\n", "\n", StringComparison.Ordinal));

        Assert.Equal("""
            [/Script/Engine.Something]
            OptionSettings=(Untouched=1)

            [/Script/Pal.PalGameWorldSettings]
            OptionSettings=(Difficulty=None,ServerName="Pulsar Palworld",ExpRate=2.000000,BanListURL="https://x/banlist.txt",RCONEnabled=True,RCONPort=8211,AdminPassword=123)

            """.Replace("\r\n", "\n", StringComparison.Ordinal), text);
    }

    [Fact]
    public void AnUnrealSectionIsCreatedWhenMissing()
    {
        var file = Unreal(ConfigFileMode.Patch, "OptionSettings", ("ServerName", "x y"), ("PublicPort", "8211"));

        Assert.Equal("[/Script/Pal.PalGameWorldSettings]\nOptionSettings=(ServerName=\"x y\",PublicPort=8211)\n", ConfigFileWriter.Apply(file, null));
        Assert.Equal("[Other]\r\nA=1\r\n\r\n[/Script/Pal.PalGameWorldSettings]\r\nOptionSettings=(ServerName=\"x y\",PublicPort=8211)\r\n",
            ConfigFileWriter.Apply(file, "[Other]\r\nA=1\r\n"));
        Assert.Equal("; Written by Pulsar before every start: changes made here are lost.\n[/Script/Pal.PalGameWorldSettings]\nOptionSettings=(ServerName=\"x y\",PublicPort=8211)\n",
            ConfigFileWriter.Apply(Unreal(ConfigFileMode.Managed, "OptionSettings", ("ServerName", "x y"), ("PublicPort", "8211")), "[Other]\nA=1\n"));
    }

    [Fact]
    public void UnrealKeysCanBePlainLinesOfASection()
    {
        // ARK's GameUserSettings.ini style.
        var file = Unreal(ConfigFileMode.Patch, structName: null, ("ServerPassword", "abc"), ("MaxPlayers", "20"));

        Assert.Equal("[/Script/Pal.PalGameWorldSettings]\nServerPassword=abc\nOther=1\nMaxPlayers=20\n[Next]\nServerPassword=keep\n",
            ConfigFileWriter.Apply(file, "[/Script/Pal.PalGameWorldSettings]\nserverpassword=old\nOther=1\nServerPassword=again\n[Next]\nServerPassword=keep\n"));
    }

    [Fact]
    public void ABrokenUnrealStructIsNotOverwritten()
    {
        var file = Unreal(ConfigFileMode.Patch, "OptionSettings", ("ServerName", "x"));

        var error = Assert.Throws<LaunchPlanException>(() => ConfigFileWriter.Apply(file, "[/Script/Pal.PalGameWorldSettings]\nOptionSettings=(ServerName=\"x\"\n"));
        Assert.Contains("OptionSettings", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("12\n")]
    [InlineData("True\n")]
    public void AnUnrealValueIsWrittenBareOnlyWhenAllOfItIsANumberOrABoolean(string value)
    {
        // $ would match before a final line feed and write the line break outside the quotes. Values with line breaks are
        // refused earlier (the next test), so the pattern is checked on its own.
        var literal = (System.Text.RegularExpressions.Regex)typeof(ConfigFileWriter)
            .GetMethod("UnrealLiteral", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, null)!;

        Assert.DoesNotMatch(literal, value);
        Assert.Matches(literal, value.TrimEnd('\n'));
        Assert.NotNull(ConfigFileWriter.ValueError(ConfigFileFormat.UnrealIni, value));
        Assert.Throws<LaunchPlanException>(() =>
            ConfigFileWriter.Apply(Unreal(ConfigFileMode.Patch, "OptionSettings", ("MaxPlayers", value)), "[/Script/Pal.PalGameWorldSettings]\n"));
    }

    [Theory]
    [InlineData("say \"hi\"")]
    [InlineData("C:\\path")]
    public void UnrealValuesCannotEscapeTheirQuotes(string value)
    {
        Assert.NotNull(ConfigFileWriter.ValueError(ConfigFileFormat.UnrealIni, value));
    }

    [Fact]
    public void ColonFilesChangeTheirKeysAndKeepTheRest()
    {
        // As SCP: Secret Laboratory's config_gameplay.txt has them, a line its comment broke included.
        const string existing = "#Server main settings\nserver_name: My Server Name\nmax_players: 20\n# The seconds that must pass\n"
            + "the player stops sprinting.\nspawn_protect_team: [1, 2]\nmap_seed: -1\n";
        var file = new RenderedConfigFile("config/config/config_gameplay.txt", ConfigFileFormat.Colon, ConfigFileMode.Patch,
            [new ConfigEntry("server_name", "Pulsar <color=red>red</color>", Secret: false), new ConfigEntry("max_players", "12", Secret: false),
                new ConfigEntry("friendly_fire", "true", Secret: false)]);

        Assert.Equal("#Server main settings\nserver_name: Pulsar <color=red>red</color>\nmax_players: 12\n# The seconds that must pass\n"
            + "the player stops sprinting.\nspawn_protect_team: [1, 2]\nmap_seed: -1\nfriendly_fire: true\n", ConfigFileWriter.Apply(file, existing));
        Assert.Null(ConfigFileWriter.KeyOf(ConfigFileFormat.Colon, "#server_name: commented"));
        Assert.Equal("auto_warhead_broadcast_message", ConfigFileWriter.KeyOf(ConfigFileFormat.Colon, "auto_warhead_broadcast_message: Alpha: now"));
    }

    [Theory]
    [InlineData("[1, 2]")] // a list
    [InlineData("{a: 1}")] // a map
    [InlineData("\"quoted\"")]
    [InlineData("'quoted'")]
    [InlineData("- item")]
    [InlineData("#comment")]
    [InlineData("text #comment")]
    [InlineData("key: value")]
    [InlineData(" padded")]
    [InlineData("padded ")]
    [InlineData("&anchor")]
    [InlineData("*alias")]
    [InlineData("|")]
    [InlineData("name\u2028enable_whitelist: false")] // a line separator, which YAML readers break lines at
    [InlineData("name\u2029x")]
    [InlineData("name\u0085x")]
    public void ColonValuesAreTakenAsTheyAreTyped(string value)
    {
        Assert.NotNull(ConfigFileWriter.ValueError(ConfigFileFormat.Colon, value));
        Assert.Null(ConfigFileWriter.ValueError(ConfigFileFormat.Colon, "-1"));
        Assert.Null(ConfigFileWriter.ValueError(ConfigFileFormat.Colon, "admin@example.com"));
    }

    [Fact]
    public void KeyValueFilesKeepValuesAsTheyAre()
    {
        const string existing = "# Mods to load\nMods=\nPublic=true\nWorkshopItems=1\nMap=Muldraugh, KY\n";
        var file = new RenderedConfigFile("zomboid/Server/pulsar.ini", ConfigFileFormat.KeyValue, ConfigFileMode.Patch,
            [new ConfigEntry("Mods", @"\Hydrocraft;\Brita", Secret: false), new ConfigEntry("Public", "false", Secret: false),
                new ConfigEntry("UPnP", "false", Secret: false)]);

        Assert.Equal("# Mods to load\nMods=\\Hydrocraft;\\Brita\nPublic=false\nWorkshopItems=1\nMap=Muldraugh, KY\nUPnP=false\n",
            ConfigFileWriter.Apply(file, existing));
        Assert.Null(ConfigFileWriter.KeyOf(ConfigFileFormat.KeyValue, "# Comment=1"));
        Assert.Equal("Time", ConfigFileWriter.KeyOf(ConfigFileFormat.KeyValue, "Time=12:30"));
    }

    [Fact]
    public void SpaceFilesChangeTheirCommandsAndKeepTheRest()
    {
        const string existing = "// Port 27015\r\nName Old name\r\nMap PEI\r\nWelcome Hi/255/0/0\r\nPvE\r\nMaxPlayers\t8\r\n";
        var file = new RenderedConfigFile("Servers/pulsar/Server/Commands.dat", ConfigFileFormat.Space, ConfigFileMode.Patch,
            [new ConfigEntry("Name", "My Unturned server", Secret: false), new ConfigEntry("MaxPlayers", "24", Secret: false),
                new ConfigEntry("PvE", "", Secret: false), new ConfigEntry("Port", "27020", Secret: false)]);

        // A commented-out command stays a comment; the one written goes at the end. Line ends stay.
        Assert.Equal("// Port 27015\r\nName My Unturned server\r\nMap PEI\r\nWelcome Hi/255/0/0\r\nMaxPlayers 24\r\nPort 27020\r\n",
            ConfigFileWriter.Apply(file, existing));
        Assert.Null(ConfigFileWriter.KeyOf(ConfigFileFormat.Space, "//Name x"));
        Assert.Equal("PvE", ConfigFileWriter.KeyOf(ConfigFileFormat.Space, "PvE"));
    }

    [Fact]
    public void XmlPropertiesChangeTheirValueAndKeepTheGamesComments()
    {
        const string existing = """
            <?xml version="1.0"?>
            <ServerSettings>
            	<!-- Server representation -->
            	<property name="ServerName"		value="My Game Host"/>		<!-- Whatever you want -->
            	<property name="ServerPort"		value="26900"/>
            	<!-- <property name="UserDataFolder"	value="absolute_path"/> -->
            	<!--
            	<property name="SaveGameFolder" value="old"/>
            	-->
            	<property name="ServerPassword"	value="secret"/>	<!-- remove me -->
            </ServerSettings>

            """;
        var file = Xml(("ServerName", "Pulsar & \"friends\""), ("ServerPort", "26905"), ("UserDataFolder", "C:/servers/abcd/data"),
            ("SaveGameFolder", "C:/servers/abcd/saves"), ("ServerPassword", ""));

        Assert.Equal("""
            <?xml version="1.0"?>
            <ServerSettings>
            	<!-- Server representation -->
            	<property name="ServerName" value="Pulsar &amp; &quot;friends&quot;"/>		<!-- Whatever you want -->
            	<property name="ServerPort" value="26905"/>
            	<!-- <property name="UserDataFolder"	value="absolute_path"/> -->
            	<!--
            	<property name="SaveGameFolder" value="old"/>
            	-->
            		<!-- remove me -->
            	<property name="UserDataFolder" value="C:/servers/abcd/data"/>
            	<property name="SaveGameFolder" value="C:/servers/abcd/saves"/>
            </ServerSettings>

            """.Replace("\r\n", "\n", StringComparison.Ordinal), ConfigFileWriter.Apply(file, existing.Replace("\r\n", "\n", StringComparison.Ordinal)));
    }

    [Fact]
    public void RemovingAnXmlPropertyKeepsTheCommentItsLineOpens()
    {
        // The comment goes on over the next lines: dropping its start would leave its end, and a broken file.
        const string existing = "<ServerSettings>\n\t<property name=\"ServerPassword\" value=\"secret\"/> <!-- the password,\n\tif any -->\n</ServerSettings>\n";

        Assert.Equal("<ServerSettings>\n\t <!-- the password,\n\tif any -->\n</ServerSettings>\n",
            ConfigFileWriter.Apply(Xml(("ServerPassword", "")), existing));
    }

    [Fact]
    public void EveryXmlPropertyOfALineIsSeen()
    {
        // Not only the first element of a line: the first one for each key gets the value, its repeats go.
        const string existing = "<ServerSettings>\n\t<property name=\"A\" value=\"1\"/> <property name=\"B\" value=\"2\"/> <property name=\"A\" value=\"3\"/>\n"
            + "\t<!-- <property name=\"B\" value=\"old\"/> --> <property name=\"C\" value=\"4\"/>\n</ServerSettings>\n";

        Assert.Equal("<ServerSettings>\n\t<property name=\"A\" value=\"9\"/> <property name=\"B\" value=\"8\"/> \n"
            + "\t<!-- <property name=\"B\" value=\"old\"/> --> \n</ServerSettings>\n",
            ConfigFileWriter.Apply(Xml(("B", "8"), ("A", "9"), ("C", "")), existing));
    }

    [Fact]
    public void AMissingXmlFileStartsAsItsRootAndABrokenOneIsNotOverwritten()
    {
        Assert.Equal("<?xml version=\"1.0\"?>\n<ServerSettings>\n\t<property name=\"ServerPort\" value=\"26900\"/>\n</ServerSettings>\n",
            ConfigFileWriter.Apply(Xml(("ServerPort", "26900")), null));
        Assert.Throws<LaunchPlanException>(() => ConfigFileWriter.Apply(Xml(("ServerPort", "26900")), "<ServerSettings>\n"));
    }

    private const string SpaceEngineersConfig = """
        <?xml version="1.0"?>
        <MyConfigDedicated xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
          <SessionSettings>
            <GameMode>Survival</GameMode>
            <MaxPlayers>4</MaxPlayers>
          </SessionSettings>
          <ServerName>Old name</ServerName>
          <Administrators />
        </MyConfigDedicated>
        """;

    [Fact]
    public void AnXmlFilesElementsGetTheirTextAndTheRestStays()
    {
        var text = ConfigFileWriter.Apply(Xml_(("/MyConfigDedicated/ServerName", "Fish & <Chips>"), ("/MyConfigDedicated/SessionSettings/MaxPlayers", "16"),
            ("/MyConfigDedicated/PremadeCheckpointPath", @"D:\servers\abcd1234\Content\CustomWorlds\Star System")), SpaceEngineersConfig);

        var document = System.Xml.Linq.XDocument.Parse(text);
        var root = document.Root!;
        Assert.Equal("Fish & <Chips>", root.Element("ServerName")!.Value);
        Assert.Contains("Fish &amp; &lt;Chips&gt;", text, StringComparison.Ordinal);
        Assert.Equal("16", root.Element("SessionSettings")!.Element("MaxPlayers")!.Value);
        Assert.Equal("Survival", root.Element("SessionSettings")!.Element("GameMode")!.Value); // the game's own
        Assert.Equal(@"D:\servers\abcd1234\Content\CustomWorlds\Star System", root.Element("PremadeCheckpointPath")!.Value); // added
        Assert.NotNull(root.Element("Administrators"));
        Assert.Equal("http://www.w3.org/2001/XMLSchema-instance", root.Attribute(System.Xml.Linq.XNamespace.Xmlns + "xsi")!.Value);
    }

    [Fact]
    public void AnXmlFileThatIsMissingStartsAsItsRootAndAnEmptyValueRemovesItsElement()
    {
        var created = ConfigFileWriter.Apply(Xml_(("/MyConfigDedicated/ServerName", "Pulsar")), null);
        Assert.Equal("Pulsar", System.Xml.Linq.XDocument.Parse(created).Root!.Element("ServerName")!.Value);

        var removed = ConfigFileWriter.Apply(Xml_(("/MyConfigDedicated/ServerName", "")), SpaceEngineersConfig);
        Assert.Null(System.Xml.Linq.XDocument.Parse(removed).Root!.Element("ServerName"));
    }

    [Theory]
    [InlineData("/MyConfigDedicated/SessionSettings", "8")] // holds elements of its own
    [InlineData("/OtherRoot/ServerName", "x")] // not this file's root
    public void AnXmlValueNeverReplacesElementsNorGoesUnderAnotherRoot(string key, string value) =>
        Assert.Throws<LaunchPlanException>(() => ConfigFileWriter.Apply(Xml_((key, value)), SpaceEngineersConfig));

    [Fact]
    public void AnXmlFileWithADtdIsRefused()
    {
        const string Entities = """<?xml version="1.0"?><!DOCTYPE r [<!ENTITY e SYSTEM "file:///C:/Windows/win.ini">]><MyConfigDedicated><ServerName>&e;</ServerName></MyConfigDedicated>""";

        var error = Assert.Throws<LaunchPlanException>(() => ConfigFileWriter.Apply(Xml_(("/MyConfigDedicated/ServerName", "x")), Entities));

        Assert.Contains("not valid XML", error.Message, StringComparison.Ordinal);
    }

    private static RenderedConfigFile Xml_(params (string Key, string Value)[] entries) =>
        new("instance/SpaceEngineers-Dedicated.cfg", ConfigFileFormat.Xml, ConfigFileMode.Patch, [.. entries.Select(e => new ConfigEntry(e.Key, e.Value, Secret: false))]);

    [Fact]
    public void ATextFileIsItsValue()
    {
        // Don't Starve Together's cluster_token.txt holds the token and nothing else.
        var token = new RenderedConfigFile("save/Cluster_1/cluster_token.txt", ConfigFileFormat.Text, ConfigFileMode.Patch,
            [new ConfigEntry(ConfigFileWriter.TextKey, "pds-g^KU_abc123^6^xyz=", Secret: true)]);

        Assert.Equal("pds-g^KU_abc123^6^xyz=", ConfigFileWriter.Apply(token, "an older token\n"));
        Assert.Equal("", ConfigFileWriter.Apply(token with { Entries = [new ConfigEntry(ConfigFileWriter.TextKey, "", Secret: true)] }, "an older token"));
        // A value kept while empty is not rendered at all: the file stays.
        Assert.Equal("an older token", ConfigFileWriter.Apply(token with { Entries = [] }, "an older token"));
        Assert.Equal("", ConfigFileWriter.Apply(token with { Entries = [] }, null));
    }

    [Fact]
    public void ATextFileHoldsOneValueAndTheValidatorSaysSo()
    {
        Assert.True(ManifestFeatures.UsesTextConfig(TestManifests.Load("dst")));
        Assert.False(ManifestFeatures.UsesTextConfig(TestManifests.Load("css")));

        var twoKeys = TestManifests.Errors(TestManifests.Change("dst", m => m["configFiles"]![4]!["set"]!["other"] = "x"));
        var otherKey = TestManifests.Errors(TestManifests.Change("dst", m => m["configFiles"]![4]!["set"] = JsonNode.Parse("""{"token": "{{var.clusterToken}}"}""")));

        Assert.Contains(twoKeys, e => e.Path == "configFiles[4].set");
        Assert.Contains(otherKey, e => e.Path == "configFiles[4].set");
    }

    [Fact]
    public void SectionsOfOneIniFilePatchedInTurnKeepEachOther()
    {
        // The agent writes a file's sections one after the other, each on what the one before wrote (Pulsar 1.0.12).
        var rcon = Unreal(ConfigFileMode.Patch, null, ("bEnabled", "True"), ("ListenPort", "27140")) with { Section = "Rcon" };
        var mode = Unreal(ConfigFileMode.Patch, null, ("bAllowFriendlyFire", "False")) with { Section = "/Script/Insurgency.INSGameMode" };
        const string Existing = "[/Script/Insurgency.INSGameMode]\nbAllowFriendlyFire=True\nbKillFeed=True\n";

        var text = ConfigFileWriter.Apply(mode, ConfigFileWriter.Apply(rcon, Existing));

        Assert.Equal("[/Script/Insurgency.INSGameMode]\nbAllowFriendlyFire=False\nbKillFeed=True\n\n[Rcon]\nbEnabled=True\nListenPort=27140\n", text);
    }

    private static RenderedConfigFile Xml(params (string Key, string Value)[] entries) =>
        new("pulsar-serverconfig.xml", ConfigFileFormat.XmlProperties, ConfigFileMode.Patch,
            [.. entries.Select(e => new ConfigEntry(e.Key, e.Value, Secret: false))])
        { Section = "ServerSettings" };

    private static RenderedConfigFile Unreal(ConfigFileMode mode, string? structName, params (string Key, string Value)[] entries) =>
        new("Pal/Saved/Config/WindowsServer/PalWorldSettings.ini", ConfigFileFormat.UnrealIni, mode,
            [.. entries.Select(e => new ConfigEntry(e.Key, e.Value, Secret: false))])
        { Section = "/Script/Pal.PalGameWorldSettings", Struct = structName };

    private static RenderedConfigFile Cfg(ConfigFileMode mode, params (string Key, string Value)[] entries) =>
        new("cstrike/cfg/server.cfg", ConfigFileFormat.SourceCfg, mode, [.. entries.Select(e => new ConfigEntry(e.Key, e.Value, Secret: false))]);
}
