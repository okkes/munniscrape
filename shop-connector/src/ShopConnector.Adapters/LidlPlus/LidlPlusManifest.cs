using Connector.Kit.Challenges;
using Connector.Kit.Manifests;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.LidlPlus;

internal static class LidlPlusManifest
{
    /// <summary>
    /// Bumped to 2 when the credential stopped being a phone number and
    /// became "an e-mail address or a phone number". Sealed into every bundle
    /// as AAD, so a bundle minted under version 1 is rejected outright rather
    /// than reinterpreted against a contract it was never issued for - which
    /// is the right outcome here, because a version-1 bundle was issued for a
    /// form that could not express the account most people actually have.
    /// </summary>
    public const int Version = 3;

    /// <summary>Countries the ticket API is addressed by. Uppercase in the URL and the header.</summary>
    public static readonly IReadOnlyList<string> Countries = ["NL", "DE", "AT", "BE", "FR", "IT", "ES"];

    public static readonly IReadOnlyList<string> Languages = ["nl", "de", "en", "fr", "it", "es"];

    /// <summary>
    /// The one box that takes either shape, anchored, and the only '@' either
    /// of them can contain belongs to the e-mail.
    ///
    /// That is not a coincidence: the adapter picks which of Lidl's two
    /// inputs to fill by looking for exactly that character, so the form's
    /// validation and the fill cannot disagree about what the user typed.
    /// The phone half is the old field's pattern unchanged - E.164, which is
    /// what an international login page wants.
    /// </summary>
    public const string UsernamePattern = @"^(?:[^@\s]+@[^@\s]+\.[A-Za-z]{2,}|\+[1-9][0-9]{6,14})$";

