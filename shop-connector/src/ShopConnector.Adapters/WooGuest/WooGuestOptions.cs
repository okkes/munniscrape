using Connector.Kit.Normalization;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.WooGuest;

/// <summary>
/// Everything about the WooCommerce Store API single-order lookup an
/// operator may need to correct without a release.
///
/// Read on 2026-07-28 from <c>woocommerce/woocommerce</c> @ <c>trunk</c> -
/// <c>StoreApi/Routes/V1/Order.php</c>,
/// <c>StoreApi/Utilities/OrderAuthorizationTrait.php</c>,
/// <c>StoreApi/Utilities/OrderController.php</c>,
/// <c>StoreApi/Schemas/V1/OrderSchema.php</c>,
/// <c>OrderItemSchema.php</c>, <c>AbstractSchema.php</c> and
/// <c>Formatters/MoneyFormatter.php</c>. The route, the two query
/// parameters, every response field, every error code and the money encoding
/// are therefore CONFIRMED. What is UNCONFIRMED is runtime: no request has
/// ever been made to a live shop, because that would mean asserting somebody
/// else's order key.
/// </summary>
public sealed record WooGuestOptions
{
    /// <summary>
    /// CONFIRMED: <c>Order::get_path_regex()</c> is
    /// <c>'/order/(?P&lt;id&gt;[\d]+)'</c> under the Store API v1 namespace.
    /// <c>{id}</c> is substituted; the rest is literal.
    /// </summary>
    public string OrderPathTemplate { get; init; } = "/wp-json/wc/store/v1/order/{id}";

    /// <summary>
    /// CONFIRMED: <c>is_authorized()</c> reads <c>$request-&gt;get_param('key')</c>
    /// and compares it with <c>hash_equals($order-&gt;get_order_key(), $key)</c>.
    /// This is the whole authorisation, so the value is a bearer capability
    /// and is declared secret in the manifest.
    /// </summary>
    public string KeyParam { get; init; } = "key";

    /// <summary>
    /// CONFIRMED: <c>$request-&gt;get_param('billing_email')</c>, compared with
    /// <c>strcasecmp</c> against the order's billing e-mail - case-insensitive,
    /// and with no grace period, deliberately.
    /// </summary>
    public string EmailParam { get; init; } = "billing_email";

    /// <summary>
    /// CONFIRMED, and the single most important declaration in this file.
    /// <c>MoneyFormatter::format()</c> returns
    /// <c>intval(round($value * 10 ** $decimals))</c> as a <b>string</b>, so
    /// every amount in a Store API payload is an integer number of minor
    /// units carried as text: <c>"1295"</c> is twelve euros ninety-five.
    ///
    /// Reading that as a decimal would multiply every order by a hundred.
    /// The unit is declared here, once, and never inferred from the value's
    /// shape - a four-digit string looks exactly like a plausible euro
    /// amount, which is precisely why guessing is banned.
    /// </summary>
    public MoneyUnit AmountUnit { get; init; } = MoneyUnit.Minor;

    /// <summary>
    /// The exponent <see cref="MoneyUnit.Minor"/> assumes, checked against
    /// the payload's own <c>currency_minor_unit</c> on every response.
    ///
    /// CONFIRMED that the field exists and that it is <c>wc_get_price_decimals()</c>,
    /// a per-shop admin setting - so a shop configured for whole euros
    /// reports 0 and a shop for a three-decimal currency reports 3. This
    /// service's <see cref="Money"/> is hundredths; anything else is a shape
    /// it cannot carry, and is refused by name rather than silently divided
    /// by the wrong power of ten.
    /// </summary>
    public int ExpectedMinorUnitExponent { get; init; } = 2;

    /// <summary>Used only where a payload states no <c>currency_code</c> of its own.</summary>
    public string Currency { get; init; } = "EUR";

    /// <summary>
    /// The order date, in the order of preference the parser tries.
    ///
    /// CONFIRMED and awkward: <c>OrderSchema::get_item_response()</c> returns
    /// no date at all - not <c>date_created</c>, not <c>date_paid</c>,
    /// nothing. The names below are what a shop with an extension or a newer
    /// schema might add, tried first because the shop's own answer beats
    /// anything a human typed; when none is present the date supplied with
    /// the order reference is used instead. See the manifest for why that
    /// field is required.
    /// </summary>
    public IReadOnlyList<string> OrderDateFields { get; init; } =
    [
        "date_created_gmt", "date_created", "date_paid_gmt", "date_paid",
    ];

    /// <summary>
    /// The country whose zone a bare order date is read in. A Store API
    /// order carries no zone, and a date without one lands on the wrong day
    /// twice a year at best.
    /// </summary>
    public string DefaultStoreCountry { get; init; } = "NL";

    /// <summary>
    /// The name on the synthetic shipping line. WooCommerce's order schema
    /// carries the shipping *total* but not the shipping *method*, so unlike
    /// Magento there is no provider word to prefer over this one.
    /// </summary>
    public string ShippingLineName { get; init; } = "Shipping";

    /// <summary>
    /// CONFIRMED: <c>OrderController::validate_order_key()</c> throws
    /// <c>woocommerce_rest_invalid_order</c> with 401 for a wrong key and
    /// <c>OrderAuthorizationTrait</c> throws the same code with 404 for an
    /// order id that does not exist. Two statuses, one code, one meaning for
    /// us: that reference does not open that order.
    /// </summary>
    public string InvalidOrderCode { get; init; } = "woocommerce_rest_invalid_order";

    /// <summary>
    /// CONFIRMED: thrown with 401 both for a missing billing e-mail and for
    /// one that does not match.
    /// </summary>
    public string InvalidEmailCode { get; init; } = "woocommerce_rest_invalid_billing_email";

    /// <summary>
    /// CONFIRMED: thrown with <b>403</b> when the order belongs to a
    /// registered customer. This is the reason the body is read before the
    /// status is mapped - a bare 403 reads as a bot wall, and this one is
    /// WooCommerce politely saying the order is not on the guest path at
    /// all.
    /// </summary>
    public string InvalidUserCode { get; init; } = "woocommerce_rest_invalid_user";

    /// <summary>
    /// The prefix every Store API error code carries. A body without it is
    /// not WooCommerce answering, so nothing in it may produce
    /// <c>invalid_credentials</c>.
    /// </summary>
    public string ErrorCodePrefix { get; init; } = "woocommerce_rest_";

    /// <summary>
    /// Statuses that mean the shop's edge is refusing us rather than
    /// answering. WooCommerce core rate-limits nothing on this route, so a
    /// 429 is Wordfence, Cloudflare or the host - a wall, not a queue.
    /// </summary>
    public IReadOnlySet<int> BlockedStatuses { get; init; } = BotWall.Statuses;

    /// <summary>An order payload is a few kilobytes. This is the ceiling, not the expectation.</summary>
    public int MaxResponseBytes { get; init; } = 1_048_576;

    /// <summary>
    /// Whether a shop may be addressed over plain http. Off, and it matters
    /// more here than anywhere else in the service: the order key travels in
    /// the query string, so cleartext hands the order to the path.
    /// </summary>
    public bool AllowPlainHttp { get; init; }

    /// <summary>
    /// Documentation only - nothing reads this. <c>www.wibra.nl</c> was
    /// fingerprinted as WooCommerce on 2026-07-28 (§9). No order has ever
    /// been requested from it.
    /// </summary>
    public IReadOnlyList<string> ExampleShops { get; init; } =
    [
        "https://www.wibra.nl",
    ];
}
