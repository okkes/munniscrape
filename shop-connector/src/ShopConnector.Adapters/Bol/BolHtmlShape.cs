using System.Globalization;
using Connector.Kit.Errors;
using Connector.Kit.Normalization;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.Bol;

/// <summary>
/// The default shape: the account page's own markup.
///
/// Nobody on this project has seen the page - it needs an account, and this
/// service does not spend a login to look - so every hook is a candidate list
/// in <see cref="BolOptions"/> and every miss is reported with the list in
/// the message. That is not defeatism, it is the only honest way to ship a
/// parser for a page you have not read: an operator with devtools open fixes
/// it in configuration, and the failure already tells them which list to fix.
///
/// One rule is not tolerant, on purpose. A block that matched but is missing
/// its id, its date or its total is
/// <see cref="ErrorCode.ProviderChanged"/> rather than a skipped row: quietly
/// dropping orders whose markup moved would show a user a shorter history
/// than they have and nothing would ever say so.
/// </summary>
internal sealed class BolHtmlShape : IBolOrdersShape
{
    private const string ProviderId = BolAdapter.ProviderId;

    public string Name => "html";

    public string Accept => "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";

    public string ConfigHint =>
        "if bol serves this page's orders as JSON, set BolOptions.OrdersShape to Json and " +
        "BolOptions.JsonOrdersUrlTemplate to the endpoint devtools names";

