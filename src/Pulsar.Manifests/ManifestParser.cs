using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pulsar.Manifests;

/// <summary>A problem with a manifest; <see cref="Path"/> points into the JSON (e.g. <c>runtime.windows-x64.executable</c>).</summary>
public sealed record ManifestError(string Path, string Message)
{
    public override string ToString() => $"{Path}: {Message}";
}

/// <param name="Sha256">Hash of the exact bytes: a template is immutable and identified by it.</param>
public sealed record ManifestParseResult(GameManifest? Manifest, IReadOnlyList<ManifestError> Errors, byte[] Sha256)
{
    public bool IsValid => Manifest is not null && Errors.Count == 0;
}

/// <summary>Reads a manifest strictly (unknown properties are errors) and validates it (<see cref="ManifestValidator"/>).</summary>
public static class ManifestParser
{
    /// <summary>Upper bound on a manifest's size: they are small, and they travel inside signed commands.</summary>
    public const int MaximumSize = 256 * 1024;

    internal static JsonSerializerOptions Options { get; } = CreateOptions();

    public static ManifestParseResult Parse(ReadOnlySpan<byte> utf8Json)
    {
        var hash = SHA256.HashData(utf8Json);
        if (utf8Json.Length > MaximumSize)
        {
            return new ManifestParseResult(null, [new ManifestError("$", $"The manifest is larger than {MaximumSize / 1024} KiB.")], hash);
        }

        GameManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<GameManifest>(utf8Json, Options);
        }
        catch (JsonException ex)
        {
            return new ManifestParseResult(null, [new ManifestError(ex.Path ?? "$", ex.Message)], hash);
        }

        if (manifest is null)
        {
            return new ManifestParseResult(null, [new ManifestError("$", "The manifest is empty.")], hash);
        }

        return new ManifestParseResult(manifest, ManifestValidator.Validate(manifest), hash);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowOutOfOrderMetadataProperties = true,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            NumberHandling = JsonNumberHandling.Strict,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            MaxDepth = 32,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}
