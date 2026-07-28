using Connector.Kit.Normalization;

namespace ShopConnector.Adapters.Amazon;

/// <summary>
/// Everything about amazon.nl an operator may need to correct without a
/// release.
///
/// The split matters here more than anywhere else in this service, because
/// amazon.nl has no API: every route below is CONFIRMED from the source of
/// three independently maintained scrapers, and every selector below is
/// UNCONFIRMED, because Amazon's rendered DOM has no published contract and
/// none of those three projects carries a Dutch fixture. A page's markup is a
/// guess with a shelf life; the routes are not.
///
/// Sources read as source, not README: <c>alexdlaird/amazon-orders</c>
/// (<c>constants.py</c>, <c>session.py</c>, <c>selectors.py</c>),
/// <c>philipmulcahy/azad</c> (<c>url.ts</c>, <c>order_list_page.ts</c>) and
/// <c>eshaffer321/amazon-monarch-sync</c>.
/// </summary>
public sealed record AmazonOptions
{
    // ---- routes: CONFIRMED -------------------------------------------------

    /// <summary>CONFIRMED. The Dutch storefront.</summary>
    public string BaseUrl { get; init; } = "https://www.amazon.nl";

    /// <summary>
    /// CONFIRMED. The order list, driven by <c>timeFilter</c> and
    /// <c>startIndex</c>. An unauthenticated GET of this path was observed to
    /// redirect to <c>/ax/claim?arb=…</c> and return 200, which is what proves
    /// the .nl site shares the .com sign-in entry.
    /// </summary>
    public string OrdersPath { get; init; } = "/your-orders/orders";

    /// <summary>
    /// CONFIRMED. Per-order detail. Recorded rather than used: it is the
    /// heavier and more dynamically rendered of the two item sources, so the
    /// print invoice below is preferred. It is here so an operator whose
    /// invoice route stops working has the alternative written down instead of
    /// having to rediscover it.
    /// </summary>
    public string OrderDetailsPath { get; init; } = "/gp/your-account/order-details";

    /// <summary>
    /// CONFIRMED, and preferred for line items and totals: the print-friendly
    /// invoice is the least dynamically rendered page Amazon serves, so it is
    /// the one least likely to change shape underneath us.
    /// </summary>
    public string InvoicePath { get; init; } = "/gp/css/summary/print.html";

    /// <summary>CONFIRMED. Best-effort sign-out.</summary>
    public string SignOutPath { get; init; } = "/gp/flex/sign-out.html";

    /// <summary>CONFIRMED. <c>timeFilter=year-YYYY</c>.</summary>
    public string TimeFilterTemplate { get; init; } = "year-{year}";

    /// <summary>
    /// UNCONFIRMED. Amazon has historically paged the order list ten at a
    /// time. The adapter does not depend on the number being right - it stops
    /// on a page that yields nothing new - but a wrong value costs extra
    /// requests against a defended site.
    /// </summary>
    public int PageSize { get; init; } = 10;

    /// <summary>Ceiling on pages walked per year, so a loop cannot run away.</summary>
    public int MaxPagesPerYear { get; init; } = 20;

    /// <summary>Ceiling on years walked in one pass.</summary>
    public int MaxYears { get; init; } = 6;

    /// <summary>
    /// The language override, DELIBERATELY OFF, and the most consequential
    /// decision in this adapter.
    ///
    /// <c>azad</c> appends <c>&amp;language=en_GB</c> to the order list URL on
    /// every non-English storefront, precisely to dodge locale-dependent
    /// parsing, and amazon.nl is not even in its table so it falls through to
    /// <c>en_US</c>. Copying that would be reasonable if we had the problem it
    /// solves. We do not:
    ///
    /// <list type="number">
    /// <item>The landmine it dodges is a money parser that assumes '.' is the
    /// decimal point. Ours declares the unit per field and hands the digits to
    /// <see cref="MoneyParser"/>, which resolves comma decimals and mixed
    /// grouping natively; <see cref="AmazonMoney"/> adds the only case it
    /// cannot decide alone. Forcing English would not make our numbers safer,
    /// and English grouping has an ambiguity of its own ("€1,234").</item>
    /// <item>The parameter is UNVERIFIED on <c>.nl</c> specifically - nobody
    /// has confirmed it takes there. If it silently does not, an adapter built
    /// for English labels meets a Dutch page and fails the way the reference
    /// fails: quietly, with plausible output.</item>
    /// <item>Dutch is what a Dutch account is actually served, so Dutch is
    /// what the fixtures record and what the parser is tested against. Every
    /// label list here also carries its English spelling, so flipping this
    /// knob - or Amazon deciding to serve English on its own - changes nothing
    /// about whether the adapter works.</item>
    /// </list>
    ///
    /// Set it to <c>en_GB</c> only after a live run has confirmed both that it
    /// takes and what it does to the rendered number format.
    /// </summary>
    public string? LanguageOverride { get; init; }

