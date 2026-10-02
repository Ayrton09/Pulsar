using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulsar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CredentialAndPanelKeyRotation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "pinned_panel_keys",
                table: "nodes",
                type: "text[]",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "superseded_at",
                table: "node_credentials",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "pinned_panel_keys",
                table: "nodes");

            migrationBuilder.DropColumn(
                name: "superseded_at",
                table: "node_credentials");
        }
    }
}
