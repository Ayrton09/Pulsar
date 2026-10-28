using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulsar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ServerClones : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_server_sequences_origin",
                table: "server_sequences");

            migrationBuilder.AddCheckConstraint(
                name: "ck_server_sequences_origin",
                table: "server_sequences",
                sql: "origin IN ('clone', 'game_update', 'schedule', 'steam_branch', 'template_update')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_server_sequences_origin",
                table: "server_sequences");

            migrationBuilder.AddCheckConstraint(
                name: "ck_server_sequences_origin",
                table: "server_sequences",
                sql: "origin IN ('game_update', 'schedule', 'steam_branch', 'template_update')");
        }
    }
}
