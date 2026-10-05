using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulsar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScopedAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "node_access",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_scope = table.Column<string>(type: "text", nullable: false),
                    granted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    granted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_node_access", x => new { x.user_id, x.node_id, x.role_id });
                    table.CheckConstraint("ck_node_access_role_scope", "role_scope = 'node'");
                    table.ForeignKey(
                        name: "fk_node_access_nodes_node_id",
                        column: x => x.node_id,
                        principalTable: "nodes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_node_access_roles_role_id_role_scope",
                        columns: x => new { x.role_id, x.role_scope },
                        principalTable: "roles",
                        principalColumns: new[] { "id", "scope" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_node_access_users_granted_by",
                        column: x => x.granted_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_node_access_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "server_access",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_scope = table.Column<string>(type: "text", nullable: false),
                    granted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    granted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_server_access", x => new { x.user_id, x.server_id, x.role_id });
                    table.CheckConstraint("ck_server_access_role_scope", "role_scope = 'server'");
                    table.ForeignKey(
                        name: "fk_server_access_roles_role_id_role_scope",
                        columns: x => new { x.role_id, x.role_scope },
                        principalTable: "roles",
                        principalColumns: new[] { "id", "scope" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_server_access_servers_server_id",
                        column: x => x.server_id,
                        principalTable: "servers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_server_access_users_granted_by",
                        column: x => x.granted_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_server_access_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_node_access_granted_by",
                table: "node_access",
                column: "granted_by");

            migrationBuilder.CreateIndex(
                name: "ix_node_access_node_id",
                table: "node_access",
                column: "node_id");

            migrationBuilder.CreateIndex(
                name: "ix_node_access_role_id_role_scope",
                table: "node_access",
                columns: new[] { "role_id", "role_scope" });

            migrationBuilder.CreateIndex(
                name: "ix_server_access_granted_by",
                table: "server_access",
                column: "granted_by");

            migrationBuilder.CreateIndex(
                name: "ix_server_access_role_id_role_scope",
                table: "server_access",
                columns: new[] { "role_id", "role_scope" });

            migrationBuilder.CreateIndex(
                name: "ix_server_access_server_id",
                table: "server_access",
                column: "server_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "node_access");

            migrationBuilder.DropTable(
                name: "server_access");
        }
    }
}
