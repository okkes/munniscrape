using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Connector.Kit.Adapters;
using Connector.Kit.Errors;
using Connector.Kit.Manifests;
using Connector.Kit.Normalization;
using Connector.Kit.Security;
using ShopConnector.Adapters.Support;
using ShopConnector.Adapters.Tests.Support;
using ShopConnector.Adapters.WooGuest;
using Xunit;

namespace ShopConnector.Adapters.Tests;

/// <summary>
/// The WooCommerce Store API single-order lookup, offline.
///
/// Every field, error code and status asserted here was read out of
/// <c>woocommerce/woocommerce</c> @ <c>trunk</c> on 2026-07-28 rather than
/// inferred, which matters most in one place: WooCommerce and a bot wall
/// share their status codes. A 403 is as likely to be
/// <c>woocommerce_rest_invalid_user</c> - "this order belongs to an account"
/// - as it is Cloudflare, and a 401 is a wrong key rather than a dead
/// session. Mapping either by status alone would tell somebody their
/// credentials are wrong when they are not.
/// </summary>
public sealed class WooGuestTests
{
    private const string OrderFixture = "woo/order.json";
    private const string InvalidOrderFixture = "woo/error-invalid-order.json";
    private const string InvalidUserFixture = "woo/error-invalid-user.json";

    private const string Shop = "https://www.wibra.nl";
    private const string Host = "www.wibra.nl";
    private const string OrderKey = "wc_order_9Ka1Lm4QpZ";

    private static readonly WooGuestOptions Options = new();

    private static readonly ProviderManifest Manifest = new WooGuestAdapter().Describe();

    // ---- the manifest ------------------------------------------------------

    [Fact]
    public void The_manifest_validates()
    {
        ManifestValidator.Validate(Manifest);

        var registry = new ProviderRegistry([new WooGuestAdapter()]);
        Assert.Equal(WooGuestAdapter.ProviderId, Assert.Single(registry.Manifests).Id);
    }

    [Fact]
    public void It_runs_inline_with_no_browser_no_agent_and_nothing_to_challenge()
    {
        Assert.Equal(ProviderRuntime.Http, Manifest.Runtime);
        Assert.False(Manifest.Agent.Required);
        Assert.Equal(AgentClass.Inline, Manifest.Agent.Class);
        Assert.Empty(Manifest.Auth.Challenges);

        Assert.True(Manifest.Unattended);
        Assert.True(Manifest.Auth.Session.Refreshable);
        Assert.False(Manifest.Auth.Session.RotatesOnUse);

        var receipts = Manifest.Resource(WooGuestAdapter.ReceiptsResource);
        Assert.NotNull(receipts);
        Assert.Equal(1, receipts.MaxRecordsPerFetch);
    }

