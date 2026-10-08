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
    [InlineData("say \"hi\"")]
    [InlineData("C:\\path")]
    public void UnrealValuesCannotEscapeTheirQuotes(string value)
    {
        Assert.NotNull(ConfigFileWriter.ValueError(ConfigFileFormat.UnrealIni, value));
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
    public void AMissingXmlFileStartsAsItsRootAndABrokenOneIsNotOverwritten()
    {
        Assert.Equal("<?xml version=\"1.0\"?>\n<ServerSettings>\n\t<property name=\"ServerPort\" value=\"26900\"/>\n</ServerSettings>\n",
            ConfigFileWriter.Apply(Xml(("ServerPort", "26900")), null));
        Assert.Throws<LaunchPlanException>(() => ConfigFileWriter.Apply(Xml(("ServerPort", "26900")), "<ServerSettings>\n"));
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
