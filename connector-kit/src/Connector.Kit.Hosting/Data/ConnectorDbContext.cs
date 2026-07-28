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
    }

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
