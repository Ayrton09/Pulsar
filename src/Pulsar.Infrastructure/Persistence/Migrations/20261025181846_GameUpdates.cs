using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulsar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GameUpdates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_notification_deliveries_event",
                table: "notification_deliveries");

            migrationBuilder.CreateTable(
                name: "server_sequences",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    origin = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    steps = table.Column<string>(type: "jsonb", nullable: false),
                    step_index = table.Column<int>(type: "integer", nullable: false),
                    step_begun = table.Column<bool>(type: "boolean", nullable: false),
                    step_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    wake_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    observed_state_sequence = table.Column<long>(type: "bigint", nullable: true),
                    observed_state_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    was_running = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    message = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_server_sequences", x => x.id);
                    table.CheckConstraint("ck_server_sequences_origin", "origin IN ('game_update', 'schedule')");
                    table.CheckConstraint("ck_server_sequences_status", "status IN ('cancelled', 'failed', 'running', 'succeeded')");
                    table.ForeignKey(
                        name: "fk_server_sequences_operations_operation_id",
                        column: x => x.operation_id,
                        principalTable: "operations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_server_sequences_servers_server_id",
                        column: x => x.server_id,
                        principalTable: "servers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "game_update_watches",
                columns: table => new
                {
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy = table.Column<string>(type: "text", nullable: false),
                    when_empty = table.Column<bool>(type: "boolean", nullable: false),
                    warning_minutes = table.Column<int>(type: "integer", nullable: false),
                    warning_message = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    installed = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    latest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    available = table.Column<bool>(type: "boolean", nullable: true),
                    checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    check_error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    check_error = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    check_requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_check_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    notified_latest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    notified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempted_latest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    sequence_id = table.Column<Guid>(type: "uuid", nullable: true),
                    auto_updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_game_update_watches", x => x.server_id);
                    table.CheckConstraint("ck_game_update_watches_policy", "policy IN ('install', 'notify', 'off')");
                    table.CheckConstraint("ck_game_update_watches_warning_minutes", "warning_minutes BETWEEN 0 AND 60");
                    table.ForeignKey(
                        name: "fk_game_update_watches_server_sequences_sequence_id",
                        column: x => x.sequence_id,
                        principalTable: "server_sequences",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_game_update_watches_servers_server_id",
                        column: x => x.server_id,
                        principalTable: "servers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_notification_deliveries_event",
                table: "notification_deliveries",
                sql: "event IN ('backup_failed', 'game_update_available', 'game_update_failed', 'node_disk_low', 'node_offline', 'public_ip_certificate', 'scheduled_task_failed', 'server_crashed', 'server_start_failed')");

            migrationBuilder.CreateIndex(
                name: "ix_game_update_watches_due",
                table: "game_update_watches",
                column: "next_check_at",
                filter: "policy <> 'off'");

            migrationBuilder.CreateIndex(
                name: "ix_game_update_watches_sequence_id",
                table: "game_update_watches",
                column: "sequence_id");

            migrationBuilder.CreateIndex(
                name: "ix_server_sequences_due",
                table: "server_sequences",
                column: "wake_at",
                filter: "status = 'running'");

            migrationBuilder.CreateIndex(
                name: "ix_server_sequences_operation_id",
                table: "server_sequences",
                column: "operation_id");

            migrationBuilder.CreateIndex(
                name: "ix_server_sequences_running",
                table: "server_sequences",
                column: "server_id",
                unique: true,
                filter: "status = 'running'");

            migrationBuilder.CreateIndex(
                name: "ix_server_sequences_server_id_created_at",
                table: "server_sequences",
                columns: new[] { "server_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "game_update_watches");

            migrationBuilder.DropTable(
                name: "server_sequences");

            migrationBuilder.DropCheckConstraint(
                name: "ck_notification_deliveries_event",
                table: "notification_deliveries");

            migrationBuilder.AddCheckConstraint(
                name: "ck_notification_deliveries_event",
                table: "notification_deliveries",
                sql: "event IN ('backup_failed', 'node_disk_low', 'node_offline', 'public_ip_certificate', 'scheduled_task_failed', 'server_crashed', 'server_start_failed')");
        }
    }
}
