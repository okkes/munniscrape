using Connector.Kit.Normalization;

namespace ShopConnector.Adapters.Coolblue;

/// <summary>
/// Which credential the consumer order call is expected to carry.
///
/// UNCONFIRMED, and one of the two facts a devtools capture must settle. The
/// discovery document advertises <c>openid:customerid</c> as a scope, which
/// hints at a resource server that keys off a customer id and would therefore
/// take the access token as a bearer - but a hint is not evidence, and the
/// site may only ever use its <c>Coolblue-Session</c> cookie. Both routes are
/// implemented so that whichever the capture shows is a configuration change
/// rather than a release.
/// </summary>
public enum CoolblueOrderCredential
{
    /// <summary>The OIDC access token, as <c>Authorization: Bearer</c>.</summary>
    Bearer,

    /// <summary>The session cookies out of the browser state the login sealed away.</summary>
    Cookie,
}

/// <summary>
/// Everything about Coolblue an operator may need to correct without a
/// release - and, unusually for this service, a clean split between a half
/// that is confirmed and a half that is not.
///
/// CONFIRMED (from Coolblue's own public OIDC discovery document and the live
/// unauthenticated redirect chain, both read 2026-07-28 and recorded in
/// docs/research/retailers-priority-b.md §2): the issuer, the authorize and
/// token endpoints, the client id, the redirect URI, the scope list, S256
/// PKCE, and the <c>refresh_token</c> grant. That is the whole front door and
/// it is the best-behaved of any retailer researched - no bot wall, no
/// captcha on the login form.
///
/// UNCONFIRMED: literally everything about how a consumer's orders are
/// actually fetched. There is zero prior art - no public repo, no blog
/// capture, no Home Assistant integration - <c>/graphql</c> is a 404 and
/// <c>api.coolblue.nl</c> does not resolve. So the fetch here is a documented
/// GUESS behind a safety catch (<see cref="OrdersEndpointConfirmed"/>) that is
/// off, and the adapter refuses to call it until a human has captured the real
/// thing. Guessing an endpoint is exactly what produced the wrong
/// <c>ReceiptPaths</c> in <c>JumboOptions</c>.
/// </summary>
public sealed record CoolblueOptions
{
    // ---- identity: CONFIRMED from the discovery document -------------------

    /// <summary>
    /// CONFIRMED: <c>authorization_endpoint</c>.
    ///
    /// Note the asymmetry with <see cref="TokenUrl"/> and do not "fix" it.
    /// Coolblue authorizes under <c>/connect/</c> (IdentityServer's own
    /// convention) and takes tokens under <c>/oauth/</c>. Both spellings came
    /// out of the same discovery document; assuming <c>/connect/token</c>
    /// because the authorize call lives under <c>/connect/</c> is a 404 that
    /// costs a whole login.
    /// </summary>
    public string AuthorizeUrl { get; init; } = "https://accounts.coolblue.nl/connect/authorize";

    /// <summary>CONFIRMED: <c>token_endpoint</c>. Under <c>/oauth/</c>, not <c>/connect/</c>.</summary>
    public string TokenUrl { get; init; } = "https://accounts.coolblue.nl/oauth/token";

    /// <summary>
    /// CONFIRMED: <c>userinfo_endpoint</c>. Standard OIDC, so with the
    /// <c>email</c> and <c>profile</c> scopes it answers with something a user
    /// can recognise their own connection by. Called best-effort - see
    /// <see cref="FetchUserInfo"/>.
    /// </summary>
    public string UserInfoUrl { get; init; } = "https://accounts.coolblue.nl/oauth/userinfo";

    /// <summary>CONFIRMED from the live redirect chain: <c>client_id=Webshop</c>.</summary>
    public string ClientId { get; init; } = "Webshop";

    /// <summary>
    /// UNCONFIRMED, and null on purpose.
    ///
    /// <c>Webshop</c> is a browser client that redirects to an https URI and
    /// sends S256 PKCE, which is precisely the public-client shape - a public
    /// client has no secret and PKCE is what replaces one. Null therefore sends
    /// the client id in the form body and no Basic header. If a live exchange
    /// is refused with <c>invalid_client</c>, set this and the adapter switches
    /// to HTTP Basic instead.
    /// </summary>
    public string? ClientSecret { get; init; }

