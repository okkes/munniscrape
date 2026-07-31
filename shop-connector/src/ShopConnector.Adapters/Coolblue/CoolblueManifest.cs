using Connector.Kit.Challenges;
using Connector.Kit.Manifests;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.Coolblue;

/// <summary>
/// Coolblue's catalogue entry.
///
/// The manifest is the only contract a consuming app codes against, so it has
/// to be honest about a provider that is genuinely half-finished: the login is
/// complete and confirmed, and the order fetch is not implemented because
/// nobody outside Coolblue knows what it looks like. That is said in the notes
/// key rather than left for a user to discover when a sync returns nothing.
/// </summary>
internal static class CoolblueManifest
{
    /// <summary>
    /// Version 1: nothing has shipped yet, so no bundle exists that could be
    /// misread. Bump this the moment the order fetch lands and changes what
    /// the material has to carry - a bundle sealed under this version may hold
    /// only tokens plus a browser state, and a later contract must not
    /// reinterpret it.
    /// </summary>
    public const int Version = 1;

    /// <summary>
    /// The copy key, and it names the caveat rather than hiding it. A consumer
    /// that renders this string's translation is telling the user, before they
    /// connect, that signing in will work and that order history will not
    /// arrive until the fetch is finished.
    ///
    /// A key, never prose: the connector cannot know the reader's language, and
    /// an English literal that reaches a screen becomes an untranslatable
    /// de-facto API the moment a consumer matches on it.
    /// </summary>
    public const string NotesKey = "connect.coolblue.notes.orders_unproven";

    /// <summary>
    /// T2. A browser drives one standards-compliant OIDC login, and the
    /// refresh token the <c>offline_access</c> scope yields serves everything
    /// afterwards over plain HTTP - no browser, no human, no stored password.
    /// That much is confirmed from Coolblue's own discovery document.
    /// </summary>
    public static ProviderManifest Build() => new()
    {
        Id = CoolblueAdapter.ProviderId,
        Name = "Coolblue",
        Kind = ProviderKind.Store,
        // Coolblue trades in NL, BE and DE, but only accounts.coolblue.nl has
        // been read. One country, stated honestly, rather than three claimed.
        Country = "NL",
        ManifestVersion = Version,
        Runtime = ProviderRuntime.BrowserOnce,
        Agent = new AgentRequirement
        {
            // A browser cannot run in the control plane, so the validator
            // requires an agent here whatever we think of the login's defences.
            Required = true,
            Class = AgentClass.Pooled,
            // 'any', not 'residential' - and that is a finding, not an
            // oversight. The 2026-07-28 check found no bot management on
            // coolblue.nl at all: server: CloudFront, no Akamai, no DataDome,
            // no Cloudflare BM cookie, and no captcha asset on the login form.
            // Demanding a residential line buys a real operational cost against
            // a wall nobody has seen. The country still matters: the site is
            // locale-routed and the login page is Dutch.
            Egress = new EgressRequirement { Country = "NL", Kind = "any" },
        },
        // The refresh token works headlessly, which is the whole thing that
        // makes scheduled sync offerable - and, per the validator, unattended
        // is only allowed to be true because the session below is refreshable.
        UnattendedFetch = true,
        // The two axes disagreeing, which is the whole reason they are two
        // fields: the refresh token fetches at three in the morning on its own,
        // and the login it came from can still meet a widget only somebody at
        // the browser can pass.
        LoginNeedsHeadedAgent = true,
        SecretCustody = SecretCustody.Client,
        WebSupport = WebSupport.Ephemeral,
        LogoRef = "coolblue",
        NotesKey = NotesKey,
        Auth = new AuthSpec
        {
            // One screen, two boxes. CONFIRMED: the authorize redirect lands on
            // a single form carrying a username input and a password input
            // together, unlike Lidl's two-screen wizard. Coolblue also offers a
            // separate passwordless-login form on the same page; it is a real
            // second route and it is deliberately not implemented, because
            // nothing about its exchange has been observed.
            Flow = AuthFlow.Password,
            Steps =
            [
                new AuthStep
                {
                    Id = "credentials",
                    LabelKey = MessageKeys.StepCredentials,
                    Fields =
                    [
                        new FieldSpec
                        {
                            Key = "username",
                            Type = FieldType.Text,
                            Secret = false,
                            Required = true,
                            LabelKey = MessageKeys.FieldEmail,
                            Autofill = "username",
                        },
                        new FieldSpec
                        {
                            Key = "password",
                            Type = FieldType.Password,
                            // An unmarked password is logged and screenshotted:
                            // redaction keys off exactly this flag, and the
                            // validator refuses the manifest without it.
                            Secret = true,
                            Required = true,
                            LabelKey = MessageKeys.FieldPassword,
                            Autofill = "current-password",
                        },
                    ],
                },
            ],
            // Declared defensively, and against the evidence rather than for
            // it: no captcha was found on this login form. But a consumer with
            // no UI for a challenge its provider suddenly raises strands the
            // user at the worst possible moment, and declaring two challenge
            // types costs nothing. Image is the kind a relay can carry;
            // AppApproval is the interactive widget, which can only be passed
            // by a human sitting at the browser we opened.
            Challenges = [ChallengeType.Image, ChallengeType.AppApproval],
            Session = new SessionSpec
            {
                // UNCONFIRMED, and deliberately conservative. The refresh grant
                // is confirmed; its lifetime is not stated anywhere public.
                // Thirty days is the common IdentityServer default and it errs
                // in the only safe direction: a manifest that under-promises
                // makes a consumer offer a reconnect slightly early, where one
                // that over-promises makes it promise months of silent syncing
                // and then fail every day.
                TtlSeconds = 2_592_000,
                // CONFIRMED: refresh_token is a supported grant and
                // offline_access is in the scope list.
                Refreshable = true,
                RotatesOnUse = true,
            },
            Reauth = new ReauthSpec { Cheap = true, TriggerCodes = ["session_expired"] },
        },
        Resources =
        [
            new ResourceSpec
            {
                Id = CoolblueAdapter.ReceiptsResource,
                Returns = ResourceShape.Receipt,
                Params =
                [
                    new ParamSpec { Key = "since", Type = ParamType.Date, Required = true },
                    new ParamSpec { Key = "until", Type = ParamType.Date },
                    new ParamSpec { Key = "include", Type = ParamType.Enum, Values = ["items"], Multi = true },
                ],
                MaxHistoryDays = 730,
                TypicalDurationSeconds = 30,
                MaxRecordsPerFetch = 200,
            },
        ],
        Limits = new ProviderLimits
        {
            MinIntervalSeconds = 21_600,
            Concurrency = 1,
            MaxHistoryDays = 730,
            // An electronics order is placed on one day and stays dated that
            // day; nothing settles late the way a card transaction does.
            SettlementLagDays = 0,
        },
    };
}
