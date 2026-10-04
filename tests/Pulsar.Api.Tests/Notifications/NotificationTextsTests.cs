using Pulsar.Application.Notifications;

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
