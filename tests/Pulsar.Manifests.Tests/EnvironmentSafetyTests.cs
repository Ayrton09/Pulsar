using System.Text.Json.Nodes;

namespace Pulsar.Manifests.Tests;

/// <summary>Settings editors choose values, never what loads code into the game's process.</summary>
public sealed class EnvironmentSafetyTests
{
    [Theory]
    [InlineData("DOTNET_STARTUP_HOOKS")]
    [InlineData("dotnet_startup_hooks")]
    [InlineData("DOTNET_gcServer")]
    [InlineData("COR_ENABLE_PROFILING")]
    [InlineData("COR_PROFILER_PATH")]
    [InlineData("CORECLR_ENABLE_PROFILING")]
    [InlineData("CORECLR_PROFILER_PATH_64")]
    public void EnvironmentNamesThatLoadCodeIntoTheRuntimeAreRefused(string name)
    {
        var errors = TestManifests.Errors(TestManifests.Change("_test", m => m["runtime"]!["windows-x64"]!["environment"]![name] = "1"));

        Assert.Contains(errors, e => e.Path == $"runtime.windows-x64.environment.{name}");
    }

    [Fact]
    public void AStringVariableInTheEnvironmentNeedsAPatternLikeAnywhereElse()
    {
        var errors = TestManifests.Errors(TestManifests.Change("_test", m =>
        {
            m["variables"]!.AsArray().Add(new JsonObject { ["key"] = "extra", ["label"] = "Extra", ["type"] = "string", ["default"] = "x", ["maxLength"] = 32 });
            m["runtime"]!["windows-x64"]!["environment"]!["PULSAR_TEST_EXTRA"] = "{{var.extra}}";
        }));

        Assert.Contains(errors, e => e.Path == "runtime.windows-x64.environment.PULSAR_TEST_EXTRA" && e.Message.Contains("pattern", StringComparison.Ordinal));
    }

    [Fact]
    public void OtherEnvironmentNamesAreAccepted()
    {
        var manifest = TestManifests.Parse(TestManifests.Change("_test", m => m["runtime"]!["windows-x64"]!["environment"]!["SteamAppId2"] = "1"));
        Assert.NotNull(manifest);
    }
}
