using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Connector.Kit.Adapters;
using Connector.Kit.Errors;
using Connector.Kit.Manifests;
using Connector.Kit.Normalization;
using Connector.Kit.Security;
using ShopConnector.Adapters.MagentoGuest;
using ShopConnector.Adapters.Support;
using ShopConnector.Adapters.Tests.Support;
using Xunit;

namespace ShopConnector.Adapters.Tests;

/// <summary>
/// The Magento guest-order lookup, offline.
///
/// Nothing here opens a socket. That is not only hygiene: no guestOrder query
/// has ever been executed against a live shop, because doing so means
/// asserting somebody else's order number, so these fixtures are the whole
/// record of what the adapter believes and the day a real capture lands the
/// diff will be visible rather than silent.
///
/// The two assertions that matter most are the day-first date and the tax
/// basis. Both are places where a plausible-looking wrong answer is
/// available and nobody would ever notice it by eye.
/// </summary>
public sealed class MagentoGuestTests
{
    private const string OrderFixture = "magento/guest-order.json";
    private const string NetOrderFixture = "magento/guest-order-net.json";
    private const string NotFoundFixture = "magento/order-not-found.json";
    private const string NeedsAccountFixture = "magento/order-needs-account.json";

    private const string Shop = "https://www.dille-kamille.nl";
    private const string Host = "www.dille-kamille.nl";

    private static readonly MagentoGuestOptions Options = new();

    private static readonly ProviderManifest Manifest = new MagentoGuestAdapter().Describe();

    // ---- the manifest ------------------------------------------------------

    [Fact]
    public void The_manifest_validates()
    {
        // A manifest the host would refuse to boot on must not pass here
        // either. Throws with every failing rule listed.
        ManifestValidator.Validate(Manifest);

        // And it survives being put in a registry, which is what the host
        // actually does at startup.
        var registry = new ProviderRegistry([new MagentoGuestAdapter()]);
        Assert.Equal(MagentoGuestAdapter.ProviderId, Assert.Single(registry.Manifests).Id);
    }

    [Fact]
    public void It_is_a_per_order_lookup_that_needs_no_browser_and_no_agent()
    {
        // T1 in the strongest sense available: one unauthenticated POST, so
        // it runs inline in the control plane and no agent is ever assigned.
        Assert.Equal(ProviderRuntime.Http, Manifest.Runtime);
        Assert.False(Manifest.Agent.Required);
        Assert.Equal(AgentClass.Inline, Manifest.Agent.Class);
        Assert.Null(Manifest.Agent.Egress);

        // There is no login page, so there is nothing to challenge. A
        // declared challenge a provider cannot raise makes a consumer build
        // a screen for a moment that never comes.
        Assert.Empty(Manifest.Auth.Challenges);

        // The honest reading of a credential that never expires: no human is
        // ever needed to keep it working, and nothing rotates on use.
        Assert.True(Manifest.Unattended);
        Assert.True(Manifest.Auth.Session.Refreshable);
        Assert.False(Manifest.Auth.Session.RotatesOnUse);
        Assert.False(Manifest.Auth.Reauth.Cheap);
        Assert.Empty(Manifest.Auth.Reauth.TriggerCodes);

        // One order, one receipt - the true number, not a placeholder.
        var receipts = Manifest.Resource(MagentoGuestAdapter.ReceiptsResource);
        Assert.NotNull(receipts);
        Assert.Equal(1, receipts.MaxRecordsPerFetch);

        // Ten years, because the platform pulls a caller's `since` forward to
        // now - MaxHistoryDays and a deliberately connected old order would
        // otherwise vanish with nothing to explain it.
        Assert.Equal(3_650, receipts.MaxHistoryDays);
    }

