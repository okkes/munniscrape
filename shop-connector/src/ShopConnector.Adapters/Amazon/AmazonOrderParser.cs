using System.Globalization;
using System.Text.RegularExpressions;
using Connector.Kit.Errors;
using Connector.Kit.Normalization;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.Amazon;

/// <summary>One row of the order list: what the list page itself states.</summary>
internal sealed record AmazonOrderSummary(string Id, DateTimeOffset PurchasedAt, Money Total, string? InvoiceUrl);

/// <summary>
/// What the print invoice adds: the lines, the charges, and how it was paid.
/// </summary>
internal sealed record AmazonInvoice
{
    public IReadOnlyList<ReceiptItem> Items { get; init; } = [];

    public ReceiptPayment Payment { get; init; } = ReceiptFactory.Payment();

    /// <summary>
    /// The invoice's own grand total, where it states one. Not used as the
    /// receipt's total - that comes from the list card - precisely so the two
    /// stay a real reconciliation pair rather than one number written twice.
    /// </summary>
    public Money? StatedTotal { get; init; }
}

/// <summary>Which line of the totals block a label names.</summary>
internal enum AmazonTotalKind
{
    Unknown,
    Subtotal,
    TotalBeforeTax,
    Shipping,
    Tax,
    Promotion,
    GiftCard,
    GrandTotal,
}

/// <summary>
/// Amazon's order history, out of rendered HTML, in Dutch.
/// </summary>
internal static partial class AmazonOrderParser
{
    /// <summary>Amazon's order number, which has looked like this for twenty years.</summary>
    [GeneratedRegex(@"\b(\d{3}-\d{7}-\d{7})\b")]
    private static partial Regex OrderNumber { get; }