    public string Url(BolOptions options, int page)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.HtmlOrdersUrlTemplate
            .Replace("{page}", page.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    public IReadOnlyList<BolOrder> Parse(string body, BolOptions options, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(options);

        var blocks = BolHtml.All(body, options.OrderBlockSelectors);

        if (blocks.Count == 0)
        {
            // "The page changed" and "this shopper has never ordered anything"
            // are the same observation - no blocks - and the honest answers to
            // them are opposite. Only the page can tell them apart.
            if (IsEmptyState(body, options)) return [];

            throw ConnectorException.ProviderChanged(
                $"{ProviderId}: the orders page was read as the '{Name}' shape and no order block matched; " +
                $"tried [{string.Join(", ", options.OrderBlockSelectors)}]. {ConfigHint}");
        }

        var orders = new List<BolOrder>(blocks.Count);
        foreach (var block in blocks) orders.Add(ParseOrder(block, options, zone));

        return orders;
    }

    private static bool IsEmptyState(string body, BolOptions options)
    {
        if (BolHtml.First(body, options.EmptyStateSelectors) is not null) return true;

        return options.EmptyStateMarkers.Any(marker => body.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static BolOrder ParseOrder(HtmlElement block, BolOptions options, TimeZoneInfo zone)
    {
        var id = OrderId(block, options);

        return new BolOrder
        {
            Id = id,
            PlacedAt = BolDate.Parse(OrderDate(block, options), zone, $"order[{id}].date"),
            Total = Amount(
                BolHtml.TextOf(block.Inner, options.OrderTotalSelectors),
                options.HtmlAmountUnit, options.Currency, $"order[{id}].total", options.OrderTotalSelectors),
            SellerName = Blank(BolHtml.TextOf(block.Inner, options.OrderSellerSelectors)),
            Payment = ReceiptFactory.Payment(
                BolNormalize.Method(BolHtml.TextOf(block.Inner, options.OrderPaymentSelectors))),
            Items = ParseItems(block, options, id),
        };
    }

    private static string OrderId(HtmlElement block, BolOptions options)
    {
        // The block's own attribute first: a front end almost always carries
        // the order number there, and an attribute survives a restyle that a
        // rendered label does not.
        if (BolHtml.Attribute(block.OpenTag, options.OrderIdAttributes) is { Length: > 0 } attribute)
        {
            return attribute;
        }

        var rendered = Blank(BolHtml.TextOf(block.Inner, options.OrderIdSelectors));
        if (rendered is not null) return rendered;

        throw ConnectorException.ProviderChanged(
            $"{ProviderId}: an order block carries no order number; tried attributes " +
            $"[{string.Join(", ", options.OrderIdAttributes)}] and " +
            $"[{string.Join(", ", options.OrderIdSelectors)}]. The external id is what makes a re-run " +
            "idempotent, so an order without one cannot be emitted");
    }

    private static string? OrderDate(HtmlElement block, BolOptions options)
    {
        if (BolHtml.First(block.Inner, options.OrderDateSelectors) is not { } element)
        {
            throw ConnectorException.ProviderChanged(
                $"{ProviderId}: an order block carries no date; tried " +
                $"[{string.Join(", ", options.OrderDateSelectors)}]");
        }

        // A machine-readable attribute beats the rendered Dutch every time -
        // it states its own offset, and it cannot be defeated by a redesign
        // that changes "14 juni 2026" to "vandaag". It sits either on the
        // labelled element or on a <time> one level inside it, and both are
        // worth more than the prose.
        return BolHtml.Attribute(element.OpenTag, options.DateAttributes)
               ?? BolHtml.AttributeWithin(element.Inner, options.DateAttributes)
               ?? BolHtml.Text(element.Inner);
    }

    private static IReadOnlyList<ReceiptItem> ParseItems(HtmlElement block, BolOptions options, string orderId)
    {
        var lines = BolHtml.All(block.Inner, options.ItemBlockSelectors);
        if (lines.Count == 0) return [];

        var items = new List<ReceiptItem>(lines.Count);

        foreach (var line in lines)
        {
            var name = Blank(BolHtml.TextOf(line.Inner, options.ItemNameSelectors));
            if (name is null) continue;

            var field = $"order[{orderId}].item[{name}]";

            var total = Optional(
                BolHtml.TextOf(line.Inner, options.ItemTotalSelectors),
                options.HtmlAmountUnit, options.Currency, field + ".total");

            var unit = Optional(
                BolHtml.TextOf(line.Inner, options.ItemUnitPriceSelectors),
                options.HtmlAmountUnit, options.Currency, field + ".unitPrice");

            var quantity = BolHtml.Number(BolHtml.TextOf(line.Inner, options.ItemQuantitySelectors));

            // A line with a name and no price at all is a shipping note or a
            // status label, not a purchase - counting it as an item at zero
            // would break reconciliation for a row that is not money.
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
                Discount = Discount(line, options, field),
            });
        }

        return items;
    }
    private static ReceiptDiscount? Discount(HtmlElement line, BolOptions options, string field)
    {
        var text = BolHtml.TextOf(line.Inner, options.ItemDiscountSelectors);
        if (Optional(text, options.HtmlAmountUnit, options.Currency, field + ".discount") is not { } amount)
        {
            return null;
        }

        if (amount.Value == 0) return null;

        // Providers state a discount with either sign; the schema requires it
        // negative so that summing for reconciliation is unconditional.
        return new ReceiptDiscount
        {
            Amount = amount.Value > 0 ? amount.Negated() : amount,
            Label = BolHtml.WithoutPrice(text),
        };
    }

    private static Money Amount(
        string? text, MoneyUnit unit, string currency, string field, IReadOnlyList<string> selectors) =>
        Optional(text, unit, currency, field)
        ?? throw ConnectorException.ProviderChanged(
            $"{ProviderId}: no amount for {field}; tried [{string.Join(", ", selectors)}]" +
            (text is null ? string.Empty : $" and found the text '{text}'"));

    /// <summary>
    /// The unit is the caller's, always. There is no overload that looks at
    /// the value and decides what it means - a heuristic that guesses wrong
    /// corrupts money silently, and reconciliation cannot catch a whole
    /// receipt that is wrong the same way.
    /// </summary>
    private static Money? Optional(string? text, MoneyUnit unit, string currency, string field)
    {
        if (BolHtml.MoneyText(text) is not { } number) return null;
        return new Money(MoneyParser.ToMinor(number, unit, field), currency);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
