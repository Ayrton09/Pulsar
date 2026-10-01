using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulsar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Nodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "nodes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    normalized_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    public_host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    hostname = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    os_description = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    os_build = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    cpu_model = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    cpu_logical_cores = table.Column<int>(type: "integer", nullable: true),
                    memory_total_bytes = table.Column<long>(type: "bigint", nullable: true),
                    servers_root = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    agent_version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    protocol_version = table.Column<int>(type: "integer", nullable: true),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_nodes", x => x.id);
                    table.CheckConstraint("ck_nodes_status", "status IN ('active', 'disabled', 'pending_enrollment')");
                });

            migrationBuilder.CreateTable(
                name: "panel_signing_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    public_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    protected_private_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    retired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_panel_signing_keys", x => x.id);
                    table.CheckConstraint("ck_panel_signing_keys_status", "status IN ('active', 'retired', 'retiring')");
                });

            migrationBuilder.CreateTable(
                name: "node_credentials",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    public_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    key_storage = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    activated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_by = table.Column<Guid>(type: "uuid", nullable: true),
                    revoke_reason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    algorithm = table.Column<string>(type: "text", nullable: false, defaultValue: "ecdsa-p256-sha256")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_node_credentials", x => x.id);
                    table.CheckConstraint("ck_node_credentials_algorithm", "algorithm = 'ecdsa-p256-sha256'");
                    table.CheckConstraint("ck_node_credentials_key_storage", "key_storage IN ('software', 'tpm')");
                    table.CheckConstraint("ck_node_credentials_status", "status IN ('active', 'pending', 'revoked', 'superseded')");
                    table.ForeignKey(
                        name: "fk_node_credentials_nodes_node_id",
                        column: x => x.node_id,
                        principalTable: "nodes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_node_credentials_users_revoked_by",
                        column: x => x.revoked_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "node_enrollment_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    node_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    consumed_by_credential_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_node_enrollment_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_node_enrollment_tokens_node_credentials_consumed_by_credent",
                        column: x => x.consumed_by_credential_id,
                        principalTable: "node_credentials",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_node_enrollment_tokens_nodes_node_id",
                        column: x => x.node_id,
                        principalTable: "nodes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_node_enrollment_tokens_users_created_by",
                        column: x => x.created_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_node_credentials_fingerprint",
                table: "node_credentials",
                column: "fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_node_credentials_revoked_by",
                table: "node_credentials",
                column: "revoked_by");

            migrationBuilder.CreateIndex(
                name: "ux_node_credentials_single_active",
                table: "node_credentials",
                column: "node_id",
                unique: true,
                filter: "status = 'active'");

            migrationBuilder.CreateIndex(
                name: "ix_node_enrollment_tokens_consumed_by_credential_id",
                table: "node_enrollment_tokens",
                column: "consumed_by_credential_id");

            migrationBuilder.CreateIndex(
                name: "ix_node_enrollment_tokens_created_by",
                table: "node_enrollment_tokens",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_node_enrollment_tokens_node_id",
                table: "node_enrollment_tokens",
                column: "node_id");

            migrationBuilder.CreateIndex(
                name: "ix_node_enrollment_tokens_token_hash",
                table: "node_enrollment_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_nodes_normalized_name",
                table: "nodes",
                column: "normalized_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_panel_signing_keys_key_id",
                table: "panel_signing_keys",
                column: "key_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_panel_signing_keys_single_active",
                table: "panel_signing_keys",
                column: "status",
                unique: true,
                filter: "status = 'active'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "node_enrollment_tokens");

            migrationBuilder.DropTable(
                name: "panel_signing_keys");

            migrationBuilder.DropTable(
                name: "node_credentials");

            migrationBuilder.DropTable(
                name: "nodes");
        }
    }
}
