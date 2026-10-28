using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulsar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TransferAllocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_server_sequences_origin",
                table: "server_sequences");

            migrationBuilder.DropIndex(
                name: "ix_node_allocations_server_id_port_name",
                table: "node_allocations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_server_sequences_origin",
                table: "server_sequences",
                sql: "origin IN ('clone', 'game_update', 'schedule', 'steam_branch', 'template_update', 'transfer')");

            migrationBuilder.CreateIndex(
                name: "ix_node_allocations_server_id_node_id_port_name",
                table: "node_allocations",
                columns: new[] { "server_id", "node_id", "port_name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_server_sequences_origin",
                table: "server_sequences");

            migrationBuilder.DropIndex(
                name: "ix_node_allocations_server_id_node_id_port_name",
                table: "node_allocations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_server_sequences_origin",
                table: "server_sequences",
                sql: "origin IN ('clone', 'game_update', 'schedule', 'steam_branch', 'template_update')");

            migrationBuilder.CreateIndex(
                name: "ix_node_allocations_server_id_port_name",
                table: "node_allocations",
                columns: new[] { "server_id", "port_name" },
                unique: true);
        }
    }
}
