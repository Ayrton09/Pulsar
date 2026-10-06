using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulsar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Addons : Migration
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
                name: "server_addons",
                columns: table => new
                {
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    release = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    installed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_server_addons", x => new { x.server_id, x.key });
                    table.ForeignKey(
                        name: "fk_server_addons_servers_server_id",
                        column: x => x.server_id,
                        principalTable: "servers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_servers_active_operation_type",
                table: "servers",
                sql: "active_operation_type IN ('addon_install', 'addon_remove', 'backup_create', 'backup_restore', 'server_delete', 'server_install', 'server_provision', 'server_update')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_operations_type",
                table: "operations",
                sql: "type IN ('addon_install', 'addon_remove', 'backup_create', 'backup_restore', 'server_delete', 'server_install', 'server_provision', 'server_update')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "server_addons");

            migrationBuilder.DropCheckConstraint(
                name: "ck_servers_active_operation_type",
                table: "servers");

            migrationBuilder.DropCheckConstraint(
                name: "ck_operations_type",
                table: "operations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_servers_active_operation_type",
                table: "servers",
                sql: "active_operation_type IN ('backup_create', 'backup_restore', 'server_delete', 'server_install', 'server_provision', 'server_update')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_operations_type",
                table: "operations",
                sql: "type IN ('backup_create', 'backup_restore', 'server_delete', 'server_install', 'server_provision', 'server_update')");
        }
    }
}
