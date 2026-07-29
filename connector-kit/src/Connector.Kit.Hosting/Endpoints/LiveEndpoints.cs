using Connector.Kit.Adapters;
using Connector.Kit.Challenges;
using Connector.Kit.Errors;
using Connector.Kit.Hosting.Auth;
using Connector.Kit.Hosting.Challenges;
using Connector.Kit.Hosting.Data;
using Connector.Kit.Hosting.Infrastructure;
using Connector.Kit.Hosting.Live;
using Connector.Kit.Hosting.Sessions;
using Connector.Kit.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Connector.Kit.Hosting.Endpoints;

/// <summary>
/// The streamed login's four routes: pictures out, pointer and keys back.
///
/// Two legs, and they are not symmetric. The agent's leg is outbound like
/// every other agent call - it POSTs the picture it just took and long-polls
/// for what the human has done since - so nothing has to dial in to a machine
/// on somebody's home line. The consumer's leg is a long-poll for the newest
/// frame and a POST of what its owner did to it.
///
/// Everything both legs touch lives in <see cref="LiveChannel"/> and therefore
/// in memory. No frame and no keystroke reaches a table, which is the whole
/// custody argument for streaming a login rather than collecting a password in
/// a form.
///
/// Both groups are declared with HTTP logging off. That is the structural half
/// of "the frame bytes are never logged": any body logger added to this
/// service later - the framework's own, or anything that honours its metadata -
/// is already told to skip these endpoints, and a new live route added to
/// either group inherits the refusal rather than needing somebody to remember
/// it.
/// </summary>
internal static class LiveEndpoints
{
    /// <summary>
    /// The consumer's half, beside the still relay's image route and resolved
    /// exactly the way it is: the session, then its run in flight, then the
    /// challenge scoped to that run.
    /// </summary>
    public static void MapConsumer(IEndpointRouteBuilder api)
    {
        var live = api.MapGroup("/{provider}/login/{sessionId}/challenges/{challengeId}/live")
            .WithHttpLogging(HttpLoggingFields.None);

        live.MapGet("/frame", async (
            HttpContext http,
            string provider,
            string sessionId,
            string challengeId,
            IProviderRegistry registry,
            SessionService sessions,
            ChallengeService challenges,
            ViewBuilder views,
            LiveChannel channel,
            ConnectorSignals signals,
            IOptions<ConnectorOptions> options,
            TimeProvider time,
            CancellationToken ct) =>
        {
            var job = await RequireLiveJobAsync(
                provider, sessionId, challengeId, registry, sessions, challenges, views, time, ct);

            var after = LiveHeaders.After(http.Request);
            var deadline = time.GetUtcNow().AddSeconds(options.Value.Timeouts.LiveFramePollSeconds);

            // A long poll rather than a fixed-interval refetch, woken by the
            // agent's POST. A consumer polling on a timer would either be
            // permanently a frame behind or spend most of its requests being
            // told nothing changed, and neither is a login somebody can type
            // into.
            while (!ct.IsCancellationRequested)
            {
                if (channel.Frame(job.Id) is { } frame && frame.Sequence > after)
                {
                    http.Response.Headers[LiveHeaders.Sequence] = LiveHeaders.FormatSequence(frame.Sequence);
                    http.Response.Headers[LiveHeaders.Size] = LiveHeaders.FormatSize(frame.Width, frame.Height);
                    http.Response.Headers.CacheControl = LiveHeaders.NoStore;
                    return Results.File(frame.Bytes, LiveHeaders.Jpeg);
                }

                var remaining = deadline - time.GetUtcNow();
                if (remaining <= TimeSpan.Zero) break;

                await signals.WaitAsync(ConnectorSignals.LiveFrame(job.Id), Slice(remaining), ct);
            }

            // Nothing newer than what the caller already has. Deliberately not
            // the current frame again: re-sending a picture the consumer is
            // already showing costs its bandwidth to change nothing.
            return Results.NoContent();
        });

        live.MapPost("/input", async (
            string provider,
            string sessionId,
            string challengeId,
            LiveInputBatch batch,
            IProviderRegistry registry,
            SessionService sessions,
            ChallengeService challenges,
            ViewBuilder views,
            LiveChannel channel,
            ConnectorSignals signals,
            TimeProvider time,
            CancellationToken ct) =>
        {
            var job = await RequireLiveJobAsync(
                provider, sessionId, challengeId, registry, sessions, challenges, views, time, ct);

            // Once, here, at the edge. Everything downstream - the queue, the
            // poll, the agent's dispatcher - is entitled to assume a batch it
            // holds is answerable, and a second opinion further in would be a
            // second definition of what a legal gesture is.
            if (!batch.IsWellFormed())
            {
                throw ConnectorException.InvalidRequest("this live input batch is not answerable");
            }

            channel.Enqueue(job.Id, batch);
            signals.Signal(ConnectorSignals.LiveInput(job.Id));

            // Accepted, not applied. Whether the page did anything with it is
            // the next frame's business, and claiming otherwise would be a
            // promise this side of the wire cannot keep.
            return Results.Accepted();
        });
    }

