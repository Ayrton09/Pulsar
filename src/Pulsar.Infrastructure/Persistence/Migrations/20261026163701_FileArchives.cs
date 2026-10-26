using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulsar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FileArchives : Migration
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

            migrationBuilder.AddCheckConstraint(
                name: "ck_servers_active_operation_type",
                table: "servers",
                sql: "active_operation_type IN ('addon_install', 'addon_remove', 'backup_create', 'backup_restore', 'files_compress', 'files_extract', 'modpack_install', 'mods_change', 'server_delete', 'server_install', 'server_provision', 'server_update')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_operations_type",
                table: "operations",
                sql: "type IN ('addon_install', 'addon_remove', 'backup_create', 'backup_restore', 'files_compress', 'files_extract', 'modpack_install', 'mods_change', 'server_delete', 'server_install', 'server_provision', 'server_update')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The older types cannot hold the file manager's zips: those operations go, and a server one of them holds is freed.
            migrationBuilder.Sql("UPDATE servers SET active_operation_id = NULL, active_operation_type = NULL WHERE active_operation_type IN ('files_compress', 'files_extract');");
            migrationBuilder.Sql("DELETE FROM operations WHERE type IN ('files_compress', 'files_extract');");

            migrationBuilder.DropCheckConstraint(
                name: "ck_servers_active_operation_type",
                table: "servers");

            migrationBuilder.DropCheckConstraint(
                name: "ck_operations_type",
                table: "operations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_servers_active_operation_type",
                table: "servers",
                sql: "active_operation_type IN ('addon_install', 'addon_remove', 'backup_create', 'backup_restore', 'modpack_install', 'mods_change', 'server_delete', 'server_install', 'server_provision', 'server_update')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_operations_type",
                table: "operations",
                sql: "type IN ('addon_install', 'addon_remove', 'backup_create', 'backup_restore', 'modpack_install', 'mods_change', 'server_delete', 'server_install', 'server_provision', 'server_update')");
        }
    }
}
