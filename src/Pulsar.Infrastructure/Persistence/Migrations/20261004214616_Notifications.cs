using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulsar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Notifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "disk_low_notified_at",
                table: "nodes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "offline_notified_at",
                table: "nodes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "notification_channels",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    events = table.Column<string[]>(type: "text[]", nullable: false),
                    server_ids = table.Column<Guid[]>(type: "uuid[]", nullable: true),
                    language = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    webhook_url_protected = table.Column<byte[]>(type: "bytea", nullable: true),
                    recipients = table.Column<string[]>(type: "text[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_channels", x => x.id);
                    table.CheckConstraint("ck_notification_channels_kind", "kind IN ('discord', 'email')");
                    table.CheckConstraint("ck_notification_channels_target", "(kind = 'discord' AND webhook_url_protected IS NOT NULL) OR (kind = 'email' AND cardinality(recipients) > 0)");
                });

            migrationBuilder.CreateTable(
                name: "notification_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    panel_url = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    smtp_host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    smtp_port = table.Column<int>(type: "integer", nullable: false),
                    smtp_security = table.Column<string>(type: "text", nullable: false),
                    smtp_user_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    smtp_password_protected = table.Column<byte[]>(type: "bytea", nullable: true),
                    from_address = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    from_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    disk_low_percent = table.Column<int>(type: "integer", nullable: false),
                    node_offline_minutes = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_settings", x => x.id);
                    table.CheckConstraint("ck_notification_settings_singleton", "id = 1");
                    table.CheckConstraint("ck_notification_settings_smtp_security", "smtp_security IN ('none', 'start_tls', 'tls')");
                });

            migrationBuilder.CreateTable(
                name: "notification_deliveries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    @event = table.Column<string>(name: "event", type: "text", nullable: false),
                    subject = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    message = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_deliveries", x => x.id);
                    table.CheckConstraint("ck_notification_deliveries_event", "event IN ('backup_failed', 'node_disk_low', 'node_offline', 'scheduled_task_failed', 'server_crashed', 'server_start_failed')");
                    table.CheckConstraint("ck_notification_deliveries_status", "status IN ('failed', 'pending', 'sent')");
                    table.ForeignKey(
                        name: "fk_notification_deliveries_notification_channels_channel_id",
                        column: x => x.channel_id,
                        principalTable: "notification_channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_created_at",
                table: "notification_deliveries",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_due",
                table: "notification_deliveries",
                column: "next_attempt_at",
                filter: "status = 'pending'");

            migrationBuilder.CreateIndex(
                name: "ix_notification_deliveries_subject",
                table: "notification_deliveries",
                columns: new[] { "channel_id", "event", "subject", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notification_deliveries");

            migrationBuilder.DropTable(
                name: "notification_settings");

            migrationBuilder.DropTable(
                name: "notification_channels");

            migrationBuilder.DropColumn(
                name: "disk_low_notified_at",
                table: "nodes");

            migrationBuilder.DropColumn(
                name: "offline_notified_at",
                table: "nodes");
        }
    }
}
