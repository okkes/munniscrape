using Connector.Kit.Normalization;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.MagentoGuest;

/// <summary>
/// Everything about the Magento guest-order lookup an operator may need to
/// correct without a release.
///
/// Unusually for this service, almost nothing here is a guess. The endpoint,
/// the two queries, every field name, the shape of <c>Money</c>, the format
/// of <c>order_date</c> and the two error categories were read out of
/// <c>magento/magento2</c> @ <c>2.4-develop</c> on 2026-07-28 - see
/// docs/research/retailers-platforms.md §3.1-§3.2 and the file references on
/// each member. What is UNCONFIRMED is runtime behaviour: no guestOrder query
/// has ever been executed against a live shop, because doing so means
/// asserting somebody else's order number.
/// </summary>
public sealed record MagentoGuestOptions
{
    /// <summary>
    /// CONFIRMED (§3): a single endpoint, always this path, core module, on
    /// by default. Appended to the shop base URL the user supplied, so a
    /// shop living under a path prefix still resolves.
    /// </summary>
    public string GraphQlPath { get; init; } = "/graphql";

    /// <summary>
    /// The selection set both queries share.
    ///
    /// CONFIRMED from <c>app/code/Magento/SalesGraphQl/etc/schema.graphqls</c>:
    /// every name below is declared on <c>CustomerOrder</c>,
    /// <c>OrderTotal</c>, <c>OrderItemInterface</c> or <c>Discount</c>.
    /// Deprecated fields (<c>created_at</c>, <c>grand_total: Float</c>,
    /// <c>order_number</c>, <c>increment_id</c>, <c>subtotal</c>) are
    /// deliberately not selected.
    ///
    /// Note what is *not* here: <c>items { discounts }</c>. Magento states an
    /// order's discounts twice - once per item and once on the total - and
    /// the totals arithmetic (see <see cref="MagentoGuestOrderParser"/>)
    /// already subtracts the order-level set. Reading both would subtract
    /// every promotion twice and produce a receipt that fails its own
    /// arithmetic for a reason nobody could see.
    ///
    /// <c>subtotal_excl_tax</c>, <c>subtotal_incl_tax</c> and
    /// <c>grand_total_excl_tax</c> arrived in 2.4.2; a shop older than that
    /// will answer with a validation error naming the field, which is why
    /// this is an option and not a literal.
    /// </summary>
    public string OrderFields { get; init; } =
        """
        number
        order_date
        status
        email
        shipping_method
        payment_methods { name type }
        total {
          grand_total { value currency }
          subtotal_incl_tax { value currency }
          subtotal_excl_tax { value currency }
          total_tax { value currency }
          total_shipping { value currency }
          shipping_handling { amount_including_tax { value currency } }
          discounts { amount { value currency } label }
        }
        items {
          product_name
          product_sku
          quantity_ordered
          product_sale_price { value currency }
        }
        """;

    /// <summary>
    /// CONFIRMED, schema.graphqls line 6:
    /// <c>guestOrder(input: GuestOrderInformationInput!): CustomerOrder!</c>
    /// with <c>input GuestOrderInformationInput { number: String! email:
    /// String! lastname: String! }</c>. <c>{fields}</c> is replaced with
    /// <see cref="OrderFields"/>.
    /// </summary>
    public string GuestOrderQuery { get; init; } =
        """
        query GuestOrder($number: String!, $email: String!, $lastname: String!) {
          guestOrder(input: {number: $number, email: $email, lastname: $lastname}) {
        {fields}
          }
        }
        """;

    /// <summary>
    /// CONFIRMED, schema.graphqls line 7 and
    /// <c>SalesGraphQl/Model/Resolver/GuestOrder.php</c>: the token resolves
    /// through the same resolver, which decrypts it into exactly
    /// <c>[number, email, lastname]</c>. The token is therefore a
    /// convenience, not extra reach - which is why the triple is the primary
    /// reference and this is the alternative.
    /// </summary>
    public string GuestOrderByTokenQuery { get; init; } =
        """
        query GuestOrderByToken($token: String!) {
          guestOrderByToken(input: {token: $token}) {
        {fields}
          }
        }
        """;

    /// <summary>CONFIRMED: the response field for <see cref="GuestOrderQuery"/>.</summary>
    public string GuestOrderField { get; init; } = "guestOrder";

    /// <summary>CONFIRMED: the response field for <see cref="GuestOrderByTokenQuery"/>.</summary>
    public string GuestOrderByTokenField { get; init; } = "guestOrderByToken";

    /// <summary>
    /// CONFIRMED: <c>type Money { value: Float, currency: CurrencyEnum }</c>.
    /// Every amount in this API - order totals, item prices, discounts - is
    /// that one type, so one declaration covers them all. Declared, never
    /// sniffed: 1234 here means one thousand two hundred and thirty four
    /// euros, and a heuristic that read it as cents would divide a real
    /// order by a hundred in silence.
    /// </summary>
    public MoneyUnit MoneyValueUnit { get; init; } = MoneyUnit.MajorDecimal;

    /// <summary>
    /// Used only when a <c>Money</c> arrives without its own
    /// <c>currency</c>. Magento states one on every amount, so this is the
    /// fallback and not the source of truth.
    /// </summary>
    public string Currency { get; init; } = "EUR";

