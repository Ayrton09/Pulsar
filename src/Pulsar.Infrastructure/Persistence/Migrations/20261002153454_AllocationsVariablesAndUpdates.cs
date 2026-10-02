using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulsar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AllocationsVariablesAndUpdates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_operations_type",
                table: "operations");

            migrationBuilder.AddColumn<string[]>(
                name: "extra_arguments",
                table: "servers",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "cancel_requested_at",
                table: "operations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "node_allocations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ip_address = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    port = table.Column<int>(type: "integer", nullable: false),
                    server_id = table.Column<Guid>(type: "uuid", nullable: true),
                    port_name = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    notes = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_node_allocations", x => x.id);
                    table.CheckConstraint("ck_node_allocations_assignment", "(server_id IS NULL) = (port_name IS NULL)");
                    table.CheckConstraint("ck_node_allocations_port", "port BETWEEN 1 AND 65535");
                    table.ForeignKey(
                        name: "fk_node_allocations_nodes_node_id",
                        column: x => x.node_id,
                        principalTable: "nodes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_node_allocations_servers_server_id",
                        column: x => x.server_id,
                        principalTable: "servers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "server_variables",
                columns: table => new
                {
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    value = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    protected_value = table.Column<byte[]>(type: "bytea", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_server_variables", x => new { x.server_id, x.key });
                    table.CheckConstraint("ck_server_variables_one_value", "(value IS NULL) <> (protected_value IS NULL)");
                    table.ForeignKey(
                        name: "fk_server_variables_servers_server_id",
                        column: x => x.server_id,
                        principalTable: "servers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_server_variables_users_updated_by",
                        column: x => x.updated_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_operations_type",
                table: "operations",
                sql: "type IN ('server_delete', 'server_install', 'server_provision', 'server_update')");

            migrationBuilder.CreateIndex(
                name: "ix_node_allocations_node_id_ip_address_port",
                table: "node_allocations",
                columns: new[] { "node_id", "ip_address", "port" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_node_allocations_server_id_port_name",
                table: "node_allocations",
                columns: new[] { "server_id", "port_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_server_variables_updated_by",
                table: "server_variables",
                column: "updated_by");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "node_allocations");

            migrationBuilder.DropTable(
                name: "server_variables");

            migrationBuilder.DropCheckConstraint(
                name: "ck_operations_type",
                table: "operations");

            migrationBuilder.DropColumn(
                name: "extra_arguments",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "cancel_requested_at",
                table: "operations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_operations_type",
                table: "operations",
                sql: "type IN ('server_delete', 'server_install', 'server_provision')");
        }
    }
}
