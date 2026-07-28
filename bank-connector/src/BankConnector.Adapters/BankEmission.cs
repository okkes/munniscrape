using Connector.Kit.Adapters;
using Connector.Kit.Normalization;
using Connector.Kit.Security;

namespace BankConnector.Adapters;

/// <summary>
/// The last thing every bank adapter does before it returns.
///
/// A bank hands us a redundant fact - the running balance after each row -
/// and the whole point of this type is that no adapter can forget to check
/// it. A broken chain means rows arrived out of order, a decimal separator
/// was misread, a debit/credit flag was inverted, or a row was dropped,
/// and every one of those produces believable output that no human
/// reviewer would spot.
/// </summary>
public static class BankEmission
{
    /// <summary>
    /// Verifies each account's chain and packages the result.
    ///
    /// <paramref name="transactions"/> must be in the provider's own order,
    /// oldest-first per account. It is deliberately NOT sorted here:
    /// "the rows arrived in the wrong order" is one of the failures the
    /// chain check exists to catch, and sorting first would hide it.
    /// </summary>
    public static FetchResult Verified(
        string providerId,
        IReadOnlyList<Account> accounts,
        IReadOnlyList<Transaction> transactions,
        bool complete = true,
        string? via = null,
        SessionMaterial? refreshedMaterial = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(transactions);

        VerifyPerAccount(providerId, transactions);

        return new FetchResult
        {
            Accounts = accounts,
            Transactions = transactions,
            Complete = complete,
            Via = via,
            RefreshedMaterial = refreshedMaterial,
        };
    }

    /// <summary>
    /// Runs the chain check per account.
    ///
    /// Verifying a concatenation of two accounts' transactions would fail at
    /// every seam, because the balance jumps from one account's closing
    /// figure to another's - so the check has to be grouped, and grouping
    /// must preserve each account's relative order.
    /// </summary>
    public static void VerifyPerAccount(string providerId, IReadOnlyList<Transaction> transactions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(transactions);

        var byAccount = new Dictionary<string, List<Transaction>>(StringComparer.Ordinal);
        foreach (var tx in transactions)
        {
            if (!byAccount.TryGetValue(tx.AccountId, out var rows))
            {
                rows = [];
                byAccount[tx.AccountId] = rows;
            }

            rows.Add(tx);
        }

        foreach (var rows in byAccount.Values)
        {
            BalanceChain.Verify(rows, providerId);
        }
    }

    /// <summary>
    /// Trims to the resource's per-fetch ceiling, keeping the OLDEST rows.
    ///
    /// Taking a contiguous oldest-first prefix is what keeps the balance
    /// chain intact - dropping rows out of the middle would break it, and
    /// dropping the oldest would leave the caller with a gap it can never
    /// discover. The remainder is the next page's problem; the control plane
    /// owns cursors.
    /// </summary>
    public static (IReadOnlyList<Transaction> Page, bool Complete) Page(
        IReadOnlyList<Transaction> ordered, int maxRecords)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRecords);

        return ordered.Count <= maxRecords
            ? (ordered, true)
            : ([.. ordered.Take(maxRecords)], false);
    }
}
