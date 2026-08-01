using Microsoft.EntityFrameworkCore;

namespace Connector.Kit.Hosting.Data;

/// <summary>
/// One context, two providers.
///
/// Every mapping here is deliberately provider-agnostic: enums are stored as
/// strings, structured values as JSON <em>text</em> rather than <c>jsonb</c>,
/// and every timestamp is a <see cref="DateTimeOffset"/> written in UTC. That
/// is what lets the identical schema run on Postgres in production and on
/// Sqlite in a test, so the tests exercise the real queries instead of a
/// stand-in.
/// </summary>
public sealed class ConnectorDbContext(DbContextOptions<ConnectorDbContext> options) : DbContext(options)
{
    public DbSet<SessionRow> Sessions => Set<SessionRow>();

    public DbSet<JobRow> Jobs => Set<JobRow>();

    public DbSet<ChallengeRow> Challenges => Set<ChallengeRow>();

    public DbSet<ResultRow> Results => Set<ResultRow>();

    public DbSet<AgentRow> Agents => Set<AgentRow>();

    public DbSet<ProfileRow> Profiles => Set<ProfileRow>();

    public DbSet<ProviderStatusRow> ProviderStatuses => Set<ProviderStatusRow>();

    public DbSet<EnrollmentRow> Enrollments => Set<EnrollmentRow>();

    /// <summary>
    /// Applies migrations when the assembly carries any, and falls back to
    /// <c>EnsureCreated</c> otherwise.
    ///
    /// No migrations are generated yet - that is a separate build step - so
    /// today this always takes the <c>EnsureCreated</c> path. The branch is
    /// here rather than added later because the call site is the same either
    /// way, and a host should not have to change when migrations land.
    /// </summary>
    public async Task EnsureCreatedOrMigrateAsync(CancellationToken ct = default)
    {
        if (Database.GetMigrations().Any())
        {
            await Database.MigrateAsync(ct);
            return;
        }

        await Database.EnsureCreatedAsync(ct);
        await AssertSchemaMatchesModelAsync(ct);
    }

