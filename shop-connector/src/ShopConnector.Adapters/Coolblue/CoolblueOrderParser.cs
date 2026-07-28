using System.Text.Json;
using Connector.Kit.Errors;
using Connector.Kit.Normalization;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.Coolblue;

/// <summary>One order, normalised as far as an unknown payload allows.</summary>
internal sealed record CoolblueOrder
{
    public required string Id { get; init; }

    public required DateTimeOffset PurchasedAt { get; init; }

    public required Money Total { get; init; }

    public IReadOnlyList<ReceiptItem> Items { get; init; } = [];

    public ReceiptPayment Payment { get; init; } = ReceiptFactory.Payment();

    /// <summary>Only where an order was collected at a physical store.</summary>
    public string? StoreName { get; init; }
}

/// <summary>
/// Parses whatever Coolblue's order endpoint turns out to answer with.
///
/// Every name this reads comes from <see cref="CoolblueOptions"/> and every
/// one of them is a candidate rather than a fact, because no response has ever
/// been observed. That makes the parser strict where a tolerant one would lie:
/// a path list that matches the wrong array, or rows that carry no id, is
/// reported as a shape change and never quietly skipped. "You have bought
/// nothing" is a worse answer than an outage.
///
/// Amounts are the one thing no amount of tolerance applies to. Their unit
/// arrives on the <see cref="CoolblueFetchPlan"/>, declared by an operator from
/// a capture, and is never inferred from the value's shape.
/// </summary>
internal static class CoolblueOrderParser
{
    private const string ProviderId = CoolblueAdapter.ProviderId;

    /// <summary>The order array, then every row in it.</summary>
    public static IReadOnlyList<CoolblueOrder> Parse(
        JsonElement root, CoolblueOptions options, CoolblueFetchPlan plan, TimeZoneInfo zone, bool wantsItems)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(plan);

        var rows = Rows(root, options);
        var orders = new List<CoolblueOrder>(rows.Count);
        var sawAnyItemCollection = false;

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var currency = MoneyReader.Currency(row, options.Currency);

            // Refused, not skipped. On a payload nobody has ever seen, a row
            // without an id is far more likely to mean the path list matched
            // some other array entirely than to mean one odd order - and a
            // parser that skips its way to an empty list reports a shape change
            // as an empty history.
            var id = JsonAccess.StrOf(row, Names(options.IdNames))
                     ?? throw Changed(
                         $"order at index {index} carries no id under any of [{Join(options.IdNames)}]. " +
                         "Either the id property is named something else, or one of the order_paths matched " +
                         "an array that is not orders");

            IReadOnlyList<ReceiptItem> items = [];

            if (wantsItems)
            {
                var lines = JsonAccess.Array(row, Names(options.ItemsNames));
                if (lines.Count > 0) sawAnyItemCollection = true;

                items = ParseItems(lines, row, options, plan, currency, id);
            }

