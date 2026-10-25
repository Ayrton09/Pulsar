using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulsar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScheduledSteps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_scheduled_tasks_action_type",
                table: "scheduled_tasks");

            migrationBuilder.AddColumn<bool>(
                name: "only_if_empty",
                table: "scheduled_tasks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "only_if_update_available",
                table: "scheduled_tasks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "sequence_id",
                table: "scheduled_task_runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_scheduled_tasks_action_type",
                table: "scheduled_tasks",
                sql: "action_type IN ('backup', 'console_command', 'restart', 'sequence', 'start', 'stop', 'update')");

            migrationBuilder.CreateIndex(
                name: "ix_scheduled_task_runs_sequence_id",
                table: "scheduled_task_runs",
                column: "sequence_id");

            migrationBuilder.AddForeignKey(
                name: "fk_scheduled_task_runs_server_sequences_sequence_id",
                table: "scheduled_task_runs",
                column: "sequence_id",
                principalTable: "server_sequences",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_scheduled_task_runs_server_sequences_sequence_id",
                table: "scheduled_task_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_scheduled_tasks_action_type",
                table: "scheduled_tasks");

            migrationBuilder.DropIndex(
                name: "ix_scheduled_task_runs_sequence_id",
                table: "scheduled_task_runs");

            migrationBuilder.DropColumn(
                name: "only_if_empty",
                table: "scheduled_tasks");

            migrationBuilder.DropColumn(
                name: "only_if_update_available",
                table: "scheduled_tasks");

            migrationBuilder.DropColumn(
                name: "sequence_id",
                table: "scheduled_task_runs");

            migrationBuilder.AddCheckConstraint(
                name: "ck_scheduled_tasks_action_type",
                table: "scheduled_tasks",
                sql: "action_type IN ('backup', 'console_command', 'restart', 'start', 'stop', 'update')");
        }
    }
}
