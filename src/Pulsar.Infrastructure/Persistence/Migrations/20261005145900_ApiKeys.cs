using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulsar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ApiKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_logs_actor_type",
                table: "audit_logs");

            migrationBuilder.CreateTable(
                name: "api_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    prefix = table.Column<string>(type: "character varying(13)", maxLength: 13, nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    permissions = table.Column<string[]>(type: "text[]", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_api_keys", x => x.id);
                    table.ForeignKey(
                        name: "fk_api_keys_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_logs_actor_type",
                table: "audit_logs",
                sql: "actor_type IN ('anonymous', 'api_key', 'node', 'schedule', 'system', 'user')");

            migrationBuilder.CreateIndex(
                name: "ix_api_keys_token_hash",
                table: "api_keys",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_api_keys_user_id",
                table: "api_keys",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_keys");

            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_logs_actor_type",
                table: "audit_logs");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_logs_actor_type",
                table: "audit_logs",
                sql: "actor_type IN ('anonymous', 'node', 'schedule', 'system', 'user')");
        }
    }
}
