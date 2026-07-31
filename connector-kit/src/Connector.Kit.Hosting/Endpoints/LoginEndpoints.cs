using Connector.Kit.Adapters;
using Connector.Kit.AgentProtocol;
using Connector.Kit.Errors;
using Connector.Kit.Hosting.Challenges;
using Connector.Kit.Hosting.Data;
using Connector.Kit.Hosting.Infrastructure;
using Connector.Kit.Hosting.Jobs;
using Connector.Kit.Hosting.Providers;
using Connector.Kit.Hosting.Sessions;
using Connector.Kit.Hosting.Tickets;
using Connector.Kit.Jobs;
using Connector.Kit.Manifests;
using Connector.Kit.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Connector.Kit.Hosting.Endpoints;

/// <summary>
/// Connecting, driving an interactive login, and disconnecting.
///
/// One generic handler for every provider. Adding a provider is a manifest
/// plus an adapter, never a controller - which is the only way a consumer can
/// render a login form it has never seen and have it work.
/// </summary>
internal static class LoginEndpoints
{
    public static void Map(IEndpointRouteBuilder api, ConnectorPlatformOptions platform)
    {
        api.MapPost("/{provider}/login", async (
            HttpContext http,
            string provider,
            LoginRequest request,
            IProviderRegistry registry,
            ProviderStatusService statuses,
            SessionService sessions,
            ILeasedJobQueue queue,
            IInlineJobRunner inline,
            ConnectorDbContext db,
            ViewBuilder views,
            ConnectorSignals signals,
            IIdempotencyStore idempotency,
            IOptions<ConnectorOptions> options,
            TimeProvider time,
            CancellationToken ct) =>
        {
            var manifest = registry.RequireManifest(provider);
            RequestContext.StampManifestVersion(http, manifest.ManifestVersion);

            await RequireWorkAcceptedAsync(statuses, manifest, ct);
            var deviceClass = RequestContext.DeviceClassOf(http);
            RequireConsent(platform, request.Consent);

            AuthInputValidator.ValidateConfig(manifest, request.Config);
            AuthInputValidator.ValidateInputs(manifest, request.Inputs);

            // A caller that timed out and retried gets the run it already
            // started, not a second one. Starting a second login would submit
            // the same credential again and spend one of the provider's
            // attempts on a request the user only made once.
            var idempotencyScope = $"login:{manifest.Id}:{request.Subject}";
            var idempotencyKey = RequestContext.IdempotencyKey(http);
            if (idempotencyKey is not null && idempotency.TryGet(idempotencyScope, idempotencyKey, out var replayed))
            {
                var already = await sessions.RequireAsync(manifest.Id, replayed, request.Subject, ct);
                var replay = await views.SessionAsync(already, deliverBundle: true, ct);
                return ConnectorResults.Json(replay, already.State == SessionState.Active
                    ? StatusCodes.Status200OK
                    : StatusCodes.Status202Accepted);
            }

            var profileId = await ResolveProfileAsync(
                db, manifest, request.PreferAgent, request.Subject, options.Value, ct);

            var session = await sessions.CreateAsync(new NewSession
            {
                ProviderId = manifest.Id,
                Subject = request.Subject,
                DeviceClass = deviceClass,
                Config = request.Config,
                Label = request.Label,
                ConsentAcceptedAt = request.Consent?.AcceptedAt,
                ConsentTermsVersion = request.Consent?.TermsVersion,
                PreferAgent = request.PreferAgent,
                ProfileId = profileId,
            }, ct);

            if (idempotencyKey is not null) idempotency.Remember(idempotencyScope, idempotencyKey, session.Id);

            var job = await queue.EnqueueAsync(new NewJob
            {
                SessionId = session.Id,
                ProviderId = manifest.Id,
                Kind = JobKind.Login,
                Inputs = request.Inputs,
                Config = request.Config,
                ProfileId = profileId,
            }, ct);

            await sessions.TransitionAsync(session, SessionState.Running, ct);
            if (inline.CanRun(manifest)) inline.Dispatch();

            // A short wait, not a long one. An HTTP-tier provider usually
            // finishes inside it and the caller gets its bundle in one round
            // trip; anything slower has SSE and a poll URL, and holding a
            // socket open for a two-minute bank login helps nobody.
            await WaitForSettleAsync(db, session.Id, signals,
                TimeSpan.FromSeconds(options.Value.Timeouts.LoginWaitSeconds), time, ct);

            db.ChangeTracker.Clear();
            var settled = await db.Sessions.FirstAsync(s => s.Id == session.Id, ct);
            var view = await views.SessionAsync(settled, deliverBundle: true, ct);

            if (settled.State == SessionState.Active) return ConnectorResults.Json(view);

            if (SessionStateMachine.IsTerminal(settled.State) || settled.State == SessionState.Blocked)
            {
                var failed = await views.LatestJobAsync(settled.Id, ct);
                return ConnectorResults.Error(new ConnectorException(
                    failed?.ErrorCode ?? ErrorCode.Internal, failed?.ErrorDetail));
            }

            return ConnectorResults.Json(view, StatusCodes.Status202Accepted);
        })
        // The same body under two statuses, which is the contract: 200 means
        // the bundle is in your hands, 202 means poll or subscribe. A consumer
        // that only reads the 200 shape never learns the second exists.
        .Produces<SessionResponse>(StatusCodes.Status200OK)
        .Produces<SessionResponse>(StatusCodes.Status202Accepted);

        api.MapGet("/{provider}/login/{sessionId}", async (
            HttpContext http,
            string provider,
            string sessionId,
            IProviderRegistry registry,
            SessionService sessions,
            ViewBuilder views,
            CancellationToken ct) =>
        {
            var manifest = registry.RequireManifest(provider);
            RequestContext.StampManifestVersion(http, manifest.ManifestVersion);

            var session = await sessions.RequireAsync(manifest.Id, sessionId, subject: null, ct);
            return ConnectorResults.Json(await views.SessionAsync(session, deliverBundle: true, ct));
        });

        api.MapGet("/{provider}/login/{sessionId}/events", async (
            HttpContext http,
            string provider,
            string sessionId,
            IProviderRegistry registry,
            SessionService sessions,
            ViewBuilder views,
            ConnectorSignals signals,
            ConnectorDbContext db,
            CancellationToken ct) =>
        {
            var manifest = registry.RequireManifest(provider);
            var session = await sessions.RequireAsync(manifest.Id, sessionId, subject: null, ct);

            await EventStream.WriteAsync(
                http,
                async token =>
                {
                    db.ChangeTracker.Clear();
                    var row = await db.Sessions.FirstOrDefaultAsync(s => s.Id == session.Id, token);
                    // The stream never hands over the bundle: a stream cannot
                    // be acknowledged, and a bundle delivered into one that
                    // nobody read would be lost silently.
                    return row is null ? null : await views.SessionAsync(row, deliverBundle: false, token);
                },
                ConnectorJson.Serialize,
                view => SessionStateMachine.IsTerminal(view.State) || view.State == SessionState.Active,
                signals,
                ConnectorSignals.Session(session.Id),
                TimeSpan.FromMinutes(10),
                ct);

            return Results.Empty;
        })
        // A stream of the same view the poll returns, one per event. Naming the
        // frame type is the only thing that makes the stream readable: the
        // status alone would say a session subscription returns nothing.
        .Produces<SessionResponse>(StatusCodes.Status200OK, "text/event-stream");

        api.MapGet("/{provider}/login/{sessionId}/challenges/{challengeId}/image", async (
            string provider,
            string sessionId,
            string challengeId,
            IProviderRegistry registry,
            SessionService sessions,
            ChallengeService challenges,
            ViewBuilder views,
            CancellationToken ct) =>
        {
            var manifest = registry.RequireManifest(provider);
            var session = await sessions.RequireAsync(manifest.Id, sessionId, subject: null, ct);

            var job = await views.LatestJobAsync(session.Id, ct)
                      ?? throw ConnectorException.Unsupported("this session has no run in flight");

            var bytes = await challenges.ImageAsync(challengeId, job.Id, ct);
            return bytes is null
                ? ConnectorResults.Error(new ConnectorException(ErrorCode.ChallengeExpired, "no image for this challenge"))
                : Results.File(bytes, "image/png");
        })
        .Produces<byte[]>(StatusCodes.Status200OK, "image/png");

        api.MapPost("/{provider}/login/{sessionId}/answer", async (
            string provider,
            string sessionId,
            AnswerRequest request,
            IProviderRegistry registry,
            SessionService sessions,
            ChallengeService challenges,
            ViewBuilder views,
            CancellationToken ct) =>
        {
            var manifest = registry.RequireManifest(provider);
            var session = await sessions.RequireAsync(manifest.Id, sessionId, subject: null, ct);

            var job = await views.LatestJobAsync(session.Id, ct)
                      ?? throw ConnectorException.Unsupported("this session has no run in flight");

            await challenges.AnswerAsync(request.ChallengeId, job.Id, request.Value, ct);
            return ConnectorResults.Json(await views.SessionAsync(session, deliverBundle: false, ct));
        });

        api.MapPost("/{provider}/login/{sessionId}/cancel", async (
            string provider,
            string sessionId,
            IProviderRegistry registry,
            SessionService sessions,
            JobOutcomeService outcomes,
            ViewBuilder views,
            CancellationToken ct) =>
        {
            var manifest = registry.RequireManifest(provider);
            var session = await sessions.RequireAsync(manifest.Id, sessionId, subject: null, ct);

            if (await views.LatestJobAsync(session.Id, ct) is { } job && !JobStateMachine.IsTerminal(job.State))
            {
                await outcomes.FailAsync(job.Id, leaseOwner: null, new JobFailRequest
                {
                    // Not retriable by construction: a cancel that came back
                    // as a retry would be the opposite of what was asked.
                    Code = ErrorCatalog.Wire(ErrorCode.InvalidRequest),
                    Detail = "cancelled by the caller",
                }, ct);
            }

            var cancelled = await sessions.RequireAsync(manifest.Id, sessionId, subject: null, ct);
            return ConnectorResults.Json(await views.SessionAsync(cancelled, deliverBundle: false, ct));
        });

        api.MapPost("/{provider}/sessions/resume", async (
            HttpContext http,
            string provider,
            ResumeRequest request,
            IProviderRegistry registry,
            ProviderStatusService statuses,
            SessionService sessions,
            ITicketStore tickets,
            IOptions<ConnectorOptions> options,
            TimeProvider time,
            CancellationToken ct) =>
        {
            var manifest = registry.RequireManifest(provider);
            RequestContext.StampManifestVersion(http, manifest.ManifestVersion);
            await RequireWorkAcceptedAsync(statuses, manifest, ct);

            var opened = await sessions.OpenAsync(manifest.Id, request.Subject, request.Bundle, ct);
            var ttl = options.Value.Timeouts.TicketSeconds;

            var ticket = tickets.Mint(new TicketGrant
            {
                Subject = request.Subject,
                SessionId = opened.Session.Id,
                ProviderId = manifest.Id,
                Material = opened.Payload.Material,
                Config = opened.Payload.Config,
                ExpiresAt = time.GetUtcNow().AddSeconds(ttl),
            });

            return ConnectorResults.Json(new ResumeResponse
            {
                Ticket = ticket,
                SessionId = opened.Session.Id,
                ExpiresIn = ttl,
                State = opened.Session.State,
            });
        });

        api.MapDelete("/{provider}/sessions/{sessionId}", async (
            HttpContext http,
            string provider,
            string sessionId,
            IProviderRegistry registry,
            SessionService sessions,
            ILeasedJobQueue queue,
            IInlineJobRunner inline,
            ConnectorDbContext db,
            CancellationToken ct) =>
        {
            var manifest = registry.RequireManifest(provider);
            RequestContext.StampManifestVersion(http, manifest.ManifestVersion);

            var session = await sessions.RequireAsync(manifest.Id, sessionId, subject: null, ct);

            // Best-effort upstream logout, then purge regardless. A user
            // disconnecting must always succeed locally, whatever the provider
            // does or does not do about it.
            //
            // Gated on the manifest now: fourteen of the sixteen adapters
            // inherit the interface's do-nothing default, and each of those
            // Disconnects was minting a job row, taking a lease and spending a
            // whole agent round trip to reach a method that returns a completed
            // task. Declaring it also lets the consumer stop telling every user
            // it "logged out upstream" when for most providers nothing left the
            // building.
            if (session.State == SessionState.Active && manifest.Logout != LogoutSupport.None)
            {
                await queue.EnqueueAsync(new NewJob
                {
                    SessionId = session.Id,
                    ProviderId = manifest.Id,
                    Kind = JobKind.Logout,
                    Config = ConnectorJson.DeserializeOr<IReadOnlyDictionary<string, string>>(
                        session.ConfigJson, new Dictionary<string, string>(StringComparer.Ordinal)),
                    ProfileId = session.ProfileId,
                }, ct);

                if (inline.CanRun(manifest)) inline.Dispatch();
            }

            await sessions.PurgeAsync(session, ct);
            db.ChangeTracker.Clear();

            return Results.NoContent();
        })
        // 204, and the document said 200 until this line existed - the default
        // inference claims a 200 for any handler it cannot read, so a route
        // that never returns one was documented as if it did.
        .Produces(StatusCodes.Status204NoContent);
    }

