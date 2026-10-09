using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulsar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SteamAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "steam_account_id",
                table: "servers",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "steam_accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    user_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    password_protected = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_steam_accounts", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_servers_steam_account_id",
                table: "servers",
                column: "steam_account_id");

            migrationBuilder.CreateIndex(
                name: "ix_steam_accounts_name",
                table: "steam_accounts",
                column: "name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_servers_steam_accounts_steam_account_id",
                table: "servers",
                column: "steam_account_id",
                principalTable: "steam_accounts",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_servers_steam_accounts_steam_account_id",
                table: "servers");

            migrationBuilder.DropTable(
                name: "steam_accounts");

            migrationBuilder.DropIndex(
                name: "ix_servers_steam_account_id",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "steam_account_id",
                table: "servers");
        }
    }
}
