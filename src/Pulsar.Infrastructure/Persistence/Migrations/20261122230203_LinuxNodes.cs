using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulsar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LinuxNodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_node_credentials_key_storage",
                table: "node_credentials");

            migrationBuilder.AddColumn<string>(
                name: "platform",
                table: "nodes",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            // Every node so far runs the Windows agent, and every template so far is for Windows only.
            migrationBuilder.Sql("UPDATE nodes SET platform = 'windows-x64' WHERE hostname IS NOT NULL;");

            migrationBuilder.AddColumn<string[]>(
                name: "platforms",
                table: "game_templates",
                type: "text[]",
                nullable: false,
                defaultValue: new[] { "windows-x64" });

            migrationBuilder.Sql("ALTER TABLE game_templates ALTER COLUMN platforms DROP DEFAULT;");

            migrationBuilder.AddCheckConstraint(
                name: "ck_node_credentials_key_storage",
                table: "node_credentials",
                sql: "key_storage IN ('file', 'software', 'tpm')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_node_credentials_key_storage",
                table: "node_credentials");

            migrationBuilder.DropColumn(
                name: "platform",
                table: "nodes");

            migrationBuilder.DropColumn(
                name: "platforms",
                table: "game_templates");

            // Linux agents' key files have no older name: their credentials keep working, named as the closest one.
            migrationBuilder.Sql("UPDATE node_credentials SET key_storage = 'software' WHERE key_storage = 'file';");

            migrationBuilder.AddCheckConstraint(
                name: "ck_node_credentials_key_storage",
                table: "node_credentials",
                sql: "key_storage IN ('software', 'tpm')");
        }
    }
}