    /// <summary>
    /// "1 van: Titel", "2 of: Title", "3 x Titel". The separator words are
    /// both languages' because the page's language is a thing a live run still
    /// has to settle.
    /// </summary>
    [GeneratedRegex(@"^\s*(\d+(?:[.,]\d+)?)\s*(?:van:|van|of:|of|x|×)\s+(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex QuantityPrefix { get; }

    /// <summary>The first four digits after "eindigend op" / "ending in".</summary>
    [GeneratedRegex(@"^\D*(\d{4})")]
    private static partial Regex LeadingFour { get; }

    /// <summary>The order id inside a link's query string.</summary>
    [GeneratedRegex(@"[?&]orderID=([^&#\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex OrderIdInUrl { get; }

    // ---- order list --------------------------------------------------------

    public static IReadOnlyList<AmazonOrderSummary> ParseList(
        HtmlNode dom, AmazonOptions options, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(dom);
        ArgumentNullException.ThrowIfNull(options);

        var cards = HtmlQuery.All(dom, options.OrderCardSelectors);
        var rows = new List<AmazonOrderSummary>(cards.Count);

        foreach (var card in cards)
        {
            var id = OrderId(card, options)
                     ?? throw Missing("order id", options.OrderIdSelectors);

            var dateText = HtmlQuery.TextOf(card, options.OrderDateSelectors)
                           ?? throw Missing($"order date on '{id}'", options.OrderDateSelectors);

            var totalText = HtmlQuery.TextOf(card, options.OrderTotalSelectors)
                            ?? throw Missing($"order total on '{id}'", options.OrderTotalSelectors);

            var total = AmazonMoney.Find(totalText, options.OrderTotalUnit, options.Currency, "order total", options)
                        ?? throw ConnectorException.ProviderChanged(
                            $"{AmazonAdapter.ProviderId}: order '{id}' states no readable total (read '{totalText}')");

            rows.Add(new AmazonOrderSummary(
                id,
                AmazonDate.Parse(dateText, zone, $"order date on '{id}'"),
                total,
                InvoiceUrl(id, options)));
        }

        return rows;
    }

    /// <summary>
    /// Whether the list offers another page. Used only as a stop signal: the
    /// walk also stops on a page that repeats what it already has, because a
    /// pagination parameter that stops advancing is the likelier first symptom
    /// of a shape change and it presents as the same page fetched twenty times
    /// against a defended site.
    /// </summary>
    public static bool HasNextPage(HtmlNode dom, AmazonOptions options)
    {
        ArgumentNullException.ThrowIfNull(dom);
        ArgumentNullException.ThrowIfNull(options);

        // No pagination block at all is not evidence that the history ended -
        // Amazon has more than one order-list layout. Say "maybe" and let the
        // freshness guard decide, because the alternative is silently
        // truncating somebody's history at ten orders.
        var pagination = HtmlQuery.First(dom, options.PaginationSelectors);
        if (pagination is null) return true;

        var next = HtmlQuery.First(pagination, options.NextPageSelectors);
        if (next is null) return false;

        // Amazon renders the "next" control disabled rather than removing it.
        var classes = next.Attribute("class") ?? string.Empty;
        if (classes.Contains("a-disabled", StringComparison.OrdinalIgnoreCase)) return false;

        var parentClasses = next.Parent?.Attribute("class") ?? string.Empty;
        return !parentClasses.Contains("a-disabled", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Four ways to read an order number, most durable first.
    ///
    /// An attribute and a query string survive the redesign that renames every
    /// class, and the classes are the shortest-lived part of this page. The
    /// last resort is the number's own shape, which Amazon has not changed in
    /// twenty years - a reading rather than a guess, and the thing that keeps
    /// this adapter alive when every selector above it has expired.
    /// </summary>
    private static string? OrderId(HtmlNode card, AmazonOptions options)
    {
        foreach (var attribute in options.OrderIdAttributes)
        {
            if (card.Attribute(attribute) is { Length: > 0 } direct) return direct.Trim();

            foreach (var descendant in card.Elements())
            {
                if (descendant.Attribute(attribute) is { Length: > 0 } nested) return nested.Trim();
            }
        }

        foreach (var link in HtmlQuery.All(card, options.OrderLinkSelectors))
        {
            if (link.Attribute("href") is { } href && OrderIdInUrl.Match(href) is { Success: true } fromHref)
            {
                return Uri.UnescapeDataString(fromHref.Groups[1].Value);
            }
        }

        if (HtmlQuery.TextOf(card, options.OrderIdSelectors) is { } text
            && OrderNumber.Match(text) is { Success: true } fromSelector)
        {
            return fromSelector.Groups[1].Value;
        }

        return OrderNumber.Match(card.Text()) is { Success: true } fromText ? fromText.Groups[1].Value : null;
    }

    /// <summary>
    /// Built rather than harvested. A card's own links point at the
    /// order-details page, which is the heavier and more dynamically rendered
    /// of the two; the print invoice takes the same order id and is the most
    /// stable page Amazon serves, which is why the research recommends it for
    /// totals.
    /// </summary>
    private static string? InvoiceUrl(string orderId, AmazonOptions options) =>
        orderId.Length == 0
            ? null
            : UrlBuilder.WithQuery(
                options.BaseUrl.TrimEnd('/') + options.InvoicePath,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["orderID"] = orderId });

    // ---- print invoice -----------------------------------------------------

    public static AmazonInvoice ParseInvoice(HtmlNode dom, AmazonOptions options)
    {
        ArgumentNullException.ThrowIfNull(dom);
        ArgumentNullException.ThrowIfNull(options);

        var items = new List<ReceiptItem>();
        items.AddRange(Products(dom, options));

        var totals = Totals(dom, options);

        // Nothing at all - no line, no charge, no total - is not an invoice
        // with nothing on it; it is a page whose shape we no longer recognise.
        // Naming both selector lists is the whole diagnosis for whoever fixes
        // it, and a fetch that silently returned an empty receipt would hide
        // the same fact behind a plausible-looking record.
        if (items.Count == 0 && totals.Count == 0)
        {
            throw ConnectorException.ProviderChanged(
                $"{AmazonAdapter.ProviderId}: the print invoice yielded neither a line nor a total; " +
                $"tried items [{string.Join(", ", options.InvoiceItemRowSelectors)}] " +
                $"and totals [{string.Join(", ", options.InvoiceTotalRowSelectors)}]");
        }

        // Shipping is a real charge and belongs in the sum. Tax is NOT added:
        // a Dutch consumer price is quoted including BTW, so the invoice's tax
        // row decomposes the total rather than adding to it, and emitting it
        // as a line would double-count the VAT on every receipt. Whether
        // Amazon's invoice ever renders a tax-exclusive breakdown is
        // UNCONFIRMED, which is why it is a switch and not an assumption -
        // and why reconciliation is what decides if the switch is wrong.
        foreach (var kind in new[] { AmazonTotalKind.Shipping, AmazonTotalKind.Tax })
        {
            if (kind == AmazonTotalKind.Tax && options.TaxIsIncludedInItemPrices) continue;

            foreach (var row in totals.Where(r => r.Kind == kind && r.Amount.Value != 0))
            {
                items.Add(new ReceiptItem { Name = row.Label, Total = row.Amount });
            }
        }

        // A promotion is modelled as a discount rather than as a negative
        // product, because that is the construct the normalized record has for
        // it and the one the consumer knows how to display. Its name is the
        // provider's own label - a connector never invents user-facing prose,
        // not even for a synthetic line.
        foreach (var row in totals.Where(r =>
                     r.Kind is AmazonTotalKind.Promotion or AmazonTotalKind.GiftCard && r.Amount.Value != 0))
        {
            items.Add(new ReceiptItem
            {
                Name = row.Label,
                Total = Money.Zero(row.Amount.Currency),
                Discount = new ReceiptDiscount { Amount = row.Amount.Abs().Negated(), Label = row.Label },
            });
        }

        return new AmazonInvoice
        {
            Items = items,
            Payment = Payment(dom, options),
            StatedTotal = totals.FirstOrDefault(r => r.Kind == AmazonTotalKind.GrandTotal)?.Amount,
        };
    }

    private static IEnumerable<ReceiptItem> Products(HtmlNode dom, AmazonOptions options)
    {
        foreach (var row in HtmlQuery.All(dom, options.InvoiceItemRowSelectors))
        {
            var priceNode = HtmlQuery.First(row, options.InvoiceItemPriceSelectors);
            if (priceNode is null) continue;

            var unitPrice = AmazonMoney.Find(
                priceNode.Text(), options.ItemAmountUnit, options.Currency, "item price", options);

            // A row with no amount is a header or a spacer, not a purchase.
            if (unitPrice is not { } price) continue;

            var nameNode = FirstOtherThan(row, options.InvoiceItemNameSelectors, priceNode);
            var cell = HtmlText.Normalize(nameNode?.Text());
            if (cell.Length == 0) continue;

            var quantity = 1m;
            var name = cell;

            if (QuantityPrefix.Match(cell) is { Success: true } prefix
                && decimal.TryParse(prefix.Groups[1].Value.Replace(',', '.'), NumberStyles.Number,
                    CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
            {
                quantity = parsed;
                name = prefix.Groups[2].Value.Trim();
            }

            var stated = FirstOtherThan(row, options.InvoiceItemTotalSelectors, priceNode) is { } totalNode
                ? AmazonMoney.Find(totalNode.Text(), options.ItemAmountUnit, options.Currency, "item total", options)
                : null;

            yield return new ReceiptItem
            {
                Name = Truncate(name, options),
                Quantity = quantity,
                UnitPrice = price,
                // A stated line total always beats a computed one; Amazon's
                // print invoice normally states only the unit price, so the
                // multiplication is the usual path.
                Total = stated ?? new Money(
                    checked((long)decimal.Round(price.Value * quantity, 0, MidpointRounding.AwayFromZero)),
                    price.Currency),
            };
        }
    }

    private sealed record TotalRow(AmazonTotalKind Kind, string Label, Money Amount);

    private static List<TotalRow> Totals(HtmlNode dom, AmazonOptions options)
    {
        var rows = new List<TotalRow>();

        foreach (var node in HtmlQuery.All(dom, options.InvoiceTotalRowSelectors))
        {
            var (label, amount) = AmazonMoney.Row(
                node.Text(), options.InvoiceTotalUnit, options.Currency, "invoice total", options);

            if (amount is not { } value || label.Length == 0) continue;

            rows.Add(new TotalRow(Categorize(label, options), label, value));
        }

        return rows;
    }

    /// <summary>
    /// Which line a Dutch (or English) label names.
    ///
    /// The order is load-bearing and is the reason these are separate lists
    /// rather than one. "subtotaal" CONTAINS "totaal"; "totaal vóór btw"
    /// CONTAINS "btw". A single contains-match against one list therefore
    /// files the subtotal as the grand total and the pre-tax figure as the
    /// tax, and the receipt then reconciles against a number that is not its
    /// total - plausibly, and wrongly.
    /// </summary>
    internal static AmazonTotalKind Categorize(string label, AmazonOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var text = label.Trim().TrimEnd(':').Trim();

        if (Any(text, options.SubtotalLabels)) return AmazonTotalKind.Subtotal;
        if (Any(text, options.TotalBeforeTaxLabels)) return AmazonTotalKind.TotalBeforeTax;
        if (Any(text, options.ShippingLabels)) return AmazonTotalKind.Shipping;
        if (Any(text, options.PromotionLabels)) return AmazonTotalKind.Promotion;
        if (Any(text, options.GiftCardLabels)) return AmazonTotalKind.GiftCard;
        if (Any(text, options.TaxLabels)) return AmazonTotalKind.Tax;
        if (Any(text, options.GrandTotalLabels)) return AmazonTotalKind.GrandTotal;

        return AmazonTotalKind.Unknown;
    }

    private static ReceiptPayment Payment(HtmlNode dom, AmazonOptions options)
    {
        var node = HtmlQuery.First(dom, options.InvoicePaymentSelectors);
        if (node is null) return ReceiptFactory.Payment();

        var text = node.Text();
        if (text.Length == 0) return ReceiptFactory.Payment();

        foreach (var marker in options.CardTailMarkers)
        {
            var at = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;

            var tail = JsonAccess.Tail(LeadingFour.Match(text[(at + marker.Length)..]) is { Success: true } digits
                ? digits.Groups[1].Value
                : null);

            return ReceiptFactory.Payment(method: "card", cardLast4: tail);
        }

        if (text.Contains("ideal", StringComparison.OrdinalIgnoreCase))
        {
            return ReceiptFactory.Payment(method: "ideal");
        }

        // Stated as unknown rather than guessed. The consumer matches receipts
        // to transactions partly on the payment tail, and it needs to know the
        // match will be weaker rather than be handed an invention.
        return ReceiptFactory.Payment();
    }

    private static HtmlNode? FirstOtherThan(HtmlNode row, IReadOnlyList<string> selectors, HtmlNode exclude)
    {
        foreach (var selector in selectors)
        {
            foreach (var hit in HtmlQuery.All(row, selector))
            {
                if (!ReferenceEquals(hit, exclude)) return hit;
            }
        }

        return null;
    }

    /// <summary>
    /// Cuts an item cell where the product's name stops and Amazon's
    /// commentary begins. Falls back to the whole cell when no marker is
    /// found: a long name is a cosmetic problem, an empty one is a data one.
    /// </summary>
    private static string Truncate(string name, AmazonOptions options)
    {
        var cut = name.Length;

        foreach (var marker in options.ItemNameStopMarkers)
        {
            var at = name.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at > 0 && at < cut) cut = at;
        }

        var trimmed = name[..cut].Trim().TrimEnd(',', ';', '-').Trim();
        return trimmed.Length == 0 ? name : trimmed;
    }

    private static bool Any(string text, IReadOnlyList<string> candidates) =>
        candidates.Any(candidate => text.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private static ConnectorException Missing(string what, IReadOnlyList<string> selectors) =>
        ConnectorException.ProviderChanged(
            $"{AmazonAdapter.ProviderId}: no {what} on the order list; tried [{string.Join(", ", selectors)}]");
}
