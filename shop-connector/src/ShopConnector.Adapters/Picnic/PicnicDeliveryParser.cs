using System.Text.Json;
using Connector.Kit.Errors;
using Connector.Kit.Normalization;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.Picnic;

/// <summary>
/// One placed order, as it appears in the slim <c>/deliveries/summary</c>
/// answer. A delivery may hold more than one - Picnic lets a household add a
/// second order to a slot right up to the cut-off - and each has its own id,
/// its own creation time and its own total, so each is its own receipt.
/// </summary>
internal sealed record PicnicOrderSummary
{
    public required string DeliveryId { get; init; }

    public required string OrderId { get; init; }

    public required DateTimeOffset PurchasedAt { get; init; }

    public required Money Total { get; init; }

    public string? Status { get; init; }
}

/// <summary>
/// Parses the two shapes this adapter reads: the delivery summary list and the
/// per-delivery detail.
///
/// Unusually, the field names here are CONFIRMED rather than guessed - they
/// come from the reference's own type declarations and from a recorded
/// response - so the reads are still alias-tolerant but a missing REQUIRED
/// field is a shape change worth naming rather than a row to skip quietly.
/// Amounts are the one thing never inferred: their unit comes from
/// <see cref="PicnicOptions"/> and is minor units, which is confirmed twice
/// over (documented "in cents", and arithmetically self-consistent against the
/// stated total).
/// </summary>
internal static class PicnicDeliveryParser
{
    private const string ProviderId = PicnicAdapter.ProviderId;

    /// <summary>
    /// The slim list. Confirmed shape: a top-level ARRAY of deliveries, each
    /// with an <c>orders</c> array of <c>DeliveryOrder</c> - id, creation_time,
    /// total_price, status, cancellation_time, and no items at all.
    /// </summary>
    public static IReadOnlyList<PicnicOrderSummary> ParseSummary(
        JsonElement root, PicnicOptions options, TimeZoneInfo zone)
    {
        var deliveries = root.ValueKind == JsonValueKind.Array
            ? JsonAccess.AsArray(root)
            : JsonAccess.Array(root, "deliveries", "items", "data");

        if (deliveries.Count == 0 && root.ValueKind != JsonValueKind.Array)
        {
            // An empty history is a top-level `[]`. An object with nothing
            // recognisable in it is the endpoint having moved.
            throw ConnectorException.ProviderChanged(
                $"{ProviderId}: the delivery summary is neither an array nor a recognisable envelope " +
                $"(got {root.ValueKind}); expected the confirmed top-level array of deliveries");
        }

        var summaries = new List<PicnicOrderSummary>();

        foreach (var delivery in deliveries)
        {
            var deliveryId = JsonAccess.StrOf(delivery, "delivery_id", "id");
            if (string.IsNullOrWhiteSpace(deliveryId))
            {
                throw ConnectorException.ProviderChanged(
                    $"{ProviderId}: a delivery in the summary carries no 'delivery_id'");
            }

            // The delivery's own creation time is the fallback for an order
            // that states none - they are within milliseconds of each other in
            // every observed response.
            var deliveryCreated = JsonAccess.StrOf(delivery, "creation_time");

            foreach (var order in JsonAccess.Array(delivery, "orders"))
            {
                var orderId = JsonAccess.StrOf(order, "id", "order_id");
                if (string.IsNullOrWhiteSpace(orderId))
                {
                    throw ConnectorException.ProviderChanged(
                        $"{ProviderId}: an order under delivery '{deliveryId}' carries no 'id'");
                }

                summaries.Add(new PicnicOrderSummary
                {
                    DeliveryId = deliveryId,
                    OrderId = orderId,
                    PurchasedAt = PurchasedAt(order, deliveryCreated, zone),
                    Total = MoneyReader.Require(order, options.TotalUnit, options.Currency,
                        $"order[{orderId}].total_price", "total_price"),
                    Status = JsonAccess.StrOf(order, "status"),
                });
            }
        }

        return summaries;
    }

