using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Connector.Kit.Errors;
using Connector.Kit.Normalization;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.Jumbo;

/// <summary>
/// One row of <c>data.storeReceipts.receipts</c>.
///
/// Note what is missing: no total and no line items. A receipt summary is a
/// transaction id, a moment, a source and a shop, and nothing else - so an
/// in-store receipt costs a second round trip before it can be stated at all.
/// </summary>
internal sealed record JumboStoreReceiptSummary
{
    public required string TransactionId { get; init; }

    public required DateTimeOffset PurchasedAt { get; init; }

    public string? Source { get; init; }

    public string? StoreName { get; init; }

    /// <summary>
    /// The online order this till record belongs to, when it is one. Jumbo
    /// builds an <c>ONLINE</c> receipt's transaction id as
    /// <c>&lt;orderId&gt;-…</c>, which is the only thing that keeps the same
    /// purchase from being emitted twice - once from each system.
    /// </summary>
    public string? OrderId { get; init; }
}

/// <summary>
/// What a print-layout parse produced. Any part of it may be absent, and
/// which part is absent is the whole diagnosis.
/// </summary>
internal sealed record JumboReceiptContents
{
    /// <summary>The <c>Totaal</c> line's amount. Null when there was no such line.</summary>
    public Money? Total { get; init; }

    public IReadOnlyList<ReceiptItem> Items { get; init; } = [];

    public string? PaymentMethod { get; init; }

    /// <summary>
    /// Why this is less than a whole receipt, in operator-facing words. Null
    /// when the layout parsed completely.
    /// </summary>
    public string? Shortfall { get; init; }
}

/// <summary>
/// The seam over the one part of Jumbo that is not a JSON contract at all.
///
/// <c>GetDigitalReceipt</c> answers with a receipt-<em>printer</em> layout
/// document whose payload is a picture of a paper receipt in text: columns,
/// headers, a terminator line. Text parsing is guesswork by nature, so it
/// lives behind an interface with recorded fixtures on the other side - the
/// day a till template changes, only this implementation moves, and the
/// change is visible in a diff rather than in somebody's grocery totals.
/// </summary>
internal interface IJumboReceiptLayoutParser
{
    JumboReceiptContents Parse(string layout, JumboOptions options, string currency);
}

/// <summary>
/// Jumbo's in-store receipts, parsed.
/// </summary>
internal static class JumboStoreReceipts
{
    private const string ProviderId = JumboAdapter.ProviderId;

    /// <summary>The leading numeric run of an <c>ONLINE</c> receipt's transaction id.</summary>
    private static readonly Regex OrderIdPrefix =
        new(@"^(\d+)-", RegexOptions.None, TimeSpan.FromMilliseconds(100));

    public static IReadOnlyList<JsonElement> Rows(JsonElement root, JumboOptions options)
    {
        if (!JsonAccess.TryPath(root, options.StoreReceiptsPath, out var node))
        {
            throw ConnectorException.ProviderChanged(
                $"{ProviderId}: no receipt collection at '{options.StoreReceiptsPath}'");
        }

        return node.ValueKind == JsonValueKind.Array ? JsonAccess.AsArray(node) : [];
    }

    /// <summary>
    /// The receipt count <c>receiptOverview</c> states, so the walk can end on
    /// a number instead of on a heuristic about repeated pages.
    /// </summary>
    public static int? TotalResults(JsonElement root, JumboOptions options) =>
        JsonAccess.TryPath(root, options.StoreReceiptsTotalPath, out var node) &&
        node.ValueKind == JsonValueKind.Number &&
        node.TryGetInt32(out var count)
            ? count
            : null;

