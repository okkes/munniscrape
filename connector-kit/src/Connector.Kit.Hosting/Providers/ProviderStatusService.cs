using Connector.Kit.Adapters;
using Connector.Kit.Hosting.Data;
using Connector.Kit.Manifests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Connector.Kit.Hosting.Providers;

/// <summary>
/// The kill switch, and the thing that lets a consumer degrade honestly.
///
/// Provider definitions are code; only their health is a row. Flipping a
/// provider to <c>paused</c> stops new work for every user of it within one
/// lease poll, and a consumer can say "Jumbo connections are paused, we're
/// fixing it" instead of showing a mystery spinner.
/// </summary>
public sealed class ProviderStatusService(
    ConnectorDbContext db,
    IProviderRegistry registry,
    TimeProvider time,
    ILogger<ProviderStatusService> logger)
{
    public static ProviderStatus ToStatus(ProviderStatusRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new ProviderStatus
        {
            ProviderId = row.ProviderId,
            State = row.State,
            Since = row.Since,
            ReasonKey = row.ReasonKey,
        };
    }

    public async Task<IReadOnlyDictionary<string, ProviderStatus>> AllAsync(CancellationToken ct)
    {
        var rows = await db.ProviderStatuses.AsNoTracking().ToListAsync(ct);
        var byId = rows.ToDictionary(r => r.ProviderId, ToStatus, StringComparer.Ordinal);

        // A provider with no row has never had an incident. Reporting it as
        // healthy beats reporting it as missing: the registry is the source of
        // truth for existence, this table only for health.
        var now = time.GetUtcNow();
        foreach (var manifest in registry.Manifests)
        {
            if (!byId.ContainsKey(manifest.Id)) byId[manifest.Id] = ProviderStatus.Healthy(manifest.Id, now);
        }

        return byId;
    }

    public async Task<ProviderStatus> GetAsync(string providerId, CancellationToken ct)
    {
        var row = await db.ProviderStatuses.AsNoTracking().FirstOrDefaultAsync(r => r.ProviderId == providerId, ct);
        return row is null ? ProviderStatus.Healthy(providerId, time.GetUtcNow()) : ToStatus(row);
    }

    public async Task<ProviderStatus> SetAsync(string providerId, ProviderState state, string? reasonKey, CancellationToken ct)
    {
        var manifest = registry.RequireManifest(providerId);
        var now = time.GetUtcNow();

        var row = await db.ProviderStatuses.FirstOrDefaultAsync(r => r.ProviderId == manifest.Id, ct);
        if (row is null)
        {
            row = new ProviderStatusRow { ProviderId = manifest.Id };
            db.ProviderStatuses.Add(row);
        }

        // Since means "since this state began", so it only moves when the
        // state does - otherwise an operator re-confirming a pause resets the
        // clock everyone is reading.
        if (row.State != state) row.Since = now;
        row.State = state;
        row.ReasonKey = reasonKey;

        await db.SaveChangesAsync(ct);
        logger.LogWarning("provider {Provider} is now {State} ({Reason})", manifest.Id, state, reasonKey ?? "-");
        return ToStatus(row);
    }

    /// <summary>
    /// Called when an adapter reports <c>provider_changed</c>. The provider's
    /// shape moved and an adapter needs fixing, so the next user gets a real
    /// message instead of a retry. Never escalates past degraded on its own -
    /// pausing a provider is an operator's decision.
    /// </summary>
    public async Task DegradeAsync(string providerId, string? reasonKey, CancellationToken ct)
    {
        var current = await GetAsync(providerId, ct);
        if (current.State != ProviderState.Healthy) return;

        await SetAsync(providerId, ProviderState.Degraded, reasonKey ?? "connect.provider.changed", ct);
    }

    /// <summary>
    /// The other half of <see cref="DegradeAsync"/>: a provider whose jobs are
    /// succeeding again is not degraded any more.
    ///
    /// Without this the flag was one-way. One unrecognised shape marked a
    /// provider degraded forever, and the catalogue kept telling every user
    /// "an engineer is on it" for a provider that had been working for hours -
    /// a status nobody can trust is worse than no status, because consumers
    /// are told to render it.
    ///
    /// Deliberately narrow: ONLY degraded recovers. <see cref="ProviderState.Paused"/>
    /// and <see cref="ProviderState.Retired"/> are operator decisions, and a
    /// job succeeding is not an argument against one - a paused provider that
    /// un-paused itself the moment a queued job drained would make the kill
    /// switch useless exactly when it is needed.
    /// </summary>
    public async Task RecoverAsync(string providerId, CancellationToken ct)
    {
        var current = await GetAsync(providerId, ct);
        if (current.State != ProviderState.Degraded) return;

        await SetAsync(providerId, ProviderState.Healthy, reasonKey: null, ct);
    }

    /// <summary>Gives every registered provider a row at startup, so the table is complete rather than sparse.</summary>
    public async Task SeedAsync(CancellationToken ct)
    {
        var known = await db.ProviderStatuses.Select(r => r.ProviderId).ToListAsync(ct);
        var missing = registry.Manifests.Select(m => m.Id).Except(known, StringComparer.Ordinal).ToList();
        if (missing.Count == 0) return;

        var now = time.GetUtcNow();
        foreach (var providerId in missing)
        {
            db.ProviderStatuses.Add(new ProviderStatusRow
            {
                ProviderId = providerId,
                State = ProviderState.Healthy,
                Since = now,
            });
        }

        await db.SaveChangesAsync(ct);
    }
}
