using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulsar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BillingExternalIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "external_id",
                table: "users",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_id",
                table: "servers",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_external_id",
                table: "users",
                column: "external_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_servers_external_id",
                table: "servers",
                column: "external_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_users_external_id",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ix_servers_external_id",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "external_id",
                table: "users");

            migrationBuilder.DropColumn(
                name: "external_id",
                table: "servers");
        }
    }
}
