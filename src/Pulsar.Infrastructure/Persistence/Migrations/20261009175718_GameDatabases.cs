using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulsar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GameDatabases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "database_limit",
                table: "servers",
                type: "integer",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.CreateTable(
                name: "database_hosts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    port = table.Column<int>(type: "integer", nullable: false),
                    user_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    password_protected = table.Column<byte[]>(type: "bytea", nullable: false),
                    server_address = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    server_port = table.Column<int>(type: "integer", nullable: true),
                    user_host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    node_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_database_hosts", x => x.id);
                    table.CheckConstraint("ck_database_hosts_port", "port BETWEEN 1 AND 65535");
                    table.CheckConstraint("ck_database_hosts_server_port", "server_port IS NULL OR server_port BETWEEN 1 AND 65535");
                });

            migrationBuilder.CreateTable(
                name: "server_databases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    host_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    user_host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    password_protected = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    password_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_server_databases", x => x.id);
                    table.ForeignKey(
                        name: "fk_server_databases_database_hosts_host_id",
                        column: x => x.host_id,
                        principalTable: "database_hosts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_server_databases_servers_server_id",
                        column: x => x.server_id,
                        principalTable: "servers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_servers_database_limit",
                table: "servers",
                sql: "database_limit BETWEEN 0 AND 50");

            migrationBuilder.CreateIndex(
                name: "ix_database_hosts_name",
                table: "database_hosts",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_server_databases_host_id_name",
                table: "server_databases",
                columns: new[] { "host_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_server_databases_server_id",
                table: "server_databases",
                column: "server_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "server_databases");

            migrationBuilder.DropTable(
                name: "database_hosts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_servers_database_limit",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "database_limit",
                table: "servers");
        }
    }
}