    public static JumboStoreReceiptSummary? ParseSummary(JsonElement row, JumboOptions options, TimeZoneInfo zone)
    {
        var id = JsonAccess.StrOf(row, "transactionId");
        if (string.IsNullOrWhiteSpace(id)) return null;

        var match = OrderIdPrefix.Match(id);

        return new JumboStoreReceiptSummary
        {
            TransactionId = id,
            PurchasedAt = ReceiptTime.Parse(
                JsonAccess.StrOf(row, "purchaseEndOn"), zone, ProviderId, $"receipt[{id}].purchaseEndOn"),
            Source = JsonAccess.StrOf(row, "receiptSource"),
            StoreName = JsonAccess.TryProp(row, out var store, "store") ? JsonAccess.StrOf(store, "name") : null,
            OrderId = match.Success ? match.Groups[1].Value : null,
        };
    }

    /// <summary>
    /// The receipt-printer payload out of a <c>GetDigitalReceipt</c> document.
    ///
    /// Returns null when the receipt image is not the JSON layout - an older
    /// receipt rendered as a picture is a real answer from Jumbo, not a shape
    /// change, and the caller decides what to do about a receipt it cannot
    /// read rather than this deciding for it.
    /// </summary>
    public static string? Layout(JsonElement root, JumboOptions options, string transactionId)
    {
        if (!JsonAccess.TryPath(root, options.DigitalReceiptPath, out var receipt))
        {
            throw ConnectorException.ProviderChanged(
                $"{ProviderId}: receipt '{transactionId}' detail has nothing at '{options.DigitalReceiptPath}'");
        }

        if (!JsonAccess.TryProp(receipt, out var image, "receiptImage"))
        {
            throw ConnectorException.ProviderChanged(
                $"{ProviderId}: receipt '{transactionId}' detail states no receiptImage");
        }

        var type = JsonAccess.StrOf(image, "type");
        if (!string.Equals(type, options.DigitalReceiptJsonType, StringComparison.OrdinalIgnoreCase)) return null;

        var payload = JsonAccess.StrOf(image, "image");
        return string.IsNullOrWhiteSpace(payload) ? null : payload;
    }

    /// <summary>The shop name from the detail document, which states it more fully than the list.</summary>
    public static string? StoreName(JsonElement root, JumboOptions options)
    {
        if (!JsonAccess.TryPath(root, options.DigitalReceiptPath, out var receipt)) return null;

        return JsonAccess.TryProp(receipt, out var store, "store") ? JsonAccess.StrOf(store, "name") : null;
    }
}

/// <summary>
/// The text parser.
///
/// The working client walks
/// <c>documents[0].documents[0].printSections[].textObjects[].textLines[].texts[].text</c>
/// and then reads the flattened lines as a paper receipt: an
/// <c>OMSCHRIJVING</c>/<c>BEDRAG</c> header opens the item block, a
/// <c>Totaal</c> line closes it and states the total, and a <c>2 X 0,94</c>
/// line amends the item above it rather than adding one of its own.
///
/// Two rules govern everything here. Nothing is invented: a line that states
/// no amount produces no item, and a promotion flag produces no discount,
/// because the layout prints a line's price already net of its promotion and
/// never prints the promotion's own value - a discount conjured here would be
/// subtracted a second time. And nothing is dropped: a layout that yields a
/// total but no items still becomes a receipt, flagged as not reconciling,
/// because a purchase the user made is not ours to hide.
/// </summary>
internal sealed partial class JumboPrintLayoutParser : IJumboReceiptLayoutParser
{
    /// <summary>CONFIRMED: <c>2 X 0,94</c> - a count, an x, and a unit price.</summary>
    [GeneratedRegex(@"^\s*(\d+)\s*[Xx]\s*(\d+[,.]\d+)\s*$")]
    private static partial Regex QuantityLine { get; }

    /// <summary>
    /// A printed amount: <c>0,94</c>, <c>29,83</c>, <c>1.234,56</c>,
    /// <c>1234,56</c>, and the trailing-minus form a till uses for a credit.
    ///
    /// Anchored on purpose. A loose match would read the <c>1,5</c> out of
    /// "COCA COLA 1,5L" as money, and a description that turns into a price is
    /// the quietest way to break a receipt.
    /// </summary>
    [GeneratedRegex(@"^-?(?:\d{1,3}(?:[.,]\d{3})+|\d+)[.,]\d{2}-?$")]
    private static partial Regex PrintedAmount { get; }

