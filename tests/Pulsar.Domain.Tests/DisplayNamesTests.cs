using Pulsar.Domain.Access;
using Pulsar.Domain.Common;
using Pulsar.Domain.Scheduling;
using Pulsar.Domain.Servers;

namespace Pulsar.Domain.Tests;

// Server, backup, schedule and API key names are shown as they are: nothing may hide in one or reorder it, so that one
// cannot be made to read like another.
public sealed class DisplayNamesTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0x200B)] // zero-width space
    [InlineData(0x202E)] // right-to-left override
    [InlineData(0x2066)] // left-to-right isolate
    [InlineData(0x061C)] // Arabic letter mark
    [InlineData(0xFEFF)] // zero-width no-break space
    [InlineData(0x2028)] // line separator
    [InlineData(0x2029)] // paragraph separator
    [InlineData(0x000A)] // line feed
    [InlineData(0x0085)] // next line
    [InlineData(0xE0001)] // language tag (a format character outside the BMP, a surrogate pair)
    [InlineData(0xE0041)] // tag Latin capital letter A
    public void NamesHaveNothingThatHidesOrBreaksThem(int character)
    {
        var name = "Retakes" + char.ConvertFromUtf32(character) + "01";

        Assert.False(DisplayNames.IsDisplayable(name));
        Assert.Equal(ServerErrors.NameInvalid, CreateServer(name).Error);
        Assert.Equal(ServerErrors.BackupNameInvalid, CreateBackup(name).Error);
        Assert.Equal(ScheduleErrors.NameInvalid, CreateSchedule(name).Error);
        var schedule = CreateSchedule("Nightly").Value;
        Assert.Equal(ScheduleErrors.NameInvalid,
            schedule.Change(name, "0 4 * * *", "UTC", ScheduledAction.Restart, ScheduledActionPayload.None, false, true, s_now).Error);
        Assert.Equal(AccessErrors.ApiKeyNameInvalid, ApiKey.Create(UserId.New(), name, null, null, s_now).Error);
    }

    [Theory]
    [InlineData("Servidor Córdoba 2")]
    [InlineData("Retakes \U0001F680")] // a symbol outside the BMP
    [InlineData("Family \U0001F468‍\U0001F469‍\U0001F467")] // an emoji sequence joined by zero-width joiners
    [InlineData("می‌خواهم")] // Persian: a zero-width non-joiner inside a word
    [InlineData("שרת 2‏")] // Hebrew with a right-to-left mark
    [InlineData("Server 1‎")] // a left-to-right mark
    public void NamesInEveryScriptAreAccepted(string name)
    {
        Assert.True(DisplayNames.IsDisplayable(name));
        Assert.True(CreateServer(name).IsSuccess);
        Assert.True(CreateBackup(name).IsSuccess);
        Assert.True(CreateSchedule(name).IsSuccess);
        Assert.True(ApiKey.Create(UserId.New(), name, null, null, s_now).IsSuccess);
    }

    private static Result<Server> CreateServer(string name) =>
        Server.Create(NodeId.New(), TemplateId.New(), name, null, Server.NewShortId(), s_now);

    private static Result<Backup> CreateBackup(string name)
    {
        var server = CreateServer("Server").Value;
        var operation = Operation.Create(OperationType.BackupCreate, server.NodeId, server.Id, null, OperationRequester.User, s_now);
        return Backup.Create(server, name, operation, null, OperationRequester.User, s_now);
    }

    private static Result<ScheduledTask> CreateSchedule(string name) =>
        ScheduledTask.Create(CreateServer("Server").Value, name, "0 4 * * *", "UTC", ScheduledAction.Restart, ScheduledActionPayload.None,
            false, true, null, s_now);
}