    // ---- money: DECLARED, never sniffed ------------------------------------

    /// <summary>
    /// The order total on the list card. Amazon publishes no number anywhere -
    /// there is no API - so every amount arrives as a rendered currency STRING
    /// in MAJOR units: "€ 1.234,56". Hence <see cref="MoneyUnit.MajorString"/>,
    /// declared here rather than inferred from the value, because a heuristic
    /// that guesses wrong corrupts financial data silently.
    /// </summary>
    public MoneyUnit OrderTotalUnit { get; init; } = MoneyUnit.MajorString;

    /// <summary>An invoice line's price. Same rendering, its own declaration.</summary>
    public MoneyUnit ItemAmountUnit { get; init; } = MoneyUnit.MajorString;

    /// <summary>An invoice's shipping, tax and grand-total rows.</summary>
    public MoneyUnit InvoiceTotalUnit { get; init; } = MoneyUnit.MajorString;

    /// <summary>A promotion, coupon or gift-card row. Its own field, its own declaration.</summary>
    public MoneyUnit DiscountAmountUnit { get; init; } = MoneyUnit.MajorString;

    /// <summary>
    /// ISO-4217, declared. Never inferred from the rendered symbol: "$" is at
    /// least four different currencies, and amazon.nl bills in euros.
    /// </summary>
    public string Currency { get; init; } = "EUR";

    /// <summary>
    /// The currency tokens an amount on this storefront may carry. Anything
    /// else in an amount stops the parse rather than being stripped - see
    /// <see cref="AmazonMoney"/>.
    /// </summary>
    public IReadOnlyList<string> CurrencyTokens { get; init; } = ["EUR", "€"];

    // ---- order list page: UNCONFIRMED, most specific first -----------------

    /// <summary>
    /// UNCONFIRMED. One card per order on <c>/your-orders/orders</c>. The
    /// reference calls this <c>ORDER_HISTORY_ENTITY_SELECTOR</c> and it has
    /// been renamed by Amazon more than once, which is exactly why it is a
    /// candidate list an operator can extend.
    /// </summary>
    public IReadOnlyList<string> OrderCardSelectors { get; init; } =
    [
        "div.order-card.js-order-card",
        "div.js-order-card",
        "li.order-card",
        "div.order",
        "[data-order-id]",
    ];

    /// <summary>UNCONFIRMED. The order number inside a card.</summary>
    public IReadOnlyList<string> OrderIdSelectors { get; init; } =
    [
        ".yohtmlc-order-id span[dir='ltr']",
        ".yohtmlc-order-id .a-color-secondary.value",
        ".yohtmlc-order-id",
        "bdi[dir='ltr']",
    ];

    /// <summary>
    /// UNCONFIRMED. Attributes an order id also appears in, which survive a
    /// visual redesign that renames every class.
    /// </summary>
    public IReadOnlyList<string> OrderIdAttributes { get; init; } = ["data-order-id", "data-orderid"];

    /// <summary>UNCONFIRMED. "Besteld op" / "Order placed".</summary>
    public IReadOnlyList<string> OrderDateSelectors { get; init; } =
    [
        ".yohtmlc-order-date .a-color-secondary.value",
        ".order-date-invoice-item .a-color-secondary.value",
        ".a-column.a-span3 .a-color-secondary.value",
    ];

    /// <summary>
    /// UNCONFIRMED. The stated order total. The reference reads
    /// <c>div.yohtmlc-order-total span.value</c> and, for Whole Foods orders,
    /// <c>#wfm-grand-total-amount</c>.
    /// </summary>
    public IReadOnlyList<string> OrderTotalSelectors { get; init; } =
    [
        ".yohtmlc-order-total .a-color-secondary.value",
        ".yohtmlc-order-total span.value",
        "div.yohtmlc-order-total",
        "#wfm-grand-total-amount",
    ];