    [Fact]
    public void It_honours_the_contract_every_shopping_provider_shares()
    {
        // The same assertions ManifestTests makes over every registered
        // provider, made here too - because this adapter is not registered
        // yet and the wiring that registers it must not be the thing that
        // discovers a mismatch.
        Assert.StartsWith("connect.", Manifest.NotesKey, StringComparison.Ordinal);

        foreach (var step in Manifest.Auth.Steps)
        {
            Assert.StartsWith("connect.", step.LabelKey, StringComparison.Ordinal);
        }

        foreach (var field in Manifest.Auth.AllFields())
        {
            Assert.StartsWith("connect.", field.LabelKey, StringComparison.Ordinal);
        }

        var receipts = Manifest.Resource("receipts");
        Assert.NotNull(receipts);
        Assert.Equal(ResourceShape.Receipt, receipts.Returns);

        var since = receipts.Param("since");
        Assert.NotNull(since);
        Assert.Equal(ParamType.Date, since.Type);
        Assert.True(since.Required);

        Assert.Equal(ParamType.Date, receipts.Param("until")?.Type);

        var include = receipts.Param("include");
        Assert.NotNull(include);
        Assert.True(include.Multi);
        Assert.Equal(new[] { "items" }, include.Values);
    }

    [Fact]
    public void The_shop_is_configuration_and_the_reference_is_a_credential()
    {
        // Multi-tenant: one adapter, every Magento shop, and the host
        // therefore belongs in config rather than in a literal.
        var shopUrl = Assert.Single(Manifest.Auth.Config, f => f.Key == MagentoGuestAdapter.ShopUrlKey);
        Assert.True(shopUrl.Required);
        Assert.False(shopUrl.Secret);

        var country = Assert.Single(Manifest.Auth.Config, f => f.Key == MagentoGuestAdapter.StoreCountryKey);
        Assert.Equal(FieldType.Select, country.Type);
        Assert.False(country.Required);

        var step = Assert.Single(Manifest.Auth.Steps);
        Assert.Equal(4, step.Fields.Count);

        // The order reference travels through the credential door rather
        // than as a resource parameter, and this is why: only a FieldSpec
        // can be marked secret, and the token is a bearer capability.
        var token = Assert.Single(step.Fields, f => f.Key == MagentoGuestAdapter.TokenInput);
        Assert.True(token.Secret, "the order token is a bearer capability and must never reach a log");

        // The order number is the connection's display name, so redacting it
        // would leave a user unable to tell two connections apart.
        Assert.False(Assert.Single(step.Fields, f => f.Key == MagentoGuestAdapter.OrderNumberInput).Secret);

        // Every field optional, because the adapter accepts either the
        // triple or the token and the manifest cannot express "one of these
        // two sets".
        Assert.All(step.Fields, f => Assert.False(f.Required));
    }

    // ---- parsing -----------------------------------------------------------

    [Fact]
    public void A_guest_order_parses_into_a_reconciled_receipt()
    {
        var receipt = Parse(OrderFixture, "NL");

        Assert.Equal("000000412", receipt.ExternalId);

        // The shop, not the platform. Fifty receipts all labelled "magento"
        // would have lost the only fact that made them worth keeping.
        Assert.Equal(Host, receipt.Merchant.Id);

        // Declared MoneyUnit.MajorDecimal: Magento's Money.value is a Float
        // in euros, so 42.02 is 4202 cents. Nothing infers a unit from the
        // value's shape - read as minor units it would have been 42 cents.
        Assert.Equal(4202, receipt.Total.Value);
        Assert.Equal("EUR", receipt.Total.Currency);

        // The redundancy Magento hands us, spent: three product lines plus
        // shipping minus the promotion equals the stated grand total.
        Assert.True(receipt.Reconciled);
        Assert.Equal(receipt.Total.Value, receipt.Items.Sum(i => i.Total.Value));

        Assert.Equal("ideal", receipt.Payment?.Method);

        // Nothing in the confirmed selection can carry a card or IBAN tail,
        // so both stay explicitly null and the consumer knows its match will
        // be weaker.
        Assert.Null(receipt.Payment?.CardLast4);
        Assert.Null(receipt.Payment?.IbanTail);
    }

