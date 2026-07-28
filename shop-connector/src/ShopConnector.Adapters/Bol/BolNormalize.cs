using Connector.Kit.Normalization;

namespace ShopConnector.Adapters.Bol;

/// <summary>
/// The two normalizations both shapes need, in one place.
///
/// Shared rather than copied because the whole point of the two shapes is
/// that they produce the same receipts: a test asserts exactly that against
/// the same order recorded both ways, and it would be worth nothing if the
/// halves it compares had drifting ideas about what "card" means or how a
/// line total is derived from a unit price.
/// </summary>
internal static class BolNormalize
{
    /// <summary>
    /// A line total from its unit price, where the payload states one and not
    /// the other. Multiplication, not a guess: the quantity is the provider's
    /// own, and where there is none a single unit is what "no quantity shown"
    /// means on every retail page there is.
    /// </summary>
    public static Money Extended(Money unit, decimal? quantity)
    {
        var count = quantity is > 0 ? decimal.Round(quantity.Value, 3, MidpointRounding.AwayFromZero) : 1m;
        return unit with { Value = (long)decimal.Round(unit.Value * count, 0, MidpointRounding.AwayFromZero) };
    }

    /// <summary>
    /// The payment method in the vocabulary <see cref="ReceiptPayment"/>
    /// documents.
    ///
    /// Never invented: text bol does not recognisably name is <c>other</c>,
    /// and no text at all stays null so the consumer knows its match against
    /// a bank transaction will be weaker, rather than being handed a guess it
    /// would trust. iDEAL is called out because it is how most of the
    /// Netherlands pays and it matches a bank line exactly; "achteraf betalen"
    /// is bol's pay-later, which settles days after the order and would
    /// otherwise look like a missing payment.
    /// </summary>
    public static string? Method(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        if (raw.Contains("ideal", StringComparison.OrdinalIgnoreCase)) return "ideal";

        if (raw.Contains("achteraf", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("factuur", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("invoice", StringComparison.OrdinalIgnoreCase))
        {
            return "invoice";
        }

        if (raw.Contains("cadeaubon", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("gift", StringComparison.OrdinalIgnoreCase))
        {
            return "giftcard";
        }

        return raw.Contains("creditcard", StringComparison.OrdinalIgnoreCase) ||
               raw.Contains("credit card", StringComparison.OrdinalIgnoreCase) ||
               raw.Contains("visa", StringComparison.OrdinalIgnoreCase) ||
               raw.Contains("mastercard", StringComparison.OrdinalIgnoreCase) ||
               raw.Contains("maestro", StringComparison.OrdinalIgnoreCase) ||
               raw.Contains("pin", StringComparison.OrdinalIgnoreCase)
            ? "card"
            : "other";
    }
}
