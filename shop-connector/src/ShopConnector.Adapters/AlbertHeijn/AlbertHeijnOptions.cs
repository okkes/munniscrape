using Connector.Kit.Normalization;

namespace ShopConnector.Adapters.AlbertHeijn;

/// <summary>
/// Everything about Albert Heijn that an operator may need to correct
/// without a release.
///
/// The client id, the client version, the user agent, the header block, the
/// auth paths and both receipt operations are read from
/// <c>gwillem/appie-go</c> v0.0.12 and are no longer guesses. What remains
/// unverifiable offline is AH's login page: its markup has no published
/// contract, so every selector below is a candidate list with a shelf life
/// and is marked as such.
/// </summary>
public sealed record AlbertHeijnOptions
{
    /// <summary>
    /// Confirmed: <c>defaultClientID</c>. The adapter previously sent
    /// "appie", which is what made a real login fail - the suffix is not
    /// cosmetic.
    /// </summary>
    public string ClientId { get; init; } = "appie-ios";

    /// <summary>Confirmed: <c>defaultClientVersion</c>. Sent as <c>x-client-version</c>.</summary>
    public string ClientVersion { get; init; } = "9.28";

    /// <summary>
    /// Confirmed: <c>defaultUserAgent</c>. Sent because the API answers
    /// differently without it, not to disguise anything - this is the honest
    /// client identity the platform's posture allows.
    /// </summary>
    public string UserAgent { get; init; } = "Appie/9.28 (iPhone17,3; iPhone; CPU OS 26_1 like Mac OS X)";

    /// <summary>Confirmed: the <c>x-application</c> the API routes on.</summary>
    public string Application { get; init; } = "AHWEBSHOP";

    /// <summary>Confirmed: <c>defaultBaseURL</c>.</summary>
    public string ApiBaseUrl { get; init; } = "https://api.ah.nl";

    /// <summary>
    /// Confirmed: <c>loginURLTemplate</c>. Only the client id is
    /// substituted; the redirect URI is placed verbatim, exactly as the
    /// reference sends it. Percent-encoding it would be a "correction" that
    /// can fail an authorize request for a reason nobody can reconstruct
    /// later.
    /// </summary>
    public string AuthorizeUrlTemplate { get; init; } =
        "https://login.ah.nl/login?client_id={client_id}&response_type=code&redirect_uri={redirect_uri}";

    /// <summary>Confirmed: the scheme Chromium refuses to navigate to.</summary>
    public string RedirectUri { get; init; } = "appie://login-exit";

    public string TokenPath { get; init; } = "/mobile-auth/v1/auth/token";

    public string RefreshPath { get; init; } = "/mobile-auth/v1/auth/token/refresh";

    /// <summary>Confirmed: receipts are GraphQL, not REST. There is no receipts path.</summary>
    public string GraphQlPath { get; init; } = "/graphql";

    /// <summary>
    /// Confirmed: the receipt list operation, sent as a document. AH's
    /// endpoint takes <c>{ query, variables }</c> and no
    /// <c>operationName</c>.
    /// </summary>
    public string ListQuery { get; init; } =
        """
        query FetchPosReceipts($offset: Int!, $limit: Int!) {
          posReceiptsPage(pagination: {offset: $offset, limit: $limit}) {
            posReceipts { id dateTime totalAmount { amount } }
          }
        }
        """;

    /// <summary>
    /// Confirmed: the receipt detail. Note what it does <em>not</em> select -
    /// no stated total, and nothing that could carry a card or IBAN tail. The
    /// receipt's total therefore comes from the list, which is what makes the
    /// two a real reconciliation pair rather than one number repeated.
    /// </summary>
    public string DetailQuery { get; init; } =
        """
        query FetchReceipt($id: String!) {
          posReceiptDetails(id: $id) {
            id
            memberId
            products  { id quantity name price { amount } amount { amount } }
            discounts { name amount { amount } }
            payments  { method amount { amount } }
          }
        }
        """;

    /// <summary>Rows per <c>posReceiptsPage</c> call.</summary>
    public int PageSize { get; init; } = 50;

    /// <summary>
    /// The list's stated total. AH sends every amount in this API as a JSON
    /// number in euros; the unit is declared rather than sniffed because a
    /// heuristic that guesses wrong corrupts financial data silently.
    /// </summary>
    public MoneyUnit ListTotalUnit { get; init; } = MoneyUnit.MajorDecimal;

    /// <summary>A product line's <c>amount</c> and <c>price</c>.</summary>
    public MoneyUnit ItemAmountUnit { get; init; } = MoneyUnit.MajorDecimal;

    /// <summary>A discount's <c>amount</c>. Its own field, and its own declaration.</summary>
    public MoneyUnit DiscountAmountUnit { get; init; } = MoneyUnit.MajorDecimal;

