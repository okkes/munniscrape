using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Connector.Kit.Adapters;
using Connector.Kit.Errors;
using Connector.Kit.Jobs;
using Connector.Kit.Manifests;
using Connector.Kit.Normalization;
using Connector.Kit.Security;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.MagentoGuest;

/// <summary>
/// One order reference: either the triple from the confirmation mail, or the
/// token from a 2.4.6+ status link.
///
/// Both reach the same resolver. <c>Resolver/GuestOrder.php</c> decrypts the
/// token into exactly <c>[number, email, lastname]</c>, so the token is a
/// shorter way to say the same thing rather than a wider capability - which
/// is why the triple is the primary path and neither is preferred over the
/// other once one of them is present.
/// </summary>
internal sealed record MagentoGuestReference
{
    public string? Number { get; init; }

    public string? Email { get; init; }

    public string? LastName { get; init; }

    public string? Token { get; init; }

    public bool HasToken => !string.IsNullOrWhiteSpace(Token);

    public bool HasTriple =>
        !string.IsNullOrWhiteSpace(Number) &&
        !string.IsNullOrWhiteSpace(Email) &&
        !string.IsNullOrWhiteSpace(LastName);

    /// <summary>What goes into the sealed bundle. Opaque to everything but this adapter.</summary>
    public IReadOnlyDictionary<string, string> ToExtra()
    {
        var extra = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Number is { Length: > 0 }) extra[MagentoGuestAdapter.OrderNumberInput] = Number;
        if (Email is { Length: > 0 }) extra[MagentoGuestAdapter.EmailInput] = Email;
        if (LastName is { Length: > 0 }) extra[MagentoGuestAdapter.LastNameInput] = LastName;
        if (Token is { Length: > 0 }) extra[MagentoGuestAdapter.TokenInput] = Token;
        return extra;
    }

    public static MagentoGuestReference FromInputs(IReadOnlyDictionary<string, string> source) => new()
    {
        Number = Value(source, MagentoGuestAdapter.OrderNumberInput),
        Email = Value(source, MagentoGuestAdapter.EmailInput),
        LastName = Value(source, MagentoGuestAdapter.LastNameInput),
        Token = Value(source, MagentoGuestAdapter.TokenInput),
    };

    private static string? Value(IReadOnlyDictionary<string, string> source, string key) =>
        source.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}

/// <summary>
/// Magento / Adobe Commerce, guest order lookup. T1 - no login, no captcha,
/// no browser, no password, and no host of its own.
///
/// This adapter is a different animal from every other one here. There is no
/// account to sign into: <c>guestOrder</c> and <c>guestOrderByToken</c> are
/// core Magento, on by default in every install, and they hand back a
/// complete <c>CustomerOrder</c> - line items, totals, shipping, discounts -
/// to anybody who can state the order number, the billing e-mail and the
/// billing surname. All three are in the order confirmation e-mail. So is
/// the token, on 2.4.6 and later.
///
/// That makes it the cheapest receipt pipeline in the project and the
/// natural downstream of the e-mail connector: the mailbox supplies the
/// reference, this turns it into a normalized receipt, and nothing in
/// between ever sees a password.
///
/// Two limits are worth knowing before anyone builds on it, both CONFIRMED
/// from source rather than guessed:
/// <list type="bullet">
/// <item>an order placed while signed in to the shop is refused
/// permanently - <c>getCustomerId()</c> set means "Please login to view the
/// order", and no reference will ever unlock it;</item>
/// <item>the lookup is per order. There is no list endpoint on the guest
/// path, and there is no way to enumerate from one order to the next.</item>
/// </list>
/// </summary>
public sealed class MagentoGuestAdapter : IProviderAdapter
{
    public const string ProviderId = "magento-guest";
    public const string ReceiptsResource = "receipts";

    public const string ShopUrlKey = "shop_base_url";
    public const string StoreCountryKey = "store_country";

    public const string OrderNumberInput = "order_number";
    public const string EmailInput = "email";
    public const string LastNameInput = "lastname";
    public const string TokenInput = "order_token";

    private static readonly ProviderManifest Manifest = MagentoGuestManifest.Build();

    private readonly MagentoGuestOptions _options;

    public MagentoGuestAdapter(MagentoGuestOptions? options = null, TimeProvider? time = null)
    {
        _options = options ?? new MagentoGuestOptions();

        // A clock is taken for symmetry with every other adapter's
        // constructor, and deliberately not stored: nothing here expires,
        // polls or times out on its own, so holding one would only invite a
        // future edit to invent a deadline this provider does not have.
        _ = time;
    }

