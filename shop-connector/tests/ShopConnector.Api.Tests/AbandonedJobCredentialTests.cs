using Connector.Kit;
using Connector.Kit.Errors;
using Connector.Kit.Hosting.Data;
using Connector.Kit.Hosting.Jobs;
using Connector.Kit.Jobs;
using Connector.Kit.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShopConnector.Api.Tests.Infrastructure;

namespace ShopConnector.Api.Tests;

/// <summary>
/// The one credential with no upper bound on how long it rested.
///
/// A login's inputs are serialised into a plain TEXT column when the job is
/// enqueued. Every path that clears them again keys on a lease that expired -
/// requeue, burn, the terminal-state scrub - and a job that was never leased
/// has no lease to expire, so none of them reached it. A login for a
/// browser-tier provider whose pooled residential agent happens to be offline
/// therefore sat Queued holding a plaintext password for as long as the row
/// existed, which is forever: the row is never deleted and nothing sweeps jobs.
///
/// Jumbo is the provider that makes it concrete. It needs a fresh username and
/// password roughly every 24 hours, so the same password re-enters that column
/// daily, and it is browser-tier - the case where there may be no agent.
/// </summary>
[Collection(ShopApiCollection.Name)]
public sealed class AbandonedJobCredentialTests(ShopApiFactory factory)
{
    /// <summary>Browser-tier and pooled, so nothing in this suite will lease it.</summary>
    private const string UnservedProvider = "jumbo";

    private const string Password = "the-password-that-used-to-stay";

    [Fact]
    public async Task A_job_nobody_leased_does_not_keep_its_password_for_ever()
    {
        var jobId = await QueueLoginAsync();

        // The fact this test exists for, asserted rather than assumed: the
        // credential really is sitting in the column, in the clear.
        Assert.Contains(Password, await ColumnAsync(jobId, j => j.InputsJson), StringComparison.Ordinal);

        await BackdateAsync(jobId, TimeSpan.FromHours(2));
        Assert.Equal(1, await SweepAsync());

        var job = await JobAsync(jobId);

        Assert.Null(job.InputsJson);
        Assert.Null(job.MaterialJson);

        // Failed, not merely scrubbed. A queued login stripped of its inputs
        // would be leased later and fail somewhere further in for a reason
        // nobody could reconstruct; this is what actually happened.
        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal(ErrorCode.AgentUnavailable, job.ErrorCode);
    }

    [Fact]
    public async Task A_job_still_inside_its_window_is_left_alone()
    {
        var jobId = await QueueLoginAsync();

        // The other half of the rule. A pooled agent that is briefly offline is
        // the ordinary case, and a sweep that discarded credentials the moment
        // nothing leased them would break every legitimate queue wait.
        await BackdateAsync(jobId, TimeSpan.FromMinutes(5));
        await SweepAsync();

        var job = await JobAsync(jobId);

        Assert.Equal(JobState.Queued, job.State);
        Assert.Contains(Password, job.InputsJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_job_an_agent_is_holding_is_never_swept_however_long_it_takes()
    {
        var jobId = await QueueLoginAsync();

        // A human mid-login can hold a job for a quarter of an hour answering
        // an SMS. It has a live lease, and a live lease is what the existing
        // expiry paths are for - this backstop must not reach past them.
        await LeaseInPlaceAsync(jobId);
        await BackdateAsync(jobId, TimeSpan.FromHours(2));

        Assert.Equal(0, await SweepAsync());

        var job = await JobAsync(jobId);

        Assert.Equal(JobState.Leased, job.State);
        Assert.Contains(Password, job.InputsJson!, StringComparison.Ordinal);
    }

    // ---- setup -------------------------------------------------------------

    private async Task<string> QueueLoginAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
        var queue = scope.ServiceProvider.GetRequiredService<ILeasedJobQueue>();

        var now = DateTimeOffset.UtcNow;
        var session = new SessionRow
        {
            Id = Ids.New(Ids.Session),
            ProviderId = UnservedProvider,
            Subject = $"u_aban_{Guid.NewGuid():N}",
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
            ProviderId = UnservedProvider,
            Kind = JobKind.Login,
            Inputs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["username"] = "somebody@example.test",
                ["password"] = Password,
            },
        }, CancellationToken.None);

        return job.Id;
    }

    /// <summary>
    /// Moves the row's clock rather than the host's. The suite shares one host
    /// on the system TimeProvider, so advancing time would move it for every
    /// other test in the assembly at once.
    /// </summary>
    private async Task BackdateAsync(string jobId, TimeSpan age)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();

        await db.Jobs.Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.UpdatedAt, DateTimeOffset.UtcNow - age));
    }

    private async Task LeaseInPlaceAsync(string jobId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();

        await db.Jobs.Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, JobState.Leased)
                .SetProperty(j => j.LeaseOwner, "agent-holding-it")
                .SetProperty(j => j.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(2)));
    }

    private async Task<int> SweepAsync()
    {
        using var scope = factory.Services.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<ILeasedJobQueue>();

        return await queue.ExpireAbandonedAsync(CancellationToken.None);
    }

    private async Task<JobRow> JobAsync(string jobId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();

        return await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
    }

    private async Task<string> ColumnAsync(string jobId, Func<JobRow, string?> read) =>
        read(await JobAsync(jobId))!;
}
