using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using LateApexEarlySpeed.Json.Schema;

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
        var result = Validator().Validate(File.ReadAllText(Path.Combine(TestManifests.RepositoryRoot(), path)));

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
        Assert.False(Validator().Validate(json).IsValid);
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

    private static JsonValidator Validator() => new(File.ReadAllText(SchemaPath()));

    private static string SchemaPath() => Path.Combine(TestManifests.RepositoryRoot(), "schemas", "manifest.v1.schema.json");
}
