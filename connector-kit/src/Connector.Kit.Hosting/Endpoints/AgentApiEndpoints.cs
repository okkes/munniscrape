using Connector.Kit.AgentProtocol;
using Connector.Kit.Errors;
using Connector.Kit.Hosting.Auth;
using Connector.Kit.Hosting.Challenges;
using Connector.Kit.Hosting.Data;
using Connector.Kit.Hosting.Infrastructure;
using Connector.Kit.Hosting.Jobs;
using Connector.Kit.Jobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Connector.Kit.Hosting.Endpoints;

/// <summary>
/// The data plane's side of the wire.
///
/// Every call here is outbound from the agent - the control plane never
/// initiates a connection to one. That single property is what lets an agent
/// sit behind NAT on a residential line, in a home lab, or on a user's own
/// machine, and it is why bring-your-own hardware needed no redesign.
/// </summary>
internal static class AgentApiEndpoints
{
    public static void Map(IEndpointRouteBuilder root)
    {
        // Enrollment authenticates with the one-time code itself, so it sits
        // outside the token filter. Everything else is behind it.
        root.MapPost("/agent/v1/enroll", async (
            EnrollRequest request,
            AgentAuth auth,
            ConnectorDbContext db,
            IOptions<ConnectorOptions> options,
            TimeProvider time,
            CancellationToken ct) =>
        {
            var enrollment = await auth.RedeemEnrollmentAsync(request.Code, ct);
            var token = AgentAuth.NewToken();
            var now = time.GetUtcNow();

            var agent = new AgentRow
            {
                Id = Ids.New(Ids.Agent),
                Name = string.IsNullOrWhiteSpace(request.Name) ? enrollment.Name : request.Name,
                Class = request.Capabilities.Class,
                // The subject rides on the code, never on the request: an
                // agent may only ever serve the user who enrolled it, and
                // letting it name its own owner would undo that in one line.
                OwnerSubject = enrollment.Subject,
                CapabilitiesJson = ConnectorJson.Serialize(request.Capabilities),
                TokenHash = AgentAuth.Hash(token),
                LastHeartbeatAt = now,
                CreatedAt = now,
            };

            db.Agents.Add(agent);
            await db.SaveChangesAsync(ct);

            return ConnectorResults.Json(new EnrollResponse
            {
                AgentId = agent.Id,
                Token = token,
                HeartbeatSeconds = options.Value.Timeouts.HeartbeatSeconds,
            });
        }).AddEndpointFilter<ConnectorExceptionFilter>();

        var agents = root.MapGroup("/agent/v1")
            .AddEndpointFilter<ConnectorExceptionFilter>()
            .AddEndpointFilter<AgentAuthFilter>();

        agents.MapPost("/heartbeat", async (
            HttpContext http,
            HeartbeatRequest request,
            ConnectorDbContext db,
            IOptions<ConnectorOptions> options,
            TimeProvider time,
            CancellationToken ct) =>
        {
            var agent = http.RequireAgent();
            var now = time.GetUtcNow();

            agent.LastHeartbeatAt = now;
            agent.CapabilitiesJson = ConnectorJson.Serialize(request.Capabilities);
            agent.Class = request.Capabilities.Class;

            await ReconcileProfilesAsync(db, agent.Id, request.Profiles, ct);
            await db.SaveChangesAsync(ct);

            return ConnectorResults.Json(new HeartbeatResponse
            {
                LeaseTtlSeconds = options.Value.Timeouts.LeaseSeconds,
                Revoked = agent.Revoked,
            });
        });

        agents.MapPost("/jobs/lease", async (
            HttpContext http,
            LeaseRequest request,
            ILeasedJobQueue queue,
            ConnectorSignals signals,
            IOptions<ConnectorOptions> options,
            TimeProvider time,
            ConnectorDbContext db,
            CancellationToken ct) =>
        {
            var agent = http.RequireAgent();
            var capabilities = ConnectorJson.DeserializeOr(agent.CapabilitiesJson, new AgentCapabilities());

            // Which subject's work this agent is allowed to see. Null only for
            // the operator's own fleet, named in configuration - never decided
            // from anything the agent sends, because Class arrives on the
            // agent's own heartbeat and it would simply claim to be pooled.
            var ownerScope = agent.OwnerSubject is { } owner
                             && !options.Value.FleetSubjects.Contains(owner, StringComparer.Ordinal)
                ? owner
                : null;

            var ttl = TimeSpan.FromSeconds(options.Value.Timeouts.LeaseSeconds);
            var deadline = time.GetUtcNow().AddSeconds(options.Value.Timeouts.AgentPollSeconds);

            // A long poll rather than a fixed-interval pull: an agent that
            // polls every few seconds is both slower to start a job and
            // noisier, and the connection is already open either way.
            while (!ct.IsCancellationRequested)
            {
                var job = await queue.TryLeaseAsync(agent.Id, ownerScope, capabilities, request.Accept, ttl, ct);
                if (job is not null) return ConnectorResults.Json(job);

                var remaining = deadline - time.GetUtcNow();
                if (remaining <= TimeSpan.Zero) break;

                db.ChangeTracker.Clear();
                await signals.WaitAsync(ConnectorSignals.Queue,
                    remaining < TimeSpan.FromSeconds(2) ? remaining : TimeSpan.FromSeconds(2), ct);
            }

            return Results.NoContent();
        });

        agents.MapPost("/jobs/{jobId}/renew", async (
            HttpContext http,
            string jobId,
            ILeasedJobQueue queue,
            IOptions<ConnectorOptions> options,
            CancellationToken ct) =>
        {
            var agent = http.RequireAgent();
            var ttl = TimeSpan.FromSeconds(options.Value.Timeouts.LeaseSeconds);

            var renewed = await queue.RenewLeaseAsync(jobId, agent.Id, ttl, ct);
            return renewed is null
                ? throw ConnectorException.Unsupported($"job '{jobId}' is no longer leased to this agent")
                : ConnectorResults.Json(new RenewResponse { LeaseExpiresAt = renewed.Value });
        });

        agents.MapPost("/jobs/{jobId}/progress", async (
            HttpContext http,
            string jobId,
            ProgressReport report,
            ILeasedJobQueue queue,
            CancellationToken ct) =>
        {
            var agent = http.RequireAgent();
            await queue.ProgressAsync(jobId, agent.Id, report, ct);
            return Results.NoContent();
        });

        agents.MapPost("/jobs/{jobId}/challenge", async (
            HttpContext http,
            string jobId,
            AgentChallengeRequest request,
            ChallengeService challenges,
            ConnectorDbContext db,
            CancellationToken ct) =>
        {
            var agent = http.RequireAgent();
            await RequireLeasedAsync(db, jobId, agent.Id, ct);

            var row = await challenges.RaiseAsync(jobId, new PendingChallenge
            {
                Type = request.Type,
                ExpiresAt = request.ExpiresAt,
                Payload = new ChallengePayload
                {
                    AnswerKind = request.AnswerKind,
                    PromptKey = request.PromptKey,
                    Code = request.Code,
                    Delivery = request.Delivery,
                    Length = request.Length,
                    Options = request.Options,
                    Url = request.Url,
                    ReturnPattern = request.ReturnPattern,
                },
                Image = DecodeImage(request.ImageBase64),
            }, ct);

            return ConnectorResults.Json(new RaiseChallengeResponse { ChallengeId = row.Id });
        });

        agents.MapGet("/jobs/{jobId}/answer", async (
            HttpContext http,
            string jobId,
            ChallengeService challenges,
            ConnectorDbContext db,
            IOptions<ConnectorOptions> options,
            CancellationToken ct) =>
        {
            var agent = http.RequireAgent();
            await RequireLeasedAsync(db, jobId, agent.Id, ct);

            // The agent names the challenge it is waiting on. Optional so an
            // older agent keeps working, but without it a job with two open
            // questions hands back the answer to the wrong one.
            var waitingFor = http.Request.Query["challenge_id"].ToString();

            var window = TimeSpan.FromSeconds(options.Value.Timeouts.AgentPollSeconds);
            var answer = await challenges.AwaitAnswerAsync(
                jobId, window, ct, string.IsNullOrWhiteSpace(waitingFor) ? null : waitingFor);

            if (answer is not null) return ConnectorResults.Json(answer);

            // Nothing yet is not the same as expired. Only a challenge that
            // ran out of time is a 408; an open one just means poll again,
            // and telling the agent to fail the job would throw away a login
            // the human is still working on.
            var pending = await challenges.PendingAsync(jobId, ct);
            return pending is null
                ? ConnectorResults.Error(new ConnectorException(ErrorCode.MfaTimeout, "challenge expired unanswered"))
                : Results.NoContent();
        });

        // The live view's agent leg, in this group and behind this group's
        // auth: a streamed login is a job like any other, and its frames must
        // not be reachable by anything that could not already lease it.
        LiveEndpoints.MapAgent(agents);

        agents.MapPost("/jobs/{jobId}/result", async (
            HttpContext http,
            string jobId,
            JobResultRequest result,
            JobOutcomeService outcomes,
            CancellationToken ct) =>
        {
            var agent = http.RequireAgent();
            var outcome = await outcomes.SucceedAsync(jobId, agent.Id, result, ct);
            return ConnectorResults.Json(new AgentAckResponse { Cursor = outcome.Cursor });
        });

        agents.MapPost("/jobs/{jobId}/fail", async (
            HttpContext http,
            string jobId,
            JobFailRequest failure,
            JobOutcomeService outcomes,
            CancellationToken ct) =>
        {
            var agent = http.RequireAgent();
            var job = await outcomes.FailAsync(jobId, agent.Id, failure, ct);
            return ConnectorResults.Json(new AgentFailResponse { State = job.State });
        });
    }

