using Connector.Kit.Errors;
using Connector.Kit.Hosting.Auth;
using Connector.Kit.Hosting.Data;
using Connector.Kit.Hosting.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Connector.Kit.Hosting.Endpoints;

/// <summary>
/// Bring-your-own agents, from the consumer's side.
///
/// Health is visible on purpose: "your ASN sync is stale because your VM has
/// been off for three days" is a real message, and it is the difference
/// between a feature a user trusts and one that quietly stops working.
/// </summary>
internal static class AgentAdminEndpoints
{
    /// <summary>An agent is online if it has heartbeat recently. Three missed beats, not one.</summary>
    private const int OfflineAfterSeconds = 90;

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/agents", async (
            string? subject,
            ConnectorDbContext db,
            TimeProvider time,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(subject))
            {
                throw ConnectorException.InvalidRequest("subject is required");
            }

            var agents = await db.Agents.AsNoTracking()
                .Where(a => a.OwnerSubject == subject)
                .OrderBy(a => a.CreatedAt)
                .ToListAsync(ct);

            var ids = agents.Select(a => a.Id).ToList();
            var profiles = await db.Profiles.AsNoTracking()
                .Where(p => ids.Contains(p.AgentId))
                .ToListAsync(ct);

            var online = time.GetUtcNow().AddSeconds(-OfflineAfterSeconds);

            return ConnectorResults.Json(new AgentListResponse
            {
                Agents = [.. agents.Select(a => View(a, profiles, online))],
            });
        });

        api.MapPost("/agents/enrollment", async (
            AgentEnrollmentRequest request,
            AgentAuth auth,
            CancellationToken ct) =>
        {
            var minted = await auth.MintEnrollmentAsync(request.Subject, request.Name, ct);
            return ConnectorResults.Json(new AgentEnrollmentResponse
            {
                Code = minted.Code,
                ExpiresAt = minted.ExpiresAt,
            });
        });

        api.MapDelete("/agents/{agentId}", async (
            string agentId,
            ConnectorDbContext db,
            CancellationToken ct) =>
        {
            var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, ct)
                        ?? throw ConnectorException.Unsupported($"unknown agent '{agentId}'");

            agent.Revoked = true;
            // The token is dead the moment it cannot match anything, and an
            // agent that learns it is revoked stops and wipes its profiles.
            agent.TokenHash = "revoked:" + agent.Id;

            // Orphan rather than delete: a profile row is how a user is told
            // which connections just stopped working, and deleting it deletes
            // the explanation.
            await db.Profiles.Where(p => p.AgentId == agentId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Healthy, false), ct);

            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        })
        .Produces(StatusCodes.Status204NoContent);

        api.MapGet("/agents/{agentId}/profiles", async (
            string agentId,
            ConnectorDbContext db,
            CancellationToken ct) =>
        {
            _ = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, ct)
                ?? throw ConnectorException.Unsupported($"unknown agent '{agentId}'");

            var profiles = await db.Profiles.AsNoTracking().Where(p => p.AgentId == agentId).ToListAsync(ct);
            return ConnectorResults.Json(profiles.Select(Profile).ToList());
        });
    }

    private static AgentView View(AgentRow agent, IReadOnlyList<ProfileRow> profiles, DateTimeOffset onlineSince) => new()
    {
        Id = agent.Id,
        Name = agent.Name,
        Class = agent.Class,
        Revoked = agent.Revoked,
        LastHeartbeatAt = agent.LastHeartbeatAt,
        Online = !agent.Revoked && agent.LastHeartbeatAt > onlineSince,
        Profiles = [.. profiles.Where(p => p.AgentId == agent.Id).Select(Profile)],
    };

    private static ProfileView Profile(ProfileRow profile) => new()
    {
        Id = profile.Id,
        Provider = profile.ProviderId,
        SessionId = profile.SessionId,
        Healthy = profile.Healthy,
        LastOkAt = profile.LastOkAt,
    };
}
