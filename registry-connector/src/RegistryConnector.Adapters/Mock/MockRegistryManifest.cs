using Connector.Kit.Challenges;
using Connector.Kit.Manifests;

namespace RegistryConnector.Adapters.Mock;

internal static class MockRegistryManifest
{
    /// <summary>
    /// An hour. Short on purpose: a registry session is not a shopping
    /// session, and a consumer that cached one for a month would be promising
    /// a sync it cannot perform without a human.
    /// </summary>
    public const int SessionTtlSeconds = 3_600;

    public static ProviderManifest Build(string id, string name, bool twoFactor) => new()
    {
        Id = id,
        Name = name,
        Kind = ProviderKind.Registry,
        Country = "NL",
        ManifestVersion = 1,
        Runtime = ProviderRuntime.Http,
        Agent = AgentRequirement.Inline,

        // False when a code is needed per sync, and this is the field doing
        // real work: it is what stops a consumer offering scheduled syncing
        // to somebody who would have to be awake for every one of them.
        UnattendedFetch = !twoFactor,
        SecretCustody = SecretCustody.Client,
        WebSupport = WebSupport.Ephemeral,
        LogoRef = "mock",
        NotesKey = "connect.mock.notes",

        // The point of the 2FA variant. Username and password are worth
        // keeping because they never change; the six digits are worth asking
        // for because they change every thirty seconds.
        OffersCredentialStore = twoFactor,

        Auth = new AuthSpec
        {
            Flow = twoFactor ? AuthFlow.PasswordTotp : AuthFlow.Password,
            Steps =
            [
                new AuthStep
                {
                    Id = "credentials",
                    LabelKey = "connect.step.credentials",
                    Fields =
                    [
                        new FieldSpec
                        {
                            Key = "username",
                            Type = FieldType.Text,
                            Required = true,
                            LabelKey = "connect.field.username",
                        },
                        new FieldSpec
                        {
                            Key = "password",
                            Type = FieldType.Password,
                            Secret = true,
                            Required = true,
                            LabelKey = "connect.field.password",
                        },
                    ],
                },
            ],
            Challenges = twoFactor ? [ChallengeType.MfaCode] : [],
            Session = new SessionSpec
            {
                TtlSeconds = SessionTtlSeconds,

                // The two halves of one fact, and the validator is right to
                // insist they agree: "unattended_fetch: true requires a
                // refreshable session - without one a human is needed every
                // time by definition". A registry guarded by an authenticator
                // has nothing to refresh, because the next sync signs in again
                // from the stored password and six new digits.
                Refreshable = !twoFactor,

                // Nothing is ever re-issued either way. Stated because the
                // field defaults to true and a consumer would otherwise watch
                // for a bundle that never comes.
                RotatesOnUse = false,
            },
            Reauth = new ReauthSpec { Cheap = !twoFactor, TriggerCodes = ["session_expired"] },
        },
        Resources =
        [
            new ResourceSpec
            {
                Id = MockRegistryAdapter.CreditsResource,
                Returns = ResourceShape.CreditRegistration,

                // No `since`. A registry states what is true NOW, not what
                // happened in a window - asking it for "credits since March"
                // is a question it has no way to answer, and a resource that
                // declared the parameter would be inviting one.
                Params = [],
            },
        ],
        Limits = new ProviderLimits { MinIntervalSeconds = 3_600, Concurrency = 1 },
    };
}