    /// <summary>A payment's <c>amount</c>, used only to pick the largest of a split tender.</summary>
    public MoneyUnit PaymentAmountUnit { get; init; } = MoneyUnit.MajorDecimal;

    public string Currency { get; init; } = "EUR";

    // ---- login page: UNCONFIRMED, most specific first ---------------------

    /// <summary>
    /// UNCONFIRMED. AH's page is Dutch, so the last candidate resolves by
    /// visible label rather than by markup: an id can be renamed by a
    /// redesign, but the word next to the box is the one thing that cannot
    /// change without the page changing meaning.
    /// </summary>
    public IReadOnlyList<string> UsernameSelectors { get; init; } =
    [
        "input[name='username']",
        "input[name='email']",
        "input#email",
        "input[autocomplete='username']",
        "input[type='email']",
        "input:below(:text('E-mailadres'))",
    ];

    public IReadOnlyList<string> PasswordSelectors { get; init; } =
    [
        "input[name='password']",
        "input#password",
        "input[autocomplete='current-password']",
        "input[type='password']",
        "input:below(:text('Wachtwoord'))",
    ];

    public IReadOnlyList<string> SubmitSelectors { get; init; } =
    [
        "button[type='submit']",
        "input[type='submit']",
        "button:has-text('Inloggen')",
        "button:has-text('Log in')",
    ];

    /// <summary>
    /// UNCONFIRMED, and deliberately narrow: only phrasings that state a
    /// <em>credential</em> failure. A generic error box would also match a
    /// consent banner or an outage notice, and invalid_credentials is never
    /// retried by anything - so a false one is permanent for that session and
    /// sends the user to reset a password that was fine.
    /// </summary>
    public IReadOnlyList<string> LoginErrorSelectors { get; init; } =
    [
        ":text('e-mailadres of wachtwoord')",
        ":text('onjuiste combinatie')",
        ":text('wachtwoord is onjuist')",
        ":text('inloggegevens kloppen niet')",
    ];

    /// <summary>
    /// The widget in general - hCaptcha, which AH fronts its login with, and
    /// reCAPTCHA - identified by its iframes and its container classes.
    /// UNCONFIRMED beyond the hCaptcha entries.
    ///
    /// The fallback of the three lists, and reached only when neither
    /// <see cref="CaptchaGridSelectors"/> nor
    /// <see cref="CaptchaCheckboxSelectors"/> matched: a widget whose parts we
    /// cannot name is a token minted inside somebody else's JavaScript, with
    /// nothing to photograph and nothing to tap, and the honest outcomes are
    /// the old two - the person at the browser passes it, or nobody can.
    /// </summary>
    public IReadOnlyList<string> InteractiveCaptchaSelectors { get; init; } =
    [
        "iframe[src*='hcaptcha']",
        "iframe[src*='recaptcha']",
        "[data-hcaptcha-widget-id]",
        ".h-captcha",
        ".g-recaptcha",
    ];

    /// <summary>
    /// hCaptcha's image grid: the frame that is relayed to the account's owner
    /// as a picture and answered with taps.
    ///
    /// CONFIRMED 2026-07-28 by probing login.ah.nl: the widget is hCaptcha and
    /// it draws two iframes, both <c>hcaptcha.com/.../hcaptcha.html</c> and
    /// told apart only by the fragment on the URL - <c>#frame=challenge</c>
    /// for the grid, <c>#frame=checkbox-i</c> for the box. The rest of the
    /// list is UNCONFIRMED: the fragment is hCaptcha's own convention and not
    /// a contract, so the title attribute is the fallback for the day it
    /// changes.
    ///
    /// Ordering against <see cref="InteractiveCaptchaSelectors"/> matters and
    /// is the gate's, not ours: a grid also matches
    /// <c>iframe[src*='hcaptcha']</c>, so the specific probe runs first or
    /// every escalation reads as the generic widget it also is.
    /// </summary>
    public IReadOnlyList<string> CaptchaGridSelectors { get; init; } =
    [
        "iframe[src*='hcaptcha.com'][src*='#frame=challenge']",
        "iframe[src*='hcaptcha'][src*='challenge']",
        "iframe[title*='hCaptcha challenge' i]",
    ];