    /// <summary>
    /// Profile rows follow what the agent reports. Rows for profiles it no
    /// longer has are marked unhealthy rather than deleted - a vanished
    /// profile is exactly what a user needs told about.
    /// </summary>
    private static async Task ReconcileProfilesAsync(
        ConnectorDbContext db, string agentId, IReadOnlyList<ProfileHealth> reported, CancellationToken ct)
    {
        var existing = await db.Profiles.Where(p => p.AgentId == agentId).ToListAsync(ct);
        var byId = existing.ToDictionary(p => p.Id, StringComparer.Ordinal);

        foreach (var profile in reported)
        {
            if (byId.TryGetValue(profile.Id, out var row))
            {
                row.Healthy = profile.Healthy;
                row.LastOkAt = profile.LastOk ?? row.LastOkAt;
                row.ProviderId = profile.Provider;
                continue;
            }

            db.Profiles.Add(new ProfileRow
            {
                Id = profile.Id,
                AgentId = agentId,
                ProviderId = profile.Provider,
                Healthy = profile.Healthy,
                LastOkAt = profile.LastOk,
            });
        }

        var reportedIds = reported.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var row in existing.Where(r => r.Healthy && !reportedIds.Contains(r.Id)))
        {
            row.Healthy = false;
        }
    }

    /// <summary>
    /// The one check every job-scoped agent route makes. Internal rather than
    /// private so the live routes call THIS one: a second copy of "is this job
    /// yours" is a second thing to get wrong, on the surface that carries live
    /// pixels of somebody's login page.
    /// </summary>
    internal static async Task RequireLeasedAsync(ConnectorDbContext db, string jobId, string agentId, CancellationToken ct)
    {
        var leased = await db.Jobs.AsNoTracking()
            .AnyAsync(j => j.Id == jobId && j.LeaseOwner == agentId, ct);

        if (!leased) throw ConnectorException.Unsupported($"job '{jobId}' is not leased to this agent");
    }

    private static byte[]? DecodeImage(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;

        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            throw ConnectorException.InvalidRequest("image_base64 is not valid base64");
        }
    }
}

public sealed record AgentAckResponse
{
    public required string Cursor { get; init; }
}

public sealed record AgentFailResponse
{
    public required JobState State { get; init; }
}
