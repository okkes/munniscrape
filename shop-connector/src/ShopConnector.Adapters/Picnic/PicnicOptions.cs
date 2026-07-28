using Connector.Kit.Normalization;

namespace ShopConnector.Adapters.Picnic;

/// <summary>
/// Everything about Picnic an operator may need to correct without a release.
///
/// Unusually for this service, almost all of it is CONFIRMED. The values below
/// are read from <c>MRVDH/picnic-api</c> (last logic commit 2026-07-02:
/// <c>lib/http-client.js</c>, <c>lib/domains/auth/service.js</c>,
/// <c>lib/domains/delivery/service.js</c>, <c>lib/domains/cart/types.d.ts</c>)
/// and cross-checked against a recorded <c>GET /deliveries/{id}</c> response.
/// The handful of genuinely unknown values are marked UNCONFIRMED and nothing
/// else is.
///
/// Deliberately NOT copied from <c>MikeBrink/python-picnic-api</c>, which is
/// what most blog posts and the Home Assistant integration point at: it was
/// last changed in 2023, contains no 2FA handling at all, and still sends
/// <c>okhttp/3.9.0</c> with the 2020 agent string.
/// </summary>
public sealed record PicnicOptions
{
    /// <summary>
    /// CONFIRMED: <c>https://storefront-prod.{countryCode}.picnicinternational.com/api/{apiVersion}</c>.
    /// The country segment is a host label, so it is lower-cased on
    /// substitution; the manifest's own option list is upper-case because that
    /// is how the consumer renders and stores it.
    /// </summary>
    public string BaseUrlTemplate { get; init; } =
        "https://storefront-prod.{country}.picnicinternational.com/api/{version}";

    /// <summary>
    /// CONFIRMED: the API version segment. It is part of the path, not a
    /// header - <c>/api/15/deliveries/summary</c> - and it is the single value
    /// most likely to move under us, which is exactly why it lives here.
    /// </summary>
    public string ApiVersion { get; init; } = "15";

    /// <summary>CONFIRMED: <c>client_id: 30100</c> in the login body, and the first field of the agent header.</summary>
    public int ClientId { get; init; } = 30_100;

    /// <summary>CONFIRMED: the reference's User-Agent. Sent because the API is a mobile API, not to disguise anything.</summary>
    public string UserAgent { get; init; } = "okhttp/4.9.0";

    /// <summary>
    /// CONFIRMED: <c>application/json; charset=UTF-8</c>, with that exact
    /// casing. Set explicitly rather than left to <see cref="System.Net.Http.StringContent"/>,
    /// which would render <c>charset=utf-8</c>.
    /// </summary>
    public string ContentType { get; init; } = "application/json; charset=UTF-8";

    /// <summary>
    /// CONFIRMED: <c>x-picnic-agent</c>. Only the 2FA calls (and the live
    /// position/scenario calls this adapter never makes) require it.
    /// </summary>
    public string PicnicAgentHeader { get; init; } = "30100;1.236.1-15553;";

    /// <summary>
    /// UNCONFIRMED, and deliberately off.
    ///
    /// The reference sends <c>x-picnic-agent</c>/<c>x-picnic-did</c> on the 2FA
    /// calls only, and its type definitions note that some cart fields appear
    /// "when picnic headers are sent" - while stating that the PRICE/PROMO
    /// decorators this adapter reads discounts from are present on delivery
    /// order lines regardless. If a live run comes back with no discounts and
    /// a stated <c>total_savings</c> that reconciliation then flags, turning
    /// this on is the first thing to try.
    /// </summary>
    public bool AlwaysSendPicnicHeaders { get; init; }

    // ---- paths: all CONFIRMED from the reference ---------------------------

    public string LoginPath { get; init; } = "/user/login";

    public string TwoFactorGeneratePath { get; init; } = "/user/2fa/generate";

    public string TwoFactorVerifyPath { get; init; } = "/user/2fa/verify";

    public string LogoutPath { get; init; } = "/user/logout";

    /// <summary>CONFIRMED: POST, body = an array of status filters. Returns deliveries WITHOUT line items.</summary>
    public string DeliverySummaryPath { get; init; } = "/deliveries/summary";

    /// <summary>CONFIRMED: GET. The only place line items exist, hence one call per delivery.</summary>
    public string DeliveryDetailPathTemplate { get; init; } = "/deliveries/{id}";

    /// <summary>
    /// CONFIRMED status vocabulary (<c>DeliveryStatus</c>): CURRENT, COMPLETED,
    /// CANCELLED. Only completed deliveries are receipts - a CURRENT one has
    /// not been charged and a CANCELLED one never will be. Sent as the POST
    /// body; an empty list asks for everything.
    /// </summary>
    public IReadOnlyList<string> StatusFilter { get; init; } = ["COMPLETED"];

