using Connector.Kit.Challenges;
using Connector.Kit.Manifests;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.Bol;

internal static class BolManifest
{
    public const int Version = 1;

    /// <summary>
    /// How long the stored cookie jar is claimed to last.
    ///
    /// A guess, and deliberately a short one. bol's login is a Spring form
    /// login: a <c>JSESSIONID</c> whose idle timeout nobody outside bol knows,
    /// probably alongside a longer-lived remember-me cookie nobody has seen
    /// either. A manifest that lies is worse than an unsupported provider -
    /// it makes the consuming app promise silent syncing and then fail - so
    /// this claims a day and a live run measures the truth. Being wrong short
    /// costs an extra sign-in; being wrong long costs a user their trust in
    /// the whole connection.
    /// </summary>
    public const int SessionTtlSeconds = 86_400;

    /// <summary>
    /// The credential is an e-mail address. CONFIRMED: the login page renders
    /// <c>&lt;input type="email" name="j_username"&gt;</c>, so a consumer that
    /// offers a member number or a phone keypad costs a failed attempt
    /// against a page that may be scoring the browser.
    /// </summary>
    public const string UsernamePattern = @"^[^@\s]+@[^@\s]+\.[A-Za-z]{2,}$";

    /// <summary>
    /// T2 in runtime and honest about everything that follows.
    ///
    /// The login is a JavaScript page with a reCAPTCHA site key in its config
    /// blob, so it happens in a browser once; the session that comes out of it
    /// is an ordinary cookie jar, so every fetch afterwards is plain HTTP.
    /// That is exactly <c>browser_once</c>.
    ///
    /// What it is NOT is unattended. There is no refresh grant of any kind -
    /// bol issues consumers no token, only a session cookie - so when the
    /// cookie dies the only way back is a human at a login page. Declaring
    /// <c>unattended: true</c> would need <c>refreshable: true</c> beside it,
    /// which would be a claim about a mechanism that does not exist, and the
    /// validator would rightly still accept it - the manifest rules cannot
    /// catch a lie, only an inconsistency. So this says false twice and lets
    /// the consumer offer what it can actually deliver: connect, sync while
    /// the session lasts, and ask for a sign-in when it does not.
    /// </summary>
    public static ProviderManifest Build() => new()
    {
        Id = BolAdapter.ProviderId,
        Name = "bol.com",
        Kind = ProviderKind.Store,
        Country = "NL",
        ManifestVersion = Version,
        Runtime = ProviderRuntime.BrowserOnce,
        Agent = new AgentRequirement
        {
            Required = true,
            Class = AgentClass.Pooled,
            // No enterprise bot vendor sits in front of bol today - confirmed
            // from response headers rather than assumed - but the login page
            // carries a reCAPTCHA key, and a datacenter range is the cheapest
            // possible signal for a risk-based score to fail on.
            Egress = new EgressRequirement { Country = "NL", Kind = "residential" },
        },
        UnattendedFetch = false,
        // reCAPTCHA's interactive form is unrelayable: there is no picture that
        // carries the question.
        LoginNeedsHeadedAgent = true,
        SecretCustody = SecretCustody.Client,
        WebSupport = WebSupport.Ephemeral,
        LogoRef = "bol",
        NotesKey = BolMessageKeys.BolNotes,
        Auth = new AuthSpec
        {
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
                            Pattern = UsernamePattern,
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
                    ],
                },
            ],
            // MfaCode because bol is assumed to send a code on a new device
            // and nobody has proved otherwise. Image for a plain captcha,
            // which a relay can carry. AppApproval for the reCAPTCHA whose
            // site key is confirmed present: a widget cannot be relayed at
            // all, so the human passes it in the agent's own window and the
            // challenge exists only to ask them and wait.
            //
            // All three are declared rather than discovered. Announcing a
            // challenge that never fires costs nothing; failing to announce
            // one that does strands a user at the worst possible moment, with
            // a consumer that has no UI for what its provider just asked.
            Challenges = [ChallengeType.MfaCode, ChallengeType.Image, ChallengeType.AppApproval],
            Session = new SessionSpec
            {
                TtlSeconds = SessionTtlSeconds,
                Refreshable = false,
                RotatesOnUse = true,
            },
            Reauth = new ReauthSpec { Cheap = false, TriggerCodes = ["session_expired"] },
        },
        Resources =
        [
            new ResourceSpec
            {
                Id = BolAdapter.ReceiptsResource,
                Returns = ResourceShape.Receipt,
                Params =
                [
                    new ParamSpec { Key = "since", Type = ParamType.Date, Required = true },
                    new ParamSpec { Key = "until", Type = ParamType.Date },
                    // "raw" hands back each order exactly as bol's API sent it.
                    // Opt-in: it is a verbatim copy of somebody's purchase, so
                    // a caller has to ask before the connector carries one.
                    new ParamSpec { Key = "include", Type = ParamType.Enum, Values = ["items", "raw"], Multi = true },
                ],
                MaxHistoryDays = 730,
                TypicalDurationSeconds = 45,
                MaxRecordsPerFetch = 200,
            },
        ],
        Limits = new ProviderLimits
        {
            MinIntervalSeconds = 21_600,
            Concurrency = 1,
            MaxHistoryDays = 730,
            // A marketplace order is placed today and settles when the seller
            // ships it, so a row can surface days after the date it will
            // eventually carry. Any caller's `since` is widened by this,
            // because fetching strictly since the last sync loses a
            // late-appearing order permanently and invisibly.
            SettlementLagDays = 7,
        },
    };
}
