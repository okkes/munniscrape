using Connector.Kit.Challenges;
using Connector.Kit.Errors;
using Connector.Kit.Hosting.Data;
using Connector.Kit.Hosting.Infrastructure;
using Connector.Kit.Jobs;
using Connector.Kit.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Connector.Kit.Hosting.Challenges;

/// <summary>
/// The relay, end to end: an adapter asks, a human answers, and everything
/// between is the platform's problem.
///
/// Two rules are enforced here because an adapter cannot be trusted to
/// remember them. A challenge always expires - it is holding a live browser
/// hostage, and a stale one must fail the job and release the agent rather
/// than pinning it for the full job timeout. And its image bytes are
/// ephemeral: they go the moment the challenge is answered or expires, are
/// never logged, and never ride a webhook.
/// </summary>
public sealed class ChallengeService(
    ConnectorDbContext db,
    ConnectorSignals signals,
    IOptions<ConnectorOptions> options,
    TimeProvider time)
{
    private readonly ConnectorOptions _options = options.Value;

    /// <summary>
    /// Stores a challenge and parks its job. The job moves to
    /// <see cref="JobState.AwaitingInput"/> in the same call, so there is no
    /// window in which a consumer can see a running job with a pending
    /// question it has not been told about.
    /// </summary>
    public async Task<ChallengeRow> RaiseAsync(string jobId, PendingChallenge challenge, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(challenge);

        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, ct)
                  ?? throw ConnectorException.Unsupported($"unknown job '{jobId}'");

        if (JobStateMachine.IsTerminal(job.State))
        {
            throw ConnectorException.InvalidRequest($"job '{jobId}' is already {job.State}");
        }

        var now = time.GetUtcNow();
        if (challenge.ExpiresAt <= now)
        {
            throw ConnectorException.InvalidRequest("challenge expires_at is already in the past");
        }

        var row = new ChallengeRow
        {
            Id = Ids.New(Ids.Challenge),
            JobId = job.Id,
            Type = challenge.Type,
            PayloadJson = ConnectorJson.Serialize(challenge.Payload),
            ImageBytes = challenge.Image,
            ExpiresAt = challenge.ExpiresAt,
            CreatedAt = now,
        };

        db.Challenges.Add(row);

        if (job.State == JobState.Leased) job.State = JobState.Running;
        if (job.State != JobState.AwaitingInput)
        {
            JobStateMachine.EnsureTransition(job.State, JobState.AwaitingInput);
            job.State = JobState.AwaitingInput;
        }

        job.Step = JobStep.AwaitingHuman;
        job.UpdatedAt = now;

        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == job.SessionId, ct);
        if (session is not null && SessionStateMachine.CanTransition(session.State, SessionState.AwaitingInput))
        {
            session.State = SessionState.AwaitingInput;
            session.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);

        signals.Signal(ConnectorSignals.Job(job.Id));
        signals.Signal(ConnectorSignals.Session(job.SessionId));
        return row;
    }

    /// <summary>The pending challenge for a job, or null. Expired ones do not count as pending.</summary>
    public async Task<ChallengeRow?> PendingAsync(string jobId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return await db.Challenges
            .Where(c => c.JobId == jobId && c.AnsweredAt == null && c.ExpiresAt > now)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<ChallengeRow?> FindAsync(string challengeId, string jobId, CancellationToken ct) =>
        await db.Challenges.FirstOrDefaultAsync(c => c.Id == challengeId && c.JobId == jobId, ct);

    /// <summary>
    /// The image, once. Returns null when there is nothing to show - which
    /// includes an answered or expired challenge, because the bytes are gone
    /// by then and saying so is the honest answer.
    /// </summary>
    public async Task<byte[]?> ImageAsync(string challengeId, string jobId, CancellationToken ct)
    {
        var row = await FindAsync(challengeId, jobId, ct);
        return row?.ImageBytes;
    }

    /// <summary>
    /// Records the human's answer.
    ///
    /// Idempotent per challenge: a second answer is a conflict rather than a
    /// silent overwrite, because overwriting one confuses the agent that is
    /// already acting on the first.
    /// </summary>
    public async Task<ChallengeRow> AnswerAsync(string challengeId, string jobId, string value, CancellationToken ct)
    {
        var row = await FindAsync(challengeId, jobId, ct)
                  ?? throw ConnectorException.Unsupported($"unknown challenge '{challengeId}'");

        // A live view carries no VALUE, and one arriving here would be stored.
        //
        // Everything a human does to a live view travels as pointer and key
        // events, replayed into the page and kept nowhere. This route persists
        // whatever it is handed into ChallengeRow.AnswerValue - so a consumer
        // that posted the typed password here, by confusion or by design, would
        // put the credential straight into the column the whole streamed login
        // exists to keep it out of.
        //
        // An EMPTY answer is still accepted, because that is how a passive
        // challenge is settled and it is how a live view ends: the adapter sees
        // the login complete and closes the question. Refusing that too would
        // leave every live view to time out on its own clock, holding a browser
        // for the full window after the work was already done.
        //
        // Refused here rather than at the endpoint, because the endpoint is not
        // the only way in.
        if (row.Type == ChallengeType.LiveView && !string.IsNullOrEmpty(value))
        {
            throw ConnectorException.Unsupported(
                $"challenge '{challengeId}' is a live view: it is answered by pointer and key events, " +
                "and a value posted here would be stored");
        }

        if (row.AnsweredAt is not null)
        {
            throw ConnectorHttpException.Conflict("challenge_already_answered");
        }

        var now = time.GetUtcNow();
        if (row.ExpiresAt <= now)
        {
            throw new ConnectorException(ErrorCode.ChallengeExpired, $"challenge '{challengeId}' expired");
        }

        row.AnsweredAt = now;
        row.AnswerValue = value;
        // The picture has served its purpose the instant it is answered.
        row.ImageBytes = null;

        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is not null && job.State == JobState.AwaitingInput)
        {
            job.State = JobState.Running;
            job.Step = JobStep.Authenticating;
            job.UpdatedAt = now;

            // The session comes back with it. Leaving it parked would strand
            // the connection in awaiting_input for the rest of the run, and
            // the state machine has no edge from there to active.
            var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == job.SessionId, ct);
            if (session is not null && session.State == SessionState.AwaitingInput)
            {
                session.State = SessionState.Running;
                session.UpdatedAt = now;
            }
        }

        await db.SaveChangesAsync(ct);

        signals.Signal(ConnectorSignals.Job(jobId));
        if (job is not null) signals.Signal(ConnectorSignals.Session(job.SessionId));
        return row;
    }

    /// <summary>
    /// Waits for an answer on the agent's behalf. Returns null when the
    /// challenge expired unanswered, which the caller turns into a clean job
    /// failure rather than a hung browser.
    /// </summary>
    /// <param name="challengeId">
    /// The one being waited on. Null means "whichever is newest", which is what
    /// this always did and is right only while a job asks one question at a
    /// time. It does today - the relay raises a grid, waits, raises the next -
    /// so nothing is broken. But the moment two are outstanding at once, the
    /// newest is not necessarily the one the caller asked about, and the answer
    /// to one question would be handed back as the answer to another. An older
    /// agent that does not send the id keeps the old behaviour rather than
    /// hanging.
    /// </param>
    public async Task<ChallengeAnswer?> AwaitAnswerAsync(
        string jobId, TimeSpan window, CancellationToken ct, string? challengeId = null)
    {
        var deadline = time.GetUtcNow() + window;

        while (!ct.IsCancellationRequested)
        {
            var now = time.GetUtcNow();
            var latest = await db.Challenges
                .AsNoTracking()
                .Where(c => c.JobId == jobId && (challengeId == null || c.Id == challengeId))
                .OrderByDescending(c => c.CreatedAt)
                .FirstOrDefaultAsync(ct);

            if (latest is null) return null;

            if (latest.AnsweredAt is not null)
            {
                return new ChallengeAnswer { ChallengeId = latest.Id, Value = latest.AnswerValue ?? string.Empty };
            }

            if (latest.ExpiresAt <= now) return null;
            if (now >= deadline) return null;

            var slice = Min(latest.ExpiresAt - now, deadline - now, TimeSpan.FromSeconds(2));
            await signals.WaitAsync(ConnectorSignals.Job(jobId), slice, ct);
            db.ChangeTracker.Clear();
        }

        return null;
    }

    /// <summary>
    /// Expires stale challenges and purges image bytes.
    ///
    /// Two passes on purpose: a challenge stops counting as answerable the
    /// moment it expires, but its bytes stay for a short grace window so a
    /// consumer that was mid-render does not get a broken image. After that
    /// they are gone.
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var purgeBefore = now.AddSeconds(-_options.Timeouts.ChallengeGraceSeconds);

        var purged = await db.Challenges
            .Where(c => c.ImageBytes != null && (c.AnsweredAt != null || c.ExpiresAt < purgeBefore))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ImageBytes, (byte[]?)null), ct);

        // The ANSWER is credential material too - an SMS code, or a callback
        // URL carrying a live authorization code - and it is owned by
        // JobOutcomeService, which clears it when the job reaches a terminal
        // state. This is the backstop for a job that never gets there: an
        // agent that lost its lease and never reported, a process killed
        // mid-run. Bounded by the grace window rather than by AnsweredAt,
        // because the adapter reads this column back to get its answer and a
        // sweep racing that read would fail the login it was protecting.
        await db.Challenges
            .Where(c => c.AnswerValue != null
                        && (c.AnsweredAt < purgeBefore || c.ExpiresAt < purgeBefore))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.AnswerValue, (string?)null), ct);

        // Anything older than the grace window is not evidence of anything.
        await db.Challenges
            .Where(c => c.ExpiresAt < now.AddDays(-1))
            .ExecuteDeleteAsync(ct);

        return purged;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b, TimeSpan c)
    {
        var min = a < b ? a : b;
        min = min < c ? min : c;
        return min < TimeSpan.Zero ? TimeSpan.Zero : min;
    }
}