            orders.Add(new CoolblueOrder
            {
                Id = id,
                // With a real offset, always. Coolblue has never been observed
                // stating one, so a bare wall clock or a bare date is read in
                // Europe/Amsterdam rather than in whatever zone the agent
                // happens to run in - a near-midnight order dated a day out is
                // worse than one that was never matched.
                PurchasedAt = ReceiptTime.Parse(
                    JsonAccess.StrOf(row, Names(options.DateNames)), zone, ProviderId,
                    $"order[{id}].date (tried [{Join(options.DateNames)}])"),
                Total = MoneyReader.Require(row, plan.TotalUnit, currency,
                    $"order[{id}].total", Names(options.TotalNames)),
                Items = items,
                Payment = ParsePayment(row, options),
                StoreName = JsonAccess.StrOf(row, Names(options.StoreNames)),
            });
        }

        // Asked for items, and not one order in the whole page had a line
        // collection under any candidate name. That is the item property being
        // named something we do not know, not a customer who bought nothing:
        // every order has at least one line by definition. Emitting the
        // receipts anyway would hand the consumer totals that reconcile
        // trivially - Reconciliation passes an empty item list - which is the
        // most convincing kind of wrong.
        if (wantsItems && orders.Count > 0 && !sawAnyItemCollection)
        {
            throw Changed(
                "items were requested and no order carries a line collection under any of " +
                $"[{Join(options.ItemsNames)}]. Capture one order's response body and set items_names");
        }

        return orders;
    }

    /// <summary>
    /// The order array. Candidate paths in order, then a bare array response,
    /// and nothing else - hunting for "the array that looks like orders" in an
    /// unknown document is how a parser silently reports the wrong data.
    /// </summary>
    public static IReadOnlyList<JsonElement> Rows(JsonElement root, CoolblueOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        foreach (var path in options.OrderPaths)
        {
            if (JsonAccess.TryPath(root, path, out var node) && node.ValueKind == JsonValueKind.Array)
            {
                return JsonAccess.AsArray(node);
            }
        }

        if (root.ValueKind == JsonValueKind.Array) return JsonAccess.AsArray(root);

        throw Changed(
            $"no order array at any of [{Join(options.OrderPaths)}], and the response is not a bare array. " +
            "This is the endpoint's shape, which has never been captured - see order_paths");
    }

    public static Merchant Merchant(string? storeName) => new()
    {
        Id = ProviderId,
        Name = "Coolblue",
        StoreName = storeName,
    };

    /// <summary>
    /// Line items, plus any order-level discount as its own negative line.
    ///
    /// An order-level discount has nothing linking it to a product, so it
    /// becomes its own line rather than being attached to a guess. Both
    /// readings reconcile identically against the stated total; only one of
    /// them invents a fact the order does not state.
    /// </summary>
    private static IReadOnlyList<ReceiptItem> ParseItems(
        IReadOnlyList<JsonElement> lines, JsonElement row, CoolblueOptions options,
        CoolblueFetchPlan plan, string currency, string orderId)
    {
        // Guaranteed present by the plan whenever items were asked for, so
        // there is no fallback unit here - there is no such thing as a
        // fallback money unit in this service.
        var itemUnit = plan.ItemUnit!.Value;
        var discountUnit = plan.DiscountUnit ?? itemUnit;

        var items = new List<ReceiptItem>(lines.Count + 1);

        foreach (var line in lines)
        {
            var name = JsonAccess.StrOf(line, Names(options.ItemNameNames));
            if (string.IsNullOrWhiteSpace(name)) continue;

            var total = MoneyReader.Optional(line, itemUnit, currency,
                $"order[{orderId}].item[{name}].total", Names(options.ItemTotalNames));
            if (total is not { } amount) continue;

            items.Add(new ReceiptItem
            {
                Name = name,
                Quantity = JsonAccess.Quantity(line, Names(options.ItemQuantityNames)),
                // Only ever Coolblue's own figure. Dividing a total by a
                // quantity produces a number that looks authoritative and is
                // wrong the moment a line carries a bundle discount.
                UnitPrice = MoneyReader.Optional(line, itemUnit, currency,
                    $"order[{orderId}].item[{name}].unitPrice", Names(options.ItemUnitPriceNames)),
                Total = amount,
                Discount = ParseDiscount(
                    JsonAccess.Array(line, Names(options.ItemDiscountNames)),
                    options, discountUnit, currency, $"order[{orderId}].item[{name}]"),
            });
        }

        var orderDiscounts = JsonAccess.Array(row, Names(options.OrderDiscountNames));
        if (orderDiscounts.Count > 0 &&
            ParseDiscount(orderDiscounts, options, discountUnit, currency, $"order[{orderId}]") is { } folded)
        {
            items.Add(new ReceiptItem
            {
                Name = folded.Label ?? "discount",
                Total = folded.Amount,
            });
        }

        return items;
    }

    /// <summary>
    /// Sums a discount collection into one negative amount. Without the
    /// discount lines an order's items do not add up to its total, which fails
    /// reconciliation and would mark every promotion as suspect.
    /// </summary>
    private static ReceiptDiscount? ParseDiscount(
        IReadOnlyList<JsonElement> discounts, CoolblueOptions options,
        MoneyUnit unit, string currency, string field)
    {
        if (discounts.Count == 0) return null;

        var total = Money.Zero(currency);
        string? label = null;

        foreach (var discount in discounts)
        {
            var amount = MoneyReader.Optional(discount, unit, currency,
                $"{field}.discount", Names(options.DiscountAmountNames));
            if (amount is not { } value) continue;

            // Providers state a discount as either sign; the schema requires it
            // negative so that summing a receipt is unconditional.
            total += value.Value > 0 ? value.Negated() : value;
            label ??= JsonAccess.StrOf(discount, Names(options.DiscountLabelNames));
        }

        return total.Value == 0 ? null : new ReceiptDiscount { Amount = total, Label = label };
    }

    /// <summary>
    /// The payment method and, where one is stated, the tail of the
    /// instrument.
    ///
    /// Deliberately reads no payment AMOUNT: that would need a fourth money
    /// unit nobody has captured, and it buys nothing the consumer needs. The
    /// tails stay explicit rather than omitted - the consumer matches receipts
    /// to bank transactions partly on them and has to know when the match will
    /// be weaker.
    /// </summary>
    private static ReceiptPayment ParsePayment(JsonElement row, CoolblueOptions options)
    {
        if (!JsonAccess.TryProp(row, out var node, Names(options.PaymentNames))) return ReceiptFactory.Payment();

        IReadOnlyList<JsonElement> candidates =
            node.ValueKind == JsonValueKind.Array ? JsonAccess.AsArray(node) : [node];

        foreach (var payment in candidates)
        {
            // A bare string is a payment method all by itself, which is how
            // half the webshops in the world state one.
            if (payment.ValueKind == JsonValueKind.String)
            {
                return ReceiptFactory.Payment(Normalize(payment.GetString()));
            }

            var method = JsonAccess.StrOf(payment, Names(options.PaymentMethodNames));
            var masked = JsonAccess.StrOf(payment, Names(options.PaymentMaskedNames));

            if (method is not null || masked is not null)
            {
                return ReceiptFactory.Payment(Normalize(method), JsonAccess.Tail(masked));
            }
        }

        return ReceiptFactory.Payment();
    }

    /// <summary>Provider payment words mapped onto the four the schema allows.</summary>
    private static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        if (raw.Contains("ideal", StringComparison.OrdinalIgnoreCase)) return "ideal";

        if (raw.Contains("cash", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("contant", StringComparison.OrdinalIgnoreCase))
        {
            return "cash";
        }

        return raw.Contains("card", StringComparison.OrdinalIgnoreCase) ||
               raw.Contains("maestro", StringComparison.OrdinalIgnoreCase) ||
               raw.Contains("visa", StringComparison.OrdinalIgnoreCase) ||
               raw.Contains("mastercard", StringComparison.OrdinalIgnoreCase) ||
               raw.Contains("pin", StringComparison.OrdinalIgnoreCase)
            ? "card"
            : "other";
    }

    /// <summary>
    /// An operator-editable candidate list as the array the alias-tolerant
    /// readers take. Explicit rather than a collection expression at each call
    /// site, so a <c>params</c> overload can never bind the list as a single
    /// element.
    /// </summary>
    private static string[] Names(IReadOnlyList<string> names) => [.. names];

    private static string Join(IReadOnlyList<string> names) => string.Join(", ", names);

    private static ConnectorException Changed(string detail) =>
        ConnectorException.ProviderChanged($"{ProviderId}: {detail}");
}
