using System.Globalization;
using System.Text.RegularExpressions;
using Connector.Kit.Errors;
using Connector.Kit.Normalization;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.Bol;

/// <summary>One order, in the only shape this adapter cares about.</summary>
internal sealed record BolOrder
{
    public required string Id { get; init; }

    /// <summary>Carries a real offset. See <see cref="BolDate"/> for why that is not optional.</summary>
    public required DateTimeOffset PlacedAt { get; init; }

    public required Money Total { get; init; }

    /// <summary>
    /// bol is a marketplace, so the seller is genuinely useful information
    /// and lands in the receipt's <c>store_name</c>. Null when the order page
    /// does not name one - bol itself ships a large share of its own orders.
    /// </summary>
    public string? SellerName { get; init; }

    public ReceiptPayment Payment { get; init; } = ReceiptFactory.Payment();

    public IReadOnlyList<ReceiptItem> Items { get; init; } = [];
}

/// <summary>
/// The seam.
///
/// The one thing about bol.com that cannot be settled without an account is
/// whether <c>/nl/nl/rnwy/account/bestellingen</c> is server-rendered HTML or
/// a shell around a JSON endpoint. Guessing would produce an adapter that is
/// either right or entirely useless, discovered at 3am by a job that fails
/// with a parser error. So both are built, an option chooses, and everything
/// either one cannot do is reported with the shape's name in the message.
///
/// Implementations are pure: URL in, orders out. No transport, no cookies, no
/// clock - which is what makes both of them fully testable against a recorded
/// payload, and what will make the losing one cheap to delete.
/// </summary>
internal interface IBolOrdersShape
{
    /// <summary><c>html</c> or <c>json</c>. Appears in every failure and in <c>FetchResult.Via</c>.</summary>
    string Name { get; }

    /// <summary>What an operator would have to change if this shape is the wrong one.</summary>
    string ConfigHint { get; }

    string Url(BolOptions options, int page);

    /// <summary>The <c>Accept</c> this shape asks for.</summary>
    string Accept { get; }

    /// <summary>
    /// The request body, or null to send none.
    /// </summary>
    /// <remarks>
    /// GraphQL is a POST with an operation in it, and the two page shapes are
    /// plain GETs. The seam carries the difference rather than the adapter
    /// branching on which shape it happens to be holding - that branch is
    /// exactly what this interface exists to avoid.
    /// </remarks>
    string? Body(BolOptions options, int page) => null;

    /// <summary>
    /// Headers this shape needs beyond the ones every bol request carries.
    /// </summary>
    /// <remarks>
    /// Kept on the shape rather than in the adapter because it is the shape
    /// that knows what it is asking for: bol's edge wants the GraphQL operation
    /// named in a header, and the two page shapes have no operation to name.
    /// </remarks>
    IReadOnlyList<KeyValuePair<string, string>> Headers(BolOptions options) => [];

    /// <summary>
    /// True where this shape's orders carry no stated total and the one on the
    /// receipt is therefore our own sum of its lines.
    /// </summary>
    /// <remarks>
    /// A property of the SHAPE rather than of the adapter, because it is a
    /// property of what the payload states: the page markup shows an order
    /// total and the GraphQL payload does not.
    /// </remarks>
    bool TotalIsDerived => false;

    /// <summary>
    /// Orders out of one page's body. Returns an empty list only where the
    /// body says so itself - an unrecognisable body is
    /// <see cref="ErrorCode.ProviderChanged"/> naming what was missing, never
    /// a quiet zero.
    /// </summary>
    IReadOnlyList<BolOrder> Parse(string body, BolOptions options, TimeZoneInfo zone);
}

internal static class BolShapes
{
    public static IBolOrdersShape For(BolOrdersShape shape) => shape switch
    {
        BolOrdersShape.GraphQl => new BolGraphQlShape(),
        BolOrdersShape.Html => new BolHtmlShape(),
        BolOrdersShape.Json => new BolJsonShape(),
        _ => throw ConnectorException.InvalidRequest($"bol: no orders shape '{shape}'"),
    };
}

/// <summary>
/// Dutch dates, with a real offset on the end.
///
/// <see cref="ReceiptTime"/> does the offset arithmetic and is the only place
/// allowed to; what it cannot do is read "14 juni 2026", because it parses
/// with the invariant culture - and a Dutch month name coming back unparsed
/// is how a near-midnight order lands on the wrong day, or on no day at all.
/// The month table is written out rather than pulled from a culture: an agent
/// image with globalization trimmed would still date every receipt correctly,
/// and failing to date a receipt is not an acceptable outcome of a packaging
/// choice.
/// </summary>
internal static partial class BolDate
{
    private const string ProviderId = BolAdapter.ProviderId;

