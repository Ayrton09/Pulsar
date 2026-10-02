using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulsar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Servers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "game_templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    source = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    content_sha256 = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_game_templates", x => x.id);
                    table.CheckConstraint("ck_game_templates_source", "source IN ('bundled', 'imported')");
                });

            migrationBuilder.CreateTable(
                name: "operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    server_id = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_via = table.Column<string>(type: "text", nullable: false),
                    progress_stage = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    progress_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true),
                    error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    error_message = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_operations", x => x.id);
                    table.CheckConstraint("ck_operations_requested_via", "requested_via IN ('schedule', 'system', 'user')");
                    table.CheckConstraint("ck_operations_status", "status IN ('cancelled', 'failed', 'interrupted', 'queued', 'running', 'succeeded')");
                    table.CheckConstraint("ck_operations_type", "type IN ('server_delete', 'server_install', 'server_provision')");
                    table.ForeignKey(
                        name: "fk_operations_nodes_node_id",
                        column: x => x.node_id,
                        principalTable: "nodes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_operations_users_requested_by",
                        column: x => x.requested_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "servers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    short_id = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    install_state = table.Column<string>(type: "text", nullable: false),
                    runtime_state = table.Column<string>(type: "text", nullable: false),
                    desired_state = table.Column<string>(type: "text", nullable: false),
                    last_state_reason = table.Column<string>(type: "text", nullable: true),
                    state_sequence = table.Column<long>(type: "bigint", nullable: false),
                    state_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    active_operation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    auto_start = table.Column<bool>(type: "boolean", nullable: false),
                    restart_on_crash = table.Column<bool>(type: "boolean", nullable: false),
                    last_exit_code = table.Column<int>(type: "integer", nullable: true),
                    last_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_stopped_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_servers", x => x.id);
                    table.CheckConstraint("ck_servers_desired_state", "desired_state IN ('running', 'stopped')");
                    table.CheckConstraint("ck_servers_install_state", "install_state IN ('failed', 'installed', 'installing', 'not_installed')");
                    table.CheckConstraint("ck_servers_last_state_reason", "last_state_reason IN ('agent_started', 'agent_stopping', 'auto_restart', 'crash_loop', 'crashed', 'exited_by_itself', 'killed', 'readiness_timed_out', 'ready', 'reconciled', 'requested', 'start_failed', 'stop_timed_out')");
                    table.CheckConstraint("ck_servers_runtime_state", "runtime_state IN ('crashed', 'offline', 'running', 'starting', 'stopping')");
                    table.ForeignKey(
                        name: "fk_servers_game_templates_template_id",
                        column: x => x.template_id,
                        principalTable: "game_templates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_servers_nodes_node_id",
                        column: x => x.node_id,
                        principalTable: "nodes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_servers_operations_active_operation_id",
                        column: x => x.active_operation_id,
                        principalTable: "operations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_game_templates_key_version",
                table: "game_templates",
                columns: new[] { "key", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_operations_active",
                table: "operations",
                column: "node_id",
                filter: "status IN ('queued', 'running')");

            migrationBuilder.CreateIndex(
                name: "ix_operations_requested_by",
                table: "operations",
                column: "requested_by");

            migrationBuilder.CreateIndex(
                name: "ix_operations_server_id_created_at",
                table: "operations",
                columns: new[] { "server_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_servers_active_operation_id",
                table: "servers",
                column: "active_operation_id");

            migrationBuilder.CreateIndex(
                name: "ix_servers_node_id",
                table: "servers",
                column: "node_id");

            migrationBuilder.CreateIndex(
                name: "ix_servers_short_id",
                table: "servers",
                column: "short_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_servers_template_id",
                table: "servers",
                column: "template_id");

            migrationBuilder.AddForeignKey(
                name: "fk_operations_servers_server_id",
                table: "operations",
                column: "server_id",
                principalTable: "servers",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_operations_servers_server_id",
                table: "operations");

            migrationBuilder.DropTable(
                name: "servers");

            migrationBuilder.DropTable(
                name: "game_templates");

            migrationBuilder.DropTable(
                name: "operations");
        }
    }
}
