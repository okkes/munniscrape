using System.Text.Json;
using Connector.Kit.Errors;
using Connector.Kit.Normalization;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.AlbertHeijn;

/// <summary>One row of <c>posReceiptsPage</c>, before any detail call.</summary>
internal sealed record AhReceiptSummary
{
    public required string Id { get; init; }

    public required DateTimeOffset PurchasedAt { get; init; }

    /// <summary>
    /// The only total AH states. The detail operation selects none, which is
    /// what makes the two a real reconciliation pair rather than one number
    /// repeated back at us.
    /// </summary>
    public required Money Total { get; init; }
}

/// <summary>
/// Parses Albert Heijn's receipt payloads, which are GraphQL and not REST.
///
/// The field names here are confirmed from <c>gwillem/appie-go</c> v0.0.12
/// and are read exactly - see <see cref="TryField"/> for why this is the one
/// place in the service where alias tolerance would be a mistake. Amounts
/// are JSON numbers in euros and their unit is declared per field by
/// <see cref="AlbertHeijnOptions"/>, never inferred from the value's shape.
/// </summary>
internal static class AlbertHeijnReceiptParser
{
    private const string ProviderId = AlbertHeijnAdapter.ProviderId;

    /// <summary>
    /// GraphQL reports a failure in the body with a 200 status. A client that
    /// only checks the status code returns nothing and calls it an empty
    /// history - which reads to a user as "you have never shopped here", a
    /// worse lie than an outage.
    /// </summary>
    public static void ThrowOnErrors(JsonElement root)
    {
        var errors = JsonAccess.Array(root, "errors");
        if (errors.Count == 0) return;

        // The first message only. The rest are usually the same fault
        // restated per selected field, and a detail nobody can act on is
        // noise in an operator's log.
        var first = errors[0];
        var message = JsonAccess.StrOf(first, "message")
                      ?? (JsonAccess.TryProp(first, out var extensions, "extensions")
                          ? JsonAccess.StrOf(extensions, "code")
                          : null)
                      ?? "unspecified";

        throw ConnectorException.ProviderChanged($"{ProviderId}: graphql returned an error ({message})");
    }

