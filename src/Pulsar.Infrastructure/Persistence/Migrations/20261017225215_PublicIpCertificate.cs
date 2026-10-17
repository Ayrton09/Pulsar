using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulsar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PublicIpCertificate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_notification_deliveries_event",
                table: "notification_deliveries");

            migrationBuilder.CreateTable(
                name: "public_ip_certificate_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    public_ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    contact_email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    directory = table.Column<string>(type: "text", nullable: false),
                    terms_agreed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    account_key_protected = table.Column<byte[]>(type: "bytea", nullable: true),
                    account_url = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    account_directory = table.Column<string>(type: "text", nullable: true),
                    account_contact = table.Column<string>(type: "character varying(261)", maxLength: 261, nullable: true),
                    terms_of_service_url = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    certificate_protected = table.Column<byte[]>(type: "bytea", nullable: true),
                    certificate_ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    certificate_directory = table.Column<string>(type: "text", nullable: true),
                    certificate_not_before = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    certificate_not_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    certificate_ari_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    certificate_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    renew_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    renewal_info_checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    consecutive_failures = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    last_error_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failure_notified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expiry_notified_for = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    recent_attempts = table.Column<DateTimeOffset[]>(type: "timestamp with time zone[]", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_public_ip_certificate_settings", x => x.id);
                    table.CheckConstraint("ck_public_ip_certificate_settings_directory", "directory IN ('production', 'staging')");
                    table.CheckConstraint("ck_public_ip_certificate_settings_enabled_ip", "NOT enabled OR public_ip IS NOT NULL");
                    table.CheckConstraint("ck_public_ip_certificate_settings_singleton", "id = 1");
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_notification_deliveries_event",
                table: "notification_deliveries",
                sql: "event IN ('backup_failed', 'node_disk_low', 'node_offline', 'public_ip_certificate', 'scheduled_task_failed', 'server_crashed', 'server_start_failed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "public_ip_certificate_settings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_notification_deliveries_event",
                table: "notification_deliveries");

            migrationBuilder.AddCheckConstraint(
                name: "ck_notification_deliveries_event",
                table: "notification_deliveries",
                sql: "event IN ('backup_failed', 'node_disk_low', 'node_offline', 'scheduled_task_failed', 'server_crashed', 'server_start_failed')");
        }
    }
}