    /// <summary>
    /// CONFIRMED from the live redirect chain. Unlike Albert Heijn's
    /// <c>appie://</c> and Lidl's <c>com.lidlplus.app://</c>, this is an
    /// ordinary https URL a browser can and does navigate to - so the redirect
    /// watcher catches a real navigation here rather than a blocked one.
    /// </summary>
    public string RedirectUri { get; init; } = "https://www.coolblue.nl/inloggen/oidc";

    /// <summary>
    /// CONFIRMED: the seven scopes the site's own authorize request carries,
    /// space-separated here because the live URL <c>+</c>-separates them.
    ///
    /// <c>offline_access</c> is the one that matters: it is what yields the
    /// refresh token, and the refresh token is the entire reason this provider
    /// can be unattended.
    /// </summary>
    public string Scope { get; init; } =
        "openid email profile offline_access openid:customerid openid:identityroleid ucp:scopes:checkout_session";

    /// <summary>CONFIRMED: the site sends <c>ui_locales=nl</c>.</summary>
    public string UiLocales { get; init; } = "nl";

    /// <summary>
    /// Whether to spend one extra call on <see cref="UserInfoUrl"/> so the
    /// connection carries the account's own name rather than only the brand.
    /// Best effort in every sense: a failure here is swallowed and the login
    /// still succeeds, because a display label is not worth failing a login
    /// that has already produced tokens.
    /// </summary>
    public bool FetchUserInfo { get; init; } = true;

    /// <summary>
    /// Whether to seal the browser's cookies and local storage into the bundle
    /// alongside the tokens.
    ///
    /// On by default and deliberately so. Which of the two credentials the
    /// order call actually needs is unknown, and this is the one that cannot
    /// be recovered later without a second login: the tokens can always be
    /// refreshed, but a session cookie not captured at login time is gone.
    /// Costs nothing and removes a whole re-login from the day the capture
    /// lands.
    /// </summary>
    public bool CaptureStorageState { get; init; } = true;

    /// <summary>Cookies from this domain (and its subdomains) travel with a cookie-mode fetch.</summary>
    public string CookieDomainSuffix { get; init; } = "coolblue.nl";

    // ---- orders: UNCONFIRMED, and disarmed ---------------------------------

    /// <summary>
    /// The safety catch, and the most important line in this file.
    ///
    /// False means the adapter refuses to fetch anything at all, before a
    /// session is touched and before a single packet leaves the machine, with
    /// a message naming exactly what to capture. Set it to true only once a
    /// human has watched the real call in devtools and filled in the values
    /// below from what they saw - never to "see if the guess works".
    ///
    /// A guess that half-works is worse than no fetch: it produces
    /// plausible-looking receipts that nobody reviews.
    /// </summary>
    public bool OrdersEndpointConfirmed { get; init; }

    /// <summary>
    /// UNCONFIRMED - an arbitrary placeholder, shaped like the account page's
    /// own path so it reads as the guess it is. It is never called while
    /// <see cref="OrdersEndpointConfirmed"/> is false.
    ///
    /// The human-facing page is <c>/mijn-coolblue-account/bestellingen</c>
    /// (CONFIRMED, it 301/307/302s to the OIDC login when unauthenticated).
    /// Whether that page is server-rendered HTML or hydrated from JSON is the
    /// first thing the capture settles; this adapter can only do the JSON case.
    /// </summary>
    public string OrdersUrl { get; init; } = "https://www.coolblue.nl/mijn-coolblue-account/api/orders";

    /// <summary>UNCONFIRMED. A list call ought to be a GET; the capture says.</summary>
    public string OrdersMethod { get; init; } = "GET";

    /// <summary>
    /// The human-facing page, quoted in the capture instructions and used by
    /// nothing else. CONFIRMED.
    /// </summary>
    public string OrdersPageUrl { get; init; } = "https://www.coolblue.nl/mijn-coolblue-account/bestellingen";

    /// <summary>UNCONFIRMED. See <see cref="CoolblueOrderCredential"/>.</summary>
    public CoolblueOrderCredential Credential { get; init; } = CoolblueOrderCredential.Bearer;

