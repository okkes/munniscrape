using Connector.Kit.Normalization;

namespace ShopConnector.Adapters.Bol;

/// <summary>
/// Which shape the account's order list is served in.
///
/// This is the one genuine unknown about bol.com, and it cannot be settled
/// without an account: <c>/nl/nl/rnwy/account/bestellingen</c> is either
/// server-rendered HTML or a shell that fetches JSON from a backing endpoint.
/// Both are implemented; this option chooses, and every failure message names
/// the shape that was tried so the first live run diagnoses itself instead of
/// producing a puzzle.
/// </summary>
public enum BolOrdersShape
{
    /// <summary>
    /// The default, and the only one confirmed against a real account.
    ///
    /// bol's order overview is a shell: the first orders arrive in the page and
    /// every "Toon meer" is a GraphQL call. So this is not a guess between two
    /// possibilities any more - it is what the site does.
    /// </summary>
    GraphQl,

    /// <summary>Parse the account page's own markup. Superseded; see the README.</summary>
    Html,

    /// <summary>A plain JSON endpoint. Never found; kept because the parser is written and tested.</summary>
    Json,
}

/// <summary>
/// bol.com settings.
///
/// Read the login mechanics as confirmed and everything past the login as a
/// guess. The split is exact and worth stating, because bol is the provider
/// where a plausible-looking wrong answer was easiest to reach:
///
/// CONFIRMED (public pages fetched during research, 2026-07-28, no account):
///   * the login form lives at <c>https://login.bol.com/wsp/login</c> and
///     posts <c>j_username</c> / <c>j_password</c> - the Servlet/Spring
///     Security field names, i.e. a Java backend with a session cookie;
///   * it sets <c>JSESSIONID</c> and <c>XSC</c>, both Secure and HttpOnly;
///   * <c>GET https://www.bol.com/nl/nl/rnwy/account/bestellingen</c>
///     unauthenticated 302s to the login;
///   * no Akamai Bot Manager, no DataDome, no Cloudflare - the Akamai names
///     in bol's CSP are mPulse RUM, which is performance telemetry;
///   * a reCAPTCHA site key sits in the login page's config blob but no
///     reCAPTCHA script is emitted on first render, so it reads as
///     risk-based rather than always-on.
///
/// NOT CONFIRMED, and every one of them is a field below: whether the order
/// list is HTML or JSON, what any of its markup or field names are called,
/// how it paginates, whether line items are on the list at all, how long the
/// session lasts, and whether bol demands a one-time code on a new device.
///
/// And the trap this provider is famous for, restated so nobody re-finds it:
/// <c>api.bol.com/retailer</c> is a SELLER API. It authenticates with
/// <c>client_credentials</c>, which carries no user context at all, so
/// <c>GET /retailer/orders</c> returns the orders placed with a marketplace
/// seller - a different person's data. It is not a hard route to a shopper's
/// history, it is not a route to it. Nothing in this adapter touches it.
/// </summary>
public sealed record BolOptions
{
    // ---- confirmed: the login -----------------------------------------------

    /// <summary>
    /// Where the browser starts.
    ///
    /// Deliberately the protected page rather than <c>login.bol.com</c>
    /// directly: bol answers it with its own 302 chain
    /// (<c>/nl/account/login.html?redirectUrl=...</c> then
    /// <c>login.bol.com/wsp/login</c>), which is the flow a real shopper
    /// takes, sets whatever state that chain sets, and returns the browser to
    /// the orders page afterwards - which is also the cleanest "we are in"
    /// signal there is.
    /// </summary>
    public string LoginStartUrl { get; init; } = "https://www.bol.com/nl/nl/rnwy/account/bestellingen";

    /// <summary>
    /// CONFIRMED. The form itself - a Next.js app under <c>/wsp/</c> - and the
    /// second entry the login tries when <see cref="LoginStartUrl"/>'s
    /// redirect chain does not end at one. Costs a navigation on a page that
    /// has already gone wrong, and buys a login that survives bol changing its
    /// 302s without changing its form.
    /// </summary>
    public string LoginFormUrl { get; init; } = "https://login.bol.com/wsp/login";

