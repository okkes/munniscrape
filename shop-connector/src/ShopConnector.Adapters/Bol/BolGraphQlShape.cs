using System.Text.Json;
using System.Text.Json.Nodes;
using Connector.Kit.Errors;
using Connector.Kit.Normalization;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.Bol;

/// <summary>
/// bol's order overview, as bol's own front end reads it.
///
/// CONFIRMED live on 2026-08-02 against a real account, which is what makes
/// this shape different from the two beside it: the page is a shell, the first
/// orders arrive rendered into it, and every "Toon meer" posts
/// <c>OrdersOverviewClient</c> to <c>/api/graphql</c> as an automatic persisted
/// query - a hash, no document.
///
/// Two things about the payload matter more than the rest.
///
/// The price is <c>listPrice.priceInclVat.amount</c>, a JSON number in MAJOR
/// units, and it is the price of ONE. The page that produced this capture
/// showed "2 x EUR 21,24" against an amount of 21.24, so a line total is
/// quantity times amount. bol's money hazard - both units wrong the same way,
/// reconciling perfectly, undetectable downstream - is settled by that
/// observation rather than by inference.
///
/// And there is no order total anywhere. Summing the lines is the only total
/// available, so every receipt from here is marked
/// <see cref="Receipt.TotalIsDerived"/> and therefore not reconciled: checking
/// our own sum against the lines it came from could never fail, and a check
/// that cannot fail must not be reported as one that passed.
/// </summary>
internal sealed class BolGraphQlShape : IBolOrdersShape
{
    public string Name => "graphql";

    public string ConfigHint =>
        "if bol has shipped a new front end, the persisted-query hash it pins is the first thing to move: " +
        "see BolOptions.OrdersPersistedQueryHash";

    public string Accept => "application/json";

    /// <summary>Always: <c>ShopOrder</c> has no total field at all.</summary>
    public bool TotalIsDerived => true;

