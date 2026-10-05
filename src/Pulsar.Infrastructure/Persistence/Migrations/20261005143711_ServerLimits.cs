using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulsar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ServerLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_servers_last_state_reason",
                table: "servers");

            migrationBuilder.AddColumn<int>(
                name: "cpu_limit_percent",
                table: "servers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "memory_limit_mb",
                table: "servers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_servers_cpu_limit_percent",
                table: "servers",
                sql: "cpu_limit_percent BETWEEN 1 AND 100");

            migrationBuilder.AddCheckConstraint(
                name: "ck_servers_last_state_reason",
                table: "servers",
                sql: "last_state_reason IN ('agent_started', 'agent_stopping', 'auto_restart', 'crash_loop', 'crashed', 'exited_by_itself', 'killed', 'memory_limit', 'readiness_timed_out', 'ready', 'reconciled', 'requested', 'start_failed', 'stop_timed_out')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_servers_memory_limit_mb",
                table: "servers",
                sql: "memory_limit_mb BETWEEN 128 AND 1048576");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_servers_cpu_limit_percent",
                table: "servers");

            migrationBuilder.DropCheckConstraint(
                name: "ck_servers_last_state_reason",
                table: "servers");

            migrationBuilder.DropCheckConstraint(
                name: "ck_servers_memory_limit_mb",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "cpu_limit_percent",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "memory_limit_mb",
                table: "servers");

            migrationBuilder.AddCheckConstraint(
                name: "ck_servers_last_state_reason",
                table: "servers",
                sql: "last_state_reason IN ('agent_started', 'agent_stopping', 'auto_restart', 'crash_loop', 'crashed', 'exited_by_itself', 'killed', 'readiness_timed_out', 'ready', 'reconciled', 'requested', 'start_failed', 'stop_timed_out')");
        }
    }
}
