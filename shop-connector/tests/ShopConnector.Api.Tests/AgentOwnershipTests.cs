using Connector.Kit;
using Connector.Kit.AgentProtocol;
using Connector.Kit.Hosting.Data;
using Connector.Kit.Hosting.Jobs;
using Connector.Kit.Jobs;
using Connector.Kit.Sessions;
using Microsoft.Extensions.DependencyInjection;
using ShopConnector.Api.Tests.Infrastructure;

namespace ShopConnector.Api.Tests;

/// <summary>
/// The invariant the enrollment endpoint states in a comment - "an agent may
/// only ever serve the user who enrolled it, and letting it name its own owner
/// would undo that in one line" - asserted against the queue that has to keep
/// it.
///
/// It was written in four places and enforced in none. `AgentRow.OwnerSubject`
/// had a column, an index, a doc comment and a write at enrollment, and its
/// only reader was the `/agents` listing. `TryLeaseAsync` filtered on provider,
/// job kind, profile affinity and per-session concurrency, and never once asked
/// whose job it was handing over - so any enrolled machine could lease any
/// user's login job and `Materialize` would deserialise that user's password
/// into it in plaintext.
///
/// These tests exist because the omission cost nothing to make and nothing to
/// keep: every suite stayed green the whole time it was there.
///
/// The provider is an agent-tier one on purpose. `InlineJobRunner` serves only
/// manifests with `Agent.Class == Inline &amp;&amp; !Agent.Required`, so a job
/// queued for a browser-tier provider stays queued instead of racing the
/// in-process pump for it.
/// </summary>
[Collection(ShopApiCollection.Name)]
public sealed class AgentOwnershipTests(ShopApiFactory factory)
{
    private const string AgentTierProvider = "ah";

    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);

    /// <summary>Only ever names the provider; the ownership rule must do the rest.</summary>
    private static AgentCapabilities Capabilities => new() { Providers = [AgentTierProvider] };

    [Fact]
    public async Task An_agent_is_never_handed_a_job_belonging_to_another_subject()
    {
        var owner = NewSubject();
        var stranger = NewSubject();

        // The stranger queues FIRST, and that ordering is the test. Candidates
        // come back `OrderBy(CreatedAt)`, so with no ownership rule the oldest
        // queued job wins and the stranger's is the one handed over. Queue the
        // owner's first and this test passes with or without the fix - which
        // it did, until the bite check caught it.
        var strangersJob = await QueueLoginAsync(stranger, "strangers-password");
        var ownersJob = await QueueLoginAsync(owner, "owners-password");

        // The agent that belongs to `owner`, asking for work.
        var leased = await LeaseAsync(ownerScope: owner);

        Assert.NotNull(leased);
        Assert.Equal(ownersJob, leased.JobId);

        // The part that actually matters: not merely "it got the right job"
        // but "the other subject's credential never entered this process".
        Assert.NotEqual(strangersJob, leased.JobId);
        Assert.DoesNotContain("strangers-password", leased.Inputs.Values);
        Assert.Contains("owners-password", leased.Inputs.Values);
    }

    [Fact]
    public async Task An_agent_scoped_to_a_subject_with_no_work_is_told_there_is_nothing_rather_than_given_someone_elses()
    {
        var stranger = NewSubject();
        var idle = NewSubject();

        await QueueLoginAsync(stranger, "strangers-password");

        // `idle` has queued nothing. Before ownership was enforced this
        // returned the stranger's job, because a queued job for a serveable
        // provider was reason enough to hand it over.
        var leased = await LeaseAsync(ownerScope: idle);

        Assert.Null(leased);
    }

    [Fact]
    public async Task The_operators_own_fleet_is_the_one_caller_that_may_cross_subjects()
    {
        var somebody = NewSubject();
        var queued = await QueueLoginAsync(somebody, "a-password");

        // A null scope is how the in-process runner and a configured fleet
        // agent ask. It is a parameter rather than something read off the
        // agent row precisely so that opting out has to be written down.
        var leased = await LeaseAsync(ownerScope: null);

        Assert.NotNull(leased);
        Assert.Equal(queued, leased.JobId);
    }

    private static string NewSubject() => $"u_own_{Guid.NewGuid():N}";

    /// <summary>
    /// A session and a queued login for it, straight into the tables. Going
    /// through POST /login would work too, but it would also drag consent,
    /// idempotency and manifest validation into a test about none of them.
    /// </summary>
    private async Task<string> QueueLoginAsync(string subject, string password)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
        var queue = scope.ServiceProvider.GetRequiredService<ILeasedJobQueue>();

        var now = DateTimeOffset.UtcNow;
        var session = new SessionRow
        {
            Id = Ids.New(Ids.Session),
            ProviderId = AgentTierProvider,
            Subject = subject,
            State = SessionState.Queued,
            ExpiresAt = now.AddHours(1),
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Sessions.Add(session);
        await db.SaveChangesAsync(CancellationToken.None);

        var job = await queue.EnqueueAsync(new NewJob
        {
            SessionId = session.Id,
            ProviderId = AgentTierProvider,
            Kind = JobKind.Login,
            Inputs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["username"] = subject,
                ["password"] = password,
            },
        }, CancellationToken.None);

        return job.Id;
    }

    private async Task<LeasedJob?> LeaseAsync(string? ownerScope)
    {
        using var scope = factory.Services.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<ILeasedJobQueue>();

        return await queue.TryLeaseAsync(
            Ids.New(Ids.Agent),
            ownerScope,
            Capabilities,
            [JobKind.Login],
            Ttl,
            CancellationToken.None);
    }
}
