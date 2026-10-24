using Pulsar.Application.Notifications;
using Pulsar.Domain.Notifications;

namespace Pulsar.Api.Tests.Notifications;

public sealed class NotificationTextsTests
{
    [Fact]
    public void SizesUseTheLanguagesDecimalSeparator()
    {
        var drives = new[] { (@"C:\", 478_000_000_000L, 999_000_000_000L) };

        var english = NotificationTexts.NodeDiskLow("en", "node-1", drives, null);
        var spanish = NotificationTexts.NodeDiskLow("es", "node-1", drives, null);

        Assert.Equal("445.2 GB free of 930.4 GB (47.8 %)", english.Fields[1].Value);
        Assert.Equal("445,2 GB libres de 930,4 GB (47,8 %)", spanish.Fields[1].Value);
        Assert.Equal("Al nodo node-1 le queda poco espacio en disco", spanish.Title);
        Assert.Equal("512 B", NotificationTexts.Size(512));
    }

    [Fact]
    public void EveryLanguageHasEveryTextWithTheSamePlaceholders()
    {
        var english = NotificationTexts.Texts["en"];
        Assert.Equal(NotificationChannel.Languages.Order(StringComparer.Ordinal), NotificationTexts.Texts.Keys.Order(StringComparer.Ordinal));
        foreach (var (language, texts) in NotificationTexts.Texts)
        {
            Assert.Equal(english.Keys.Order(StringComparer.Ordinal), texts.Keys.Order(StringComparer.Ordinal));
            foreach (var (key, text) in english)
            {
                Assert.True(Placeholders(text) == Placeholders(texts[key]), $"{language} {key}: {texts[key]}");
                Assert.False(string.IsNullOrWhiteSpace(texts[key]), $"{language} {key} is empty");
            }
        }

        static string Placeholders(string text) =>
            string.Join(',', System.Text.RegularExpressions.Regex.Matches(text, @"\{(\w+)\}").Select(m => m.Value).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("de", "445,2 GB von 930,4 GB frei (47,8 %)", "Retakes ist abgestürzt")]
    [InlineData("pt", "445,2 GB livres de 930,4 GB (47,8 %)", "Retakes travou")]
    [InlineData("ja", "930.4 GB 中 445.2 GB 空き（47.8 %）", "Retakes がクラッシュしました")]
    [InlineData("zh", "共 930.4 GB，可用 445.2 GB（47.8 %）", "Retakes 已崩溃")]
    public void AMessageIsInTheChannelsLanguageWithItsNumbers(string language, string drive, string crashed)
    {
        var facts = new ServerFacts(Guid.NewGuid(), "Retakes", "Counter-Strike 2", "node-1");

        Assert.Equal(drive, NotificationTexts.NodeDiskLow(language, "node-1", [(@"C:\", 478_000_000_000L, 999_000_000_000L)], null).Fields[1].Value);
        Assert.Equal(crashed, NotificationTexts.ServerCrashed(language, facts, 1, crashLoop: false, null).Title);
    }

    [Fact]
    public void AValueIsNeverReadAsAPlaceholder()
    {
        // A server named like a placeholder stays its name.
        var facts = new ServerFacts(Guid.NewGuid(), "{task}", "Counter-Strike 2", "node-1");

        Assert.Equal("Scheduled task “nightly” of {task} failed", NotificationTexts.ScheduledTaskFailed("en", facts, "nightly", null, null).Title);
    }

    [Fact]
    public void AClippedDetailNeverEndsInHalfAnEmoji()
    {
        var facts = new ServerFacts(Guid.NewGuid(), "Retakes", "Counter-Strike 2", "node-1");

        var message = NotificationTexts.BackupFailed("en", facts, "nightly", new string('a', 299) + "\U0001F600 the rest", remoteCopy: false, link: null);

        var error = message.Fields.Single(f => f.Name == "Error").Value;
        Assert.Equal(new string('a', 299) + "…", error);
    }

    [Fact]
    public void ACrashLoopSaysTheServerWasLeftStopped()
    {
        var facts = new ServerFacts(Guid.NewGuid(), "Retakes", "Counter-Strike 2", "node-1");

        var message = NotificationTexts.ServerCrashed("en", facts, 1, crashLoop: true, "https://panel.example.com/servers/1");

        Assert.Equal("Retakes keeps crashing", message.Title);
        Assert.Contains("it is stopped", message.Text, StringComparison.Ordinal);
        Assert.Equal(["Server", "Game", "Node", "Exit code"], message.Fields.Select(f => f.Name));
        Assert.Equal("https://panel.example.com/servers/1", message.Link);
    }
}
