using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pulsar.Api.Tests.Infrastructure;

namespace Pulsar.Api.Tests;

/// <summary>
/// The frontend's TypeScript client is generated from src/Pulsar.Web/src/api/openapi.json. This test fails when that
/// copy is stale; run it with PULSAR_UPDATE_OPENAPI=true to rewrite it, then <c>npm run api:generate</c> in
/// src/Pulsar.Web (CI checks both).
/// </summary>
public sealed class OpenApiDocumentTests(NoDatabaseApiFactory factory) : IClassFixture<NoDatabaseApiFactory>
{
    private static readonly JsonSerializerOptions s_indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [Fact]
    public async Task CommittedDocumentMatchesTheApi()
    {
        using var client = factory.CreateClient();
        var live = await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);
        var normalized = JsonNode.Parse(live)!.ToJsonString(s_indented).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";

        var path = Path.Combine(RepositoryRoot(), "src", "Pulsar.Web", "src", "api", "openapi.json");
        if (string.Equals(Environment.GetEnvironmentVariable("PULSAR_UPDATE_OPENAPI"), "true", StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, normalized, TestContext.Current.CancellationToken);
        }

        Assert.True(File.Exists(path), $"{path} is missing. Run the tests with PULSAR_UPDATE_OPENAPI=true.");
        var committed = (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.True(committed == normalized,
            "src/Pulsar.Web/src/api/openapi.json is stale. Run the API tests with PULSAR_UPDATE_OPENAPI=true, then 'npm run api:generate'.");
    }

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