    public ProviderManifest Describe() => Manifest;

    /// <summary>
    /// Validates the reference by using it once, and seals it.
    ///
    /// A connect step that only stored what it was handed would move every
    /// possible failure - a typo, an account order, a shop that is not
    /// Magento - into a background fetch nobody is watching. Spending one
    /// request here means the user learns at the moment they can still fix
    /// it, and it costs the shop exactly the request the first sync would
    /// have made anyway.
    /// </summary>
    public async Task<LoginResult> LoginAsync(IJobContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var shop = ShopBaseUrl.Parse(Config(ctx, ShopUrlKey), ProviderId, ShopUrlKey, _options.AllowPlainHttp);
        var reference = RequireReference(MagentoGuestReference.FromInputs(ctx.Inputs));

        ctx.Progress(JobStep.Authenticating);

        using var document = await QueryAsync(ctx, shop, reference, ct).ConfigureAwait(false);
        var order = MagentoGuestOrderParser.RequireOrder(document.RootElement, FieldFor(reference));

        // Parsed rather than merely fetched: a payload we cannot turn into a
        // receipt is a broken connection, and saying so now beats an empty
        // sync tomorrow.
        var receipt = MagentoGuestOrderParser.Parse(
            order, _options, ctx.SessionId, ShopBaseUrl.MerchantId(shop), Zone(ctx));

        ctx.Progress(JobStep.Finalizing);

        return new LoginResult
        {
            // No token, no cookie, no storage state - the credential is the
            // reference itself, and Extra is where a provider-specific one
            // belongs. It is sealed with everything else in the bundle.
            Material = new SessionMaterial { Extra = reference.ToExtra() },
            Account = new ProviderAccount
            {
                // An identifier, not prose: the shop and the order, so a user
                // with several connected orders can tell them apart.
                DisplayName = $"{ShopBaseUrl.MerchantId(shop)} #{receipt.ExternalId}",
                ExternalId = receipt.ExternalId,
            },
            // Deliberately unset. Nothing upstream states an expiry, so the
            // manifest's TTL - a year, chosen rather than measured - stands.
        };
    }

    public async Task<FetchResult> FetchAsync(IJobContext ctx, ResourceRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(request);

        if (!string.Equals(request.ResourceId, ReceiptsResource, StringComparison.Ordinal))
        {
            throw ConnectorException.Unsupported($"{ProviderId}: no resource '{request.ResourceId}'");
        }

        var shop = ShopBaseUrl.Parse(Config(ctx, ShopUrlKey), ProviderId, ShopUrlKey, _options.AllowPlainHttp);

        var stored = ctx.Material?.Extra;
        if (stored is null || stored.Count == 0)
        {
            throw ConnectorException.SessionExpired($"{ProviderId}: the bundle carries no order reference");
        }

        var reference = RequireReference(MagentoGuestReference.FromInputs(stored));

        ctx.Progress(JobStep.Downloading);
        using var document = await QueryAsync(ctx, shop, reference, ct).ConfigureAwait(false);

        ctx.Progress(JobStep.Parsing);
        var order = MagentoGuestOrderParser.RequireOrder(document.RootElement, FieldFor(reference));
        var receipt = MagentoGuestOrderParser.Parse(
            order, _options, ctx.SessionId, ShopBaseUrl.MerchantId(shop), Zone(ctx));

        ctx.Progress(JobStep.Normalizing);

        // The window is a filter, not a page cursor: this resource returns
        // the one order the connection is for. A caller who asks for last
        // week and connected a March order gets nothing, which is their
        // request honoured rather than a record lost.
        IReadOnlyList<Receipt> receipts = ReceiptFactory.InWindow(receipt.PurchasedAt, request) ? [receipt] : [];

        return new FetchResult
        {
            Receipts = receipts,
            // Nothing rotates. There is no token to re-issue, so re-sealing
            // the bundle after every fetch would persist the same bytes.
            RefreshedMaterial = null,
            Complete = true,
            Via = reference.HasToken ? _options.GuestOrderByTokenField : _options.GuestOrderField,
        };
    }

    /// <summary>
    /// The endpoint this connection will be called at. Public so an operator
    /// can see what a shop URL resolves to without running a job.
    /// </summary>
    public string EndpointFor(string shopBaseUrl) =>
        ShopBaseUrl.Endpoint(
            ShopBaseUrl.Parse(shopBaseUrl, ProviderId, ShopUrlKey, _options.AllowPlainHttp),
            _options.GraphQlPath);

