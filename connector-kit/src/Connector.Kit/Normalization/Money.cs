using System.Globalization;
using System.Text.Json.Serialization;
using Connector.Kit.Errors;

namespace Connector.Kit.Normalization;

/// <summary>
/// Money is always minor units plus an ISO-4217 code. Never a float, never
/// a decimal that has been through a locale.
/// </summary>
public readonly record struct Money
{
    [JsonConstructor]
    public Money(long value, string currency)
    {
        Value = value;
        Currency = currency;
    }

    /// <summary>Minor units. Negative means money left the account.</summary>
    public long Value { get; init; }

    /// <summary>ISO-4217, uppercase.</summary>
    public string Currency { get; init; }

    public static Money Eur(long minorUnits) => new(minorUnits, "EUR");

    public static Money Zero(string currency = "EUR") => new(0, currency);

    public Money Negated() => this with { Value = -Value };

    public Money Abs() => Value < 0 ? Negated() : this;

    public static Money operator +(Money a, Money b)
    {
        EnsureSameCurrency(a, b);
        return a with { Value = a.Value + b.Value };
    }

    public static Money operator -(Money a, Money b)
    {
        EnsureSameCurrency(a, b);
        return a with { Value = a.Value - b.Value };
    }

    private static void EnsureSameCurrency(Money a, Money b)
    {
        if (!string.Equals(a.Currency, b.Currency, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"currency mismatch: {a.Currency} vs {b.Currency}");
        }
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Value / 100m:0.00} {Currency}");
}

/// <summary>
/// How a provider represents an amount in a given field.
///
/// There is deliberately no "guess it" option. Every one of these APIs
/// represents money differently, and some are inconsistent between their
/// own endpoints. A heuristic that guesses wrong corrupts financial data
/// silently, which is the one failure mode worse than crashing - so each
/// adapter declares the unit per field and the result is checked against a
/// stated total (see <see cref="Reconciliation"/>).
/// </summary>
public enum MoneyUnit
{
    /// <summary>Already minor units: <c>1234</c> is 12.34.</summary>
    Minor,

    /// <summary>Major units with a fractional part: <c>12.34</c>.</summary>
    MajorDecimal,

    /// <summary>Major units as a string, possibly with a comma separator: <c>"12,34"</c>.</summary>
    MajorString,
}

public static class MoneyParser
{
    /// <summary>
    /// Converts a provider value to minor units under a declared unit. The
    /// unit is never inferred from the value's shape.
    /// </summary>
    public static long ToMinor(decimal raw, MoneyUnit unit) => unit switch
    {
        MoneyUnit.Minor => checked((long)decimal.Truncate(raw)),
        MoneyUnit.MajorDecimal or MoneyUnit.MajorString => checked((long)decimal.Round(raw * 100m, 0, MidpointRounding.AwayFromZero)),
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, null),
    };

    /// <summary>
    /// Parses a provider string under a declared unit. Accepts both '.' and
    /// ',' as the decimal separator because Dutch providers use both, often
    /// within the same account, but refuses anything genuinely ambiguous
    /// rather than picking a reading.
    /// </summary>
    public static long ToMinor(string? raw, MoneyUnit unit, string field = "amount")
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw ConnectorException.ProviderChanged($"{field}: empty money value");
        }

        var cleaned = raw.Trim().Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("€", string.Empty, StringComparison.Ordinal);

        var hasDot = cleaned.Contains('.', StringComparison.Ordinal);
        var hasComma = cleaned.Contains(',', StringComparison.Ordinal);

        if (hasDot && hasComma)
        {
            // Whichever comes last is the decimal separator; the other is a
            // thousands grouping. "1.234,56" and "1,234.56" both resolve.
            var decimalSep = cleaned.LastIndexOf(',') > cleaned.LastIndexOf('.') ? ',' : '.';
            var groupSep = decimalSep == ',' ? '.' : ',';
            cleaned = cleaned.Replace(groupSep.ToString(), string.Empty, StringComparison.Ordinal)
                .Replace(decimalSep, '.');
        }
        else if (hasComma)
        {
            cleaned = cleaned.Replace(',', '.');
        }

        if (!decimal.TryParse(cleaned, NumberStyles.Number | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out var parsed))
        {
            throw ConnectorException.ProviderChanged($"{field}: cannot parse money value '{raw}'");
        }

        return ToMinor(parsed, unit);
    }
}