    private static readonly Dictionary<string, int> Months = new(StringComparer.OrdinalIgnoreCase)
    {
        ["januari"] = 1, ["jan"] = 1,
        ["februari"] = 2, ["feb"] = 2,
        ["maart"] = 3, ["mrt"] = 3, ["mar"] = 3,
        ["april"] = 4, ["apr"] = 4,
        ["mei"] = 5,
        ["juni"] = 6, ["jun"] = 6,
        ["juli"] = 7, ["jul"] = 7,
        ["augustus"] = 8, ["aug"] = 8,
        ["september"] = 9, ["sep"] = 9, ["sept"] = 9,
        ["oktober"] = 10, ["okt"] = 10, ["oct"] = 10,
        ["november"] = 11, ["nov"] = 11,
        ["december"] = 12, ["dec"] = 12,
    };

    /// <summary>Anything already machine-readable goes straight through.</summary>
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}")]
    private static partial Regex Iso { get; }

    /// <summary>"14 juni 2026", "14 jun. 2026", "vrijdag 14 juni 2026".</summary>
    [GeneratedRegex(@"(?<day>\d{1,2})\s+(?<month>[A-Za-z]+)\.?\s+(?<year>\d{4})")]
    private static partial Regex Spelled { get; }

    /// <summary>"14-06-2026" and "14/06/2026", which bol also uses in places.</summary>
    [GeneratedRegex(@"(?<day>\d{1,2})[-/](?<month>\d{1,2})[-/](?<year>\d{4})")]
    private static partial Regex Numeric { get; }

    /// <summary>A time of day riding along with any of the above.</summary>
    [GeneratedRegex(@"(?<hour>\d{1,2})[:.](?<minute>\d{2})(?::(?<second>\d{2}))?")]
    private static partial Regex Clock { get; }

    /// <summary>
    /// <paramref name="raw"/> in whichever of the shapes bol writes, resolved
    /// in <paramref name="zone"/> when it carries no offset of its own.
    /// </summary>
    public static DateTimeOffset Parse(string? raw, TimeZoneInfo zone, string field)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw ConnectorException.ProviderChanged($"{ProviderId}: {field} is missing");
        }

        var text = raw.Trim();

        // An ISO timestamp is bol telling us exactly what it means, including
        // its offset when it states one. Nothing here improves on that.
        if (Iso.IsMatch(text)) return ReceiptTime.Parse(text, zone, ProviderId, field);

        if (Spelled.Match(text) is { Success: true } spelled)
        {
            var name = spelled.Groups["month"].Value;
            if (!Months.TryGetValue(name, out var month))
            {
                throw ConnectorException.ProviderChanged(
                    $"{ProviderId}: {field} names a month this adapter does not know ('{name}')");
            }

            return Compose(
                Number(spelled.Groups["year"]), month, Number(spelled.Groups["day"]), text, zone, field);
        }

        if (Numeric.Match(text) is { Success: true } numeric)
        {
            return Compose(
                Number(numeric.Groups["year"]),
                Number(numeric.Groups["month"]),
                Number(numeric.Groups["day"]),
                text, zone, field);
        }

        // Not a shape we recognise. Named rather than guessed at: a receipt
        // dated wrongly is worse than a run that stops and says why.
        throw ConnectorException.ProviderChanged($"{ProviderId}: cannot parse {field} from '{raw}'");
    }

    /// <summary>
    /// Builds an invariant timestamp and hands the offset question to
    /// <see cref="ReceiptTime"/>, so there is exactly one implementation of
    /// "what does a wall clock in Amsterdam mean".
    ///
    /// A date with no time becomes local midnight, which still carries a real
    /// offset - the provider simply did not tell us the hour, and inventing
    /// one would be worse than admitting the resolution we have.
    /// </summary>
    private static DateTimeOffset Compose(
        int year, int month, int day, string text, TimeZoneInfo zone, string field)
    {
        if (year < 1000 || month is < 1 or > 12 || day is < 1 or > 31)
        {
            throw ConnectorException.ProviderChanged($"{ProviderId}: {field} '{text}' is not a real date");
        }

        var (hour, minute, second) = TimeOfDay(text);

        var stamp = string.Create(CultureInfo.InvariantCulture,
            $"{year:D4}-{month:D2}-{day:D2}T{hour:D2}:{minute:D2}:{second:D2}");

        return ReceiptTime.Parse(stamp, zone, ProviderId, field);
    }

    private static (int Hour, int Minute, int Second) TimeOfDay(string text)
    {
        // A time needs a ':' or a '.' between its halves, which is why no
        // date this method is given can be mistaken for one: the spelled form
        // separates with spaces and the numeric form with '-' or '/'.
        var match = Clock.Match(text);
        if (!match.Success) return (0, 0, 0);

        var hour = Number(match.Groups["hour"]);
        var minute = Number(match.Groups["minute"]);
        var second = match.Groups["second"].Success ? Number(match.Groups["second"]) : 0;

        return hour is >= 0 and < 24 && minute is >= 0 and < 60 && second is >= 0 and < 60
            ? (hour, minute, second)
            : (0, 0, 0);
    }

    private static int Number(Group group) =>
        int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : -1;
}
