using System.Net.Http.Headers;
using System.Text.Json;
using Connector.Kit.Adapters;
using Connector.Kit.Errors;
using Connector.Kit.Jobs;
using Connector.Kit.Manifests;
using Connector.Kit.Normalization;
using Connector.Kit.Security;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.WooGuest;

/// <summary>
/// One order-received link, taken apart.
///
/// Everything here comes out of the URL WooCommerce puts in its confirmation
/// mail - <c>/checkout/order-received/{id}/?key=wc_order_…</c> - plus the
/// billing e-mail the same mail is addressed to, plus the order date the
/// payload does not carry.
/// </summary>
internal sealed record WooGuestReference
{
    public required string OrderId { get; init; }

    public required string Key { get; init; }

    public required string BillingEmail { get; init; }

    /// <summary>
    /// The order's date, because the Store API's order response has none.
    /// Kept with the reference rather than re-asked on every fetch: it is
    /// part of what identifies this order to us.
    /// </summary>
    public string? OrderDate { get; init; }

    public IReadOnlyDictionary<string, string> ToExtra()
    {
        var extra = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [WooGuestAdapter.OrderIdInput] = OrderId,
            [WooGuestAdapter.OrderKeyInput] = Key,
            [WooGuestAdapter.EmailInput] = BillingEmail,
        };

        if (OrderDate is { Length: > 0 } date) extra[WooGuestAdapter.OrderDateInput] = date;
        return extra;
    }

    public static WooGuestReference From(IReadOnlyDictionary<string, string> source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new WooGuestReference
        {
            OrderId = Require(source, WooGuestAdapter.OrderIdInput),
            Key = Require(source, WooGuestAdapter.OrderKeyInput),
            BillingEmail = Require(source, WooGuestAdapter.EmailInput),
            OrderDate = Value(source, WooGuestAdapter.OrderDateInput),
        };
    }

    private static string Require(IReadOnlyDictionary<string, string> source, string key) =>
        Value(source, key) ?? throw ConnectorException.InvalidRequest(
            $"{WooGuestAdapter.ProviderId}: '{key}' is required - it is in the order-received link in the confirmation mail");

    private static string? Value(IReadOnlyDictionary<string, string> source, string key) =>
        source.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}

/// <summary>
/// WooCommerce Store API, single-order lookup. T1 - no login, no captcha, no
/// browser, no password, and no host of its own.
///
/// The Store API exposes exactly one order route,
/// <c>GET /wp-json/wc/store/v1/order/{id}</c>, and authorises it with two
/// query parameters rather than a session: the order key and the billing
/// e-mail, compared with <c>hash_equals</c> and <c>strcasecmp</c>
/// respectively. Both are in the order-received link WooCommerce mails after
/// checkout. That is the entire mechanism, and it is core - no plugin, no
/// merchant cooperation, no consumer key.
///
/// Three limits, all CONFIRMED from source rather than assumed:
/// <list type="bullet">
/// <item>an order placed while signed in to the shop returns
/// <c>woocommerce_rest_invalid_user</c> with HTTP 403 and is unreachable on
/// this route for ever;</item>
/// <item>there is no list endpoint - order history on WooCommerce exists only
/// as themed HTML, which is why the login path is not worth building;</item>
/// <item>the response carries no date, so the reference has to.</item>
/// </list>
/// </summary>
public sealed class WooGuestAdapter : IProviderAdapter
{
    public const string ProviderId = "woo-guest";
    public const string ReceiptsResource = "receipts";

    public const string ShopUrlKey = "shop_base_url";
    public const string StoreCountryKey = "store_country";

    public const string OrderIdInput = "order_id";
    public const string OrderKeyInput = "order_key";
    public const string EmailInput = "billing_email";
    public const string OrderDateInput = "order_date";

    private static readonly ProviderManifest Manifest = WooGuestManifest.Build();

    private readonly WooGuestOptions _options;

    public WooGuestAdapter(WooGuestOptions? options = null, TimeProvider? time = null)
    {
        _options = options ?? new WooGuestOptions();

        // Taken for symmetry with every other adapter and deliberately not
        // stored: nothing here expires, polls or waits, so keeping a clock
        // would only invite a deadline this provider does not have.
        _ = time;
    }

    public ProviderManifest Describe() => Manifest;