    [Fact]
    public void It_honours_the_contract_every_shopping_provider_shares()
    {
        // The assertions ManifestTests makes over every registered provider,
        // made here too: this adapter is not registered yet, and the wiring
        // that registers it must not be the thing that discovers a mismatch.
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
    public void The_order_key_is_declared_secret_and_the_date_is_required()
    {
        var step = Assert.Single(Manifest.Auth.Steps);

        // hash_equals against this single string is the whole authorisation.
        // ParamSpec has no secret flag and FieldSpec does, which is the
        // reason the reference is modelled as a credential at all.
        var key = Assert.Single(step.Fields, f => f.Key == WooGuestAdapter.OrderKeyInput);
        Assert.True(key.Secret, "the order key is a bearer capability and must never reach a log");
        Assert.True(key.Required);

        // OrderSchema::get_item_response() returns no date field of any
        // kind, so the reference has to carry one or the receipt has no
        // timestamp at all.
        var date = Assert.Single(step.Fields, f => f.Key == WooGuestAdapter.OrderDateInput);
        Assert.Equal(FieldType.Date, date.Type);
        Assert.True(date.Required);

        // The shop is configuration, because one adapter serves every
        // WooCommerce shop there is.
        Assert.Contains(Manifest.Auth.Config, f => f.Key == WooGuestAdapter.ShopUrlKey);
    }

    // ---- parsing -----------------------------------------------------------

    [Fact]
    public void An_order_parses_into_a_reconciled_receipt()
    {
        var receipt = Parse(OrderFixture);

        Assert.Equal("4821", receipt.ExternalId);
        Assert.Equal(Host, receipt.Merchant.Id);

        // Declared MoneyUnit.Minor: the Store API's money formatter returns
        // intval(round(value * 10 ** decimals)) as a *string*, so "2246" is
        // twenty-two euros forty-six. Read as a decimal it would have been
        // two hundred and twenty-four thousand cents, which looks entirely
        // plausible on a screen.
        Assert.Equal(2246, receipt.Total.Value);
        Assert.Equal("EUR", receipt.Total.Currency);

        // Two product lines, one fee, one shipping line.
        Assert.Equal(4, receipt.Items.Count);
        Assert.True(receipt.Reconciled);

        // Nothing in the order schema names a payment method, a card or an
        // IBAN - so every tail is explicitly null rather than omitted.
        Assert.NotNull(receipt.Payment);
        Assert.Null(receipt.Payment.Method);
        Assert.Null(receipt.Payment.CardLast4);
    }

    [Fact]
    public void The_coupon_is_a_discount_on_its_line_and_is_not_counted_twice()
    {
        var receipt = Parse(OrderFixture);

        var towels = Assert.Single(receipt.Items, i => i.Name.StartsWith("Badhanddoek", StringComparison.Ordinal));

        // The line is emitted gross of its coupon with the coupon as an
        // explicit negative, so the two net to line_total + line_total_tax -
        // which is exactly what total_items and total_items_tax are built
        // from.
        Assert.Equal(1450, towels.Total.Value);
        Assert.Equal(-146, towels.Discount?.Amount.Value);

        // The order also states the same discount in `coupons` and in
        // `totals.total_discount`, for display. Reading either would subtract
        // it a second time and leave every couponed receipt failing its own
        // arithmetic.
        Assert.DoesNotContain(receipt.Items, i => i.Name == "welkom10");
        Assert.Equal(receipt.Total.Value, receipt.Items.Sum(i => i.Total.Value + (i.Discount?.Amount.Value ?? 0)));
    }

    [Fact]
    public void A_unit_price_is_stated_only_where_the_line_is_a_single_unit()
    {
        var receipt = Parse(OrderFixture);

        var pegs = Assert.Single(receipt.Items, i => i.Name == "Wasknijpers 50 stuks");
        Assert.Equal(1m, pegs.Quantity);
        Assert.Equal(422, pegs.UnitPrice?.Value);

        // Two towels at 6.49 in the catalogue today - but `prices.price` is
        // built with prepare_product_price_response($product, …), the
        // product's current price, not what this order paid. Quoting it
        // would put a confident wrong number next to every item whose price
        // has changed since, so a multi-unit line states no unit price at
        // all.
        var towels = Assert.Single(receipt.Items, i => i.Name.StartsWith("Badhanddoek", StringComparison.Ordinal));
        Assert.Equal(2m, towels.Quantity);
        Assert.Null(towels.UnitPrice);
    }

    [Fact]
    public void Fees_and_shipping_are_lines_gross_of_their_own_tax()
    {
        var receipt = Parse(OrderFixture);

        Assert.Equal(42, Assert.Single(receipt.Items, i => i.Name == "Betaalkosten iDEAL").Total.Value);

        // 3.95 plus 0.83 of tax. Mixing the bases is how a receipt ends up a
        // euro away from its own total.
        Assert.Equal(478, Assert.Single(receipt.Items, i => i.Name == Options.ShippingLineName).Total.Value);
    }

    [Fact]
    public void The_purchase_time_comes_from_the_reference_and_carries_a_real_offset()
    {
        var receipt = Parse(OrderFixture, stated: "2026-07-14");

        // Midnight in the shop's country, because a bare day is what the
        // consumer's date field produces - but with a real offset on it, so
        // it cannot drift onto the wrong day on the consumer's side.
        Assert.Equal(new DateOnly(2026, 7, 14), DateOnly.FromDateTime(receipt.PurchasedAt.Date));
        Assert.Equal(TimeSpan.FromHours(2), receipt.PurchasedAt.Offset);
    }

    [Fact]
    public void A_full_instant_in_the_reference_is_honoured_as_given()
    {
        // What the future e-mail connector will supply from the message
        // header: an instant that already states its own offset.
        var receipt = Parse(OrderFixture, stated: "2026-01-09T18:22:05+01:00");

        Assert.Equal(TimeSpan.FromHours(1), receipt.PurchasedAt.Offset);
        Assert.Equal(18, receipt.PurchasedAt.Hour);
    }

    [Fact]
    public void A_shop_that_states_its_own_date_beats_anything_a_human_typed()
    {
        var payload = Fixture.Object(OrderFixture);
        payload["date_created_gmt"] = "2026-07-14T09:31:22";

        using var document = Fixture.Reparse(payload);
        var receipt = ParseElement(document.RootElement, "2026-01-01");

        // A `_gmt` field is UTC without saying so, and is read as such.
        Assert.Equal(TimeSpan.Zero, receipt.PurchasedAt.Offset);
        Assert.Equal(new DateOnly(2026, 7, 14), DateOnly.FromDateTime(receipt.PurchasedAt.UtcDateTime));
    }

    [Fact]
    public void An_order_with_no_date_anywhere_is_provider_changed()
    {
        using var document = Fixture.Doc(OrderFixture);

        var error = Assert.Throws<ConnectorException>(() => ParseElement(document.RootElement, stated: null));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains("date", error.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    // ---- the unit is declared, and checked --------------------------------

    [Fact]
    public void A_shop_that_does_not_price_in_hundredths_is_refused_by_name()
    {
        var payload = Fixture.Object(OrderFixture);
        payload["totals"]!["currency_minor_unit"] = 3;

        using var document = Fixture.Reparse(payload);

        var error = Assert.Throws<ConnectorException>(() => ParseElement(document.RootElement));

        // currency_minor_unit is wc_get_price_decimals(), a per-shop
        // setting. This service's Money is hundredths; anything else is a
        // shape it cannot carry, and dividing by the wrong power of ten
        // silently would put a plausible wrong number in front of somebody.
        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains("currency_minor_unit", error.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("totals")]
    [InlineData("id")]
    public void A_missing_required_field_is_provider_changed(string field)
    {
        var payload = Fixture.Object(OrderFixture);
        payload.Remove(field);

        using var document = Fixture.Reparse(payload);

        var error = Assert.Throws<ConnectorException>(() => ParseElement(document.RootElement));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains(field, error.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void A_line_with_no_totals_at_all_fails_loudly_rather_than_shrinking_the_receipt()
    {
        var payload = Fixture.Object(OrderFixture);
        var totals = (JsonObject)payload["items"]![0]!["totals"]!;
        totals.Remove("line_subtotal");
        totals.Remove("line_subtotal_tax");
        totals.Remove("line_total");
        totals.Remove("line_total_tax");

        using var document = Fixture.Reparse(payload);

        var error = Assert.Throws<ConnectorException>(() => ParseElement(document.RootElement));

        // Skipping the line would have produced a smaller receipt that still
        // looked fine - the worst possible outcome.
        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
    }

    [Fact]
    public void An_order_whose_lines_do_not_add_up_is_flagged_and_never_dropped()
    {
        var payload = Fixture.Object(OrderFixture);

        // The stated total, moved. Note what is *not* mutated here: a wrong
        // line_subtotal alone would be absorbed by the coupon line the
        // parser derives from it, because the discount is the difference
        // between gross and net. The redundancy that actually bites is the
        // order's own total against the sum of what it is made of, which is
        // exactly what Reconciliation checks.
        payload["totals"]!["total_price"] = "2500";

        using var document = Fixture.Reparse(payload);
        var receipt = ParseElement(document.RootElement);

        Assert.False(receipt.Reconciled);

        // Still emitted. Dropping it would hide a real purchase; trusting it
        // would hand over a total we know disagrees with its own contents.
        Assert.Equal(2500, receipt.Total.Value);
        Assert.NotEmpty(receipt.Items);
    }

    // ---- what the provider says, and what it means ------------------------

    [Fact]
    public async Task A_wrong_key_is_invalid_credentials()
    {
        // validate_order_key() throws woocommerce_rest_invalid_order with
        // 401. The reference is the credential here, so this is a credential
        // failure - and the platform never retries one, which is what we
        // want against a host that may be counting attempts.
        var error = await LoginFailureAsync(Stub.Fixture(InvalidOrderFixture, HttpStatusCode.Unauthorized));

        Assert.Equal(ErrorCode.InvalidCredentials, error.Code);
    }

    [Fact]
    public async Task A_403_that_says_woocommerce_rest_invalid_user_is_unsupported_not_blocked()
    {
        // The single most mis-mappable response in this adapter. 403 is the
        // status a bot wall uses; here it means the order belongs to a
        // registered customer and will never be readable on the guest route.
        var error = await LoginFailureAsync(Stub.Fixture(InvalidUserFixture, HttpStatusCode.Forbidden));

        Assert.Equal(ErrorCode.UnsupportedResource, error.Code);
        Assert.NotEqual(ErrorCode.BlockedByProvider, error.Code);
        Assert.NotEqual(ErrorCode.InvalidCredentials, error.Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData((HttpStatusCode)429)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Bot_protection_is_blocked_by_provider_and_never_invalid_credentials(HttpStatusCode status)
    {
        // A refusing status with nothing of WooCommerce's in the body is the
        // host's own protection - Wordfence, Cloudflare, the CDN - and never
        // a verdict on what the user typed.
        var error = await LoginFailureAsync(Stub.Status(status));

        Assert.Equal(ErrorCode.BlockedByProvider, error.Code);
        Assert.NotEqual(ErrorCode.InvalidCredentials, error.Code);
    }

    [Fact]
    public async Task A_wordfence_block_page_is_blocked_even_with_a_200()
    {
        var wall = Stub.Html("<html><body>Your access to this site has been limited by Wordfence</body></html>");

        var error = await LoginFailureAsync(wall);

        Assert.Equal(ErrorCode.BlockedByProvider, error.Code);
    }

    [Fact]
    public async Task An_unrecognised_error_body_is_a_shape_change_and_not_a_credential_verdict()
    {
        var response = Stub.Json("""{"code":"rest_no_route","message":"No route was found","data":{"status":404}}""",
            HttpStatusCode.NotFound);

        var error = await LoginFailureAsync(response);

        // Not WooCommerce's Store API answering, so nothing in that body
        // gets to produce invalid_credentials.
        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
    }

    // ---- the auth mechanics ------------------------------------------------

    [Fact]
    public async Task The_key_and_the_billing_email_authorise_the_request_and_the_credential_is_latched()
    {
        using var handler = Responder(_ => Stub.Fixture(OrderFixture));
        using var ctx = Context(handler, Reference());

        var login = await new WooGuestAdapter().LoginAsync(ctx, CancellationToken.None);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal("/wp-json/wc/store/v1/order/4821", sent.Path);

        // Confirmed from is_authorized(): get_param('key') and
        // get_param('billing_email'), and nothing else.
        Assert.Contains("key=wc_order_9Ka1Lm4QpZ", sent.Query, StringComparison.Ordinal);
        Assert.Contains("billing_email=shopper%40example.nl", sent.Query, StringComparison.Ordinal);

        // The key travels in a query string, so the latch matters: after it,
        // a lost lease fails the job rather than replaying an attempt
        // against a stranger's shop.
        Assert.True(ctx.CredentialWasSubmitted);

        Assert.Equal(OrderKey, login.Material.Extra[WooGuestAdapter.OrderKeyInput]);
        Assert.Equal("2026-07-14", login.Material.Extra[WooGuestAdapter.OrderDateInput]);
        Assert.Equal($"{Host} #4821", login.Account?.DisplayName);
    }

    [Fact]
    public async Task A_fetch_reuses_the_sealed_reference_and_rotates_nothing()
    {
        using var handler = Responder(_ => Stub.Fixture(OrderFixture));
        using var ctx = new FakeJobContext(handler)
        {
            Config = ConfigFor(),
            Material = new SessionMaterial { Extra = Reference() },
        };

        var result = await new WooGuestAdapter().FetchAsync(
            ctx, Requests.Receipts(since: Requests.Day(2026, 1, 1)), CancellationToken.None);

        var receipt = Assert.Single(result.Receipts);
        Assert.Equal("4821", receipt.ExternalId);
        Assert.Equal("store_api_order", result.Via);
        Assert.Null(result.RefreshedMaterial);
        Assert.True(result.Complete);
    }

    [Fact]
    public async Task A_reference_missing_its_key_is_a_bad_request_and_never_a_network_call()
    {
        var partial = Reference();
        partial.Remove(WooGuestAdapter.OrderKeyInput);

        using var ctx = Context(null, partial);

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => new WooGuestAdapter().LoginAsync(ctx, CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidRequest, error.Code);
        Assert.Equal(0, ((ThrowingHttpHandler)ctx.Handler).Calls);
        Assert.False(ctx.CredentialWasSubmitted);
    }

    [Fact]
    public void The_endpoint_an_operator_can_see_never_carries_the_key()
    {
        var url = new WooGuestAdapter().EndpointFor(Shop, "4821");

        Assert.Equal("https://www.wibra.nl/wp-json/wc/store/v1/order/4821", url);
        Assert.DoesNotContain("key=", url, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://www.wibra.nl")]
    [InlineData("https://192.168.1.10")]
    [InlineData("https://metadata.internal")]
    public void A_shop_url_that_is_not_a_public_https_shop_is_refused(string url)
    {
        // Plain http matters more here than anywhere else in the service:
        // the order key is in the query string, so cleartext hands the order
        // to whoever is on the path.
        var error = Assert.Throws<ConnectorException>(() => new WooGuestAdapter().EndpointFor(url, "4821"));

        Assert.Equal(ErrorCode.InvalidRequest, error.Code);
    }

    // ---- helpers -----------------------------------------------------------

    private static Receipt Parse(string fixture, string? stated = "2026-07-14")
    {
        using var document = Fixture.Doc(fixture);
        return ParseElement(document.RootElement, stated);
    }

    private static Receipt ParseElement(JsonElement root, string? stated = "2026-07-14") =>
        WooGuestOrderParser.Parse(root, Options, "ses_test", Host, stated, RetailZones.For("NL"));

    private static Dictionary<string, string> Reference() => new(StringComparer.Ordinal)
    {
        [WooGuestAdapter.OrderIdInput] = "4821",
        [WooGuestAdapter.OrderKeyInput] = OrderKey,
        [WooGuestAdapter.EmailInput] = "shopper@example.nl",
        [WooGuestAdapter.OrderDateInput] = "2026-07-14",
    };

    private static Dictionary<string, string> ConfigFor() => new(StringComparer.Ordinal)
    {
        [WooGuestAdapter.ShopUrlKey] = Shop,
        [WooGuestAdapter.StoreCountryKey] = "NL",
    };

    private static StubHttpHandler Responder(Func<RecordedRequest, HttpResponseMessage> respond) =>
        new((request, _) => respond(request));

    private static FakeJobContext Context(HttpMessageHandler? handler, IReadOnlyDictionary<string, string> inputs) =>
        new(handler)
        {
            Config = ConfigFor(),
            Inputs = inputs,
        };

    private static async Task<ConnectorException> LoginFailureAsync(HttpResponseMessage response)
    {
        using var handler = Responder(_ => response);
        using var ctx = Context(handler, Reference());

        return await Assert.ThrowsAsync<ConnectorException>(
            () => new WooGuestAdapter().LoginAsync(ctx, CancellationToken.None));
    }
}
