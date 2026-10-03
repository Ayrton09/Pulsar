using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Pulsar.Domain.Scheduling;
using Pulsar.Domain.Servers;
using Pulsar.Infrastructure.Identity;

namespace Pulsar.Infrastructure.Persistence.Configurations;

internal sealed class ScheduledTaskConfiguration : IEntityTypeConfiguration<ScheduledTask>
{
    public void Configure(EntityTypeBuilder<ScheduledTask> builder)
    {
        builder.ToTable("scheduled_tasks", t => t.HasCheckConstraint("ck_scheduled_tasks_action_type",
            SnakeCaseEnum<ScheduledAction>.CheckConstraint("action_type")));

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.Name).HasMaxLength(ScheduledTask.MaxNameLength);
        builder.Property(t => t.CronExpression).HasMaxLength(ScheduledTask.MaxCronLength);
        builder.Property(t => t.TimeZone).HasMaxLength(ScheduledTask.MaxTimeZoneLength);
        builder.Property(t => t.Action).HasColumnName("action_type").HasConversion<SnakeCaseEnumConverter<ScheduledAction>>();
        builder.Property(t => t.Payload).HasColumnName("action_payload").HasColumnType("jsonb").HasConversion<PayloadConverter>();
        // The scheduler (taking a run) and users (editing) can race: the loser reads the task again.
        builder.Property<uint>("Version").IsRowVersion();
        builder.HasIndex(t => t.NextRunAt).HasFilter("is_enabled").HasDatabaseName("ix_scheduled_tasks_due");
        builder.HasIndex(t => t.ServerId);

        builder.HasOne<Server>().WithMany().HasForeignKey(t => t.ServerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<PulsarUser>().WithMany().HasForeignKey(t => t.CreatedBy).OnDelete(DeleteBehavior.SetNull);
    }

    /// <summary>Only what the action uses: <c>{"command":"say hi"}</c>, <c>{"validate":true}</c> or <c>{}</c>.</summary>
    private sealed class PayloadConverter() : ValueConverter<ScheduledActionPayload, string>(
        payload => JsonSerializer.Serialize(payload, s_json),
        json => JsonSerializer.Deserialize<ScheduledActionPayload>(json, s_json) ?? ScheduledActionPayload.None)
    {
        private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        };
    }
}

internal sealed class ScheduledTaskRunConfiguration : IEntityTypeConfiguration<ScheduledTaskRun>
{
    public void Configure(EntityTypeBuilder<ScheduledTaskRun> builder)
    {
        builder.ToTable("scheduled_task_runs", t =>
        {
            t.HasCheckConstraint("ck_scheduled_task_runs_status", SnakeCaseEnum<ScheduledRunStatus>.CheckConstraint("status"));
            t.HasCheckConstraint("ck_scheduled_task_runs_occurrences", "occurrences >= 1");
        });

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Status).HasConversion<SnakeCaseEnumConverter<ScheduledRunStatus>>();
        builder.Property(r => r.ErrorCode).HasMaxLength(ScheduledTaskRun.MaxErrorCodeLength);
        builder.Property(r => r.Message).HasMaxLength(ScheduledTaskRun.MaxMessageLength);
        builder.HasIndex(r => new { r.TaskId, r.StartedAt }).HasDatabaseName("ix_task_runs_task");
        // Runs the panel stopped in the middle of are found here.
        builder.HasIndex(r => r.StartedAt).HasFilter("status = 'running'").HasDatabaseName("ix_task_runs_running");

        builder.HasOne<ScheduledTask>().WithMany().HasForeignKey(r => r.TaskId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Operation>().WithMany().HasForeignKey(r => r.OperationId).OnDelete(DeleteBehavior.SetNull);
    }
}
