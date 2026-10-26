using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulsar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ServerMods : Migration
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

            migrationBuilder.AddColumn<string>(
                name: "game_version",
                table: "servers",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "mod_catalog_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    curse_forge_key = table.Column<byte[]>(type: "bytea", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mod_catalog_settings", x => x.id);
                    table.CheckConstraint("ck_mod_catalog_settings_singleton", "id = 1");
                });

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
                    table.CheckConstraint("ck_server_modpacks_source", "source IN ('curse_forge', 'modrinth')");
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

            migrationBuilder.CreateTable(
                name: "server_mods",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    project_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    version_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    version_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    path = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    dependency = table.Column<bool>(type: "boolean", nullable: false),
                    requires = table.Column<string>(type: "jsonb", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    installed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_server_mods", x => x.id);
                    table.CheckConstraint("ck_server_mods_source", "source IN ('curse_forge', 'modrinth')");
                    table.CheckConstraint("ck_server_mods_state", "state IN ('installed', 'installing', 'removing')");
                    table.ForeignKey(
                        name: "fk_server_mods_operations_operation_id",
                        column: x => x.operation_id,
                        principalTable: "operations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_server_mods_servers_server_id",
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

            migrationBuilder.CreateIndex(
                name: "ix_server_mods_operation_id",
                table: "server_mods",
                column: "operation_id",
                filter: "operation_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_server_mods_server_id_project_id",
                table: "server_mods",
                columns: new[] { "server_id", "project_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mod_catalog_settings");

            migrationBuilder.DropTable(
                name: "server_modpacks");

            migrationBuilder.DropTable(
                name: "server_mods");

            migrationBuilder.DropCheckConstraint(
                name: "ck_servers_active_operation_type",
                table: "servers");

            migrationBuilder.DropCheckConstraint(
                name: "ck_operations_type",
                table: "operations");

            migrationBuilder.DropColumn(
                name: "game_version",
                table: "servers");

            migrationBuilder.AddCheckConstraint(
                name: "ck_servers_active_operation_type",
                table: "servers",
                sql: "active_operation_type IN ('addon_install', 'addon_remove', 'backup_create', 'backup_restore', 'server_delete', 'server_install', 'server_provision', 'server_update')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_operations_type",
                table: "operations",
                sql: "type IN ('addon_install', 'addon_remove', 'backup_create', 'backup_restore', 'server_delete', 'server_install', 'server_provision', 'server_update')");
        }
    }
}
