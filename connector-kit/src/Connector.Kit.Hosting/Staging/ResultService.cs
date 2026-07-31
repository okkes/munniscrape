using System.Text.Json.Nodes;
using Connector.Kit.AgentProtocol;
using Connector.Kit.Hosting.Data;
using Connector.Kit.Hosting.Infrastructure;
using Connector.Kit.Normalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Connector.Kit.Hosting.Staging;

/// <summary>
/// Staging, not storage.
///
/// Rows land here so a caller can pick them up over its own authenticated
/// channel, and leave on ack or after a hard TTL - whichever comes first.
/// The connector is a pipe, and the reason that matters is stated plainly in
/// the design: neither service may become the honeypot holding both retail
/// credentials and everyone's purchase history.
/// </summary>
public sealed class ResultService(
    ConnectorDbContext db,
    IOptions<ConnectorOptions> options,
    TimeProvider time)
{
    private readonly ConnectorOptions _options = options.Value;

    /// <summary>
    /// Writes a job's output under a fresh cursor and returns it.
    ///
    /// Re-emitting a record the session already has updates it in place
    /// rather than duplicating: <c>(session, resource, external_id)</c> is
    /// unique, and with the content hash that is what makes a re-run - and
    /// therefore the settlement-lag widening every adapter relies on - free.
    /// </summary>
    public async Task<string> StageAsync(JobRow job, JobResultRequest result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(result);

        var cursor = Ids.New(Ids.Cursor);
        var resource = job.ResourceId ?? "results";
        var staged = Enumerate(result, job.SessionId).ToList();
        if (staged.Count == 0) return cursor;

        var externalIds = staged.Select(s => s.ExternalId).ToList();
        var existing = await db.Results
            .Where(r => r.SessionId == job.SessionId && r.Resource == resource && externalIds.Contains(r.ExternalId))
            .ToDictionaryAsync(r => r.ExternalId, StringComparer.Ordinal, ct);

        var now = time.GetUtcNow();
        foreach (var record in staged)
        {
            // Null unless this pass asked for it. Assigned rather than merged,
            // so a later fetch without include=raw does not leave an older
            // pass's raw sitting on a row nobody asked to keep it on.
            var raw = result.Raw.GetValueOrDefault(record.ExternalId);

            if (existing.TryGetValue(record.ExternalId, out var row))
            {
                // Same content means the caller already has an identical row;
                // it still moves onto this cursor so one ack clears the pass.
                row.PayloadJson = record.PayloadJson;
                row.RawJson = raw;
                row.ContentHash = record.ContentHash;
                row.Cursor = cursor;
                row.JobId = job.Id;
                row.CreatedAt = now;
                continue;
            }

            db.Results.Add(new ResultRow
            {
                Id = record.Id,
                JobId = job.Id,
                SessionId = job.SessionId,
                Resource = resource,
                ExternalId = record.ExternalId,
                PayloadJson = record.PayloadJson,
                RawJson = raw,
                ContentHash = record.ContentHash,
                Cursor = cursor,
                CreatedAt = now,
            });
        }

        await db.SaveChangesAsync(ct);
        return cursor;
    }

    /// <summary>
    /// The records a job produced, newest cursor first, as raw JSON for the
    /// response.
    ///
    /// The provider's own payload is grafted on under <c>raw</c> only where one
    /// was staged, which happens only where the fetch asked for it. A record
    /// the adapter could not hand raw back for simply has no such field, rather
    /// than a null that reads like the provider sent nothing.
    /// </summary>
    public async Task<StagedPage> ReadAsync(string jobId, CancellationToken ct)
    {
        var rows = await db.Results
            .AsNoTracking()
            .Where(r => r.JobId == jobId)
            .OrderBy(r => r.CreatedAt)
            .ThenBy(r => r.Id)
            .ToListAsync(ct);

        var data = new JsonArray();
        foreach (var row in rows)
        {
            if (JsonNode.Parse(row.PayloadJson) is not { } node) continue;

            if (node is JsonObject record
                && row.RawJson is { Length: > 0 } raw
                && JsonNode.Parse(raw) is { } parsed)
            {
                record["raw"] = parsed;
            }

            data.Add(node);
        }

        return new StagedPage(data, rows.Count == 0 ? null : rows[^1].Cursor);
    }

    /// <summary>
    /// "Delivered, purge it." Acknowledged rows go immediately; that is what
    /// keeps the connector a pipe rather than a second copy of the consumer's
    /// database.
    /// </summary>
    public async Task<int> AckAsync(string sessionId, string resource, string cursor, CancellationToken ct) =>
        await db.Results
            .Where(r => r.SessionId == sessionId && r.Resource == resource && r.Cursor == cursor)
            .ExecuteDeleteAsync(ct);

    /// <summary>Unacknowledged rows die anyway. The caller's silence is not a reason to keep data.</summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        var cutoff = time.GetUtcNow().AddDays(-_options.Timeouts.ResultRetentionDays);
        return await db.Results.Where(r => r.CreatedAt < cutoff).ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Ids and content hashes are minted here, not by adapters: an adapter
    /// that invents its own would break the idempotency the whole pipeline
    /// depends on, and it is not the kind of mistake that shows up in a test.
    /// </summary>
    private static IEnumerable<StagedRecord> Enumerate(JobResultRequest result, string sessionId)
    {
        foreach (var account in result.Accounts)
        {
            var hash = ContentHash.Of(account);
            var withIds = account with
            {
                Id = Ids.ForRecord(Ids.Account, sessionId, account.ExternalId),
                ContentHash = hash,
            };
            yield return new StagedRecord(withIds.Id, account.ExternalId, hash, ConnectorJson.Serialize(withIds));
        }

        foreach (var transaction in result.Transactions)
        {
            var hash = ContentHash.Of(transaction);
            var withIds = transaction with
            {
                Id = Ids.ForRecord(Ids.Transaction, sessionId, transaction.ExternalId),
                ContentHash = hash,
            };
            yield return new StagedRecord(withIds.Id, transaction.ExternalId, hash, ConnectorJson.Serialize(withIds));
        }

        foreach (var receipt in result.Receipts)
        {
            // Reconciliation is applied here so the flag is a platform fact
            // rather than an adapter's opinion of its own output.
            var verified = receipt.WithReconciliation();
            var hash = ContentHash.Of(verified);
            var withIds = verified with
            {
                Id = Ids.ForRecord(Ids.Receipt, sessionId, receipt.ExternalId),
                ContentHash = hash,
            };
            yield return new StagedRecord(withIds.Id, receipt.ExternalId, hash, ConnectorJson.Serialize(withIds));
        }
    }

    private readonly record struct StagedRecord(string Id, string ExternalId, string ContentHash, string PayloadJson);
}

public readonly record struct StagedPage(JsonArray Data, string? Cursor);
