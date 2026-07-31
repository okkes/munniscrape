using Connector.Kit.Errors;
using Connector.Kit.Jobs;
using Connector.Kit.Manifests;
using Connector.Kit.Security;
using ShopConnector.Adapters.Fixtures;
using ShopConnector.Adapters.Jumbo;
using ShopConnector.Adapters.Tests.Support;
using Xunit;

namespace ShopConnector.Adapters.Tests;

/// <summary>
/// The Jumbo fetch loop, driven through the <see cref="IJumboGraphQlClient"/>
/// seam.
///
/// Six defaults in this adapter were wrong and five of them show up here: the
/// response paths, the two independent paginations, where line items come
/// from, that in-store receipts have none, and that no persisted-query
/// handshake is in play. The sixth - the money unit - is pinned in
/// <see cref="JumboParsingTests"/>.
/// </summary>
public sealed class JumboAdapterTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 26, 9, 0, 0, TimeSpan.Zero);

    private static JumboAdapter Adapter(IJumboGraphQlClient graphql, JumboOptions? options = null) =>
        new(options ?? new JumboOptions(), graphql, new FixedTimeProvider(Now));

    private static ResourceRequest SinceJuly => Requests.Receipts(since: Requests.Day(2026, 7, 1));

    [Fact]
    public void The_manifest_validates_and_is_honest_about_a_daily_login()
    {
        var manifest = Adapter(new FakeJumboGraphQl()).Describe();

        // A manifest that cannot boot the host must not pass the suite either.
        ManifestValidator.Validate(manifest);

        Assert.Equal(ProviderRuntime.BrowserInteractive, manifest.Runtime);
        Assert.False(manifest.UnattendedFetch);
        Assert.Equal(86_400, manifest.Auth.Session.TtlSeconds);

        // Still false, and now for the right reason: Jumbo's Auth0 tenant does
        // support refresh_token and offline_access, but the web client's code
        // is exchanged server-side and the browser is handed nothing but a
        // cookie. There is a refresh path and this connector is not on it.
        Assert.False(manifest.Auth.Session.Refreshable);
    }

    // ---- the whole pass ----------------------------------------------------

    [Fact]
    public async Task Fetch_emits_both_online_orders_and_till_receipts_from_the_confirmed_shapes()
    {
        var graphql = FakeJumboGraphQl.Recorded();
        using var ctx = new FakeJobContext { Material = JumboFixtures.LiveSession };

        var result = await Adapter(graphql).FetchAsync(ctx, SinceJuly, CancellationToken.None);

        // Two orders and two till receipts came back, but one of the receipts
        // is the till record of one of the orders - so three purchases, not
        // four.
        Assert.Equal(3, result.Receipts.Count);
        Assert.Equal(
            new[] { "order-90211", "receipt-TX-2026-07-19-778812", "order-90118" },
            result.Receipts.Select(r => r.ExternalId));

        // Newest first.
        Assert.Equal(new[] { 3113L, 1106L, 1240L }, result.Receipts.Select(r => r.Total.Value));
        Assert.All(result.Receipts, r => Assert.Equal("EUR", r.Total.Currency));
        Assert.All(result.Receipts, r => Assert.True(r.Reconciled));
        Assert.All(result.Receipts, r => Assert.Equal(TimeSpan.FromHours(2), r.PurchasedAt.Offset));

        Assert.Equal("graphql", result.Via);
        Assert.True(result.Complete);

        // Nothing rotates: there is no token in our hands to refresh.
        Assert.Null(result.RefreshedMaterial);

        // One list call, one detail per order, one detail for the till receipt
        // that survived deduplication.
        Assert.Equal(4, graphql.Calls.Count);
        Assert.Single(graphql.ListCalls);
        Assert.Equal(2, graphql.Calls.Count(c => c.OperationName == "OrderPagesOrder"));
        Assert.Single(graphql.Calls, c => c.OperationName == "GetDigitalReceipt");

        // Carried from the material rather than minted per run.
        Assert.All(graphql.DeviceIds, id => Assert.Equal("device-fixture", id));
    }

    /// <summary>
    /// The till record of an online order carries that order's id in its
    /// transaction id. Emitting both would count one weekly shop twice, and
    /// the duplicate would look exactly like a real second purchase.
    /// </summary>
    [Fact]
    public async Task An_online_order_s_till_receipt_is_not_emitted_a_second_time()
    {
        var graphql = FakeJumboGraphQl.Recorded();
        using var ctx = new FakeJobContext { Material = JumboFixtures.LiveSession };

        var result = await Adapter(graphql).FetchAsync(ctx, SinceJuly, CancellationToken.None);

        Assert.DoesNotContain(result.Receipts, r => r.ExternalId == "receipt-90211-2026-07-20-0001");

        // The dedupe happens before the detail call, so the round trip is
        // saved too.
        Assert.DoesNotContain(graphql.Calls, c =>
            c.OperationName == "GetDigitalReceipt" &&
            c.Variables["transactionId"]!.GetValue<string>() == "90211-2026-07-20-0001");
    }

    [Fact]
    public async Task An_operator_can_turn_the_deduplication_off()
    {
        var graphql = FakeJumboGraphQl.Recorded();
        using var ctx = new FakeJobContext { Material = JumboFixtures.LiveSession };

        var options = new JumboOptions { DeduplicateOnlineStoreReceipts = false };
        var result = await Adapter(graphql, options).FetchAsync(ctx, SinceJuly, CancellationToken.None);

        Assert.Equal(4, result.Receipts.Count);
        Assert.Contains(result.Receipts, r => r.ExternalId == "receipt-90211-2026-07-20-0001");
    }

    // ---- the two paginations -----------------------------------------------

    /// <summary>
    /// Online orders page by <c>offset</c>/<c>limit</c> nested inside
    /// <c>ordersInput</c>; in-store receipts page by a top-level, zero-based
    /// <c>page</c>. They are two systems with two cursors, and the single
    /// offset counter this adapter used to carry drove neither.
    /// </summary>
    [Fact]
    public async Task Orders_and_receipts_advance_their_own_cursors()
    {
        var graphql = FakeJumboGraphQl.Recorded(
            FixtureCatalog.Read("jumbo/walk-page-1.json"),
            FixtureCatalog.Read("jumbo/walk-page-2.json"));

        using var ctx = new FakeJobContext { Material = JumboFixtures.LiveSession };
        var options = new JumboOptions { OrdersPageSize = 2, ReceiptsPageSize = 1 };

        var result = await Adapter(graphql, options).FetchAsync(
            ctx, Requests.Receipts(since: Requests.Day(2026, 7, 1), items: false), CancellationToken.None);

        var list = graphql.ListCalls;
        Assert.Equal(3, list.Count);

        // offset advances by the order limit; page advances by one. Nothing
        // about either derives from the other.
        Assert.Equal(new[] { 0, 2, 4 }, list.Select(c => c.Variables["ordersInput"]!["offset"]!.GetValue<int>()));
        Assert.Equal(new[] { 0, 1, 2 }, list.Select(c => c.Variables["page"]!.GetValue<int>()));

        // Three orders across two pages, plus two till receipts.
        Assert.Equal(5, result.Receipts.Count);
        Assert.True(result.Complete);
    }

    [Fact]
    public async Task The_confirmed_order_input_is_sent_verbatim()
    {
        var graphql = FakeJumboGraphQl.Recorded();
        using var ctx = new FakeJobContext { Material = JumboFixtures.LiveSession };

        await Adapter(graphql).FetchAsync(ctx, SinceJuly, CancellationToken.None);

        var input = Assert.Single(graphql.ListCalls).Variables["ordersInput"]!;

        Assert.Equal("DESC", input["direction"]!.GetValue<string>());
        Assert.Equal("deliveryDate", input["sortBy"]!.GetValue<string>());

        // What restricts the list to completed orders. Without it the walk
        // would return baskets that are still open.
        Assert.Equal("CLOSED", input["statusCategory"]!.GetValue<string>());
    }

    /// <summary>
    /// No <c>extensions.persistedQuery</c> anywhere, in either direction. The
    /// router accepts plain documents from an authenticated session, so the
    /// APQ handshake this adapter used to carry was machinery for a protocol
    /// that is not in play.
    /// </summary>
    [Fact]
    public async Task Every_call_sends_a_plain_document_and_never_a_persisted_query()
    {
        var graphql = FakeJumboGraphQl.Recorded();
        using var ctx = new FakeJobContext { Material = JumboFixtures.LiveSession };

        await Adapter(graphql).FetchAsync(ctx, SinceJuly, CancellationToken.None);

        Assert.All(graphql.Calls, call =>
        {
            Assert.False(string.IsNullOrWhiteSpace(call.Query));
            Assert.False(call.Variables.ContainsKey("extensions"));

            var body = HttpJumboGraphQlClient.Body(call);
            Assert.False(body.ContainsKey("extensions"));
        });
    }

    /// <summary>
    /// <c>OrderPagesOrder</c>, and the name matters: the plausible-looking
    /// transposition <c>OrdersPageOrders</c> is corroborated nowhere and would
    /// be a schema validation error rather than a slower path. <c>$orderId</c>
    /// is a <c>Float!</c>, so it goes on the wire as a number.
    /// </summary>
    [Fact]
    public async Task Line_items_are_asked_for_by_the_confirmed_operation_with_a_numeric_order_id()
    {
        var graphql = FakeJumboGraphQl.Recorded();
        using var ctx = new FakeJobContext { Material = JumboFixtures.LiveSession };

        await Adapter(graphql).FetchAsync(ctx, SinceJuly, CancellationToken.None);

        var detail = graphql.Calls.First(c => c.OperationName == "OrderPagesOrder");

        Assert.Contains("OrderPagesOrder(", detail.Query, StringComparison.Ordinal);
        Assert.Contains("$orderId: Float!", detail.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("OrdersPageOrders", detail.Query, StringComparison.Ordinal);

        Assert.Equal(90211d, detail.Variables["orderId"]!.GetValue<double>());
    }

    [Fact]
    public void A_non_numeric_order_id_is_a_shape_change_rather_than_a_bad_request()
    {
        var error = Assert.Throws<ConnectorException>(() => JumboAdapter.OrderDetailVariables("ORD-90211"));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains("Float!", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_items_no_order_detail_is_fetched_at_all()
    {
        var graphql = FakeJumboGraphQl.Recorded();
        using var ctx = new FakeJobContext { Material = JumboFixtures.LiveSession };

        var result = await Adapter(graphql).FetchAsync(
            ctx, Requests.Receipts(since: Requests.Day(2026, 7, 1), items: false), CancellationToken.None);

        Assert.DoesNotContain(graphql.Calls, c => c.OperationName == "OrderPagesOrder");

        // The totals still arrive: they come from the list, not the detail.
        Assert.Equal(3113, result.Receipts.First(r => r.ExternalId == "order-90211").Total.Value);

        // A till receipt has no total in the list at all, so its detail is
        // fetched whether or not items were asked for.
        Assert.Single(graphql.Calls, c => c.OperationName == "GetDigitalReceipt");
        Assert.Equal(1106, result.Receipts.First(r => r.ExternalId.StartsWith("receipt-", StringComparison.Ordinal))
            .Total.Value);
    }

    // ---- in-store receipts that only half-read -----------------------------

    /// <summary>
    /// A layout that yields a total but no items is still a purchase the user
    /// made. It is emitted and flagged, never dropped - and never allowed to
    /// claim it reconciles, because an empty item list reconciles trivially
    /// and that would be the parser vouching for work it did not do.
    /// </summary>
    [Fact]
    public async Task A_receipt_whose_items_will_not_parse_is_emitted_unreconciled_rather_than_dropped()
    {
        var graphql = FakeJumboGraphQl.Recorded();
        graphql.DigitalReceipts["*"] = FixtureCatalog.Read("jumbo/digital-receipt-no-items.json");

        using var ctx = new FakeJobContext { Material = JumboFixtures.LiveSession };
        var result = await Adapter(graphql).FetchAsync(ctx, SinceJuly, CancellationToken.None);

        var till = Assert.Single(result.Receipts, r => r.ExternalId == "receipt-TX-2026-07-19-778812");

        Assert.Equal(1106, till.Total.Value);
        Assert.Empty(till.Items);
        Assert.False(till.Reconciled);

        // The orders are untouched by the till template moving.
        Assert.Equal(3, result.Receipts.Count);
        Assert.All(result.Receipts.Where(r => r.ExternalId.StartsWith("order-", StringComparison.Ordinal)),
            r => Assert.True(r.Reconciled));
    }

    [Fact]
    public async Task A_receipt_with_no_readable_total_names_what_was_missing()
    {
        var graphql = FakeJumboGraphQl.Recorded();
        graphql.DigitalReceipts["*"] = FixtureCatalog.Read("jumbo/digital-receipt-no-total.json");

        using var ctx = new FakeJobContext { Material = JumboFixtures.LiveSession };

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter(graphql).FetchAsync(ctx, SinceJuly, CancellationToken.None));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains("TX-2026-07-19-778812", error.Detail, StringComparison.Ordinal);
        Assert.Contains("Totaal", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_receipt_rendered_as_a_picture_states_that_nothing_else_carries_its_total()
    {
        var graphql = FakeJumboGraphQl.Recorded();
        graphql.DigitalReceipts["*"] = FixtureCatalog.Read("jumbo/digital-receipt-image-only.json");

        using var ctx = new FakeJobContext { Material = JumboFixtures.LiveSession };

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter(graphql).FetchAsync(ctx, SinceJuly, CancellationToken.None));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains("JSON", error.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The escape hatch: an operator meeting a legacy image-only receipt can
    /// keep syncing tonight and fix the parser tomorrow - and the pass reports
    /// itself partial rather than quietly returning fewer shops.
    /// </summary>
    [Fact]
    public async Task An_operator_can_skip_an_unreadable_receipt_and_the_pass_says_it_is_partial()
    {
        var graphql = FakeJumboGraphQl.Recorded();
        graphql.DigitalReceipts["*"] = FixtureCatalog.Read("jumbo/digital-receipt-image-only.json");

        using var ctx = new FakeJobContext { Material = JumboFixtures.LiveSession };
        var options = new JumboOptions { SkipUnreadableStoreReceipts = true };

        var result = await Adapter(graphql, options).FetchAsync(ctx, SinceJuly, CancellationToken.None);

        Assert.Equal(2, result.Receipts.Count);
        Assert.All(result.Receipts, r => Assert.StartsWith("order-", r.ExternalId, StringComparison.Ordinal));
        Assert.False(result.Complete);
    }

    [Fact]
    public async Task The_till_receipt_detail_budget_bounds_a_pass_and_reports_it_partial()
    {
        var graphql = FakeJumboGraphQl.Recorded(
            FixtureCatalog.Read("jumbo/walk-page-1.json"),
            FixtureCatalog.Read("jumbo/walk-page-2.json"));

        using var ctx = new FakeJobContext { Material = JumboFixtures.LiveSession };
        var options = new JumboOptions
        {
            OrdersPageSize = 2,
            ReceiptsPageSize = 1,
            StoreReceiptDetailBudget = 1,
        };

        var result = await Adapter(graphql, options).FetchAsync(
            ctx, Requests.Receipts(since: Requests.Day(2026, 7, 1), items: false), CancellationToken.None);

        // Newest first, so the budget cuts the oldest.
        Assert.Single(graphql.Calls, c => c.OperationName == "GetDigitalReceipt");
        Assert.Contains(result.Receipts, r => r.ExternalId == "receipt-TX-2026-07-19-778812");
        Assert.DoesNotContain(result.Receipts, r => r.ExternalId == "receipt-TX-2026-07-05-771204");
        Assert.False(result.Complete);
    }

    // ---- the window and the walk's guards ----------------------------------

    [Fact]
    public async Task A_page_entirely_older_than_the_window_ends_the_walk()
    {
        var graphql = FakeJumboGraphQl.Recorded();
        using var ctx = new FakeJobContext { Material = JumboFixtures.LiveSession };

        var result = await Adapter(graphql).FetchAsync(
            ctx, Requests.Receipts(since: Requests.Day(2026, 8, 1)), CancellationToken.None);

        // Newest first, so one whole page below the window means every later
        // page is too - and walking the full history on every sync is the call
        // pattern most likely to trip protection.
        Assert.Empty(result.Receipts);
        Assert.Single(graphql.ListCalls);
    }

    [Fact]
    public async Task A_page_that_repeats_stops_the_walk_instead_of_replaying_it()
    {
        // The last recorded page repeats, which is what a cursor that is not
        // advancing anything upstream actually looks like from here.
        var graphql = FakeJumboGraphQl.Recorded(FixtureCatalog.Read("jumbo/walk-page-1.json"));

        using var ctx = new FakeJobContext { Material = JumboFixtures.LiveSession };
        var options = new JumboOptions { OrdersPageSize = 2, ReceiptsPageSize = 1 };

        var result = await Adapter(graphql, options).FetchAsync(
            ctx, Requests.Receipts(since: Requests.Day(2026, 7, 1), items: false), CancellationToken.None);

        // Two passes, not forty: the second sees nothing it has not already
        // seen on either side.
        Assert.Equal(2, graphql.ListCalls.Count);
        Assert.Equal(3, result.Receipts.Count);
    }

    // ---- session and resource ----------------------------------------------

    /// <summary>
    /// The device header is not required - the confirmed working client sends
    /// none at all - so a bundle without one is served rather than sent back
    /// through an Auth0 login behind Akamai for a header the API may never
    /// read. This adapter used to refuse it.
    /// </summary>
    [Fact]
    public async Task A_session_with_no_device_id_is_still_fetched_for()
    {
        var graphql = FakeJumboGraphQl.Recorded();
        using var ctx = new FakeJobContext
        {
            Material = new SessionMaterial { StorageState = FixtureCatalog.Read("jumbo/storage-state.json") },
        };

        var result = await Adapter(graphql).FetchAsync(ctx, SinceJuly, CancellationToken.None);

        Assert.Equal(3, result.Receipts.Count);
        Assert.All(graphql.DeviceIds, Assert.Null);
    }

    [Fact]
    public async Task An_auth_error_in_the_body_ends_the_session()
    {
        var graphql = FakeJumboGraphQl.Recorded(FixtureCatalog.Read("jumbo/graphql-errors.json"));
        using var ctx = new FakeJobContext { Material = JumboFixtures.LiveSession };

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter(graphql).FetchAsync(ctx, SinceJuly, CancellationToken.None));

        // A fetch never had a password, so a rejection here is a dead cookie
        // and not a wrong credential.
        Assert.Equal(ErrorCode.SessionExpired, error.Code);
        Assert.NotEqual(ErrorCode.InvalidCredentials, error.Code);
    }

    [Fact]
    public async Task An_unknown_resource_is_refused_before_any_call()
    {
        var graphql = FakeJumboGraphQl.Recorded();
        using var ctx = new FakeJobContext { Material = JumboFixtures.LiveSession };

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter(graphql).FetchAsync(
                ctx, new ResourceRequest { ResourceId = "orders" }, CancellationToken.None));

        Assert.Equal(ErrorCode.UnsupportedResource, error.Code);
        Assert.Empty(graphql.Calls);
    }
}
