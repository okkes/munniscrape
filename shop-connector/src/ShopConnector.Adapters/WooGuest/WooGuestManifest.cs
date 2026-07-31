using Connector.Kit.Manifests;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.WooGuest;

/// <summary>
/// Copy keys this provider needs that <see cref="MessageKeys"/> does not
/// have yet. Here rather than there because MessageKeys is a file every
/// adapter being written tonight would otherwise touch; fold them in once the
/// tree is quiet.
/// </summary>
internal static class WooGuestMessages
{
    public const string Notes = "connect.woo-guest.notes";
    public const string StepOrderReference = "connect.step.order_reference";
    public const string FieldShopUrl = "connect.field.shop_url";
    public const string FieldStoreCountry = "connect.field.store_country";
    public const string FieldOrderId = "connect.field.order_id";
    public const string FieldOrderKey = "connect.field.order_key";
    public const string FieldOrderDate = "connect.field.order_date";
}

internal static class WooGuestManifest
{
    public const int Version = 1;

    public static readonly IReadOnlyList<string> StoreCountries = ["NL", "BE", "DE", "AT", "FR", "IT", "ES"];

    /// <summary>CONFIRMED: the route regex is <c>'/order/(?P&lt;id&gt;[\d]+)'</c> - digits only.</summary>
    public const string OrderIdPattern = @"^[0-9]{1,12}$";

    /// <summary>
    /// WooCommerce mints an order key as <c>wc_order_</c> plus a random
    /// string. The prefix is not enforced in core's comparison - it is
    /// <c>hash_equals</c> against whatever the order holds - so the pattern
    /// stays permissive about the tail and only insists it looks like a key
    /// rather than a sentence. UNCONFIRMED as a constraint; loose on purpose,
    /// because rejecting a real key is worse than accepting a typo the shop
    /// will refuse anyway.
    /// </summary>
    public const string OrderKeyPattern = @"^[A-Za-z0-9_-]{8,128}$";

    public const string EmailPattern = @"^[^@\s]+@[^@\s]+\.[A-Za-z]{2,}$";

    /// <summary>A bare date, or a full instant when the caller has one - see the field's comment.</summary>
    public const string OrderDatePattern = @"^\d{4}-\d{2}-\d{2}([T ]\d{2}:\d{2}(:\d{2})?([.,]\d+)?(Z|[+-]\d{2}:?\d{2})?)?$";

    public const string ShopUrlPattern = @"^(?:https?://)?[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?(?::\d{2,5})?(?:/[^\s?#]*)?$";