    /// <summary>
    /// While the page's URL still contains this, the login has not completed.
    /// The cheapest signal there is, and the one that does not depend on a
    /// selector surviving a redesign.
    /// </summary>
    public string LoginHostMarker { get; init; } = "login.bol.com";

    /// <summary>
    /// CONFIRMED name. <c>j_username</c> is the Spring Security form-login
    /// field, read off the public login page; the rest are candidates behind
    /// it in case bol's Next.js front end renames the input it renders.
    /// </summary>
    public IReadOnlyList<string> UsernameSelectors { get; init; } =
    [
        "input[name='j_username']",
        "input#j_username",
        "input[autocomplete='username']",
        "input[type='email']",
    ];

    /// <summary>CONFIRMED name: <c>j_password</c>, the other half of the Spring form login.</summary>
    public IReadOnlyList<string> PasswordSelectors { get; init; } =
    [
        "input[name='j_password']",
        "input#j_password",
        "input[autocomplete='current-password']",
        "input[type='password']",
    ];

    /// <summary>
    /// UNCONFIRMED. The form has no <c>action</c> and is hydrated by React, so
    /// the button's own hooks were not readable from the server-rendered HTML.
    /// </summary>
    public IReadOnlyList<string> SubmitSelectors { get; init; } =
    [
        "button[type='submit']",
        "[data-test='login-submit']",
        "input[type='submit']",
    ];

    /// <summary>Best effort - a consent wall left standing covers the form.</summary>
    public IReadOnlyList<string> ConsentSelectors { get; init; } =
    [
        "#js-first-screen-accept-all-button",
        "button[data-test='consent-modal-confirm-btn']",
        "#onetrust-accept-btn-handler",
        "button[data-test='accept-all']",
    ];

    /// <summary>
    /// UNCONFIRMED, and the most dangerous list in this file after the money
    /// units.
    ///
    /// Only hooks that mean "this credential is wrong" belong here, because a
    /// match on one is the single path that reports
    /// <see cref="Connector.Kit.Errors.ErrorCode.InvalidCredentials"/> - which
    /// is never retried, and which sends a user to reset a password that was
    /// fine. Anything generic ("er is iets misgegaan") belongs in
    /// <see cref="BlockedNoticeSelectors"/> instead: Lidl's login turned out
    /// to answer an automated browser with exactly such a notice, and reading
    /// it as a bad password was a real bug.
    /// </summary>
    public IReadOnlyList<string> LoginErrorSelectors { get; init; } =
    [
        "[data-test='login-error']",
        "[data-test='invalid-credentials']",
    ];

    /// <summary>
    /// UNCONFIRMED and empty on purpose.
    ///
    /// Nothing has been observed, so nothing is claimed. If a live run finds
    /// bol answering an automated browser with a generic notice - the shape a
    /// risk-based reCAPTCHA verdict takes when there is no widget to show -
    /// its selector goes here and the outcome becomes
    /// <see cref="Connector.Kit.Errors.ErrorCode.BlockedByProvider"/>, which
    /// is the truth, rather than a credential error, which is not.
    /// </summary>
    public IReadOnlyList<string> BlockedNoticeSelectors { get; init; } = [];

    /// <summary>
    /// UNCONFIRMED. Research question 3 - "does bol force an e-mail or SMS
    /// code on a new device?" - is answered "assume yes", so the box is
    /// looked for and the code is relayed to the human. Never solved: it is in
    /// their mailbox, and only they can read it.
    /// </summary>
    public IReadOnlyList<string> VerificationCodeSelectors { get; init; } =
    [
        "input[autocomplete='one-time-code']",
        "input[name='verificationCode']",
        "input[name='otp']",
        "input[name='code']",
    ];

    /// <summary>UNCONFIRMED. The code screen's own button; an Enter keypress is the fallback.</summary>
    public IReadOnlyList<string> VerificationSubmitSelectors { get; init; } =
    [
        "[data-test='verify-code-submit']",
        "button[type='submit']",
    ];

