using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulsar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TemplateUpdates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_server_sequences_origin",
                table: "server_sequences");

            migrationBuilder.AddColumn<string>(
                name: "attempted_template",
                table: "game_update_watches",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "notified_template",
                table: "game_update_watches",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "template_policy",
                table: "game_update_watches",
                type: "text",
                nullable: false,
                defaultValue: "notify"); // as a new watch: newer templates are shown and notified

            migrationBuilder.AddCheckConstraint(
                name: "ck_server_sequences_origin",
                table: "server_sequences",
                sql: "origin IN ('game_update', 'schedule', 'template_update')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_game_update_watches_template_policy",
                table: "game_update_watches",
                sql: "template_policy IN ('install', 'notify', 'off')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_server_sequences_origin",
                table: "server_sequences");

            migrationBuilder.DropCheckConstraint(
                name: "ck_game_update_watches_template_policy",
                table: "game_update_watches");

            migrationBuilder.DropColumn(
                name: "attempted_template",
                table: "game_update_watches");

            migrationBuilder.DropColumn(
                name: "notified_template",
                table: "game_update_watches");

            migrationBuilder.DropColumn(
                name: "template_policy",
                table: "game_update_watches");

            migrationBuilder.Sql("DELETE FROM server_sequences WHERE origin = 'template_update';");

            migrationBuilder.AddCheckConstraint(
                name: "ck_server_sequences_origin",
                table: "server_sequences",
                sql: "origin IN ('game_update', 'schedule')");
        }
    }
}
