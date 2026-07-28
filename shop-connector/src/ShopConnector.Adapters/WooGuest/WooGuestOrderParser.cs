using System.Globalization;
using System.Text.Json;
using Connector.Kit.Errors;
using Connector.Kit.Normalization;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.WooGuest;

/// <summary>
/// Turns one Store API order into one normalized receipt.
///
/// The arithmetic is the interesting part and it is fully determined by the
/// source. <c>OrderSchema::get_totals()</c> states <c>total_items</c> as the
/// sum of each line's post-discount, net-of-tax total, so the order's
/// grand total decomposes as
/// <c>total_items + total_items_tax + total_fees + total_fees_tax +
/// total_shipping + total_shipping_tax</c> - with <c>total_discount</c>
/// already absorbed into the line totals and stated separately only for
/// display. This parser therefore emits each line gross of its coupon with
/// the coupon as an explicit negative, which nets to exactly those figures
/// and lets <see cref="Reconciliation"/> check the whole thing against
/// <c>total_price</c>.
/// </summary>
internal static class WooGuestOrderParser
{
    private const string ProviderId = WooGuestAdapter.ProviderId;

    /// <summary>
    /// The order's stated error, where WooCommerce answered with one.
    ///
    /// A WP_Error from a permission callback comes back as
    /// <c>{"code": …, "message": …, "data": {"status": …}}</c>, and reading
    /// it is what keeps this adapter from libelling a shop's edge or a
    /// user's typing. In particular <c>woocommerce_rest_invalid_user</c>
    /// arrives with HTTP 403 - the status a bot wall uses - and means
    /// something completely different.
    /// </summary>
    public static ConnectorException Translate(JsonElement root, int status, WooGuestOptions options)
    {
        var code = JsonAccess.StrOf(root, "code");

        if (code is null || !code.StartsWith(options.ErrorCodePrefix, StringComparison.Ordinal))
        {
            // Not WooCommerce answering. Whatever it was, it does not get to
            // produce a credential verdict.
            return ProviderHttp.Failure(
                (System.Net.HttpStatusCode)status, ProviderId, "order", options.BlockedStatuses);
        }

        if (string.Equals(code, options.InvalidUserCode, StringComparison.Ordinal))
        {
            // The order exists and belongs to a registered customer. No key
            // and no e-mail will ever open it on this route, so this is a
            // permanent property of the order rather than a bad reference.
            return ConnectorException.Unsupported(
                $"{ProviderId}: that order was placed with an account at the shop, " +
                "so the store API's guest route cannot read it");
        }

        if (string.Equals(code, options.InvalidOrderCode, StringComparison.Ordinal) ||
            string.Equals(code, options.InvalidEmailCode, StringComparison.Ordinal))
        {
            // The reference is the credential here, so a reference that does
            // not open the order is a credential failure - never retried,
            // which is what we want against a shop whose host may be
            // counting failed attempts.
            return ConnectorException.InvalidCredentials(
                $"{ProviderId}: the shop rejected the order reference ({code})");
        }

        return ConnectorException.ProviderChanged($"{ProviderId}: the store API returned '{code}' ({status})");
    }

    public static Receipt Parse(
        JsonElement order,
        WooGuestOptions options,
        string sessionId,
        string merchantHost,
        string? statedOrderDate,
        TimeZoneInfo zone)
    {
        if (order.ValueKind != JsonValueKind.Object)
        {
            throw ConnectorException.ProviderChanged($"{ProviderId}: the order is a {order.ValueKind}, not an object");
        }

        var id = JsonAccess.StrOf(order, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            throw ConnectorException.ProviderChanged($"{ProviderId}: the order carries no id");
        }

        if (!JsonAccess.TryProp(order, out var totals, "totals") || totals.ValueKind != JsonValueKind.Object)
        {
            throw ConnectorException.ProviderChanged($"{ProviderId}: the order carries no totals");
        }

        EnsureMinorUnit(totals, options, "totals");

        var currency = Currency(totals, options);
        var total = MoneyReader.Require(totals, options.AmountUnit, currency, "totals.total_price", "total_price");

        return ReceiptFactory.Build(
            sessionId,
            id,
            // The shop, not the platform: a consumer handed receipts all
            // labelled "woocommerce" has lost the fact that made them worth
            // having.
            new Merchant { Id = merchantHost, Name = merchantHost, StoreName = null },
            PurchasedAt(order, options, statedOrderDate, zone),
            total,
            // Nothing in the order schema names a payment method, a card or
            // an IBAN - checked field by field, not assumed - so every tail
            // is explicitly null and the consumer knows its match will be
            // weaker.
            ReceiptFactory.Payment(),
            BuildItems(order, totals, options, currency));
    }