    /// <summary>UNCONFIRMED. Markers that say the page itself sent the code to a mailbox.</summary>
    public IReadOnlyList<string> EmailDeliverySelectors { get; init; } =
    [
        "[data-test='verification-by-email']",
        "[data-delivery='email']",
    ];

    /// <summary>UNCONFIRMED. The same, for a code that went to a phone.</summary>
    public IReadOnlyList<string> SmsDeliverySelectors { get; init; } =
    [
        "[data-test='verification-by-sms']",
        "[data-delivery='sms']",
    ];

    /// <summary>
    /// The interactive widgets, identified by their iframes and containers.
    /// A reCAPTCHA site key is CONFIRMED present in the login page's config
    /// blob; whether it is ever rendered is not, so these exist to make the
    /// difference between "a wall stood here" and a job that hangs.
    ///
    /// A widget is not a picture. It wants drags and tile clicks and mints its
    /// token inside its own JavaScript, so no screenshot and no typed answer
    /// can pass one - which is why meeting one unattended is reported at once
    /// rather than relayed to nobody.
    /// </summary>
    public IReadOnlyList<string> InteractiveCaptchaSelectors { get; init; } =
    [
        "iframe[src*='recaptcha']",
        "iframe[src*='hcaptcha']",
        ".g-recaptcha",
        "[data-sitekey]",
    ];

    /// <summary>UNCONFIRMED. A plain image captcha is the one kind a relay can carry.</summary>
    public IReadOnlyList<string> ImageCaptchaSelectors { get; init; } =
    [
        "img[alt*='captcha' i]",
        "img[src*='captcha' i]",
    ];

    /// <summary>
    /// UNCONFIRMED. Where a typed captcha's answer goes - and, because a
    /// picture with nowhere to type it is as far out of reach as a widget,
    /// also what decides which of the two is standing in the way.
    ///
    /// Deliberately does not list the one-time-code box: relaying the 2FA step
    /// as a captcha would photograph it and call it a wall.
    /// </summary>
    public IReadOnlyList<string> CaptchaInputSelectors { get; init; } =
    [
        "input[name='captcha']",
        "input#captcha",
    ];

    // ---- the session cookies ------------------------------------------------

    /// <summary>Everything under bol.com; the host match below decides what is actually sent.</summary>
    public string CookieDomainSuffix { get; init; } = "bol.com";

    /// <summary>
    /// CONFIRMED names, UNCONFIRMED meaning. <c>JSESSIONID</c> and <c>XSC</c>
    /// are the two cookies the login sets; which of them www.bol.com actually
    /// authenticates on is not known, so the presence of any one of these is
    /// taken as "a session exists" and the fetch's own answer settles the rest.
    /// </summary>
    public IReadOnlyList<string> SessionCookieNames { get; init; } =
    [
        "JSESSIONID",
        "XSC",
        "shopping_session_id",
        "BUI",
    ];

    /// <summary>The host the orders request goes to; only cookies scoped to it are sent.</summary>
    public string OrdersHost { get; init; } = "www.bol.com";

    // ---- the orders themselves ----------------------------------------------

    /// <summary>
    /// Which of the two implemented shapes to read. HTML by default, because
    /// the page is known to exist and the JSON endpoint is not known to.
    /// </summary>
    public BolOrdersShape OrdersShape { get; init; } = BolOrdersShape.GraphQl;

    // ---- the GraphQL shape: CONFIRMED live, 2026-08-02 -----------------------

    /// <summary>CONFIRMED. The endpoint every "Toon meer" posts to.</summary>
    public string GraphQlUrl { get; init; } = "https://www.bol.com/api/graphql";

    /// <summary>CONFIRMED. The operation the order overview runs.</summary>
    public string OrdersOperationName { get; init; } = "OrdersOverviewClient";

    /// <summary>
    /// CONFIRMED. An automatic persisted query: bol sends this hash and no
    /// document at all.
    /// </summary>
    /// <remarks>
    /// It pins a specific compiled operation, so it is the value most likely
    /// to move when bol ships a front end. A hash bol no longer knows answers
    /// <c>PersistedQueryNotFound</c>, which this adapter reports as
    /// <c>provider_changed</c> naming this setting - an operator edit, not a
    /// release. There is no document to fall back to: the schema is not public
    /// and inventing one would be guessing at the shape all over again.
    /// </remarks>
    public string OrdersPersistedQueryHash { get; init; } =
        "sha256:d195253b815a6082cdee63bef6234513d5c0520c9f064e96fc1a9037d689add2";

