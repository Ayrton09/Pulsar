using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using LateApexEarlySpeed.Json.Schema;
using LateApexEarlySpeed.Json.Schema.Common;

namespace Pulsar.Manifests.Tests;

/// <summary>The published JSON Schema (<c>schemas/manifest.v1.schema.json</c>) accepts the real manifests and matches the model.</summary>
public sealed class SchemaTests
{
    private static readonly JsonObject s_schema = JsonNode.Parse(File.ReadAllText(SchemaPath()))!.AsObject();

    public static TheoryData<string> BundledManifests() => [.. Directory.EnumerateFiles(Path.Combine(TestManifests.RepositoryRoot(), "games"),
        "manifest.json", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(TestManifests.RepositoryRoot(), p))];

    [Theory]
    [MemberData(nameof(BundledManifests))]
    public void EveryManifestInTheRepositoryMatchesTheSchema(string path)
    {
        var result = Validate(File.ReadAllText(Path.Combine(TestManifests.RepositoryRoot(), path)));

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.ValidationErrors.Select(e => $"{e.Keyword} at {e.InstanceLocation}: {e.ErrorMessage}")));
    }

    [Theory]
    [InlineData("""{"surprise": true}""")]
    [InlineData("""{"install": {"steps": [{"type": "run", "command": "cmd"}]}}""")]
    [InlineData("""{"install": {"steps": [{"type": "steamcmd", "appId": 730, "login": "someone"}]}}""")]
    [InlineData("""{"stop": {"steps": [{"type": "wait", "seconds": 5, "extra": 1}], "timeoutSeconds": 5}}""")]
    [InlineData("""{"readiness": {"type": "logPattern", "timeoutSeconds": 5}}""")]
    [InlineData("""{"ports": [{"name": "game", "label": "Game", "protocols": ["sctp"], "default": 1}]}""")]
    [InlineData("""{"configFiles": [{"path": "../server.cfg", "format": "source-cfg", "mode": "patch", "set": {"a": "b"}}]}""")]
    [InlineData("""{"configFiles": [{"path": ".Pulsar/server.cfg", "format": "source-cfg", "mode": "patch", "set": {"a": "b"}}]}""")]
    [InlineData("""{"version": "1.0.١"}""")]
    [InlineData("""{"addons": [{"key": "x1", "name": "X", "license": "MIT", "url": "https://example.com/", "target": "cstrike", "source": {"type": "githubRelease", "repo": "../..", "asset": "x"}}]}""")]
    [InlineData("""{"addons": [{"key": "x1", "name": "X", "license": "MIT", "url": "https://example.com/", "target": ".pulsar", "source": {"type": "githubRelease", "repo": "a/b", "asset": "x"}}]}""")]
    [InlineData("""{"addons": [{"key": "x1", "name": "X", "license": "MIT", "url": "https://example.com/", "target": "cstrike/../..", "source": {"type": "githubRelease", "repo": "a/b", "asset": "x"}}]}""")]
    [InlineData("""{"addons": [{"key": "x1", "name": "X", "license": "MIT", "url": "https://example.com/", "target": "PULSAR~1", "source": {"type": "githubRelease", "repo": "a/b", "asset": "x"}}]}""")]
    // The same paths the file manager refuses (ServerFilePath): an empty segment, a trailing slash, a device name, a name Windows would change
    // (a trailing dot or space), a control character.
    [InlineData("""{"configFiles": [{"path": "cstrike//server.cfg", "format": "source-cfg", "mode": "patch", "set": {"a": "b"}}]}""")]
    [InlineData("""{"configFiles": [{"path": "cstrike/cfg/", "format": "source-cfg", "mode": "patch", "set": {"a": "b"}}]}""")]
    [InlineData("""{"configFiles": [{"path": "cstrike/CON", "format": "source-cfg", "mode": "patch", "set": {"a": "b"}}]}""")]
    [InlineData("""{"configFiles": [{"path": "cstrike/nul.txt", "format": "source-cfg", "mode": "patch", "set": {"a": "b"}}]}""")]
    [InlineData("""{"configFiles": [{"path": "cstrike/Com1 .cfg", "format": "source-cfg", "mode": "patch", "set": {"a": "b"}}]}""")]
    [InlineData("""{"configFiles": [{"path": "cstrike/server.", "format": "source-cfg", "mode": "patch", "set": {"a": "b"}}]}""")]
    [InlineData("""{"configFiles": [{"path": "cstrike/server.cfg ", "format": "source-cfg", "mode": "patch", "set": {"a": "b"}}]}""")]
    [InlineData("""{"configFiles": [{"path": "cstrike/ server.cfg", "format": "source-cfg", "mode": "patch", "set": {"a": "b"}}]}""")]
    [InlineData("""{"configFiles": [{"path": "cstrike/ser\u0001ver.cfg", "format": "source-cfg", "mode": "patch", "set": {"a": "b"}}]}""")]
    // A fixed release is not a pre-release.
    [InlineData("""{"addons": [{"key": "x1", "name": "X", "license": "MIT", "url": "https://example.com/", "target": "cstrike", "source": {"type": "githubRelease", "repo": "a/b", "asset": "x", "tag": "v1", "prerelease": true}}]}""")]
    public void TheSchemaRefusesWhatTheParserRefuses(string overlay)
    {
        var json = TestManifests.Change("css", m =>
        {
            foreach (var (name, value) in JsonNode.Parse(overlay)!.AsObject())
            {
                m[name] = value?.DeepClone();
            }
        });

        Assert.False(ManifestParser.Parse(System.Text.Encoding.UTF8.GetBytes(json)).IsValid);
        Assert.False(Validate(json).IsValid);
    }

    [Theory]
    [InlineData("""{"a": 1}""")]
    [InlineData("""[1]""")]
    public void AVariablesDefaultIsAScalarForTheSchemaAndTheParser(string value)
    {
        var json = TestManifests.Change("css", m => m["variables"]!.AsArray().First(v => v!["key"]!.GetValue<string>() == "map")!["default"] = JsonNode.Parse(value));

        var result = ManifestParser.Parse(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.Contains(result.Errors, e => e.Path.EndsWith(".default", StringComparison.Ordinal));
        Assert.False(Validate(json).IsValid);
    }

    // An addon's target is a relative path, or empty for the server's folder.
    [Theory]
    [InlineData("")]
    [InlineData("cstrike/addons")]
    public void TheSchemaTakesWhatTheParserTakes(string target)
    {
        var json = TestManifests.Change("css", m => m["addons"] = JsonNode.Parse($$$"""
            [{"key": "x1", "name": "X", "license": "MIT", "url": "https://example.com/", "target": "{{{target}}}", "source": {"type": "githubRelease", "repo": "a/b", "asset": "x"}}]
            """));

        Assert.True(ManifestParser.Parse(System.Text.Encoding.UTF8.GetBytes(json)).IsValid);
        Assert.True(Validate(json).IsValid);
    }

    /// <summary>Every record of the model has a definition with the same JSON properties, required ones and enum values.</summary>
    [Fact]
    public void TheSchemaDescribesEveryPartOfTheModel()
    {
        var definitions = s_schema["$defs"]!.AsObject();
        var seen = new HashSet<Type>();
        var pending = new Queue<Type>([typeof(GameManifest)]);
        while (pending.TryDequeue(out var type))
        {
            if (!seen.Add(type))
            {
                continue;
            }

            foreach (var derived in type.GetCustomAttributes<JsonDerivedTypeAttribute>())
            {
                pending.Enqueue(derived.DerivedType);
            }

            if (type.IsAbstract)
            {
                if (type == typeof(ArgumentTemplate))
                {
                    pending.Enqueue(typeof(ArgumentCondition)); // the other form is a plain string
                }

                continue;
            }

            var definition = definitions[type.Name]?.AsObject();
            Assert.True(definition is not null, $"The schema has no $defs/{type.Name}.");
            var properties = definition["properties"]!.AsObject();
            var discriminator = type.BaseType?.GetCustomAttribute<JsonPolymorphicAttribute>() is not null ? new[] { "type" } : [];

            var constructor = type.GetConstructors().MaxBy(c => c.GetParameters().Length)!;
            var expected = constructor.GetParameters().Select(p => JsonName(p.Name!))
                .Concat(type.GetProperties().Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name).OfType<string>())
                .Concat(discriminator).Order(StringComparer.Ordinal).ToList();
            Assert.Equal(expected, properties.Select(p => p.Key).Order(StringComparer.Ordinal).ToList());

            var required = constructor.GetParameters().Where(p => !p.HasDefaultValue).Select(p => JsonName(p.Name!)).Concat(discriminator)
                .Order(StringComparer.Ordinal).ToList();
            Assert.Equal(required, (definition["required"]?.AsArray().Select(r => (string)r!) ?? []).Order(StringComparer.Ordinal).ToList());

            foreach (var parameter in constructor.GetParameters())
            {
                var element = ElementType(parameter.ParameterType);
                if (element.IsEnum)
                {
                    var values = Enum.GetValues(element).Cast<object>().Select(v => JsonSerializer.Serialize(v, element, s_enumOptions).Trim('"'));
                    var schemaValues = EnumValues(properties[JsonName(parameter.Name!)]!);
                    Assert.Equal(values.Order(StringComparer.Ordinal), schemaValues.Order(StringComparer.Ordinal));
                }
                else if (element.Assembly == typeof(GameManifest).Assembly)
                {
                    pending.Enqueue(element);
                }
            }
        }

        Assert.Contains(typeof(SteamCmdStep), seen);
        Assert.Contains(typeof(RconDefinition), seen);
    }

    private static readonly JsonSerializerOptions s_enumOptions = new()
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // "logfile+rcon" as written
    };

    private static IEnumerable<string> EnumValues(JsonNode property)
    {
        var node = property["items"] ?? property;
        return node["enum"]?.AsArray().Select(v => (string)v!) ?? [(string)node["const"]!];
    }

    private static Type ElementType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            return ElementType(type.GetGenericArguments()[0]);
        }

        return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>) ? ElementType(type.GetGenericArguments()[1]) : type;
    }

    private static string JsonName(string name) => JsonNamingPolicy.CamelCase.ConvertName(name);

    // The validator gives each pattern 200 ms by default, which a busy CI runner can take just to compile the schema's longer ones.
    private static readonly JsonSchemaOptions s_validation = new() { RegexMatchTimeout = TimeSpan.FromSeconds(30) };

    private static ValidationResult Validate(string json) => new JsonValidator(File.ReadAllText(SchemaPath())).Validate(json, s_validation);

    private static string SchemaPath() => Path.Combine(TestManifests.RepositoryRoot(), "schemas", "manifest.v1.schema.json");
}