    /// <summary>UNCONFIRMED. The link that carries <c>orderID=</c>.</summary>
    public IReadOnlyList<string> OrderLinkSelectors { get; init; } =
    [
        "a[href*='orderID=']",
        "a[href*='order-details']",
    ];

    /// <summary>
    /// UNCONFIRMED. The pagination block, looked for BEFORE the next link.
    ///
    /// The distinction is what stops the walk fetching one page too many on
    /// every single year. When this block is present its "next" control is
    /// authoritative; when it is absent - Amazon has more than one order-list
    /// layout - the walk falls back to its own freshness guard rather than
    /// assuming the history ended.
    /// </summary>
    public IReadOnlyList<string> PaginationSelectors { get; init; } =
    [
        "ul.a-pagination",
        ".a-pagination",
        "div.pagination",
    ];

    /// <summary>UNCONFIRMED. The "next" control inside the pagination block.</summary>
    public IReadOnlyList<string> NextPageSelectors { get; init; } =
    [
        "li.a-last a",
        ".a-last a",
        "a.s-pagination-next",
    ];

    // ---- print invoice: UNCONFIRMED ---------------------------------------

    /// <summary>UNCONFIRMED. One row per purchased line on the print invoice.</summary>
    public IReadOnlyList<string> InvoiceItemRowSelectors { get; init; } =
    [
        "table.product-view tr.item-row",
        "tr.item-row",
        "table[data-item-table] tr",
    ];

    /// <summary>
    /// UNCONFIRMED. The cell carrying "1 van: Titel" / "1 of: Title". The
    /// price cell is excluded by identity rather than by a <c>:not()</c>
    /// selector, so this list stays inside the subset of CSS an operator can
    /// edit without learning which pseudo-classes are supported.
    /// </summary>
    public IReadOnlyList<string> InvoiceItemNameSelectors { get; init; } =
    [
        "td.item-title",
        "td[colspan]",
        "td",
    ];

    /// <summary>UNCONFIRMED. The cell carrying the unit price.</summary>
    public IReadOnlyList<string> InvoiceItemPriceSelectors { get; init; } =
    [
        "td.item-price",
        "td[align='right']",
    ];

    /// <summary>
    /// UNCONFIRMED, and optional: Amazon's print invoice normally states a
    /// UNIT price and a quantity rather than a line total. When a line total
    /// is present it is preferred, because a stated figure always beats a
    /// computed one.
    /// </summary>
    public IReadOnlyList<string> InvoiceItemTotalSelectors { get; init; } = ["td.item-total"];

    /// <summary>
    /// UNCONFIRMED. Where an item cell stops being the product's name and
    /// starts being Amazon's commentary on it - the seller, the condition, the
    /// returns window. Cutting there keeps a receipt line readable and, more
    /// importantly, keeps it STABLE: a name that silently grows a new
    /// parenthetical changes the receipt's content hash and de-duplicates
    /// against nothing.
    /// </summary>
    public IReadOnlyList<string> ItemNameStopMarkers { get; init; } =
    [
        "verkocht door",
        "sold by",
        "leverancier:",
        "staat:",
        "condition:",
        "terugsturen kan tot",
    ];

    /// <summary>UNCONFIRMED. The label/value rows in the totals block.</summary>
    public IReadOnlyList<string> InvoiceTotalRowSelectors { get; init; } =
    [
        "table.totals tr",
        "#od-subtotals .a-row",
        "table[data-totals] tr",
    ];

    /// <summary>UNCONFIRMED. Where a card or account tail is stated.</summary>
    public IReadOnlyList<string> InvoicePaymentSelectors { get; init; } =
    [
        ".payment-information",
        "#payment-information",
        "table.payment-method",
    ];

    // ---- invoice labels: Dutch first, English beside it --------------------
    //
    // Matched in the priority order the parser applies, which is why
    // "totaal vóór btw" and "subtotaal" are separate categories: "subtotaal"
    // contains "totaal" and "totaal vóór btw" contains "btw", so a naive
    // contains-match against a single list assigns both of them to the wrong
    // row and the receipt reconciles against a number that is not its total.

    public IReadOnlyList<string> SubtotalLabels { get; init; } =
        ["artikelen (subtotaal)", "subtotaal", "items (subtotal)", "subtotal"];

