using Connector.Kit.Manifests;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.MagentoGuest;

/// <summary>
/// Copy keys this provider needs that <see cref="MessageKeys"/> does not
/// have yet.
///
/// They live here rather than in MessageKeys because six adapters are being
/// written against this tree at once and MessageKeys is a file all of them
/// would touch. Fold them in when the tree is quiet; the constants are what
/// the consuming app translates either way.
/// </summary>
internal static class MagentoGuestMessages
{
    public const string Notes = "connect.magento-guest.notes";
    public const string StepOrderReference = "connect.step.order_reference";
    public const string FieldShopUrl = "connect.field.shop_url";
    public const string FieldStoreCountry = "connect.field.store_country";
    public const string FieldOrderNumber = "connect.field.order_number";
    public const string FieldLastName = "connect.field.last_name";
    public const string FieldOrderToken = "connect.field.order_token";
}

internal static class MagentoGuestManifest
{
    public const int Version = 1;

    /// <summary>
    /// Countries whose zone <c>order_date</c>'s wall clock may belong to.
    /// The value is a country because <see cref="RetailZones"/> already maps
    /// one to a zone, including the Windows fallback an agent image with
    /// globalisation trimmed needs.
    /// </summary>
    public static readonly IReadOnlyList<string> StoreCountries = ["NL", "BE", "DE", "AT", "FR", "IT", "ES"];

    /// <summary>
    /// An order number as Magento mints it: an increment id, digits with
    /// optional separators, sometimes prefixed per store view.
    /// UNCONFIRMED as a constraint - it is deliberately loose, because a
    /// pattern that rejects a real order number is worse than one that
    /// accepts a typo the shop will reject anyway.
    /// </summary>
    public const string OrderNumberPattern = @"^[A-Za-z0-9][A-Za-z0-9._/-]{2,63}$";

    /// <summary>UNCONFIRMED, and loose for the same reason. Anything with an '@' and a dot after it.</summary>
    public const string EmailPattern = @"^[^@\s]+@[^@\s]+\.[A-Za-z]{2,}$";

    /// <summary>
    /// A shop's base URL. Accepts a bare host so "wibra.nl" is not rejected
    /// for missing a scheme the person never types; the adapter promotes it
    /// to https and refuses anything that resolves inside our own network.
    /// </summary>
    public const string ShopUrlPattern = @"^(?:https?://)?[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?(?::\d{2,5})?(?:/[^\s?#]*)?$";