    /// <summary>
    /// T1, and - like its Magento sibling - a per-order lookup rather than a
    /// connection to an account.
    ///
    /// <b>Why this shape.</b> The Store API exposes exactly one order route
    /// and it is authorised by two query parameters: the order key and the
    /// billing e-mail, both of which are in the order-received link and the
    /// confirmation mail. There is no list endpoint, no session and no
    /// password. The reference is modelled as an auth field rather than a
    /// resource parameter for the reason spelled out on the Magento
    /// manifest, and it is sharper here: <c>key</c> is a literal bearer
    /// secret compared with <c>hash_equals</c>, and <see cref="ParamSpec"/>
    /// has no <c>secret</c> flag to keep one out of a log.
    ///
    /// <b>The date.</b> Every other provider gets <c>purchased_at</c> from
    /// the payload. This one cannot: <c>OrderSchema::get_item_response()</c>
    /// returns no date field of any kind - confirmed by reading it, not by
    /// looking for one and giving up. So the reference carries the order
    /// date, which the confirmation mail states and which the future e-mail
    /// connector already holds as the message's own timestamp. A required
    /// field the user can answer beats a receipt dated by a guess.
    /// </summary>
    public static ProviderManifest Build() => new()
    {
        Id = WooGuestAdapter.ProviderId,
        Name = "WooCommerce (guest order)",
        Kind = ProviderKind.Store,
        Country = "NL",
        ManifestVersion = Version,

        // One unauthenticated GET. No browser, no agent, no egress
        // requirement - it runs inline in the control plane.
        Runtime = ProviderRuntime.Http,
        Agent = AgentRequirement.Inline,
        UnattendedFetch = true,
        SecretCustody = SecretCustody.Client,
        WebSupport = WebSupport.Ephemeral,
        LogoRef = "woocommerce",
        NotesKey = WooGuestMessages.Notes,
        Auth = new AuthSpec
        {
            // The least-wrong member of a frozen enum with no entry for a
            // capability reference: one step, everything collected up front,
            // nothing out of band.
            Flow = AuthFlow.Password,
            Config =
            [
                new FieldSpec
                {
                    Key = WooGuestAdapter.ShopUrlKey,
                    Type = FieldType.Text,
                    Secret = false,
                    Required = true,
                    LabelKey = WooGuestMessages.FieldShopUrl,
                    Pattern = ShopUrlPattern,
                    Autofill = "url",
                },
                new FieldSpec
                {
                    Key = WooGuestAdapter.StoreCountryKey,
                    Type = FieldType.Select,
                    Options = StoreCountries,
                    Secret = false,
                    Required = false,
                    LabelKey = WooGuestMessages.FieldStoreCountry,
                },
            ],
            Steps =
            [
                new AuthStep
                {
                    Id = "order-reference",
                    LabelKey = WooGuestMessages.StepOrderReference,
                    Fields =
                    [
                        new FieldSpec
                        {
                            Key = WooGuestAdapter.OrderIdInput,
                            Type = FieldType.Text,
                            Secret = false,
                            Required = true,
                            LabelKey = WooGuestMessages.FieldOrderId,
                            Pattern = OrderIdPattern,
                        },
                        new FieldSpec
                        {
                            Key = WooGuestAdapter.OrderKeyInput,
                            // Text rather than Password: it is pasted out of
                            // a link and never remembered, and masking a
                            // pasted value only stops the user checking their
                            // own paste. Secret regardless - this single
                            // string is the entire authorisation, and
                            // redaction keys off this flag rather than off
                            // the field's type.
                            Type = FieldType.Text,
                            Secret = true,
                            Required = true,
                            LabelKey = WooGuestMessages.FieldOrderKey,
                            Pattern = OrderKeyPattern,
                        },
                        new FieldSpec
                        {
                            Key = WooGuestAdapter.EmailInput,
                            Type = FieldType.Text,
                            Secret = false,
                            Required = true,
                            LabelKey = MessageKeys.FieldEmail,
                            Pattern = EmailPattern,
                            Autofill = "email",
                        },
                        new FieldSpec
                        {
                            // Required because the payload has no date and a
                            // receipt without a real timestamp is not a
                            // receipt. A bare day is accepted and read at
                            // midnight in the shop's country; an ISO instant
                            // is accepted too and preferred, which is what
                            // the e-mail connector will be able to supply
                            // from the message header.
                            Key = WooGuestAdapter.OrderDateInput,
                            Type = FieldType.Date,
                            Secret = false,
                            Required = true,
                            LabelKey = WooGuestMessages.FieldOrderDate,
                            Pattern = OrderDatePattern,
                        },
                    ],
                },
            ],

            // None. There is no login page here, so there is nothing to
            // challenge - and a declared challenge a provider never raises
            // makes a consumer build a screen for a moment that never comes.
            Challenges = [],
            Session = new SessionSpec
            {
                // A year, chosen rather than measured: an order key lives as
                // long as the order unless a shop regenerates it, and
                // nothing states an expiry. The cost of being wrong is one
                // paste.
                TtlSeconds = 31_536_000,

                // No human is ever needed to keep this usable, which is what
                // this field carries. Nothing expires and nothing rotates.
                Refreshable = true,
                RotatesOnUse = false,
            },

            // When a key stops working the order is gone or the shop
            // regenerated it, and only a human can supply the new one.
            Reauth = new ReauthSpec { Cheap = false, TriggerCodes = [] },
        },
        Resources =
        [
            new ResourceSpec
            {
                Id = WooGuestAdapter.ReceiptsResource,
                Returns = ResourceShape.Receipt,
                Params =
                [
                    new ParamSpec { Key = "since", Type = ParamType.Date, Required = true },
                    new ParamSpec { Key = "until", Type = ParamType.Date },
                    new ParamSpec { Key = "include", Type = ParamType.Enum, Values = ["items"], Multi = true },
                ],
                // Ten years, so the platform's window clamp cannot silently
                // exclude an old order the user deliberately connected.
                MaxHistoryDays = 3_650,
                TypicalDurationSeconds = 5,
                MaxRecordsPerFetch = 1,
            },
        ],
        Limits = new ProviderLimits
        {
            MinIntervalSeconds = 21_600,
            Concurrency = 1,
            MaxHistoryDays = 3_650,
        },
    };
}
