using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulsar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ResourceAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_notification_deliveries_event",
                table: "notification_deliveries");

            migrationBuilder.AddColumn<bool>(
                name: "down_after_crash",
                table: "servers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "cpu_high_percent",
                table: "notification_settings",
                type: "integer",
                nullable: false,
                defaultValue: 90);

            migrationBuilder.AddColumn<int>(
                name: "memory_high_percent",
                table: "notification_settings",
                type: "integer",
                nullable: false,
                defaultValue: 90);

            migrationBuilder.AddColumn<int>(
                name: "resource_high_minutes",
                table: "notification_settings",
                type: "integer",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.AddCheckConstraint(
                name: "ck_notification_deliveries_event",
                table: "notification_deliveries",
                sql: "event IN ('backup_failed', 'game_update_available', 'game_update_failed', 'node_disk_low', 'node_offline', 'public_ip_certificate', 'resource_high', 'scheduled_task_failed', 'server_crashed', 'server_start_failed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_notification_deliveries_event",
                table: "notification_deliveries");

            migrationBuilder.DropColumn(
                name: "down_after_crash",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "cpu_high_percent",
                table: "notification_settings");

            migrationBuilder.DropColumn(
                name: "memory_high_percent",
                table: "notification_settings");

            migrationBuilder.DropColumn(
                name: "resource_high_minutes",
                table: "notification_settings");

            migrationBuilder.AddCheckConstraint(
                name: "ck_notification_deliveries_event",
                table: "notification_deliveries",
                sql: "event IN ('backup_failed', 'game_update_available', 'game_update_failed', 'node_disk_low', 'node_offline', 'public_ip_certificate', 'scheduled_task_failed', 'server_crashed', 'server_start_failed')");
        }
    }
}
