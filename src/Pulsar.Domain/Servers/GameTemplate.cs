using Pulsar.Domain.Common;

namespace Pulsar.Domain.Servers;

public enum TemplateSource
{
    /// <summary>Shipped with the panel (<c>games/</c>).</summary>
    Bundled,

    /// <summary>Imported by an administrator (later phases).</summary>
    Imported,
}

/// <summary>
/// An immutable game manifest version (docs/architecture/04): <see cref="Key"/> + <see cref="Version"/> identify it and
/// <see cref="Content"/> keeps the exact bytes that were validated. Servers are pinned to one template.
/// </summary>
public sealed class GameTemplate
{
    private GameTemplate()
    {
        // For EF Core materialization.
        Key = null!;
        Version = null!;
        Name = null!;
        Content = null!;
        ContentSha256 = null!;
    }

    private GameTemplate(TemplateId id, string key, string version, int schemaVersion, string name, string? description, TemplateSource source,
        string content, byte[] contentSha256, DateTimeOffset now)
    {
        Id = id;
        Key = key;
        Version = version;
        SchemaVersion = schemaVersion;
        Name = name;
        Description = description;
        Source = source;
        Content = content;
        ContentSha256 = contentSha256;
        CreatedAt = now;
    }

    public TemplateId Id { get; private set; }

    public string Key { get; private set; }

    public string Version { get; private set; }

    public int SchemaVersion { get; private set; }

    public string Name { get; private set; }

    public string? Description { get; private set; }

    public TemplateSource Source { get; private set; }

    /// <summary>The manifest exactly as validated (text, not a parsed form: the hash identifies these bytes).</summary>
    public string Content { get; private set; }

    public byte[] ContentSha256 { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>A template from an already validated manifest.</summary>
    public static GameTemplate Create(string key, string version, int schemaVersion, string name, string? description, TemplateSource source,
        string content, byte[] contentSha256, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        ArgumentNullException.ThrowIfNull(contentSha256);
        return new GameTemplate(TemplateId.New(), key, version, schemaVersion, name, description, source, content, contentSha256, now);
    }

    /// <summary>
    /// Orders semantic versions (<c>1.2.3</c>, <c>1.2.3-beta.1</c>; manifests are validated to have one): by their
    /// numbers, and a pre-release before its release. Below zero when <paramref name="left"/> is older.
    /// </summary>
    public static int CompareVersions(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var (leftNumbers, leftPre) = Split(left);
        var (rightNumbers, rightPre) = Split(right);
        for (var i = 0; i < 3; i++)
        {
            var compared = leftNumbers[i].CompareTo(rightNumbers[i]);
            if (compared != 0)
            {
                return compared;
            }
        }

        return (leftPre, rightPre) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            _ => string.CompareOrdinal(leftPre, rightPre),
        };

        static (long[] Numbers, string? PreRelease) Split(string version)
        {
            var core = version.Split('+')[0];
            var dash = core.IndexOf('-', StringComparison.Ordinal);
            var numbers = (dash < 0 ? core : core[..dash]).Split('.');
            var parsed = new long[3];
            for (var i = 0; i < 3 && i < numbers.Length; i++)
            {
                _ = long.TryParse(numbers[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out parsed[i]);
            }

            return (parsed, dash < 0 ? null : core[(dash + 1)..]);
        }
    }
}
