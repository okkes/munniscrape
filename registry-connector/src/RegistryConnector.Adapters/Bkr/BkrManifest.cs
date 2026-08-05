using Connector.Kit.Challenges;
using Connector.Kit.Manifests;

using RegistryConnector.Adapters.Support;

namespace RegistryConnector.Adapters.Bkr;

internal static class BkrManifest
{
    public const int Version = 1;

    /// <summary>
    /// An hour. BKR states a standing position rather than a feed, and its own
    /// portal changes when a credit opens or closes - a handful of times a
    /// year. A consumer syncing this hourly is asking a human for six digits
    /// hourly, which is the surest way to have the connection turned off.
    /// </summary>
    public const int SessionTtlSeconds = 3_600;

    public static ProviderManifest Build() => new()
    {
        Id = BkrAdapter.ProviderId,
        Name = "BKR",
        Kind = ProviderKind.Registry,
        Country = "NL",
        ManifestVersion = Version,

        // T3. Sign-in is Azure AD B2C behind a second factor, and the portal
        // is server-rendered - so a browser drives the login and plain HTTP
        // could not, but nothing here needs a browser to stay open afterwards.
        Runtime = ProviderRuntime.BrowserInteractive,
        Agent = new AgentRequirement
        {
            Required = true,
            Class = AgentClass.Pooled,
            Egress = new EgressRequirement { Country = "NL", Kind = "residential" },
        },

        // FALSE, and it stays false whatever the user stores.
        //
        // The seed below can remove the six-digit prompt, and it is tempting to
        // read that as "so it can sync unattended now". It cannot, and must
        // not: the credential lives in a bundle the user's own device holds,
        // so there is nobody to hand it over at 3am. A registry that signed
        // itself in with nobody present is an account takeover with a cron
        // entry, and the whole custody model exists to make that impossible
        // rather than merely discouraged.
        UnattendedFetch = false,
        LoginNeedsHeadedAgent = false,

        // The password is worth keeping: it does not change, and retyping it
        // for a monthly sync is the friction that stops people syncing.
        OffersCredentialStore = true,

        SecretCustody = SecretCustody.Client,
        WebSupport = WebSupport.Ephemeral,
        LogoRef = "bkr",
        NotesKey = MessageKeys.BkrNotes,

        Auth = new AuthSpec
        {
            Flow = AuthFlow.PasswordTotp,
            Steps =
            [
                new AuthStep
                {
                    Id = "credentials",
                    LabelKey = MessageKeys.StepCredentials,

                    // ALL THREE OPTIONAL, and that is the whole design. What
                    // the user supplies decides how much of the sign-in they
                    // have to watch:
                    //
                    //   nothing            -> BKR's own page is streamed from
                    //                         its first screen
                    //   username+password  -> both are typed in, then the page
                    //                         is streamed for the second
                    //                         factor - which covers a texted
                    //                         code, an authenticator code, and
                    //                         any screen offering a choice
                    //                         between them
                    //   ...plus the seed   -> the code is computed here and
                    //                         nobody is disturbed at all
                    //
                    // A required field would make the first two impossible to
                    // offer, and the first is what a first connect looks like.
                    Fields =
                    [
                        new FieldSpec
                        {
                            Key = "username",
                            Type = FieldType.Text,
                            Required = false,
                            LabelKey = MessageKeys.FieldEmail,
                        },
                        new FieldSpec
                        {
                            Key = "password",
                            Type = FieldType.Password,
                            Secret = true,
                            Required = false,
                            LabelKey = MessageKeys.FieldPassword,
                        },

                        // The one that needs explaining before it is filled
                        // in, not after - see connect.bkr.totp.
                        //
                        // Secret, obviously: it is the seed behind every code
                        // the account will ever accept. Storing it means this
                        // connection alone can pass both factors, which is a
                        // real reduction in what two-factor was bought for.
                        // It is optional and unset by default, and the login
                        // works without it by asking.
                        new FieldSpec
                        {
                            Key = "totp",
                            Type = FieldType.Password,
                            Secret = true,
                            Required = false,
                            LabelKey = MessageKeys.BkrTotpSecret,
                        },
                    ],
                },
            ],

            // LiveView answers everything the typed path cannot: a texted
            // code, an authenticator code, a choice between the two, a
            // password reset, a consent screen nobody has met yet. MfaCode is
            // declared beside it because a stored seed is not a guarantee -
            // a clock that has drifted, or a seed that was mistyped, has to
            // be answerable by a human rather than fatal.
            Challenges = [ChallengeType.LiveView, ChallengeType.MfaCode],

            Session = new SessionSpec
            {
                TtlSeconds = SessionTtlSeconds,

                // Nothing to refresh. B2C hands the portal an id_token by
                // form_post and the portal keeps a cookie; the browser is
                // never given a refresh token, so the next sync signs in
                // again from the stored password and a new code.
                Refreshable = false,
                RotatesOnUse = false,
            },
            Reauth = new ReauthSpec { Cheap = false, TriggerCodes = ["session_expired"] },
        },

        Resources =
        [
            new ResourceSpec
            {
                Id = BkrAdapter.CreditsResource,
                Returns = ResourceShape.CreditRegistration,

                // No `since` and no `until`. A register states what is true
                // NOW; asking it for "credits since March" is a question it
                // has no way to answer, and declaring the parameter would
                // invite one.
                Params = [],
                MaxRecordsPerFetch = 200,
            },
        ],

        Limits = new ProviderLimits { MinIntervalSeconds = 3_600, Concurrency = 1 },
    };
}
