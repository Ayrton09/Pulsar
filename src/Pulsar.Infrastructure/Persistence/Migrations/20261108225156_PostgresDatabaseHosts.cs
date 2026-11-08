using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pulsar.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PostgresDatabaseHosts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "engine",
                table: "database_hosts",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "mysql");

            migrationBuilder.AddCheckConstraint(
                name: "ck_database_hosts_engine",
                table: "database_hosts",
                sql: "engine IN ('mysql', 'postgres')");

            // A PostgreSQL that holds the panel's database may now also hold game servers' databases, whose roles any game
            // server's account can sign in with: PostgreSQL lets every role connect to every database unless told otherwise,
            // so the panel's own database stops letting PUBLIC in (its owner, the panel's account, keeps its access; a
            // superuser is never kept out). Only its owner can do this: a panel whose account is not (an external database
            // set up by hand) goes on, and doc 08 says to do it there by hand.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    EXECUTE format('REVOKE CONNECT, TEMPORARY ON DATABASE %I FROM PUBLIC', current_database());
                EXCEPTION WHEN insufficient_privilege THEN
                    RAISE NOTICE 'The panel''s account does not own its database: PUBLIC''s access to it is left as it is.';
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_database_hosts_engine",
                table: "database_hosts");

            migrationBuilder.DropColumn(
                name: "engine",
                table: "database_hosts");
        }
    }
}
