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
    public const int Version = 2;

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
        // T1. Nothing here drives a browser any more: the human signs in on
        // Lidl's own page in their OWN browser and hands back the code, so
        // this adapter only ever exchanges it and calls a JSON API.
        //
        // The browser tier was tried and is proven not to work. On 2026-07-28
        // a live attempt with correct credentials and correct selectors was
        // bounced back to the identifier screen with a generic notice:
        // reCAPTCHA Enterprise scores the browser, not the account, and a
        // fresh automated Chromium fails that scoring every time. A real
        // device passes it because it is one.
        Runtime = ProviderRuntime.Http,
        Agent = AgentRequirement.Inline,
        Unattended = true,
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
            Flow = AuthFlow.OauthRedirect,
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
                    Id = "authorize",
                    LabelKey = MessageKeys.StepRedirect,

                    // No credential fields at all, and that is the headline:
                    // the connector never sees the password. The human types
                    // it into Lidl's own page, in their own browser, and only
                    // a single-use authorization code comes back.
                    //
                    // The field is optional because it is not something anyone
                    // can supply up front - the authorize URL does not exist
                    // until the challenge is raised. A native shell fills it
                    // by intercepting the redirect and the human never sees
                    // it; a browser cannot follow a custom scheme, so the demo
                    // asks for a paste.
                    Fields =
                    [
                        new FieldSpec
                        {
                            Key = "redirect_url",
                            Type = FieldType.Text,

                            // Secret: the pasted address carries a live
                            // authorization code. Not a password, but it buys
                            // the same access for the minute it lives.
                            Secret = true,
                            Required = false,
                            LabelKey = MessageKeys.PasteRedirect,
                        },
                    ],
                },
            ],

            // Only the redirect. The code, any captcha and any device check
            // all happen inside the human's own browser now, where they are
            // that browser's problem rather than ours - which is precisely
            // what makes this work at all.
            Challenges = [ChallengeType.Redirect],
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
                    new ParamSpec { Key = "include", Type = ParamType.Enum, Values = ["items"], Multi = true },
                ],
                MaxHistoryDays = 730,
                TypicalDurationSeconds = 30,
                MaxRecordsPerFetch = 200,
            },
        ],
        Limits = new ProviderLimits { MinIntervalSeconds = 21_600, Concurrency = 1, MaxHistoryDays = 730 },
    };
}