    /// <summary>
    /// T2, and the tier's flagship: a browser drives login exactly once, and
    /// the refresh token serves every fetch afterwards with no browser, no
    /// human and no stored password.
    /// </summary>
    public static ProviderManifest Build() => new()
    {
        Id = LidlPlusAdapter.ProviderId,
        Name = "Lidl Plus",
        Kind = ProviderKind.Store,
        Country = "NL",
        ManifestVersion = Version,
        // T3, and back in a browser - but not the same browser that failed.
        //
        // The reCAPTCHA Enterprise finding from 2026-07-28 stands: it scores
        // the BROWSER, not the account, and a fresh automated Chromium fails
        // it every time. The conclusion drawn from that - hand the sign-in to
        // the user's own browser and ask them to paste the callback address
        // back - was correct for a native shell and unusable for anyone else.
        // Lidl's OAuth client is the phone app, so it returns to
        // com.lidlplus.app://callback, an address a desktop browser cannot
        // follow; the instruction "copy this error page's URL" is too much to
        // ask of somebody connecting a loyalty card.
        //
        // Streaming answers the scoring directly. A real person drives the
        // page, so reCAPTCHA is looking at real behaviour, and the redirect
        // arrives in OUR browser where the adapter reads the code itself.
        // Nobody copies anything. What comes out is still an OAuth refresh
        // token, so everything below this line is unchanged: 90 days,
        // renewable, and no browser after the first sign-in.
        Runtime = ProviderRuntime.BrowserInteractive,
        Agent = new AgentRequirement
        {
            Required = true,
            Class = AgentClass.Pooled,
            Egress = new EgressRequirement { Country = "NL", Kind = "residential" },
        },
        // Still true, and it is the whole reason this provider is worth having:
        // the login is the only part that needs anybody. After it, a refresh
        // token serves every fetch for ninety days with nobody present.
        UnattendedFetch = true,
        LoginNeedsHeadedAgent = false,

        // No credential store, and the validator is right to refuse one here:
        // "offers_credential_store is refused on a refreshable session - the
        // refresh is what stops the human being asked again". Lidl's refresh
        // token already does that for ninety days, so holding a password
        // beside it would buy nothing and risk something. The optional fields
        // below are for the ONE sign-in, not for keeping.
        SecretCustody = SecretCustody.Client,
        WebSupport = WebSupport.Ephemeral,
        LogoRef = "lidl",
        NotesKey = MessageKeys.LidlNotes,
        Auth = new AuthSpec
        {
            // Not PasswordSms any more, and the change is a correction rather
            // than a downgrade. Lidl sends its one-time code by SMS to a user
            // who signed in with a phone number and by e-mail to one who
            // signed in with an address, so "password_sms" was true for half
            // the accounts and a wrong instruction for the other half. The
            // kit's enum has no password_otp and the kit is frozen, so the
            // honest value left is the one that describes what the consumer
            // must render: a single credential step. That a code follows is
            // declared below in challenges, where its delivery is decided per
            // login and stated on the challenge itself.
            Flow = AuthFlow.Password,
            // Neither secrets nor challenges: country and language appear in
            // the ticket URLs and in the Accept-Language and Country
            // headers, and nothing works without them.
            Config =
            [
                new FieldSpec
                {
                    Key = "country",
                    Type = FieldType.Select,
                    Options = Countries,
                    Required = true,
                    LabelKey = MessageKeys.LidlCountry,
                },
                new FieldSpec
                {
                    Key = "language",
                    Type = FieldType.Select,
                    Options = Languages,
                    Required = true,
                    LabelKey = MessageKeys.LidlLanguage,
                },
            ],
            Steps =
            [
                new AuthStep
                {
                    Id = "credentials",
                    LabelKey = MessageKeys.StepCredentials,

                    // Both OPTIONAL, and that is the contract the whole login
                    // rests on. Supply them and the adapter types them into
                    // Lidl's page for you; supply nothing and the page is
                    // streamed from the first screen. A first connect can
                    // therefore be offered to somebody who has not decided yet
                    // whether to let us hold a password - and the credential
                    // store above only ever seals what was actually typed here.
                    Fields =
                    [
                        new FieldSpec
                        {
                            Key = "username",
                            Type = FieldType.Text,
                            Required = false,
                            Pattern = UsernamePattern,
                            LabelKey = MessageKeys.FieldEmailOrPhone,
                        },
                        new FieldSpec
                        {
                            Key = "password",
                            Type = FieldType.Password,
                            Secret = true,
                            Required = false,
                            LabelKey = MessageKeys.FieldPassword,
                        },
                    ],
                },
            ],

            // LiveView first, because it is the answer to the wall: reCAPTCHA
            // Enterprise scores the browser, and the only browser that passes
            // is one a real person is driving. The other three are the typed
            // path, which is a shipped path - Lidl sends a one-time code by
            // SMS or e-mail depending on which identifier was used, and may
            // raise a picture or a widget on the way. Declaring one and
            // raising four strands a user at the worst possible moment.
            Challenges =
            [
                ChallengeType.LiveView,
                ChallengeType.MfaCode,
                ChallengeType.Image,
                ChallengeType.AppApproval,
            ],
            Session = new SessionSpec { TtlSeconds = 7_776_000, Refreshable = true, RotatesOnUse = true },
            Reauth = new ReauthSpec { Cheap = true, TriggerCodes = ["session_expired"] },
        },
        Resources =
        [
            new ResourceSpec
            {
                Id = LidlPlusAdapter.ReceiptsResource,
                Returns = ResourceShape.Receipt,
                Params =
                [
                    new ParamSpec { Key = "since", Type = ParamType.Date, Required = true },
                    new ParamSpec { Key = "until", Type = ParamType.Date },
                    new ParamSpec { Key = "include", Type = ParamType.Enum, Values = ["items", "raw"], Multi = true },
                ],
                MaxHistoryDays = 730,
                TypicalDurationSeconds = 30,
                MaxRecordsPerFetch = 200,
            },
        ],
        Limits = new ProviderLimits { MinIntervalSeconds = 21_600, Concurrency = 1, MaxHistoryDays = 730 },
    };
}
