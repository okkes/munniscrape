using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Connector.Kit.Normalization;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.LidlPlus;

/// <summary>
/// Lidl's till receipt, read out of the HTML it prints.
///
/// CONFIRMED live on 2026-08-04 against a real account, and it replaces a
/// parser that was reading fields nobody had ever seen. A v3 ticket detail
/// carries <c>"ticketType": "HTML"</c> and no line-item collection at all -
/// not <c>itemsLine</c>, not <c>items</c>, not <c>lines</c>. What it carries
/// is <c>htmlPrintedReceipt</c>: the paper receipt, marked up.
///
/// That sounds worse than it is. Lidl does not render the lines as text and
/// leave us to align columns - every article span carries the facts as data
/// attributes:
/// <code>
///   &lt;span class="article"
///         data-art-id="0005936"
///         data-art-description="Baguette steenoven"
///         data-unit-price="0,79"
///         data-art-quantity="0,084"   (weighed goods only)
///         data-tax-type="B"&gt;
/// </code>
/// So the item facts are structured; only the line TOTAL has to come out of
/// the rendered text, because it is the one number the attributes omit.
///
/// The line total is READ rather than recomputed, and that is deliberate.
/// A weighed article states a quantity and a price per kilo, so the total
/// looks derivable - 0,084 kg of ginger at 5,99 is printed as 0,50 - and for
/// every receipt seen so far the arithmetic happens to agree. That is luck.
/// Whatever the till printed is what the customer was actually charged, and
/// how it got there is the till's business; a parser that multiplied would
/// disagree with the bank statement the day the two differ, and
/// reconciliation would fail pointing at nothing.
/// </summary>
internal static partial class LidlPrintedReceipt
{
    /// <summary>
    /// One rendered line, gathered from the several styled spans Lidl splits
    /// it across. They all repeat the same id and the same data attributes.
    /// </summary>
    private sealed record Line
    {
        public required string Id { get; init; }

        public required string Text { get; init; }

        public string? ArticleId { get; init; }

        public string? Description { get; init; }

        public string? UnitPrice { get; init; }

        public string? Quantity { get; init; }

        /// <summary>CONFIRMED: the tender line carries data-tender-description="Bankpas".</summary>
        public string? TenderDescription { get; init; }
    }

    /// <summary>
    /// A span that carries an id, which is the only kind that is a receipt
    /// LINE.
    ///
    /// The id requirement is load-bearing rather than tidy. Lidl wraps each
    /// block in an id-less span - <c>&lt;span class="purchase_list"&gt;</c> -
    /// and a pattern that matched those too would pair the wrapper's opening
    /// tag with the FIRST inner closing tag, swallowing the first real line of
    /// every block. The card tail is on exactly such a first line, which is
    /// how this was found.
    /// </summary>
    [GeneratedRegex(
        @"<span\b(?<attrs>[^>]*\bid\s*=\s*""[^""]*""[^>]*)>(?<text>.*?)</span>",
        RegexOptions.Singleline)]
    private static partial Regex Span { get; }

    [GeneratedRegex(@"(?<name>[a-zA-Z-]+)\s*=\s*""(?<value>[^""]*)""")]
    private static partial Regex Attribute { get; }

    /// <summary>A money token: 1,59 or -0,10 or 1.234,56.</summary>
    [GeneratedRegex(@"-?\d{1,3}(?:\.\d{3})*,\d{2}")]
    private static partial Regex Amount { get; }

    /// <summary>
    /// CONFIRMED. The masked card, as the payment terminal block prints it:
    /// "Kaart                 xxxxxxxxxxxx9785".
    /// </summary>
    [GeneratedRegex(@"[xX*]{4,}\s*(?<tail>\d{4})\b")]
    private static partial Regex CardTail { get; }