    public IReadOnlyList<string> TotalBeforeTaxLabels { get; init; } =
        ["totaal vóór btw", "totaal voor btw", "total before tax", "totaal excl. btw"];

    public IReadOnlyList<string> ShippingLabels { get; init; } =
        ["verzendkosten", "verzending & verwerking", "verzending", "shipping & handling", "shipping"];

    public IReadOnlyList<string> TaxLabels { get; init; } =
        ["btw", "belasting", "estimated tax", "vat", "tax"];

    public IReadOnlyList<string> PromotionLabels { get; init; } =
        ["actiekorting", "promotiekorting", "korting", "promotion applied", "promotion", "coupon"];

    public IReadOnlyList<string> GiftCardLabels { get; init; } =
        ["cadeaubon", "tegoedbon", "gift card amount", "gift card"];

    public IReadOnlyList<string> GrandTotalLabels { get; init; } =
        ["eindtotaal", "ordertotaal", "totaalbedrag", "totaal", "grand total", "order total", "total"];

    /// <summary>UNCONFIRMED. "eindigend op 1234" / "ending in 1234".</summary>
    public IReadOnlyList<string> CardTailMarkers { get; init; } =
        ["eindigend op", "eindigt op", "ending in", "ending with"];

    /// <summary>
    /// UNCONFIRMED. Whether the prices on a line are quoted INCLUDING BTW, in
    /// which case the invoice's tax row decomposes the total rather than
    /// adding to it and must not be emitted as a line of its own.
    ///
    /// True by default because a Dutch consumer price is quoted VAT-inclusive
    /// by law, so a receipt that added the tax row on top would double-count
    /// the VAT on every single order - and it would still look like a receipt.
    /// If a live invoice turns out to break the total down tax-exclusively,
    /// this is the one field to flip, and the symptom will be every receipt
    /// arriving with <c>reconciled: false</c> and short by exactly the BTW.
    /// </summary>
    public bool TaxIsIncludedInItemPrices { get; init; } = true;

    /// <summary>
    /// UNCONFIRMED. What an order list with nothing on it says. Without this
    /// an empty history and a list whose card selector has expired look
    /// identical, and the second one must not be reported as "you have no
    /// orders".
    /// </summary>
    public IReadOnlyList<string> EmptyHistoryMarkers { get; init; } =
    [
        "geen bestellingen",
        "Je hebt geen bestellingen geplaatst",
        "niets besteld",
        "no orders",
        "You have not placed any orders",
    ];

    // ---- sign-in page: UNCONFIRMED ----------------------------------------

    public IReadOnlyList<string> UsernameSelectors { get; init; } =
    [
        "input#ap_email",
        "input#ap_email_login",
        "input[name='email']",
        "input[type='email']",
        "input[name='username']",
    ];

    public IReadOnlyList<string> PasswordSelectors { get; init; } =
    [
        "input#ap_password",
        "input[name='password']",
        "input[type='password']",
    ];

    /// <summary>UNCONFIRMED. The button on the e-mail screen of the two-step form.</summary>
    public IReadOnlyList<string> ContinueSelectors { get; init; } =
    [
        "input#continue",
        "#continue input[type='submit']",
        "input[type='submit'][aria-labelledby*='continue']",
    ];

    /// <summary>UNCONFIRMED. The button on the password screen.</summary>
    public IReadOnlyList<string> SubmitSelectors { get; init; } =
    [
        "input#signInSubmit",
        "#signInSubmit input[type='submit']",
        "input[type='submit']",
        "button[type='submit']",
    ];

    /// <summary>
    /// UNCONFIRMED, and deliberately narrow: only phrasings that state a
    /// CREDENTIAL failure. <c>invalid_credentials</c> is never retried by
    /// anything, so a false one is permanent for that session and sends
    /// somebody to reset a password that was fine. A generic Amazon error box
    /// also carries "There was a problem", which is not evidence of anything.
    /// </summary>
    public IReadOnlyList<string> LoginErrorSelectors { get; init; } =
    [
        "#auth-password-invalid-password-alert",
        "#auth-email-invalid-claim-alert",
        "#auth-error-message-box .a-alert-content",
    ];

    /// <summary>UNCONFIRMED. The one-time code box.</summary>
    public IReadOnlyList<string> OtpSelectors { get; init; } =
    [
        "input#auth-mfa-otpcode",
        "input[name='otpCode']",
        "input[autocomplete='one-time-code']",
    ];