    /// <summary>
    /// Refuses to start against a database that is missing something the model
    /// has.
    ///
    /// <c>EnsureCreated</c> creates missing TABLES and nothing else. A column
    /// added to an entity after the database exists is simply never created, so
    /// the service starts perfectly happily and then throws a 500 the first
    /// time a request writes that column - which reads to whoever hit it as a
    /// broken provider rather than a schema that was never brought forward.
    /// That happened: two columns landed in one afternoon and the first user to
    /// press Connect got "Something broke on our side."
    ///
    /// One empty SELECT per table, naming every column the model expects. It
    /// costs a handful of round trips at start-up, uses the database's own
    /// answer rather than a hand-rolled diff, and works on any relational
    /// provider because it is just SQL. A mismatch becomes a refusal to start
    /// with the table named - the same call this file's neighbours make about
    /// a manifest that lies.
    /// </summary>
    private async Task AssertSchemaMatchesModelAsync(CancellationToken ct)
    {
        foreach (var entity in Model.GetEntityTypes())
        {
            if (entity.GetTableName() is not { } table) continue;

            var columns = entity.GetProperties()
                .Select(p => p.GetColumnName())
                .Where(c => !string.IsNullOrEmpty(c))
                .Select(Quote)
                .ToList();

            if (columns.Count == 0) continue;

            // Built here rather than interpolated at the call site so it is
            // plain that nothing in it came from a request: every identifier is
            // read off the EF model this assembly was compiled with, and each
            // one goes through Quote. WHERE 1 = 0 so this reads nothing and
            // still has to resolve every name.
            var probe = "SELECT " + string.Join(", ", columns) + " FROM " + Quote(table) + " WHERE 1 = 0";

            try
            {
                await Database.ExecuteSqlRawAsync(probe, ct);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"the '{table}' table does not match the model this build expects. " +
                    "EnsureCreated only ever creates missing tables, so a column added since " +
                    "this database was created is absent and every write touching it fails at " +
                    "request time. Bring the schema forward, or drop the database if it holds " +
                    "nothing worth keeping.",
                    ex);
            }
        }
    }

    private static string Quote(string identifier) =>
        '"' + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';

    /// <summary>
    /// The provider switch, exposed so a test can build a context without the
    /// whole platform.
    /// </summary>
    public static void Configure(DbContextOptionsBuilder builder, ConnectorDatabaseOptions database)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(database);

        switch (database.Provider)
        {
            case ConnectorDatabaseProvider.Postgres:
                builder.UseNpgsql(database.ConnectionString);
                break;
            case ConnectorDatabaseProvider.Sqlite:
                builder.UseSqlite(database.ConnectionString);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(database), database.Provider, "unknown database provider");
        }
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        configurationBuilder.Properties<DateTimeOffset>()
            .HaveConversion<UtcInstantConverter>()
            .HaveMaxLength(UtcInstantConverter.StoredLength)
            .AreUnicode(false);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<SessionRow>(e =>
        {
            e.ToTable("sessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.ProviderId).HasMaxLength(64);
            e.Property(x => x.Subject).HasMaxLength(128);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.DeviceClass).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.AgentId).HasMaxLength(64);
            e.Property(x => x.ProfileId).HasMaxLength(64);
            e.Property(x => x.Label).HasMaxLength(128);
            e.Property(x => x.ConsentTermsVersion).HasMaxLength(32);
            // Every authorisation check is (subject, provider); nothing else
            // may reach a session, so this is the index that matters.
            e.HasIndex(x => new { x.Subject, x.ProviderId });
            e.HasIndex(x => x.ExpiresAt);
        });

        modelBuilder.Entity<JobRow>(e =>
        {
            e.ToTable("jobs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.SessionId).HasMaxLength(64);
            e.Property(x => x.ProviderId).HasMaxLength(64);
            e.Property(x => x.ResourceId).HasMaxLength(64);
            e.Property(x => x.LeaseOwner).HasMaxLength(64);
            e.Property(x => x.ProfileId).HasMaxLength(64);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Step).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.ErrorCode).HasConversion<string>().HasMaxLength(32);
            // The leasing query filters on exactly this pair, and a lease
            // that scans the table is a lease that loses races.
            e.HasIndex(x => new { x.State, x.ProviderId });
            e.HasIndex(x => x.SessionId);
            e.HasIndex(x => x.LeaseExpiresAt);
        });

        modelBuilder.Entity<ChallengeRow>(e =>
        {
            e.ToTable("challenges");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.JobId).HasMaxLength(64);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(32);
            e.HasIndex(x => x.JobId);
            e.HasIndex(x => x.ExpiresAt);
        });

        modelBuilder.Entity<ResultRow>(e =>
        {
            e.ToTable("results");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.JobId).HasMaxLength(64);
            e.Property(x => x.SessionId).HasMaxLength(64);
            e.Property(x => x.Resource).HasMaxLength(64);
            e.Property(x => x.ExternalId).HasMaxLength(256);
            e.Property(x => x.ContentHash).HasMaxLength(80);
            e.Property(x => x.Cursor).HasMaxLength(64);
            // (session, resource, external_id) uniqueness is what makes a
            // re-run free for the caller: a repeat never duplicates.
            e.HasIndex(x => new { x.SessionId, x.Resource, x.ExternalId }).IsUnique();
            e.HasIndex(x => x.Cursor);
            e.HasIndex(x => x.CreatedAt);
        });

        modelBuilder.Entity<AgentRow>(e =>
        {
            e.ToTable("agents");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.Class).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.OwnerSubject).HasMaxLength(128);
            e.Property(x => x.TokenHash).HasMaxLength(80);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.OwnerSubject);
        });

        modelBuilder.Entity<ProfileRow>(e =>
        {
            e.ToTable("profiles");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.AgentId).HasMaxLength(64);
            e.Property(x => x.ProviderId).HasMaxLength(64);
            e.Property(x => x.SessionId).HasMaxLength(64);
            e.HasIndex(x => x.AgentId);
            e.HasIndex(x => x.SessionId);
        });

        modelBuilder.Entity<ProviderStatusRow>(e =>
        {
            e.ToTable("provider_status");
            e.HasKey(x => x.ProviderId);
            e.Property(x => x.ProviderId).HasMaxLength(64);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.ReasonKey).HasMaxLength(128);
        });

        modelBuilder.Entity<EnrollmentRow>(e =>
        {
            e.ToTable("enrollments");
            e.HasKey(x => x.CodeHash);
            e.Property(x => x.CodeHash).HasMaxLength(80);
            e.Property(x => x.Subject).HasMaxLength(128);
            e.Property(x => x.Name).HasMaxLength(128);
            e.HasIndex(x => x.ExpiresAt);
        });
    }
}
