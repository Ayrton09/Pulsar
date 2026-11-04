using System.Reflection;
using System.Text.Json;
using Pulsar.Domain.Audit;

namespace Pulsar.Domain.Tests.Audit;

// The audit log names each action in the reader's language (src/Pulsar.Web/src/locales/*/common.json, audit.actions):
// one left out shows as its raw code ("server.owner_changed").
public sealed class AuditActionNamesTests
{
    [Fact]
    public void EveryActionIsNamedInEnglish()
    {
        var actions = typeof(AuditActions).GetNestedTypes()
            .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();
        Assert.True(actions.Count > 100, $"{actions.Count} actions");

        using var english = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Pulsar.Web", "src", "locales", "en", "common.json")));
        var names = english.RootElement.GetProperty("audit").GetProperty("actions");
        var unnamed = actions.Where(action => !Named(names, action)).ToList();
        Assert.True(unnamed.Count == 0, $"Not named: {string.Join(", ", unnamed)}");
    }

    private static bool Named(JsonElement names, string action)
    {
        var at = names;
        foreach (var part in action.Split('.'))
        {
            if (at.ValueKind != JsonValueKind.Object || !at.TryGetProperty(part, out at))
            {
                return false;
            }
        }

        return at.ValueKind == JsonValueKind.String;
    }

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