    /// <summary>
    /// T1, and a provider that is honestly not a connection to an account.
    ///
    /// <b>Why this shape.</b> There is no session here and no sealed
    /// credential in the usual sense: a user supplies a reference to one
    /// order - the number, e-mail and surname that are all in the order
    /// confirmation mail, or the token from the 2.4.6+ status link - and the
    /// shop hands back that order. It is a per-order lookup wearing a
    /// connection's clothes, and the manifest has to say so somehow.
    ///
    /// Two shapes were possible. The reference could be a <em>resource
    /// parameter</em>, making the connection the shop and each fetch an
    /// order; or it could be an <em>auth field</em>, making the connection
    /// the order. The second is what is built here, for one decisive reason:
    /// <see cref="ParamSpec"/> has no <c>secret</c> flag and
    /// <see cref="FieldSpec"/> does. A guest order reference is a bearer
    /// capability - anyone holding it can read someone's name, address and
    /// shopping - and routing one through a query string would put it in
    /// every access log between here and the database with nothing in the
    /// platform able to mark it for redaction. So the reference goes through
    /// the credential door, which is the door with the lock on it.
    ///
    /// The consequence is stated rather than hidden: one connection is one
    /// order. Twenty orders is twenty connections. That is the true shape of
    /// what a guest lookup can do, and pretending otherwise would mean
    /// leaking capabilities to buy a tidier list.
    ///
    /// <b>Multi-tenant.</b> One adapter, every Magento shop. The host is
    /// <c>auth.config</c>, never a literal, because the platform is the
    /// provider and the shop is the setting.
    /// </summary>
    public static ProviderManifest Build() => new()
    {
        Id = MagentoGuestAdapter.ProviderId,
        Name = "Magento (guest order)",
        Kind = ProviderKind.Store,
        // The platform is global; this deployment's market is not. Country is
        // a single value in this contract, and NL is the market these shops
        // are being connected for - it is not a claim about Magento.
        Country = "NL",
        ManifestVersion = Version,

        // No browser, ever. The guest query is one unauthenticated POST, so
        // this can run inline in the control plane and no agent is needed -
        // which is exactly what makes it the cheapest provider in the
        // service to operate.
        Runtime = ProviderRuntime.Http,
        Agent = AgentRequirement.Inline,

        // True, and worth defending. A fetch here needs no human: the
        // reference does not expire, is not rotated by the shop, and is not
        // a session that can go stale. What ends a connection is the order
        // being deleted or the shop moving - both of which surface as a real
        // error rather than as a silent empty result.
        Unattended = true,
        SecretCustody = SecretCustody.Client,
        WebSupport = WebSupport.Ephemeral,
        LogoRef = "magento",
        NotesKey = MagentoGuestMessages.Notes,
        Auth = new AuthSpec
        {
            // The kit's enum has no member for "a capability reference", and
            // the kit is frozen. Password is the least-wrong of the nine:
            // one step, every field collected up front, no out-of-band code,
            // no redirect, no provider round trip in the middle. The flow
            // drives the consumer's UI and nothing else, and the UI this
            // needs is exactly a single form.
            Flow = AuthFlow.Password,

            // The shop, not the order. Config is sealed into the bundle and
            // handed back on every fetch, which is precisely what a
            // multi-tenant adapter needs: the host is a setting of the
            // connection, not a credential and not a per-call parameter.
            Config =
            [
                new FieldSpec
                {
                    Key = MagentoGuestAdapter.ShopUrlKey,
                    Type = FieldType.Text,
                    Secret = false,
                    Required = true,
                    LabelKey = MagentoGuestMessages.FieldShopUrl,
                    Pattern = ShopUrlPattern,
                    Autofill = "url",
                },
                new FieldSpec
                {
                    Key = MagentoGuestAdapter.StoreCountryKey,
                    Type = FieldType.Select,
                    Options = StoreCountries,
                    Secret = false,
                    // Optional: order_date is a wall clock in the shop's own
                    // configured zone, and most people cannot say what a
                    // shop's admin timezone is. Absent, the adapter uses its
                    // configured default. Being an hour out is recoverable;
                    // making the form unanswerable is not.
                    Required = false,
                    LabelKey = MagentoGuestMessages.FieldStoreCountry,
                },
            ],
            Steps =
            [
                new AuthStep
                {
                    Id = "order-reference",
                    LabelKey = MagentoGuestMessages.StepOrderReference,
                    // Every field is optional and the adapter enforces the
                    // rule the manifest cannot express: either the token, or
                    // all three of number, e-mail and surname. Marking the
                    // triple Required would lock out a user who has only the
                    // status link, and marking the token Required would lock
                    // out every shop older than 2.4.6. The alternative -
                    // two providers - would be a lie about there being two
                    // APIs when the source shows one resolver behind both.
                    Fields =
                    [
                        new FieldSpec
                        {
                            Key = MagentoGuestAdapter.OrderNumberInput,
                            Type = FieldType.Text,
                            // Not secret. It is the connection's display
                            // name, and redacting it would leave a user
                            // unable to tell two connections apart. The
                            // triple as a whole is the authorisation; no
                            // single part of it is a password, exactly as a
                            // username is not.
                            Secret = false,
                            Required = false,
                            LabelKey = MagentoGuestMessages.FieldOrderNumber,
                            Pattern = OrderNumberPattern,
                        },
                        new FieldSpec
                        {
                            Key = MagentoGuestAdapter.EmailInput,
                            Type = FieldType.Text,
                            Secret = false,
                            Required = false,
                            LabelKey = MessageKeys.FieldEmail,
                            Pattern = EmailPattern,
                            Autofill = "email",
                        },
                        new FieldSpec
                        {
                            Key = MagentoGuestAdapter.LastNameInput,
                            Type = FieldType.Text,
                            Secret = false,
                            Required = false,
                            LabelKey = MagentoGuestMessages.FieldLastName,
                            Autofill = "family-name",
                        },
                        new FieldSpec
                        {
                            Key = MagentoGuestAdapter.TokenInput,
                            // Text and not Password: it is pasted out of a
                            // link, never remembered, and masking a pasted
                            // value only stops the user checking their own
                            // paste. Secret all the same - it is a bearer
                            // capability, and redaction keys off this flag,
                            // not off the field's type.
                            Type = FieldType.Text,
                            Secret = true,
                            Required = false,
                            LabelKey = MagentoGuestMessages.FieldOrderToken,
                        },
                    ],
                },
            ],

            // None. There is no login page, so there is no captcha, no code
            // and no redirect - the guest query is a plain POST to an
            // endpoint Magento core leaves open. Declaring a challenge we
            // cannot raise would make a consumer build UI for a moment that
            // never comes.
            Challenges = [],

            Session = new SessionSpec
            {
                // A year. Neither Magento nor the shop states an expiry on a
                // guest reference - the order stays readable for as long as
                // it exists - so nothing here is measured. A year is the
                // longest we are willing to promise on no evidence, and the
                // cost of being wrong is one paste.
                TtlSeconds = 31_536_000,

                // The honest reading, and the one the validator's rule was
                // written for. Its message is "without one a human is needed
                // every time by definition" - the premise being that a
                // session which cannot be renewed will expire and strand the
                // user. This credential never expires and never rotates, so
                // no human is ever needed to keep it working, which is the
                // fact this field exists to carry.
                Refreshable = true,

                // Nothing rotates. There is no token to re-issue, so a
                // consumer that persisted a new bundle after every fetch
                // would be persisting the same bytes.
                RotatesOnUse = false,
            },

            // No silent renewal exists: when a reference stops working the
            // order is gone or the shop changed, and only a human can supply
            // a new one. Declaring reauth cheap would have the platform
            // queue a refresh job with nothing to refresh.
            Reauth = new ReauthSpec { Cheap = false, TriggerCodes = [] },
        },
        Resources =
        [
            new ResourceSpec
            {
                Id = MagentoGuestAdapter.ReceiptsResource,
                Returns = ResourceShape.Receipt,
                Params =
                [
                    // Declared exactly as every other shopping provider
                    // declares them, so a consumer needs no special case.
                    // They filter rather than page here: this resource
                    // returns the one order the connection is for, and a
                    // window that excludes it returns nothing. That is the
                    // caller's request being honoured, not data going
                    // missing.
                    new ParamSpec { Key = "since", Type = ParamType.Date, Required = true },
                    new ParamSpec { Key = "until", Type = ParamType.Date },
                    new ParamSpec { Key = "include", Type = ParamType.Enum, Values = ["items"], Multi = true },
                ],

                // Ten years, and the reason is the platform's own window
                // clamp: it pulls a caller's `since` forward to
                // now - MaxHistoryDays, and a user who deliberately
                // connected a three-year-old order would otherwise get an
                // empty result with nothing to explain it.
                MaxHistoryDays = 3_650,
                TypicalDurationSeconds = 5,

                // One order, one receipt. The field must be positive and
                // this is the true number, not a placeholder.
                MaxRecordsPerFetch = 1,
            },
        ],
        Limits = new ProviderLimits
        {
            // Six hours. A completed order barely changes, and the endpoint
            // belongs to a small shop that never agreed to serve us.
            MinIntervalSeconds = 21_600,
            Concurrency = 1,
            MaxHistoryDays = 3_650,
        },
    };
}