/// <summary>
/// What a caller hands to <see cref="ChallengeService.RaiseAsync"/>: the
/// consumer-visible part, the bytes, and the deadline.
/// </summary>
public sealed record PendingChallenge
{
    public required ChallengeType Type { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Exactly what the consumer will be shown. No prose, only keys and values.</summary>
    public required ChallengePayload Payload { get; init; }

    /// <summary>Redacted PNG, already redacted before it left the agent.</summary>
    public byte[]? Image { get; init; }
}

/// <summary>
/// The consumer-facing shape of a challenge, minus its bytes. Mirrors the
/// kit's <see cref="Challenge"/> without the fields only an agent cares
/// about.
/// </summary>
public sealed record ChallengePayload
{
    /// <summary>
    /// Carried because it decides what the consumer RENDERS, and the consumer
    /// only ever sees this payload. Dropping it here would not fail anything:
    /// every challenge would simply arrive as <see cref="ChallengeAnswerKind.Text"/>,
    /// a grid would be drawn as a text box, and the tap relay would be dead in
    /// production while every test still passed.
    /// </summary>
    public ChallengeAnswerKind AnswerKind { get; init; } = ChallengeAnswerKind.Text;

    public string? PromptKey { get; init; }

    public string? Code { get; init; }

    public string? Delivery { get; init; }

    public int? Length { get; init; }

    public IReadOnlyList<ChallengeOption>? Options { get; init; }

    public string? Url { get; init; }

    public string? ReturnPattern { get; init; }

    public static ChallengePayload From(Challenge challenge)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        return new ChallengePayload
        {
            AnswerKind = challenge.AnswerKind,
            PromptKey = challenge.PromptKey,
            Code = challenge.Code,
            Delivery = challenge.Delivery,
            Length = challenge.Length,
            Options = challenge.Options,
            Url = challenge.Url,
            ReturnPattern = challenge.ReturnPattern,
        };
    }
}
