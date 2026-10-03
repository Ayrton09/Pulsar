using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulsar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Scheduling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "scheduled_tasks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    cron_expression = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    time_zone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    action_type = table.Column<string>(type: "text", nullable: false),
                    action_payload = table.Column<string>(type: "jsonb", nullable: false),
                    only_if_running = table.Column<bool>(type: "boolean", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    next_run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_run_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scheduled_tasks", x => x.id);
                    table.CheckConstraint("ck_scheduled_tasks_action_type", "action_type IN ('backup', 'console_command', 'restart', 'start', 'stop', 'update')");
                    table.ForeignKey(
                        name: "fk_scheduled_tasks_servers_server_id",
                        column: x => x.server_id,
                        principalTable: "servers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_scheduled_tasks_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "scheduled_task_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    task_id = table.Column<Guid>(type: "uuid", nullable: false),
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scheduled_for = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    occurrences = table.Column<int>(type: "integer", nullable: false),
                    error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    message = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_scheduled_task_runs", x => x.id);
                    table.CheckConstraint("ck_scheduled_task_runs_occurrences", "occurrences >= 1");
                    table.CheckConstraint("ck_scheduled_task_runs_status", "status IN ('failed', 'running', 'skipped', 'succeeded')");
                    table.ForeignKey(
                        name: "fk_scheduled_task_runs_operations_operation_id",
                        column: x => x.operation_id,
                        principalTable: "operations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_scheduled_task_runs_scheduled_tasks_task_id",
                        column: x => x.task_id,
                        principalTable: "scheduled_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_scheduled_task_runs_operation_id",
                table: "scheduled_task_runs",
                column: "operation_id");

            migrationBuilder.CreateIndex(
                name: "ix_task_runs_running",
                table: "scheduled_task_runs",
                column: "started_at",
                filter: "status = 'running'");

            migrationBuilder.CreateIndex(
                name: "ix_task_runs_task",
                table: "scheduled_task_runs",
                columns: new[] { "task_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_scheduled_tasks_created_by",
                table: "scheduled_tasks",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_scheduled_tasks_due",
                table: "scheduled_tasks",
                column: "next_run_at",
                filter: "is_enabled");

            migrationBuilder.CreateIndex(
                name: "ix_scheduled_tasks_server_id",
                table: "scheduled_tasks",
                column: "server_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "scheduled_task_runs");

            migrationBuilder.DropTable(
                name: "scheduled_tasks");
        }
    }
}
