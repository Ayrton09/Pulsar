using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulsar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ServerModpacks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_servers_active_operation_type",
                table: "servers");

            migrationBuilder.DropCheckConstraint(
                name: "ck_operations_type",
                table: "operations");

            migrationBuilder.CreateTable(
                name: "server_modpacks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    project_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    version_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    version_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    server_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    loader = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    game_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    loader_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    state = table.Column<string>(type: "text", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    installed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_server_modpacks", x => x.id);
                    table.CheckConstraint("ck_server_modpacks_source", "source IN ('modrinth')");
                    table.CheckConstraint("ck_server_modpacks_state", "state IN ('installed', 'installing')");
                    table.ForeignKey(
                        name: "fk_server_modpacks_operations_operation_id",
                        column: x => x.operation_id,
                        principalTable: "operations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_server_modpacks_servers_server_id",
                        column: x => x.server_id,
                        principalTable: "servers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_servers_active_operation_type",
                table: "servers",
                sql: "active_operation_type IN ('addon_install', 'addon_remove', 'backup_create', 'backup_restore', 'modpack_install', 'mods_change', 'server_delete', 'server_install', 'server_provision', 'server_update')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_operations_type",
                table: "operations",
                sql: "type IN ('addon_install', 'addon_remove', 'backup_create', 'backup_restore', 'modpack_install', 'mods_change', 'server_delete', 'server_install', 'server_provision', 'server_update')");

            migrationBuilder.CreateIndex(
                name: "ix_server_modpacks_operation_id",
                table: "server_modpacks",
                column: "operation_id",
                filter: "operation_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_server_modpacks_server_id",
                table: "server_modpacks",
                column: "server_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "server_modpacks");

            migrationBuilder.DropCheckConstraint(
                name: "ck_servers_active_operation_type",
                table: "servers");

            migrationBuilder.DropCheckConstraint(
                name: "ck_operations_type",
                table: "operations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_servers_active_operation_type",
                table: "servers",
                sql: "active_operation_type IN ('addon_install', 'addon_remove', 'backup_create', 'backup_restore', 'mods_change', 'server_delete', 'server_install', 'server_provision', 'server_update')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_operations_type",
                table: "operations",
                sql: "type IN ('addon_install', 'addon_remove', 'backup_create', 'backup_restore', 'mods_change', 'server_delete', 'server_install', 'server_provision', 'server_update')");
        }
    }
}
