using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulsar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SteamBranches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_server_sequences_origin",
                table: "server_sequences");

            migrationBuilder.AddColumn<string>(
                name: "steam_branch",
                table: "servers",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "branches",
                table: "game_update_watches",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.AddCheckConstraint(
                name: "ck_server_sequences_origin",
                table: "server_sequences",
                sql: "origin IN ('game_update', 'schedule', 'steam_branch', 'template_update')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_server_sequences_origin",
                table: "server_sequences");

            migrationBuilder.DropColumn(
                name: "steam_branch",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "branches",
                table: "game_update_watches");

            migrationBuilder.AddCheckConstraint(
                name: "ck_server_sequences_origin",
                table: "server_sequences",
                sql: "origin IN ('game_update', 'schedule', 'template_update')");
        }
    }
}