    /// <summary>
    /// Every purchased line, in receipt order, with discounts attached to the
    /// article they were printed under.
    /// </summary>
    public static IReadOnlyList<ReceiptItem> Items(string? printed, LidlPlusOptions options, string currency)
    {
        ArgumentNullException.ThrowIfNull(options);

        var lines = Lines(printed);
        if (lines.Count == 0) return [];

        var items = new List<ReceiptItem>();

        foreach (var line in lines)
        {
            // A line with no article id is either a discount belonging to the
            // article above it, or a subtotal, or a header.
            if (line.ArticleId is null)
            {
                Apply(items, line, options, currency);
                continue;
            }

            // The continuation line of a weighed article - "0,084 kg x 5,99" -
            // repeats every attribute of the line above and states no total of
            // its own. Recognised by its text not starting with the article's
            // own description, which is what the first line always does.
            if (!StartsWithDescription(line)) continue;

            if (Total(line.Text) is not { } total) continue;

            items.Add(new ReceiptItem
            {
                Name = line.Description ?? "?",
                Quantity = Decimal(line.Quantity) ?? 1m,
                UnitPrice = Money(line.UnitPrice, options, currency),
                Total = new Money(Minor(total, options), currency),
            });
        }

        return items;
    }

    /// <summary>
    /// How the shop was paid for, and the last four of the card where the
    /// terminal block prints them.
    /// </summary>
    public static ReceiptPayment Payment(string? printed, LidlPlusOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var lines = Lines(printed);
        if (lines.Count == 0) return ReceiptFactory.Payment();

        // CONFIRMED: the tender line carries data-tender-description="Bankpas".
        string? method = null;
        foreach (var line in lines)
        {
            if (line.Id.StartsWith("purchase_summary", StringComparison.Ordinal) &&
                line.TenderDescription is { Length: > 0 } tender)
            {
                method = tender;
                break;
            }
        }

        string? tail = null;
        foreach (var line in lines)
        {
            if (CardTail.Match(line.Text) is { Success: true } match)
            {
                tail = match.Groups["tail"].Value;
                break;
            }
        }

        return ReceiptFactory.Payment(Method(method), tail);
    }

    /// <summary>
    /// A discount, or a line that is neither an article nor a discount.
    ///
    /// "In prijs verlaagd  -0,10" is printed under the article it reduces and
    /// carries no attributes of its own, so it is attached to the last item
    /// added. Without that, items would not sum to the stated total and every
    /// receipt with a markdown on it would arrive unreconciled.
    /// </summary>
    private static void Apply(List<ReceiptItem> items, Line line, LidlPlusOptions options, string currency)
    {
        if (items.Count == 0) return;
        if (!line.Id.StartsWith("purchase_list", StringComparison.Ordinal)) return;

        var amount = Total(line.Text);
        if (amount is not { } value || value >= 0) return;

        var label = Amount.Replace(line.Text, string.Empty).Trim();
        var last = items[^1];

        items[^1] = last with
        {
            Discount = new ReceiptDiscount
            {
                Amount = new Money(Minor(value, options), currency),
                Label = string.IsNullOrWhiteSpace(label) ? null : label,
            },

            // The line total Lidl printed is BEFORE this markdown, and the
            // platform's rule is that items plus their discounts equal the
            // stated total - so the total stays as printed and the discount
            // carries the difference.
            Total = last.Total,
        };
    }

