using System.Text.Json.Nodes;
using Connector.Kit.Adapters;
using Connector.Kit.Errors;
using Connector.Kit.Hosting.Data;
using Connector.Kit.Hosting.Infrastructure;
using Connector.Kit.Hosting.Jobs;
using Connector.Kit.Hosting.Providers;
using Connector.Kit.Hosting.Staging;
using Connector.Kit.Hosting.Sessions;
using Connector.Kit.Hosting.Tickets;
using Connector.Kit.Jobs;
using Connector.Kit.Manifests;
using Connector.Kit.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Connector.Kit.Hosting.Endpoints;

/// <summary>
/// The user's requested shape, served by exactly one handler:
/// <c>GET /v1/jumbo/receipts?since=…&amp;include=items</c>.
///
/// A fetch is a job, so it can take a minute and it can stop to ask a
/// question - a T3 provider re-authenticates on every run. The response is
/// therefore one of three: the data, a job handle, or a challenge. All three
/// are the same code path; only how far it got before the window closed
/// differs.
/// </summary>
internal static class ResourceEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/{provider}/{resource}", async (
            HttpContext http,
            string provider,
            string resource,
            ITicketStore tickets,
            FetchRunner runner,
            CancellationToken ct) =>
        {
            var ticket = RequestContext.RequireTicket(http);
            if (!tickets.TryRedeem(ticket, provider, out var grant))
            {
                throw ConnectorException.SessionExpired("ticket is unknown or expired");
            }

            return await runner.RunAsync(http, provider, resource, RequestContext.Query(http), grant, ct);
        })
        .Produces<DataResponse>(StatusCodes.Status200OK)
        .Produces<JobAcceptedResponse>(StatusCodes.Status202Accepted);

        // The one-shot form, for a caller that wants a single round trip and
        // does not need a ticket. Everything after the bundle is opened is the
        // identical path.
        api.MapPost("/{provider}/{resource}:fetch", async (
            HttpContext http,
            string provider,
            string resource,
            OneShotFetchRequest request,
            IProviderRegistry registry,
            SessionService sessions,
            FetchRunner runner,
            IOptions<ConnectorOptions> options,
            TimeProvider time,
            CancellationToken ct) =>
        {
            var manifest = registry.RequireManifest(provider);
            var opened = await sessions.OpenAsync(manifest.Id, request.Subject, request.Bundle, ct);

            var grant = new TicketGrant
            {
                Subject = request.Subject,
                SessionId = opened.Session.Id,
                ProviderId = manifest.Id,
                Material = opened.Payload.Material,
                Config = opened.Payload.Config,
                ExpiresAt = time.GetUtcNow().AddSeconds(options.Value.Timeouts.TicketSeconds),
            };

            return await runner.RunAsync(http, provider, resource, Flatten(request.Params), grant, ct);
        })
        // The two outcomes a fetch has, and the reason FetchRunner.RunAsync
        // stays Task<IResult>: which one you get depends on how far the job got
        // before the window closed, so no single static claim can be true.
        .Produces<DataResponse>(StatusCodes.Status200OK)
        .Produces<JobAcceptedResponse>(StatusCodes.Status202Accepted);

        api.MapPost("/{provider}/{resource}/ack", async (
            HttpContext http,
            string provider,
            string resource,
            AckRequest request,
            IProviderRegistry registry,
            ITicketStore tickets,
            ResultService results,
            CancellationToken ct) =>
        {
            var manifest = registry.RequireManifest(provider);
            RequestContext.StampManifestVersion(http, manifest.ManifestVersion);

            var ticket = RequestContext.RequireTicket(http);
            if (!tickets.TryRedeem(ticket, provider, out var grant))
            {
                throw ConnectorException.SessionExpired("ticket is unknown or expired");
            }

            if (manifest.Resource(resource) is null)
            {
                throw ConnectorException.Unsupported($"provider '{manifest.Id}' has no resource '{resource}'");
            }

            var purged = await results.AckAsync(grant.SessionId, resource, request.Cursor, ct);
            return ConnectorResults.Json(new AckResponse { Purged = purged });
        });
    }

    /// <summary>
    /// The one-shot body carries params as JSON, where a list is a real array
    /// rather than a comma-joined string. Flattening to the query-string shape
    /// means one validator sees both forms and they cannot diverge.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Flatten(IReadOnlyDictionary<string, JsonNode?> body)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var (key, node) in body)
        {
            switch (node)
            {
                case null:
                    continue;
                case JsonArray array:
                    result[key] = [.. array.Where(v => v is not null).Select(v => v!.ToString())];
                    break;
                default:
                    result[key] = [node.ToString()];
                    break;
            }
        }

        return result;
    }
}

public sealed record AckResponse
{
    public required int Purged { get; init; }
}

