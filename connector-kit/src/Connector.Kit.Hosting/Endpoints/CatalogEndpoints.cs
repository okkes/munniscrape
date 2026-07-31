using System.Text.Json;
using System.Text.Json.Nodes;
using Connector.Kit.Adapters;
using Connector.Kit.Hosting.Data;
using Connector.Kit.Hosting.Infrastructure;
using Connector.Kit.Hosting.Providers;
using Connector.Kit.Hosting.Sessions;
using Connector.Kit.Jobs;
using Connector.Kit.Manifests;
using Connector.Kit.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Connector.Kit.Hosting.Endpoints;

/// <summary>
/// The catalogue, health and the kill switch.
///
/// The catalogue is the only contract a consumer codes against: it renders
/// login forms from it, decides which resources to offer from it, and knows
/// before asking whether scheduled sync is offerable. Its digest is an ETag
/// so a consumer can cache it and revalidate instead of refetching a document
/// that changes on a deploy at most.
/// </summary>
internal static class CatalogEndpoints
{
    public static void Map(IEndpointRouteBuilder api, IEndpointRouteBuilder root, ConnectorPlatformOptions platform)
    {
        api.MapGet("/providers", async (
            HttpContext http,
            IProviderRegistry registry,
            ProviderStatusService statuses,
            CancellationToken ct) =>
        {
            var etag = $"\"{registry.CatalogDigest}\"";
            if (NotModified(http, etag)) return Results.StatusCode(StatusCodes.Status304NotModified);

            http.Response.Headers.ETag = etag;

            var health = await statuses.AllAsync(ct);
            var providers = registry.Manifests.Select(m => Describe(m, health.GetValueOrDefault(m.Id))).ToList();

            return ConnectorResults.Json(new CatalogResponse
            {
                Providers = providers,
                Service = Descriptor(platform, registry),
            });
        })
        // Named here rather than inferred: this handler also answers 304, so
        // its declared type is the bare IResult the two branches share and the
        // funnel's own claim never reaches the document.
        .Produces<CatalogResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status304NotModified);

        api.MapGet("/providers/{id}", async (
            HttpContext http,
            string id,
            IProviderRegistry registry,
            ProviderStatusService statuses,
            CancellationToken ct) =>
        {
            var manifest = registry.RequireManifest(id);
            RequestContext.StampManifestVersion(http, manifest.ManifestVersion);

            var etag = $"\"{registry.CatalogDigest}:{manifest.Id}\"";
            if (NotModified(http, etag)) return Results.StatusCode(StatusCodes.Status304NotModified);

            http.Response.Headers.ETag = etag;
            return ConnectorResults.Json(Describe(manifest, await statuses.GetAsync(manifest.Id, ct)));
        })
        // A free-form object, exactly as CatalogResponse.Providers already
        // declares one: the manifest is serialised through a node so the
        // document's shape stays the spec's, and a wrapper type here would
        // describe our storage rather than the contract.
        .Produces<JsonObject>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status304NotModified);

        // Liveness carries no auth and no data: a probe that needs a
        // credential is a probe that fails for the wrong reason.
        root.MapGet("/v1/health", () => ConnectorResults.Json(new HealthResponse
        {
            Status = "ok",
            Version = platform.ServiceVersion,
        }));

        api.MapGet("/status", async (
            IProviderRegistry registry,
            ProviderStatusService statuses,
            ConnectorDbContext db,
            TimeProvider time,
            CancellationToken ct) =>
        {
            var health = await statuses.AllAsync(ct);
            var since = time.GetUtcNow().AddSeconds(-90);

            var agents = await db.Agents.AsNoTracking().ToListAsync(ct);
            var queued = await db.Jobs.CountAsync(j => j.State == JobState.Queued, ct);
            var running = await db.Jobs.CountAsync(j => j.State == JobState.Leased || j.State == JobState.Running, ct);
            var awaiting = await db.Jobs.CountAsync(j => j.State == JobState.AwaitingInput, ct);

            return ConnectorResults.Json(new StatusResponse
            {
                Service = Descriptor(platform, registry),
                Providers = [.. health.Values.OrderBy(p => p.ProviderId, StringComparer.Ordinal)],
                Agents = new AgentPoolView
                {
                    Total = agents.Count,
                    Online = agents.Count(a => !a.Revoked && a.LastHeartbeatAt > since),
                    Revoked = agents.Count(a => a.Revoked),
                },
                Queue = new QueueView { Queued = queued, Running = running, AwaitingInput = awaiting },
            });
        });

        // The kill switch. Pausing a provider stops new work for every user of
        // it within one lease poll, and lets the consumer say something true
        // instead of showing a spinner.
        api.MapPost("/admin/providers/{id}/status", async (
            string id,
            ProviderStatusUpdate update,
            ProviderStatusService statuses,
            SessionService sessions,
            ConnectorDbContext db,
            CancellationToken ct) =>
        {
            var status = await statuses.SetAsync(id, update.State, update.ReasonKey, ct);

            if (update.State == ProviderState.Retired)
            {
                // Retired means gone for good, so existing sessions are not
                // merely paused - they are invalid, and saying so now beats
                // failing every one of them individually later.
                var live = await db.Sessions
                    .Where(s => s.ProviderId == id && s.State != SessionState.Disabled && s.State != SessionState.Expired)
                    .ToListAsync(ct);

                foreach (var session in live)
                {
                    await sessions.TransitionOrTerminateAsync(session, SessionState.Expired, ct);
                }
            }

            return ConnectorResults.Json(status);
        });
    }

    /// <summary>
    /// The manifest exactly as the kit defines it, with its health grafted on.
    ///
    /// Serialising through a node rather than a wrapper type keeps the
    /// document's shape identical to the schema in the spec - a consumer
    /// should never have to unwrap our storage decisions to read a manifest.
    /// </summary>
    private static JsonObject Describe(ProviderManifest manifest, ProviderStatus? status)
    {
        var node = JsonSerializer.SerializeToNode(manifest, ConnectorJson.Options)!.AsObject();
        if (status is not null)
        {
            node["status"] = JsonSerializer.SerializeToNode(status, ConnectorJson.Options);
        }

        return node;
    }

    private static ServiceDescriptor Descriptor(ConnectorPlatformOptions platform, IProviderRegistry registry) => new()
    {
        Kind = platform.ServiceKind,
        Version = platform.ServiceVersion,
        ManifestDigest = registry.CatalogDigest,
    };

    private static bool NotModified(HttpContext http, string etag)
    {
        var presented = http.Request.Headers.IfNoneMatch;
        foreach (var candidate in presented)
        {
            if (candidate is null) continue;
            if (candidate == "*" || candidate.Split(',').Any(c => c.Trim() == etag)) return true;
        }

        return false;
    }
}
