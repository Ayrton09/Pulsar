using System.Text;
using Pulsar.Application.Servers;
using Pulsar.Domain.Common;
using Pulsar.Domain.Servers;
using Pulsar.Manifests;

namespace Pulsar.Api.Tests.Servers;

/// <summary>A value kept in clear for a variable that a newer template version makes secret is encrypted by the upgrade.</summary>
public sealed class SecretReprotectionTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AValueOfAVariableThatBecameSecretIsProtected()
    {
        var manifest = ManifestParser.Parse(Encoding.UTF8.GetBytes(await File.ReadAllTextAsync(GameServerTests.GamePath("css"), TestContext.Current.CancellationToken))).Manifest!;
        Assert.True(manifest.VariableList.Single(v => v.Key == "password").Secret);
        var server = Server.Create(NodeId.New(), TemplateId.New(), "Retakes", null, "abcd2345", s_now).Value!;
        server.SetVariable("password", "join-me", null, s_now); // stored in clear by the version where it was not secret
        server.SetVariable("map", "cs_office", null, s_now);
        var specs = new ServerSpecs(null!, new ReversingProtector());

        var set = specs.SetVariables(server, manifest, new Dictionary<string, string?>(), canEditProtected: true, null, s_now);

        Assert.True(set.IsSuccess, set.Error?.Message);
        var password = server.Variables.Single(v => v.Key == "password");
        Assert.True(password.IsSecret);
        Assert.Null(password.Value);
        Assert.Equal("join-me", specs.Values(server)["password"]);
        Assert.Equal("cs_office", server.Variables.Single(v => v.Key == "map").Value); // plain ones stay plain
    }

    private sealed class ReversingProtector : IVariableProtector
    {
        public byte[] Protect(string value) => Encoding.UTF8.GetBytes(new string([.. value.Reverse()]));

        public string Unprotect(byte[] protectedValue) => new([.. Encoding.UTF8.GetString(protectedValue).Reverse()]);
    }
}
