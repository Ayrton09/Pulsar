using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulsar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MetricHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "node_metrics",
                columns: table => new
                {
                    node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resolution = table.Column<short>(type: "smallint", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    samples = table.Column<int>(type: "integer", nullable: false),
                    cpu_sum = table.Column<double>(type: "double precision", nullable: false),
                    cpu_max = table.Column<double>(type: "double precision", nullable: false),
                    memory_used_sum = table.Column<long>(type: "bigint", nullable: false),
                    memory_used_max = table.Column<long>(type: "bigint", nullable: false),
                    memory_total = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_node_metrics", x => new { x.node_id, x.resolution, x.at });
                    table.CheckConstraint("ck_node_metrics_resolution", "resolution IN (1, 60)");
                    table.CheckConstraint("ck_node_metrics_samples", "samples > 0");
                    table.ForeignKey(
                        name: "fk_node_metrics_nodes_node_id",
                        column: x => x.node_id,
                        principalTable: "nodes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "server_metrics",
                columns: table => new
                {
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resolution = table.Column<short>(type: "smallint", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    samples = table.Column<int>(type: "integer", nullable: false),
                    cpu_sum = table.Column<double>(type: "double precision", nullable: false),
                    cpu_max = table.Column<double>(type: "double precision", nullable: false),
                    memory_sum = table.Column<long>(type: "bigint", nullable: false),
                    memory_max = table.Column<long>(type: "bigint", nullable: false),
                    player_samples = table.Column<int>(type: "integer", nullable: false),
                    players_sum = table.Column<long>(type: "bigint", nullable: false),
                    players_max = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_server_metrics", x => new { x.server_id, x.resolution, x.at });
                    table.CheckConstraint("ck_server_metrics_resolution", "resolution IN (1, 60)");
                    table.CheckConstraint("ck_server_metrics_samples", "samples > 0 AND player_samples BETWEEN 0 AND samples");
                    table.ForeignKey(
                        name: "fk_server_metrics_servers_server_id",
                        column: x => x.server_id,
                        principalTable: "servers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_node_metrics_resolution_at",
                table: "node_metrics",
                columns: new[] { "resolution", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_server_metrics_resolution_at",
                table: "server_metrics",
                columns: new[] { "resolution", "at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "node_metrics");

            migrationBuilder.DropTable(
                name: "server_metrics");
        }
    }
}
