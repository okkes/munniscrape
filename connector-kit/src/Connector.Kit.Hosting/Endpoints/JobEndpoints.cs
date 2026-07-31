using Connector.Kit.Adapters;
using Connector.Kit.Errors;
using Connector.Kit.Hosting.Challenges;
using Connector.Kit.Hosting.Data;
using Connector.Kit.Hosting.Infrastructure;
using Connector.Kit.Jobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Connector.Kit.Hosting.Endpoints;

/// <summary>
/// Following a fetch that did not finish inside its window. Identical
/// contract to a login: poll, subscribe, answer.
/// </summary>
internal static class JobEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/{provider}/jobs/{jobId}", async (
            HttpContext http,
            string provider,
            string jobId,
            IProviderRegistry registry,
            ConnectorDbContext db,
            ViewBuilder views,
            CancellationToken ct) =>
        {
            var manifest = registry.RequireManifest(provider);
            RequestContext.StampManifestVersion(http, manifest.ManifestVersion);

            var job = await RequireAsync(db, manifest.Id, jobId, ct);
            return ConnectorResults.Json(await views.JobAsync(job, deliverBundle: true, ct));
        });

        api.MapGet("/{provider}/jobs/{jobId}/events", async (
            HttpContext http,
            string provider,
            string jobId,
            IProviderRegistry registry,
            ConnectorDbContext db,
            ViewBuilder views,
            ConnectorSignals signals,
            CancellationToken ct) =>
        {
            var manifest = registry.RequireManifest(provider);
            var job = await RequireAsync(db, manifest.Id, jobId, ct);

            await EventStream.WriteAsync(
                http,
                async token =>
                {
                    db.ChangeTracker.Clear();
                    var row = await db.Jobs.FirstOrDefaultAsync(j => j.Id == job.Id, token);
                    // Never the bundle over a stream: it can only be handed
                    // over once, and a stream cannot acknowledge receipt.
                    return row is null ? null : await views.JobAsync(row, deliverBundle: false, token);
                },
                ConnectorJson.Serialize,
                view => JobStateMachine.IsTerminal(view.State),
                signals,
                ConnectorSignals.Job(job.Id),
                TimeSpan.FromMinutes(10),
                ct);

            return Results.Empty;
        })
        // The same view the poll returns, one per event - identical contract to
        // a login's stream, and named the same way.
        .Produces<JobResponse>(StatusCodes.Status200OK, "text/event-stream");

        api.MapPost("/{provider}/jobs/{jobId}/answer", async (
            string provider,
            string jobId,
            AnswerRequest request,
            IProviderRegistry registry,
            ConnectorDbContext db,
            ChallengeService challenges,
            ViewBuilder views,
            CancellationToken ct) =>
        {
            var manifest = registry.RequireManifest(provider);
            var job = await RequireAsync(db, manifest.Id, jobId, ct);

            await challenges.AnswerAsync(request.ChallengeId, job.Id, request.Value, ct);
            return ConnectorResults.Json(await views.JobAsync(job, deliverBundle: false, ct));
        });
    }

    private static async Task<JobRow> RequireAsync(ConnectorDbContext db, string providerId, string jobId, CancellationToken ct) =>
        await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId && j.ProviderId == providerId, ct)
        ?? throw ConnectorException.Unsupported($"unknown job '{jobId}'");
}