    /// <summary>
    /// CONFIRMED, and a trap worth stating in full.
    /// <c>SalesGraphQl/Model/Formatter/Order.php</c> renders
    /// <c>order_date</c> as
    /// <c>$this-&gt;timezone-&gt;date($order-&gt;getCreatedAt())-&gt;format(DateTime::DATETIME_SLASH_PHP_FORMAT)</c>,
    /// and <c>DATETIME_SLASH_PHP_FORMAT</c> is <c>'d/m/Y H:i:s'</c>
    /// (lib/internal/Magento/Framework/Stdlib/DateTime.php line 28).
    ///
    /// Day first. A general-purpose parser reading "07/09/2026" under an
    /// invariant culture returns 9 July for an order placed on 7 September -
    /// a two-month error, silent, on every order placed before the 13th of a
    /// month. So the format is matched exactly rather than guessed, and
    /// anything that does not match falls through to the ISO reader for the
    /// older shops that still emit <c>getCreatedAt()</c> raw.
    /// </summary>
    public IReadOnlyList<string> OrderDateFormats { get; init; } =
    [
        "d/M/yyyy H:mm:ss",
        "d/M/yyyy HH:mm:ss",
        "d/M/yyyy",
    ];

    /// <summary>
    /// The zone <c>order_date</c>'s wall clock belongs to.
    ///
    /// CONFIRMED that the formatter converts UTC into the shop's own
    /// configured timezone and then drops the offset. Which zone that is, is
    /// a per-shop admin setting we cannot read, so the connection may state
    /// it as <c>store_country</c> and this is the default when it does not.
    /// Getting it wrong costs an hour or two, never a day; getting the
    /// day-first format wrong costs two months, which is why that one is
    /// matched exactly and this one has a default.
    /// </summary>
    public string DefaultStoreCountry { get; init; } = "NL";

    /// <summary>
    /// Whether <c>product_sale_price</c> includes tax, or null to work it
    /// out from the order's own subtotals.
    ///
    /// CONFIRMED from <c>SalesGraphQl/Model/OrderItem/DataProvider.php</c>:
    /// the field is <c>displaySalesPriceInclTax($storeId) ?
    /// getPriceInclTax() : getPrice()</c> - a per-shop admin setting, stated
    /// nowhere in the response. Dutch shops usually display prices including
    /// tax; German and B2B ones often do not.
    ///
    /// Null means: sum the item lines and compare against
    /// <c>subtotal_incl_tax</c> and <c>subtotal_excl_tax</c>, both of which
    /// Magento states. That is not sniffing a money unit - the unit is
    /// declared above - it is spending a redundancy the provider handed us to
    /// answer a question it refused to answer directly, and it is checked
    /// again by reconciliation afterwards.
    /// </summary>
    public bool? ItemPricesIncludeTax { get; init; }

    /// <summary>
    /// The name on the synthetic shipping line, when the order names no
    /// shipping method of its own. Data rather than copy - it lands in
    /// <c>ReceiptItem.Name</c> next to product names, not in a message - but
    /// it is an option so a Dutch deployment can say "Verzendkosten".
    /// </summary>
    public string ShippingLineName { get; init; } = "Shipping";

    /// <summary>
    /// The name on the synthetic tax line, emitted only where the item
    /// prices are net of tax and the order states a tax amount.
    /// </summary>
    public string TaxLineName { get; init; } = "Tax";

    /// <summary>
    /// CONFIRMED from <c>Resolver/GuestOrder.php</c>: the resolver throws
    /// <c>GraphQlNoSuchEntityException("We couldn't locate an order with the
    /// information provided.")</c> for a wrong number, a wrong lastname, a
    /// wrong e-mail or an undecryptable token - one indistinguishable answer
    /// for all four, which is good security and means we cannot tell a user
    /// which part they mistyped.
    /// </summary>
    public string NotFoundCategory { get; init; } = "graphql-no-such-entity";

    /// <summary>
    /// CONFIRMED from the same file: <c>GraphQlAuthorizationException("Please
    /// login to view the order.")</c> when <c>$order-&gt;getCustomerId()</c>
    /// is set. An order placed while signed in to the shop is not reachable
    /// through the guest path at all, ever - see the notes on the manifest.
    /// </summary>
    public string AuthorizationCategory { get; init; } = "graphql-authorization";

    /// <summary>
    /// Statuses that mean the shop's edge is refusing us. Magento core
    /// rate-limits nothing on <c>/graphql</c>, so a 429 is a bolted-on wall.
    /// </summary>
    public IReadOnlySet<int> BlockedStatuses { get; init; } = BotWall.Statuses;

    /// <summary>An order payload is a few kilobytes. This is the ceiling, not the expectation.</summary>
    public int MaxResponseBytes { get; init; } = 1_048_576;

    /// <summary>
    /// Whether a shop may be addressed over plain http. Off: the reference
    /// travels in the request body here, but the same switch guards both
    /// platform adapters and WooCommerce puts a bearer key in the query
    /// string.
    /// </summary>
    public bool AllowPlainHttp { get; init; }

    /// <summary>
    /// Documentation only - nothing reads this. Two Dutch shops confirmed
    /// live on 2026-07-28 to answer <c>/graphql</c> unauthenticated
    /// (§3.3): <c>www.dille-kamille.nl</c> and <c>www.chasin.nl</c>. Neither
    /// has ever been sent a guestOrder query.
    /// </summary>
    public IReadOnlyList<string> ExampleShops { get; init; } =
    [
        "https://www.dille-kamille.nl",
        "https://www.chasin.nl",
    ];
}
