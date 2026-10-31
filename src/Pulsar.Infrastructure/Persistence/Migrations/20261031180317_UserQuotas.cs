using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulsar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UserQuotas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "owner_id",
                table: "servers",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "user_quotas",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    max_servers = table.Column<int>(type: "integer", nullable: false),
                    max_memory_mb = table.Column<int>(type: "integer", nullable: false),
                    max_cpu_percent = table.Column<int>(type: "integer", nullable: false),
                    node_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_quotas", x => x.user_id);
                    table.CheckConstraint("ck_user_quotas_max_cpu_percent", "max_cpu_percent BETWEEN 0 AND 10000");
                    table.CheckConstraint("ck_user_quotas_max_memory_mb", "max_memory_mb >= 0");
                    table.CheckConstraint("ck_user_quotas_max_servers", "max_servers BETWEEN 0 AND 1000");
                    table.ForeignKey(
                        name: "fk_user_quotas_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_servers_owner_id",
                table: "servers",
                column: "owner_id");

            migrationBuilder.AddForeignKey(
                name: "fk_servers_users_owner_id",
                table: "servers",
                column: "owner_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_servers_users_owner_id",
                table: "servers");

            migrationBuilder.DropTable(
                name: "user_quotas");

            migrationBuilder.DropIndex(
                name: "ix_servers_owner_id",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "owner_id",
                table: "servers");
        }
    }
}
