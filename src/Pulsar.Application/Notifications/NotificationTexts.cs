using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pulsar.Domain.Common;
using Pulsar.Domain.Notifications;

namespace Pulsar.Application.Notifications;

/// <summary>
/// The messages, in the channel's language (one of the panel's: <see cref="NotificationChannel.Languages"/>). The texts are
/// the JSON files in Notifications/Texts, one per language, embedded in this assembly; a text a language lacks is the
/// English one. Built when the event happens, so a delivery retried later says what was true then.
/// </summary>
public static partial class NotificationTexts
{
    private static readonly FrozenDictionary<string, FrozenDictionary<string, string>> s_texts = Load();

    public static NotificationMessage ServerCrashed(string language, ServerFacts server, int? exitCode, bool crashLoop, string? link,
        bool memoryLimit = false)
    {
        var code = exitCode?.ToString(CultureInfo.InvariantCulture) ?? "?";
        var kind = memoryLimit && !crashLoop ? "serverOutOfMemory" : crashLoop ? "serverCrashLoop" : "serverCrashed";
        return new NotificationMessage(Text(language, $"{kind}.title", ("server", server.Name)), Text(language, $"{kind}.text"),
            NotificationSeverity.Error, ServerFields(language, server, (Text(language, "fields.exitCode"), code)), link);
    }

    public static NotificationMessage ServerStartFailed(string language, ServerFacts server, bool notReady, string? detail, string? link) =>
        notReady
            ? new NotificationMessage(Text(language, "serverNotReady.title", ("server", server.Name)), Text(language, "serverNotReady.text"),
                NotificationSeverity.Warning, ServerFields(language, server), link)
            : new NotificationMessage(Text(language, "serverStartFailed.title", ("server", server.Name)), Text(language, "serverStartFailed.text"),
                NotificationSeverity.Error, ServerFields(language, server, (Text(language, "fields.error"), detail)), link);

    public static NotificationMessage BackupFailed(string language, ServerFacts server, string backupName, string? error, bool remoteCopy, string? link)
    {
        var kind = remoteCopy ? "backupCopyFailed" : "backupFailed";
        return new NotificationMessage(Text(language, $"{kind}.title", ("server", server.Name)), Text(language, $"{kind}.text"),
            NotificationSeverity.Error,
            ServerFields(language, server, (Text(language, "fields.backup"), backupName), (Text(language, "fields.error"), error)), link);
    }

    public static NotificationMessage ScheduledTaskFailed(string language, ServerFacts server, string taskName, string? error, string? link) =>
        new(Text(language, "scheduledTaskFailed.title", ("task", taskName), ("server", server.Name)), Text(language, "scheduledTaskFailed.text"),
            NotificationSeverity.Error,
            ServerFields(language, server, (Text(language, "fields.task"), taskName), (Text(language, "fields.error"), error)), link);

    public static NotificationMessage GameUpdateAvailable(string language, ServerFacts server, string? installed, string latest, bool installs,
        string? link) =>
        new(Text(language, "gameUpdateAvailable.title", ("server", server.Name)),
            Text(language, installs ? "gameUpdateAvailable.installs" : "gameUpdateAvailable.text"), NotificationSeverity.Warning,
            ServerFields(language, server, (Text(language, "fields.installed"), installed), (Text(language, "fields.latest"), latest)), link);

    public static NotificationMessage GameUpdateInstalled(string language, ServerFacts server, string? latest, string? link) =>
        new(Text(language, "gameUpdateInstalled.title", ("server", server.Name)), Text(language, "gameUpdateInstalled.text"),
            NotificationSeverity.Recovered, ServerFields(language, server, (Text(language, "fields.latest"), latest)), link);

    public static NotificationMessage GameUpdateFailed(string language, ServerFacts server, string? latest, string? error, string? link) =>
        new(Text(language, "gameUpdateFailed.title", ("server", server.Name)), Text(language, "gameUpdateFailed.text"), NotificationSeverity.Error,
            ServerFields(language, server, (Text(language, "fields.latest"), latest), (Text(language, "fields.error"), error)), link);

    public static NotificationMessage NodeOffline(string language, string nodeName, DateTimeOffset since, string? link) =>
        new(Text(language, "nodeOffline.title", ("node", nodeName)), Text(language, "nodeOffline.text"), NotificationSeverity.Warning,
            [new(Text(language, "fields.node"), nodeName), new(Text(language, "fields.lastSeen"), Moment(since))], link);

    public static NotificationMessage NodeOnline(string language, string nodeName, string? link) =>
        new(Text(language, "nodeOnline.title", ("node", nodeName)), Text(language, "nodeOnline.text"), NotificationSeverity.Recovered,
            [new(Text(language, "fields.node"), nodeName)], link);

    public static NotificationMessage NodeDiskLow(string language, string nodeName, IReadOnlyList<(string Root, long Free, long Total)> drives, string? link)
    {
        var fields = new List<NotificationField> { new(Text(language, "fields.node"), nodeName) };
        foreach (var (root, free, total) in drives)
        {
            var percent = total > 0 ? (double)free / total * 100 : 0;
            fields.Add(new(root, Text(language, "nodeDiskLow.drive", ("free", Size(free, language)), ("total", Size(total, language)),
                ("percent", percent.ToString("0.#", Culture(language))))));
        }

        return new NotificationMessage(Text(language, "nodeDiskLow.title", ("node", nodeName)), Text(language, "nodeDiskLow.text"),
            NotificationSeverity.Warning, fields, link);
    }

