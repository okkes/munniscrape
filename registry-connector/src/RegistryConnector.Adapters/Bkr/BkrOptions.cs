using Connector.Kit.Normalization;

namespace RegistryConnector.Adapters.Bkr;

/// <summary>
/// BKR's settings. Everything the portal could rename lives here, so a change
/// on their side is an operator edit rather than a release.
///
/// The labels are load-bearing and deliberately not selectors. The print block
/// this parser reads keys its values by the Dutch label a human sees -
/// "Overeenkomstnummer", "Bedrag" - and those are far stabler than the
/// Bootstrap column classes around them, which change whenever somebody
/// adjusts a layout. Confirmed against a live capture on 2026-08-04.
/// </summary>
public sealed record BkrOptions
{
    public string PortalUrl { get; init; } = "https://portaal.mijnkredietregistratie.nl/";

    /// <summary>
    /// The host the sign-in happens on. Azure AD B2C, tenant
    /// <c>bkrconsp.onmicrosoft.com</c>, policy
    /// <c>B2C_1A_SignUp_SignIn_SmsOrTotp</c> - whose name states the two
    /// second factors on offer.
    /// </summary>
    public string LoginHost { get; init; } = "login.mijnkredietregistratie.nl";

    /// <summary>Where a completed sign-in lands. Reaching it is what ends the login.</summary>
    public string SignedInUrlMarker { get; init; } = "portaal.mijnkredietregistratie.nl";

    // ---- the sign-in page ---------------------------------------------------

    /// <summary>
    /// CONFIRMED. B2C's stock ids, present in the capture.
    /// </summary>
    public IReadOnlyList<string> UsernameSelectors { get; init; } =
        ["#signInName", "#email", "input[name='signInName']", "input[type='email']"];

    public IReadOnlyList<string> PasswordSelectors { get; init; } =
        ["#password", "input[name='password']", "input[type='password']"];

    public IReadOnlyList<string> SubmitSelectors { get; init; } =
        ["#next", "button#next", "button[type='submit']"];

    /// <summary>
    /// CONFIRMED. The second-factor screen carries <c>#otpCode</c> beside
    /// <c>#QrCodeVerifyInstruction</c>, which is B2C's authenticator page.
    /// </summary>
    public IReadOnlyList<string> CodeSelectors { get; init; } =
        ["#otpCode", "#verificationCode", "input[name='otpCode']"];

    public IReadOnlyList<string> CodeSubmitSelectors { get; init; } =
        ["#continue", "button#continue", "button[type='submit']"];

    // ---- the print block ----------------------------------------------------

    /// <summary>
    /// The hidden block holding one page per credit. Its absence is a shape
    /// change worth stopping for, not a reason to fall back to scraping the
    /// visible list.
    /// </summary>
    public string PrintBlockMarker { get; init; } = "pdf-export";

    /// <summary>
    /// The stable per-credit identifier. BKR's own contract number, which the
    /// portal will even route on - /Home/Details/{number} resolves the same
    /// page as the GUID the list links to. Keyed on this rather than the GUID
    /// because a re-issued GUID would duplicate every credit on the next sync.
    /// </summary>
    public string ReferenceLabel { get; init; } = "Overeenkomstnummer";

    public string CreditorLabel { get; init; } = "Aanbieder";

    public string AmountLabel { get; init; } = "Bedrag";

    public string KindLabel { get; init; } = "Kredietsoort";

    public string RegisteredLabel { get; init; } = "Registratiedatum";

    public string ExpectedEndLabel { get; init; } = "Verwachte einddatum";

    /// <summary>
    /// The field that decides running versus ended. Present and filled means
    /// the credit is over; blank means it is not. Never inferred from the
    /// expected end date having passed - that is precisely the mistake this
    /// field exists to prevent.
    /// </summary>
    public string ActualEndLabel { get; init; } = "Werkelijke einddatum";

    public string SurnameLabel { get; init; } = "Achternaam";

    public string InitialsLabel { get; init; } = "Voorletter(s)";

    /// <summary>Where a special notice would be stated, if there were one.</summary>
    public IReadOnlyList<string> ArrearsLabels { get; init; } = ["Bijzonderheden", "Bijzonderheid", "Code"];

    /// <summary>
    /// What the register prints when there is nothing to report. Matched so
    /// that "no arrears" arrives as null rather than as the literal sentence,
    /// which a consumer would render as though it were a code.
    /// </summary>
    public IReadOnlyList<string> NoArrearsMarkers { get; init; } =
        ["Geen bijzonderheden", "Geen bijzonderheid"];

    /// <summary>
    /// The page's own words for "nothing is registered against you". Only
    /// this makes an empty result an answer rather than a parse failure.
    /// </summary>
    public string EmptyRegisterMarker { get; init; } = "geen kredieten";

    /// <summary>
    /// Dutch credit types, longest and most specific first: "Doorlopend
    /// krediet" and "Aflopend krediet" both end in "krediet", so a shorter
    /// needle would swallow the other.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, CreditKind>> CreditKinds { get; init; } =
    [
        new("Doorlopend krediet", CreditKind.Revolving),
        new("Aflopend krediet", CreditKind.Instalment),
        new("Verzendhuiskrediet", CreditKind.DeferredPayment),
        new("Hypoth", CreditKind.Mortgage),
        new("Lease", CreditKind.Lease),
        new("Schuldregeling", CreditKind.Other),
    ];

    public IReadOnlyList<string> DateFormats { get; init; } = ["dd-MM-yyyy", "d-M-yyyy", "dd/MM/yyyy"];

    public string Currency { get; init; } = "EUR";

    // ---- patience -----------------------------------------------------------

    public int SelectorTimeoutMs { get; init; } = 15_000;

    /// <summary>Short: these probe a page that is already the right one.</summary>
    public int ProbeMs { get; init; } = 800;

    /// <summary>
    /// How long a streamed sign-in may stay open. Generous: somebody may be
    /// finding a password manager, waiting on a text, or reading a consent
    /// screen. The job budget stops counting while parked on the challenge.
    /// </summary>
    public int LiveLoginSeconds { get; init; } = 600;

    /// <summary>How long the human has to type six digits when we ask for them.</summary>
    public int CodeChallengeSeconds { get; init; } = 300;

    public int SettlePollSeconds { get; init; } = 2;

    public int SignInSeconds { get; init; } = 120;
}
