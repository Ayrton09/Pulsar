using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulsar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SftpSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sftp_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    port = table.Column<int>(type: "integer", nullable: false),
                    public_host = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    host_public_key = table.Column<byte[]>(type: "bytea", nullable: true),
                    protected_host_key = table.Column<byte[]>(type: "bytea", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sftp_settings", x => x.id);
                    table.CheckConstraint("ck_sftp_settings_port", "port BETWEEN 1 AND 65535");
                    table.CheckConstraint("ck_sftp_settings_singleton", "id = 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sftp_settings");
        }
    }
}
