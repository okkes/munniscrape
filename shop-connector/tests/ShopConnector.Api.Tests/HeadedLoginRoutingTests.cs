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
/// Who gets offered a login that needs somebody at the browser.
///
/// Jumbo's sign-in can meet an interactive Auth0 wall, and an interactive wall
/// cannot be photographed into an answer - the only person who can pass it is
/// one with hands on that machine. The manifest says so now
/// (<c>login_needs_headed_agent</c>), but until the queue read it the fact was
/// discovered rather than declared: a headless pooled agent leased the login,
/// drove it for two minutes, met the widget, and failed the job
/// <c>blocked_by_provider</c>. A wasted lease for the user, a wasted run
/// against a defended provider, and an error that reads to the account's owner
/// like their account is broken.
///
/// The gate is on the LOGIN alone, and the second test is the one that matters
/// most: the same headless agent must still be offered the same provider's
/// fetches, which are most of the work and need nobody at all. A rule that
/// idled it for those would trade one waste for a larger one.
/// </summary>
[Collection(ShopApiCollection.Name)]
public sealed class HeadedLoginRoutingTests(ShopApiFactory factory)
{
    /// <summary>Browser-interactive, pooled, and declares login_needs_headed_agent.</summary>
    private const string HeadedOnlyProvider = "jumbo";

    /// <summary>
    /// Also browser-tier and pooled, but its login is streamed to the account's
    /// owner wherever they are - so any agent may take it.
    /// </summary>
    private const string RelayableProvider = "ah";

    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task A_headless_agent_is_not_offered_a_login_that_needs_somebody_at_the_browser()
    {
        var queued = await QueueAsync(HeadedOnlyProvider, JobKind.Login);

        var leased = await LeaseAsync(headed: false, JobKind.Login);

        // Not merely "a different job" - nothing at all. The job waits for an
        // agent that can finish it rather than being spent on one that cannot.
        Assert.Null(leased);
        Assert.Equal(JobState.Queued, await StateOfAsync(queued));
    }

    [Fact]
    public async Task The_same_headless_agent_is_still_offered_that_providers_fetches()
    {
        var queued = await QueueAsync(HeadedOnlyProvider, JobKind.Fetch);

        var leased = await LeaseAsync(headed: false, JobKind.Fetch);

        // The whole point of two flags rather than one. Jumbo's fetch needs
        // nobody, and withholding it would idle a perfectly capable agent for
        // a constraint that belongs to the sign-in.
        Assert.NotNull(leased);
        Assert.Equal(queued, leased.JobId);
    }

    [Fact]
    public async Task A_headed_agent_is_offered_the_login_the_headless_one_was_refused()
    {
        // Coolblue rather than Jumbo so this test does not race the queued job
        // the refusal test above deliberately leaves behind.
        const string provider = "coolblue";

        var queued = await QueueAsync(provider, JobKind.Login);

        Assert.Null(await LeaseAsync(headed: false, JobKind.Login, provider));
        Assert.Equal(JobState.Queued, await StateOfAsync(queued));

        Assert.True(
            await LeasedByAsync(headed: true, provider, queued),
            "a headed agent should be offered a login that needs one");
    }

    [Fact]
    public async Task A_login_that_can_be_relayed_still_goes_to_any_agent()
    {
        var queued = await QueueAsync(RelayableProvider, JobKind.Login);

        // Albert Heijn streams its own page to whoever owns the account, wall
        // and all, so "headless" says nothing about whether it can finish.
        // Gating on the runtime instead of the declared fact would have caught
        // this one too, and wrongly.
        Assert.True(
            await LeasedByAsync(headed: false, RelayableProvider, queued),
            "a relayable login should not need a headed agent");
    }

    // ---- setup -------------------------------------------------------------

    private async Task<string> QueueAsync(string provider, JobKind kind)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
        var queue = scope.ServiceProvider.GetRequiredService<ILeasedJobQueue>();

        var now = DateTimeOffset.UtcNow;
        var session = new SessionRow
        {
            Id = Ids.New(Ids.Session),
            ProviderId = provider,
            // A subject of its own per job: per-session concurrency is 1, so
            // two jobs sharing one session would hide each other.
            Subject = $"u_headed_{Guid.NewGuid():N}",
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
            ProviderId = provider,
            Kind = kind,
            Request = kind == JobKind.Fetch ? new ResourceRequest { ResourceId = "receipts" } : null,
        }, CancellationToken.None);

        return job.Id;
    }

    private async Task<LeasedJob?> LeaseAsync(bool headed, JobKind kind, string? provider = null)
    {
        using var scope = factory.Services.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<ILeasedJobQueue>();

        var capabilities = new AgentCapabilities
        {
            Providers = [provider ?? HeadedOnlyProvider],
            Headed = headed,
        };

        return await queue.TryLeaseAsync(
            Ids.New(Ids.Agent),
            // The operator's own fleet, so ownership never masks the answer.
            ownerSubject: null,
            capabilities,
            [kind],
            Ttl,
            CancellationToken.None);
    }

    /// <summary>
    /// Leases until the job under test is the one handed over.
    ///
    /// The suite shares one host, so other classes leave queued logins for
    /// these same providers behind and candidates come back oldest-first.
    /// Asserting "the very next lease is mine" would be asserting the order of
    /// the whole assembly; what this class is about is whether the job is
    /// offered to this agent at all.
    /// </summary>
    private async Task<bool> LeasedByAsync(bool headed, string provider, string jobId)
    {
        for (var attempt = 0; attempt < 25; attempt++)
        {
            var leased = await LeaseAsync(headed, JobKind.Login, provider);
            if (leased is null) return false;
            if (leased.JobId == jobId) return true;
        }

        return false;
    }

    private async Task<JobState> StateOfAsync(string jobId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
        return (await db.Jobs.FindAsync(jobId))!.State;
    }
}
