using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShopConnector.Adapters.Support;

/// <summary>
/// Alias-tolerant, case-insensitive reads over a provider payload.
///
/// Retailers rename and re-case fields between releases without telling
/// anyone, so a parser bound to one spelling breaks on a change that meant
/// nothing. Tolerating extra fields and aliases is the rule here. The one
/// deliberate exception is money, which is never inferred - see
/// <see cref="MoneyReader"/>.
/// </summary>
internal static partial class JsonAccess
{
    /// <summary>
    /// Leading numeric token of a loosely formatted quantity: providers
    /// write "2", "2 x", "0,750 kg" and "1.000" for the same idea.
    /// </summary>
    [GeneratedRegex(@"-?\d+(?:[.,]\d+)?")]
    private static partial Regex NumericToken { get; }

    /// <summary>
    /// First property matching any of <paramref name="names"/>. Exact
    /// matches win over case-insensitive ones, and the name order is the
    /// preference order - put the confirmed spelling first.
    /// </summary>
    public static bool TryProp(JsonElement parent, out JsonElement value, params string[] names)
    {
        value = default;
        if (parent.ValueKind != JsonValueKind.Object) return false;

        foreach (var name in names)
        {
            if (parent.TryGetProperty(name, out var exact) && exact.ValueKind != JsonValueKind.Null)
            {
                value = exact;
                return true;
            }
        }

        foreach (var name in names)
        {
            foreach (var property in parent.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Null) continue;
                if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;

                value = property.Value;
                return true;
            }
        }

        return false;
    }

    /// <summary>Walks a dotted path, e.g. <c>data.orders.items</c>.</summary>
    public static bool TryPath(JsonElement root, string path, out JsonElement value)
    {
        value = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryProp(value, out var next, segment))
            {
                value = default;
                return false;
            }

            value = next;
        }

        return true;
    }

    public static string? Str(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };

    public static string? StrOf(JsonElement parent, params string[] names) =>
        TryProp(parent, out var value, names) ? Str(value) : null;

    /// <summary>
    /// A count, not an amount. Loose parsing is safe here precisely because
    /// a quantity is never money: a misread quantity fails reconciliation
    /// loudly instead of corrupting a total silently.
    /// </summary>
    public static decimal? Quantity(JsonElement parent, params string[] names)
    {
        if (!TryProp(parent, out var value, names)) return null;
        if (value.ValueKind == JsonValueKind.Number) return value.TryGetDecimal(out var direct) ? direct : null;

        var raw = Str(value);
        if (raw is null) return null;

        var match = NumericToken.Match(raw);
        if (!match.Success) return null;

        return decimal.TryParse(match.Value.Replace(',', '.'), NumberStyles.Number,
            CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// The first named property that is an array. Returns an empty list
    /// rather than throwing: an absent collection is a normal provider
    /// answer (a receipt with no discounts), not a shape change.
    /// </summary>
    public static IReadOnlyList<JsonElement> Array(JsonElement parent, params string[] names)
    {
        if (!TryProp(parent, out var value, names) || value.ValueKind != JsonValueKind.Array) return [];
        return [.. value.EnumerateArray()];
    }

    public static IReadOnlyList<JsonElement> AsArray(JsonElement element) =>
        element.ValueKind == JsonValueKind.Array ? [.. element.EnumerateArray()] : [];

    /// <summary>
    /// Last <paramref name="count"/> digits of a masked instrument, e.g.
    /// <c>"•••• 1234"</c> or <c>"XXXXXXXXXXXX1234"</c>. Null when the
    /// provider gave us nothing usable - and null must stay null, because
    /// the consumer needs to know its match will be weaker rather than be
    /// handed a guess.
    /// </summary>
    public static string? Tail(string? masked, int count = 4)
    {
        if (string.IsNullOrWhiteSpace(masked)) return null;

        var digits = new string([.. masked.Where(char.IsAsciiDigit)]);
        return digits.Length >= count ? digits[^count..] : null;
    }
}
