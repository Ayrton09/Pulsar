using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
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
        builder.Property(t => t.Payload).HasColumnName("action_payload").HasColumnType("jsonb").HasConversion(new PayloadConverter(), PayloadConverter.Comparer);
        // The scheduler (taking a run) and users (editing) can race: the loser reads the task again.
        builder.Property<uint>("Version").IsRowVersion();
        builder.HasIndex(t => t.NextRunAt).HasFilter("is_enabled").HasDatabaseName("ix_scheduled_tasks_due");
        builder.HasIndex(t => t.ServerId);

        builder.HasOne<Server>().WithMany().HasForeignKey(t => t.ServerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<PulsarUser>().WithMany().HasForeignKey(t => t.CreatedBy).OnDelete(DeleteBehavior.SetNull);
    }

    /// <summary>
    /// Only what the action uses: <c>{"command":"say hi"}</c>, <c>{"validate":true}</c>,
    /// <c>{"steps":[{"kind":"broadcast","text":"Restarting"},{"kind":"restart"}]}</c> or <c>{}</c>.
    /// </summary>
    private sealed class PayloadConverter() : ValueConverter<ScheduledActionPayload, string>(
        payload => JsonSerializer.Serialize(payload, s_json),
        json => JsonSerializer.Deserialize<ScheduledActionPayload>(json, s_json) ?? ScheduledActionPayload.None)
    {
        private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        };

        /// <summary>Payloads are equal when they store the same (records compare their steps' list by reference).</summary>
        public static ValueComparer<ScheduledActionPayload> Comparer { get; } = new(
            (a, b) => JsonSerializer.Serialize(a, s_json) == JsonSerializer.Serialize(b, s_json),
            payload => JsonSerializer.Serialize(payload, s_json).GetHashCode(StringComparison.Ordinal),
            payload => payload);
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
        // Steps kept no longer leave their run pointing at nothing; one still running then ends as interrupted.
        builder.HasOne<ServerSequence>().WithMany().HasForeignKey(r => r.SequenceId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class ServerSequenceConfiguration : IEntityTypeConfiguration<ServerSequence>
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public void Configure(EntityTypeBuilder<ServerSequence> builder)
    {
        builder.ToTable("server_sequences", t =>
        {
            t.HasCheckConstraint("ck_server_sequences_status", SnakeCaseEnum<SequenceStatus>.CheckConstraint("status"));
            t.HasCheckConstraint("ck_server_sequences_origin", SnakeCaseEnum<SequenceOrigin>.CheckConstraint("origin"));
        });

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.Origin).HasConversion<SnakeCaseEnumConverter<SequenceOrigin>>();
        builder.Property(s => s.Status).HasConversion<SnakeCaseEnumConverter<SequenceStatus>>();
        builder.Property(s => s.Name).HasMaxLength(ServerSequence.MaxNameLength);
        builder.Property(s => s.ErrorCode).HasMaxLength(ServerSequence.MaxErrorCodeLength);
        builder.Property(s => s.Message).HasMaxLength(ServerSequence.MaxMessageLength);
        builder.Property(s => s.Steps).HasColumnType("jsonb").HasConversion(
            steps => JsonSerializer.Serialize(steps, s_json),
            json => JsonSerializer.Deserialize<List<SequenceStep>>(json, s_json) ?? new List<SequenceStep>(),
            new ValueComparer<IReadOnlyList<SequenceStep>>((a, b) => a!.SequenceEqual(b!), steps => steps.Aggregate(0, (hash, step) => HashCode.Combine(hash, step)),
                steps => steps.ToList()));
        // The runner (moving it on) and users (cancelling it) can race: the loser reads it again.
        builder.Property<uint>("Version").IsRowVersion();
        builder.HasIndex(s => s.WakeAt).HasFilter("status = 'running'").HasDatabaseName("ix_server_sequences_due");
        // At most one runs on a server (the start checks it with the server's row locked; this keeps it so).
        builder.HasIndex(s => s.ServerId).IsUnique().HasFilter("status = 'running'").HasDatabaseName("ix_server_sequences_running");
        builder.HasIndex(s => new { s.ServerId, s.CreatedAt });

        builder.HasOne<Server>().WithMany().HasForeignKey(s => s.ServerId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Operation>().WithMany().HasForeignKey(s => s.OperationId).OnDelete(DeleteBehavior.SetNull);
    }
}

