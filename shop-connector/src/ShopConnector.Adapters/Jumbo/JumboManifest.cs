using Connector.Kit.Challenges;
using Connector.Kit.Manifests;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.Jumbo;

internal static class JumboManifest
{
    /// <summary>
    /// Still 1. The corrections that landed with the confirmed research
    /// changed what the adapter reads and what a total means - not the shape
    /// of the session material, which is still a browser storage state plus a
    /// device id. Bumping this would invalidate every issued bundle and force
    /// a fresh Auth0 login behind Akamai for nobody's benefit.
    /// </summary>
    public const int Version = 1;

    /// <summary>The cookie's real life. Not a target, a measurement.</summary>
    public const int SessionTtlSeconds = 86_400;

    /// <summary>
    /// T3, and the numbers here are the whole point of the manifest.
    ///
    /// Jumbo's credential is a browser cookie with a ~24 hour life that this
    /// connector cannot renew, so a human signs in about once a day. A
    /// manifest claiming thirty days and a refreshable session would make the
    /// consuming app promise a month of silent syncing and then fail every day
    /// after the first.
    ///
    /// The declaration is unchanged; the <em>reason</em> behind it is
    /// corrected. It used to read "Jumbo has no refresh grant of any kind",
    /// and that is false: <c>auth.jumbo.com</c>'s discovery document
    /// advertises <c>refresh_token</c>, the tenant supports <c>offline_access</c>
    /// and PKCE S256, and the web client's own <c>/authorize</c> call asks for
    /// <c>offline_access</c> by name. What is true is narrower and is why
    /// <c>refreshable: false</c> still stands: that client redirects to a
    /// server-side callback, Jumbo's own backend exchanges the code and keeps
    /// the tokens, and the browser is handed nothing but a session cookie. So
    /// there is a refresh path and we are not on it. Written down properly
    /// because the old wording would have stopped anyone from ever revisiting
    /// a mobile-style public client, which is the one route that would hand
    /// over a real refresh token and move this provider to T2.
    /// </summary>
    public static ProviderManifest Build() => new()
    {
        Id = JumboAdapter.ProviderId,
        Name = "Jumbo",
        Kind = ProviderKind.Store,
        Country = "NL",
        ManifestVersion = Version,
        Runtime = ProviderRuntime.BrowserInteractive,
        Agent = new AgentRequirement
        {
            Required = true,
            Class = AgentClass.Pooled,
            // CONFIRMED-live: www.jumbo.com is fronted by Akamai (akaas_as,
            // ak_bmsc) and the login is Auth0 Universal Login. A datacenter
            // range is a high-risk signal before the credentials are even
            // considered.
            Egress = new EgressRequirement { Country = "NL", Kind = "residential" },
        },
        Unattended = false,              // a human signs in roughly daily
        SecretCustody = SecretCustody.Client,
        WebSupport = WebSupport.Ephemeral,
        LogoRef = "jumbo",
        NotesKey = MessageKeys.JumboNotes,
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
                            LabelKey = MessageKeys.FieldEmail,
                            Autofill = "username",
                        },
                        new FieldSpec
                        {
                            Key = "password",
                            Type = FieldType.Password,
                            Secret = true,
                            LabelKey = MessageKeys.FieldPassword,
                            Autofill = "current-password",
                        },
                    ],
                },
            ],
            // Image for a plain captcha, which can be photographed and typed
            // back. AppApproval for the interactive widgets Auth0 Universal
            // Login actually ships - hCaptcha, reCAPTCHA and Turnstile assets
            // are all CONFIRMED-live on the page, and Auth0 activates one on
            // risk score. None of those can be relayed: a widget wants drags
            // and tile clicks and mints its token in its own JavaScript, so
            // the human passes it in the window the agent opened and the
            // challenge exists only to ask them and wait. MfaCode because
            // Auth0 supports a one-time code and Jumbo may have it on.
            //
            // A consumer with no UI for a challenge its provider raises
            // strands the user at the worst possible moment, so all three are
            // declared rather than discovered.
            Challenges = [ChallengeType.Image, ChallengeType.AppApproval, ChallengeType.MfaCode],
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
                Id = JumboAdapter.ReceiptsResource,
                Returns = ResourceShape.Receipt,
                Params =
                [
                    new ParamSpec { Key = "since", Type = ParamType.Date, Required = true },
                    new ParamSpec { Key = "until", Type = ParamType.Date },
                    new ParamSpec { Key = "include", Type = ParamType.Enum, Values = ["items"], Multi = true },
                ],
                // Longer than it was: an in-store receipt costs a second round
                // trip before it can state a total at all, which the previous
                // design did not budget for.
                TypicalDurationSeconds = 90,
                MaxRecordsPerFetch = 200,
            },
        ],
        Limits = new ProviderLimits
        {
            MinIntervalSeconds = 21_600,
            Concurrency = 1,

            // OBSERVED 2026-07-28: Jumbo returned an online order dated
            // 2024-12-19 - 586 days old - through the ordinary orders query.
            // The platform default is 365, and inheriting it silently clamped
            // the caller's `since` and hid the ONLY order on a live account:
            // the fetch reported zero receipts, complete, with no error, which
            // is the most expensive kind of wrong answer.
            //
            // Three years is the smallest round figure that comfortably covers
            // what was seen. Jumbo's real retention is still UNCONFIRMED - all
            // that is established is that it exceeds 586 days.
            MaxHistoryDays = 1_095,
        },
    };
}
