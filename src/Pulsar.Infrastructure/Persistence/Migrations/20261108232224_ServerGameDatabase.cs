using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulsar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ServerGameDatabase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "game_database_id",
                table: "servers",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_servers_game_database_id",
                table: "servers",
                column: "game_database_id");

            migrationBuilder.AddForeignKey(
                name: "fk_servers_server_databases_game_database_id",
                table: "servers",
                column: "game_database_id",
                principalTable: "server_databases",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_servers_server_databases_game_database_id",
                table: "servers");

            migrationBuilder.DropIndex(
                name: "ix_servers_game_database_id",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "game_database_id",
                table: "servers");
        }
    }
}
