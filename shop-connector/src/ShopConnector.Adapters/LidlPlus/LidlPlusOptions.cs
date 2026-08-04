using Connector.Kit.Normalization;

namespace ShopConnector.Adapters.LidlPlus;

/// <summary>
/// Lidl Plus settings. Endpoints, headers, client id, client secret, redirect
/// URI and scopes are all confirmed from the reference implementation
/// (<c>yagueto/lidl-plus</c>, <c>lidlplus/api.py</c>), and so - for the first
/// time - are the four input names the login page uses. Everything still
/// marked UNCONFIRMED below is listed here so a redesign is an operator edit
/// rather than a release.
/// </summary>
public sealed record LidlPlusOptions
{
    public string AuthorizeUrl { get; init; } = "https://accounts.lidl.com/connect/authorize";

    public string TokenUrl { get; init; } = "https://accounts.lidl.com/connect/token";

    public string ClientId { get; init; } = "LidlPlusNativeClient";

    /// <summary>
    /// CONFIRMED from the reference, read 2026-07-28: it base64s the literal
    /// <c>LidlPlusNativeClient:secret</c> for the token endpoint's HTTP Basic
    /// header, so the word <c>secret</c> is the value and not a placeholder
    /// somebody forgot to fill in.
    ///
    /// This was empty, which sent <c>LidlPlusNativeClient:</c> - the shape a
    /// public client would use, and one Lidl rejects. Overridable because a
    /// hardcoded secret in a shipped mobile app is exactly the kind of value
    /// that gets rotated without notice.
    /// </summary>
    public string ClientSecret { get; init; } = "secret";

    public string RedirectUri { get; init; } = "com.lidlplus.app://callback";

    /// <summary><c>offline_access</c> is the scope that yields the refresh token.</summary>
    public string Scope { get; init; } = "openid profile offline_access lpprofile lpapis";

    public string TicketsBaseUrl { get; init; } = "https://tickets.lidlplus.com";

    /// <summary>
    /// The list and the detail sit on different API versions. This is not a
    /// typo and must not be "fixed": v2 answers the list, v3 answers the
    /// detail.
    /// </summary>
    public string TicketListPathTemplate { get; init; } = "/api/v2/{country}/tickets?pageNumber={page}&onlyFavorite=false";

    public string TicketDetailPathTemplate { get; init; } = "/api/v3/{country}/tickets/{id}";

    /// <summary>
    /// CONFIRMED live on 2026-08-03, and the single value that made every
    /// Lidl fetch fail.
    ///
    /// This used to be <c>999.99.9</c>, taken from the public reference
    /// implementation. Lidl's edge now drops the TCP connection on that exact
    /// string - not a 403, no response at all, which surfaced here as
    /// "request timed out" sixty seconds later and named nothing.
    ///
    /// It is that string specifically and not implausible versions in general.
    /// Measured against the live endpoint, one header at a time:
    /// <code>
    ///   App-Version: 999.99.9   -> connection dropped
    ///   App-Version: 999.99.99  -> 401
    ///   App-Version: 99.99.9    -> 401
    ///   App-Version: 15.20.3    -> 401
    /// </code>
    /// A 401 is the right answer to a bogus token, so every other value gets
    /// as far as authentication. The sentinel is blocklisted because it is
    /// what the well-known scraper library sends.
    ///
    /// The value is therefore not validated by Lidl, only screened. A real
    /// release number is used rather than another sentinel: the next value
    /// somebody blocklists will be whichever one the scrapers converge on.
    /// </summary>
    public string AppVersionHeader { get; init; } = "15.20.3";

    /// <summary>Confirmed casing. The API routes on it; it is not a disguise.</summary>
    public string OperatingSystemHeader { get; init; } = "iOs";

    public string AppHeader { get; init; } = "com.lidl.eci.lidl.plus";

    /// <summary>
    /// UNCONFIRMED unit. Lidl states amounts as comma-decimal strings
    /// ("12,34"). Declared, never sniffed, and checked by reconciliation.
    /// </summary>
    public MoneyUnit AmountUnit { get; init; } = MoneyUnit.MajorString;

    public string Currency { get; init; } = "EUR";

    public int MaxPages { get; init; } = 20;

    /// <summary>
    /// How long the human has to fetch the one-time code and type it back.
    /// Generous: they may have to go and find the phone, or wait for a mail
    /// to arrive.
    /// </summary>
    public int CodeChallengeSeconds { get; init; } = 300;

    /// <summary>
    /// CONFIRMED length: Lidl's verification code is six digits. Stated so
    /// the consumer can size the box and validate before spending a code.
    /// </summary>
    public int CodeLength { get; init; } = 6;

