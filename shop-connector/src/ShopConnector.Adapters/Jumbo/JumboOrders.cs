using System.Text.Json;
using Connector.Kit.Errors;
using Connector.Kit.Normalization;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.Jumbo;

/// <summary>
/// One row of <c>data.onlineOrders.orders</c>.
///
/// The list states the total; the line items live in a separate
/// <c>OrderPagesOrder</c> call. Keeping the two apart is what makes them a
/// real reconciliation pair rather than one number repeated back to itself.
/// </summary>
internal sealed record JumboOrderSummary
{
    public required string OrderId { get; init; }

    public required DateTimeOffset PurchasedAt { get; init; }

    public required Money Total { get; init; }

    public string? StoreName { get; init; }
}

/// <summary>
/// What an <c>OrderPagesOrder</c> document says about one order.
/// </summary>
internal sealed record JumboOrderDetail
{
    public IReadOnlyList<ReceiptItem> Items { get; init; } = [];

    public ReceiptPayment Payment { get; init; } = ReceiptFactory.Payment();
}

/// <summary>
/// Jumbo's online orders, parsed.
///
/// Every path and field name below is CONFIRMED from the operation documents
/// in <see cref="JumboOptions"/>. What is deliberately <em>not</em> inferred
/// is the money unit: order amounts are decimal strings of euros and
/// catalogue prices are cents, and the two live one field apart in the same
/// schema - so the unit is declared per field in the options and read from
/// there, never guessed from the value's shape.
/// </summary>
internal static class JumboOrders
{
    private const string ProviderId = JumboAdapter.ProviderId;

    /// <summary>
    /// The rows of an order list page.
    ///
    /// A path that does not resolve is <c>provider_changed</c> rather than an
    /// empty list: "you have never shopped at Jumbo" is a worse lie than an
    /// outage, and this is exactly the mistake the four guessed
    /// <c>ReceiptPaths</c> would have produced silently.
    /// </summary>
    public static IReadOnlyList<JsonElement> Rows(JsonElement root, JumboOptions options)
    {
        if (!JsonAccess.TryPath(root, options.OnlineOrdersPath, out var node))
        {
            throw ConnectorException.ProviderChanged(
                $"{ProviderId}: no order collection at '{options.OnlineOrdersPath}'");
        }

        return node.ValueKind == JsonValueKind.Array ? JsonAccess.AsArray(node) : [];
    }

    /// <summary>
    /// The order count the list states, when it states one. A count in a shape
    /// we cannot read is simply absent: it bounds the walk as a courtesy, and
    /// the walk's own guards still end it without one.
    /// </summary>
    public static int? TotalCount(JsonElement root, JumboOptions options) =>
        JsonAccess.TryPath(root, options.OnlineOrdersTotalPath, out var node) &&
        node.ValueKind == JsonValueKind.Number &&
        node.TryGetInt32(out var count)
            ? count
            : null;

    /// <summary>
    /// One order summary, or null when the row cannot be keyed and therefore
    /// cannot be deduped.
    /// </summary>
    public static JumboOrderSummary? ParseSummary(JsonElement row, JumboOptions options, TimeZoneInfo zone)
    {
        var id = JsonAccess.StrOf(row, "orderId");
        if (string.IsNullOrWhiteSpace(id)) return null;

        // Stated on the money object itself, not on the order, so it is read
        // from there and only falls back when Jumbo says nothing.
        var currency = JsonAccess.TryProp(row, out var money, "totalToPayMoneyType")
            ? MoneyReader.Currency(money, options.Currency)
            : options.Currency;

        // CONFIRMED decimal-string euros. Declared, never sniffed - see
        // JumboOptions.OrderTotalUnit for why both of Jumbo's conventions are
        // written down there.
        var total = MoneyReader.Optional(row, options.OrderTotalUnit, currency,
                        $"order[{id}].totalToPayMoneyType", "totalToPayMoneyType")
                    ?? throw ConnectorException.ProviderChanged(
                        $"{ProviderId}: order '{id}' states no totalToPayMoneyType");

        return new JumboOrderSummary
        {
            OrderId = id,
            // deliveryDate carries no offset of its own, so it is read in
            // Europe/Amsterdam rather than in whatever zone the agent happens
            // to run in - a near-midnight order otherwise lands on the wrong
            // day, and a receipt matched to the wrong day is worse than one
            // not matched at all.
            PurchasedAt = ReceiptTime.Parse(
                JsonAccess.StrOf(row, "deliveryDate"), zone, ProviderId, $"order[{id}].deliveryDate"),
            Total = total,
            StoreName = JsonAccess.StrOf(row, "branchName"),
        };
    }

