using System.Globalization;
using System.Text.RegularExpressions;
using Connector.Kit.Errors;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.Amazon;

/// <summary>
/// The order date, in the languages amazon.nl actually renders.
///
/// The reference library hands its date strings to
/// <c>dateutil.parser.parse(value, fuzzy=True)</c>, which knows English month
/// names and nothing else. On a Dutch page "12 mei 2026" comes back as
/// <c>None</c> and the order silently loses its date. Both languages are
/// therefore read here, because the language the page renders in is a thing a
/// live run has to settle - see <see cref="AmazonOptions.LanguageOverride"/> -
/// and an adapter that only survives one answer is an adapter that breaks on
/// the other.
///
/// Amazon states a DAY and no time of day, so the offset is the one thing
/// this cannot take from the provider. It is stated as midnight
/// Europe/Amsterdam rather than left as a bare date: the platform's contract
/// is that <c>purchased_at</c> carries a real offset, and a receipt whose day
/// is reinterpreted in the agent's own zone lands on the wrong side of
/// midnight for anybody running outside CET.
/// </summary>
internal static partial class AmazonDate
{
    /// <summary>"12 mei 2026", "12 May 2026", "12 mei. 2026".</summary>
    [GeneratedRegex(@"(\d{1,2})\s+([\p{L}\.]+)\s+(\d{4})")]
    private static partial Regex DayMonthYear { get; }

    /// <summary>"May 12, 2026" - the shape a forced English locale renders.</summary>
    [GeneratedRegex(@"([\p{L}\.]+)\s+(\d{1,2}),?\s+(\d{4})")]
    private static partial Regex MonthDayYear { get; }

    /// <summary>"2026-05-12" and "12-05-2026" / "12/05/2026".</summary>
    [GeneratedRegex(@"(\d{4})-(\d{2})-(\d{2})")]
    private static partial Regex IsoDate { get; }

    [GeneratedRegex(@"(\d{1,2})[-/](\d{1,2})[-/](\d{4})")]
    private static partial Regex NumericDate { get; }

    /// <summary>
    /// Dutch and English month names, full and abbreviated, keyed without
    /// case or trailing dot. Written out rather than taken from a
    /// <see cref="CultureInfo"/>: an agent image with globalization trimmed
    /// still has to be able to date a receipt, and a month table is not the
    /// place to discover that it cannot.
    /// </summary>
    private static readonly Dictionary<string, int> Months = new(StringComparer.OrdinalIgnoreCase)
    {
        ["januari"] = 1, ["jan"] = 1, ["january"] = 1,
        ["februari"] = 2, ["feb"] = 2, ["february"] = 2, ["febr"] = 2,
        ["maart"] = 3, ["mrt"] = 3, ["march"] = 3, ["mar"] = 3,
        ["april"] = 4, ["apr"] = 4,
        ["mei"] = 5, ["may"] = 5,
        ["juni"] = 6, ["jun"] = 6, ["june"] = 6,
        ["juli"] = 7, ["jul"] = 7, ["july"] = 7,
        ["augustus"] = 8, ["aug"] = 8, ["august"] = 8,
        ["september"] = 9, ["sep"] = 9, ["sept"] = 9, ["septembre"] = 9,
        ["oktober"] = 10, ["okt"] = 10, ["october"] = 10, ["oct"] = 10,
        ["november"] = 11, ["nov"] = 11,
        ["december"] = 12, ["dec"] = 12,
    };

    /// <summary>
    /// The day Amazon states, at midnight in the provider's own zone.
    /// </summary>
    public static DateTimeOffset Parse(string? raw, TimeZoneInfo zone, string field)
    {
        if (TryParse(raw, zone, field, out var parsed)) return parsed;

        throw ConnectorException.ProviderChanged(
            $"{AmazonAdapter.ProviderId}: cannot read {field} from '{raw}'; " +
            "no Dutch or English date was recognised");
    }

    public static bool TryParse(string? raw, TimeZoneInfo zone, string field, out DateTimeOffset parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var text = HtmlText.Normalize(raw);
        if (Day(text) is not { } day) return false;

        // Reused rather than reimplemented: the fold rule and the Windows
        // zone fallback both live in ReceiptTime, and a second copy of either
        // is a second thing to get wrong twice a year.
        // DateOnly refuses a format string carrying a time, so the midnight is
        // appended rather than formatted: the day is Amazon's, the time of day
        // is ours to state and there is nothing to read it from.
        parsed = ReceiptTime.Parse(
            day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00:00",
            zone,
            AmazonAdapter.ProviderId,
            field);

        return true;
    }

    private static DateOnly? Day(string text)
    {
        if (IsoDate.Match(text) is { Success: true } iso)
        {
            return Build(Number(iso.Groups[3].Value), Number(iso.Groups[2].Value), Number(iso.Groups[1].Value));
        }

        if (DayMonthYear.Match(text) is { Success: true } dmy && Month(dmy.Groups[2].Value) is { } dmyMonth)
        {
            return Build(Number(dmy.Groups[1].Value), dmyMonth, Number(dmy.Groups[3].Value));
        }

        if (MonthDayYear.Match(text) is { Success: true } mdy && Month(mdy.Groups[1].Value) is { } mdyMonth)
        {
            return Build(Number(mdy.Groups[2].Value), mdyMonth, Number(mdy.Groups[3].Value));
        }

        // Last, and only last: d-m-Y is the Dutch numeric order, but m/d/Y is
        // the American one and the two are indistinguishable for the first
        // twelve days of a month. This runs after every named-month shape so
        // an ambiguous string is only ever reached when nothing better was on
        // the page.
        if (NumericDate.Match(text) is { Success: true } numeric)
        {
            return Build(Number(numeric.Groups[1].Value), Number(numeric.Groups[2].Value),
                Number(numeric.Groups[3].Value));
        }

        return null;
    }

    private static DateOnly? Build(int day, int month, int year)
    {
        if (year is < 1900 or > 2999 || month is < 1 or > 12) return null;
        if (day < 1 || day > DateTime.DaysInMonth(year, month)) return null;

        return new DateOnly(year, month, day);
    }

    private static int? Month(string token)
    {
        var key = token.Trim().TrimEnd('.');
        return Months.TryGetValue(key, out var month) ? month : null;
    }

    private static int Number(string token) => int.Parse(token, CultureInfo.InvariantCulture);
}