    /// <summary>
    /// How long the human has to finish signing in. Generous on purpose: this
    /// is somebody switching to a browser, possibly finding a password
    /// manager, possibly getting a text. The job's own budget stops counting
    /// while it waits, so this is the only clock that matters.
    /// </summary>
    public int ClientAuthorizationSeconds { get; init; } = 900;

    public int SelectorTimeoutMs { get; init; } = 15_000;

    /// <summary>Short probes: these run on a page that is already the right one.</summary>
    public int ProbeMs { get; init; } = 500;

    /// <summary>
    /// How long to look for the password box on the FIRST screen before
    /// deciding this is the two-step wizard and clicking Next.
    ///
    /// Short on purpose. Lidl asks for the identifier and the password on
    /// separate screens, so on the live page this probe always misses, and
    /// every millisecond of it is latency on every login. Long enough to
    /// cover a slow render of a single-page form, short enough that being
    /// wrong costs nothing. Raise it only if a single-page variant starts
    /// being misread as a wizard.
    /// </summary>
    public int PasswordProbeMs { get; init; } = 2_000;

    public int RedirectTimeoutSeconds { get; init; } = 120;

    /// <summary>How long each pass waits on the redirect before looking at the page again.</summary>
    public int RedirectPollSeconds { get; init; } = 5;

    /// <summary>
    /// How long a streamed sign-in may stay open.
    ///
    /// Far longer than the typed path's patience, and deliberately: somebody
    /// is reading a real page, may be waiting on a text message, and may be
    /// doing it one-handed on a phone. The job budget stops counting while
    /// parked on the challenge, so this is generosity rather than latency.
    /// </summary>
    public int LiveLoginSeconds { get; init; } = 600;

    /// <summary>How long the human has to answer a relayed image captcha.</summary>
    public int ChallengeSeconds { get; init; } = 300;

    /// <summary>
    /// How long the human has to pass an interactive widget in the browser
    /// window we opened in front of them. Longer than a typed captcha: nobody
    /// is relaying anything, so the wait has to cover someone noticing.
    /// </summary>
    public int InteractiveCaptchaSeconds { get; init; } = 600;

    // ---- login page: the reference's names first --------------------------

    /// <summary>
    /// The e-mail box. <c>input-email</c> is CONFIRMED from the reference
    /// (read 2026-07-28), which selects it by its <c>name</c> attribute; the
    /// rest are candidates behind it.
    ///
    /// Its existence is the whole point of this list. Lidl's README says
    /// "username (phone number)", so the adapter only ever knew how to fill
    /// the phone box - and the source shows the page has both.
    /// </summary>
    // OBSERVED on accounts.lidl.com, 2026-07-28, by loading the login page
    // and reading its markup - no credential submitted. Two things that page
    // settled, and that no amount of reading the reference would have:
    //
    //   * The stable hook is `data-testid`. Every class on that page is
    //     generated Tailwind ("px-4 rounded-form-radius bg-transparent ...") -
    //     a class selector there is a selector with a shelf life of one
    //     redeploy. data-testid leads every list for that reason.
    //   * The reference's `role_next` is not on the page at all. It cost two
    //     live attempts before the page was simply looked at, which is the
    //     lesson: read the markup first, guess never.

    public IReadOnlyList<string> EmailSelectors { get; init; } =
    [
        "input[data-testid='input-email']",
        "input#input-email",
        "input[name='input-email']",
        "input[autocomplete='email']",
        "input[type='email']",
    ];

    public IReadOnlyList<string> PhoneSelectors { get; init; } =
    [
        "input[data-testid='input-phone']",
        "input#input-phone",
        "input[name='input-phone']",
        "input[autocomplete='tel']",
        "input[type='tel']",
    ];

    /// <summary>
    /// Step two. Present in the DOM from the first paint but hidden, inside a
    /// second form, so it is found by id rather than by visibility.
    /// </summary>
    public IReadOnlyList<string> PasswordSelectors { get; init; } =
    [
        "input[data-testid='login-input-password']",
        "input#Password",
        "input[name='Password']",
        "input[autocomplete='current-password']",
        "input[type='password']",
    ];

    /// <summary>
    /// The identifier step's button - reads "Volgende" (Next), which is what
    /// makes this a two-screen login rather than a form with two boxes.
    /// </summary>
    public IReadOnlyList<string> SubmitSelectors { get; init; } =
    [
        "[data-testid='login-or-register-submit-button']",
        "button[type='submit']",
        "input[type='submit']",
    ];

    /// <summary>
    /// OBSERVED on screen two. Both buttons read "Volgende" and neither is
    /// the other: screen one is <c>login-or-register-submit-button</c>, screen
    /// two is the generic <c>button-primary</c>. One list for both clicked the
    /// wrong form and cost a live attempt to discover.
    /// </summary>
    public IReadOnlyList<string> PasswordSubmitSelectors { get; init; } =
    [
        "[data-testid='button-primary']",
        "#btn_submit_password",
        "button[type='submit']",
    ];

