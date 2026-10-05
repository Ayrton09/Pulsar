using System.Text;
using System.Text.Json.Nodes;

namespace Pulsar.Manifests.Tests;

public sealed class ManifestTests
{
    public static TheoryData<string> BundledManifests() => [.. Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "games"), "manifest.json",
        SearchOption.AllDirectories).Select(p => Path.GetRelativePath(RepositoryRoot(), p))];

    [Theory]
    [MemberData(nameof(BundledManifests))]
    public void EveryManifestInTheRepositoryIsValid(string path)
    {
        var result = ManifestParser.Parse(File.ReadAllBytes(Path.Combine(RepositoryRoot(), path)));

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void TheTestManifestRendersWithItsDefaults()
    {
        var manifest = TestManifest();

        var plan = Render(manifest, new Dictionary<string, string?>());

        Assert.Equal("bin/Pulsar.TestGameServer.exe", plan.Executable);
        Assert.Equal("data", plan.WorkingDirectory);
        Assert.Equal(["--ready-after", "1000"], plan.Arguments);
        Assert.Equal("hello", plan.Environment["PULSAR_TEST_GREETING"]);
        Assert.Equal("abcd1234", plan.Environment["PULSAR_TEST_SERVER"]);
        Assert.Equal(ManifestConsoleMode.Pty, plan.Console.Mode);
        Assert.IsType<LogPatternReadiness>(plan.Readiness);
        Assert.IsType<ConsoleCommandStop>(Assert.Single(plan.Stop.Steps));
    }

    [Fact]
    public void ConditionalArgumentsFollowTheirVariable()
    {
        var plan = Render(TestManifest(), new Dictionary<string, string?> { ["ignoreQuit"] = "true", ["readyAfterMs"] = "0" });

        Assert.Equal(["--ready-after", "0", "--ignore-quit"], plan.Arguments);
    }

    [Fact]
    public void AConditionCanSayWhatGoesWhenItDoesNotHold()
    {
        var manifest = Parse(Manifest(m => m["runtime"]!["windows-x64"]!["arguments"]!.AsArray()
            .Add(new JsonObject { ["if"] = "var.ignoreQuit", ["then"] = new JsonArray("--on"), ["else"] = new JsonArray("--off") })));

        var on = Render(manifest, new Dictionary<string, string?> { ["ignoreQuit"] = "true", ["readyAfterMs"] = "0" });
        var off = Render(manifest, new Dictionary<string, string?> { ["ignoreQuit"] = "false", ["readyAfterMs"] = "0" });

        Assert.Equal("--on", on.Arguments[^1]);
        Assert.Equal("--off", off.Arguments[^1]);
        Assert.DoesNotContain("--on", off.Arguments);
    }

    [Theory]
    [InlineData("readyAfterMs", "-1")]
    [InlineData("readyAfterMs", "600001")]
    [InlineData("readyAfterMs", "ten")]
    [InlineData("ignoreQuit", "yes")]
    [InlineData("message", "semi;colon")]
    [InlineData("message", "line\nbreak")]
    [InlineData("unknown", "x")]
    public void ValuesThatBreakTheirDefinitionAreRefused(string key, string value)
    {
        Assert.Throws<LaunchPlanException>(() => Render(TestManifest(), new Dictionary<string, string?> { [key] = value }));
    }

    [Theory]
    [InlineData("de_dust2 +rcon_password x")] // stays one argument, and the pattern refuses it anyway
    [InlineData("+rcon_password")]
    [InlineData("-insecure")]
    [InlineData(" de_dust2")]
    public void AVariableCanNeverBecomeAFlagOrAConsoleCommand(string map)
    {
        // A pattern that (wrongly) allows anything: the renderer still refuses values that would read as flags.
        var manifest = Parse(Manifest(m =>
        {
            m["variables"]!.AsArray().Add(new JsonObject { ["key"] = "map", ["label"] = "Map", ["type"] = "string", ["pattern"] = "^.*$" });
            m["runtime"]!["windows-x64"]!["arguments"]!.AsArray().Add("+map");
            m["runtime"]!["windows-x64"]!["arguments"]!.AsArray().Add("{{var.map}}");
        }));

        if (map.Contains(' ', StringComparison.Ordinal) && !map.StartsWith(' '))
        {
            var plan = Render(manifest, new Dictionary<string, string?> { ["map"] = map });
            Assert.Equal(map, plan.Arguments[^1]);
            return;
        }

        Assert.Throws<LaunchPlanException>(() => Render(manifest, new Dictionary<string, string?> { ["map"] = map }));
    }

    [Fact]
    public void ExtraArgumentsNeedTheManifestsPermission()
    {
        Assert.Throws<LaunchPlanException>(() => Render(TestManifest(), new Dictionary<string, string?>(), ["-condebug"]));

        var allowing = Parse(Manifest(m => m["runtime"]!["windows-x64"]!["allowExtraArguments"] = true));
        var plan = Render(allowing, new Dictionary<string, string?>(), ["-condebug"]);
        Assert.Equal("-condebug", plan.Arguments[^1]);
    }

    // An extra argument comes after the manifest's own, so it would win: it may not set what the game's manifest sets
    // (ports, the RCON password Pulsar generates…), a sensitive setting (kept in clear in the server's settings) or,
    // on RCON consoles, anything about RCON; and Source command lines run ';'-separated commands.
    [Theory]
    [InlineData("+rcon_password")]
    [InlineData("+RCON_PASSWORD")]
    [InlineData("-port")]
    [InlineData("-IP")]
    [InlineData("+tv_port")]
    [InlineData("+map")]
    [InlineData("+sv_setsteamaccount")]
    [InlineData("+sv_password")]
    [InlineData("+sv_rcon_maxfailures")]
    [InlineData("-usercon")]
    [InlineData("+game_type 0;rcon_password x")]
    public void ExtraArgumentsCannotOverrideWhatTheGameOrPulsarSets(string extra)
    {
        var error = Assert.Throws<LaunchPlanException>(() => RenderCs2(["-tickrate", "128", extra, "value"]));
        Assert.Contains(extra.Split(' ', ';')[0], error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OtherExtraArgumentsFollowTheGamesOwn()
    {
        var plan = RenderCs2(["-tickrate", "128", "+exec", "practice.cfg", "+sv_cheats", "1"]);

        Assert.Equal(["-tickrate", "128", "+exec", "practice.cfg", "+sv_cheats", "1"], plan.Arguments.TakeLast(6));
    }

    [Theory]
    [InlineData("""{"surprise": true}""", "$")]
    [InlineData("""{"install": {"steps": [{"type": "run", "command": "cmd"}]}}""", "install")]
    [InlineData("""{"stop": {"steps": [{"type": "shell"}], "timeoutSeconds": 5}}""", "stop")]
    [InlineData("""{"runtime": {"windows-x64": {"executable": "a.exe", "arguments": [{"if": "var.x"}], "console": {"mode": "pty"}}}}""", "runtime")]
    public void UnknownPropertiesAndStepsAreErrors(string overlay, string area)
    {
        var json = Manifest(m =>
        {
            foreach (var (name, value) in JsonNode.Parse(overlay)!.AsObject())
            {
                m[name] = value?.DeepClone();
            }
        });

        var result = ManifestParser.Parse(Encoding.UTF8.GetBytes(json));

        Assert.False(result.IsValid);
        Assert.Null(result.Manifest);
        TestContext.Current.SendDiagnosticMessage($"{area}: {result.Errors[0]}");
    }

    [Theory]
    [InlineData("readiness")]
    [InlineData("stop")]
    [InlineData("runtime")]
    [InlineData("key")]
    public void RequiredSectionsCannotBeLeftOut(string section)
    {
        var result = ManifestParser.Parse(Encoding.UTF8.GetBytes(Manifest(m => m.Remove(section))));

        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData("../outside.exe")]
    [InlineData("bin/../../outside.exe")]
    [InlineData("C:/Windows/System32/cmd.exe")]
    [InlineData("/bin/game.exe")]
    [InlineData(@"bin\game.exe")]
    [InlineData("bin/game.exe:stream")]
    [InlineData("bin/CON.exe")]
    [InlineData("bin/nul")]
    [InlineData("bin./game.exe")]
    [InlineData("bin/start.bat")]
    [InlineData("bin/setup.ps1")]
    [InlineData("bin/game.exe ")]
    public void ExecutablesMustBeExeFilesInsideTheServerDirectory(string executable)
    {
        var errors = Errors(m => m["runtime"]!["windows-x64"]!["executable"] = executable);

        Assert.Contains(errors, e => e.Path == "runtime.windows-x64.executable");
    }

    [Theory]
    [InlineData("{{var.nope}}", "Unknown variable")]
    [InlineData("{{port.game}}", "Unknown port")]
    [InlineData("{{rcon.password}}", "needs an RCON console")]
    [InlineData("{{env.PATH}}", "Unknown placeholder")]
    [InlineData("{{var.readyAfterMs", "Malformed")]
    [InlineData("{{ var.readyAfterMs }}", "Malformed")]
    [InlineData("{{var.free}}", "needs a 'pattern'")]
    public void PlaceholdersAreAClosedSet(string argument, string message)
    {
        var errors = Errors(m =>
        {
            m["variables"]!.AsArray().Add(new JsonObject { ["key"] = "free", ["label"] = "Free", ["type"] = "string" });
            m["runtime"]!["windows-x64"]!["arguments"]!.AsArray().Add(argument);
        });

        Assert.Contains(errors, e => e.Message.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void BacktrackingRegularExpressionsAreRefused()
    {
        var errors = Errors(m => m["readiness"] = new JsonObject { ["type"] = "logPattern", ["pattern"] = @"^(a)\1$", ["timeoutSeconds"] = 10 });

        Assert.Contains(errors, e => e.Path == "readiness.pattern");
    }

    [Fact]
    public void OnlyTheTestManifestCanInstallTheTestServer()
    {
        var errors = Errors(m => m["key"] = "not-the-test");

        Assert.Contains(errors, e => e.Path == "install.steps[0]");
    }

    [Fact]
    public void ReservedEnvironmentVariablesAndIncompleteConsolesAreRefused()
    {
        var errors = Errors(m =>
        {
            m["runtime"]!["windows-x64"]!["environment"]!["PATH"] = "C:/evil";
            m["runtime"]!["windows-x64"]!["console"] = new JsonObject { ["mode"] = "logfile+rcon" }; // without its log file and RCON
            m["stop"]!["steps"]!.AsArray().Add(new JsonObject { ["type"] = "ctrlC" });
        });

        Assert.Contains(errors, e => e.Path == "runtime.windows-x64.environment.PATH");
        Assert.Contains(errors, e => e.Path == "runtime.windows-x64.console.logFile");
        Assert.Contains(errors, e => e.Path == "runtime.windows-x64.console.rcon");
        Assert.Contains(errors, e => e.Path == "stop.steps[0]"); // a console command needs a console
        Assert.Contains(errors, e => e.Path == "stop.steps[1]");
    }

    [Fact]
    public void AProcessConsoleCanTakeItsCommandsThroughRcon()
    {
        // Palworld: the output comes from its console, commands go through RCON.
        var manifest = (Action<JsonObject> change) => Manifest(m =>
        {
            var console = new JsonObject { ["mode"] = "pty", ["rcon"] = new JsonObject { ["protocol"] = "source", ["port"] = "game" } };
            change(console);
            m["runtime"]!["windows-x64"]!["console"] = console;
            m["runtime"]!["windows-x64"]!["arguments"]!.AsArray().Add("-adminpassword={{rcon.password}}");
            m["ports"] = new JsonArray(new JsonObject { ["name"] = "game", ["label"] = "Game", ["protocols"] = new JsonArray("udp"), ["default"] = 8211 });
        });

        Assert.Equal(ManifestConsoleMode.Pty, Parse(manifest(_ => { })).Runtime[ManifestValidator.WindowsX64].Console.Mode);
        Parse(manifest(c => c["mode"] = "pipes"));
        Assert.Contains(TestManifests.Errors(manifest(c => c["logFile"] = "output.log")), e => e.Path == "runtime.windows-x64.console.logFile");
        Assert.Contains(TestManifests.Errors(manifest(c => c["inputNewline"] = "\r\n")), e => e.Path == "runtime.windows-x64.console.inputNewline");
    }

    [Theory]
    [InlineData("message", null)] // a string with a pattern
    [InlineData("readyAfterMs", "install.steps[2].versionVar")] // an integer
    [InlineData("nope", "install.steps[2].versionVar")]
    public void MinecraftsVersionComesFromAVariable(string variable, string? error)
    {
        var json = Manifest(m => m["install"]!["steps"]!.AsArray().Add(new JsonObject { ["type"] = "minecraftServer", ["versionVar"] = variable }));

        if (error is null)
        {
            Assert.IsType<MinecraftServerStep>(Parse(json).Install!.Steps[2]);
        }
        else
        {
            Assert.Contains(TestManifests.Errors(json), e => e.Path == error);
        }
    }

    [Fact]
    public void TheServerFolderIsOnlyKnownToTheAgent()
    {
        // SCP:SL ignores relative folders for its configuration.
        var manifest = Parse(Manifest(m => m["runtime"]!["windows-x64"]!["arguments"]!.AsArray().Add("-configpath={{server.dir}}\\config\\")));
        var binding = new ServerBinding("abcd1234", new Dictionary<string, string?>());

        Assert.Null(LaunchPlanRenderer.Check(manifest, ManifestValidator.WindowsX64, binding)); // the panel checks with a stand-in
        Assert.Throws<LaunchPlanException>(() => LaunchPlanRenderer.Render(manifest, ManifestValidator.WindowsX64, binding));
        var plan = LaunchPlanRenderer.Render(manifest, ManifestValidator.WindowsX64, binding with { Directory = "D:\\Pulsar\\servers\\abcd1234" });
        Assert.Equal("-configpath=D:\\Pulsar\\servers\\abcd1234\\config\\", plan.Arguments[^1]);
    }

    [Fact]
    public void AFollowingPortIsTheOneItFollowsPlusItsOffset()
    {
        var json = Manifest(m => m["ports"] = Ports(follows: new JsonObject { ["port"] = "game", ["offset"] = 1 }));
        var manifest = Parse(json); // valid

        var binding = (int query) => new ServerBinding("abcd1234", new Dictionary<string, string?>(),
            new Dictionary<string, PortAssignment> { ["game"] = new("10.0.0.5", 2456), ["query"] = new("10.0.0.5", query) });
        Assert.Null(LaunchPlanRenderer.Check(manifest, ManifestValidator.WindowsX64, binding(2457)));
        Assert.Equal("Port 'query' must be port 'game' + 1 on the same address.", LaunchPlanRenderer.Check(manifest, ManifestValidator.WindowsX64, binding(2460)));
    }

    [Theory]
    [InlineData("nope", 1, 2457, "ports[1].follows.port")] // no such port
    [InlineData("query", 1, 2457, "ports[1].follows.port")] // itself
    [InlineData("game", 0, 2456, "ports[1].follows.offset")]
    [InlineData("game", 1, 2458, "ports[1].default")] // not the followed default + 1
    public void FollowingPortsAreChecked(string port, int offset, int defaultPort, string path)
    {
        var errors = Errors(m => m["ports"] = Ports(new JsonObject { ["port"] = port, ["offset"] = offset }, defaultPort));

        Assert.Contains(errors, e => e.Path == path);
    }

    private static JsonArray Ports(JsonObject follows, int queryDefault = 2457) =>
    [
        new JsonObject { ["name"] = "game", ["label"] = "Game", ["protocols"] = new JsonArray("udp"), ["default"] = 2456 },
        new JsonObject { ["name"] = "query", ["label"] = "Query", ["protocols"] = new JsonArray("udp"), ["default"] = queryDefault, ["follows"] = follows },
    ];

    [Fact]
    public void AServerIsQueriedOnItsQueryPortsAddress()
    {
        var manifest = Parse(Manifest(m =>
        {
            m["ports"] = QueryPorts();
            m["query"] = new JsonObject { ["protocol"] = "a2s", ["port"] = "query" };
        }));
        var binding = (string address) => new ServerBinding("abcd1234", new Dictionary<string, string?>(),
            new Dictionary<string, PortAssignment> { ["game"] = new(address, 28015), ["query"] = new(address, 28017), ["rcon"] = new(address, 28016) });

        Assert.Equal(new QueryEndpoint(QueryProtocol.A2s, "127.0.0.1", 28017),
            LaunchPlanRenderer.Render(manifest, ManifestValidator.WindowsX64, binding(PortAssignment.AnyAddress)).Query);
        Assert.Equal(new QueryEndpoint(QueryProtocol.A2s, "10.0.0.5", 28017),
            LaunchPlanRenderer.Render(manifest, ManifestValidator.WindowsX64, binding("10.0.0.5")).Query);
        Assert.Null(Render(TestManifest(), new Dictionary<string, string?>()).Query);
    }

    [Theory]
    [InlineData("a2s", "nope")] // no such port
    [InlineData("a2s", "rcon")] // TCP only: A2S is UDP
    [InlineData("minecraft", "query")] // UDP only: the Server List Ping is TCP
    [InlineData("a2s", "tv")] // optional: the server may not have it
    public void TheQueryPortIsChecked(string protocol, string port)
    {
        var errors = Errors(m =>
        {
            m["ports"] = QueryPorts();
            m["query"] = new JsonObject { ["protocol"] = protocol, ["port"] = port };
        });

        Assert.Contains(errors, e => e.Path == "query.port");
    }

    [Fact]
    public void QueryReadinessNeedsAQuery()
    {
        var readiness = new JsonObject { ["type"] = "query", ["timeoutSeconds"] = 120 };

        Assert.Contains(Errors(m => m["readiness"] = readiness.DeepClone()), e => e.Path == "readiness");
        Assert.IsType<QueryReadiness>(Parse(Manifest(m =>
        {
            m["ports"] = QueryPorts();
            m["query"] = new JsonObject { ["protocol"] = "a2s", ["port"] = "query" };
            m["readiness"] = readiness.DeepClone();
        })).Readiness);
        Assert.Contains(Errors(m =>
        {
            m["ports"] = QueryPorts();
            m["query"] = new JsonObject { ["protocol"] = "a2s", ["port"] = "query" };
            m["readiness"] = new JsonObject { ["type"] = "query", ["timeoutSeconds"] = 0 };
        }), e => e.Path == "readiness.timeoutSeconds");
    }

    private static JsonArray QueryPorts() =>
    [
        new JsonObject { ["name"] = "game", ["label"] = "Game", ["protocols"] = new JsonArray("udp"), ["default"] = 28015 },
        new JsonObject { ["name"] = "query", ["label"] = "Query", ["protocols"] = new JsonArray("udp"), ["default"] = 28017 },
        new JsonObject { ["name"] = "rcon", ["label"] = "Remote console", ["protocols"] = new JsonArray("tcp"), ["default"] = 28016 },
        new JsonObject { ["name"] = "tv", ["label"] = "TV", ["protocols"] = new JsonArray("udp"), ["default"] = 28020, ["required"] = false },
    ];

    [Fact]
    public void TheHashIdentifiesTheExactBytes()
    {
        var bytes = File.ReadAllBytes(TestManifestPath());
        var withSpace = bytes.Append((byte)' ').ToArray();

        Assert.Equal(ManifestParser.Parse(bytes).Sha256, ManifestParser.Parse(bytes).Sha256);
        Assert.NotEqual(ManifestParser.Parse(bytes).Sha256, ManifestParser.Parse(withSpace).Sha256);
    }

    private static LaunchPlan Render(GameManifest manifest, IReadOnlyDictionary<string, string?> values, IReadOnlyList<string>? extra = null) =>
        LaunchPlanRenderer.Render(manifest, ManifestValidator.WindowsX64, new ServerBinding("abcd1234", values, ExtraArguments: extra));

    private static LaunchPlan RenderCs2(IReadOnlyList<string> extra) => LaunchPlanRenderer.Render(TestManifests.Load("cs2"),
        ManifestValidator.WindowsX64, new ServerBinding("abcd1234", new Dictionary<string, string?>(),
            new Dictionary<string, PortAssignment> { ["game"] = new(PortAssignment.AnyAddress, 27015) }, extra, RconPassword: "generated"));

    private static GameManifest TestManifest() => TestManifests.Load("_test");

    private static GameManifest Parse(string json) => TestManifests.Parse(json);

    private static IReadOnlyList<ManifestError> Errors(Action<JsonObject> change) => TestManifests.Errors(Manifest(change));

    private static string Manifest(Action<JsonObject> change) => TestManifests.Change("_test", change);

    private static string TestManifestPath() => TestManifests.PathOf("_test");

    private static string RepositoryRoot() => TestManifests.RepositoryRoot();
}