    /// <summary>
    /// CONFIRMED. The cursor variable, and it really is a string: bol sends
    /// <c>"5"</c>, then <c>"10"</c> - an offset, not an opaque token.
    /// </summary>
    public string AfterVariable { get; init; } = "after";

    /// <summary>CONFIRMED. Five orders per call, which is what the button fetches.</summary>
    public int OrdersPageSize { get; init; } = 5;

    /// <summary>
    /// CONFIRMED, and the one value on bol worth being certain about.
    ///
    /// <c>listPrice.priceInclVat.amount</c> is a JSON number in euros -
    /// <c>31.75</c>, not <c>3175</c>. Observed rather than inferred, because
    /// this is the setting whose wrong value cannot be caught downstream: were
    /// it declared minor, every bol receipt would be a hundredth of the truth
    /// and would still reconcile, since the same number feeds both sides.
    /// </summary>
    public MoneyUnit GraphQlAmountUnit { get; init; } = MoneyUnit.MajorDecimal;

    /// <summary>
    /// CONFIRMED, and the header without which nothing else matters.
    ///
    /// bol's edge requires the operation to be named in a HEADER as well as in
    /// the body, and refuses the request before GraphQL ever sees it when it is
    /// missing:
    /// <code>
    /// HTTP 400 {"code":"InvalidRequest","message":"Request is invalid", ...}
    /// </code>
    /// Adding it alone turns that same request into a 200. Verified against
    /// three different operations, signed out, so it is a property of the
    /// endpoint rather than of any one account.
    /// </summary>
    public string OperationNameHeader { get; init; } = "bol-app-operation-name";

    /// <summary>
    /// CONFIRMED. bol double-submits its CSRF token: the value is in the
    /// <c>XSRF-TOKEN</c> cookie and has to be echoed in this header. Without
    /// it the endpoint answers <c>403 XSRF failed</c> - which is a distinct
    /// failure from the one above and was reached only after fixing it.
    /// </summary>
    public string XsrfHeader { get; init; } = "x-xsrf-token";

    /// <summary>The cookie the value above is read from.</summary>
    public string XsrfCookieName { get; init; } = "XSRF-TOKEN";

    /// <summary>
    /// CONFIRMED. What bol puts in the body of the 403 when the token is wrong
    /// or missing - the literal text <c>XSRF failed</c>.
    ///
    /// 403 is otherwise in <see cref="BlockStatuses"/>, so without this the
    /// answer reads as bol refusing us: the provider goes degraded and the user
    /// is told to wait for something that will never clear on its own. It is a
    /// session that needs renewing, and saying so is the difference between a
    /// dead end and a sign-in button.
    /// </summary>
    public string XsrfFailureMarker { get; init; } = "XSRF failed";

    /// <summary>
    /// CONFIRMED. What <c>data.me.__typename</c> says once the session is over.
    ///
    /// bol answers a signed-out request with <c>200</c> and a perfectly valid
    /// payload that simply has no <c>orders</c> field. Without this, the parser
    /// reads a missing field and reports <c>provider_changed</c> - telling the
    /// user bol has been rebuilt when all that happened is that their session
    /// expired, and sending an operator to look for a change that never was.
    /// </summary>
    public string AnonymousTypeName { get; init; } = "AnonymousCustomer";

    /// <summary>
    /// CONFIRMED path, UNCONFIRMED pagination. The account app is bol's
    /// "runway" (<c>rnwy</c>) front end and the path was observed directly;
    /// <c>page</c> is a guess, and research question 4 - "does order history
    /// paginate, and by what parameter?" - is exactly what a live run settles.
    /// A template rather than a builder so that changing it is an operator
    /// edit: <c>?pageNumber=</c>, <c>?offset=</c> or nothing at all are all
    /// one string away.
    /// </summary>
    public string HtmlOrdersUrlTemplate { get; init; } =
        "https://www.bol.com/nl/nl/rnwy/account/bestellingen?page={page}";

