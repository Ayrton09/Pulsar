using Pulsar.Agent.Core.Processes;
using Pulsar.Agent.Core.Steam;

namespace Pulsar.Agent.Core.Tests;

public sealed class SteamBuildsTests : IDisposable
{
    /// <summary>What SteamCMD prints for <c>+app_info_print 896660</c>, after its own lines (trimmed).</summary>
    internal const string AppInfo = """
        Redirecting stderr to 'C:\servers\abcd2345\.pulsar\steamcmd\logs\stderr.txt'
        Logging directory: 'C:\servers\abcd2345\.pulsar\steamcmd/logs'
        Connecting anonymously to Steam Public...OK
        Waiting for client config...OK
        Waiting for user info...OK
        AppID : 896660, change number : 26513842/0, last change : Tue Oct 21 10:05:53 2026
        "896660"
        {
        	"common"
        	{
        		"name"		"Valheim Dedicated Server"
        		"type"		"Tool"
        	}
        	"depots"
        	{
        		"896661"
        		{
        			"config"
        			{
        				"oslist"		"windows"
        			}
        			"manifests"
        			{
        				"public"
        				{
        					"gid"		"5134950413296475153"
        					"size"		"1838829367"
        				}
        			}
        		}
        		"branches"
        		{
        			"public"
        			{
        				"buildid"		"20476133"
        				"timeupdated"		"1760000000"
        			}
        			"public-test"
        			{
        				"buildid"		"20500001"
        				"description"		"Public \"test\" branch"
        				"pwdrequired"		"1"
        			}
        		}
        	}
        }
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pulsar-steam-builds", Guid.NewGuid().ToString("N"));

    public SteamBuildsTests() => Directory.CreateDirectory(Path.Combine(_root, "steamapps"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void TheLatestBuildOfABranchIsReadFromWhatSteamCmdPrinted()
    {
        Assert.Equal("20476133", SteamBuilds.Latest(AppInfo, 896660, SteamBuilds.PublicBranch));
        Assert.Equal("20500001", SteamBuilds.Latest(AppInfo, 896660, "Public-Test"));
        Assert.Null(SteamBuilds.Latest(AppInfo, 896660, "experimental"));
        // Another app's info, or none at all.
        Assert.Null(SteamBuilds.Latest(AppInfo, 896661, SteamBuilds.PublicBranch));
        Assert.Null(SteamBuilds.Latest("Connecting anonymously to Steam Public...OK\nERROR! Timed out waiting for AppInfo update.", 896660,
            SteamBuilds.PublicBranch));
    }

    [Fact]
    public void TheAppsBlockMustStartALineAndBeComplete()
    {
        // Quoted inside a line ("Steam: "896660" {") it is not the app's info; cut off, it is not read either.
        Assert.Null(SteamBuilds.Latest("""say "896660" { "depots" { "branches" { "public" { "buildid" "1" } } } }""", 896660, "public"));
        Assert.Null(SteamBuilds.Latest(AppInfo[..AppInfo.IndexOf("\"timeupdated\"", StringComparison.Ordinal)], 896660, "public"));
        Assert.Equal("7", SteamBuilds.Latest("""
            "896660" { "depots" { "branches" { "public" { "buildid" "7" } } } }
            """, 896660, "public"));
    }

    [Theory]
    [InlineData("12a45")]
    [InlineData("")]
    [InlineData("123456789012345678901")]
    [InlineData("-1")]
    public void ABuildIdIsANumber(string build)
    {
        Assert.Null(SteamBuilds.Latest($$"""
            "896660" { "depots" { "branches" { "public" { "buildid" "{{build}}" } } } }
            """, 896660, "public"));
    }

    [Fact]
    public void TheInstalledBuildComesFromTheAppManifestSteamCmdKeeps()
    {
        File.WriteAllText(Path.Combine(_root, "steamapps", "appmanifest_896660.acf"), """
            "AppState"
            {
            	"appid"		"896660"
            	"Universe"		"1"
            	"name"		"Valheim Dedicated Server"
            	"StateFlags"		"4"
            	"buildid"		"20476133"
            	"UserConfig"
            	{
            		"BetaKey"		"public-test"
            	}
            }
            """);

        Assert.Equal("20476133", SteamBuilds.Installed(new DirectServerFiles(), _root, 896660));
        Assert.Null(SteamBuilds.Installed(new DirectServerFiles(), _root, 730));
    }

    [Fact]
    public void AnAppManifestThatIsNotSteamCmdsNamesNoBuild()
    {
        var path = Path.Combine(_root, "steamapps", "appmanifest_896660.acf");
        File.WriteAllText(path, "\"AppState\" { \"buildid\" \"1\" ");
        Assert.Null(SteamBuilds.Installed(new DirectServerFiles(), _root, 896660));

        File.WriteAllText(path, "\"AppState\" { \"buildid\" \"1\" }" + new string(' ', 1024 * 1024));
        Assert.Null(SteamBuilds.Installed(new DirectServerFiles(), _root, 896660));
    }

    [Fact]
    public void KeyValuesAreReadWithinBounds()
    {
        var deep = string.Concat(Enumerable.Repeat("\"k\" { ", KeyValues.MaxDepth + 2)) + string.Concat(Enumerable.Repeat("} ", KeyValues.MaxDepth + 2));
        Assert.Null(KeyValues.Parse(deep));

        var parsed = KeyValues.Parse("""
            // a comment
            "a" "1" [$WIN32]
            b { "c" "x\\y\ttab" }
            """);
        Assert.NotNull(parsed);
        Assert.Equal("1", parsed["A"]?.Value);
        Assert.Equal("x\\y\ttab", parsed["b"]?["c"]?.Value);
        Assert.Null(KeyValues.Parse("\"a\" }"));
        Assert.Null(KeyValues.Parse("{ \"a\" \"1\" }"));
    }
}
