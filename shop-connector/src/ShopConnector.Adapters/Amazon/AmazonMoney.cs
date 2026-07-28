using System.Text.RegularExpressions;
using Connector.Kit.Errors;
using Connector.Kit.Normalization;

namespace ShopConnector.Adapters.Amazon;

/// <summary>
/// Dutch money, parsed on purpose rather than by accident.
///
/// This class exists because of a specific, confirmed bug in the best public
/// reference for Amazon's order pages. <c>alexdlaird/amazon-orders</c>, in
/// <c>amazonorders/entity/parsable.py</c>, does:
///
/// <code>
/// value = re.sub("[a-zA-Z$£€₹,]+", "", value)
/// currency = util.to_type(value)
/// </code>
///
/// It strips commas and assumes '.' is the decimal point. On Dutch rendering
/// that is exactly backwards: <c>"€ 12,50"</c> loses its comma and becomes
/// <c>1250.0</c>, and <c>"€ 1.234,56"</c> becomes the unparseable
/// <c>"1.234.56"</c>. The first case does not throw. It returns a number a
/// hundred times too large, and nothing downstream can tell. Porting that
/// regex is the single most expensive mistake available on this provider.
///
/// So: the unit is DECLARED by the caller (<see cref="MoneyUnit"/>), never
/// sniffed from the value; the currency is DECLARED, never inferred from a
/// symbol; and the actual digits go through <see cref="MoneyParser"/>, which
/// already resolves comma decimals and mixed grouping. What is added here is
/// the one case <see cref="MoneyParser"/> cannot decide alone - see
/// <see cref="Normalize"/>.
/// </summary>
internal static partial class AmazonMoney
{
    /// <summary>
    /// A whole-euro amount written with Dutch grouping and no cents:
    /// <c>"1.234"</c>, <c>"12.500"</c>. Anchored, and the trailing groups must
    /// be exactly three digits.
    /// </summary>
    [GeneratedRegex(@"^\d{1,3}(?:\.\d{3})+$")]
    private static partial Regex DutchGrouping { get; }

    /// <summary>The same shape with English grouping: <c>"1,234"</c>.</summary>
    [GeneratedRegex(@"^\d{1,3}(?:,\d{3})+$")]
    private static partial Regex EnglishGrouping { get; }

    /// <summary>What is left once every currency token and separator is gone.</summary>
    [GeneratedRegex(@"^[0-9.,]+$")]
    private static partial Regex DigitsAndSeparators { get; }

    /// <summary>
    /// The first amount inside a longer run of text - a totals row reads
    /// <c>"Totaal: € 1.234,56"</c> and a line item
    /// <c>"1 van: Titel … € 12,50"</c>.
    ///
    /// The currency alternation is deliberately WIDER than the tokens this
    /// storefront accepts. Matching only euros would make a dollar amount look
    /// like no amount at all, and "no total on the card" is a much worse
    /// diagnosis than "this order is not in euros" - the first sends an
    /// operator hunting for a selector that is working perfectly.
    /// </summary>
    [GeneratedRegex(@"(?:EUR|USD|GBP|CHF|SEK|PLN|[€$£¥₹])\s*-?\s*[0-9][0-9.,]*"
                    + @"|-?\s*[0-9][0-9.,]*\s*(?:EUR|USD|GBP|CHF|SEK|PLN|[€$£¥₹])")]
    private static partial Regex AmountInText { get; }

    /// <summary>
    /// Parses one rendered amount under a declared unit and a declared
    /// currency.
    /// </summary>
    public static Money Parse(string? raw, MoneyUnit unit, string currency, string field, AmazonOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(raw))
        {
            throw ConnectorException.ProviderChanged($"{AmazonAdapter.ProviderId}: {field} is empty");
        }

        var text = HtmlText.Normalize(raw);
        var negative = IsNegative(text);
        var digits = StripCurrency(text, field, options);