    public string Url(BolOptions options, int page)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.GraphQlUrl;
    }

    /// <summary>
    /// The operation, named again in a header. bol's edge rejects the request
    /// with a 400 before GraphQL sees it when this is missing, which is exactly
    /// how this adapter's first live run failed.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> Headers(BolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return [new KeyValuePair<string, string>(options.OperationNameHeader, options.OrdersOperationName)];
    }

    /// <summary>
    /// The cursor is an offset counted in orders, as a string, exactly as bol
    /// sends it: page one asks for <c>"0"</c>, page two for <c>"5"</c>.
    /// </summary>
    public string? Body(BolOptions options, int page)
    {
        ArgumentNullException.ThrowIfNull(options);

        var after = Math.Max(0, page - 1) * Math.Max(1, options.OrdersPageSize);

        return new JsonObject
        {
            ["operationName"] = options.OrdersOperationName,
            ["variables"] = new JsonObject
            {
                [options.AfterVariable] = after.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            ["extensions"] = new JsonObject
            {
                ["persistedQuery"] = new JsonObject
                {
                    ["version"] = 1,
                    ["sha256Hash"] = options.OrdersPersistedQueryHash,
                },
            },
        }.ToJsonString();
    }

    public IReadOnlyList<BolOrder> Parse(string body, BolOptions options, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(options);

        using var document = Parse(body);
        var root = document.RootElement;

        // GraphQL reports failure in the body with a 200, so the errors array
        // is read before the data is trusted at all. A hash bol has forgotten
        // arrives here, not as a status code.
        Refuse(root);

        if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("me", out var me))
        {
            throw ConnectorException.ProviderChanged(
                $"{BolAdapter.ProviderId}: the response carries no data.me.orders; {ConfigHint}");
        }

        // A signed-out request is a 200 with a valid payload that simply has no
        // orders on it. Checked BEFORE the missing-field branch below, because
        // the two are indistinguishable by shape alone and only one of them is
        // a provider change - reporting an expired session as "bol rebuilt its
        // site" sends the user nowhere and an operator hunting for nothing.
        if (string.Equals(
                JsonAccess.StrOf(me, "__typename"), options.AnonymousTypeName, StringComparison.Ordinal))
        {
            throw ConnectorException.SessionExpired(
                $"{BolAdapter.ProviderId}: bol answered as {options.AnonymousTypeName}, so the stored session " +
                "is no longer signed in. bol issues no refresh token, so this needs a new login.");
        }

        if (!me.TryGetProperty("orders", out var orders))
        {
            throw ConnectorException.ProviderChanged(
                $"{BolAdapter.ProviderId}: the response carries no data.me.orders; {ConfigHint}");
        }

        // Null is an answer - an account that has never ordered - and not a
        // shape change.
        if (orders.ValueKind == JsonValueKind.Null) return [];

        if (orders.ValueKind != JsonValueKind.Array)
        {
            throw ConnectorException.ProviderChanged(
                $"{BolAdapter.ProviderId}: data.me.orders is a {orders.ValueKind}, not a list");
        }

        var parsed = new List<BolOrder>();

        foreach (var order in orders.EnumerateArray())
        {
            if (Read(order, options, zone) is { } one) parsed.Add(one);
        }

        return parsed;
    }

    private static BolOrder? Read(JsonElement order, BolOptions options, TimeZoneInfo zone)
    {
        var reference = JsonAccess.StrOf(order, "reference");

        // A row with no reference cannot be keyed, so it cannot be deduped
        // against a later pass either. Skipped rather than invented.
        if (string.IsNullOrWhiteSpace(reference)) return null;

        var items = new List<ReceiptItem>();
        var total = 0L;
        string? seller = null;

        if (order.TryGetProperty("items", out var lines) && lines.ValueKind == JsonValueKind.Array)
        {
            foreach (var line in lines.EnumerateArray())
            {
                if (Read(line, options, out var item, out var lineSeller) is not true) continue;

                items.Add(item);
                total += item.Total.Value;

                // bol is a marketplace and the seller is per LINE, so one
                // order can carry several. Naming the first is honest for the
                // common case and never claims to be the only one.
                seller ??= lineSeller;
            }
        }

        return new BolOrder
        {
            Id = reference,
            PlacedAt = BolDate.Parse(JsonAccess.StrOf(order, "creationDateTime"), zone, "creationDateTime"),
            Total = new Money(total, options.Currency),
            SellerName = seller,
            Items = items,
        };
    }

    private static bool Read(JsonElement line, BolOptions options, out ReceiptItem item, out string? seller)
    {
        item = null!;
        seller = null;

        if (!line.TryGetProperty("offerInformation", out var offer)) return false;

        var name = JsonAccess.StrOf(offer, "productTitle");
        if (string.IsNullOrWhiteSpace(name)) return false;

        if (!offer.TryGetProperty("listPrice", out var listPrice)
            || !listPrice.TryGetProperty("priceInclVat", out var incl))
        {
            return false;
        }

        // Major units, stated as a number. Declared rather than sniffed: the
        // whole point of BolOptions.ItemUnit is that a unit is never inferred
        // from a value.
        var unit = MoneyReader.Require(incl, options.GraphQlAmountUnit, options.Currency, "listPrice.priceInclVat", "amount");

        var quantity = line.TryGetProperty("quantity", out var q) && q.TryGetInt32(out var parsed) ? parsed : 1;
        if (quantity < 1) quantity = 1;

        if (offer.TryGetProperty("retailer", out var retailer))
        {
            var named = JsonAccess.StrOf(retailer, "name");
            if (!string.IsNullOrWhiteSpace(named)) seller = named;
        }

        item = new ReceiptItem
        {
            Name = name,
            Quantity = quantity,
            UnitPrice = unit,
            Total = new Money(unit.Value * quantity, unit.Currency),
        };

        return true;
    }

    private static void Refuse(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errors)
            || errors.ValueKind != JsonValueKind.Array
            || errors.GetArrayLength() == 0)
        {
            return;
        }

        var first = errors[0];
        var message = JsonAccess.StrOf(first, "message") ?? "the operation was refused";

        throw ConnectorException.ProviderChanged(
            $"{BolAdapter.ProviderId}: graphql refused OrdersOverviewClient: {message}. " +
            "A persisted-query hash bol no longer knows reports itself here; " +
            "see BolOptions.OrdersPersistedQueryHash");
    }

    private static JsonDocument Parse(string body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw ConnectorException.ProviderChanged(
                $"{BolAdapter.ProviderId}: the orders response is not JSON ({ex.Message})");
        }
    }
}
