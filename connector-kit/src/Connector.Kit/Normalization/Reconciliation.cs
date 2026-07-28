using Connector.Kit.Errors;

namespace Connector.Kit.Normalization;

/// <summary>
/// Where a provider gives us a redundant fact, check it.
///
/// Running balances, stated totals versus summed line items, counts in an
/// export header - redundancy that goes unchecked is redundancy wasted, and
/// every one of these catches a class of error that otherwise produces
/// plausible-looking output no human reviewer would spot.
/// </summary>
public static class Reconciliation
{
    /// <summary>One cent, to absorb the provider's own rounding.</summary>
    public const long ToleranceMinorUnits = 1;

    /// <summary>
    /// Do the line items, net of discounts, sum to the stated total?
    ///
    /// A receipt that fails is emitted with <see cref="Receipt.Reconciled"/>
    /// false rather than dropped - the consumer decides. Silently dropping
    /// it would hide a real purchase; silently trusting it would hand over
    /// a total we know is inconsistent with its own contents.
    /// </summary>
    public static bool ItemsReconcile(Receipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.Items.Count == 0) return true;   // nothing to check against

        var sum = 0L;
        foreach (var item in receipt.Items)
        {
            sum += item.Total.Value;
            if (item.Discount is { } discount) sum += discount.Amount.Value;
        }

        return Math.Abs(sum - receipt.Total.Value) <= ToleranceMinorUnits;
    }

    /// <summary>Applies the check and stamps the outcome onto the record.</summary>
    public static Receipt WithReconciliation(this Receipt receipt) =>
        receipt with { Reconciled = ItemsReconcile(receipt) };

    /// <summary>
    /// The signed gap between what the merchant said the receipt came to and
    /// what it itemised. Zero when they agree.
    /// </summary>
    public static long Unattributed(Receipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.Items.Count == 0) return 0;

        var itemised = 0L;
        foreach (var item in receipt.Items)
        {
            itemised += item.Total.Value;
            if (item.Discount is { } discount) itemised += discount.Amount.Value;
        }

        return receipt.Total.Value - itemised;
    }

    /// <summary>
    /// Closes the gap with one explicitly DERIVED line, so a consumer can show
    /// a breakdown that adds up to what was actually paid.
    ///
    /// Merchants routinely charge things they do not itemise - a delivery fee,
    /// a deposit - and the alternative to naming that gap is a receipt whose
    /// lines quietly total less than its total, which reads as a bug in us
    /// rather than an omission by them.
    ///
    /// Deliberately NOT called a fee. We know the amount and not the reason,
    /// and inventing a reason for money is how a receipt stops being evidence.
    /// The total is never touched: it remains the merchant's own figure,
    /// because that is what a bank transaction is matched against.
    /// </summary>
    public static Receipt WithUnattributedLine(this Receipt receipt, string labelKey)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        var gap = Unattributed(receipt);
        if (Math.Abs(gap) <= ToleranceMinorUnits) return receipt.WithReconciliation();

        var line = new ReceiptItem
        {
            Name = labelKey,
            Kind = ReceiptLineKind.Unattributed,
            Total = new Money(gap, receipt.Total.Currency),
        };

        return (receipt with { Items = [.. receipt.Items, line] }).WithReconciliation();
    }
}

/// <summary>
/// Verifies a provider's own running balances against its own amounts.
///
/// For each adjacent pair the earlier resulting balance plus the later
/// signed amount must equal the later resulting balance. A mismatch means
/// rows arrived out of order, a decimal separator was misread, a
/// debit/credit flag was inverted, or a row was dropped - all of which
/// otherwise produce believable garbage.
/// </summary>
public static class BalanceChain
{
    /// <summary>
    /// <paramref name="ordered"/> must be oldest-first. Returns the index of
    /// the first transaction that breaks the chain, or null when it holds.
    /// Transactions without a stated balance are skipped, not guessed at.
    /// </summary>
    public static int? FindBreak(IReadOnlyList<Transaction> ordered)
    {
        ArgumentNullException.ThrowIfNull(ordered);

        for (var i = 1; i < ordered.Count; i++)
        {
            var previous = ordered[i - 1].ResultingBalance;
            var current = ordered[i].ResultingBalance;
            if (previous is null || current is null) continue;

            var expected = previous.Value.Value + ordered[i].Amount.Value;
            if (Math.Abs(expected - current.Value.Value) > Reconciliation.ToleranceMinorUnits)
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>
    /// Fails the run rather than emitting data we cannot vouch for. This is
    /// <see cref="ErrorCode.ProviderChanged"/>, not <c>Internal</c>: the
    /// export's shape moved, and an adapter needs fixing.
    /// </summary>
    public static void Verify(IReadOnlyList<Transaction> ordered, string providerId)
    {
        if (FindBreak(ordered) is not { } index) return;

        throw ConnectorException.ProviderChanged(
            $"{providerId}: balance chain broken at index {index} " +
            $"(external_id '{ordered[index].ExternalId}'); the export's shape or ordering changed");
    }
}