    /// <summary>
    /// UNCONFIRMED. Any extra request headers the captured call carries -
    /// an <c>x-csrf-token</c>, an api version, an <c>x-requested-with</c>.
    ///
    /// A <c>_csrfSecret</c> cookie and a <c>csrf</c> hidden field are CONFIRMED
    /// on the login form, so Coolblue does use CSRF tokens; whether a read
    /// needs one is unknown. Empty rather than guessed.
    /// </summary>
    public IReadOnlyDictionary<string, string> ExtraHeaders { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// UNCONFIRMED response paths to the order array, tried in order, dotted.
    ///
    /// A candidate list rather than a search: hunting for "the array that looks
    /// like orders" in an unknown document is how a parser silently reports the
    /// wrong data. A bare array response is also accepted. None matching is
    /// <c>provider_changed</c> with the tried paths named.
    /// </summary>
    public IReadOnlyList<string> OrderPaths { get; init; } =
    [
        "data.orders",
        "orders",
        "data.items",
        "items",
        "results",
        "data",
    ];

    /// <summary>UNCONFIRMED property names for an order's own id. Required; a row without one is refused.</summary>
    public IReadOnlyList<string> IdNames { get; init; } =
        ["orderId", "orderNumber", "id", "number", "reference"];

    /// <summary>UNCONFIRMED property names for when the order was placed.</summary>
    public IReadOnlyList<string> DateNames { get; init; } =
        ["orderDate", "placedAt", "createdAt", "date", "dateTime", "orderedAt"];

    /// <summary>UNCONFIRMED property names for the order's stated total.</summary>
    public IReadOnlyList<string> TotalNames { get; init; } =
        ["totalAmount", "totalPrice", "total", "amount", "grandTotal"];

    /// <summary>UNCONFIRMED property names for the line-item collection.</summary>
    public IReadOnlyList<string> ItemsNames { get; init; } =
        ["orderLines", "lines", "products", "items", "orderItems"];

    /// <summary>UNCONFIRMED property names for a line's description.</summary>
    public IReadOnlyList<string> ItemNameNames { get; init; } =
        ["name", "productName", "title", "description"];

    /// <summary>UNCONFIRMED property names for a line's quantity.</summary>
    public IReadOnlyList<string> ItemQuantityNames { get; init; } =
        ["quantity", "amount", "count", "units"];

    /// <summary>UNCONFIRMED property names for a line's own extended total.</summary>
    public IReadOnlyList<string> ItemTotalNames { get; init; } =
        ["totalPrice", "lineTotal", "totalAmount", "subtotal", "amount"];

    /// <summary>
    /// UNCONFIRMED property names for a line's unit price.
    ///
    /// Only ever the provider's own figure. Dividing a total by a quantity
    /// produces a number that looks authoritative and is wrong the moment a
    /// line carries a bundle discount.
    /// </summary>
    public IReadOnlyList<string> ItemUnitPriceNames { get; init; } =
        ["unitPrice", "price", "pricePerUnit", "itemPrice"];

    /// <summary>UNCONFIRMED property names for a discount collection on a line.</summary>
    public IReadOnlyList<string> ItemDiscountNames { get; init; } =
        ["discounts", "promotions", "reductions"];

    /// <summary>UNCONFIRMED property names for a discount collection on the order itself.</summary>
    public IReadOnlyList<string> OrderDiscountNames { get; init; } =
        ["discounts", "promotions", "vouchers"];

    /// <summary>UNCONFIRMED property names for a discount's own amount.</summary>
    public IReadOnlyList<string> DiscountAmountNames { get; init; } =
        ["amount", "discountAmount", "value", "total"];

    /// <summary>UNCONFIRMED property names for a discount's label.</summary>
    public IReadOnlyList<string> DiscountLabelNames { get; init; } =
        ["name", "label", "description", "title"];

    /// <summary>UNCONFIRMED property names for the payment block or collection.</summary>
    public IReadOnlyList<string> PaymentNames { get; init; } =
        ["payments", "payment", "paymentMethods", "paymentMethod"];

    /// <summary>UNCONFIRMED property names for a payment's method word.</summary>
    public IReadOnlyList<string> PaymentMethodNames { get; init; } =
        ["method", "type", "paymentMethod", "name"];

    /// <summary>UNCONFIRMED property names for a masked instrument, for the last four digits.</summary>
    public IReadOnlyList<string> PaymentMaskedNames { get; init; } =
        ["maskedCardNumber", "cardNumber", "last4", "accountNumber", "iban"];

    /// <summary>
    /// UNCONFIRMED property names for a pickup store, where an order was
    /// collected at one rather than delivered.
    /// </summary>
    public IReadOnlyList<string> StoreNames { get; init; } =
        ["storeName", "store", "pickupPoint", "pickupLocation"];

    // ---- money: DECLARED, never sniffed, and null until it is known --------

    /// <summary>
    /// The unit an order's stated total is written in. Null, because Coolblue
    /// has never answered us and nobody knows.
    ///
    /// Null is not a default that quietly means "euros" - the adapter refuses
    /// to fetch at all until this is declared, even with
    /// <see cref="OrdersEndpointConfirmed"/> set. That refusal exists because
    /// reconciliation cannot save us here: it catches an inconsistent PAIR, not
    /// a consistently wrong one, so a total and its lines both misread as
    /// minor units reconcile perfectly at a hundredth of the real money. That
    /// exact mistake is live in this repo tonight on another provider. Read the
    /// unit off the capture and write it down.
    /// </summary>
    public MoneyUnit? TotalUnit { get; init; }

    /// <summary>
    /// The unit a line amount is written in. Its own declaration, because an
    /// API that states a total in one unit and its lines in another is not
    /// hypothetical - see the note on <see cref="TotalUnit"/>. Required only
    /// when a caller asks for items.
    /// </summary>
    public MoneyUnit? ItemUnit { get; init; }

    /// <summary>
    /// The unit a discount amount is written in. Falls back to
    /// <see cref="ItemUnit"/> when unset, because a discount that sits inside a
    /// line is overwhelmingly written the way that line is - but it is stated
    /// separately so the capture can disagree.
    /// </summary>
    public MoneyUnit? DiscountUnit { get; init; }

    /// <summary>
    /// Fallback ISO-4217 when the payload states none. Never inferred from a
    /// symbol: "€" is unambiguous but "$" is at least four currencies, and a
    /// fallback that only works for one of them is a trap.
    /// </summary>
    public string Currency { get; init; } = "EUR";

    /// <summary>Rows per call, if the captured endpoint turns out to take a size at all.</summary>
    public int PageSize { get; init; } = 50;

    // ---- login page --------------------------------------------------------

    /// <summary>
    /// The identifier box. <c>username</c> is the name the login form's own
    /// markup uses (OBSERVED in the 2026-07-28 capture of the authorize
    /// redirect's landing page, alongside hidden <c>csrf</c>, <c>view</c> and
    /// <c>view_context</c> fields), so it leads the list; the rest are
    /// candidates behind it.
    /// </summary>
    public IReadOnlyList<string> UsernameSelectors { get; init; } =
    [
        "input[name='username']",
        "input#username",
        "input[autocomplete='username']",
        "input[type='email']",
        "input[name='email']",
    ];

    /// <summary>The password box. <c>password</c> is OBSERVED on the same form.</summary>
    public IReadOnlyList<string> PasswordSelectors { get; init; } =
    [
        "input[name='password']",
        "input#password",
        "input[autocomplete='current-password']",
        "input[type='password']",
    ];

    /// <summary>
    /// UNCONFIRMED. The capture recorded the form's inputs but not its button,
    /// so this is the generic ordering plus the Dutch copy - the words next to
    /// a login button cannot change without the page changing meaning, which
    /// is more than can be said for its classes.
    /// </summary>
    public IReadOnlyList<string> SubmitSelectors { get; init; } =
    [
        "button[type='submit']",
        "input[type='submit']",
        "button:has-text('Inloggen')",
        "button:has-text('Log in')",
    ];

    /// <summary>
    /// UNCONFIRMED, and deliberately narrow: only phrasings that state a
    /// CREDENTIAL failure.
    ///
    /// A generic error box would also match a consent banner or an outage
    /// notice, and <c>invalid_credentials</c> is never retried by anything - so
    /// a false positive is permanent for that session and sends the user to
    /// reset a password that was fine. This is the only path in the adapter
    /// allowed to report one.
    /// </summary>
    public IReadOnlyList<string> LoginErrorSelectors { get; init; } =
    [
        ":text('e-mailadres of wachtwoord')",
        ":text('combinatie van e-mailadres en wachtwoord')",
        ":text('wachtwoord is onjuist')",
        ":text('inloggegevens kloppen niet')",
    ];

    /// <summary>UNCONFIRMED. A consent wall left standing covers the form.</summary>
    public IReadOnlyList<string> ConsentSelectors { get; init; } =
    [
        "button#accept-cookies",
        "button[data-testid='consent-accept-all']",
        "button:has-text('Accepteren')",
        "button:has-text('Akkoord')",
    ];

    /// <summary>
    /// UNCONFIRMED, and OBSERVED ABSENT: the 2026-07-28 check found no captcha
    /// asset on Coolblue's login form and no bot-management cookie anywhere on
    /// the site (<c>server: CloudFront</c>, no Akamai, no DataDome, no
    /// Cloudflare BM).
    ///
    /// The probe stays anyway, and costs one short look per settle pass. A
    /// widget appearing later is a thing the adapter must be able to NAME - the
    /// alternative is a job that waits out its whole budget and then reports a
    /// shape change, which is the failure that has already happened twice on
    /// this platform.
    /// </summary>
    public IReadOnlyList<string> InteractiveCaptchaSelectors { get; init; } =
    [
        "iframe[src*='hcaptcha']",
        "iframe[src*='recaptcha']",
        "[data-hcaptcha-widget-id]",
        ".h-captcha",
        ".g-recaptcha",
    ];

    /// <summary>UNCONFIRMED. A plain image captcha is the one kind a relay can carry.</summary>
    public IReadOnlyList<string> ImageCaptchaSelectors { get; init; } =
    [
        "img[alt*='captcha' i]",
        "img[src*='captcha' i]",
        "img[id*='captcha' i]",
        ".captcha img",
    ];

    /// <summary>
    /// UNCONFIRMED. Where a typed captcha's answer goes - and, because a
    /// picture with nowhere to type it is no more answerable than a widget,
    /// also what decides which of the two is standing in the way.
    /// </summary>
    public IReadOnlyList<string> CaptchaInputSelectors { get; init; } =
    [
        "input[name='captcha']",
        "input#captcha",
    ];

    /// <summary>
    /// UNCONFIRMED. Text an edge challenge or an interstitial puts in a body
    /// that was supposed to be JSON. Matched case-insensitively.
    ///
    /// Coolblue fronts on CloudFront and no wall has been seen, but an HTML
    /// body where JSON was promised has exactly two readings - a wall or a
    /// bounce to the login - and calling either of them a parse failure buries
    /// the diagnosis.
    /// </summary>
    public IReadOnlyList<string> BlockPageMarkers { get; init; } =
    [
        "captcha",
        "access denied",
        "request blocked",
        "cloudfront",
        "cf-ray",
        "akamai",
        "datadome",
    ];

    /// <summary>
    /// UNCONFIRMED. Text that says an HTML body is Coolblue's login page
    /// rather than a wall - which is <c>session_expired</c>, not a block, and
    /// the two want opposite things from the user.
    /// </summary>
    public IReadOnlyList<string> LoginPageMarkers { get; init; } =
    [
        "accounts.coolblue.nl",
        "/inloggen",
        "name=\"csrf\"",
    ];

    public int SelectorTimeoutMs { get; init; } = 15_000;

    /// <summary>Short probes: the settle loop runs them on every pass.</summary>
    public int ProbeMs { get; init; } = 500;

    /// <summary>How long the login may take to resolve into a code, an error or a wall.</summary>
    public int LoginSettleSeconds { get; init; } = 180;

    /// <summary>How long each settle pass waits on the redirect before looking at the page again.</summary>
    public int RedirectPollSeconds { get; init; } = 5;

    /// <summary>How long the human has to answer a relayed image captcha.</summary>
    public int ChallengeSeconds { get; init; } = 300;

    /// <summary>
    /// How long the human has to pass an interactive widget in the browser
    /// window we opened in front of them. Longer than a typed captcha: nobody
    /// is relaying anything, so the wait has to cover someone noticing.
    /// </summary>
    public int InteractiveCaptchaSeconds { get; init; } = 600;
}