    public IReadOnlyList<string> OtpSubmitSelectors { get; init; } =
    [
        "input#auth-signin-button",
        "#auth-signin-button input[type='submit']",
        "input[type='submit']",
    ];

    /// <summary>
    /// UNCONFIRMED. How the page says the code was delivered, resolved by the
    /// visible words rather than by markup: an id can be renamed by a
    /// redesign, but the sentence telling somebody where to look for their
    /// code cannot change without the page changing meaning.
    ///
    /// Probed app first, then sms, then e-mail, and null when none of them
    /// matches. The e-mail candidates come last because an OTP page often
    /// prints the account's own address in its header, and matching that would
    /// tell somebody to watch a mailbox while their code sits in an app.
    /// </summary>
    public IReadOnlyList<string> OtpAppSelectors { get; init; } =
    [
        ":text('authenticator')",
        ":text('verificatie-app')",
        ":text('authenticatie-app')",
    ];

    public IReadOnlyList<string> OtpSmsSelectors { get; init; } =
    [
        ":text('tekstbericht')",
        ":text('sms')",
        ":text('text message')",
    ];

    public IReadOnlyList<string> OtpEmailSelectors { get; init; } =
    [
        ":text('e-mail gestuurd')",
        ":text('sent to your email')",
        ":text('mailbox')",
    ];

    /// <summary>
    /// UNCONFIRMED. Amazon showing that it has stopped talking to us for a
    /// reason that is not a password: a locked account, an "approve this
    /// sign-in" wall we cannot reach. Blocked, never invalid_credentials.
    /// </summary>
    public IReadOnlyList<string> RefusalSelectors { get; init; } =
    [
        "#auth-account-locked-alert",
        "#auth-suspended-alert",
    ];

    /// <summary>UNCONFIRMED. A consent wall left standing covers the form.</summary>
    public IReadOnlyList<string> ConsentSelectors { get; init; } =
    [
        "#sp-cc-accept",
        "input#sp-cc-accept",
        "button[name='accept']",
    ];

    // ---- bot protection ----------------------------------------------------

    /// <summary>
    /// UNCONFIRMED. The walls that cannot be relayed: AWS WAF's JS challenge,
    /// and the ACIC challenge page at <c>/ax/aaut/verify/ap/challenge</c> whose
    /// container the reference names <c>#aa-challenge-page-captcha-container</c>.
    ///
    /// These are widgets, not pictures. They want drags and tile clicks and
    /// mint their token inside their own JavaScript, so no screenshot and no
    /// typed answer can pass one however faithfully the page is photographed -
    /// which is why they route to somebody sitting at the browser or to
    /// <c>blocked_by_provider</c>, and never to a solving service. The
    /// reference ships integrations for three paid solvers. We ship none.
    /// </summary>
    public IReadOnlyList<string> InteractiveCaptchaSelectors { get; init; } =
    [
        "#aa-challenge-page-captcha-container",
        "form[action*='/ax/aaut/verify']",
        "#challenge-container",
        "#captcha-container",
        "iframe[src*='captcha']",
        "iframe[src*='hcaptcha']",
        "iframe[src*='recaptcha']",
    ];

    /// <summary>
    /// UNCONFIRMED. The legacy OCR captcha - a picture and a box - which is
    /// the one kind a relay can actually carry to the account's owner.
    /// </summary>
    public IReadOnlyList<string> ImageCaptchaSelectors { get; init; } =
    [
        "#auth-captcha-image",
        "img[src*='captcha']",
        "form[action*='validateCaptcha'] img",
    ];

    /// <summary>UNCONFIRMED. Where a typed captcha's answer goes.</summary>
    public IReadOnlyList<string> CaptchaInputSelectors { get; init; } =
    [
        "input#auth-captcha-guess",
        "input#captchacharacters",
        "input[name='field-keywords']",
    ];

    /// <summary>
    /// UNCONFIRMED. Markers in the raw body that name a challenge no selector
    /// caught. <c>window.gokuProps</c> is how the reference detects an AWS WAF
    /// JS challenge; the token it yields is the <c>aws-waf-token</c> cookie.
    /// </summary>
    public IReadOnlyList<string> ChallengeBodyMarkers { get; init; } =
    [
        "gokuProps",
        "aws-waf-token",
        "/errors/validateCaptcha",
        "opfcaptcha",
        "Enter the characters you see below",
        "Voer de tekens in die je hieronder ziet",
    ];

