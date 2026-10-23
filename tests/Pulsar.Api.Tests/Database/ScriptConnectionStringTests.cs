using System.ComponentModel;
using System.Reflection;
using System.Text.RegularExpressions;
using Npgsql;

namespace Pulsar.Api.Tests.Database;

/// <summary>
/// The install and backup scripts read the panel's connection string themselves (ConvertFrom-PgConnectionString, for
/// pg_dump, psql and pg_restore): every name Npgsql takes for the keys they use must be one they read as that key, or a
/// connection string the panel works with would be read wrong by its backup.
/// </summary>
public sealed partial class ScriptConnectionStringTests
{
    // The script's key for each property of Npgsql's the scripts use.
    private static readonly Dictionary<string, string> s_used = new(StringComparer.Ordinal)
    {
        [nameof(NpgsqlConnectionStringBuilder.Host)] = "host",
        [nameof(NpgsqlConnectionStringBuilder.Port)] = "port",
        [nameof(NpgsqlConnectionStringBuilder.Username)] = "username",
        [nameof(NpgsqlConnectionStringBuilder.Database)] = "database",
        [nameof(NpgsqlConnectionStringBuilder.Password)] = "password",
        [nameof(NpgsqlConnectionStringBuilder.SslMode)] = "ssl mode",
        [nameof(NpgsqlConnectionStringBuilder.RootCertificate)] = "root certificate",
    };

    [Theory]
    [InlineData("Install-Pulsar.ps1")]
    [InlineData("Backup-Pulsar.ps1")]
    public void EveryNameNpgsqlTakesForAKeyTheScriptsUseIsReadAsThatKey(string script)
    {
        var names = ScriptNames(script);
        foreach (var (property, key) in s_used)
        {
            foreach (var name in NpgsqlNames(property))
            {
                var read = names.TryGetValue(name, out var alias) ? alias : name;
                Assert.True(read == key, $"{script} reads Npgsql's '{name}' ({property}) as '{read}', not '{key}'.");
            }
        }

        // And no name of its own: each one it reads as a key, Npgsql takes as that key too.
        foreach (var (name, key) in names)
        {
            Assert.Equal(s_used.Single(u => u.Value == key).Key, NpgsqlPropertyOf(name));
        }
    }

    /// <summary>Every name Npgsql takes for a property: its keyword and its synonyms, in lower case (keys are case-insensitive).</summary>
    private static HashSet<string> NpgsqlNames(string property)
    {
        var info = typeof(NpgsqlConnectionStringBuilder).GetProperty(property)!;
        var names = new HashSet<string>(StringComparer.Ordinal) { info.GetCustomAttribute<DisplayNameAttribute>()!.DisplayName.ToLowerInvariant() };
        foreach (var attribute in info.GetCustomAttributes().Where(a => a.GetType().Name == "NpgsqlConnectionStringPropertyAttribute"))
        {
            foreach (var synonym in (string[])attribute.GetType().GetProperty("Synonyms")!.GetValue(attribute)!)
            {
                names.Add(synonym.ToLowerInvariant());
            }
        }

        // Each name works as that property.
        foreach (var name in names)
        {
            Assert.Equal(info.Name, NpgsqlPropertyOf(name));
        }

        return names;
    }

    private static string NpgsqlPropertyOf(string name)
    {
        var builder = new NpgsqlConnectionStringBuilder();
        builder[name] = name.Contains("ssl", StringComparison.Ordinal) ? "Require" : name == "port" ? "6543" : "x";
        return s_used.Keys.Single(property =>
        {
            var value = typeof(NpgsqlConnectionStringBuilder).GetProperty(property)!.GetValue(builder);
            return value is not null && !Equals(value, typeof(NpgsqlConnectionStringBuilder).GetProperty(property)!.GetValue(new NpgsqlConnectionStringBuilder()));
        });
    }

    /// <summary>The other names ConvertFrom-PgConnectionString reads as a key (its <c>$names</c> table).</summary>
    private static Dictionary<string, string> ScriptNames(string script)
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), "scripts", "install", script));
        var start = text.IndexOf("function ConvertFrom-PgConnectionString", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{script} has no ConvertFrom-PgConnectionString.");
        var table = text[start..text.IndexOf("$builder", start, StringComparison.Ordinal)];
        return Entry().Matches(table).ToDictionary(m => m.Groups["name"].Value, m => m.Groups["key"].Value, StringComparer.Ordinal);
    }

    [GeneratedRegex("'(?<name>[a-z ]+)' = '(?<key>[a-z ]+)'", RegexOptions.CultureInvariant)]
    private static partial Regex Entry();

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Pulsar.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Pulsar.slnx not found above the test output directory.");
    }
}