    /// <summary>
    /// Lidl's word for the tender, mapped onto the four the normalized schema
    /// names. Null stays null: a guess here weakens a consumer's
    /// receipt-to-transaction match without telling it so.
    /// </summary>
    /// <remarks>
    /// The Dutch words are the point. "Bankpas" is what a Dutch till prints
    /// for a debit card, and the mapping every other adapter here uses does
    /// not know it - so it would have arrived as "other", which is the value
    /// that means "we looked and could not tell".
    /// </remarks>
    private static string? Method(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        if (raw.Contains("ideal", StringComparison.OrdinalIgnoreCase)) return "ideal";

        if (raw.Contains("contant", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("cash", StringComparison.OrdinalIgnoreCase))
        {
            return "cash";
        }

        return raw.Contains("bankpas", StringComparison.OrdinalIgnoreCase)
               || raw.Contains("pin", StringComparison.OrdinalIgnoreCase)
               || raw.Contains("card", StringComparison.OrdinalIgnoreCase)
               || raw.Contains("kaart", StringComparison.OrdinalIgnoreCase)
               || raw.Contains("maestro", StringComparison.OrdinalIgnoreCase)
               || raw.Contains("visa", StringComparison.OrdinalIgnoreCase)
               || raw.Contains("mastercard", StringComparison.OrdinalIgnoreCase)
            ? "card"
            : "other";
    }

    private static bool StartsWithDescription(Line line) =>
        line.Description is { Length: > 0 } description &&
        line.Text.TrimStart().StartsWith(description.AsSpan(0, Math.Min(description.Length, 8)), StringComparison.Ordinal);

    /// <summary>
    /// The last money token on the line, which is where a till prints the
    /// amount. Anything earlier belongs to a quantity or a unit price.
    /// </summary>
    private static decimal? Total(string text)
    {
        decimal? last = null;

        foreach (Match match in Amount.Matches(text))
        {
            if (decimal.TryParse(match.Value, NumberStyles.Number, Dutch, out var value)) last = value;
        }

        return last;
    }

    private static readonly CultureInfo Dutch = CultureInfo.GetCultureInfo("nl-NL");

    private static decimal? Decimal(string? raw) =>
        decimal.TryParse(raw, NumberStyles.Number, Dutch, out var value) ? value : null;

    private static Money? Money(string? raw, LidlPlusOptions options, string currency) =>
        Decimal(raw) is { } value ? new Money(Minor(value, options), currency) : null;

    /// <summary>
    /// Through the platform's own converter, under the unit the receipt
    /// prints. A till prints major units with a comma - "0,79" - and the
    /// rounding rule for a half cent is the platform's to make, not this
    /// file's.
    /// </summary>
    private static long Minor(decimal major, LidlPlusOptions options) =>
        MoneyParser.ToMinor(major, options.PrintedAmountUnit);

    /// <summary>
    /// The spans, gathered back into the lines they were split out of.
    ///
    /// Lidl emits one span per styled run and repeats the id and every data
    /// attribute on all of them, so a line is the concatenation of the spans
    /// sharing an id. Grouped by ORDER of first appearance rather than by a
    /// dictionary: receipt order is the only order these have.
    /// </summary>
    private static IReadOnlyList<Line> Lines(string? printed)
    {
        if (string.IsNullOrWhiteSpace(printed)) return [];

        var order = new List<string>();
        var byId = new Dictionary<string, Line>(StringComparer.Ordinal);

        foreach (Match span in Span.Matches(printed))
        {
            var attrs = Attributes(span.Groups["attrs"].Value);
            if (!attrs.TryGetValue("id", out var id) || id.Length == 0) continue;

            // Nested spans are matched too and would double-count the text.
            // The inner ones carry no tags of their own after decoding, so
            // stripping any residual markup is enough.
            var text = WebUtility.HtmlDecode(Tags.Replace(span.Groups["text"].Value, string.Empty));

            if (byId.TryGetValue(id, out var existing))
            {
                byId[id] = existing with
                {
                    Text = existing.Text + text,
                    ArticleId = existing.ArticleId ?? attrs.GetValueOrDefault("data-art-id"),
                    Description = existing.Description ?? Decode(attrs.GetValueOrDefault("data-art-description")),
                    UnitPrice = existing.UnitPrice ?? attrs.GetValueOrDefault("data-unit-price"),
                    Quantity = existing.Quantity ?? attrs.GetValueOrDefault("data-art-quantity"),
                    TenderDescription = existing.TenderDescription ?? Decode(attrs.GetValueOrDefault("data-tender-description")),
                };

                continue;
            }

            order.Add(id);
            byId[id] = new Line
            {
                Id = id,
                Text = text,
                ArticleId = attrs.GetValueOrDefault("data-art-id"),
                Description = Decode(attrs.GetValueOrDefault("data-art-description")),
                UnitPrice = attrs.GetValueOrDefault("data-unit-price"),
                Quantity = attrs.GetValueOrDefault("data-art-quantity"),
                TenderDescription = Decode(attrs.GetValueOrDefault("data-tender-description")),
            };
        }

        return [.. order.Select(id => byId[id])];
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags { get; }

    private static string? Decode(string? raw) => raw is null ? null : WebUtility.HtmlDecode(raw);

    private static Dictionary<string, string> Attributes(string raw)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in Attribute.Matches(raw))
        {
            found[match.Groups["name"].Value] = match.Groups["value"].Value;
        }

        return found;
    }
}
