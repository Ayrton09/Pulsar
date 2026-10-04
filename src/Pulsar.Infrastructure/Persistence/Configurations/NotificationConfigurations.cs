using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pulsar.Domain.Notifications;

namespace Pulsar.Infrastructure.Persistence.Configurations;

internal sealed class NotificationChannelConfiguration : IEntityTypeConfiguration<NotificationChannel>
{
    public void Configure(EntityTypeBuilder<NotificationChannel> builder)
    {
        builder.ToTable("notification_channels", t =>
        {
            t.HasCheckConstraint("ck_notification_channels_kind", SnakeCaseEnum<NotificationKind>.CheckConstraint("kind"));
            // A Discord channel has its webhook; an email channel has recipients.
            t.HasCheckConstraint("ck_notification_channels_target",
                "(kind = 'discord' AND webhook_url_protected IS NOT NULL) OR (kind = 'email' AND cardinality(recipients) > 0)");
        });

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Name).HasMaxLength(NotificationChannel.MaxNameLength);
        builder.Property(c => c.Kind).HasConversion<SnakeCaseEnumConverter<NotificationKind>>();
        builder.Property(c => c.Language).HasMaxLength(8);
        builder.PrimitiveCollection(c => c.Events).ElementType().HasConversion<SnakeCaseEnumConverter<NotificationEvent>>();
        builder.PrimitiveCollection(c => c.ServerIds);
        builder.PrimitiveCollection(c => c.Recipients);
        builder.Property<uint>("Version").IsRowVersion();
    }
}

internal sealed class NotificationSettingsConfiguration : IEntityTypeConfiguration<NotificationSettings>
{
    public void Configure(EntityTypeBuilder<NotificationSettings> builder)
    {
        builder.ToTable("notification_settings", t =>
        {
            t.HasCheckConstraint("ck_notification_settings_singleton", $"id = {NotificationSettings.SingletonId}");
            t.HasCheckConstraint("ck_notification_settings_smtp_security", SnakeCaseEnum<SmtpSecurity>.CheckConstraint("smtp_security"));
        });

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.PanelUrl).HasMaxLength(NotificationSettings.MaxUrlLength);
        builder.Property(s => s.SmtpHost).HasMaxLength(NotificationSettings.MaxHostLength);
        builder.Property(s => s.SmtpSecurity).HasConversion<SnakeCaseEnumConverter<SmtpSecurity>>();
        builder.Property(s => s.SmtpUserName).HasMaxLength(NotificationSettings.MaxUserNameLength);
        builder.Property(s => s.FromAddress).HasMaxLength(Domain.Notifications.NotificationChannel.MaxEmailLength);
        builder.Property(s => s.FromName).HasMaxLength(NotificationSettings.MaxFromNameLength);
        builder.Property<uint>("Version").IsRowVersion();
    }
}

internal sealed class NotificationDeliveryConfiguration : IEntityTypeConfiguration<NotificationDelivery>
{
    public void Configure(EntityTypeBuilder<NotificationDelivery> builder)
    {
        builder.ToTable("notification_deliveries", t =>
        {
            t.HasCheckConstraint("ck_notification_deliveries_status", SnakeCaseEnum<DeliveryStatus>.CheckConstraint("status"));
            t.HasCheckConstraint("ck_notification_deliveries_event", SnakeCaseEnum<NotificationEvent>.CheckConstraint("event"));
        });

        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();
        builder.Property(d => d.Event).HasConversion<SnakeCaseEnumConverter<NotificationEvent>>();
        builder.Property(d => d.Status).HasConversion<SnakeCaseEnumConverter<DeliveryStatus>>();
        builder.Property(d => d.Subject).HasMaxLength(NotificationDelivery.MaxSubjectLength);
        builder.Property(d => d.LastError).HasMaxLength(NotificationDelivery.MaxErrorLength);
        builder.Property(d => d.Message).HasColumnType("jsonb").HasConversion<MessageConverter>();
        builder.Property<uint>("Version").IsRowVersion();
        // The sender's query: pending deliveries due now.
        builder.HasIndex(d => d.NextAttemptAt).HasFilter("status = 'pending'").HasDatabaseName("ix_notification_deliveries_due");
        // Repeats of the same event about the same subject, and a channel's history.
        builder.HasIndex(d => new { d.ChannelId, d.Event, d.Subject, d.CreatedAt }).HasDatabaseName("ix_notification_deliveries_subject");
        builder.HasIndex(d => d.CreatedAt);

        builder.HasOne<NotificationChannel>().WithMany().HasForeignKey(d => d.ChannelId).OnDelete(DeleteBehavior.Cascade);
    }

    private sealed class MessageConverter() : ValueConverter<NotificationMessage, string>(
        message => JsonSerializer.Serialize(message, s_json),
        json => JsonSerializer.Deserialize<NotificationMessage>(json, s_json)!)
    {
        private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        };
    }
}
