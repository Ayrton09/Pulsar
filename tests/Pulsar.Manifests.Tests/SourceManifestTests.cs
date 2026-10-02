using System.Text.Json.Nodes;

namespace Pulsar.Manifests.Tests;

/// <summary>Ports, RCON, SteamCMD and configuration files, through the Counter-Strike: Source manifest.</summary>
public sealed class SourceManifestTests
{
    private const string Rcon = "Rc0nPassw0rdRc0nPassw0rdRc0nPass";

    private static readonly Dictionary<string, PortAssignment> s_gamePort = new() { ["game"] = new("0.0.0.0", 27115) };

    [Fact]
    public void RendersPortsRconAndConfigurationFiles()
    {
        var plan = Render(new Dictionary<string, string?> { ["map"] = "cs_office", ["maxPlayers"] = "24" });

        Assert.Equal("srcds.exe", plan.Executable);
        Assert.Equal(
        [
            "-game", "cstrike", "-console", "-condebug", "-nocrashdialog", "-ip", "0.0.0.0", "-port", "27115",
            "+maxplayers", "24", "+map", "cs_office", "+rcon_password", Rcon, "+sv_rcon_maxfailures", "5", "+sv_rcon_banpenalty", "30",
        ], plan.Arguments);
        Assert.Equal(ManifestConsoleMode.LogfileRcon, plan.Console.Mode);
        Assert.Equal("cstrike/console.log", plan.Console.LogFile);
        Assert.Equal(new RconEndpoint(RconProtocol.Source, "127.0.0.1", 27115, Rcon), plan.Rcon);
        var port = Assert.Single(plan.Ports);
        Assert.Equal(("game", "0.0.0.0", 27115), (port.Name, port.IpAddress, port.Port));
        Assert.Equal([PortProtocol.Udp, PortProtocol.Tcp], port.Protocols);

        var file = Assert.Single(plan.ConfigFiles);
        Assert.Equal("cstrike/cfg/server.cfg", file.Path);
        Assert.Equal([new ConfigEntry("hostname", "Pulsar CS:S", Secret: false), new ConfigEntry("sv_password", "", Secret: true)], file.Entries);
        Assert.IsType<RconReadiness>(plan.Readiness);
        Assert.Equal("quit", Assert.IsType<RconCommandStop>(Assert.Single(plan.Stop.Steps)).Command);
    }

    [Fact]
    public void AnOptionalPortAddsItsArgumentsOnlyWhenAssigned()
    {
        var ports = new Dictionary<string, PortAssignment>(s_gamePort) { ["tv"] = new("192.168.1.10", 27120) };

        var plan = Render(new Dictionary<string, string?>(), ports);

        Assert.Equal(["+tv_enable", "1", "+tv_port", "27120"], plan.Arguments.Skip(9).Take(4));
        Assert.Contains(plan.Ports, p => p is { Name: "tv", IpAddress: "192.168.1.10", Port: 27120, Protocols: [PortProtocol.Udp] });
    }

    [Fact]
    public void ARconEndpointUsesTheAllocationsAddressWhenItIsNotEveryInterface()
    {
        var plan = Render(new Dictionary<string, string?>(), new Dictionary<string, PortAssignment> { ["game"] = new("10.0.0.5", 27015) });

        Assert.Equal("10.0.0.5", plan.Rcon!.Address);
        Assert.DoesNotContain(Rcon, plan.Rcon.ToString(), StringComparison.Ordinal);
    }

    public static TheoryData<string, string> BadBindings() => new()
    {
        { """{}""", "Port 'game' has no allocation" },
        { """{"game": {"ip": "0.0.0.0", "port": 0}}""", "invalid allocation" },
        { """{"game": {"ip": "999.1.1.1", "port": 27015}}""", "invalid allocation" },
        { """{"game": {"ip": "::1", "port": 27015}}""", "invalid allocation" },
        { """{"game": {"ip": "0.0.0.0", "port": 27015}, "tv": {"ip": "0.0.0.0", "port": 27015}}""", "same port number" },
        { """{"game": {"ip": "0.0.0.0", "port": 27015}, "query": {"ip": "0.0.0.0", "port": 27016}}""", "Unknown port 'query'" },
    };