/// <summary>
/// The shared body of every fetch: validate, enqueue, wait a little, and
/// answer with whatever is true by then.
/// </summary>
public sealed class FetchRunner(
    IProviderRegistry registry,
    ProviderStatusService statuses,
    ILeasedJobQueue queue,
    IInlineJobRunner inline,
    ConnectorDbContext db,
    ViewBuilder views,
    ResultService results,
    ConnectorSignals signals,
    IOptions<ConnectorOptions> options,
    TimeProvider time)
{
    public async Task<IResult> RunAsync(
        HttpContext http,
        string providerId,
        string resourceId,
        IReadOnlyDictionary<string, IReadOnlyList<string>> query,
        TicketGrant grant,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(grant);

        var manifest = registry.RequireManifest(providerId);
        RequestContext.StampManifestVersion(http, manifest.ManifestVersion);

        var status = await statuses.GetAsync(manifest.Id, ct);
        if (!status.AcceptsWork)
        {
            throw new ConnectorException(ErrorCode.ProviderUnavailable, $"provider '{manifest.Id}' is {status.State}");
        }

        var resource = manifest.Resource(resourceId)
                       ?? throw ConnectorException.Unsupported($"provider '{manifest.Id}' has no resource '{resourceId}'");

        var request = ParamBinder.Bind(manifest, resource, query, time.GetUtcNow());

        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == grant.SessionId, ct)
                      ?? throw ConnectorException.SessionExpired("session no longer exists");

        var job = await queue.EnqueueAsync(new NewJob
        {
            SessionId = session.Id,
            ProviderId = manifest.Id,
            Kind = JobKind.Fetch,
            ResourceId = resource.Id,
            Request = request,
            Config = MergeConfig(session, grant),
            Material = grant.Material,
            ProfileId = session.ProfileId,
        }, ct);

        if (inline.CanRun(manifest)) inline.Dispatch();

        var window = TimeSpan.FromSeconds(Math.Min(
            options.Value.Timeouts.FetchWaitSeconds,
            Math.Max(5, resource.TypicalDurationSeconds)));

        await WaitAsync(job.Id, window, ct);

        db.ChangeTracker.Clear();
        var settled = await db.Jobs.FirstAsync(j => j.Id == job.Id, ct);

        switch (settled.State)
        {
            case JobState.Succeeded:
            {
                var page = await results.ReadAsync(settled.Id, ct);
                var rotated = await CollectBundleAsync(settled.SessionId, ct);

                return ConnectorResults.Json(new DataResponse
                {
                    Resource = resource.Id,
                    Data = page.Data,
                    Cursor = page.Cursor,
                    Complete = settled.Complete,
                    Session = rotated is null ? null : new SessionBundle { Bundle = rotated, Rotated = true },
                });
            }

            case JobState.Failed or JobState.Expired:
                throw new ConnectorException(settled.ErrorCode ?? ErrorCode.Internal, settled.ErrorDetail);

            default:
                return ConnectorResults.Json(await views.AcceptedAsync(settled, ct), StatusCodes.Status202Accepted);
        }
    }

    /// <summary>
    /// The bundle's config wins where the two disagree: it is what the user
    /// actually connected with, and the session row is a convenience copy.
    /// </summary>
    private static IReadOnlyDictionary<string, string> MergeConfig(SessionRow session, TicketGrant grant)
    {
        var merged = new Dictionary<string, string>(
            ConnectorJson.DeserializeOr<IReadOnlyDictionary<string, string>>(
                session.ConfigJson, new Dictionary<string, string>(StringComparer.Ordinal)),
            StringComparer.Ordinal);

        foreach (var (key, value) in grant.Config) merged[key] = value;
        return merged;
    }

    private async Task<string?> CollectBundleAsync(string sessionId, CancellationToken ct)
    {
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session?.PendingBundle is not { } bundle) return null;

        session.PendingBundle = null;
        session.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return bundle;
    }

    private async Task WaitAsync(string jobId, TimeSpan window, CancellationToken ct)
    {
        var deadline = time.GetUtcNow() + window;

        while (time.GetUtcNow() < deadline && !ct.IsCancellationRequested)
        {
            db.ChangeTracker.Clear();
            var state = await db.Jobs.Where(j => j.Id == jobId).Select(j => j.State).FirstOrDefaultAsync(ct);

            if (state is JobState.Succeeded or JobState.Failed or JobState.Expired or JobState.AwaitingInput) return;

            var remaining = deadline - time.GetUtcNow();
            if (remaining <= TimeSpan.Zero) return;

            await signals.WaitAsync(ConnectorSignals.Job(jobId),
                remaining < TimeSpan.FromMilliseconds(250) ? remaining : TimeSpan.FromMilliseconds(250), ct);
        }
    }
}
