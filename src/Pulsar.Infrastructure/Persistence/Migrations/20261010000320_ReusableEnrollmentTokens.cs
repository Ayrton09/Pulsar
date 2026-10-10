using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulsar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReusableEnrollmentTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_nodes_status",
                table: "nodes");

            migrationBuilder.AddColumn<bool>(
                name: "approval_required",
                table: "nodes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "enrollment_token_id",
                table: "nodes",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "reusable_enrollment_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    token_prefix = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    max_uses = table.Column<int>(type: "integer", nullable: false),
                    uses = table.Column<int>(type: "integer", nullable: false),
                    require_approval = table.Column<bool>(type: "boolean", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reusable_enrollment_tokens", x => x.id);
                    table.CheckConstraint("ck_reusable_enrollment_tokens_max_uses", "max_uses BETWEEN 1 AND 1000");
                    table.CheckConstraint("ck_reusable_enrollment_tokens_uses", "uses >= 0 AND uses <= max_uses");
                    table.ForeignKey(
                        name: "fk_reusable_enrollment_tokens_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_nodes_enrollment_token_id",
                table: "nodes",
                column: "enrollment_token_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_nodes_status",
                table: "nodes",
                sql: "status IN ('active', 'disabled', 'pending_approval', 'pending_enrollment')");

            migrationBuilder.CreateIndex(
                name: "ix_reusable_enrollment_tokens_created_by",
                table: "reusable_enrollment_tokens",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_reusable_enrollment_tokens_token_hash",
                table: "reusable_enrollment_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_nodes_reusable_enrollment_tokens_enrollment_token_id",
                table: "nodes",
                column: "enrollment_token_id",
                principalTable: "reusable_enrollment_tokens",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_nodes_reusable_enrollment_tokens_enrollment_token_id",
                table: "nodes");

            migrationBuilder.DropTable(
                name: "reusable_enrollment_tokens");

            migrationBuilder.DropIndex(
                name: "ix_nodes_enrollment_token_id",
                table: "nodes");

            migrationBuilder.DropCheckConstraint(
                name: "ck_nodes_status",
                table: "nodes");

            migrationBuilder.DropColumn(
                name: "approval_required",
                table: "nodes");

            migrationBuilder.DropColumn(
                name: "enrollment_token_id",
                table: "nodes");

            migrationBuilder.AddCheckConstraint(
                name: "ck_nodes_status",
                table: "nodes",
                sql: "status IN ('active', 'disabled', 'pending_enrollment')");
        }
    }
}
