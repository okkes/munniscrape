using Connector.Kit.Adapters;
using Connector.Kit.AgentProtocol;
using Connector.Kit.Errors;
using Connector.Kit.Hosting.Data;
using Connector.Kit.Hosting.Infrastructure;
using Connector.Kit.Hosting.Providers;
using Connector.Kit.Jobs;
using Connector.Kit.Manifests;
using Connector.Kit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Connector.Kit.Hosting.Jobs;

/// <summary>
/// The portable implementation: a candidate SELECT followed by a conditional
/// UPDATE, retried on a lost race.
///
/// <c>SELECT … FOR UPDATE SKIP LOCKED</c> would be shorter and is Postgres
/// only, and the tests run on Sqlite. Optimistic claiming costs one extra
/// round trip when two agents collide and works identically on both, which is
/// worth more than the round trip: a queue whose real behaviour is only
/// exercised in production is a queue nobody can trust.
/// </summary>
public sealed class EfLeasedJobQueue(
    ConnectorDbContext db,
    IProviderRegistry registry,
    ConnectorSignals signals,
    IOptions<ConnectorOptions> options,
    TimeProvider time,
    ILogger<EfLeasedJobQueue> logger) : ILeasedJobQueue
{
    /// <summary>
    /// One lease, one recovery, then done. The second attempt exists so a
    /// deploy or a crashed agent does not lose work; a third would be a retry
    /// loop, and for a login job a retry loop is an account lockout.
    /// </summary>
    public const int MaxAttempts = 2;

    private const int CandidateBatch = 16;
    private const int RaceRetries = 4;

    private readonly ConnectorOptions _options = options.Value;

    public async Task<JobRow> EnqueueAsync(NewJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);

        var now = time.GetUtcNow();
        var row = new JobRow
        {
            Id = Ids.New(Ids.Job),
            SessionId = job.SessionId,
            ProviderId = job.ProviderId,
            Kind = job.Kind,
            State = JobState.Queued,
            ParamsJson = job.Request is null ? "{}" : ConnectorJson.Serialize(job.Request),
            ResourceId = job.ResourceId,
            Step = JobStep.Queued,
            StepsDoneJson = "[]",
            ConfigJson = ConnectorJson.Serialize(job.Config),
            InputsJson = job.Inputs is null ? null : ConnectorJson.Serialize(job.Inputs),
            MaterialJson = job.Material is null ? null : ConnectorJson.Serialize(job.Material),
            ProfileId = job.ProfileId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Jobs.Add(row);
        await db.SaveChangesAsync(ct);

        signals.Signal(ConnectorSignals.Queue);
        return row;
    }

    public async Task<LeasedJob?> TryLeaseAsync(
        string agentId,
        string? ownerSubject,
        AgentCapabilities capabilities,
        IReadOnlyList<JobKind> accept,
        TimeSpan leaseTtl,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(accept);
        if (accept.Count == 0) return null;

        var paused = await PausedProvidersAsync(ct);
        var serveable = registry.Manifests
            .Where(m => !paused.Contains(m.Id) && capabilities.CanServe(m))
            .Select(m => m.Id)
            .ToHashSet(StringComparer.Ordinal);

        if (serveable.Count == 0) return null;

        // T4: a job pinned to a profile may only ever go to the agent that
        // holds it. Nothing else can drive a browser that is already logged in.
        var ownedProfiles = await db.Profiles
            .Where(p => p.AgentId == agentId)
            .Select(p => p.Id)
            .ToListAsync(ct);

        var wantsLogin = accept.Contains(JobKind.Login);
        var wantsFetch = accept.Contains(JobKind.Fetch);
        var wantsRefresh = accept.Contains(JobKind.Refresh);
        var wantsLogout = accept.Contains(JobKind.Logout);

        for (var round = 0; round < RaceRetries; round++)
        {
            var queued = db.Jobs
                .Where(j => j.State == JobState.Queued
                            && serveable.Contains(j.ProviderId)
                            // An OR chain over constants rather than a
                            // Contains over a collection of value-converted
                            // enums: that one shape is not reliably
                            // translatable on every provider, and this queue
                            // must behave identically on both.
                            && ((wantsLogin && j.Kind == JobKind.Login)
                                || (wantsFetch && j.Kind == JobKind.Fetch)
                                || (wantsRefresh && j.Kind == JobKind.Refresh)
                                || (wantsLogout && j.Kind == JobKind.Logout))
                            && (j.ProfileId == null || ownedProfiles.Contains(j.ProfileId))
                            // Per-session concurrency is 1, always. Two runs
                            // against one account at once is the fastest way
                            // to look like a bot.
                            && !db.Jobs.Any(other => other.SessionId == j.SessionId
                                                     && other.Id != j.Id
                                                     && (other.State == JobState.Leased
                                                         || other.State == JobState.Running
                                                         || other.State == JobState.AwaitingInput)));

            // Ownership. An agent may only ever serve the subject that
            // enrolled it, which is what the enrollment endpoint means when it
            // takes the subject off the one-time code rather than the request.
            // Until this clause existed that was a comment and an index and
            // nothing else: any enrolled machine could lease any user's login
            // job, and Materialize would hand it that user's password in
            // plaintext.
            //
            // Added as a separate Where rather than a term in the big
            // predicate so the fleet and in-process cases emit no clause at
            // all, and so this reads as the rule it is.
            if (ownerSubject is not null)
            {
                queued = queued.Where(j => db.Sessions.Any(
                    s => s.Id == j.SessionId && s.Subject == ownerSubject));
            }

            var candidates = await queued
                .OrderBy(j => j.CreatedAt)
                .ThenBy(j => j.Id)
                .Take(CandidateBatch)
                .AsNoTracking()
                .ToListAsync(ct);

            if (candidates.Count == 0) return null;

            foreach (var candidate in candidates)
            {
                var now = time.GetUtcNow();
                var leaseExpiresAt = now + leaseTtl;

                var claimed = await db.Jobs
                    .Where(j => j.Id == candidate.Id && j.State == JobState.Queued && j.LeaseOwner == null)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(j => j.State, JobState.Leased)
                        .SetProperty(j => j.LeaseOwner, agentId)
                        .SetProperty(j => j.LeaseExpiresAt, leaseExpiresAt)
                        .SetProperty(j => j.Attempts, j => j.Attempts + 1)
                        .SetProperty(j => j.Step, JobStep.AgentAssigned)
                        .SetProperty(j => j.UpdatedAt, now), ct);

                if (claimed != 1) continue;   // another agent won; try the next candidate

                signals.Signal(ConnectorSignals.Job(candidate.Id));
                signals.Signal(ConnectorSignals.Session(candidate.SessionId));
                return Materialize(candidate, leaseExpiresAt);
            }
        }

        logger.LogDebug("agent {AgentId} lost every lease race in this pass", agentId);
        return null;
    }

    public async Task<DateTimeOffset?> RenewLeaseAsync(string jobId, string agentId, TimeSpan leaseTtl, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var leaseExpiresAt = now + leaseTtl;

        var renewed = await db.Jobs
            .Where(j => j.Id == jobId
                        && j.LeaseOwner == agentId
                        && (j.State == JobState.Leased || j.State == JobState.Running || j.State == JobState.AwaitingInput))
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.LeaseExpiresAt, leaseExpiresAt)
                .SetProperty(j => j.UpdatedAt, now), ct);

        return renewed == 1 ? leaseExpiresAt : null;
    }

    public async Task<JobRow> CompleteAsync(string jobId, string? leaseOwner, CancellationToken ct)
    {
        var job = await RequireOwnedAsync(jobId, leaseOwner, tracked: true, ct);
        if (JobStateMachine.IsTerminal(job.State)) return job;

        // A job that produced a result was running, whether or not it ever got
        // round to saying so: progress is reported asynchronously and a fast
        // adapter can finish before its first report lands.
        if (job.State is JobState.Leased or JobState.AwaitingInput) job.State = JobState.Running;

        JobStateMachine.EnsureTransition(job.State, JobState.Succeeded);
        job.State = JobState.Succeeded;
        job.Step = JobStep.Finalizing;
        job.LeaseOwner = null;
        job.LeaseExpiresAt = null;
        job.UpdatedAt = time.GetUtcNow();
        ClearSecrets(job);

        await db.SaveChangesAsync(ct);
        signals.Signal(ConnectorSignals.Job(job.Id));
        signals.Signal(ConnectorSignals.Session(job.SessionId));
        return job;
    }

    public async Task<JobRow> FailAsync(string jobId, string? leaseOwner, ErrorCode code, string? detail, CancellationToken ct)
    {
        var job = await RequireOwnedAsync(jobId, leaseOwner, tracked: true, ct);
        if (JobStateMachine.IsTerminal(job.State)) return job;

        var now = time.GetUtcNow();
        job.ErrorCode = code;
        job.ErrorDetail = Truncate(detail);
        job.LeaseOwner = null;
        job.LeaseExpiresAt = null;
        job.UpdatedAt = now;

        if (MayRetry(job, code))
        {
            job.State = JobState.Queued;
            job.Step = JobStep.Queued;
            await db.SaveChangesAsync(ct);
            signals.Signal(ConnectorSignals.Queue);
        }
        else
        {
            JobStateMachine.EnsureTransition(job.State, JobState.Failed);
            job.State = JobState.Failed;
            ClearSecrets(job);
            await db.SaveChangesAsync(ct);
        }

        signals.Signal(ConnectorSignals.Job(job.Id));
        signals.Signal(ConnectorSignals.Session(job.SessionId));
        return job;
    }

    /// <summary>
    /// Records typed progress - and never, ever resurrects a finished job.
    ///
    /// Progress is reported asynchronously by design: the inline runner drains
    /// a channel and a remote agent posts over HTTP, so a report is routinely
    /// still in flight when the run that produced it finishes. That makes the
    /// obvious implementation - read the row, set the fields, SaveChanges -
    /// a read-modify-write race against <see cref="CompleteAsync"/>, and the
    /// losing write puts <c>running</c> back over <c>succeeded</c>.
    ///
    /// The damage is out of all proportion to the courtesy: nothing owns the
    /// lease any more, so nothing will ever finish that job again, and because
    /// per-session concurrency is 1 the session's every later job then sits
    /// queued and is never leased. The user's next sync does not fail - it
    /// simply never happens, and no error is ever raised to say so. Observed
    /// as a fetch stuck at <c>queued</c> behind a job that had already
    /// returned its data.
    ///
    /// So every guard lives in the WHERE clause of a conditional UPDATE rather
    /// than in a decision made beforehand on a row this context read moments
    /// ago. One statement per rule, for the reason <see cref="BurnAsync"/> is
    /// split the same way: a CASE over a value-converted enum in a SET list is
    /// the kind of thing that translates on one provider and not the other.
    /// </summary>
    public async Task ProgressAsync(string jobId, string? leaseOwner, ProgressReport report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);

        // Untracked: every write below is a conditional UPDATE, and a tracked
        // copy left behind would be a stale row waiting for somebody else's
        // SaveChanges to flush it back.
        var job = await RequireOwnedAsync(jobId, leaseOwner, tracked: false, ct);
        if (JobStateMachine.IsTerminal(job.State)) return;

        // `leased -> running` only. AwaitingInput must survive untouched: a job
        // parked on a challenge that reports a step is still parked.
        if (job.State == JobState.Leased)
        {
            await db.Jobs
                .Where(j => j.Id == jobId && j.State == JobState.Leased)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.State, JobState.Running), ct);
        }

        // Latch only, never unlatch: an agent that reports false after true
        // would silently re-arm the retry it is there to prevent. Its own
        // statement, so a report that latches nothing cannot write the latch
        // back at all, and one that does is not conditional on a state this
        // context may already have misread - a credential that went upstream
        // is a fact, and recording it late is safer than losing it.
        if (report.CredentialSubmitted)
        {
            await db.Jobs
                .Where(j => j.Id == jobId && !j.CredentialSubmitted)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.CredentialSubmitted, true), ct);
        }

        var applied = await db.Jobs
            .Where(j => j.Id == jobId
                        && j.State != JobState.Succeeded
                        && j.State != JobState.Failed
                        && j.State != JobState.Expired)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Step, report.Step)
                .SetProperty(j => j.StepsDoneJson,
                    report.StepsDone.Count > 0 ? ConnectorJson.Serialize(report.StepsDone) : job.StepsDoneJson)
                .SetProperty(j => j.UpdatedAt, time.GetUtcNow()), ct);

        // Nothing applied means the run reached its outcome first, and that
        // outcome has already woken everyone waiting on it.
        if (applied == 0) return;

        signals.Signal(ConnectorSignals.Job(job.Id));
        signals.Signal(ConnectorSignals.Session(job.SessionId));
    }

    public async Task<int> RequeueExpiredLeasesAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();

        // Recoverable: the agent went away before anything was submitted and
        // this job has a life left. It goes back exactly once.
        var requeued = await db.Jobs
            .Where(j => j.LeaseExpiresAt != null
                        && j.LeaseExpiresAt < now
                        && (j.State == JobState.Leased || j.State == JobState.Running || j.State == JobState.AwaitingInput)
                        && !j.CredentialSubmitted
                        && j.Attempts < MaxAttempts)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, JobState.Queued)
                .SetProperty(j => j.Step, JobStep.Queued)
                .SetProperty(j => j.LeaseOwner, (string?)null)
                .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(j => j.UpdatedAt, now), ct);

        // Unrecoverable, and the two reasons are genuinely different. A
        // credential that went upstream may or may not have counted; we do not
        // find out by trying again, so the user signs in again. An agent that
        // vanished twice is simply not there.
        var submitted = await BurnAsync(now, credentialSubmitted: true,
            ErrorCode.SessionExpired, "lease lost after a credential went upstream; never retried", ct);

        var exhausted = await BurnAsync(now, credentialSubmitted: false,
            ErrorCode.AgentUnavailable, "no agent completed this job within its attempts", ct);

        var burnt = submitted + exhausted;

        if (requeued > 0) signals.Signal(ConnectorSignals.Queue);
        if (requeued + burnt > 0)
        {
            logger.LogInformation("reclaimed {Requeued} expired lease(s), failed {Burnt}", requeued, burnt);
        }

        return requeued + burnt;
    }

    /// <summary>
    /// One conditional update per reason. Splitting them keeps the SET list
    /// free of a CASE over a value-converted enum, which is the kind of thing
    /// that translates on one provider and not the other.
    /// </summary>
    private Task<int> BurnAsync(DateTimeOffset now, bool credentialSubmitted, ErrorCode code, string detail, CancellationToken ct) =>
        db.Jobs
            .Where(j => j.LeaseExpiresAt != null
                        && j.LeaseExpiresAt < now
                        && (j.State == JobState.Leased || j.State == JobState.Running || j.State == JobState.AwaitingInput)
                        && j.CredentialSubmitted == credentialSubmitted
                        && (credentialSubmitted || j.Attempts >= MaxAttempts))
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, JobState.Failed)
                .SetProperty(j => j.ErrorCode, code)
                .SetProperty(j => j.ErrorDetail, detail)
                .SetProperty(j => j.LeaseOwner, (string?)null)
                .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(j => j.InputsJson, (string?)null)
                .SetProperty(j => j.MaterialJson, (string?)null)
                .SetProperty(j => j.UpdatedAt, now), ct);

    /// <summary>
    /// The one place retry is decided. Every clause is a separate reason to
    /// stop, and any of them alone is enough.
    /// </summary>
    private static bool MayRetry(JobRow job, ErrorCode code) =>
        !ErrorCatalog.NeverRetry.Contains(code)
        && ErrorCatalog.IsRetriable(code)
        && !job.CredentialSubmitted
        && job.Attempts < MaxAttempts
        && JobStateMachine.CanTransition(job.State, JobState.Queued);

    /// <summary>
    /// The job, if this owner may act on it. <paramref name="tracked"/> is
    /// false for callers that write by conditional UPDATE: handing them a
    /// tracked entity would leave a stale row in the context behind a write
    /// the change tracker never saw.
    /// </summary>
    private async Task<JobRow> RequireOwnedAsync(string jobId, string? leaseOwner, bool tracked, CancellationToken ct)
    {
        var jobs = tracked ? db.Jobs : db.Jobs.AsNoTracking();

        var job = await jobs.FirstOrDefaultAsync(j => j.Id == jobId, ct)
                  ?? throw ConnectorException.Unsupported($"unknown job '{jobId}'");

        if (leaseOwner is not null && !string.Equals(job.LeaseOwner, leaseOwner, StringComparison.Ordinal))
        {
            throw ConnectorException.Unsupported($"job '{jobId}' is not leased to '{leaseOwner}'");
        }

        return job;
    }

    /// <summary>
    /// Inputs and material live only while the job might still need them.
    /// A terminal job holding a credential is a credential at rest with no
    /// reason to exist.
    /// </summary>
    private static void ClearSecrets(JobRow job)
    {
        job.InputsJson = null;
        job.MaterialJson = null;
    }

    private async Task<HashSet<string>> PausedProvidersAsync(CancellationToken ct)
    {
        var rows = await db.ProviderStatuses.AsNoTracking().ToListAsync(ct);
        return rows
            .Where(r => !ProviderStatusService.ToStatus(r).AcceptsWork)
            .Select(r => r.ProviderId)
            .ToHashSet(StringComparer.Ordinal);
    }

    private LeasedJob Materialize(JobRow job, DateTimeOffset leaseExpiresAt)
    {
        var manifest = registry.RequireManifest(job.ProviderId);
        return new LeasedJob
        {
            JobId = job.Id,
            SessionId = job.SessionId,
            Provider = job.ProviderId,
            Kind = job.Kind,
            Inputs = ConnectorJson.DeserializeOr(job.InputsJson, EmptyStrings),
            Config = ConnectorJson.DeserializeOr(job.ConfigJson, EmptyStrings),
            Material = job.MaterialJson is null ? null : ConnectorJson.Deserialize<SessionMaterial>(job.MaterialJson),
            Request = job.Kind == JobKind.Fetch
                ? ConnectorJson.DeserializeOr<ResourceRequest?>(job.ParamsJson, null)
                : null,
            ProfileId = job.ProfileId,
            LeaseExpiresAt = leaseExpiresAt,
            Limits = new JobLimits
            {
                TimeoutSeconds = Math.Max(_options.Timeouts.JobTimeoutSeconds, LongestResourceSeconds(manifest)),
                PolitenessMs = _options.Timeouts.PolitenessMs,
            },
        };
    }

    private static int LongestResourceSeconds(ProviderManifest manifest) =>
        manifest.Resources.Count == 0 ? 0 : manifest.Resources.Max(r => r.TypicalDurationSeconds) * 4;

    private static string? Truncate(string? detail) =>
        detail is null || detail.Length <= 1024 ? detail : detail[..1024];

    private static readonly IReadOnlyDictionary<string, string> EmptyStrings =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
