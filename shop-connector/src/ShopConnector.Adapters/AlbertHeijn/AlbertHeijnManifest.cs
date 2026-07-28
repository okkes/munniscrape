using Connector.Kit.Challenges;
using Connector.Kit.Manifests;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.AlbertHeijn;

internal static class AlbertHeijnManifest
{
    /// <summary>
    /// Bumped to 2 when the login stopped being a pasted redirect and became
    /// an e-mail and a password. Sealed into every bundle as AAD, so a bundle
    /// minted under version 1 is rejected outright rather than reinterpreted
    /// against a contract it was never issued for.
    /// </summary>
    public const int Version = 2;

    /// <summary>
    /// T2. A browser drives the OAuth login once and the refresh token serves
    /// every fetch after that with no browser, no human and no stored
    /// password.
    ///
    /// The predecessor made the human read a failed <c>appie://</c>
    /// navigation out of their browser's console and paste it back. That is
    /// unusable anywhere, and impossible on a phone, so the agent now types
    /// the credentials and captures the redirect itself.
    /// </summary>
    /// <param name="liveLogin">
    /// When true the manifest declares <see cref="AuthFlow.RemoteBrowser"/> and
    /// NO fields, because the human signs in on Albert Heijn's own page
    /// streamed to their device and this platform never sees the password.
    ///
    /// Built from the option rather than declared twice, so the flow, the
    /// steps and the challenge list cannot drift apart from what the adapter
    /// actually does - a manifest promising a password form for a login that
    /// never asks for one is a consumer drawing two boxes nobody can fill.
    /// </param>
    public static ProviderManifest Build(bool liveLogin = true) => new()
    {
        Id = AlbertHeijnAdapter.ProviderId,
        Name = "Albert Heijn",
        Kind = ProviderKind.Store,
        Country = "NL",
        ManifestVersion = Version,
        Runtime = ProviderRuntime.BrowserOnce,
        Agent = new AgentRequirement
        {
            Required = true,
            Class = AgentClass.Pooled,
            // login.ah.nl is fronted by hCaptcha; a datacenter range fails
            // that test before the credentials are even considered.
            Egress = new EgressRequirement { Country = "NL", Kind = "residential" },
        },
        Unattended = true,               // the refresh token works headlessly
        SecretCustody = SecretCustody.Client,
        WebSupport = WebSupport.Ephemeral,
        LogoRef = "ah",
        NotesKey = MessageKeys.AhNotes,
        Auth = new AuthSpec
        {
            Flow = liveLogin ? AuthFlow.RemoteBrowser : AuthFlow.Password,

            // Empty under the live login, and the validator enforces that it
            // stays empty: there is no form, because there is nothing for a
            // consumer to ask. The human types their e-mail and password into
            // Albert Heijn's own page, which they can see.
            Steps = liveLogin ? [] :
            [
                new AuthStep
                {
                    Id = "credentials",
                    LabelKey = MessageKeys.StepCredentials,
                    Fields =
                    [
                        // An e-mail address, not a member number or a phone.
                        // Getting this wrong in the consumer's form costs the
                        // user a failed attempt against a defended page.
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
                    ],
                },
            ],
            // Image for a plain captcha, which is a picture and a text box
            // and can be relayed. AppApproval for hCaptcha, which cannot: the
            // widget is passed in the agent's own window by whoever is
            // sitting at it, and the challenge exists only to ask them and
            // wait - which is exactly the passive contract. Redirect stays
            // declared as the last resort: when the agent's browser cannot
            // get through, the human finishing on AH's own page in their own
            // browser still can.
            //
            // Adding a challenge type does not change what a bundle means, so
            // ManifestVersion deliberately stays at 2: bumping it would
            // invalidate every live AH connection to announce a new question.
            //
            // Under the live login the list is LiveView and Redirect: the
            // whole page is streamed, so a captcha inside it needs no separate
            // relay - the human is already looking at it and already has a
            // pointer on it. Redirect stays as the last resort either way.
            Challenges = liveLogin
                ? [ChallengeType.LiveView, ChallengeType.Redirect]
                : [ChallengeType.Image, ChallengeType.AppApproval, ChallengeType.Redirect],
            Session = new SessionSpec { TtlSeconds = 7_776_000, Refreshable = true, RotatesOnUse = true },
            Reauth = new ReauthSpec { Cheap = true, TriggerCodes = ["session_expired"] },
        },
        Resources =
        [
            new ResourceSpec
            {
                Id = AlbertHeijnAdapter.ReceiptsResource,
                Returns = ResourceShape.Receipt,
                Params =
                [
                    new ParamSpec { Key = "since", Type = ParamType.Date, Required = true },
                    new ParamSpec { Key = "until", Type = ParamType.Date },
                    new ParamSpec { Key = "include", Type = ParamType.Enum, Values = ["items"], Multi = true },
                ],
                TypicalDurationSeconds = 25,
                MaxRecordsPerFetch = 200,
            },
        ],
    };
}
