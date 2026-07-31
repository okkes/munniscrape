using Connector.Kit.Challenges;
using Connector.Kit.Manifests;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.Picnic;

/// <summary>
/// Copy keys Picnic needs that no other provider does.
///
/// Kept here rather than in the shared <see cref="MessageKeys"/> table on
/// purpose: several adapters are being written against that one file at the
/// same time, and a provider that owns two strings does not need to be a
/// merge conflict. The shared keys that already exist are reused verbatim.
/// </summary>
internal static class PicnicMessageKeys
{
    public const string Notes = "connect.picnic.notes";

    public const string Country = "connect.picnic.country";
}

internal static class PicnicManifest
{
    public const int Version = 1;

    /// <summary>The countries Picnic operates in, and therefore the host labels that exist.</summary>
    public static readonly IReadOnlyList<string> Countries = ["NL", "DE", "FR"];

    /// <summary>
    /// UNCONFIRMED, and the only number in this manifest that is a judgement
    /// rather than a finding.
    ///
    /// Picnic states no token lifetime and the reference never expires one, so
    /// thirty days is a deliberate under-claim. In practice the token is
    /// re-issued on every response (see <see cref="SessionSpec.RotatesOnUse"/>),
    /// so a connection synced anywhere near the six-hour floor renews itself
    /// indefinitely and never reaches this number at all - it only bounds how
    /// long a bundle may sit idle. Under-claiming costs a re-login the user did
    /// not strictly need; over-claiming makes the consumer promise a month of
    /// silent syncing and then fail, which is the worse of the two.
    /// </summary>
    public const int SessionTtlSeconds = 2_592_000;

    /// <summary>
    /// T1 - the first provider here that needs no browser and no agent at all.
    ///
    /// Picnic's mobile API is plain JSON over HTTPS with no edge protection, no
    /// captcha and no OAuth dance: a password hash goes up, a token comes back
    /// in a response header, and every call after that is a header. There is
    /// nothing for Chromium to do, so <c>runtime: http</c> with an inline agent
    /// is not an optimisation - it is the honest description, and it is what
    /// lets this provider run in the control plane's own process while every
    /// other store in the catalogue waits for a pooled browser.
    /// </summary>
    public static ProviderManifest Build() => new()
    {
        Id = PicnicAdapter.ProviderId,
        Name = "Picnic",
        Kind = ProviderKind.Store,
        Country = "NL",
        ManifestVersion = Version,

        // The whole point of this entry. `http` is the only runtime the
        // validator lets run inline, and Picnic genuinely qualifies.
        Runtime = ProviderRuntime.Http,
        Agent = AgentRequirement.Inline,

        // No browser, no human, and a token that renews itself by being used:
        // scheduled sync is offerable, and saying so is true.
        UnattendedFetch = true,
        // None, and not because LogoutAsync is unimplemented - it is, and it
        // POSTs /user/logout. It cannot run. Disconnect enqueues the logout job
        // with no material, because custody is Client: the token lives in the
        // sealed bundle on the user's device and the control plane does not
        // have it. PicnicSession then throws session_expired and the adapter's
        // own best-effort catch swallows it, silently, every time.
        //
        // Declaring Session here would be the manifest promising a revocation
        // that has never happened. The endpoint would first have to open the
        // bundle the consuming app already sends it - see
        // DisconnectLogoutTests.A_logout_job_is_handed_no_credential_to_log_out_with.
        Logout = LogoutSupport.None,
        SecretCustody = SecretCustody.Client,
        WebSupport = WebSupport.Ephemeral,
        LogoRef = "picnic",
        NotesKey = PicnicMessageKeys.Notes,
        Auth = new AuthSpec
        {
            // Deliberately not `password_sms`. Picnic's second factor is a
            // per-ACCOUNT setting, not a property of the provider: the login
            // response says `second_factor_authentication_required` and for
            // most accounts it says false. Declaring password_sms tells every
            // user to expect a text message, and the ones who never get one are
            // left waiting on a phone that will not ring. The MfaCode challenge
            // below carries the truth on the logins where it actually happens,
            // with `delivery: "sms"` on it - which is confirmed, because SMS is
            // the only channel /user/2fa/generate is asked for.
            Flow = AuthFlow.Password,
            Config =
            [
                new FieldSpec
                {
                    Key = "country",
                    Type = FieldType.Select,
                    Secret = false,
                    Required = true,
                    LabelKey = PicnicMessageKeys.Country,
                    // The country is the host label AND the Accept-Language, so
                    // it is neither a credential nor a challenge - it is a
                    // setting, and it belongs in the section the manifest has
                    // for settings rather than in a hack in the consuming app.
                    Options = Countries,
                },
            ],
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
                            // The password is hashed before it leaves this
                            // process, which makes it no less of a secret: the
                            // hash is a password-equivalent to Picnic, and
                            // redaction keys off exactly this flag.
                            Secret = true,
                            Required = true,
                            LabelKey = MessageKeys.FieldPassword,
                            Autofill = "current-password",
                        },
                    ],
                },
            ],
            // MfaCode is the SMS code, and it is confirmed rather than
            // defensive - the endpoints, the body and the 204 are all read from
            // the reference. Nothing else is declared: Picnic has no captcha,
            // no redirect and no app approval, and announcing challenges a
            // provider cannot raise is its own kind of lie.
            Challenges = [ChallengeType.MfaCode],
            Session = new SessionSpec
            {
                TtlSeconds = SessionTtlSeconds,
                // There is no refresh grant, and this is still true: every
                // response may carry a fresh `x-picnic-auth`, which the client
                // swaps in. The session renews by being used, with no human -
                // which is exactly what this field asks.
                Refreshable = true,
                RotatesOnUse = true,
            },
            Reauth = new ReauthSpec { Cheap = true, TriggerCodes = ["session_expired"] },
        },
        Resources =
        [
            new ResourceSpec
            {
                Id = PicnicAdapter.ReceiptsResource,
                Returns = ResourceShape.Receipt,
                Params =
                [
                    new ParamSpec { Key = "since", Type = ParamType.Date, Required = true },
                    new ParamSpec { Key = "until", Type = ParamType.Date },
                    new ParamSpec { Key = "include", Type = ParamType.Enum, Values = ["items"], Multi = true },
                ],
                MaxHistoryDays = 730,
                // The summary is one call; the line items are one call per
                // delivery on top of it, so asking for items on a long window
                // is where the time goes.
                TypicalDurationSeconds = 45,
                MaxRecordsPerFetch = 100,
            },
        ],
        Limits = new ProviderLimits
        {
            MinIntervalSeconds = 21_600,
            Concurrency = 1,
            MaxHistoryDays = 730,
            // Picnic is a delivery service: an order is placed days before the
            // groceries arrive and before the direct debit settles. A caller
            // that fetches strictly since its last sync would keep missing the
            // orders that were placed inside the old window and only settled
            // after it, permanently and invisibly - so any `since` is widened
            // by a week.
            SettlementLagDays = 7,
        },
    };
}