    /// <summary>
    /// The line items and payment of one order.
    ///
    /// A line's <c>Total</c> is its price <em>before</em> discount and its
    /// <c>Discount</c> is the difference to the price after, so the two sum
    /// back to exactly what Jumbo says the line cost. Both numbers come from
    /// the provider; nothing here is derived from a rate or a guess.
    /// </summary>
    public static JumboOrderDetail ParseDetail(
        JsonElement root, JumboOptions options, string currency, string orderId)
    {
        if (!JsonAccess.TryPath(root, options.OrderDetailPath, out var order))
        {
            throw ConnectorException.ProviderChanged(
                $"{ProviderId}: order '{orderId}' detail has nothing at '{options.OrderDetailPath}'");
        }

        var items = new List<ReceiptItem>();

        foreach (var line in JsonAccess.Array(order, "items"))
        {
            var name = LineName(line);
            var field = $"order[{orderId}].item[{name}]";

            var gross = MoneyReader.Optional(line, options.OrderLineUnit, currency,
                $"{field}.linePriceExDiscount", "linePriceExDiscount");
            var net = MoneyReader.Optional(line, options.OrderLineUnit, currency,
                $"{field}.linePriceIncDiscount", "linePriceIncDiscount");

            if (gross is null && net is null)
            {
                // Both confirmed price fields are gone from the line. That is
                // the schema moving, not an order with a free product.
                throw ConnectorException.ProviderChanged(
                    $"{ProviderId}: order '{orderId}' line '{name}' states neither " +
                    "linePriceExDiscount nor linePriceIncDiscount");
            }

            var total = gross ?? net!.Value;

            items.Add(new ReceiptItem
            {
                Name = name,
                Quantity = JsonAccess.Quantity(line, "quantity", "orderedQuantity", "pickedQuantity"),
                UnitPrice = UnitPrice(line, options, currency, field),
                Total = total,
                Discount = Discount(line, total, net),
            });
        }

        AddDeposits(items, order, options, currency, orderId);
        AddSurcharges(items, order, options, currency, orderId);

        return new JumboOrderDetail
        {
            Items = items,
            // paymentMethod is a bare string on the order. Jumbo states no
            // card or IBAN tail anywhere in this document, and an explicit
            // null is what tells the consumer its receipt-to-transaction match
            // will be weaker here rather than looking like an oversight.
            Payment = ReceiptFactory.Payment(JumboReceipts.Method(JsonAccess.StrOf(order, "paymentMethod"))),
        };
    }

    private static string LineName(JsonElement line)
    {
        if (JsonAccess.TryProp(line, out var details, "details"))
        {
            var title = JsonAccess.StrOf(details, "title");
            if (!string.IsNullOrWhiteSpace(title)) return title;
        }

        // The SKU is not a name, but it is the provider's own identifier for
        // the thing bought - which beats dropping the line and the money on it.
        return JsonAccess.StrOf(line, "sku") ?? "?";
    }

    private static Money? UnitPrice(JsonElement line, JumboOptions options, string currency, string field)
    {
        if (!JsonAccess.TryProp(line, out var perUnit, "pricePerUnit")) return null;

        // pricePerUnit wraps its money one level deeper than everything else
        // in this document: { price: { amount, currency }, unit }.
        return MoneyReader.Optional(perUnit, options.OrderLineUnit, currency, $"{field}.pricePerUnit", "price");
    }