        return new Money(MoneyParser.ToMinor(Normalize(digits), unit, field) * (negative ? -1 : 1), currency);
    }

    /// <summary>
    /// Same, but for a cell that carries prose around the amount. Returns null
    /// when the text holds no amount at all, which is a normal answer for a
    /// row that is only a label.
    /// </summary>
    public static Money? Find(string? raw, MoneyUnit unit, string currency, string field, AmazonOptions options)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var text = HtmlText.Normalize(raw);
        var match = AmountInText.Match(text);
        if (!match.Success) return null;

        var negative = IsNegativeAt(text, match);
        var digits = StripCurrency(match.Value, field, options);
        var value = MoneyParser.ToMinor(Normalize(digits), unit, field);

        return new Money(negative ? -value : value, currency);
    }

    /// <summary>
    /// A totals row split into what it is called and what it is worth.
    ///
    /// The label is everything before the amount, which is how a row survives
    /// being marked up as one cell, two cells or a definition list - none of
    /// which the three reference projects agree on either.
    /// </summary>
    public static (string Label, Money? Amount) Row(
        string? raw, MoneyUnit unit, string currency, string field, AmazonOptions options)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (string.Empty, null);

        var text = HtmlText.Normalize(raw);
        var match = AmountInText.Match(text);
        if (!match.Success) return (text, null);

        // The separator characters between a label and its amount, whichever
        // of them the row happens to use: "Actiekorting: -€ 9,99" leaves
        // "Actiekorting: -" behind, and that trailing punctuation ends up as
        // an emitted item's NAME.
        var label = text[..match.Index].Trim().TrimEnd(':', '-', '−', ' ');
        var negative = IsNegativeAt(text, match);
        var value = MoneyParser.ToMinor(Normalize(StripCurrency(match.Value, field, options)), unit, field);

        return (label, new Money(negative ? -value : value, currency));
    }

    private static bool IsNegative(string text) =>
        text.Contains('-', StringComparison.Ordinal)
        || text.Contains('−', StringComparison.Ordinal)     // minus sign
        || (text.Contains('(', StringComparison.Ordinal) && text.Contains(')', StringComparison.Ordinal));

    /// <summary>
    /// Whether the amount at <paramref name="match"/> is negative, judged from
    /// the token itself and the character immediately before it - never from
    /// the whole line.
    ///
    /// The difference is not theoretical. A Dutch invoice row is labelled
    /// "Btw-bedrag" and a promotion row "Actie-korting"; scanning the line for
    /// a hyphen turns the first into a credit and leaves the second one right
    /// by luck. A receipt whose tax is subtracted instead of added still looks
    /// perfectly plausible, and only reconciliation would ever notice.
    /// </summary>
    private static bool IsNegativeAt(string text, Match match)
    {
        if (IsNegative(match.Value)) return true;

        var i = match.Index - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i])) i--;

        if (i < 0) return false;
        if (text[i] is '-' or '−') return true;

        var after = text.AsSpan(match.Index + match.Length).TrimStart();
        return text[i] == '(' && after.Length > 0 && after[0] == ')';
    }

    /// <summary>
    /// Removes the currency tokens the operator has declared acceptable, and
    /// refuses anything else.
    ///
    /// This is the guard that keeps a non-euro order from being recorded as
    /// euros. amazon.nl bills in EUR, but a marketplace order, a gift card in
    /// another currency or a locale flip would render a different symbol - and
    /// a symbol is never enough to name a currency anyway ("$" is at least
    /// four of them), so the honest outcome is to stop and say what was seen
    /// rather than to guess or to silently drop the symbol.
    /// </summary>
    private static string StripCurrency(string text, string field, AmazonOptions options)
    {
        var stripped = text;
        foreach (var token in options.CurrencyTokens)
        {
            stripped = stripped.Replace(token, string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        stripped = stripped
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("(", string.Empty, StringComparison.Ordinal)
            .Replace(")", string.Empty, StringComparison.Ordinal)
            .Replace("+", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("−", string.Empty, StringComparison.Ordinal);

        if (!DigitsAndSeparators.IsMatch(stripped))
        {
            throw ConnectorException.ProviderChanged(
                $"{AmazonAdapter.ProviderId}: {field} '{text}' is not an amount in " +
                $"[{string.Join(", ", options.CurrencyTokens)}]; " +
                "an amount in another currency must not be recorded as one in this one");
        }

        return stripped;
    }

    /// <summary>
    /// The one decision <see cref="MoneyParser"/> cannot make for us, and the
    /// reason this method exists at all.
    ///
    /// With both separators present the kit resolves the string correctly:
    /// whichever comes last is the decimal point, so <c>"1.234,56"</c> and
    /// <c>"1,234.56"</c> both land on 123456 minor units. With only one
    /// separator it reads that separator as the decimal point, which is right
    /// for <c>"12,50"</c> and <c>"12.50"</c> and wrong for a whole-euro amount
    /// written with grouping: <c>"€ 1.234"</c> would become 1.234 - one
    /// thousandth of the real figure - and, like the reference's bug, it would
    /// not throw.
    ///
    /// Note what is and is not decided here. The UNIT stays declared by the
    /// caller: this never turns a major amount into a minor one or back. All
    /// that is resolved is which character in a rendered string is a thousands
    /// separator, which is unavoidable when the provider publishes no number
    /// at all - only text - and it is resolved by an anchored shape rather
    /// than by preference: exactly three digits after every separator, and no
    /// second kind of separator anywhere.
    /// </summary>
    private static string Normalize(string digits)
    {
        if (DutchGrouping.IsMatch(digits))
        {
            return digits.Replace(".", string.Empty, StringComparison.Ordinal);
        }

        if (EnglishGrouping.IsMatch(digits))
        {
            return digits.Replace(",", string.Empty, StringComparison.Ordinal);
        }

        return digits;
    }
}
