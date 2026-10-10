using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulsar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BackupCopyMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "copy_mode",
                table: "backups",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "game_save",
                table: "backups",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "snapshot_error",
                table: "backups",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_backups_copy_mode",
                table: "backups",
                sql: "copy_mode IN ('live', 'snapshot', 'stopped')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_backups_game_save",
                table: "backups",
                sql: "game_save IN ('confirmed', 'unconfirmed', 'waited')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_backups_copy_mode",
                table: "backups");

            migrationBuilder.DropCheckConstraint(
                name: "ck_backups_game_save",
                table: "backups");

            migrationBuilder.DropColumn(
                name: "copy_mode",
                table: "backups");

            migrationBuilder.DropColumn(
                name: "game_save",
                table: "backups");

            migrationBuilder.DropColumn(
                name: "snapshot_error",
                table: "backups");
        }
    }
}