    [Fact]
    public void Order_date_is_read_day_first_because_magento_writes_it_that_way()
    {
        var receipt = Parse(OrderFixture, "NL");

        // The fixture says "07/09/2026 21:05:00". Magento's
        // DATETIME_SLASH_PHP_FORMAT is 'd/m/Y H:i:s', so that is the seventh
        // of September. An invariant-culture parse would read it as 9 July,
        // silently, on every order placed before the 13th of a month - which
        // is why the format is matched exactly rather than guessed.
        Assert.Equal(2026, receipt.PurchasedAt.Year);
        Assert.Equal(9, receipt.PurchasedAt.Month);
        Assert.Equal(7, receipt.PurchasedAt.Day);
        Assert.Equal(21, receipt.PurchasedAt.Hour);

        // A real offset, never a bare date: September is still summer time
        // in Amsterdam.
        Assert.Equal(TimeSpan.FromHours(2), receipt.PurchasedAt.Offset);
    }

    [Fact]
    public void Items_carry_their_quantity_unit_price_and_line_total()
    {
        var receipt = Parse(OrderFixture, "NL");

        var towel = Assert.Single(receipt.Items, i => i.Name == "Theedoek gestreept");
        Assert.Equal(2m, towel.Quantity);

        // product_sale_price is a UNIT price - confirmed in
        // SalesGraphQl/Model/OrderItem/DataProvider.php - so the line total
        // is the unit times the quantity and not the field itself.
        Assert.Equal(795, towel.UnitPrice?.Value);
        Assert.Equal(1590, towel.Total.Value);

        var candles = Assert.Single(receipt.Items, i => i.Name == "Bijenwaskaars");
        Assert.Equal(3m, candles.Quantity);
        Assert.Equal(1275, candles.Total.Value);
    }

    [Fact]
    public void Shipping_and_the_promotion_are_lines_of_their_own()
    {
        var receipt = Parse(OrderFixture, "NL");

        // The shop's own words for the delivery, not a configured default.
        var shipping = Assert.Single(receipt.Items, i => i.Name == "PostNL - Brievenbuspakket");
        Assert.Equal(499, shipping.Total.Value);

        // Magento links no discount to a product, so the promotion becomes
        // its own negative line rather than being attached to a guess.
        var promotion = Assert.Single(receipt.Items, i => i.Name == "Zomeractie 10%");
        Assert.Equal(-412, promotion.Total.Value);
    }

    [Fact]
    public void A_shop_that_prices_gross_gets_shipping_including_tax_and_no_tax_line()
    {
        var receipt = Parse(OrderFixture, "NL");

        // The fixture's item prices sum to subtotal_incl_tax (41.15) and not
        // to subtotal_excl_tax (34.01), so the lines are gross. Adding
        // total_tax on top would count every cent of item tax twice and put
        // the receipt eight euros over its own total.
        Assert.DoesNotContain(receipt.Items, i => i.Name == Options.TaxLineName);

        // Gross lines need gross shipping: 4.99 including tax, not the 4.13
        // that total_shipping states.
        Assert.Equal(499, Assert.Single(receipt.Items, i => i.Name == "PostNL - Brievenbuspakket").Total.Value);
        Assert.True(receipt.Reconciled);
    }

    [Fact]
    public void A_shop_that_prices_net_gets_a_tax_line_and_still_reconciles()
    {
        var receipt = Parse(NetOrderFixture, "DE");

        // Here the lines sum to subtotal_excl_tax (104.00), so tax is a line
        // of its own and shipping - zero on this order - is not emitted.
        Assert.Equal(12376, receipt.Total.Value);
        Assert.Equal(3, receipt.Items.Count);
        Assert.Equal(1976, Assert.Single(receipt.Items, i => i.Name == Options.TaxLineName).Total.Value);
        Assert.True(receipt.Reconciled);

        // November, and the store country decides the zone: Berlin is back
        // on standard time by then.
        Assert.Equal(11, receipt.PurchasedAt.Month);
        Assert.Equal(2, receipt.PurchasedAt.Day);
        Assert.Equal(TimeSpan.FromHours(1), receipt.PurchasedAt.Offset);

        // An unrecognised gateway code becomes "other" rather than a guess
        // at what "adyen_cc" might have meant.
        Assert.Equal("other", receipt.Payment?.Method);
    }

