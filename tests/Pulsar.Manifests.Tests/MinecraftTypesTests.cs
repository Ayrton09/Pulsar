using System.Text.Json.Nodes;

namespace Pulsar.Manifests.Tests;

/// <summary>
/// Minecraft's server types (Pulsar 1.0.9): one manifest installs Mojang's server, Paper, Purpur, Fabric, Forge or
/// NeoForge, and starts each one the way its install leaves it.
/// </summary>
public sealed class MinecraftTypesTests
{
    [Theory]
    [InlineData("vanilla", "-jar server.jar")]
    [InlineData("paper", "-jar server.jar")]
    [InlineData("purpur", "-jar server.jar")]
    [InlineData("fabric", "-jar fabric-server-launch.jar")]
    [InlineData("forge", "@.pulsar/minecraft-args.txt")]
    [InlineData("neoforge", "@.pulsar/minecraft-args.txt")]
    public void EachTypeStartsAsItsInstallLeavesIt(string type, string start)
    {
        var plan = Render(TestManifests.Load("minecraft"), type);

        Assert.Equal(["-Xmx2048M", "-Dstdout.encoding=UTF-8", "-Dstderr.encoding=UTF-8", .. start.Split(' '), "--nogui"], plan.Arguments);
    }

    [Fact]
    public void MojangsServerIsTheDefaultAndTheStepReadsTheTypeAndTheLoader()
    {
        var manifest = TestManifests.Load("minecraft");

        Assert.Equal(["-Xmx2048M", "-Dstdout.encoding=UTF-8", "-Dstderr.encoding=UTF-8", "-jar", "server.jar", "--nogui"],
            Render(manifest, type: null).Arguments);
        var step = Assert.IsType<MinecraftServerStep>(Assert.Single(manifest.Install!.Steps));
        Assert.Equal(("version", "serverType", "loaderVersion"), (step.VersionVar, step.TypeVar, step.LoaderVar));
        Assert.Equal(step, Assert.Single(manifest.Update!.Steps));
        Assert.True(ManifestFeatures.UsesMinecraftTypes(manifest));
        Assert.Equal(MinecraftServerStep.Types.Order(StringComparer.Ordinal),
            manifest.VariableList.Single(v => v.Key == "serverType").Options!.Select(o => o.Value).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheTypeVariableOffersOnlyTypesPulsarInstalls()
    {
        var errors = TestManifests.Errors(TestManifests.Change("minecraft", manifest =>
            manifest["variables"]!.AsArray().Single(v => v!["key"]!.GetValue<string>() == "serverType")!["options"]!.AsArray()
                .Add(new JsonObject { ["value"] = "spigot", ["label"] = "Spigot", ["map"] = new JsonObject { ["jar"] = "spigot.jar", ["argfile"] = "" } })));

        Assert.Contains(errors, e => e.Path == "install.steps[0].typeVar");
        Assert.Contains(errors, e => e.Path == "update.steps[0].typeVar");
    }

    [Fact]
    public void AConditionOnWhatAnOptionMapsNeedsEveryOptionToMapIt()
    {
        var errors = TestManifests.Errors(TestManifests.Change("minecraft", manifest =>
            manifest["variables"]!.AsArray().Single(v => v!["key"]!.GetValue<string>() == "serverType")!["options"]![0]!["map"]!.AsObject()
                .Remove("argfile")));

        Assert.Contains(errors, e => e.Path.EndsWith(".if", StringComparison.Ordinal));
    }

    [Fact]
    public void AManifestWithoutTypesNeedsNoNewAgent()
    {
        var manifest = TestManifests.Parse(TestManifests.Change("minecraft", json =>
        {
            foreach (var steps in new[] { json["install"]!["steps"]!, json["update"]!["steps"]! })
            {
                steps[0]!.AsObject().Remove("typeVar");
                steps[0]!.AsObject().Remove("loaderVar");
            }

            json["runtime"]!["windows-x64"]!["arguments"] = new JsonArray("-Xmx{{var.memoryMb}}M", "-jar", "server.jar", "--nogui");
        }));

        Assert.False(ManifestFeatures.UsesMinecraftTypes(manifest));
    }

    private static LaunchPlan Render(GameManifest manifest, string? type)
    {
        var values = new Dictionary<string, string?> { ["eula"] = "accepted" };
        if (type is not null)
        {
            values["serverType"] = type;
        }

        var ports = manifest.PortList.ToDictionary(p => p.Name, p => new PortAssignment(PortAssignment.AnyAddress, p.Default), StringComparer.Ordinal);
        return LaunchPlanRenderer.Render(manifest, ManifestValidator.WindowsX64,
            new ServerBinding("abcd1234", values, ports, Directory: @"D:\Pulsar\servers\abcd1234"));
    }
}