    /// <summary>
    /// Uses the reference once to prove it works, then seals it.
    ///
    /// The alternative - store and hope - moves a typo, an account order or
    /// a shop that is not WooCommerce into a background fetch nobody is
    /// watching. One request now costs the shop exactly what the first sync
    /// would have cost it anyway.
    /// </summary>
    public async Task<LoginResult> LoginAsync(IJobContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var shop = ShopBaseUrl.Parse(Config(ctx, ShopUrlKey), ProviderId, ShopUrlKey, _options.AllowPlainHttp);
        var reference = WooGuestReference.From(ctx.Inputs);

        ctx.Progress(JobStep.Authenticating);

        using var document = await GetOrderAsync(ctx, shop, reference, ct).ConfigureAwait(false);

        // Parsed rather than merely fetched: a payload we cannot turn into a
        // receipt is a broken connection, and the moment to say so is while
        // the user is still looking.
        var receipt = WooGuestOrderParser.Parse(
            document.RootElement, _options, ctx.SessionId, ShopBaseUrl.MerchantId(shop),
            reference.OrderDate, Zone(ctx));

        ctx.Progress(JobStep.Finalizing);

        return new LoginResult
        {
            // No token and no cookie: the credential is the order key, and
            // Extra is where a provider-specific one belongs. It is sealed
            // with the rest of the bundle, which is the whole reason the
            // reference came in through a declared secret field.
            Material = new SessionMaterial { Extra = reference.ToExtra() },
            Account = new ProviderAccount
            {
                DisplayName = $"{ShopBaseUrl.MerchantId(shop)} #{receipt.ExternalId}",
                ExternalId = receipt.ExternalId,
            },
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

        var reference = WooGuestReference.From(stored);

        ctx.Progress(JobStep.Downloading);
        using var document = await GetOrderAsync(ctx, shop, reference, ct).ConfigureAwait(false);

        ctx.Progress(JobStep.Parsing);
        var receipt = WooGuestOrderParser.Parse(
            document.RootElement, _options, ctx.SessionId, ShopBaseUrl.MerchantId(shop),
            reference.OrderDate, Zone(ctx));

        ctx.Progress(JobStep.Normalizing);

        // A filter, not a cursor: this resource returns the one order the
        // connection is for, and a window that excludes it returns nothing.
        IReadOnlyList<Receipt> receipts = ReceiptFactory.InWindow(receipt.PurchasedAt, request) ? [receipt] : [];

        return new FetchResult
        {
            Receipts = receipts,
            RefreshedMaterial = null,
            Complete = true,
            Via = "store_api_order",
        };
    }

    /// <summary>
    /// The URL a reference resolves to, minus the secret.
    ///
    /// Public so an operator can see where a connection points without
    /// running a job - and returning it without the key on purpose, because
    /// the first thing anybody does with a diagnostic string is paste it
    /// somewhere.
    /// </summary>
    public string EndpointFor(string shopBaseUrl, string orderId)
    {
        var shop = ShopBaseUrl.Parse(shopBaseUrl, ProviderId, ShopUrlKey, _options.AllowPlainHttp);
        return ShopBaseUrl.Endpoint(shop, OrderPath(orderId));
    }

    internal string OrderPath(string orderId) =>
        _options.OrderPathTemplate.Replace("{id}", Uri.EscapeDataString(orderId), StringComparison.Ordinal);

    internal string RequestUrl(Uri shop, WooGuestReference reference) =>
        UrlBuilder.WithQuery(
            ShopBaseUrl.Endpoint(shop, OrderPath(reference.OrderId)),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [_options.KeyParam] = reference.Key,
                [_options.EmailParam] = reference.BillingEmail,
            });

    private async Task<JsonDocument> GetOrderAsync(
        IJobContext ctx, Uri shop, WooGuestReference reference, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, RequestUrl(shop, reference));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // The order key is about to leave this machine in a query string.
        // After this a lost lease fails the job rather than requeuing it -
        // the rule that protects a password, applied to the thing that is one
        // here. It is not ceremony: a WordPress host running Wordfence
        // counts failed attempts per IP, and a silent retry storm against
        // somebody else's shop is how this service earns a permanent block.
        ctx.CredentialSubmitted();

        using var response = await ProviderHttp.SendAsync(ctx.Http, request, ProviderId, ct).ConfigureAwait(false);

        var text = await BotWall.ReadBoundedAsync(
            response, _options.MaxResponseBytes, ProviderId, "order", ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode) throw Failure(response, text);

        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            // JSON was promised and something else arrived - a challenge
            // page served with a 200 is exactly this case, and calling it a
            // shape change would send an operator hunting a schema that
            // never moved.
            throw BotWall.Suspects(response, text)
                ? ConnectorException.Blocked($"{ProviderId}: a bot wall answered instead of the shop")
                : new ConnectorException(ErrorCode.ProviderChanged, $"{ProviderId}: order did not return JSON", ex);
        }
    }

    /// <summary>
    /// A non-success status, read in the right order: the shop's own words
    /// first, the status only after.
    ///
    /// This matters more here than anywhere else in the service, because
    /// WooCommerce and a bot wall share their status codes. A 403 is
    /// <c>woocommerce_rest_invalid_user</c> - "this order belongs to an
    /// account" - as often as it is Cloudflare, and a 401 is a wrong key
    /// rather than an expired session. Mapping either by status alone would
    /// tell somebody their credentials are wrong when they are not, which is
    /// the one mistake this codebase treats as serious.
    /// </summary>
    private ConnectorException Failure(HttpResponseMessage response, string body)
    {
        var status = (int)response.StatusCode;

        try
        {
            using var document = JsonDocument.Parse(body);
            var stated = WooGuestOrderParser.Translate(document.RootElement, status, _options);

            // A stated WooCommerce verdict always wins: it is the shop
            // telling us what happened, and no amount of edge evidence
            // outranks that.
            if (stated.Code is not ErrorCode.BlockedByProvider) return stated;
        }
        catch (JsonException)
        {
            // Not JSON. Almost always an interstitial, handled below.
        }

        return BotWall.Suspects(response, body) || _options.BlockedStatuses.Contains(status)
            ? ConnectorException.Blocked($"{ProviderId}: the shop's edge refused us with {status}")
            : ProviderHttp.Failure(response.StatusCode, ProviderId, "order", _options.BlockedStatuses);
    }

    private TimeZoneInfo Zone(IJobContext ctx) =>
        RetailZones.For(Config(ctx, StoreCountryKey) ?? _options.DefaultStoreCountry);

    private static string? Config(IJobContext ctx, string key) =>
        ctx.Config.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}

/// <summary>
/// The registration entry point. A static factory rather than a line edited
/// into ShopAdapters directly, because that file is the one every adapter
/// landing on this tree tonight would otherwise share.
/// </summary>
public static class WooGuestAdapters
{
    public static IProviderAdapter Create(WooGuestOptions? options = null, TimeProvider? time = null) =>
        new WooGuestAdapter(options, time);
}
