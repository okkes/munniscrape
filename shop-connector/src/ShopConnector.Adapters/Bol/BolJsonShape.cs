using System.Globalization;
using System.Text.Json;
using Connector.Kit.Errors;
using Connector.Kit.Normalization;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.Bol;

/// <summary>
/// The other shape: an order list fetched as JSON by the account page.
///
/// Nothing here is confirmed - no JSON order endpoint has been identified for
/// bol's consumer site, and the research that looked for one found only
/// seller-API clients. It is built anyway, and built to the same standard as
/// the HTML shape, because the alternative is discovering at 3am that the
/// page is JSON-backed and having nothing at all. Every name it reaches for
/// is an alias list in <see cref="BolOptions"/>, and its URL is a template:
/// a live run that finds the real endpoint turns this from dead code into the
/// working path with two configuration edits and no release.
///
/// If this shape wins, the provider's tier can probably drop from
/// <c>browser_once</c> toward <c>http</c> - but only after someone has
/// watched a login, because the login is still a JavaScript page with a
/// reCAPTCHA key on hand.
/// </summary>
internal sealed class BolJsonShape : IBolOrdersShape
{
    private const string ProviderId = BolAdapter.ProviderId;

    public string Name => "json";

    public string Accept => "application/json, text/plain, */*";

    public string ConfigHint =>
        "if bol serves this account page as plain HTML, set BolOptions.OrdersShape to Html; " +
        "if it is JSON at a different address, correct BolOptions.JsonOrdersUrlTemplate and " +
        "BolOptions.JsonOrderPaths";

    public string Url(BolOptions options, int page)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.JsonOrdersUrlTemplate
            .Replace("{page}", page.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    public IReadOnlyList<BolOrder> Parse(string body, BolOptions options, TimeZoneInfo zone, bool keepRaw = false)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(options);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            // Non-JSON where JSON was promised is almost always an
            // interstitial the guard did not recognise, so it is a shape
            // change worth paging someone about rather than a parse bug.
            throw new ConnectorException(
                ErrorCode.ProviderChanged,
                $"{ProviderId}: the orders endpoint was read as the '{Name}' shape and did not return JSON. " +
                ConfigHint,
                ex);
        }

        using (document)
        {
            var rows = Rows(document.RootElement, options);
            var orders = new List<BolOrder>(rows.Count);

            foreach (var row in rows)
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                orders.Add(ParseOrder(row, options, zone));
            }

