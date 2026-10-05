using System.Collections.Frozen;
using System.Text;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pulsar.Domain.Common;

namespace Pulsar.Infrastructure.Persistence;

internal sealed class UserIdConverter() : ValueConverter<UserId, Guid>(id => id.Value, value => new UserId(value));

internal sealed class RoleIdConverter() : ValueConverter<RoleId, Guid>(id => id.Value, value => new RoleId(value));

internal sealed class NodeIdConverter() : ValueConverter<NodeId, Guid>(id => id.Value, value => new NodeId(value));

internal sealed class ServerIdConverter() : ValueConverter<ServerId, Guid>(id => id.Value, value => new ServerId(value));

internal sealed class TemplateIdConverter() : ValueConverter<TemplateId, Guid>(id => id.Value, value => new TemplateId(value));

internal sealed class OperationIdConverter() : ValueConverter<OperationId, Guid>(id => id.Value, value => new OperationId(value));

internal sealed class AllocationIdConverter() : ValueConverter<AllocationId, Guid>(id => id.Value, value => new AllocationId(value));

internal sealed class BackupIdConverter() : ValueConverter<BackupId, Guid>(id => id.Value, value => new BackupId(value));

internal sealed class ScheduledTaskIdConverter() : ValueConverter<ScheduledTaskId, Guid>(id => id.Value, value => new ScheduledTaskId(value));

internal sealed class ScheduledRunIdConverter() : ValueConverter<ScheduledRunId, Guid>(id => id.Value, value => new ScheduledRunId(value));

internal sealed class ApiKeyIdConverter() : ValueConverter<ApiKeyId, Guid>(id => id.Value, value => new ApiKeyId(value));

internal sealed class BackupStorageIdConverter() : ValueConverter<BackupStorageId, Guid>(id => id.Value, value => new BackupStorageId(value));

internal sealed class NotificationChannelIdConverter() : ValueConverter<NotificationChannelId, Guid>(id => id.Value, value => new NotificationChannelId(value));

internal sealed class NotificationDeliveryIdConverter() : ValueConverter<NotificationDeliveryId, Guid>(id => id.Value, value => new NotificationDeliveryId(value));

/// <summary>Stores enums as snake_case text (<c>PendingEnrollment</c> → <c>pending_enrollment</c>), matched by a CHECK constraint.</summary>
internal sealed class SnakeCaseEnumConverter<TEnum>() : ValueConverter<TEnum, string>(
    value => SnakeCaseEnum<TEnum>.ToText(value),
    text => SnakeCaseEnum<TEnum>.Parse(text))
    where TEnum : struct, Enum;

internal static class SnakeCaseEnum<TEnum>
    where TEnum : struct, Enum
{
    private static readonly FrozenDictionary<TEnum, string> s_toText =
        Enum.GetValues<TEnum>().ToFrozenDictionary(v => v, v => ToSnakeCase(v.ToString()));

    private static readonly FrozenDictionary<string, TEnum> s_fromText =
        s_toText.ToFrozenDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);

    public static string ToText(TEnum value) => s_toText[value];

    public static TEnum Parse(string text) =>
        s_fromText.TryGetValue(text, out var value)
            ? value
            : throw new InvalidOperationException($"'{text}' is not a valid {typeof(TEnum).Name} value in the database.");

    /// <summary>SQL CHECK expression allowing exactly the enum's values.</summary>
    public static string CheckConstraint(string column) =>
        $"{column} IN ({string.Join(", ", s_toText.Values.Order(StringComparer.Ordinal).Select(v => $"'{v}'"))})";

    private static string ToSnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(name[i]));
        }

        return builder.ToString();
    }
}
