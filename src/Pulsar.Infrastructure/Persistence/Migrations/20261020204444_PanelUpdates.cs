using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulsar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PanelUpdates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "node_updates",
                columns: table => new
                {
                    node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    from_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    to_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_node_updates", x => x.node_id);
                    table.ForeignKey(
                        name: "fk_node_updates_nodes_node_id",
                        column: x => x.node_id,
                        principalTable: "nodes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_node_updates_users_requested_by",
                        column: x => x.requested_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "update_state",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    check_automatically = table.Column<bool>(type: "boolean", nullable: false),
                    checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    check_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    latest_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    latest_manifest = table.Column<string>(type: "character varying(65536)", maxLength: 65536, nullable: true),
                    latest_manifest_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_update_state", x => x.id);
                    table.CheckConstraint("ck_update_state_singleton", "id = 1");
                });

            migrationBuilder.CreateIndex(
                name: "ix_node_updates_requested_by",
                table: "node_updates",
                column: "requested_by");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "node_updates");

            migrationBuilder.DropTable(
                name: "update_state");
        }
    }
}
