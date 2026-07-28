using System.Globalization;
using System.Text.Json;
using Connector.Kit.Errors;
using Connector.Kit.Normalization;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.MagentoGuest;

/// <summary>
/// Turns one <c>CustomerOrder</c> into one normalized receipt.
///
/// Every field name read here is declared in
/// <c>SalesGraphQl/etc/schema.graphqls</c>, and the reads are exact rather
/// than alias-tolerant for the same reason Albert Heijn's are: a GraphQL
/// response can only answer with the names the query asked for, so accepting
/// a different spelling could never repair a real payload and would only
/// hide a query we have stopped sending.
///
/// The one genuinely hard part is tax. Magento's <c>product_sale_price</c> is
/// a unit price that includes tax or not depending on a per-shop admin
/// setting the response never states - so the arithmetic that makes a
/// receipt reconcile has two forms, and which one applies is settled from the
/// order's own subtotals. See <see cref="PricesIncludeTax"/>.
/// </summary>
internal static class MagentoGuestOrderParser
{
    private const string ProviderId = MagentoGuestAdapter.ProviderId;

    /// <summary>
    /// GraphQL reports a failure in the body with a 200 status, so the
    /// errors array is read before any data is trusted.
    ///
    /// The categories are not decoration. <c>graphql-no-such-entity</c> is
    /// the single answer Magento gives for a wrong number, a wrong surname,
    /// a wrong e-mail and an undecryptable token - deliberately
    /// indistinguishable, which is good of them and means the user cannot be
    /// told which part they mistyped. <c>graphql-authorization</c> means
    /// something else entirely: the order exists but was placed by a signed-in
    /// customer, so the guest path will never return it however correct the
    /// reference is.
    /// </summary>
    public static void ThrowOnErrors(JsonElement root, MagentoGuestOptions options)
    {
        var errors = JsonAccess.Array(root, "errors");
        if (errors.Count == 0) return;

        var first = errors[0];
        var message = JsonAccess.StrOf(first, "message") ?? "unspecified";
        var category = JsonAccess.TryProp(first, out var extensions, "extensions")
            ? JsonAccess.StrOf(extensions, "category")
            : null;

        if (string.Equals(category, options.NotFoundCategory, StringComparison.Ordinal))
        {
            // The reference is the credential here, so a reference that
            // matches no order is a credential failure - and the platform
            // never retries one, which is exactly right against a shop that
            // may be counting attempts.
            throw ConnectorException.InvalidCredentials(
                $"{ProviderId}: the shop could not locate an order for that reference");
        }

        if (string.Equals(category, options.AuthorizationCategory, StringComparison.Ordinal))
        {
            // Not a credential problem and not a wall: a permanent property
            // of that order. Saying "wrong password" here would send someone
            // to re-read a confirmation mail that is perfectly correct.
            throw ConnectorException.Unsupported(
                $"{ProviderId}: that order was placed with an account at the shop, " +
                "so the guest lookup cannot read it");
        }

        throw ConnectorException.ProviderChanged(
            $"{ProviderId}: graphql returned an error ({message}; category '{category ?? "none"}')");
    }

    /// <summary>The order object under <c>data</c>, or a stated shape change.</summary>
    public static JsonElement RequireOrder(JsonElement root, string field)
    {
        if (!TryField(root, field, out var order) || order.ValueKind == JsonValueKind.Null)
        {
            throw ConnectorException.ProviderChanged(
                $"{ProviderId}: the response carries no {field}; the guest order operation moved");
        }

        if (order.ValueKind != JsonValueKind.Object)
        {
            throw ConnectorException.ProviderChanged($"{ProviderId}: {field} is a {order.ValueKind}, not an object");
        }

        return order;
    }

    public static Receipt Parse(
        JsonElement order,
        MagentoGuestOptions options,
        string sessionId,
        string merchantHost,
        TimeZoneInfo zone)
    {
        var number = JsonAccess.StrOf(order, "number");
        if (string.IsNullOrWhiteSpace(number))
        {
            // Without it there is nothing to key the record on, and a record
            // that cannot be keyed cannot be deduplicated on a re-run.
            throw ConnectorException.ProviderChanged($"{ProviderId}: the order carries no number");
        }

        var purchasedAt = ParseOrderDate(JsonAccess.StrOf(order, "order_date"), options, zone);

        if (!JsonAccess.TryProp(order, out var total, "total") || total.ValueKind != JsonValueKind.Object)
        {
            throw ConnectorException.ProviderChanged($"{ProviderId}: the order carries no total");
        }

        var currency = JsonAccess.TryProp(total, out var grand, "grand_total")
            ? MoneyReader.Currency(grand, options.Currency)
            : options.Currency;

        var grandTotal = MoneyReader.Require(
            total, options.MoneyValueUnit, currency, "total.grand_total", "grand_total");

        var items = BuildItems(order, total, options, currency);

        return ReceiptFactory.Build(
            sessionId,
            number,
            // The shop, not the platform. A consumer handed fifty receipts
            // all labelled "magento" has lost the only fact that made them
            // worth having.
            new Merchant { Id = merchantHost, Name = merchantHost, StoreName = null },
            purchasedAt,
            grandTotal,
            ParsePayment(order),
            items);
    }