    private static async Task RequireWorkAcceptedAsync(ProviderStatusService statuses, ProviderManifest manifest, CancellationToken ct)
    {
        var status = await statuses.GetAsync(manifest.Id, ct);
        if (status.AcceptsWork) return;

        throw new ConnectorException(ErrorCode.ProviderUnavailable,
            $"provider '{manifest.Id}' is {status.State}");
    }

    /// <summary>
    /// Consent is checked here or nowhere: an adapter cannot know what terms
    /// the consumer showed, and a stale acceptance is a legal problem long
    /// before it is a technical one.
    /// </summary>
    private static void RequireConsent(ConnectorPlatformOptions platform, ConsentRecord? consent)
    {
        if (!platform.RequireConsent) return;

        if (consent?.AcceptedAt is null)
        {
            throw new ConnectorException(ErrorCode.ConsentExpired, "no recorded consent");
        }

        if (platform.ConsentTermsVersion is { } required &&
            !string.Equals(consent.TermsVersion, required, StringComparison.Ordinal))
        {
            throw new ConnectorException(ErrorCode.ConsentExpired, "consent predates the current terms");
        }
    }

    /// <summary>
    /// T4 routing. A persistent login lives on one machine, so the session is
    /// pinned to a profile on it before any job exists - otherwise the first
    /// job would be offered to whichever agent asked first, and none of them
    /// but one can serve it.
    /// </summary>
    private static async Task<string?> ResolveProfileAsync(
        ConnectorDbContext db,
        ProviderManifest manifest,
        string? preferAgent,
        string subject,
        ConnectorOptions options,
        CancellationToken ct)
    {
        if (manifest.Runtime != ProviderRuntime.BrowserPersistent) return null;

        if (string.IsNullOrWhiteSpace(preferAgent))
        {
            throw ConnectorException.InvalidRequest(
                $"provider '{manifest.Id}' needs a persistent profile; name the agent with prefer_agent");
        }

        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == preferAgent && !a.Revoked, ct)
                    ?? throw new ConnectorException(ErrorCode.AgentUnavailable, $"unknown agent '{preferAgent}'");

