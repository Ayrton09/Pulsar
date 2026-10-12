using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulsar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentClientCertificates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "agent_can_use_certificate",
                table: "nodes",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "connected_with_certificate",
                table: "nodes",
                type: "boolean",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "agent_transport_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    require_client_certificates = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_transport_settings", x => x.id);
                    table.CheckConstraint("ck_agent_transport_settings_singleton", "id = 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_transport_settings");

            migrationBuilder.DropColumn(
                name: "agent_can_use_certificate",
                table: "nodes");

            migrationBuilder.DropColumn(
                name: "connected_with_certificate",
                table: "nodes");
        }
    }
}
