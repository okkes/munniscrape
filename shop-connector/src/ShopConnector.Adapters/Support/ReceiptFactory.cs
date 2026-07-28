using Connector.Kit;
using Connector.Kit.Jobs;
using Connector.Kit.Normalization;

namespace ShopConnector.Adapters.Support;

/// <summary>
/// The single construction point for an emitted receipt, so that no adapter
/// can forget the two things every receipt owes the consumer: a
/// reconciliation verdict and a content hash.
/// </summary>
internal static class ReceiptFactory
{
    public static Receipt Build(
        string sessionId,
        string externalId,
        Merchant merchant,
        DateTimeOffset purchasedAt,
        Money total,
        ReceiptPayment payment,
        IReadOnlyList<ReceiptItem> items)
    {
        // Reconcile first, hash second. The hash covers the facts, not the
        // verdict, so the order is not load-bearing today - fixing it here
        // keeps it from becoming load-bearing by accident later.
        var receipt = new Receipt
        {
            Id = Ids.ForRecord(Ids.Receipt, sessionId, externalId),
            ExternalId = externalId,
            Merchant = merchant,
            PurchasedAt = purchasedAt,
            Total = total,
            Payment = payment,
            Items = items,
        }.WithReconciliation();

        return receipt with { ContentHash = ContentHash.Of(receipt) };
    }

    /// <summary>
    /// A payment block with every field stated, including the ones we do
    /// not have. The consumer matches receipts to transactions partly on
    /// the payment tail; an explicit null tells it the match will be weaker,
    /// where an omitted field would just look like an oversight.
    /// </summary>
    public static ReceiptPayment Payment(string? method = null, string? cardLast4 = null, string? ibanTail = null) =>
        new() { Method = method, CardLast4 = cardLast4, IbanTail = ibanTail };

    /// <summary>
    /// Inclusive window filter. The platform has already widened any
    /// caller-supplied <c>since</c> by the provider's settlement lag, so
    /// this compares against the request as given.
    /// </summary>
    public static bool InWindow(DateTimeOffset purchasedAt, ResourceRequest request)
    {
        var day = DateOnly.FromDateTime(purchasedAt.Date);
        if (request.Since is { } since && day < since) return false;
        if (request.Until is { } until && day > until) return false;
        return true;
    }
}
