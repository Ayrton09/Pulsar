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
            // superuser is never kept out). Only its owner can do this: for anyone else PostgreSQL only warns, or refuses,
            // and the panel goes on; it says so in its log at every start while PUBLIC may still connect
            // (DatabaseInitializer), and the guide says how to do it by hand.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    EXECUTE format('REVOKE CONNECT, TEMPORARY ON DATABASE %I FROM PUBLIC', current_database());
                EXCEPTION WHEN insufficient_privilege THEN
                    NULL;
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