    /// <summary>
    /// UNCONFIRMED endpoint, in every part. No JSON order endpoint has been
    /// identified for the consumer site - this is the shape to switch to if a
    /// live run finds the page fetching its data, and the URL to correct when
    /// devtools names the real one.
    /// </summary>
    public string JsonOrdersUrlTemplate { get; init; } =
        "https://www.bol.com/nl/rnwy/api/account/orders?page={page}";

    /// <summary>
    /// UNCONFIRMED paths to the array of orders inside the JSON, tried in
    /// order. A list rather than a search: guessing which array in an unknown
    /// document holds orders is how a parser silently reports the wrong data.
    /// None matching is <c>provider_changed</c> naming every path tried.
    /// </summary>
    public IReadOnlyList<string> JsonOrderPaths { get; init; } =
    [
        "orders",
        "data.orders",
        "results",
        "items",
        "content",
    ];

    /// <summary>UNCONFIRMED field names for one order, most likely first.</summary>
    public IReadOnlyList<string> JsonOrderIdNames { get; init; } = ["orderId", "orderNumber", "id", "number"];

    public IReadOnlyList<string> JsonOrderDateNames { get; init; } =
        ["orderDate", "placedAt", "date", "createdAt", "dateTime"];

    public IReadOnlyList<string> JsonOrderTotalNames { get; init; } =
        ["totalAmount", "total", "orderTotal", "grandTotal", "amount"];

    public IReadOnlyList<string> JsonSellerNames { get; init; } = ["sellerName", "seller", "merchant", "shopName"];

    public IReadOnlyList<string> JsonItemArrayNames { get; init; } =
        ["orderItems", "items", "lines", "products", "orderLines"];

    public IReadOnlyList<string> JsonItemNameNames { get; init; } = ["title", "productTitle", "name", "description"];

    public IReadOnlyList<string> JsonItemQuantityNames { get; init; } = ["quantity", "amount", "count"];

    public IReadOnlyList<string> JsonItemUnitPriceNames { get; init; } = ["unitPrice", "pricePerUnit", "price"];

    public IReadOnlyList<string> JsonItemTotalNames { get; init; } = ["totalPrice", "lineTotal", "total", "subTotal"];

    public IReadOnlyList<string> JsonItemDiscountNames { get; init; } = ["discount", "discountAmount", "promotion"];

    public IReadOnlyList<string> JsonPaymentMethodNames { get; init; } = ["paymentMethod", "payment", "method"];

    /// <summary>UNCONFIRMED. The masked instrument, if bol states one at all.</summary>
    public IReadOnlyList<string> JsonPaymentMaskNames { get; init; } =
        ["maskedCardNumber", "cardNumber", "last4", "iban"];

    // ---- the HTML shape's hooks ---------------------------------------------

    /// <summary>
    /// UNCONFIRMED, all of it. Nobody on this project has seen this page.
    ///
    /// The selector subset is documented on <see cref="BolHtml"/>; the first
    /// candidate that matches anything wins, exactly as the Playwright lists
    /// elsewhere work, so repairing this adapter after a redesign is a
    /// configuration edit and not a release.
    /// </summary>
    public IReadOnlyList<string> OrderBlockSelectors { get; init; } =
    [
        "[data-test=order-container]",
        "[data-test=order]",
        "[data-test*=order-overview-item]",
        ".order-overview__item",
    ];

    /// <summary>
    /// The order number as an attribute of the block itself, which is where a
    /// front end most often puts it. Falls back to
    /// <see cref="OrderIdSelectors"/>.
    /// </summary>
    public IReadOnlyList<string> OrderIdAttributes { get; init; } =
        ["data-order-id", "data-ordernumber", "data-test-order-id", "id"];

    public IReadOnlyList<string> OrderIdSelectors { get; init; } =
        ["[data-test=order-number]", "[data-test=order-id]"];