    /// <summary>
    /// hCaptcha's "I am human" box, on its own. CONFIRMED as an iframe on
    /// login.ah.nl; the fragment and the title are UNCONFIRMED conventions.
    ///
    /// Clicking this is a real click on a real control - the single gesture
    /// the widget is built to receive, and frequently the whole of what it
    /// asks for. It is not a solve and it is not a bypass: what it does is
    /// hand the decision to hCaptcha, which either lets the login through or
    /// draws the grid that the human then answers.
    /// </summary>
    public IReadOnlyList<string> CaptchaCheckboxSelectors { get; init; } =
    [
        "iframe[src*='hcaptcha.com'][src*='#frame=checkbox']",
        "iframe[src*='hcaptcha'][src*='checkbox']",
        "iframe[title*='hCaptcha' i][title*='checkbox' i]",
    ];

    /// <summary>
    /// UNCONFIRMED, and unproven on AH specifically: a plain image captcha is
    /// the one kind a relay can actually carry, so it stays supported even
    /// though hCaptcha is what a live attempt has met so far.
    /// </summary>
    public IReadOnlyList<string> ImageCaptchaSelectors { get; init; } =
    [
        "img[alt*='captcha' i]",
        "img[src*='captcha' i]",
        "img[id*='captcha' i]",
        ".captcha img",
    ];

    /// <summary>
    /// UNCONFIRMED. Where a plain typed captcha's answer goes - and, because
    /// a picture with nowhere to type it is no more answerable than a widget,
    /// also what decides which of the two is standing in the way.
    /// </summary>
    public IReadOnlyList<string> CaptchaInputSelectors { get; init; } =
    [
        "input[name='captcha']",
        "input#captcha",
        "input[autocomplete='one-time-code']",
    ];

    /// <summary>UNCONFIRMED. A consent wall left standing covers the form.</summary>
    public IReadOnlyList<string> ConsentSelectors { get; init; } =
    [
        "button#accept-cookies",
        "button:has-text('Accepteer')",
        "button:has-text('Akkoord')",
    ];

    public int SelectorTimeoutMs { get; init; } = 15_000;

    /// <summary>How long the login may take to resolve into a code, an error or a wall.</summary>
    public int LoginSettleSeconds { get; init; } = 180;

    /// <summary>
    /// How long each settle pass waits for the redirect before looking at the
    /// page again. Short, because the page is the only place a captcha or a
    /// stated error ever shows up.
    /// </summary>
    public int RedirectPollSeconds { get; init; } = 5;

    /// <summary>How long the human has to answer a relayed image captcha.</summary>
    public int ChallengeSeconds { get; init; } = 300;

    /// <summary>
    /// How long the human has on hCaptcha - a grid relayed to their own device
    /// as a picture to tap, or, when the widget shows no grid we can name, the
    /// widget left standing in the browser window we opened. Longer than a
    /// typed captcha on purpose: the wait has to cover someone noticing a
    /// prompt on a phone in another room.
    /// </summary>
    public int InteractiveCaptchaSeconds { get; init; } = 600;

    /// <summary>
    /// How many gestures the relay carries for one hCaptcha before handing it
    /// back: a tick, then a grid, then whatever the widget asks next. A human
    /// who answers a grid in two messages spends one of these too.
    ///
    /// Bounded on purpose. The loop's exit condition otherwise belongs to
    /// hCaptcha's JavaScript, and a login that asks a person to tap pictures
    /// indefinitely is one nobody finishes.
    /// </summary>
    public int CaptchaRelayRounds { get; init; } = 4;

    /// <summary>
    /// Stream Albert Heijn's own login page to the human instead of typing
    /// their credentials into it.
    ///
    /// On by default, because it is the only shape of this login that works
    /// for somebody with one device and no technical knowledge: no password
    /// reaches this platform at all, and whatever AH asks for next - a
    /// captcha, an SMS code, an app approval, something none of us has seen -
    /// is answered by the person who can actually answer it, on the page that
    /// asked for it.
    ///
    /// False restores the typed login and its captcha relay, which is proven
    /// live and stays reachable for the day AH changes something here.
    /// </summary>
    public bool LiveLogin { get; init; } = true;

    /// <summary>
    /// How long a streamed login may stay open. Generous on purpose: it has to
    /// cover reading an SMS, finding a password manager, and a captcha with
    /// several rounds in it. The job's own budget is parked while a human
    /// thinks, so this is the bound that actually applies.
    /// </summary>
    public int LiveLoginSeconds { get; init; } = 900;

    /// <summary>
    /// How long the human has on the last-resort redirect fallback, where
    /// they finish on AH's page in their own browser. Longer than a captcha:
    /// it is a whole login, on a device they may have to go and find.
    /// </summary>
    public int RedirectChallengeSeconds { get; init; } = 600;

    /// <summary>
    /// Whether a login that produced no code may fall back to asking the
    /// human to finish on AH's own page. Worth keeping: their browser and
    /// their IP are the ones most likely to get past the wall that stopped
    /// the agent.
    /// </summary>
    public bool RedirectFallbackEnabled { get; init; } = true;
}
