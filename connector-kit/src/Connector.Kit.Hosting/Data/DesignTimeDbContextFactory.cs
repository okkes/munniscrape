using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Connector.Kit.Hosting.Data;

/// <summary>
/// The context <c>dotnet ef migrations</c> builds against. Never used at
/// run time, and it never opens the connection below.
///
/// Npgsql on purpose, and only Npgsql: a migration carries the SQL of the
/// provider it was scaffolded for, and this platform runs Postgres wherever it
/// keeps anything worth migrating. Sqlite is the test path, where every run
/// starts from an empty file and <c>EnsureCreated</c> is both correct and
/// faster - see <see cref="ConnectorDbContext.EnsureCreatedOrMigrateAsync"/>,
/// which is why that method asks which provider it is on before choosing.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ConnectorDbContext>
{
    public ConnectorDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ConnectorDbContext>()
            // A shape, not a destination. Scaffolding reads the model and the
            // provider; it never connects.
            .UseNpgsql("Host=localhost;Database=connector_design;Username=design;Password=design")
            .Options);
}