    public static IReadOnlyList<AhReceiptSummary> ParseList(JsonElement root, AlbertHeijnOptions options)
    {
        if (!TryField(root, "posReceiptsPage", out var page))
        {
            throw ConnectorException.ProviderChanged(
                $"{ProviderId}: the response carries no posReceiptsPage; the receipts operation moved");
        }

        // A null page is an answer - an account with nothing on it - and not
        // a shape change.
        if (page.ValueKind == JsonValueKind.Null) return [];

        if (page.ValueKind != JsonValueKind.Object)
        {
            throw ConnectorException.ProviderChanged(
                $"{ProviderId}: posReceiptsPage is a {page.ValueKind}, not an object");
        }

        if (!page.TryGetProperty("posReceipts", out var node))
        {
            throw ConnectorException.ProviderChanged($"{ProviderId}: posReceiptsPage carries no posReceipts");
        }

        if (node.ValueKind == JsonValueKind.Null) return [];

        if (node.ValueKind != JsonValueKind.Array)
        {
            throw ConnectorException.ProviderChanged($"{ProviderId}: posReceipts is a {node.ValueKind}, not a list");
        }

        var zone = RetailZones.Dutch;
        var rows = JsonAccess.AsArray(node);
        var summaries = new List<AhReceiptSummary>(rows.Count);

        foreach (var row in rows)
        {
            var id = JsonAccess.StrOf(row, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;   // a row we cannot key is a row we cannot dedupe

            summaries.Add(new AhReceiptSummary
            {
                Id = id,
                // AH sends a local Dutch wall clock. Interpreted in
                // Europe/Amsterdam rather than the agent's own zone: a
                // near-midnight purchase dated a day out is worse than one
                // that was never matched.
                PurchasedAt = ReceiptTime.Parse(
                    JsonAccess.StrOf(row, "dateTime"), zone, ProviderId, "posReceipt.dateTime"),
                Total = MoneyReader.Require(row, options.ListTotalUnit, options.Currency,
                    "posReceipt.totalAmount", "totalAmount"),
            });
        }

        return summaries;
    }

    /// <summary>
    /// Line items from <c>posReceiptDetails</c>, with the discounts folded in.
    ///
    /// AH states discounts in their own array with nothing linking one to a
    /// product, so each becomes its own negative line rather than being
    /// attached to a guess. Both readings reconcile identically against the
    /// list's stated total; only one of them invents a fact the receipt does
    /// not state.
    /// </summary>
    public static IReadOnlyList<ReceiptItem> ParseItems(JsonElement root, AlbertHeijnOptions options)
    {
        var detail = RequireDetail(root);
        var currency = options.Currency;

        var products = JsonAccess.Array(detail, "products");
        var discounts = JsonAccess.Array(detail, "discounts");
        var items = new List<ReceiptItem>(products.Count + discounts.Count);

        foreach (var product in products)
        {
            var name = JsonAccess.StrOf(product, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;

            var total = MoneyReader.Optional(product, options.ItemAmountUnit, currency,
                $"product[{name}].amount", "amount");
            if (total is not { } amount) continue;

            items.Add(new ReceiptItem
            {
                Name = name,
                Quantity = JsonAccess.Quantity(product, "quantity"),
                // Only ever AH's own unit price. Dividing a total by a
                // quantity produces a number that looks authoritative and is
                // wrong for anything sold by weight.
                UnitPrice = MoneyReader.Optional(product, options.ItemAmountUnit, currency,
                    $"product[{name}].price", "price"),
                Total = amount,
            });
        }

        foreach (var discount in discounts)
        {
            // A confirmed field that is absent is a shape change, and this
            // one moves money: dropping the line would leave every
            // promotion-heavy receipt failing its own arithmetic with nothing
            // to explain why.
            var label = JsonAccess.StrOf(discount, "name")
                        ?? throw ConnectorException.ProviderChanged(
                            $"{ProviderId}: a discount line carries no name");

            var amount = MoneyReader.Require(discount, options.DiscountAmountUnit, currency,
                $"discount[{label}].amount", "amount");

            items.Add(new ReceiptItem
            {
                Name = label,
                // Negative by contract, whichever sign AH chose, so that
                // summing a receipt is unconditional.
                Total = amount.Value > 0 ? amount.Negated() : amount,
            });
        }

        return items;
    }

    /// <summary>
    /// The payment tail, as far as AH states one.
    ///
    /// The confirmed operation selects a method and an amount and nothing
    /// that could carry a card number or an IBAN, so both tails are null by
    /// construction rather than by oversight. They stay explicit: the
    /// consumer matches receipts to transactions partly on the tail and needs
    /// to know its match here will be weaker.
    /// </summary>
    public static ReceiptPayment ParsePayment(JsonElement root, AlbertHeijnOptions options)
    {
        var detail = RequireDetail(root);

        string? method = null;
        var largest = 0L;

        foreach (var payment in JsonAccess.Array(detail, "payments"))
        {
            var stated = Normalize(JsonAccess.StrOf(payment, "method"));
            if (stated is null) continue;

            // A split tender lists more than one leg. The largest is the one
            // a consumer will match a bank transaction against.
            var amount = MoneyReader.Optional(payment, options.PaymentAmountUnit, options.Currency,
                "payment.amount", "amount")?.Abs().Value ?? 0L;

            if (method is not null && amount <= largest) continue;

            method = stated;
            largest = amount;
        }

        return ReceiptFactory.Payment(method);
    }

    public static Merchant Merchant(string? storeName) => new()
    {
        Id = ProviderId,
        Name = "Albert Heijn",
        StoreName = storeName,
    };

    private static JsonElement RequireDetail(JsonElement root)
    {
        if (!TryField(root, "posReceiptDetails", out var detail) || detail.ValueKind != JsonValueKind.Object)
        {
            // Not an empty receipt: a receipt the list just named and the
            // detail then denies is a shape change worth paging someone
            // about, and "you bought nothing" is the wrong thing to emit.
            throw ConnectorException.ProviderChanged($"{ProviderId}: the detail carries no posReceiptDetails");
        }

        return detail;
    }

    /// <summary>
    /// A field under the <c>data</c> envelope, or at the root when a caller
    /// already unwrapped it.
    ///
    /// Exact and case-sensitive, with no aliases - the one place in this
    /// service where tolerance would be a mistake. A GraphQL response can
    /// only answer with the names the query asked for, so accepting a
    /// different spelling could never repair a real payload and would only
    /// hide a query we have stopped sending.
    ///
    /// A JSON null is returned rather than skipped, because "the page is
    /// null" is an answer and telling it apart from "no such field" is the
    /// whole difference between an empty history and a broken one.
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

    /// <summary>Provider payment words mapped onto the four the schema allows.</summary>
    private static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        if (raw.Contains("cash", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("contant", StringComparison.OrdinalIgnoreCase))
        {
            return "cash";
        }

        if (raw.Contains("ideal", StringComparison.OrdinalIgnoreCase)) return "ideal";

        return raw.Contains("card", StringComparison.OrdinalIgnoreCase) ||
               raw.Contains("pin", StringComparison.OrdinalIgnoreCase) ||
               raw.Contains("maestro", StringComparison.OrdinalIgnoreCase)
            ? "card"
            : "other";
    }
}