    /// <summary>CONFIRMED: the only channel the reference generates a code on.</summary>
    public string TwoFactorChannel { get; init; } = "SMS";

    // ---- money: CONFIRMED cents -------------------------------------------

    /// <summary>
    /// CONFIRMED minor units (cents).
    ///
    /// Not inferred, and not a hopeful default. <c>OrderArticle.price</c> is
    /// documented in the reference's own types as "Base per-unit price in
    /// cents"; <c>total_savings</c> as "Total savings from bundle discounts in
    /// cents"; <c>membership_savings</c> as "(in cents)". A recorded
    /// <c>/deliveries/{id}</c> response then proves it arithmetically:
    /// the line <c>display_price</c> values sum to 4865, <c>total_savings</c>
    /// is 88, <c>total_deposit</c> is 35, and <c>total_price</c> is exactly
    /// 4812 = 4865 - 88 + 35. Those numbers are only self-consistent as cents.
    ///
    /// This is the field that has already been got wrong once on this
    /// platform. If it is ever changed, change it because a capture said so.
    /// </summary>
    public MoneyUnit TotalUnit { get; init; } = MoneyUnit.Minor;

    /// <summary>CONFIRMED minor units. An order line's <c>display_price</c>. See <see cref="TotalUnit"/>.</summary>
    public MoneyUnit LineUnit { get; init; } = MoneyUnit.Minor;

    /// <summary>CONFIRMED minor units. An article's <c>price</c> - the documented "in cents" field.</summary>
    public MoneyUnit ArticleUnit { get; init; } = MoneyUnit.Minor;

    /// <summary>CONFIRMED minor units. A PRICE decorator's <c>display_price</c>, which is the discounted line total.</summary>
    public MoneyUnit DiscountUnit { get; init; } = MoneyUnit.Minor;

    /// <summary>
    /// Picnic operates in three euro countries, so this never varies today.
    /// Stated rather than assumed because a currency inferred from a symbol is
    /// how a connector starts reporting dollars.
    /// </summary>
    public string Currency { get; init; } = "EUR";

    // ---- shape --------------------------------------------------------------

    /// <summary>
    /// The name given to the synthetic deposit line.
    ///
    /// Picnic charges <c>total_deposit</c> (statiegeld, bag fees) on top of the
    /// line items, so without a line for it the items cannot sum to the stated
    /// total and every single receipt would be flagged. This is a data label
    /// taken from Picnic's own field name, not user-facing copy - the consumer
    /// renders item names as data, and translating a product list is not a
    /// thing it does.
    /// </summary>
    public string DepositItemName { get; init; } = "deposit";

    /// <summary>
    /// UNCONFIRMED. The error codes in a Picnic error body
    /// (<c>{"error":{"code":…}}</c>) that mean the credentials were wrong
    /// rather than anything else.
    ///
    /// This list is the ONLY route to <c>invalid_credentials</c> in this
    /// adapter, and it is deliberately short. Telling somebody their password
    /// is wrong when the truth is a block or an outage sends them to reset a
    /// password that was fine, and a credential error is never retried - so
    /// the mistake is permanent for that connect attempt too.
    /// </summary>
    public IReadOnlyList<string> InvalidCredentialCodes { get; init; } =
        ["AUTH_INVALID_CRED", "AUTH_INVALID_CREDENTIALS", "AUTH_ERROR"];

    /// <summary>UNCONFIRMED. The length of the SMS code, for the consumer's input mask.</summary>
    public int OtpLength { get; init; } = 6;

    /// <summary>How long the human has to read a text message and type the code back.</summary>
    public int OtpChallengeSeconds { get; init; } = 300;

    /// <summary>
    /// Bytes behind <c>x-picnic-did</c>. CONFIRMED format: the reference's
    /// default is 16 hex characters, so eight bytes. The value itself is minted
    /// per connection and carried - never the reference's hard-coded literal,
    /// which would hand every user of this connector the same device identity.
    /// </summary>
    public int DeviceIdBytes { get; init; } = 8;

    /// <summary>
    /// CONFIRMED: <c>Accept-Language</c> follows the country, and the country
    /// is also the host label. Written out rather than derived so that adding a
    /// country is a visible edit instead of a silent lower-case.
    /// </summary>
    public IReadOnlyDictionary<string, string> LanguageByCountry { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["NL"] = "nl",
            ["DE"] = "de",
            ["FR"] = "fr",
        };
}
