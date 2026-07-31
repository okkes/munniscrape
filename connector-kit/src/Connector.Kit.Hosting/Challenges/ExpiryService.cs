using Connector.Kit.AgentProtocol;
using Connector.Kit.Errors;
using Connector.Kit.Hosting.Data;
using Connector.Kit.Hosting.Infrastructure;
using Connector.Kit.Hosting.Jobs;
using Connector.Kit.Hosting.Live;
using Connector.Kit.Hosting.Sessions;
using Connector.Kit.Hosting.Staging;
using Connector.Kit.Hosting.Tickets;
using Connector.Kit.Jobs;
using Connector.Kit.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Connector.Kit.Hosting.Challenges;

/// <summary>
/// The sweeper.
///
/// Everything in this service has a deadline, and a deadline nobody enforces
/// is a comment. A challenge left pending holds a live browser and an agent
/// hostage; a lease left dangling means a user waits forever for a machine
/// that is switched off; a staged row left unacked is data we promised to
/// forget. One loop, run often, closes all of them.
/// </summary>
public sealed class ExpiryService(
    IServiceScopeFactory scopes,
    ITicketStore tickets,
    IIdempotencyStore idempotency,
    LiveChannel live,
    IOptions<ConnectorOptions> options,
    TimeProvider time,
    ILogger<ExpiryService> logger) : BackgroundService
{
    private readonly ConnectorOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.Timeouts.ExpirySweepSeconds));
        using var timer = new PeriodicTimer(interval, time);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // A failed sweep must never take the service down: the next
                // one is fifteen seconds away and will find the same rows.
                logger.LogError(ex, "expiry sweep failed");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken)) return;
        }
    }

    public async Task SweepAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var provider = scope.ServiceProvider;
        var db = provider.GetRequiredService<ConnectorDbContext>();
        var queue = provider.GetRequiredService<ILeasedJobQueue>();
        var challenges = provider.GetRequiredService<ChallengeService>();
        var results = provider.GetRequiredService<ResultService>();
        var outcomes = provider.GetRequiredService<JobOutcomeService>();

        await FailJobsOnExpiredChallengesAsync(db, outcomes, ct);
        await challenges.SweepAsync(ct);
        await queue.RequeueExpiredLeasesAsync(ct);

        // Before the reconcile below, so a session whose only run was never
        // taken is told the same way as one whose run died: this fails jobs,
        // and that is what turns a failed job into a session saying something
        // true instead of "running" until its TTL runs out.
        await queue.ExpireAbandonedAsync(ct);

        await ReconcileStrandedSessionsAsync(db, provider.GetRequiredService<SessionService>(), ct);
        await ExpireSessionsAsync(db, ct);
        await results.SweepAsync(ct);
        await ExpireEnrollmentsAsync(db, ct);
        tickets.Sweep();
        idempotency.Sweep();

        // A login abandoned mid-stream leaves a photograph of a half-filled
        // login form in memory with nobody coming for it. The job outcome
        // drops the channel on the paths that end a job cleanly; this is the
        // one that closes the tab nobody told us about, and the reason the
        // frame slot cannot be leaked by forgetting a call.
        live.Sweep();
    }

    /// <summary>
    /// A lease that expires fails its job inside the queue, in bulk, without
    /// ever loading a session. That is the right shape for the queue and the
    /// wrong outcome for the user: a connection whose only run died would sit
    /// on "running" until its TTL ran out days later. This closes the gap, so
    /// every terminal job leaves the session saying something true.
    /// </summary>
    private static async Task ReconcileStrandedSessionsAsync(ConnectorDbContext db, SessionService sessions, CancellationToken ct)
    {
        var stranded = await db.Sessions
            .Where(s => (s.State == SessionState.Queued
                         || s.State == SessionState.Running
                         || s.State == SessionState.AwaitingInput)
                        && db.Jobs.Any(j => j.SessionId == s.Id)
                        && !db.Jobs.Any(j => j.SessionId == s.Id
                                             && (j.State == JobState.Queued
                                                 || j.State == JobState.Leased
                                                 || j.State == JobState.Running
                                                 || j.State == JobState.AwaitingInput)))
            // Oldest first, with a deterministic tiebreak. A batched sweep
            // without an order is free to return the same rows every pass, so
            // anything it cannot settle would starve the rest forever.
            .OrderBy(s => s.UpdatedAt)
            .ThenBy(s => s.Id)
            .Take(64)
            .ToListAsync(ct);

        foreach (var session in stranded)
        {
            var last = await db.Jobs
                .Where(j => j.SessionId == session.Id)
                .OrderByDescending(j => j.CreatedAt)
                .ThenByDescending(j => j.Id)
                .FirstOrDefaultAsync(ct);

            // A succeeded job already moved the session itself; only a
            // failure can leave one behind.
            if (last is null || last.State is not (JobState.Failed or JobState.Expired)) continue;

            var next = JobOutcomeService.SessionStateFor(last.ErrorCode ?? ErrorCode.Internal, session.State);
            await sessions.TransitionOrTerminateAsync(session, next, ct);
        }
    }

    /// <summary>
    /// A challenge that ran out of time fails its job cleanly and releases the
    /// agent, rather than pinning a browser for the whole job timeout while
    /// nobody is coming.
    /// </summary>
    private async Task FailJobsOnExpiredChallengesAsync(ConnectorDbContext db, JobOutcomeService outcomes, CancellationToken ct)
    {
        var now = time.GetUtcNow();

        var stranded = await db.Jobs
            .Where(j => j.State == JobState.AwaitingInput
                        && !db.Challenges.Any(c => c.JobId == j.Id && c.AnsweredAt == null && c.ExpiresAt > now))
            // Ordered for the same reason as the session sweep: an unordered
            // batch can revisit the same rows indefinitely and never reach
            // the ones behind them.
            .OrderBy(j => j.UpdatedAt)
            .ThenBy(j => j.Id)
            .Select(j => j.Id)
            .Take(64)
            .ToListAsync(ct);

        foreach (var jobId in stranded)
        {
            await outcomes.FailAsync(jobId, leaseOwner: null, new JobFailRequest
            {
                Code = ErrorCatalog.Wire(ErrorCode.ChallengeExpired),
                Detail = "no answer before the challenge expired",
            }, ct);
        }
    }

    private async Task ExpireSessionsAsync(ConnectorDbContext db, CancellationToken ct)
    {
        var now = time.GetUtcNow();

        // Only non-terminal states move; a disabled session stays disabled,
        // because "the user turned this off" and "this ran out" are different
        // things to say to them.
        await db.Sessions
            .Where(s => s.ExpiresAt < now
                        && (s.State == SessionState.Queued
                            || s.State == SessionState.Running
                            || s.State == SessionState.AwaitingInput
                            || s.State == SessionState.Active
                            || s.State == SessionState.NeedsReauth))
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.State, SessionState.Expired)
                .SetProperty(s => s.PendingBundle, (string?)null)
                .SetProperty(s => s.UpdatedAt, now), ct);
    }

    private async Task ExpireEnrollmentsAsync(ConnectorDbContext db, CancellationToken ct)
    {
        var cutoff = time.GetUtcNow().AddDays(-1);
        await db.Enrollments.Where(e => e.ExpiresAt < cutoff).ExecuteDeleteAsync(ct);
    }
}
