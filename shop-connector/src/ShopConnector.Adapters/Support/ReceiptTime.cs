using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Connector.Kit.Errors;

namespace ShopConnector.Adapters.Support;

/// <summary>
/// Turns a provider timestamp into one that carries a real offset.
///
/// A bare date, or a wall-clock string reinterpreted in the agent's own
/// time zone, makes a near-midnight purchase land on the wrong day - and a
/// receipt matched to the wrong day is worse than one not matched at all.
/// So: an explicit offset is honoured, and anything without one is
/// interpreted in the provider's country zone rather than the machine's.
/// </summary>
internal static partial class ReceiptTime
{
    [GeneratedRegex(@"(?:[Zz]|[+-]\d{2}:?\d{2})$")]
    private static partial Regex OffsetSuffix { get; }

    public static DateTimeOffset Parse(string? raw, TimeZoneInfo zone, string providerId, string field)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw ConnectorException.ProviderChanged($"{providerId}: {field} is missing");
        }

        var text = raw.Trim();

        if (OffsetSuffix.IsMatch(text) &&
            DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var stated))
        {
            return stated;
        }

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var wallClock))
        {
            // Kind must be Unspecified for GetUtcOffset to read the value as
            // local *to that zone* rather than to this machine. During the
            // autumn fold an ambiguous hour resolves to standard time; an
            // hour of error twice a year beats a whole-day error every day.
            var unspecified = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);
            return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified));
        }

        throw ConnectorException.ProviderChanged($"{providerId}: cannot parse {field} timestamp '{raw}'");
    }
}

/// <summary>
/// Country to time zone, written out rather than derived. Every country in
/// the Lidl list happens to sit on CET today, so a shortcut would work and
/// would silently break the day a non-CET country is added - an explicit
/// table makes that a visible edit.
/// </summary>
internal static class RetailZones
{
    private const string Netherlands = "Europe/Amsterdam";

    private static readonly Dictionary<string, string> ByCountry = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NL"] = Netherlands,
        ["BE"] = "Europe/Brussels",
        ["DE"] = "Europe/Berlin",
        ["AT"] = "Europe/Vienna",
        ["FR"] = "Europe/Paris",
        ["IT"] = "Europe/Rome",
        ["ES"] = "Europe/Madrid",
    };

    /// <summary>
    /// Windows ships its own zone ids. .NET maps IANA ids through ICU on
    /// every supported platform, but an agent image with globalization
    /// trimmed would fail the lookup, and failing to date a receipt is not
    /// an acceptable outcome of a packaging choice.
    /// </summary>
    private static readonly Dictionary<string, string> WindowsFallback = new(StringComparer.Ordinal)
    {
        [Netherlands] = "W. Europe Standard Time",
        ["Europe/Brussels"] = "Romance Standard Time",
        ["Europe/Berlin"] = "W. Europe Standard Time",
        ["Europe/Vienna"] = "W. Europe Standard Time",
        ["Europe/Paris"] = "Romance Standard Time",
        ["Europe/Rome"] = "W. Europe Standard Time",
        ["Europe/Madrid"] = "Romance Standard Time",
    };

    private static readonly ConcurrentDictionary<string, TimeZoneInfo> Cache = new(StringComparer.Ordinal);

    public static TimeZoneInfo Dutch => For("NL");

    public static TimeZoneInfo For(string? country)
    {
        var id = country is not null && ByCountry.TryGetValue(country, out var mapped) ? mapped : Netherlands;
        return Cache.GetOrAdd(id, Resolve);
    }

    private static TimeZoneInfo Resolve(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            if (WindowsFallback.TryGetValue(id, out var windowsId))
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
                }
                catch (Exception inner) when (inner is TimeZoneNotFoundException or InvalidTimeZoneException)
                {
                    throw new ConnectorException(ErrorCode.Internal, $"time zone '{id}' unavailable on this host", inner);
                }
            }

            throw new ConnectorException(ErrorCode.Internal, $"time zone '{id}' unavailable on this host", ex);
        }
    }
}
