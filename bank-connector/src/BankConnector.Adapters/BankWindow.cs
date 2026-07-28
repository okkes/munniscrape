using Connector.Kit.Errors;
using Connector.Kit.Jobs;
using Connector.Kit.Manifests;

namespace BankConnector.Adapters;

/// <summary>Inclusive at both ends, exactly like the query parameters.</summary>
public readonly record struct FetchWindow(DateOnly From, DateOnly To)
{
    public bool Contains(DateOnly day) => day >= From && day <= To;
}

/// <summary>
/// Turns a caller's <c>since</c>/<c>until</c> into the range an adapter
/// actually asks the bank for.
///
/// The interesting part is that the two are not the same. A bank export
/// shows BOOKED transactions: a card payment made on the 19th may not
/// appear until it settles on the 21st, and it then appears dated the 19th,
/// behind a cursor that has already moved past it. Fetching strictly since
/// the last sync therefore loses rows permanently, and invisibly - from the
/// consumer's point of view they never existed. Widening by the provider's
/// settlement lag and letting content hashes absorb the overlap is a
/// correctness requirement, not an optimisation.
/// </summary>
public static class BankWindow
{
    /// <summary>
    /// What a caller gets when it names no <c>since</c> at all. Short
    /// enough that a first connect does not download a year, long enough
    /// that a monthly credit-card statement is complete.
    /// </summary>
    public const int DefaultSinceDays = 90;

    public static FetchWindow Resolve(
        ResourceRequest request,
        ProviderManifest manifest,
        DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(manifest);

        var maxHistoryDays = manifest.Resource(request.ResourceId)?.MaxHistoryDays
                             ?? manifest.Limits.MaxHistoryDays;

        var to = request.Until ?? today;
        var from = (request.Since ?? today.AddDays(-DefaultSinceDays))
            .AddDays(-manifest.Limits.SettlementLagDays);

        var floor = today.AddDays(-maxHistoryDays);
        if (from < floor) from = floor;

        // Widening can only ever move `from` earlier, so an inverted window
        // is always the caller's own doing and worth naming as such.
        if (to < from)
        {
            throw ConnectorException.InvalidRequest(
                $"until '{to:yyyy-MM-dd}' precedes since '{request.Since:yyyy-MM-dd}' after the provider's history limit was applied");
        }

        return new FetchWindow(from, to);
    }
}