    internal string FieldFor(MagentoGuestReference reference) =>
        reference.HasToken ? _options.GuestOrderByTokenField : _options.GuestOrderField;

    /// <summary>
    /// Either the token or the whole triple. The manifest cannot express
    /// "one of these two sets", so the rule lives here and says which set is
    /// missing rather than which box is empty.
    /// </summary>
    internal static MagentoGuestReference RequireReference(MagentoGuestReference reference)
    {
        if (reference.HasToken || reference.HasTriple) return reference;

        throw ConnectorException.InvalidRequest(
            $"{ProviderId}: supply either '{TokenInput}', or all of " +
            $"'{OrderNumberInput}', '{EmailInput}' and '{LastNameInput}' - they are all in the order confirmation mail");
    }

    internal string BuildQuery(MagentoGuestReference reference)
    {
        var template = reference.HasToken ? _options.GuestOrderByTokenQuery : _options.GuestOrderQuery;
        return template.Replace("{fields}", _options.OrderFields, StringComparison.Ordinal);
    }

    private async Task<JsonDocument> QueryAsync(
        IJobContext ctx, Uri shop, MagentoGuestReference reference, CancellationToken ct)
    {
        var variables = reference.HasToken
            ? new JsonObject { ["token"] = reference.Token }
            : new JsonObject
            {
                ["number"] = reference.Number,
                ["email"] = reference.Email,
                ["lastname"] = reference.LastName,
            };

        var body = new JsonObject
        {
            ["query"] = BuildQuery(reference),
            ["variables"] = variables,
        }.ToJsonString();

        var url = ShopBaseUrl.Endpoint(shop, _options.GraphQlPath);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            // CONFIRMED mandatory (§3): Magento's GraphQL endpoint rejects a
            // request without this content type outright.
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // The reference is about to leave this machine. After this point a
        // lost lease fails the job instead of requeuing it - the same rule
        // that protects a password, applied to the thing that plays the part
        // of one here. It matters more than it looks: a shop behind Wordfence
        // counts failed lookups, and a silent retry storm against somebody
        // else's WordPress is how an IP earns a permanent block.
        ctx.CredentialSubmitted();

        using var response = await ProviderHttp.SendAsync(ctx.Http, request, ProviderId, ct).ConfigureAwait(false);

        var text = await BotWall.ReadBoundedAsync(
            response, _options.MaxResponseBytes, ProviderId, "guest order", ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode) throw Failure(response, text);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            // JSON was promised and something else arrived. A challenge page
            // served with a 200 is the case this catches, and calling it a
            // shape change would send an operator hunting a schema that
            // never moved.
            throw BotWall.Suspects(response, text)
                ? ConnectorException.Blocked($"{ProviderId}: a bot wall answered instead of the shop")
                : new ConnectorException(
                    ErrorCode.ProviderChanged, $"{ProviderId}: guest order did not return JSON", ex);
        }

        try
        {
            MagentoGuestOrderParser.ThrowOnErrors(document.RootElement, _options);
        }
        catch
        {
            document.Dispose();
            throw;
        }

        return document;
    }

    /// <summary>
    /// A non-success status, read as carefully as it deserves.
    ///
    /// Magento answers a bad reference inside a 200 body, so a status here is
    /// almost never about the reference and is very often the shop's edge:
    /// these are hosts nobody vetted, and Cloudflare, Akamai and Wordfence
    /// all sit in front of some of them. Telling a user their order number
    /// is wrong when a wall never let the request through is the specific
    /// mistake this method exists to avoid.
    /// </summary>
    private ConnectorException Failure(HttpResponseMessage response, string body)
    {
        if (BotWall.Suspects(response, body))
        {
            return ConnectorException.Blocked(
                $"{ProviderId}: the shop's edge refused us with {(int)response.StatusCode}");
        }

        return ProviderHttp.Failure(response.StatusCode, ProviderId, "guest order", _options.BlockedStatuses);
    }

    private TimeZoneInfo Zone(IJobContext ctx) =>
        RetailZones.For(Config(ctx, StoreCountryKey) ?? _options.DefaultStoreCountry);

    private static string? Config(IJobContext ctx, string key) =>
        ctx.Config.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}

/// <summary>
/// The registration entry point.
///
/// A static factory rather than a line edited into ShopAdapters directly,
/// because six adapters are landing on this tree at once and that file is the
/// one they all share.
/// </summary>
public static class MagentoGuestAdapters
{
    public static IProviderAdapter Create(MagentoGuestOptions? options = null, TimeProvider? time = null) =>
        new MagentoGuestAdapter(options, time);
}
