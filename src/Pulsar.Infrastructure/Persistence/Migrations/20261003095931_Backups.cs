using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulsar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Backups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_operations_type",
                table: "operations");

            migrationBuilder.AddColumn<string>(
                name: "active_operation_type",
                table: "servers",
                type: "text",
                nullable: true);

            // Servers busy with an operation when the panel is updated keep it, with its type.
            migrationBuilder.Sql("UPDATE servers SET active_operation_type = o.type FROM operations o WHERE servers.active_operation_id = o.id;");

            migrationBuilder.AddColumn<int>(
                name: "backup_limit",
                table: "servers",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "backups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    file_count = table.Column<int>(type: "integer", nullable: true),
                    skipped_files = table.Column<int>(type: "integer", nullable: true),
                    sha256 = table.Column<byte[]>(type: "bytea", nullable: true),
                    is_locked = table.Column<bool>(type: "boolean", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_via = table.Column<string>(type: "text", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error_message = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_backups", x => x.id);
                    table.CheckConstraint("ck_backups_created_via", "created_via IN ('schedule', 'system', 'user')");
                    table.CheckConstraint("ck_backups_status", "status IN ('cancelled', 'completed', 'creating', 'failed')");
                    table.ForeignKey(
                        name: "fk_backups_nodes_node_id",
                        column: x => x.node_id,
                        principalTable: "nodes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_backups_operations_operation_id",
                        column: x => x.operation_id,
                        principalTable: "operations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_backups_servers_server_id",
                        column: x => x.server_id,
                        principalTable: "servers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_backups_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_servers_active_operation_type",
                table: "servers",
                sql: "active_operation_type IN ('backup_create', 'backup_restore', 'server_delete', 'server_install', 'server_provision', 'server_update')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_servers_backup_limit",
                table: "servers",
                sql: "backup_limit BETWEEN 1 AND 1000");

            migrationBuilder.AddCheckConstraint(
                name: "ck_operations_type",
                table: "operations",
                sql: "type IN ('backup_create', 'backup_restore', 'server_delete', 'server_install', 'server_provision', 'server_update')");

            migrationBuilder.CreateIndex(
                name: "ix_backups_created_by",
                table: "backups",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_backups_node_id",
                table: "backups",
                column: "node_id");

            migrationBuilder.CreateIndex(
                name: "ix_backups_operation_id",
                table: "backups",
                column: "operation_id");

            migrationBuilder.CreateIndex(
                name: "ix_backups_server_id_created_at",
                table: "backups",
                columns: new[] { "server_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "backups");

            migrationBuilder.DropCheckConstraint(
                name: "ck_servers_active_operation_type",
                table: "servers");

            migrationBuilder.DropCheckConstraint(
                name: "ck_servers_backup_limit",
                table: "servers");

            migrationBuilder.DropCheckConstraint(
                name: "ck_operations_type",
                table: "operations");

            migrationBuilder.DropColumn(
                name: "active_operation_type",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "backup_limit",
                table: "servers");

            migrationBuilder.AddCheckConstraint(
                name: "ck_operations_type",
                table: "operations",
                sql: "type IN ('server_delete', 'server_install', 'server_provision', 'server_update')");
        }
    }
}