    /// <summary>
    /// Day first, and matched exactly.
    ///
    /// <c>DATETIME_SLASH_PHP_FORMAT</c> is <c>'d/m/Y H:i:s'</c>, so
    /// "07/09/2026" is 7 September. An invariant-culture parse reads that
    /// same string as 9 July and says nothing - a two-month error on every
    /// order placed before the 13th of a month. Older shops emit
    /// <c>getCreatedAt()</c> unformatted instead, which is ISO and
    /// unambiguous, so that falls through to the shared reader.
    /// </summary>
    public static DateTimeOffset ParseOrderDate(string? raw, MagentoGuestOptions options, TimeZoneInfo zone)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw ConnectorException.ProviderChanged($"{ProviderId}: order_date is missing");
        }

        var text = raw.Trim();

        foreach (var format in options.OrderDateFormats)
        {
            if (!DateTime.TryParseExact(text, format, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var wallClock))
            {
                continue;
            }

            // Kind must be Unspecified for GetUtcOffset to read the value as
            // local to the shop's zone rather than to whichever machine the
            // control plane happens to be on.
            var unspecified = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);
            return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified));
        }

        return ReceiptTime.Parse(text, zone, ProviderId, "order_date");
    }

    /// <summary>
    /// Which side of tax the item prices are on, decided from the order's own
    /// numbers.
    ///
    /// This is not the money-unit heuristic the platform forbids - the unit
    /// is declared in <see cref="MagentoGuestOptions.MoneyValueUnit"/> and
    /// nothing here looks at the shape of a value. It is a different
    /// question, which Magento answers nowhere in the payload and answers
    /// twice by accident: it states both subtotals, and the item lines can
    /// only sum to one of them. Where they sum to both (a zero-tax order) or
    /// to neither (a shape we have not seen), the net reading is used and
    /// reconciliation flags the result rather than anything being asserted.
    ///
    /// The tolerance widens by one cent per line because Magento rounds each
    /// line itself, and a fifty-line order can legitimately be fifty cents
    /// away from its own subtotal.
    /// </summary>
    public static bool PricesIncludeTax(long itemSum, Money? inclusive, Money? exclusive, int lineCount, bool? declared)
    {
        if (declared is { } forced) return forced;

        var tolerance = Math.Max(Reconciliation.ToleranceMinorUnits, lineCount);
        var matchesInclusive = inclusive is { } incl && Math.Abs(itemSum - incl.Value) <= tolerance;
        var matchesExclusive = exclusive is { } excl && Math.Abs(itemSum - excl.Value) <= tolerance;

        return matchesInclusive && !matchesExclusive;
    }

    private static IReadOnlyList<ReceiptItem> BuildItems(
        JsonElement order, JsonElement total, MagentoGuestOptions options, string currency)
    {
        var unit = options.MoneyValueUnit;
        var lines = new List<ReceiptItem>();
        var itemSum = 0L;

        foreach (var item in JsonAccess.Array(order, "items"))
        {
            var name = JsonAccess.StrOf(item, "product_name") ?? JsonAccess.StrOf(item, "product_sku");
            if (string.IsNullOrWhiteSpace(name)) continue;

            var unitPrice = MoneyReader.Optional(
                item, unit, currency, $"item[{name}].product_sale_price", "product_sale_price");

            if (unitPrice is not { } price) continue;

            // A missing quantity is treated as one rather than failing the
            // whole receipt. The line total is then wrong for a multi-buy,
            // and reconciliation says so loudly - which beats losing a real
            // purchase over a field Magento declares nullable.
            var quantity = JsonAccess.Quantity(item, "quantity_ordered") ?? 1m;
            var lineTotal = Scale(price, quantity);

            itemSum += lineTotal.Value;

            lines.Add(new ReceiptItem
            {
                Name = name,
                Quantity = quantity,
                UnitPrice = price,
                Total = lineTotal,
            });
        }

        var includesTax = PricesIncludeTax(
            itemSum,
            MoneyReader.Optional(total, unit, currency, "total.subtotal_incl_tax", "subtotal_incl_tax"),
            MoneyReader.Optional(total, unit, currency, "total.subtotal_excl_tax", "subtotal_excl_tax"),
            lines.Count,
            options.ItemPricesIncludeTax);

        // The shop's own description of the delivery where it states one -
        // "Flat Rate - Fixed", "PostNL" - and a configured word only where it
        // does not. It sits on the order, not on the totals.
        var shippingName = JsonAccess.StrOf(order, "shipping_method") ?? options.ShippingLineName;
        AppendShipping(lines, total, options, currency, includesTax, shippingName);

        if (!includesTax)
        {
            // Only where the item lines are net of tax. Adding it on top of
            // gross lines would count every cent of item tax twice.
            var tax = MoneyReader.Optional(total, unit, currency, "total.total_tax", "total_tax");
            if (tax is { Value: > 0 } amount)
            {
                lines.Add(new ReceiptItem { Name = options.TaxLineName, Total = amount });
            }
        }

        AppendDiscounts(lines, total, options, currency);
        return lines;
    }

    /// <summary>
    /// Shipping, on whichever side of tax the item lines are.
    ///
    /// CONFIRMED from <c>Resolver/OrderTotal.php</c>: <c>total_shipping</c> is
    /// <c>getShippingAmount()</c> - net of tax - while
    /// <c>shipping_handling.amount_including_tax</c> is
    /// <c>getShippingInclTax()</c>. Mixing the two bases is how a receipt
    /// ends up a euro and change away from its own total.
    /// </summary>
    private static void AppendShipping(
        List<ReceiptItem> lines, JsonElement total, MagentoGuestOptions options,
        string currency, bool includesTax, string shippingName)
    {
        var unit = options.MoneyValueUnit;

        Money? shipping = null;

        if (includesTax && JsonAccess.TryProp(total, out var handling, "shipping_handling"))
        {
            shipping = MoneyReader.Optional(
                handling, unit, currency, "total.shipping_handling.amount_including_tax", "amount_including_tax");
        }

        shipping ??= MoneyReader.Optional(total, unit, currency, "total.total_shipping", "total_shipping");

        if (shipping is not { Value: > 0 } amount) return;

        lines.Add(new ReceiptItem { Name = shippingName, Total = amount });
    }

    /// <summary>
    /// Order-level discounts, as their own negative lines.
    ///
    /// Magento links nothing here to a product - <c>OrderTotal.discounts</c>
    /// is the cart's promotions, stated once - so each becomes its own line
    /// rather than being attached to a guess. Amounts arrive positive
    /// (<c>abs()</c> in the resolver) and are negated here, so summing a
    /// receipt is unconditional.
    /// </summary>
    private static void AppendDiscounts(
        List<ReceiptItem> lines, JsonElement total, MagentoGuestOptions options, string currency)
    {
        foreach (var discount in JsonAccess.Array(total, "discounts"))
        {
            var label = JsonAccess.StrOf(discount, "label") ?? "discount";
            var amount = MoneyReader.Optional(
                discount, options.MoneyValueUnit, currency, $"total.discounts[{label}].amount", "amount");

            if (amount is not { } value || value.Value == 0) continue;

            lines.Add(new ReceiptItem
            {
                Name = label,
                Total = value.Value > 0 ? value.Negated() : value,
            });
        }
    }

    /// <summary>
    /// The payment tail, as far as Magento states one.
    ///
    /// <c>OrderPaymentMethod</c> carries a name and a method code and nothing
    /// that could hold a card or IBAN tail, so both stay explicitly null -
    /// the consumer matches receipts to transactions partly on the tail and
    /// needs to know its match here will be weaker.
    /// </summary>
    private static ReceiptPayment ParsePayment(JsonElement order)
    {
        foreach (var payment in JsonAccess.Array(order, "payment_methods"))
        {
            var method = Normalize(JsonAccess.StrOf(payment, "type"))
                         ?? Normalize(JsonAccess.StrOf(payment, "name"));

            if (method is not null) return ReceiptFactory.Payment(method);
        }

        return ReceiptFactory.Payment();
    }

    /// <summary>Multiplies a unit price by a quantity in minor units, never in floating point.</summary>
    private static Money Scale(Money unitPrice, decimal quantity) => unitPrice with
    {
        Value = checked((long)decimal.Round(unitPrice.Value * quantity, 0, MidpointRounding.AwayFromZero)),
    };

    /// <summary>
    /// A field under the <c>data</c> envelope, or at the root when a caller
    /// already unwrapped it. Exact and case-sensitive - see the type comment.
    /// </summary>
    private static bool TryField(JsonElement root, string name, out JsonElement value)
    {
        value = default;
        if (root.ValueKind != JsonValueKind.Object) return false;

        if (root.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty(name, out value))
        {
            return true;
        }

        return root.TryGetProperty(name, out value);
    }

    /// <summary>
    /// Magento payment codes and labels mapped onto the four words the
    /// schema allows. The gateways are what a Dutch shop actually runs -
    /// Mollie, Buckaroo and Adyen all name the rail in the method code.
    /// </summary>
    private static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        if (raw.Contains("ideal", StringComparison.OrdinalIgnoreCase)) return "ideal";

        if (raw.Contains("cash", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("contant", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("cashondelivery", StringComparison.OrdinalIgnoreCase))
        {
            return "cash";
        }

        return raw.Contains("card", StringComparison.OrdinalIgnoreCase) ||
               raw.Contains("creditcard", StringComparison.OrdinalIgnoreCase) ||
               raw.Contains("maestro", StringComparison.OrdinalIgnoreCase) ||
               raw.Contains("visa", StringComparison.OrdinalIgnoreCase)
            ? "card"
            : "other";
    }
}