    public static NotificationMessage PublicIpCertificateFailed(string language, string ip, string error, DateTimeOffset? expiresAt, string? link)
    {
        var fields = new List<NotificationField> { new(Text(language, "fields.address"), ip), new(Text(language, "fields.error"), Clip(error)) };
        if (expiresAt is { } at)
        {
            fields.Add(new(Text(language, "fields.currentCertificateExpires"), Moment(at)));
        }

        return new NotificationMessage(Text(language, "publicIpCertificateFailed.title"), Text(language, "publicIpCertificateFailed.text"),
            NotificationSeverity.Error, fields, link);
    }

    public static NotificationMessage PublicIpCertificateExpiring(string language, string ip, DateTimeOffset expiresAt, string? link) =>
        new(Text(language, "publicIpCertificateExpiring.title"), Text(language, "publicIpCertificateExpiring.text"), NotificationSeverity.Warning,
            [new(Text(language, "fields.address"), ip), new(Text(language, "fields.expires"), Moment(expiresAt))], link);

    public static NotificationMessage PublicIpCertificateRenewed(string language, string ip, DateTimeOffset expiresAt, string? link) =>
        new(Text(language, "publicIpCertificateRenewed.title"), Text(language, "publicIpCertificateRenewed.text"), NotificationSeverity.Recovered,
            [new(Text(language, "fields.address"), ip), new(Text(language, "fields.expires"), Moment(expiresAt))], link);

    public static NotificationMessage Test(string language, string? link) =>
        new(Text(language, "test.title"), Text(language, "test.text"), NotificationSeverity.Recovered, [], link);

    /// <summary>1 decimal, binary units, as the UI shows sizes, with the language's decimal separator.</summary>
    public static string Size(long bytes, string language = "en")
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value.ToString(unit == 0 ? "0" : "0.#", Culture(language))} {units[unit]}";
    }

    /// <summary>The language's text for <paramref name="key"/> (English when it has none), its {placeholders} filled.</summary>
    internal static string Text(string language, string key, params (string Name, string Value)[] values)
    {
        var text = (s_texts.TryGetValue(language, out var texts) && texts.TryGetValue(key, out var own) ? own : null)
            ?? s_texts["en"].GetValueOrDefault(key)
            ?? throw new InvalidOperationException($"No notification text {key}.");
        return Placeholder().Replace(text, match =>
        {
            var name = match.Groups[1].Value;
            foreach (var (candidate, value) in values)
            {
                if (candidate == name)
                {
                    return value;
                }
            }

            return match.Value;
        });
    }

    /// <summary>Every language's texts, flattened to "section.key": loaded once from the embedded files.</summary>
    internal static FrozenDictionary<string, FrozenDictionary<string, string>> Texts => s_texts;

    private static FrozenDictionary<string, FrozenDictionary<string, string>> Load()
    {
        var assembly = typeof(NotificationTexts).Assembly;
        var languages = new Dictionary<string, FrozenDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var language in NotificationChannel.Languages)
        {
            using var stream = assembly.GetManifestResourceStream($"Pulsar.Application.Notifications.Texts.{language}.json")
                ?? throw new InvalidOperationException($"The notification texts of {language} are not in the assembly.");
            using var document = JsonDocument.Parse(stream);
            var texts = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var section in document.RootElement.EnumerateObject())
            {
                foreach (var entry in section.Value.EnumerateObject())
                {
                    texts[$"{section.Name}.{entry.Name}"] = entry.Value.GetString()!;
                }
            }

            languages[language] = texts.ToFrozenDictionary(StringComparer.Ordinal);
        }

        return languages.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static string Moment(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    // Numbers as the language writes them (English: the invariant culture, as before).
    private static CultureInfo Culture(string language) => language switch
    {
        "en" or "" => CultureInfo.InvariantCulture,
        "pt" => CultureInfo.GetCultureInfo("pt-BR"),
        "zh" => CultureInfo.GetCultureInfo("zh-CN"),
        _ => NotificationChannel.Languages.Contains(language, StringComparer.Ordinal) ? CultureInfo.GetCultureInfo(language) : CultureInfo.InvariantCulture,
    };

    private static NotificationField[] ServerFields(string language, ServerFacts server, params (string Name, string? Value)[] extra) =>
    [
        new(Text(language, "fields.server"), server.Name),
        new(Text(language, "fields.game"), server.Game),
        new(Text(language, "fields.node"), server.NodeName),
        .. extra.Where(f => !string.IsNullOrWhiteSpace(f.Value)).Select(f => new NotificationField(f.Name, Clip(f.Value!))),
    ];

    private static string Clip(string value) => value.Length > 300 ? TextLimits.Truncate(value, 300) + "…" : value;

    [GeneratedRegex(@"\{(\w+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();
}

/// <summary>What messages say about a server.</summary>
public sealed record ServerFacts(Guid Id, string Name, string Game, string NodeName);