    /// <summary>
    /// When the user bought it.
    ///
    /// A Picnic delivery has two moments and they are routinely a day apart -
    /// in the recorded response the order was placed at 20:14 on the 17th and
    /// the van arrived at 19:48 on the 18th. <c>creation_time</c> is the one
    /// that means "bought": it is the checkout, the moment the basket became a
    /// committed order at a fixed price, and the moment the payment instrument
    /// on the order was authorised. <c>delivery_time</c> is a logistics fact
    /// about a van, and using it would date a purchase to a day on which the
    /// user did nothing.
    ///
    /// It also survives the cases the other does not: a CURRENT order has no
    /// delivery time yet and a CANCELLED one never will, while every order ever
    /// placed has a creation time. The gap between the two is not thrown away -
    /// the manifest's settlement lag is what stops a caller's `since` window
    /// from losing an order that was placed before it and only settled inside
    /// it.
    ///
    /// The value carries a real offset of its own (<c>+02:00</c>), which is
    /// honoured; the Amsterdam fallback only applies if Picnic ever stops
    /// stating one.
    /// </summary>
    private static DateTimeOffset PurchasedAt(JsonElement order, string? deliveryCreated, TimeZoneInfo zone)
    {
        var stated = JsonAccess.StrOf(order, "creation_time") ?? deliveryCreated;
        return ReceiptTime.Parse(stated, zone, ProviderId, "order.creation_time");
    }

    /// <summary>
    /// The full orders inside a <c>GET /deliveries/{id}</c> answer, keyed by
    /// order id. Documents are short-lived, so the caller must read what it
    /// needs before disposing the one these elements came from.
    /// </summary>
    public static IReadOnlyDictionary<string, JsonElement> OrdersById(JsonElement detail)
    {
        if (!JsonAccess.TryProp(detail, out var orders, "orders") || orders.ValueKind != JsonValueKind.Array)
        {
            throw ConnectorException.ProviderChanged(
                $"{ProviderId}: the delivery detail carries no 'orders' array; " +
                "line items exist nowhere else in this API");
        }

        var byId = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var order in orders.EnumerateArray())
        {
            if (JsonAccess.StrOf(order, "id", "order_id") is { Length: > 0 } id) byId[id] = order;
        }