    /// <summary>
    /// UNCONFIRMED. A hard refusal rather than a challenge: no puzzle is
    /// offered and there is nothing for a human to do. Reported as
    /// <c>blocked_by_provider</c> even on a 200, because a bot wall that
    /// answers 200 is still a bot wall. Cloudflare and Akamai are listed for
    /// completeness - Amazon runs its own - so that a CDN change in front of
    /// the storefront is diagnosed rather than parsed.
    /// </summary>
    public IReadOnlyList<string> BlockedBodyMarkers { get; init; } =
    [
        "To discuss automated access to Amazon data please contact",
        "Sorry, we just need to make sure you're not a robot",
        "Request blocked",
        "Access Denied",
        "cf-browser-verification",
        "Attention Required! | Cloudflare",
        "Reference #18.",
    ];

    /// <summary>
    /// UNCONFIRMED. URL fragments that mean we are looking at the sign-in
    /// chain rather than at data. During a fetch that is a dead session.
    /// </summary>
    public IReadOnlyList<string> SignInUrlMarkers { get; init; } = ["/ap/signin", "/ax/claim", "/ap/cvf"];

    /// <summary>UNCONFIRMED. URL fragments that mean the browser is on the order history.</summary>
    public IReadOnlyList<string> SignedInUrlMarkers { get; init; } =
        ["/your-orders/orders", "/gp/css/order-history", "/gp/your-account/order-history"];

    /// <summary>
    /// CONFIRMED from <c>constants.py</c>:
    /// <c>COOKIES_SET_WHEN_AUTHENTICATED = ["x-main"]</c>. A login that leaves
    /// no such cookie has not signed anybody in, whatever the page looks like.
    /// </summary>
    public IReadOnlyList<string> AuthCookieNames { get; init; } = ["x-main"];

    /// <summary>
    /// Statuses that mean amazon.nl is REFUSING us rather than failing.
    ///
    /// 429 is in this set deliberately. The platform's default reading of 429
    /// is <c>rate_limited</c>, which is retriable and tells the consumer to
    /// wait a bit; from a bot wall it is not a queueing hint, it is a refusal,
    /// and retrying into it is how a session gets escalated to a hard block.
    /// 503 is here for the same reason: Amazon's interstitials answer 503 far
    /// more often than they answer 403.
    ///
    /// 500 is deliberately NOT here. A genuine server error is
    /// <c>provider_unavailable</c> and retriable; calling it a block would
    /// tell a user to stop using a connection that is merely having a bad
    /// minute.
    /// </summary>
    public IReadOnlySet<int> BlockStatuses { get; init; } =
        new HashSet<int> { 403, 429, 502, 503, 504, 511 };

    // ---- patience ----------------------------------------------------------

    public int SelectorTimeoutMs { get; init; } = 15_000;

    /// <summary>
    /// Short: on Amazon's two-screen sign-in this probe always misses on the
    /// first screen, and that wait is pure latency on every single login.
    /// </summary>
    public int PasswordProbeMs { get; init; } = 1_500;

    /// <summary>Short probes: the settle loop runs them on every pass.</summary>
    public int ProbeMs { get; init; } = 500;

    /// <summary>How long the login may take to resolve into an order page, an error or a wall.</summary>
    public int LoginSettleSeconds { get; init; } = 240;

    public int RedirectPollSeconds { get; init; } = 5;

    /// <summary>How long the human has to answer a relayed image captcha.</summary>
    public int ChallengeSeconds { get; init; } = 300;

    /// <summary>
    /// How long the human has to pass a WAF or ACIC widget in the browser
    /// window in front of them. Longer than a typed captcha on purpose:
    /// nobody is relaying anything, so the wait has to cover someone noticing
    /// and walking back to their desk.
    /// </summary>
    public int InteractiveCaptchaSeconds { get; init; } = 600;

    /// <summary>How long the human has to read a one-time code and type it back.</summary>
    public int CodeChallengeSeconds { get; init; } = 300;

    /// <summary>UNCONFIRMED. Amazon's OTP is six digits.</summary>
    public int CodeLength { get; init; } = 6;
}
