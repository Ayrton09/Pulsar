using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulsar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ModSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_server_mods_source",
                table: "server_mods");

            migrationBuilder.DropCheckConstraint(
                name: "ck_server_modpacks_source",
                table: "server_modpacks");

            migrationBuilder.AddCheckConstraint(
                name: "ck_server_mods_source",
                table: "server_mods",
                sql: "source IN ('curse_forge', 'modrinth', 'thunderstore', 'umod')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_server_modpacks_source",
                table: "server_modpacks",
                sql: "source IN ('curse_forge', 'modrinth', 'thunderstore', 'umod')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_server_mods_source",
                table: "server_mods");

            migrationBuilder.DropCheckConstraint(
                name: "ck_server_modpacks_source",
                table: "server_modpacks");

            migrationBuilder.AddCheckConstraint(
                name: "ck_server_mods_source",
                table: "server_mods",
                sql: "source IN ('curse_forge', 'modrinth')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_server_modpacks_source",
                table: "server_modpacks",
                sql: "source IN ('curse_forge', 'modrinth')");
        }
    }
}
