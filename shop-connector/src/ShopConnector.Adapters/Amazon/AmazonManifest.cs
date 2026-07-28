using Connector.Kit.Challenges;
using Connector.Kit.Manifests;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.Amazon;

/// <summary>
/// Copy keys this provider needs that the shared table does not have yet.
///
/// Declared here rather than added to <see cref="MessageKeys"/> because that
/// file is shared with every other adapter being written tonight, and a
/// two-line edit to a shared file is not worth the merge. Fold them in when
/// the night is over; the values are the contract, not the location.
/// </summary>
internal static class AmazonMessageKeys
{
    public const string Notes = "connect.amazon.notes";
}

internal static class AmazonManifest
{
    public const int Version = 1;

    /// <summary>
    /// Thirty days, and UNCONFIRMED.
    ///
    /// Amazon's <c>x-main</c> cookie is long-lived, but the honest figure is
    /// how long the whole session keeps being ACCEPTED, and that is decided by
    /// Amazon's risk engine rather than by an expiry we can read. Thirty days
    /// is a deliberately conservative reading of a cookie that may well last a
    /// year: a manifest that over-promises makes the consuming app tell a user
    /// their connection is fine right up until it is not, and being early with
    /// a reconnect prompt costs one sign-in.
    /// </summary>
    public const int SessionTtlSeconds = 2_592_000;

    /// <summary>
    /// T3, and every field below is an admission rather than an ambition.
    ///
    /// <c>browser_interactive</c> because there is no consumer API to fall
    /// back to - three maintained projects in three languages all scrape HTML,
    /// and if a JSON endpoint existed at least one of them would be using it.
    ///
    /// <c>unattended: false</c> is the load-bearing one. Amazon challenges
    /// routinely, and a challenge that arrives at three in the morning has
    /// nobody to answer it: the AWS WAF and ACIC walls are interactive
    /// widgets, which means they are passed by a human at the browser or not
    /// at all. Declaring unattended operation here would make the consumer
    /// offer scheduled syncing and then fail it - and the reference library
    /// only manages unattended runs by shipping integrations for three paid
    /// captcha-solving services, which is exactly the thing this platform will
    /// not do.
    /// </summary>
    public static ProviderManifest Build() => new()
    {
        Id = AmazonAdapter.ProviderId,
        Name = "Amazon.nl",
        Kind = ProviderKind.Store,
        Country = "NL",
        ManifestVersion = Version,
        Runtime = ProviderRuntime.BrowserInteractive,
        Agent = new AgentRequirement
        {
            Required = true,
            Class = AgentClass.Pooled,
            // A datacenter range fails Amazon's first test before the
            // credentials are even considered. An attended BYO agent is the
            // configuration that can actually pass an ACIC puzzle; a pooled
            // one on residential egress is the configuration that at least
            // gets to be asked.
            Egress = new EgressRequirement { Country = "NL", Kind = "residential" },
        },
        Unattended = false,
        SecretCustody = SecretCustody.Client,
        // The sign-in is a multi-screen chain that can stop for an OTP and a
        // visual challenge. Repeating it on every browser visit is not a thing
        // to ask of anybody, but the bundle still dies with the tab, which is
        // what Ephemeral means and is the honest setting for a session this
        // heavy.
        WebSupport = WebSupport.Ephemeral,
        LogoRef = "amazon",
        NotesKey = AmazonMessageKeys.Notes,
        Auth = new AuthSpec
        {
            // Amazon asks for the address and the password on two SCREENS, but
            // that is a property of its page rather than of the consumer's
            // form: both values are collected once and the adapter drives both
            // screens. TwoStep would make the consuming app render a round
            // trip that does not exist. What follows the password - a one-time
            // code, a puzzle - is declared below as a challenge, where its
            // delivery is decided per login instead of being frozen here.
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
                            Secret = true,
                            Required = true,
                            LabelKey = MessageKeys.FieldPassword,
                            Autofill = "current-password",
                        },

                        // There is deliberately NO otp_secret field.
                        //
                        // The reference library accepts a TOTP shared secret
                        // and auto-solves Amazon's one-time code with it, and
                        // its own documentation notes that enabling 2FA
                        // reduces how often Amazon challenges. That is a real
                        // operational win and it is still refused here: a
                        // shared secret is the second factor, and a service
                        // holding both factors has not connected to an account
                        // on a user's behalf, it has taken it over. The code
                        // goes to the human through the challenge protocol,
                        // which already reaches them wherever they are.
                    ],
                },
            ],
            // MfaCode is Amazon's one-time code. Image is the legacy OCR
            // captcha, the only wall a relay can carry. AppApproval is how the
            // platform expresses a widget that is passed in the browser window
            // in front of somebody - the AWS WAF challenge and the ACIC
            // "choose all the buckets" puzzle - where there is nothing to type
            // back at us and the only honest alternatives are a human at that
            // browser or blocked_by_provider.
            Challenges = [ChallengeType.MfaCode, ChallengeType.Image, ChallengeType.AppApproval],
            Session = new SessionSpec
            {
                TtlSeconds = SessionTtlSeconds,
                // No refresh grant exists: the credential is a cookie jar.
                // Nothing can renew it without driving the sign-in chain
                // again, which is a human's job on this provider.
                Refreshable = false,
                RotatesOnUse = true,
            },
            Reauth = new ReauthSpec { Cheap = false, TriggerCodes = ["session_expired"] },
        },
        Resources =
        [
            new ResourceSpec
            {
                Id = AmazonAdapter.ReceiptsResource,
                Returns = ResourceShape.Receipt,
                Params =
                [
                    new ParamSpec { Key = "since", Type = ParamType.Date, Required = true },
                    new ParamSpec { Key = "until", Type = ParamType.Date },
                    new ParamSpec { Key = "include", Type = ParamType.Enum, Values = ["items"], Multi = true },
                ],
                MaxHistoryDays = 1_095,
                // Every page is a browser navigation and every set of line
                // items is one more. This is minutes, not seconds, and saying
                // so lets a consumer choose to poll rather than block.
                TypicalDurationSeconds = 180,
                // Deliberately lower than the other stores'. Two hundred
                // orders with line items is two hundred and ten navigations
                // against a site that challenges when it is bored; a partial
                // pass that comes back for the rest is the safer shape.
                MaxRecordsPerFetch = 50,
            },
        ],
        Limits = new ProviderLimits
        {
            MinIntervalSeconds = 21_600,
            Concurrency = 1,
            MaxHistoryDays = 1_095,
            // An Amazon order's total moves after it is placed: it ships in
            // parts, it gets refunded, a promotion applies late. Re-reading a
            // fortnight on every sync is what keeps the first version of a
            // total from being the only one ever recorded.
            SettlementLagDays = 14,
        },
    };
}
