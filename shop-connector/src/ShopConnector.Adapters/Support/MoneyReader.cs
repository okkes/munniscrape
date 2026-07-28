using System.Text.Json;
using Connector.Kit.Errors;
using Connector.Kit.Normalization;

namespace ShopConnector.Adapters.Support;

/// <summary>
/// Reads an amount under a unit the caller has already declared.
///
/// There is no overload that inspects the value and decides. Every provider
/// in this service represents money differently and at least one is
/// inconsistent between its own endpoints, so a heuristic would corrupt
/// financial data silently - the one failure mode worse than crashing. The
/// declared unit governs, the JSON type is only transport, and the result
/// is checked against the receipt's stated total by
/// <see cref="Reconciliation"/>.
/// </summary>
internal static class MoneyReader
{
    public static Money Read(JsonElement element, MoneyUnit unit, string currency, string field) => element.ValueKind switch
    {
        JsonValueKind.Number => new Money(MoneyParser.ToMinor(element.GetDecimal(), unit), currency),
        JsonValueKind.String => new Money(MoneyParser.ToMinor(element.GetString(), unit, field), currency),
        _ => throw ConnectorException.ProviderChanged($"{field}: expected a money value, got {element.ValueKind}"),
    };

    /// <summary>
    /// Resolves a possibly-nested amount: providers wrap the same number as
    /// <c>12.34</c>, <c>{"amount": 12.34}</c> and
    /// <c>{"amount": {"amount": 12.34}}</c>, sometimes across two endpoints
    /// of the same API.
    /// </summary>
    public static Money? Optional(JsonElement parent, MoneyUnit unit, string currency, string field, params string[] names)
    {
        if (!JsonAccess.TryProp(parent, out var value, names)) return null;
        return Unwrap(value, unit, currency, field);
    }

    public static Money Require(JsonElement parent, MoneyUnit unit, string currency, string field, params string[] names) =>
        Optional(parent, unit, currency, field, names)
        ?? throw ConnectorException.ProviderChanged($"{field}: no amount under any of [{string.Join(", ", names)}]");

    private static Money? Unwrap(JsonElement value, MoneyUnit unit, string currency, string field)
    {
        for (var depth = 0; depth < 3 && value.ValueKind == JsonValueKind.Object; depth++)
        {
            if (!JsonAccess.TryProp(value, out var inner, "amount", "value", "gross", "total")) return null;
            value = inner;
        }

        return value.ValueKind is JsonValueKind.Number or JsonValueKind.String
            ? Read(value, unit, currency, field)
            : null;
    }

    /// <summary>
    /// ISO-4217 code where the provider states one, otherwise the caller's
    /// default. Never inferred from a symbol: "$" is at least four
    /// different currencies.
    /// </summary>
    public static string Currency(JsonElement parent, string fallback)
    {
        if (!JsonAccess.TryProp(parent, out var value, "currency", "currencyCode", "isoCode")) return fallback;

        var code = value.ValueKind == JsonValueKind.Object
            ? JsonAccess.StrOf(value, "code", "isoCode", "currencyCode")
            : JsonAccess.Str(value);

        return code is { Length: 3 } ? code.ToUpperInvariant() : fallback;
    }
}