    public IReadOnlyList<string> OrderDateSelectors { get; init; } =
        ["[data-test=order-date]", "[data-test=order-placed]", "time"];

    /// <summary>
    /// A machine-readable date beats a rendered one whenever the page offers
    /// it - the Dutch text has to be understood by hand, and that is the
    /// riskier of the two paths.
    /// </summary>
    public IReadOnlyList<string> DateAttributes { get; init; } = ["datetime", "data-date", "data-order-date"];

    public IReadOnlyList<string> OrderTotalSelectors { get; init; } =
        ["[data-test=order-total]", "[data-test=order-amount]", "[data-test=total-amount]"];

    public IReadOnlyList<string> OrderSellerSelectors { get; init; } =
        ["[data-test=order-seller]", "[data-test=seller-name]", "[data-test=sold-by]"];

    public IReadOnlyList<string> OrderPaymentSelectors { get; init; } =
        ["[data-test=payment-method]", "[data-test=order-payment]"];

    public IReadOnlyList<string> ItemBlockSelectors { get; init; } =
        ["[data-test=order-item]", "[data-test=order-line]", "[data-test=product-item]"];

    public IReadOnlyList<string> ItemNameSelectors { get; init; } =
        ["[data-test=item-title]", "[data-test=product-title]", "[data-test=title]"];

    public IReadOnlyList<string> ItemQuantitySelectors { get; init; } =
        ["[data-test=item-quantity]", "[data-test=quantity]"];

    public IReadOnlyList<string> ItemUnitPriceSelectors { get; init; } =
        ["[data-test=item-unit-price]", "[data-test=unit-price]"];

    public IReadOnlyList<string> ItemTotalSelectors { get; init; } =
        ["[data-test=item-total]", "[data-test=item-price]", "[data-test=line-total]"];

    public IReadOnlyList<string> ItemDiscountSelectors { get; init; } =
        ["[data-test=item-discount]", "[data-test=discount]"];

    /// <summary>
    /// How an account with no orders says so.
    ///
    /// Without this, "the page changed" and "this shopper has never ordered
    /// anything" are the same observation - zero blocks - and the honest
    /// answers to them are opposite: one pages an operator, the other is a
    /// perfectly good empty sync. UNCONFIRMED, and the substrings are checked
    /// against the raw page as well as the selectors.
    /// </summary>
    public IReadOnlyList<string> EmptyStateSelectors { get; init; } =
        ["[data-test=no-orders]", "[data-test=empty-state]", "[data-test=orders-empty]"];

    public IReadOnlyList<string> EmptyStateMarkers { get; init; } =
    [
        "Je hebt nog geen bestellingen",
        "geen bestellingen gevonden",
        "Nog geen bestellingen",
    ];

    // ---- interstitials ------------------------------------------------------

    /// <summary>
    /// A 200 that is really the login form. The session cookie died, which is
    /// <c>session_expired</c> - a new login job - and not a parse failure.
    /// <c>j_username</c> leads because it is unambiguous.
    /// </summary>
    public IReadOnlyList<string> LoginPageMarkers { get; init; } =
    [
        "j_username",
        "j_password",
        "login.bol.com/wsp",
    ];

    /// <summary>
    /// A 200 that is really a wall.
    ///
    /// bol runs no enterprise bot vendor today - confirmed from response
    /// headers, not assumed - so this list is defence against a change rather
    /// than a description of the present. It is checked BEFORE the login
    /// markers on purpose: mistaking a wall for a dead session starts a fresh
    /// login against the wall, which is the more expensive of the two wrong
    /// answers.
    /// </summary>
    public IReadOnlyList<string> BotWallMarkers { get; init; } =
    [
        "cf-browser-verification",
        "Attention Required! | Cloudflare",
        "__cf_chl",
        "datadome",
        "DataDome",
        "Reference&#32;&#35;",
        "AkamaiGHost",
        "Access Denied",
        "Pardon Our Interruption",
    ];