            return orders;
        }
    }

    /// <summary>
    /// The array of orders, at whichever of the candidate paths holds one.
    ///
    /// An array that resolves and is empty is an account with no orders in
    /// this page, which is a perfectly good answer. No path resolving at all
    /// is the shape having moved, and it names every path tried - guessing
    /// which array in an unknown document holds orders is exactly how a
    /// parser silently reports the wrong data.
    /// </summary>
    private IReadOnlyList<JsonElement> Rows(JsonElement root, BolOptions options)
    {
        if (root.ValueKind == JsonValueKind.Array) return JsonAccess.AsArray(root);

        foreach (var path in options.JsonOrderPaths)
        {
            if (!JsonAccess.TryPath(root, path, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Array) return JsonAccess.AsArray(value);

            // A wrapper around the array - { "orders": { "items": [...] } } -
            // is common enough to be worth one hop, and no more than one.
            if (value.ValueKind == JsonValueKind.Object &&
                JsonAccess.TryProp(value, out var nested, "items", "results", "content", "orders") &&
                nested.ValueKind == JsonValueKind.Array)
            {
                return JsonAccess.AsArray(nested);
            }
        }

        throw ConnectorException.ProviderChanged(
            $"{ProviderId}: the orders endpoint was read as the '{Name}' shape and carried no order array; " +
            $"tried [{string.Join(", ", options.JsonOrderPaths)}]. {ConfigHint}");
    }

    private static BolOrder ParseOrder(JsonElement row, BolOptions options, TimeZoneInfo zone)
    {
        var id = JsonAccess.StrOf(row, [.. options.JsonOrderIdNames]);
        if (string.IsNullOrWhiteSpace(id))
        {
            throw ConnectorException.ProviderChanged(
                $"{ProviderId}: an order carries no id under any of " +
                $"[{string.Join(", ", options.JsonOrderIdNames)}]. The external id is what makes a re-run " +
                "idempotent, so an order without one cannot be emitted");
        }

        var currency = MoneyReader.Currency(row, options.Currency);

        return new BolOrder
        {
            Id = id,
            PlacedAt = BolDate.Parse(
                JsonAccess.StrOf(row, [.. options.JsonOrderDateNames]), zone, $"order[{id}].date"),
            // The unit is declared, never sniffed - see BolOptions.JsonAmountUnit,
            // which is the one value on this provider that can be wrong without
            // anything noticing.
            Total = MoneyReader.Require(
                row, options.JsonAmountUnit, currency, $"order[{id}].total", [.. options.JsonOrderTotalNames]),
            SellerName = Seller(row, options),
            Payment = Payment(row, options),
            Items = ParseItems(row, options, id, currency),
        };
    }

    private static string? Seller(JsonElement row, BolOptions options)
    {
        if (!JsonAccess.TryProp(row, out var seller, [.. options.JsonSellerNames])) return null;

        return seller.ValueKind == JsonValueKind.Object
            ? JsonAccess.StrOf(seller, "displayName", "name", "title")
            : JsonAccess.Str(seller);
    }

    private static ReceiptPayment Payment(JsonElement row, BolOptions options)
    {
        var method = JsonAccess.TryProp(row, out var payment, [.. options.JsonPaymentMethodNames])
            ? payment.ValueKind == JsonValueKind.Object
                ? JsonAccess.StrOf(payment, "type", "method", "name", "description")
                : JsonAccess.Str(payment)
            : null;

        var masked = JsonAccess.StrOf(row, [.. options.JsonPaymentMaskNames]);
        if (masked is null && JsonAccess.TryProp(row, out var block, [.. options.JsonPaymentMethodNames]) &&
            block.ValueKind == JsonValueKind.Object)
        {
            masked = JsonAccess.StrOf(block, [.. options.JsonPaymentMaskNames]);
        }

        // Explicit nulls, not an omitted block: the consumer must be able to
        // tell "bol gave us no tail" from "nobody thought about it", because
        // it is what disambiguates two identical purchases on one day.
        return ReceiptFactory.Payment(BolNormalize.Method(method), JsonAccess.Tail(masked));
    }

    private static IReadOnlyList<ReceiptItem> ParseItems(
        JsonElement row, BolOptions options, string orderId, string currency)
    {
        var lines = JsonAccess.Array(row, [.. options.JsonItemArrayNames]);
        var items = new List<ReceiptItem>(lines.Count);

        foreach (var line in lines)
        {
            var name = JsonAccess.StrOf(line, [.. options.JsonItemNameNames]);
            if (string.IsNullOrWhiteSpace(name)) continue;

            var field = $"order[{orderId}].item[{name}]";
            var lineCurrency = MoneyReader.Currency(line, currency);
            var quantity = JsonAccess.Quantity(line, [.. options.JsonItemQuantityNames]);

            var unit = MoneyReader.Optional(
                line, options.JsonAmountUnit, lineCurrency, field + ".unitPrice", [.. options.JsonItemUnitPriceNames]);

            var total = MoneyReader.Optional(
                line, options.JsonAmountUnit, lineCurrency, field + ".total", [.. options.JsonItemTotalNames]);

            Money lineTotal;
            if (total is { } stated) lineTotal = stated;
            else if (unit is { } each) lineTotal = BolNormalize.Extended(each, quantity);
            else continue;

            items.Add(new ReceiptItem
            {
                Name = name,
                Quantity = quantity,
                UnitPrice = unit,
                Total = lineTotal,
                Discount = Discount(line, options, lineCurrency, field),
            });
        }

        return items;
    }
    private static ReceiptDiscount? Discount(
        JsonElement line, BolOptions options, string currency, string field)
    {
        var amount = MoneyReader.Optional(
            line, options.JsonAmountUnit, currency, field + ".discount", [.. options.JsonItemDiscountNames]);

        if (amount is not { } value || value.Value == 0) return null;

        var label = JsonAccess.TryProp(line, out var block, [.. options.JsonItemDiscountNames]) &&
                    block.ValueKind == JsonValueKind.Object
            ? JsonAccess.StrOf(block, "description", "label", "name")
            : null;

        // Providers state a discount with either sign; the schema requires it
        // negative so that summing for reconciliation is unconditional.
        return new ReceiptDiscount
        {
            Amount = value.Value > 0 ? value.Negated() : value,
            Label = label,
        };
    }
}
