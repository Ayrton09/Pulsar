using Microsoft.Extensions.Configuration;

namespace Pulsar.Api.Tests;

public sealed class PanelSettingsFileTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("pulsar-settings-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void TheFileComesAfterTheProgramsSettingsAndBeforeTheEnvironment()
    {
        var program = Write("appsettings.json", """{ "A": "program", "B": "program", "C": "program" }""");
        var file = Write("panel.json", """{ "B": "file", "C": "file" }""");
        var configuration = new ConfigurationManager();
        configuration.AddJsonFile(program, optional: false);
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["C"] = "environment" });

        PanelSettingsFile.Add(configuration, file);

        Assert.Equal("program", configuration["A"]);
        Assert.Equal("file", configuration["B"]);
        Assert.Equal("environment", configuration["C"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void NoFileAddsNothing(string? path)
    {
        var configuration = new ConfigurationManager();
        var before = configuration.Sources.Count;

        PanelSettingsFile.Add(configuration, path);

        Assert.Equal(before, configuration.Sources.Count);
    }

    [Fact]
    public void ARelativePathOrAMissingFileStopsThePanel()
    {
        var configuration = new ConfigurationManager();
        var before = configuration.Sources.Count;

        Assert.Throws<PanelStartupException>(() => PanelSettingsFile.Add(configuration, "panel.json"));
        Assert.Throws<PanelStartupException>(() => PanelSettingsFile.Add(configuration, Path.Combine(_folder, "missing.json")));
        Assert.Equal(before, configuration.Sources.Count);
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, text);
        return path;
    }
}
