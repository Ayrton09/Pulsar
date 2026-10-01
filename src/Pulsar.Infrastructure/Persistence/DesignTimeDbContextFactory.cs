using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Pulsar.Infrastructure.Persistence;

/// <summary>
/// Used by <c>dotnet ef</c> to create migrations. Adding a migration does not connect to the database; commands that do
/// (update, script --idempotent against a live database) read PULSAR_DESIGN_CONNECTION.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PulsarDbContext>
{
    public PulsarDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("PULSAR_DESIGN_CONNECTION")
            ?? "Host=localhost;Database=pulsar_design";
        var options = new DbContextOptionsBuilder<PulsarDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new PulsarDbContext(options);
    }
}