        // Naming somebody else's machine must not pin a session to it. The
        // same message as an agent that does not exist, deliberately: whether
        // a given agent id belongs to another user is not this caller's to
        // learn.
        if (agent.OwnerSubject is { } owner
            && !string.Equals(owner, subject, StringComparison.Ordinal)
            && !options.FleetSubjects.Contains(owner, StringComparer.Ordinal))
        {
            throw new ConnectorException(ErrorCode.AgentUnavailable, $"unknown agent '{preferAgent}'");
        }

        var existing = await db.Profiles.FirstOrDefaultAsync(
            p => p.AgentId == agent.Id && p.ProviderId == manifest.Id && p.SessionId == null, ct);

        if (existing is not null) return existing.Id;

        var profile = new ProfileRow
        {
            Id = Ids.New(Ids.Profile),
            AgentId = agent.Id,
            ProviderId = manifest.Id,
            // Not healthy until the agent says so on a heartbeat: claiming a
            // profile works before it exists produces a job that routes
            // somewhere real and then fails there.
            Healthy = false,
            LastOkAt = null,
        };

        db.Profiles.Add(profile);
        await db.SaveChangesAsync(ct);
        return profile.Id;
    }

    /// <summary>
    /// Waits for the session to reach a state worth reporting. Signal-driven
    /// with a bounded fallback, so a run that finishes in 40ms answers in
    /// 40ms and one that stalls still answers on time.
    /// </summary>
    private static async Task WaitForSettleAsync(
        ConnectorDbContext db, string sessionId, ConnectorSignals signals, TimeSpan window, TimeProvider time, CancellationToken ct)
    {
        var deadline = time.GetUtcNow() + window;

        while (time.GetUtcNow() < deadline && !ct.IsCancellationRequested)
        {
            db.ChangeTracker.Clear();
            var state = await db.Sessions.Where(s => s.Id == sessionId).Select(s => s.State).FirstOrDefaultAsync(ct);

            if (state is SessionState.Active or SessionState.AwaitingInput or SessionState.Failed
                or SessionState.Blocked or SessionState.Expired)
            {
                return;
            }

            var remaining = deadline - time.GetUtcNow();
            if (remaining <= TimeSpan.Zero) return;

            await signals.WaitAsync(ConnectorSignals.Session(sessionId),
                remaining < TimeSpan.FromMilliseconds(250) ? remaining : TimeSpan.FromMilliseconds(250), ct);
        }
    }
}