    /// <summary>
    /// The line's own discount, as the gap between its price before and after.
    ///
    /// Not read from <c>promotions[].discount</c>: that would be a third
    /// number that has to agree with the other two, and when it does not the
    /// receipt silently stops adding up. The promotion is still where the
    /// label comes from.
    /// </summary>
    private static ReceiptDiscount? Discount(JsonElement line, Money gross, Money? net)
    {
        if (net is not { } paid) return null;

        var delta = paid.Value - gross.Value;
        if (delta == 0) return null;

        return new ReceiptDiscount
        {
            // Negative by contract, whichever sign the provider chose.
            Amount = new Money(delta > 0 ? -delta : delta, gross.Currency),
            Label = PromotionLabel(line),
        };
    }

    private static string? PromotionLabel(JsonElement line)
    {
        foreach (var promotion in JsonAccess.Array(line, "promotions"))
        {
            var label = JsonAccess.StrOf(promotion, "description", "type");
            if (!string.IsNullOrWhiteSpace(label)) return label;
        }

        return null;
    }

    /// <summary>
    /// Deposits are money the customer actually paid, so they are lines on the
    /// receipt. Leaving them out is the difference between an order that
    /// reconciles and one that is short by the crate.
    /// </summary>
    private static void AddDeposits(
        List<ReceiptItem> items, JsonElement order, JumboOptions options, string currency, string orderId)
    {
        foreach (var line in JsonAccess.Array(order, "items"))
        {
            foreach (var deposit in JsonAccess.Array(line, "deposits"))
            {
                var name = JsonAccess.StrOf(deposit, "description") ?? options.DepositLabel;
                var unit = MoneyReader.Optional(deposit, options.OrderLineUnit, currency,
                    $"order[{orderId}].deposit[{name}].unitPrice", "unitPrice");
                if (unit is not { } price) continue;

                var quantity = JsonAccess.Quantity(deposit, "quantity") ?? 1m;

                items.Add(new ReceiptItem
                {
                    Name = name,
                    Quantity = quantity,
                    UnitPrice = price,
                    // Multiplied rather than read: the document states a unit
                    // price and a count and no line total, so this is the only
                    // way the deposit reaches the receipt at all. Rounded away
                    // from zero, the same rule MoneyParser uses.
                    Total = new Money(
                        (long)decimal.Round(price.Value * quantity, 0, MidpointRounding.AwayFromZero),
                        price.Currency),
                });
            }
        }
    }

    private static void AddSurcharges(
        List<ReceiptItem> items, JsonElement order, JumboOptions options, string currency, string orderId)
    {
        foreach (var line in JsonAccess.Array(order, "items"))
        {
            foreach (var surcharge in JsonAccess.Array(line, "surcharges"))
            {
                var name = JsonAccess.StrOf(surcharge, "type") ?? "surcharge";
                var value = MoneyReader.Optional(surcharge, options.OrderLineUnit, currency,
                    $"order[{orderId}].surcharge[{name}].value", "value");
                if (value is not { } amount) continue;

                items.Add(new ReceiptItem { Name = name, Total = amount });
            }
        }
    }
}

/// <summary>
/// The handful of decisions both halves of Jumbo share.
/// </summary>
internal static class JumboReceipts
{
    public static Merchant Merchant(string? storeName) => new()
    {
        Id = JumboAdapter.ProviderId,
        Name = "Jumbo",
        StoreName = storeName,
    };

    /// <summary>
    /// The provider's word for how it was paid, mapped onto the four the
    /// normalized schema names. Null stays null - a guess here weakens a
    /// consumer's receipt-to-transaction match without telling it so.
    /// </summary>
    public static string? Method(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        if (raw.Contains("ideal", StringComparison.OrdinalIgnoreCase)) return "ideal";
        if (raw.Contains("cash", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("contant", StringComparison.OrdinalIgnoreCase))
        {
            return "cash";
        }

        return raw.Contains("card", StringComparison.OrdinalIgnoreCase) ||
               raw.Contains("pin", StringComparison.OrdinalIgnoreCase) ||
               raw.Contains("maestro", StringComparison.OrdinalIgnoreCase) ||
               raw.Contains("visa", StringComparison.OrdinalIgnoreCase) ||
               raw.Contains("mastercard", StringComparison.OrdinalIgnoreCase)
            ? "card"
            : "other";
    }
}
