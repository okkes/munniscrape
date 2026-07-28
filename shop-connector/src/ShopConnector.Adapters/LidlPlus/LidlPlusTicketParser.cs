using System.Text.Json;
using Connector.Kit.Errors;
using Connector.Kit.Normalization;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.LidlPlus;

/// <summary>One row of the v2 ticket list.</summary>
internal sealed record LidlTicketSummary
{
    public required string Id { get; init; }

    public required DateTimeOffset PurchasedAt { get; init; }

    public required Money Total { get; init; }

    public string? StoreName { get; init; }
}

/// <summary>
/// Parses the v2 list and the v3 detail.
///
/// The endpoints and headers are confirmed; the payload field names are not,
/// so every read takes a candidate list. Amounts are the exception to
/// tolerance: their unit comes from <see cref="LidlPlusOptions.AmountUnit"/>
/// and is never inferred from the value's shape.
/// </summary>
internal static class LidlPlusTicketParser
{
    private const string ProviderId = LidlPlusAdapter.ProviderId;

    public static IReadOnlyList<LidlTicketSummary> ParseList(
        JsonElement root, LidlPlusOptions options, TimeZoneInfo zone)
    {
        var rows = root.ValueKind == JsonValueKind.Array
            ? JsonAccess.AsArray(root)
            : JsonAccess.Array(root, "tickets", "records", "items", "data");

        if (rows.Count == 0 && root.ValueKind == JsonValueKind.Object &&
            !JsonAccess.TryProp(root, out _, "tickets", "records", "items", "data", "totalCount", "page", "size"))
        {
            throw ConnectorException.ProviderChanged($"{ProviderId}: ticket list is not a recognisable envelope");
        }

        var summaries = new List<LidlTicketSummary>(rows.Count);
        foreach (var row in rows)
        {
            var id = JsonAccess.StrOf(row, "id", "ticketId", "identifier");
            if (string.IsNullOrWhiteSpace(id)) continue;

            var moment = JsonAccess.StrOf(row, "date", "ticketDate", "purchaseDate", "dateTime");
            var currency = MoneyReader.Currency(row, options.Currency);

            summaries.Add(new LidlTicketSummary
            {
                Id = id,
                PurchasedAt = ReceiptTime.Parse(moment, zone, ProviderId, "ticket.date"),
                Total = MoneyReader.Require(row, options.AmountUnit, currency,
                    "ticket.total", "totalAmount", "total", "amount"),
                StoreName = JsonAccess.StrOf(row, "storeName", "store", "storeCode"),
            });
        }

        return summaries;
    }

    /// <summary>
    /// Line items from a v3 ticket detail. Lidl states its discounts as
    /// their own collection per line, which is the shape reconciliation
    /// wants: item totals plus (negative) discounts must equal the stated
    /// total.
    /// </summary>
    public static IReadOnlyList<ReceiptItem> ParseItems(JsonElement detail, LidlPlusOptions options)
    {
        var lines = JsonAccess.Array(detail, "itemsLine", "itemsLines", "items", "lines");
        var currency = MoneyReader.Currency(detail, options.Currency);
        var items = new List<ReceiptItem>(lines.Count);

        foreach (var line in lines)
        {
            var name = JsonAccess.StrOf(line, "name", "description", "productName");
            if (string.IsNullOrWhiteSpace(name)) continue;

            var total = MoneyReader.Optional(line, options.AmountUnit, currency,
                $"item[{name}].total", "extendedAmount", "originalAmount", "totalAmount", "amount");
            if (total is not { } amount) continue;

            items.Add(new ReceiptItem
            {
                Name = name,
                Quantity = JsonAccess.Quantity(line, "quantity", "units", "amountOfProducts"),
                UnitPrice = MoneyReader.Optional(line, options.AmountUnit, currency,
                    $"item[{name}].unitPrice", "currentUnitPrice", "unitPrice", "pricePerUnit"),
                Total = amount,
                Discount = ParseDiscount(line, options, currency, name),
            });
        }

        return items;
    }

    public static ReceiptPayment ParsePayment(JsonElement detail)
    {
        var payments = JsonAccess.Array(detail, "payments", "paymentMethods", "tenderTypes");
        foreach (var payment in payments)
        {
            var masked = JsonAccess.StrOf(payment, "cardNumber", "maskedCardNumber", "last4", "accountNumber");
            var method = JsonAccess.StrOf(payment, "type", "description", "paymentType");

            if (masked is not null || method is not null)
            {
                return ReceiptFactory.Payment(Normalize(method), JsonAccess.Tail(masked));
            }
        }

        // Explicit nulls, not an omitted block: the consumer must be able to
        // tell "Lidl gave us no tail" from "nobody thought about it".
        return ReceiptFactory.Payment();
    }

    public static Merchant Merchant(string? storeName) => new()
    {
        Id = ProviderId,
        Name = "Lidl",
        StoreName = storeName,
    };

    /// <summary>
    /// Sums a line's discounts into one negative amount. Without them a
    /// receipt's items do not add up to its total, which fails
    /// reconciliation and would mark every promotion-heavy shop as suspect.
    /// </summary>
    private static ReceiptDiscount? ParseDiscount(
        JsonElement line, LidlPlusOptions options, string currency, string itemName)
    {
        var discounts = JsonAccess.Array(line, "discounts", "itemDiscounts", "promotions");
        if (discounts.Count == 0) return null;

        var total = Money.Zero(currency);
        string? label = null;

        foreach (var discount in discounts)
        {
            var amount = MoneyReader.Optional(discount, options.AmountUnit, currency,
                $"item[{itemName}].discount", "amount", "discountAmount", "value");
            if (amount is not { } value) continue;

            // Providers state a discount as either sign; the schema requires
            // it negative so that summing is unconditional.
            total += value.Value > 0 ? value.Negated() : value;
            label ??= JsonAccess.StrOf(discount, "description", "label", "name");
        }

        return total.Value == 0 ? null : new ReceiptDiscount { Amount = total, Label = label };
    }

    private static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        if (raw.Contains("cash", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("bar", StringComparison.OrdinalIgnoreCase))
        {
            return "cash";
        }

        return raw.Contains("card", StringComparison.OrdinalIgnoreCase) ||
               raw.Contains("maestro", StringComparison.OrdinalIgnoreCase) ||
               raw.Contains("visa", StringComparison.OrdinalIgnoreCase) ||
               raw.Contains("pin", StringComparison.OrdinalIgnoreCase)
            ? "card"
            : "other";
    }
}