    /// <summary>
    /// The agent's half, inside the authenticated agent group and behind the
    /// same lease check as every other job-scoped agent route.
    /// </summary>
    public static void MapAgent(IEndpointRouteBuilder agents)
    {
        var live = agents.MapGroup("/jobs/{jobId}/live")
            .WithHttpLogging(HttpLoggingFields.None);

        live.MapPost("/frame", async (
            HttpContext http,
            string jobId,
            LiveChannel channel,
            ConnectorSignals signals,
            ConnectorDbContext db,
            CancellationToken ct) =>
        {
            var agent = http.RequireAgent();
            await AgentApiEndpoints.RequireLeasedAsync(db, jobId, agent.Id, ct);

            var sequence = LiveHeaders.RequireSequence(http.Request);
            var (width, height) = LiveHeaders.RequireSize(http.Request);
            var bytes = await LiveHeaders.ReadFrameAsync(http.Request, ct);

            var kept = channel.Publish(jobId, new LiveFrame
            {
                Sequence = sequence,
                Width = width,
                Height = height,
                Bytes = bytes,
            });

            // Only a frame that is actually now wakes the consumers waiting
            // for one; a POST that overtook a newer picture has nothing to
            // tell them.
            if (kept) signals.Signal(ConnectorSignals.LiveFrame(jobId));

            // 200 either way. A stale frame is the network's fault, not the
            // agent's, and an error here would make an agent retry a
            // photograph that is already out of date.
            return Results.Ok();
        });

        live.MapGet("/input", async (
            HttpContext http,
            string jobId,
            LiveChannel channel,
            ConnectorSignals signals,
            ConnectorDbContext db,
            IOptions<ConnectorOptions> options,
            TimeProvider time,
            CancellationToken ct) =>
        {
            var agent = http.RequireAgent();
            await AgentApiEndpoints.RequireLeasedAsync(db, jobId, agent.Id, ct);

            var after = LiveHeaders.After(http.Request);
            var deadline = time.GetUtcNow().AddSeconds(options.Value.Timeouts.AgentPollSeconds);

            while (!ct.IsCancellationRequested)
            {
                var pending = channel.Collect(jobId, after);
                if (pending.Events.Count > 0) return ConnectorResults.Json(pending);

                var remaining = deadline - time.GetUtcNow();
                if (remaining <= TimeSpan.Zero) break;

                await signals.WaitAsync(ConnectorSignals.LiveInput(jobId), Slice(remaining), ct);
            }

            return Results.NoContent();
        });
    }

    /// <summary>
    /// The run behind a live challenge, or a refusal.
    ///
    /// Resolved through the session rather than from the job id, exactly as
    /// the still relay's image route is: a challenge is looked up scoped to
    /// the session's own run, so one belonging to somebody else's login is
    /// simply not found rather than found and then judged.
    ///
    /// Three separate refusals, because they mean different things to the
    /// consumer. A challenge that is not a live view is a caller asking a
    /// still picture to be a stream. One that has been answered or has expired
    /// is gone: the live view's terminal marker is what the adapter's success
    /// signal writes, so refusing here is where the agent's latch stops being
    /// a property of one loop on one machine and becomes a fact the control
    /// plane enforces - after it, no further pixels leave and no further
    /// keystroke is accepted, whatever the frame loop is still doing.
    /// </summary>
    private static async Task<JobRow> RequireLiveJobAsync(
        string provider,
        string sessionId,
        string challengeId,
        IProviderRegistry registry,
        SessionService sessions,
        ChallengeService challenges,
        ViewBuilder views,
        TimeProvider time,
        CancellationToken ct)
    {
        var manifest = registry.RequireManifest(provider);
        var session = await sessions.RequireAsync(manifest.Id, sessionId, subject: null, ct);

        // A session that is no longer signing in has no live view, whatever
        // rows outlive it. Checked before the job lookup rather than after,
        // because a successful login is normally followed by a fetch - and
        // that fetch becomes the session's latest run, so the challenge is
        // simply not on it and the caller was told "unknown challenge", which
        // travels as `unsupported_resource`. A consumer renders that as "this
        // provider does not offer that", so a sign-in that had just worked
        // perfectly presented itself as a broken provider.
        if (session.State is not (SessionState.Queued or SessionState.Running or SessionState.AwaitingInput))
        {
            throw new ConnectorException(ErrorCode.ChallengeExpired, "this live view is over");
        }

        var job = await views.LatestJobAsync(session.Id, ct)
                  ?? throw ConnectorException.Unsupported("this session has no run in flight");

        var challenge = await challenges.FindAsync(challengeId, job.Id, ct)
                        ?? throw ConnectorException.Unsupported($"unknown challenge '{challengeId}'");

        if (challenge.Type != ChallengeType.LiveView)
        {
            throw ConnectorException.Unsupported($"challenge '{challengeId}' is not a live view");
        }

        if (challenge.AnsweredAt is not null || challenge.ExpiresAt <= time.GetUtcNow())
        {
            throw new ConnectorException(ErrorCode.ChallengeExpired, $"live view '{challengeId}' is over");
        }

        return job;
    }

    /// <summary>
    /// How long to sleep between checks when no signal arrives.
    ///
    /// Capped well under the poll window so a signal missed on a busy instance
    /// costs a fraction of a second rather than the whole wait - which at a
    /// dozen frames a second is the difference between a live view and a
    /// slideshow.
    /// </summary>
    private static TimeSpan Slice(TimeSpan remaining) =>
        remaining < Fallback ? remaining : Fallback;

    private static readonly TimeSpan Fallback = TimeSpan.FromMilliseconds(500);
}
