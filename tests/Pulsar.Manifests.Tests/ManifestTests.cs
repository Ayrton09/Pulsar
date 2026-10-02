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

        var plan = LaunchPlanRenderer.Render(manifest, ManifestValidator.WindowsX64, new Dictionary<string, string?>(), "abcd1234");

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
        var plan = LaunchPlanRenderer.Render(TestManifest(), ManifestValidator.WindowsX64,
            new Dictionary<string, string?> { ["ignoreQuit"] = "true", ["readyAfterMs"] = "0" }, "abcd1234");

        Assert.Equal(["--ready-after", "0", "--ignore-quit"], plan.Arguments);
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
        Assert.Throws<LaunchPlanException>(() => LaunchPlanRenderer.Render(TestManifest(), ManifestValidator.WindowsX64,
            new Dictionary<string, string?> { [key] = value }, "abcd1234"));
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
            var plan = LaunchPlanRenderer.Render(manifest, ManifestValidator.WindowsX64, new Dictionary<string, string?> { ["map"] = map }, "abcd1234");
            Assert.Equal(map, plan.Arguments[^1]);
            return;
        }

        Assert.Throws<LaunchPlanException>(() => LaunchPlanRenderer.Render(manifest, ManifestValidator.WindowsX64,
            new Dictionary<string, string?> { ["map"] = map }, "abcd1234"));
    }

    [Fact]
    public void ExtraArgumentsNeedTheManifestsPermission()
    {
        Assert.Throws<LaunchPlanException>(() => LaunchPlanRenderer.Render(TestManifest(), ManifestValidator.WindowsX64,
            new Dictionary<string, string?>(), "abcd1234", ["-condebug"]));

        var allowing = Parse(Manifest(m => m["runtime"]!["windows-x64"]!["allowExtraArguments"] = true));
        var plan = LaunchPlanRenderer.Render(allowing, ManifestValidator.WindowsX64, new Dictionary<string, string?>(), "abcd1234", ["-condebug"]);
        Assert.Equal("-condebug", plan.Arguments[^1]);
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
    [InlineData("{{port.game}}", "Port placeholders")]
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
    public void ReservedEnvironmentVariablesAndUnsupportedConsoleModesAreRefused()
    {
        var errors = Errors(m =>
        {
            m["runtime"]!["windows-x64"]!["environment"]!["PATH"] = "C:/evil";
            m["runtime"]!["windows-x64"]!["console"] = new JsonObject { ["mode"] = "logfile+rcon" };
            m["stop"]!["steps"]!.AsArray().Add(new JsonObject { ["type"] = "ctrlC" });
        });

        Assert.Contains(errors, e => e.Path == "runtime.windows-x64.environment.PATH");
        Assert.Contains(errors, e => e.Path == "runtime.windows-x64.console.mode");
        Assert.Contains(errors, e => e.Path == "stop.steps[1]");
    }

    [Fact]
    public void TheHashIdentifiesTheExactBytes()
    {
        var bytes = File.ReadAllBytes(TestManifestPath());
        var withSpace = bytes.Append((byte)' ').ToArray();

        Assert.Equal(ManifestParser.Parse(bytes).Sha256, ManifestParser.Parse(bytes).Sha256);
        Assert.NotEqual(ManifestParser.Parse(bytes).Sha256, ManifestParser.Parse(withSpace).Sha256);
    }

    private static GameManifest TestManifest() => Parse(File.ReadAllText(TestManifestPath()));

    private static GameManifest Parse(string json)
    {
        var result = ManifestParser.Parse(Encoding.UTF8.GetBytes(json));
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        return result.Manifest!;
    }

    private static IReadOnlyList<ManifestError> Errors(Action<JsonObject> change)
    {
        var result = ManifestParser.Parse(Encoding.UTF8.GetBytes(Manifest(change)));
        Assert.NotEmpty(result.Errors);
        return result.Errors;
    }

    private static string Manifest(Action<JsonObject> change)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(TestManifestPath()))!.AsObject();
        change(manifest);
        return manifest.ToJsonString();
    }

    private static string TestManifestPath() => Path.Combine(RepositoryRoot(), "games", "_test", "manifest.json");

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Pulsar.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException($"Could not find Pulsar.slnx above {AppContext.BaseDirectory}.");
    }
}