    /// <summary>
    /// Statuses that mean bol is refusing us rather than failing.
    ///
    /// 429 is here rather than under rate limiting deliberately: bol publishes
    /// no consumer quota, and a shopper's own account page answering 429 reads
    /// as a wall in front of an automated client. Either reading is honest;
    /// what neither may ever be is <c>invalid_credentials</c>, because telling
    /// somebody their password is wrong when the truth is bot protection sends
    /// them to reset a password that was fine and leaves the block
    /// undiagnosed. 502 and 504 belong here because an edge that tarpits a
    /// request reports it as an upstream failure.
    /// </summary>
    public IReadOnlySet<int> BlockStatuses { get; init; } = new HashSet<int> { 403, 429, 502, 504 };

    // ---- money and time -----------------------------------------------------

    /// <summary>
    /// CONFIRMED unit for the HTML shape: a Dutch page renders
    /// <c>&#8364;&#160;12,50</c> - decimal COMMA, dot for thousands - so the
    /// amount is a major-unit string.
    ///
    /// Declared, never sniffed. The platform has already had one provider's
    /// totals come out a hundredfold wrong from a unit that was assumed, and
    /// reconciliation cannot save it: item amounts wrong the same way still
    /// sum to a total wrong the same way, so a consistently wrong unit passes
    /// every check and corrupts every figure.
    /// </summary>
    public MoneyUnit HtmlAmountUnit { get; init; } = MoneyUnit.MajorString;

    /// <summary>
    /// UNCONFIRMED unit for the JSON shape, and the single most dangerous
    /// value in this file.
    ///
    /// <see cref="MoneyUnit.MajorString"/> is the least-bad default rather
    /// than a finding: it reads <c>"12,50"</c>, <c>"12.50"</c> and
    /// <c>12.50</c> all as twelve euros fifty, so it is right for every shape
    /// except one - a JSON that states minor units, where <c>1250</c> would
    /// become &#8364;1250,00. That failure is silent. It is the first thing a
    /// live run must check, and the check is trivial: compare one order's
    /// number against what the page shows a human.
    /// </summary>
    public MoneyUnit JsonAmountUnit { get; init; } = MoneyUnit.MajorString;

    /// <summary>
    /// Declared, never inferred from a symbol - "&#36;" is at least four
    /// currencies. bol.com/nl trades in euro.
    /// </summary>
    public string Currency { get; init; } = "EUR";

    // ---- patience -----------------------------------------------------------

    public int MaxPages { get; init; } = 20;

    public int SelectorTimeoutMs { get; init; } = 15_000;

    /// <summary>Short probes: these run on a page that is already the right one.</summary>
    public int ProbeMs { get; init; } = 500;

    public int LoginSettleSeconds { get; init; } = 120;

    /// <summary>How long each pass waits before looking at the page again.</summary>
    public int PollSeconds { get; init; } = 2;

    /// <summary>How often the cookie jar is inspected while waiting for the login to land.</summary>
    public int SessionPollMs { get; init; } = 500;

    /// <summary>How long the human has to fetch a one-time code and type it back.</summary>
    public int CodeChallengeSeconds { get; init; } = 300;

    /// <summary>
    /// UNCONFIRMED length, so nothing is stated.
    ///
    /// Null rather than a plausible 6: the consumer sizes its box and
    /// validates the answer against this, and a wrong length rejects a valid
    /// code before it is ever tried - burning a code the user has to wait for
    /// again. Set it once a live run has seen the screen.
    /// </summary>
    public int? CodeLength { get; init; }

    /// <summary>How long the human has to answer a relayed image captcha.</summary>
    public int ChallengeSeconds { get; init; } = 300;

    /// <summary>
    /// How long the human has to pass an interactive widget in the browser
    /// window we opened in front of them. Longer than a typed captcha:
    /// nobody is relaying anything, so the wait has to cover someone noticing.
    /// </summary>
    public int InteractiveCaptchaSeconds { get; init; } = 600;

    /// <summary>
    /// The client identity sent to bol. Honest, not a disguise: an ordinary
    /// browser fingerprint is what the page is built for, and nothing here
    /// claims to be anything it is not.
    /// </summary>
    public string UserAgent { get; init; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36";

    public string AcceptLanguage { get; init; } = "nl-NL,nl;q=0.9,en;q=0.8";
}
