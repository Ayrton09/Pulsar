using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulsar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoteBackups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "backup_storage_id",
                table: "servers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "remote_error",
                table: "backups",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "remote_key",
                table: "backups",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "remote_status",
                table: "backups",
                type: "text",
                nullable: false,
                defaultValue: "none");

            migrationBuilder.AddColumn<Guid>(
                name: "storage_id",
                table: "backups",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "backup_storages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    endpoint = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    region = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    bucket = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    prefix = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    access_key_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    secret_protected = table.Column<byte[]>(type: "bytea", nullable: false),
                    path_style = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_backup_storages", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_servers_backup_storage_id",
                table: "servers",
                column: "backup_storage_id");

            migrationBuilder.CreateIndex(
                name: "ix_backups_storage_id",
                table: "backups",
                column: "storage_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_backups_remote_status",
                table: "backups",
                sql: "remote_status IN ('copied', 'failed', 'none', 'pending')");

            migrationBuilder.AddForeignKey(
                name: "fk_backups_backup_storages_storage_id",
                table: "backups",
                column: "storage_id",
                principalTable: "backup_storages",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_servers_backup_storages_backup_storage_id",
                table: "servers",
                column: "backup_storage_id",
                principalTable: "backup_storages",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_backups_backup_storages_storage_id",
                table: "backups");

            migrationBuilder.DropForeignKey(
                name: "fk_servers_backup_storages_backup_storage_id",
                table: "servers");

            migrationBuilder.DropTable(
                name: "backup_storages");

            migrationBuilder.DropIndex(
                name: "ix_servers_backup_storage_id",
                table: "servers");

            migrationBuilder.DropIndex(
                name: "ix_backups_storage_id",
                table: "backups");

            migrationBuilder.DropCheckConstraint(
                name: "ck_backups_remote_status",
                table: "backups");

            migrationBuilder.DropColumn(
                name: "backup_storage_id",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "remote_error",
                table: "backups");

            migrationBuilder.DropColumn(
                name: "remote_key",
                table: "backups");

            migrationBuilder.DropColumn(
                name: "remote_status",
                table: "backups");

            migrationBuilder.DropColumn(
                name: "storage_id",
                table: "backups");
        }
    }
}