    public JumboReceiptContents Parse(string layout, JumboOptions options, string currency)
    {
        ArgumentNullException.ThrowIfNull(options);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(layout);
        }
        catch (JsonException ex)
        {
            // The field is declared a string and is documented as carrying a
            // layout document; something else in it is the provider moving.
            throw new ConnectorException(
                ErrorCode.ProviderChanged, $"{JumboAdapter.ProviderId}: the receipt layout is not JSON", ex);
        }

        using (document)
        {
            var lines = Flatten(document.RootElement, options);
            if (lines.Count == 0)
            {
                return new JumboReceiptContents
                {
                    Shortfall = $"no text lines under '{options.PrintSectionsPath}'",
                };
            }

            var totalIndex = IndexOfPrefix(lines, options.TotalPrefix, from: 0, requireAmount: true);
            var total = totalIndex >= 0 ? Amount(lines[totalIndex].Fields, options, currency) : null;

            var headerIndex = HeaderIndex(lines, options);
            var payment = PaymentMethod(lines, options);

            // No terminator means no total, and an in-store receipt has no
            // other source of one anywhere in Jumbo's API. Items without a
            // total to check them against would be a number we cannot vouch
            // for, so they are not offered.
            if (totalIndex < 0)
            {
                return new JumboReceiptContents
                {
                    PaymentMethod = payment,
                    Shortfall = $"no '{options.TotalPrefix}' line",
                };
            }

            if (headerIndex < 0 || totalIndex <= headerIndex)
            {
                // A total with no readable item block is still a receipt worth
                // emitting; the caller flags it rather than dropping it.
                return new JumboReceiptContents
                {
                    Total = total,
                    PaymentMethod = payment,
                    Shortfall = headerIndex < 0
                        ? $"no items header ({string.Join("/", options.ItemsHeaderTokens)})"
                        : $"the '{options.TotalPrefix}' line is above the items header",
                };
            }

            return new JumboReceiptContents
            {
                Total = total,
                Items = Items(lines, headerIndex + 1, totalIndex, options, currency),
                PaymentMethod = payment,
            };
        }
    }

    /// <summary>
    /// The flattened text lines. Each keeps its own text fields rather than
    /// being joined, because the receipt's columns - description, promotion
    /// flag, amount - are the only structure this document has.
    /// </summary>
    internal static IReadOnlyList<PrintLine> Flatten(JsonElement root, JumboOptions options)
    {
        if (!WalkPath(root, options.PrintSectionsPath, out var sections) ||
            sections.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var lines = new List<PrintLine>();

        foreach (var section in sections.EnumerateArray())
        {
            foreach (var textObject in JsonAccess.Array(section, "textObjects"))
            {
                foreach (var textLine in JsonAccess.Array(textObject, "textLines"))
                {
                    var fields = new List<string>();
                    foreach (var text in JsonAccess.Array(textLine, "texts"))
                    {
                        var value = JsonAccess.StrOf(text, "text");
                        if (value is not null) fields.Add(value.Trim());
                    }

                    if (fields.Count > 0) lines.Add(new PrintLine(fields));
                }
            }
        }

        return lines;
    }

    /// <summary>
    /// A dotted path where a numeric segment indexes an array, because the
    /// confirmed walk into this document goes through two of them.
    /// </summary>
    internal static bool WalkPath(JsonElement root, string path, out JsonElement value)
    {
        value = root;

        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                if (value.ValueKind != JsonValueKind.Array || index < 0 || index >= value.GetArrayLength())
                {
                    value = default;
                    return false;
                }

                value = value[index];
                continue;
            }

            if (!JsonAccess.TryProp(value, out var next, segment))
            {
                value = default;
                return false;
            }

            value = next;
        }

        return true;
    }

    private static List<ReceiptItem> Items(
        IReadOnlyList<PrintLine> lines, int from, int to, JumboOptions options, string currency)
    {
        var items = new List<ReceiptItem>();

        for (var i = from; i < to; i++)
        {
            var fields = lines[i].Fields;

            // "2 X 0,94" amends the item printed above it: the count and the
            // unit price belong to a line whose total is already stated. A
            // quantity line with nothing above it is a stray and is ignored
            // rather than turned into an item with no name.
            var quantity = QuantityLine.Match(fields[0]);
            if (quantity.Success)
            {
                if (items.Count == 0) continue;

                items[^1] = items[^1] with
                {
                    Quantity = decimal.Parse(quantity.Groups[1].Value, CultureInfo.InvariantCulture),
                    UnitPrice = new Money(
                        MoneyParser.ToMinor(quantity.Groups[2].Value, options.StoreReceiptUnit, "receipt.unitPrice"),
                        currency),
                };

                continue;
            }

            if (Amount(fields, options, currency) is not { } amount) continue;

            var name = fields[0];
            if (string.IsNullOrWhiteSpace(name)) continue;

            // A line whose only word is the promotion flag continues the item
            // above it. Making it an item of its own would put a line called
            // "P" on somebody's grocery receipt and double-count its amount.
            if (string.Equals(name, options.PromotionFlag, StringComparison.OrdinalIgnoreCase)) continue;

            // The amount field is not a description, so a single-column line
            // that is nothing but a number is not an item either.
            if (PrintedAmount.IsMatch(name)) continue;

            items.Add(new ReceiptItem { Name = name, Total = amount });
        }

        return items;
    }

    private static int HeaderIndex(IReadOnlyList<PrintLine> lines, JumboOptions options)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var text = lines[i].Text;
            if (options.ItemsHeaderTokens.All(t => text.Contains(t, StringComparison.OrdinalIgnoreCase))) return i;
        }

        return -1;
    }

    private static int IndexOfPrefix(
        IReadOnlyList<PrintLine> lines, string prefix, int from, bool requireAmount)
    {
        for (var i = from; i < lines.Count; i++)
        {
            var fields = lines[i].Fields;
            if (!fields[0].StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (requireAmount && !fields.Any(f => PrintedAmount.IsMatch(f))) continue;

            return i;
        }

        return -1;
    }

    /// <summary>
    /// How it was paid, from the line the till prints after the total. The
    /// method is the first word on that line that is not the marker and not an
    /// amount; failing that, the first word of the line below it.
    /// </summary>
    private static string? PaymentMethod(IReadOnlyList<PrintLine> lines, JumboOptions options)
    {
        var index = IndexOfPrefix(lines, options.PaidPrefix, from: 0, requireAmount: false);
        if (index < 0) return null;

        foreach (var field in lines[index].Fields.Skip(1))
        {
            if (field.Length > 0 && !PrintedAmount.IsMatch(field)) return JumboReceipts.Method(field);
        }

        return index + 1 < lines.Count ? JumboReceipts.Method(lines[index + 1].Fields[0]) : null;
    }

    /// <summary>
    /// The rightmost printed amount on a line. Rightmost because a receipt
    /// prints its money in the last column, and a description that happens to
    /// contain a number would otherwise be read as one.
    /// </summary>
    private static Money? Amount(IReadOnlyList<string> fields, JumboOptions options, string currency)
    {
        for (var i = fields.Count - 1; i >= 0; i--)
        {
            var field = fields[i];
            if (!PrintedAmount.IsMatch(field)) continue;

            // A till writes a credit with the sign on the right.
            var negative = field.EndsWith('-');
            var text = negative ? field[..^1] : field;

            var minor = MoneyParser.ToMinor(text, options.StoreReceiptUnit, "receipt.amount");
            return new Money(negative ? -minor : minor, currency);
        }

        return null;
    }
}

/// <summary>One printed line, still in its columns.</summary>
internal sealed record PrintLine(IReadOnlyList<string> Fields)
{
    public string Text => string.Join(' ', Fields);
}