        return byId;
    }

    /// <summary>
    /// The line items of one order, plus the deposit line that makes them add
    /// up.
    ///
    /// The reconciliation identity, confirmed arithmetically against a recorded
    /// response: <c>total_price = SUM(line.display_price) - total_savings +
    /// total_deposit</c>. Each line's <c>display_price</c> is its gross, a PRICE
    /// decorator states the discounted figure, and the differences are exactly
    /// <c>total_savings</c>. Emitting the discounts as negative amounts and the
    /// deposit as its own line is what turns that identity into the check the
    /// platform runs.
    /// </summary>
    public static IReadOnlyList<ReceiptItem> ParseItems(JsonElement order, PicnicOptions options)
    {
        var lines = JsonAccess.Array(order, "items");
        var items = new List<ReceiptItem>(lines.Count + 1);

        foreach (var line in lines)
        {
            var articles = JsonAccess.Array(line, "items");
            var lineId = JsonAccess.StrOf(line, "id") ?? "?";

            // Every observed line holds one article repeated by a QUANTITY
            // decorator, so the first one names the line.
            var name = articles.Count > 0 ? JsonAccess.StrOf(articles[0], "name") : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                throw ConnectorException.ProviderChanged(
                    $"{ProviderId}: order line '{lineId}' has no 'items[].name'; " +
                    "a priced line with no product name is a shape this parser cannot report honestly");
            }

            // The charged gross for the line. Not the article's price: a line
            // of two yoghurts states 270 while the article states 139 each,
            // and only the line total reconciles.
            var gross = MoneyReader.Require(line, options.LineUnit, options.Currency,
                $"item[{name}].display_price", "display_price", "price");

            items.Add(new ReceiptItem
            {
                Name = name,
                Quantity = Quantity(articles),
                // Picnic's documented "base per-unit price in cents", before
                // bundle discounts. Stated because it is the provider's own
                // per-unit figure; note it does NOT necessarily multiply out to
                // the line total, which is why the line total is what
                // reconciles.
                UnitPrice = articles.Count > 0
                    ? MoneyReader.Optional(articles[0], options.ArticleUnit, options.Currency,
                        $"item[{name}].price", "price")
                    : null,
                Total = gross,
                Discount = Discount(line, gross, options, name),
            });
        }

        if (Deposit(order, options) is { } deposit) items.Add(deposit);

        return items;
    }

    /// <summary>
    /// How many were bought.
    ///
    /// The QUANTITY decorator is authoritative and the length of the articles
    /// array is not: a line of two yoghurts carries a single article entry with
    /// <c>{"type":"QUANTITY","quantity":2}</c> beside it. Reading the array
    /// length would report one, which is wrong in a way nothing downstream
    /// could catch - a quantity is not money, so reconciliation would still
    /// pass.
    /// </summary>
    private static decimal? Quantity(IReadOnlyList<JsonElement> articles)
    {
        if (articles.Count == 0) return null;

        foreach (var decorator in JsonAccess.Array(articles[0], "decorators"))
        {
            if (!string.Equals(JsonAccess.StrOf(decorator, "type"), "QUANTITY", StringComparison.Ordinal)) continue;
            if (JsonAccess.Quantity(decorator, "quantity") is { } stated) return stated;
        }

        return articles.Count;
    }

    /// <summary>
    /// A line's promotion, as a negative amount.
    ///
    /// Picnic states the discount indirectly: a PRICE decorator carries the
    /// figure actually charged, and a PROMO decorator carries the shop's own
    /// wording for it. The difference is the discount, and the differences
    /// across a whole order are exactly the order's <c>total_savings</c>.
    /// </summary>
    private static ReceiptDiscount? Discount(
        JsonElement line, Money gross, PicnicOptions options, string name)
    {
        Money? charged = null;
        string? label = null;

        foreach (var decorator in JsonAccess.Array(line, "decorators"))
        {
            var type = JsonAccess.StrOf(decorator, "type");

            if (string.Equals(type, "PRICE", StringComparison.Ordinal))
            {
                charged ??= MoneyReader.Optional(decorator, options.DiscountUnit, options.Currency,
                    $"item[{name}].decorator.display_price", "display_price");
            }
            else if (string.Equals(type, "PROMO", StringComparison.Ordinal))
            {
                label ??= JsonAccess.StrOf(decorator, "text", "label", "description");
            }
        }

        if (charged is not { } net) return null;

        var delta = net - gross;

        // Only a reduction is a discount. A PRICE decorator ABOVE the line's
        // own gross is not something this parser understands, and inventing a
        // positive "discount" would corrupt the sum quietly; leaving it out
        // lets reconciliation flag the receipt, which is the loud outcome.
        return delta.Value < 0 ? new ReceiptDiscount { Amount = delta, Label = label } : null;
    }

    /// <summary>
    /// Statiegeld and bag fees, as their own line.
    ///
    /// <c>total_deposit</c> is charged on top of the line items, so without a
    /// line for it the items cannot sum to the stated total and every receipt
    /// with a single returnable bottle on it would be flagged. The quantity is
    /// the number of deposit-bearing units Picnic itself counted.
    /// </summary>
    private static ReceiptItem? Deposit(JsonElement order, PicnicOptions options)
    {
        var deposit = MoneyReader.Optional(order, options.TotalUnit, options.Currency,
            "order.total_deposit", "total_deposit");

        if (deposit is not { } amount || amount.Value == 0) return null;

        decimal counted = 0;
        foreach (var line in JsonAccess.Array(order, "deposit_breakdown"))
        {
            counted += JsonAccess.Quantity(line, "count") ?? 0;
        }

        return new ReceiptItem
        {
            Name = options.DepositItemName,
            Quantity = counted > 0 ? counted : null,
            Total = amount,
        };
    }

    /// <summary>
    /// How the order was paid for.
    ///
    /// Picnic states a masked IBAN and a payment type, never a card tail. The
    /// nulls are explicit so the consumer knows its match will be weaker rather
    /// than reading an omission as an oversight.
    /// </summary>
    public static ReceiptPayment ParsePayment(JsonElement order)
    {
        if (!JsonAccess.TryProp(order, out var info, "transaction_info")) return ReceiptFactory.Payment();

        return ReceiptFactory.Payment(
            Method(JsonAccess.StrOf(info, "payment_type")),
            cardLast4: null,
            ibanTail: JsonAccess.Tail(JsonAccess.StrOf(info, "redacted_iban")));
    }

    /// <summary>
    /// Picnic has no shops. It is a delivery service, so there is no store the
    /// user stood in and no store name to state - null, rather than a hub code
    /// dressed up as one.
    /// </summary>
    public static Merchant Merchant() => new()
    {
        Id = ProviderId,
        Name = "Picnic",
        StoreName = null,
    };

    /// <summary>
    /// The schema's payment vocabulary is <c>card</c>, <c>cash</c>,
    /// <c>ideal</c>, <c>other</c>. Picnic's confirmed values are IDEAL and
    /// DIRECT_DEBIT; the latter has no term of its own here, so it lands in
    /// "other" and the IBAN tail beside it carries the identifying detail.
    /// </summary>
    private static string? Method(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        if (raw.Contains("IDEAL", StringComparison.OrdinalIgnoreCase)) return "ideal";
        if (raw.Contains("CASH", StringComparison.OrdinalIgnoreCase)) return "cash";

        return raw.Contains("CARD", StringComparison.OrdinalIgnoreCase) ||
               raw.Contains("MAESTRO", StringComparison.OrdinalIgnoreCase) ||
               raw.Contains("VISA", StringComparison.OrdinalIgnoreCase)
            ? "card"
            : "other";
    }
}