    /// <summary>
    /// The page offers "Gebruik telefoonnummer" to swap the identifier input.
    /// Only needed if a phone is supplied while the e-mail box is showing.
    /// </summary>
    public IReadOnlyList<string> SwitchMethodSelectors { get; init; } =
    [
        "[data-testid='switch-method-button']",
    ];

    /// <summary>
    /// OBSERVED 2026-07-28. What Lidl does to an automated browser after the
    /// password: it throws the login back to the e-mail screen carrying
    /// "Er is iets mis gegaan. Probeer het later nog eens." - something went
    /// wrong, try later.
    ///
    /// It is deliberately uninformative, and it is NOT a credential error:
    /// the same password signs in by hand. The login page carries a
    /// reCAPTCHA Enterprise anchor, so the overwhelmingly likely reading is a
    /// score-based verdict on the browser rather than anything about the
    /// account. Score-based means there is no widget and nothing a human can
    /// solve, which is why this is <see cref="Errors.ErrorCode.BlockedByProvider"/>
    /// and not a challenge.
    ///
    /// Matching on copy is fragile and it is the only signal offered. Failing
    /// to match costs the old behaviour - a 144-second wait for a redirect
    /// that is never coming, reported as a shape change - so a stale string
    /// here degrades to merely unhelpful rather than to wrong.
    /// </summary>
    public IReadOnlyList<string> BlockedNoticeSelectors { get; init; } =
    [
        "p:has-text('Er is iets mis gegaan')",
        "p:has-text('Something went wrong')",
        "[data-testid='error-message']",
    ];

    /// <summary><c>VerificationCode</c> is CONFIRMED from the reference.</summary>
    public IReadOnlyList<string> VerificationCodeSelectors { get; init; } =
    [
        "input[name='VerificationCode']",
        "input[autocomplete='one-time-code']",
        "input[name='code']",
        "input#input-code",
    ];

    /// <summary>
    /// UNCONFIRMED, and honestly so: the code screen is the one step that
    /// cannot be reached without spending a real login, so unlike the others
    /// it was not read off the page.
    ///
    /// <c>button-primary</c> leads it on evidence rather than hope - screen
    /// two's "Volgende" is exactly that generic testid, so the flow appears to
    /// use a specific hook for the first screen and the generic one from then
    /// on. Replace this the moment a live run reaches the code screen.
    /// </summary>
    public IReadOnlyList<string> VerificationSubmitSelectors { get; init; } =
    [
        "[data-testid='button-primary']",
        "[data-testid='verify-code-submit-button']",
        "#btn_submit_code",
        "button[type='submit']",
        "input[type='submit']",
    ];

    /// <summary>
    /// UNCONFIRMED. Markers that say the page itself sent the code by e-mail.
    ///
    /// The reference takes the mode as a parameter (<c>"phone"</c> or
    /// <c>"email"</c>) rather than reading it back, so there is no confirmed
    /// signal for this and these are guesses like every other selector here.
    /// They exist because they are the only evidence that beats an inference:
    /// a user who signed in with a phone number can still have the code sent
    /// to their mailbox, and only the page knows.
    /// </summary>
    public IReadOnlyList<string> EmailDeliverySelectors { get; init; } =
    [
        "[data-verify-mode='email']",
        "[data-mode='email']",
        "input[name='VerificationMode'][value='email']",
    ];

    /// <summary>UNCONFIRMED. The same, for a code that went to a phone.</summary>
    public IReadOnlyList<string> SmsDeliverySelectors { get; init; } =
    [
        "[data-verify-mode='phone']",
        "[data-mode='phone']",
        "input[name='VerificationMode'][value='phone']",
    ];

    /// <summary>
    /// UNCONFIRMED, and unproven on Lidl specifically. The interactive
    /// widgets - hCaptcha and reCAPTCHA - identified by their iframes and
    /// their container classes.
    ///
    /// These are not pictures. A widget wants drags and tile clicks and mints
    /// its token inside its own JavaScript, so no photograph and no typed
    /// answer can carry one. accounts.lidl.com is defended by the same class
    /// of thing login.ah.nl turned out to be fronted by, and the failure that
    /// mattered there was a job that hung rather than said so.
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
    /// UNCONFIRMED. Where a plain typed captcha's answer goes - and, because
    /// a picture with nowhere to type it is no more answerable than a widget,
    /// also what decides which of the two is standing in the way.
    ///
    /// Deliberately does <em>not</em> list <c>VerificationCode</c>: the
    /// one-time code box is not a captcha box, and matching it here would
    /// photograph the 2FA step and call it a wall.
    /// </summary>
    public IReadOnlyList<string> CaptchaInputSelectors { get; init; } =
    [
        "input[name='captcha']",
        "input#captcha",
    ];
}