    [Theory]
    [MemberData(nameof(BadBindings))]
    public void PortsMustMatchTheManifest(string ports, string message)
    {
        var assignments = JsonNode.Parse(ports)!.AsObject().ToDictionary(p => p.Key, p => new PortAssignment((string)p.Value!["ip"]!, (int)p.Value!["port"]!));

        var error = Assert.Throws<LaunchPlanException>(() => Render(new Dictionary<string, string?>(), assignments));

        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAgentMustProvideTheRconPassword()
    {
        var manifest = TestManifests.Load("css");

        Assert.Throws<LaunchPlanException>(() => LaunchPlanRenderer.Render(manifest, ManifestValidator.WindowsX64,
            new ServerBinding("abcd1234", new Dictionary<string, string?>(), s_gamePort)));
        Assert.Null(LaunchPlanRenderer.Check(manifest, ManifestValidator.WindowsX64, new ServerBinding("abcd1234", new Dictionary<string, string?>(), s_gamePort)));
    }

    [Theory]
    [InlineData("hostname", "My \"quoted\" server")]
    [InlineData("hostname", "x; rcon_password owned")]
    [InlineData("password", "has space")]
    public void ConfigurationValuesCannotBreakTheirLine(string key, string value)
    {
        Assert.Throws<LaunchPlanException>(() => Render(new Dictionary<string, string?> { [key] = value }));
    }

    [Fact]
    public void SecretsAreNotEchoedInErrors()
    {
        var manifest = TestManifests.Parse(TestManifests.Change("css", m =>
        {
            m["variables"]!.AsArray().Add(new JsonObject
            {
                ["key"] = "token",
                ["label"] = "Token",
                ["type"] = "string",
                ["secret"] = true,
                ["pattern"] = "^[A-Za-z0-9-]{1,64}$",
            });
            m["runtime"]!["windows-x64"]!["arguments"]!.AsArray().Add("+sv_setsteamaccount");
            m["runtime"]!["windows-x64"]!["arguments"]!.AsArray().Add("{{var.token}}");
        }));

        var error = Assert.Throws<LaunchPlanException>(() => LaunchPlanRenderer.Render(manifest, ManifestValidator.WindowsX64,
            new ServerBinding("abcd1234", new Dictionary<string, string?> { ["token"] = "-secret-value" }, s_gamePort, RconPassword: Rcon)));

        Assert.DoesNotContain("secret-value", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("console.logFile", null, "runtime.windows-x64.console.logFile")]
    [InlineData("console.logFile", "../outside.log", "runtime.windows-x64.console.logFile")]
    [InlineData("console.rcon.port", "tv", "runtime.windows-x64.console.rcon.port")] // UDP only and optional
    [InlineData("console.rcon.port", "nope", "runtime.windows-x64.console.rcon.port")]
    [InlineData("console.inputNewline", "\n", "runtime.windows-x64.console.inputNewline")]
    public void LogfileRconConsolesAreComplete(string property, string? value, string path)
    {
        var errors = TestManifests.Errors(TestManifests.Change("css", m =>
        {
            var target = m["runtime"]!["windows-x64"]!.AsObject();
            var parts = property.Split('.');
            foreach (var part in parts[..^1])
            {
                target = target[part]!.AsObject();
            }

            if (value is null)
            {
                target.Remove(parts[^1]);
            }
            else
            {
                target[parts[^1]] = value;
            }
        }));

        Assert.Contains(errors, e => e.Path == path);
    }

    [Fact]
    public void AnRconConsoleNeedsItsPasswordPassedToTheGame()
    {
        var errors = TestManifests.Errors(TestManifests.Change("css", m =>
        {
            var arguments = m["runtime"]!["windows-x64"]!["arguments"]!.AsArray();
            arguments.Remove(arguments.Single(a => a is JsonValue v && (string?)v == "{{rcon.password}}"));
        }));

        Assert.Contains(errors, e => e.Message.Contains("RCON password", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"type": "consoleCommand", "command": "quit"}""", "use 'rconCommand'")]
    [InlineData("""{"type": "ctrlC"}""", "Ctrl+C needs")]
    public void StopStepsMatchTheConsole(string step, string message)
    {
        var errors = TestManifests.Errors(TestManifests.Change("css", m => m["stop"]!["steps"]!.AsArray().Add(JsonNode.Parse(step))));

        Assert.Contains(errors, e => e.Message.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void OptionalPortsAreUsedOnlyInsideTheirCondition()
    {
        var errors = TestManifests.Errors(TestManifests.Change("css", m => m["runtime"]!["windows-x64"]!["arguments"]!.AsArray().Add("{{port.tv}}")));

        Assert.Contains(errors, e => e.Message.Contains("Port 'tv' is optional", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"type": "steamcmd", "appId": 232330, "login": "someone"}""", "install.steps[1].login")]
    [InlineData("""{"type": "steamcmd", "appId": 0}""", "install.steps[1].appId")]
    [InlineData("""{"type": "steamcmd", "appId": 232330, "beta": "pre release"}""", "install.steps[1].beta")]
    [InlineData("""{"type": "steamcmd", "appId": 232330, "beta": "x", "betaPasswordVar": "hostname"}""", "install.steps[1].betaPasswordVar")]
    [InlineData("""{"type": "writeFileIfMissing", "path": "../x.cfg", "content": ""}""", "install.steps[1].path")]
    [InlineData("""{"type": "testGameServer", "path": "bin"}""", "install.steps[1]")]
    public void InstallStepsAreChecked(string step, string path)
    {
        var errors = TestManifests.Errors(TestManifests.Change("css", m => m["install"]!["steps"]!.AsArray().Add(JsonNode.Parse(step))));

        Assert.Contains(errors, e => e.Path == path);
    }

    [Theory]
    [InlineData("hostname x", "{{var.hostname}}")]
    [InlineData("hostname", "literal; quit")]
    [InlineData("hostname", "{{var.free}}")] // a string without a pattern
    public void ConfigurationFilesAreChecked(string key, string value)
    {
        var errors = TestManifests.Errors(TestManifests.Change("css", m =>
        {
            m["variables"]!.AsArray().Add(new JsonObject { ["key"] = "free", ["label"] = "Free", ["type"] = "string" });
            m["configFiles"]![0]!["set"] = new JsonObject { [key] = value };
        }));

        Assert.Contains(errors, e => e.Path.StartsWith("configFiles[0].set", StringComparison.Ordinal));
    }

    [Fact]
    public void SecretsStayOutOfTheEnvironment()
    {
        var errors = TestManifests.Errors(TestManifests.Change("css",
            m => m["runtime"]!["windows-x64"]!["environment"] = new JsonObject { ["JOIN"] = "{{var.password}}", ["RCON"] = "{{rcon.password}}" }));

        Assert.Contains(errors, e => e.Path == "runtime.windows-x64.environment.JOIN");
        Assert.Contains(errors, e => e.Path == "runtime.windows-x64.environment.RCON");
    }

    [Theory]
    [InlineData("../cfg/**")]
    [InlineData("C:/Windows/**")]
    [InlineData("cfg\\**")]
    public void BackupGlobsStayInsideTheServer(string glob)
    {
        var errors = TestManifests.Errors(TestManifests.Change("css", m => m["backup"]!["include"]!.AsArray().Add(glob)));

        Assert.Contains(errors, e => e.Path.StartsWith("backup.include", StringComparison.Ordinal));
    }

    [Fact]
    public void PortsAreNamedOnceWithDistinctDefaults()
    {
        var errors = TestManifests.Errors(TestManifests.Change("css", m =>
        {
            m["ports"]!.AsArray().Add(new JsonObject { ["name"] = "game", ["label"] = "Again", ["protocols"] = new JsonArray("udp"), ["default"] = 27020 });
            m["ports"]!.AsArray().Add(new JsonObject { ["name"] = "Query", ["label"] = "Query", ["protocols"] = new JsonArray(), ["default"] = 70000 });
        }));

        Assert.Contains(errors, e => e.Path == "ports[2].name");
        Assert.Contains(errors, e => e.Path == "ports[3].name");
        Assert.Contains(errors, e => e.Path == "ports[3].protocols");
        Assert.Contains(errors, e => e.Path == "ports[3].default");
        Assert.Contains(errors, e => e.Path == "ports");
    }

    private static LaunchPlan Render(IReadOnlyDictionary<string, string?> values, IReadOnlyDictionary<string, PortAssignment>? ports = null) =>
        LaunchPlanRenderer.Render(TestManifests.Load("css"), ManifestValidator.WindowsX64, new ServerBinding("abcd1234", values, ports ?? s_gamePort,
            RconPassword: Rcon));
}