    /// <summary>
    /// The exponent the payload's amounts are actually scaled by.
    ///
    /// CONFIRMED that <c>currency_minor_unit</c> is <c>wc_get_price_decimals()</c>,
    /// a per-shop setting. This service's <see cref="Money"/> is hundredths;
    /// a shop reporting anything else is a shape we cannot carry, and saying
    /// so by name is the only honest answer. Silently dividing by the wrong
    /// power of ten would put a plausible, wrong number in front of somebody.
    /// </summary>
    public static void EnsureMinorUnit(JsonElement carrier, WooGuestOptions options, string where)
    {
        if (!JsonAccess.TryProp(carrier, out var stated, "currency_minor_unit")) return;

        var exponent = stated.ValueKind == JsonValueKind.Number && stated.TryGetInt32(out var value)
            ? value
            : int.TryParse(JsonAccess.Str(stated), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : -1;

        if (exponent == options.ExpectedMinorUnitExponent) return;

        throw ConnectorException.ProviderChanged(
            $"{ProviderId}: {where}.currency_minor_unit is {exponent}, not {options.ExpectedMinorUnitExponent}; " +
            "this shop's amounts are not in hundredths and cannot be read safely");
    }

    /// <summary>
    /// The order's own date where a shop states one, and the date supplied
    /// with the reference otherwise.
    ///
    /// CONFIRMED that stock WooCommerce states none: the Store API's order
    /// response has no date field at all. A bare day is read at midnight in
    /// the shop's country - a real offset on a real day, which is the point
    /// - and a full instant is honoured as given, which is what the e-mail
    /// connector will be able to supply from the message header.
    /// </summary>
    public static DateTimeOffset PurchasedAt(
        JsonElement order, WooGuestOptions options, string? stated, TimeZoneInfo zone)
    {
        foreach (var field in options.OrderDateFields)
        {
            var value = JsonAccess.StrOf(order, field);
            if (string.IsNullOrWhiteSpace(value)) continue;

            // A `_gmt` field is UTC without saying so; the shared reader
            // honours an explicit offset and otherwise uses the zone, and
            // treating a GMT field as shop-local would be an hour or two out
            // rather than a day.
            return ReceiptTime.Parse(value, field.EndsWith("_gmt", StringComparison.Ordinal)
                ? TimeZoneInfo.Utc
                : zone, ProviderId, field);
        }

        if (!string.IsNullOrWhiteSpace(stated)) return ReceiptTime.Parse(stated, zone, ProviderId, "order_date");

        throw ConnectorException.ProviderChanged(
            $"{ProviderId}: the order states no date under any of " +
            $"[{string.Join(", ", options.OrderDateFields)}] and none was supplied with the reference");
    }

    private static IReadOnlyList<ReceiptItem> BuildItems(
        JsonElement order, JsonElement totals, WooGuestOptions options, string currency)
    {
        var unit = options.AmountUnit;
        var lines = new List<ReceiptItem>();

        foreach (var item in JsonAccess.Array(order, "items"))
        {
            var name = JsonAccess.StrOf(item, "name") ?? JsonAccess.StrOf(item, "sku");
            if (string.IsNullOrWhiteSpace(name)) continue;

            if (!JsonAccess.TryProp(item, out var itemTotals, "totals")) continue;
            EnsureMinorUnit(itemTotals, options, $"items[{name}].totals");

            var gross = Sum(itemTotals, options, currency, name, "line_subtotal", "line_subtotal_tax");
            var net = Sum(itemTotals, options, currency, name, "line_total", "line_total_tax");

            // A line with neither is not a line we can price. Skipping it
            // would quietly shrink the receipt, so the whole payload is
            // called out instead.
            if (gross is null && net is null)
            {
                throw ConnectorException.ProviderChanged(
                    $"{ProviderId}: items[{name}].totals carries neither line_subtotal nor line_total");
            }

            var quantity = JsonAccess.Quantity(item, "quantity");
            var grossTotal = gross ?? net!.Value;
            var discount = net is { } paid && grossTotal.Value > paid.Value
                ? new ReceiptDiscount { Amount = (grossTotal - paid).Negated(), Label = null }
                : null;

            lines.Add(new ReceiptItem
            {
                Name = name,
                Quantity = quantity,
                // Only where the line is a single unit, in which case the
                // line total *is* the unit price and nothing is inferred.
                //
                // The payload's own `prices.price` is deliberately not used:
                // OrderItemSchema builds it with
                // prepare_product_price_response($product, …), which is the
                // product's price in the catalogue today - not what this
                // order paid. Reading it would put a confident, wrong number
                // next to every item whose price has since changed.
                UnitPrice = quantity == 1m ? grossTotal : null,
                Total = grossTotal,
                Discount = discount,
            });
        }

        AppendFees(lines, order, options, currency);
        AppendShipping(lines, totals, options, currency);
        return lines;
    }

    /// <summary>
    /// Fees, gross of their own tax, so they land on the same side of tax as
    /// the item lines.
    /// </summary>
    private static void AppendFees(
        List<ReceiptItem> lines, JsonElement order, WooGuestOptions options, string currency)
    {
        foreach (var fee in JsonAccess.Array(order, "fees"))
        {
            var name = JsonAccess.StrOf(fee, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;

            if (!JsonAccess.TryProp(fee, out var feeTotals, "totals")) continue;
            EnsureMinorUnit(feeTotals, options, $"fees[{name}].totals");

            var amount = Sum(feeTotals, options, currency, name, "total", "total_tax");
            if (amount is not { } value || value.Value == 0) continue;

            lines.Add(new ReceiptItem { Name = name, Total = value });
        }
    }

    /// <summary>
    /// Shipping, gross of tax. Both halves are declared
    /// <c>[string, null]</c> in the schema - a shop that has not calculated
    /// shipping sends null - and a null is an answer, not a shape change.
    /// </summary>
    private static void AppendShipping(
        List<ReceiptItem> lines, JsonElement totals, WooGuestOptions options, string currency)
    {
        var shipping = Sum(totals, options, currency, "shipping", "total_shipping", "total_shipping_tax");
        if (shipping is not { } amount || amount.Value == 0) return;

        lines.Add(new ReceiptItem { Name = options.ShippingLineName, Total = amount });
    }

    /// <summary>
    /// The payload's own ISO code, never a guess from a symbol.
    ///
    /// <see cref="MoneyReader.Currency"/> knows <c>currency</c>,
    /// <c>currencyCode</c> and <c>isoCode</c>; the Store API spells it
    /// <c>currency_code</c>, which none of those match, so it is read here
    /// rather than silently falling back to the configured default on every
    /// non-euro shop.
    /// </summary>
    private static string Currency(JsonElement carrier, WooGuestOptions options)
    {
        var stated = JsonAccess.StrOf(carrier, "currency_code");
        return stated is { Length: 3 } ? stated.ToUpperInvariant() : options.Currency;
    }

    /// <summary>
    /// Two named amounts added, or null when neither is present. Absent is
    /// zero here rather than an error: WooCommerce omits or nulls a tax field
    /// on an order that has no tax, which is an ordinary answer.
    /// </summary>
    private static Money? Sum(
        JsonElement carrier, WooGuestOptions options, string currency, string owner, string net, string tax)
    {
        var value = MoneyReader.Optional(carrier, options.AmountUnit, currency, $"{owner}.{net}", net);
        var taxes = MoneyReader.Optional(carrier, options.AmountUnit, currency, $"{owner}.{tax}", tax);

        if (value is null && taxes is null) return null;

        return (value ?? new Money(0, currency)) + (taxes ?? new Money(0, currency));
    }
}
