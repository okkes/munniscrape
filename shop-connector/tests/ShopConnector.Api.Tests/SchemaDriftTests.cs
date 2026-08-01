using Connector.Kit.Hosting;
using Connector.Kit.Hosting.Data;
using Microsoft.EntityFrameworkCore;

namespace ShopConnector.Api.Tests;

/// <summary>
/// Start-up refuses a database that is missing something the model has.
///
/// <c>EnsureCreated</c> creates missing TABLES and nothing else. A column added
/// to an entity after the database exists is never created, so the service
/// starts happily and throws a 500 the first time a request writes it - which
/// reads to whoever hit it as a broken provider rather than a schema nobody
/// brought forward.
///
/// That is not hypothetical. Two columns landed in one afternoon
/// (<c>sessions.PendingCredentialBundle</c> and <c>results.RawJson</c>), every
/// test stayed green because the suite builds a fresh SQLite file per run, and
/// the first person to press Connect on a two-day-old Postgres volume got
/// "Something broke on our side."
/// </summary>
public sealed class SchemaDriftTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "schema-drift-tests", Guid.NewGuid().ToString("N"));

    public SchemaDriftTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task A_database_that_matches_the_model_starts()
    {
        await using var db = Context("match");

        await db.EnsureCreatedOrMigrateAsync();

        // And again, because start-up runs against an EXISTING database far
        // more often than a fresh one.
        await db.EnsureCreatedOrMigrateAsync();
    }

    [Fact]
    public async Task A_column_the_model_gained_after_the_database_was_created_refuses_to_start()
    {
        await using var db = Context("drift");
        await db.EnsureCreatedOrMigrateAsync();

        // Exactly what an added column looks like from the database's side: the
        // table is there, one of its columns is not.
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE sessions DROP COLUMN \"PendingCredentialBundle\"");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => db.EnsureCreatedOrMigrateAsync());

        // Names the table, so an operator knows where to look rather than
        // reading a stack trace from a request that failed hours later.
        Assert.Contains("'sessions' table", error.Message, StringComparison.Ordinal);
        Assert.Contains("EnsureCreated only ever creates missing tables", error.Message, StringComparison.Ordinal);

        // The database's own answer is kept, because it is the thing that names
        // the column.
        Assert.NotNull(error.InnerException);
        Assert.Contains("PendingCredentialBundle", error.InnerException.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other column from the same afternoon, on a different table - so this
    /// asserts the check walks the whole model rather than one entity somebody
    /// remembered.
    /// </summary>
    [Fact]
    public async Task The_check_covers_every_table_and_not_just_sessions()
    {
        await using var db = Context("results");
        await db.EnsureCreatedOrMigrateAsync();

        await db.Database.ExecuteSqlRawAsync("ALTER TABLE results DROP COLUMN \"RawJson\"");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => db.EnsureCreatedOrMigrateAsync());

        Assert.Contains("'results' table", error.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Sqlite's pool may still hold the file. Not worth failing over.
        }
    }

    private ConnectorDbContext Context(string name)
    {
        var builder = new DbContextOptionsBuilder<ConnectorDbContext>();

        ConnectorDbContext.Configure(builder, new ConnectorDatabaseOptions
        {
            Provider = ConnectorDatabaseProvider.Sqlite,
            ConnectionString = $"Data Source={Path.Combine(_root, name + ".db")}",
        });

        return new ConnectorDbContext(builder.Options);
    }
}