    [Fact]
    public void An_order_whose_lines_do_not_add_up_is_flagged_and_never_dropped()
    {
        var payload = Fixture.Object(OrderFixture);
        var order = payload["data"]!["guestOrder"]!;

        // One line silently worth ten euros more than it was. This is the
        // exact failure reconciliation exists to catch: every field is
        // present, every value is plausible, and only the arithmetic knows.
        order["items"]![0]!["product_sale_price"]!["value"] = 17.95;

        using var document = Fixture.Reparse(payload);
        var receipt = ParseElement(document.RootElement, "NL");

        Assert.False(receipt.Reconciled);

        // Still emitted. Dropping it would hide a real purchase; trusting it
        // would hand over a total we know disagrees with its own contents.
        Assert.Equal(4202, receipt.Total.Value);
    }

    // ---- shapes we cannot parse -------------------------------------------

    [Theory]
    [InlineData("number")]
    [InlineData("order_date")]
    [InlineData("total")]
    public void A_missing_required_field_is_provider_changed(string field)
    {
        var payload = Fixture.Object(OrderFixture);
        var order = (JsonObject)payload["data"]!["guestOrder"]!;
        order.Remove(field);

        using var document = Fixture.Reparse(payload);

        var error = Assert.Throws<ConnectorException>(() => ParseElement(document.RootElement, "NL"));

        // Names what was missing, so the fix is a diff rather than a hunt.
        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains(field, error.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void A_response_with_no_order_at_all_is_provider_changed()
    {
        using var document = JsonDocument.Parse("""{"data":{}}""");

        var error = Assert.Throws<ConnectorException>(
            () => MagentoGuestOrderParser.RequireOrder(document.RootElement, "guestOrder"));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
    }

    // ---- what the provider says, and what it means ------------------------

    [Fact]
    public async Task A_reference_that_matches_no_order_is_invalid_credentials()
    {
        // graphql-no-such-entity is Magento's single answer for a wrong
        // number, a wrong surname, a wrong e-mail and an undecryptable token
        // - deliberately indistinguishable. The reference is the credential
        // here, so this is a credential failure, and the platform never
        // retries one.
        var error = await LoginFailureAsync(Stub.Fixture(NotFoundFixture));

        Assert.Equal(ErrorCode.InvalidCredentials, error.Code);
    }

    [Fact]
    public async Task An_order_placed_with_an_account_is_unsupported_and_not_a_bad_password()
    {
        // graphql-authorization means the order exists but was placed while
        // signed in, so no reference will ever unlock it. Calling that
        // invalid_credentials would send somebody to re-read a confirmation
        // mail that is perfectly correct.
        var error = await LoginFailureAsync(Stub.Fixture(NeedsAccountFixture));

        Assert.Equal(ErrorCode.UnsupportedResource, error.Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData((HttpStatusCode)429)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Bot_protection_is_blocked_by_provider_and_never_invalid_credentials(HttpStatusCode status)
    {
        // These shops are hosts nobody vetted and a long tail of them sit
        // behind Cloudflare or Akamai. Telling a user their order number is
        // wrong when a wall never let the request through sends them to fix
        // something that was already right.
        var error = await LoginFailureAsync(Stub.Status(status));

        Assert.Equal(ErrorCode.BlockedByProvider, error.Code);
        Assert.NotEqual(ErrorCode.InvalidCredentials, error.Code);
    }

    [Fact]
    public async Task A_challenge_page_served_with_a_200_is_blocked_and_not_a_shape_change()
    {
        var wall = Stub.Html("<html><head><title>Just a moment...</title></head><body></body></html>");

        var error = await LoginFailureAsync(wall);

        // A 200 that is not JSON usually is a shape change. This one is not,
        // and calling it one would send an operator hunting a schema that
        // never moved.
        Assert.Equal(ErrorCode.BlockedByProvider, error.Code);
    }

    // ---- the auth mechanics ------------------------------------------------

    [Fact]
    public async Task Login_latches_the_credential_before_the_reference_leaves_the_machine()
    {
        using var handler = Responder(_ => Stub.Fixture(OrderFixture));
        using var ctx = Context(handler, Triple());

        _ = await new MagentoGuestAdapter().LoginAsync(ctx, CancellationToken.None);

        // After this the platform fails a lost lease instead of requeuing it.
        // It matters here for the same reason it matters for a password: a
        // shop behind Wordfence counts failed lookups per IP.
        Assert.True(ctx.CredentialWasSubmitted);
    }

    [Fact]
    public async Task Login_seals_the_reference_and_the_fetch_reuses_it()
    {
        using var handler = Responder(_ => Stub.Fixture(OrderFixture));
        using var ctx = Context(handler, Triple());

        var login = await new MagentoGuestAdapter().LoginAsync(ctx, CancellationToken.None);

        // No token, no cookie, no storage state: the credential is the
        // reference, and Extra is where a provider-specific one belongs.
        Assert.Null(login.Material.AccessToken);
        Assert.Null(login.Material.RefreshToken);
        Assert.Null(login.Material.StorageState);
        Assert.Equal("000000412", login.Material.Extra["order_number"]);
        Assert.Equal("shopper@example.com", login.Material.Extra["email"]);
        Assert.Equal("de Vries", login.Material.Extra["lastname"]);

        // The connection names the shop and the order, so a user with
        // several connected orders can tell them apart.
        Assert.Equal($"{Host} #000000412", login.Account?.DisplayName);

        using var fetchHandler = Responder(_ => Stub.Fixture(OrderFixture));
        using var fetchCtx = new FakeJobContext(fetchHandler)
        {
            Config = ConfigFor("NL"),
            Material = login.Material,
        };

        var result = await new MagentoGuestAdapter().FetchAsync(
            fetchCtx, Requests.Receipts(since: Requests.Day(2026, 1, 1)), CancellationToken.None);

        Assert.Single(result.Receipts);
        Assert.Equal("guestOrder", result.Via);

        // Nothing rotates, so re-sealing the bundle after every fetch would
        // persist the same bytes.
        Assert.Null(result.RefreshedMaterial);

        var sent = Assert.Single(fetchHandler.Requests);
        Assert.Equal("/graphql", sent.Path);
        Assert.Contains("guestOrder(input:", sent.Body, StringComparison.Ordinal);
        Assert.Contains("\"lastname\":\"de Vries\"", sent.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_token_reference_uses_the_token_operation_instead()
    {
        var payload = Fixture.Object(OrderFixture);
        var order = payload["data"]!["guestOrder"]!;
        var byToken = new JsonObject { ["data"] = new JsonObject { ["guestOrderByToken"] = order.DeepClone() } };

        using var handler = Responder(_ => Stub.Json(byToken.ToJsonString()));
        using var ctx = Context(handler, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["order_token"] = "eyJ0b2tlbiI6ImV4YW1wbGUifQ",
        });

        var login = await new MagentoGuestAdapter().LoginAsync(ctx, CancellationToken.None);

        Assert.Equal("000000412", login.Account?.ExternalId);

        var sent = Assert.Single(handler.Requests);
        Assert.Contains("guestOrderByToken(input:", sent.Body, StringComparison.Ordinal);

        // The 2.4.6 token decrypts to exactly [number, email, lastname] - it
        // is a shorter way to say the same thing, not a wider capability -
        // so only the token is sealed and nothing is invented around it.
        Assert.Equal("eyJ0b2tlbiI6ImV4YW1wbGUifQ", login.Material.Extra["order_token"]);
        Assert.False(login.Material.Extra.ContainsKey("order_number"));
    }

    [Fact]
    public async Task Neither_a_token_nor_a_whole_triple_is_a_bad_request_and_never_a_network_call()
    {
        using var ctx = Context(null, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["order_number"] = "000000412",
            // no e-mail, no surname
        });

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => new MagentoGuestAdapter().LoginAsync(ctx, CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidRequest, error.Code);

        // The default handler throws on use, so an adapter that sent a
        // half-built reference upstream would fail here rather than quietly
        // burning an attempt against a stranger's shop.
        Assert.Equal(0, ((ThrowingHttpHandler)ctx.Handler).Calls);
        Assert.False(ctx.CredentialWasSubmitted);
    }

    [Fact]
    public async Task A_window_that_excludes_the_order_returns_nothing_rather_than_the_wrong_thing()
    {
        using var handler = Responder(_ => Stub.Fixture(OrderFixture));
        using var ctx = new FakeJobContext(handler)
        {
            Config = ConfigFor("NL"),
            Material = new SessionMaterial { Extra = Triple() },
        };

        // The order is dated 7 September; the caller asked for October
        // onwards. The window is a filter here, not a page cursor, so this
        // is the caller's request honoured rather than a record lost.
        var result = await new MagentoGuestAdapter().FetchAsync(
            ctx, Requests.Receipts(since: Requests.Day(2026, 10, 1)), CancellationToken.None);

        Assert.Empty(result.Receipts);
        Assert.True(result.Complete);
    }

    [Fact]
    public async Task A_fetch_without_a_sealed_reference_is_session_expired()
    {
        using var ctx = Context(null, new Dictionary<string, string>(StringComparer.Ordinal));

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => new MagentoGuestAdapter().FetchAsync(ctx, Requests.Receipts(), CancellationToken.None));

        Assert.Equal(ErrorCode.SessionExpired, error.Code);
    }

    // ---- the shop URL is a caller-chosen address --------------------------

    [Theory]
    [InlineData("http://www.dille-kamille.nl")]
    [InlineData("https://localhost/shop")]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://169.254.169.254")]
    [InlineData("https://10.4.1.9")]
    [InlineData("https://admin.internal")]
    [InlineData("https://user:pass@www.dille-kamille.nl")]
    public void A_shop_url_that_is_not_a_public_https_shop_is_refused(string url)
    {
        // The one place this service makes a server-side request to an
        // address a caller chose. Plain http would put an order key in
        // cleartext; the rest are our own estate wearing a shop's clothes.
        var error = Assert.Throws<ConnectorException>(() => new MagentoGuestAdapter().EndpointFor(url));

        Assert.Equal(ErrorCode.InvalidRequest, error.Code);
    }

    [Fact]
    public void A_bare_host_is_accepted_and_a_path_prefix_is_kept()
    {
        var adapter = new MagentoGuestAdapter();

        // "wibra.nl" is what a person types, and refusing it teaches nobody
        // anything.
        Assert.Equal("https://www.chasin.nl/graphql", adapter.EndpointFor("www.chasin.nl"));

        // A shop living under a prefix is a real deployment, and
        // new Uri(base, path) would throw the prefix away.
        Assert.Equal("https://example.nl/shop/graphql", adapter.EndpointFor("https://example.nl/shop"));
    }

    // ---- helpers -----------------------------------------------------------

    private static Receipt Parse(string fixture, string country)
    {
        using var document = Fixture.Doc(fixture);
        return ParseElement(document.RootElement, country);
    }

    private static Receipt ParseElement(JsonElement root, string country)
    {
        var order = MagentoGuestOrderParser.RequireOrder(root, "guestOrder");
        return MagentoGuestOrderParser.Parse(order, Options, "ses_test", Host, RetailZones.For(country));
    }

    private static Dictionary<string, string> Triple() => new(StringComparer.Ordinal)
    {
        ["order_number"] = "000000412",
        ["email"] = "shopper@example.com",
        ["lastname"] = "de Vries",
    };

    private static Dictionary<string, string> ConfigFor(string country) => new(StringComparer.Ordinal)
    {
        [MagentoGuestAdapter.ShopUrlKey] = Shop,
        [MagentoGuestAdapter.StoreCountryKey] = country,
    };

    private static StubHttpHandler Responder(Func<RecordedRequest, HttpResponseMessage> respond) =>
        new((request, _) => respond(request));

    private static FakeJobContext Context(HttpMessageHandler? handler, IReadOnlyDictionary<string, string> inputs) =>
        new(handler)
        {
            Config = ConfigFor("NL"),
            Inputs = inputs,
        };

    private static async Task<ConnectorException> LoginFailureAsync(HttpResponseMessage response)
    {
        using var handler = Responder(_ => response);
        using var ctx = Context(handler, Triple());

        return await Assert.ThrowsAsync<ConnectorException>(
            () => new MagentoGuestAdapter().LoginAsync(ctx, CancellationToken.None));
    }
}
