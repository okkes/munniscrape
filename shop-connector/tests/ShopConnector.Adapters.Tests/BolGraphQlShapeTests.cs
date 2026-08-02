using System.Text.Json;
using Connector.Kit.Errors;
using Connector.Kit.Normalization;
using ShopConnector.Adapters.Bol;
using ShopConnector.Adapters.Fixtures;
using ShopConnector.Adapters.Support;
using ShopConnector.Adapters.Tests.Support;
using Xunit;

namespace ShopConnector.Adapters.Tests;

/// <summary>
/// bol's order overview, read the way bol's own front end reads it.
///
/// This is the first bol shape written against a real payload rather than a
/// guess. The two before it were both plausible and the file said so - "nobody
/// on this project has seen this page" - and the URL they asked for now 404s.
/// A capture on 2026-08-02 settled it: the page is a shell, and every
/// "Toon meer" posts <c>OrdersOverviewClient</c> to <c>/api/graphql</c>.
///
/// The fixture keeps every key and nesting level of that response and none of
/// the purchases: a public repo is no place for somebody's order history.
/// </summary>
public sealed class BolGraphQlShapeTests
{
    private static readonly BolOptions Options = new();

    private static readonly TimeZoneInfo Zone = RetailZones.Dutch;

    private static readonly BolGraphQlShape Shape = new();

    private static string Fixture() => FixtureCatalog.Read("bol/orders-graphql.json");

    private static IReadOnlyList<BolOrder> Parse() => Shape.Parse(Fixture(), Options, Zone);

    // ---- the request --------------------------------------------------------

    [Fact]
    public void The_call_is_the_persisted_operation_bol_sends_itself()
    {
        using var body = JsonDocument.Parse(Shape.Body(Options, page: 1)!);
        var root = body.RootElement;

        Assert.Equal("OrdersOverviewClient", root.GetProperty("operationName").GetString());

        // An automatic persisted query: the hash alone, no document. bol's
        // schema is not public, so there is nothing to fall back to and
        // inventing a document would be guessing at the shape again.
        var persisted = root.GetProperty("extensions").GetProperty("persistedQuery");
        Assert.Equal(1, persisted.GetProperty("version").GetInt32());
        Assert.Equal(
            "sha256:d195253b815a6082cdee63bef6234513d5c0520c9f064e96fc1a9037d689add2",
            persisted.GetProperty("sha256Hash").GetString());

        Assert.Equal("https://www.bol.com/api/graphql", Shape.Url(Options, page: 1));
    }

    /// <summary>
    /// The cursor is an offset in ORDERS and a string, which is how bol sends
    /// it: the first click asked for "5" with five already on the page.
    /// </summary>
    [Theory]
    [InlineData(1, "0")]
    [InlineData(2, "5")]
    [InlineData(3, "10")]
    public void The_cursor_counts_orders_from_zero_as_a_string(int page, string expected)
    {
        using var body = JsonDocument.Parse(Shape.Body(Options, page)!);

        var after = body.RootElement.GetProperty("variables").GetProperty("after");

        Assert.Equal(JsonValueKind.String, after.ValueKind);
        Assert.Equal(expected, after.GetString());
    }

    [Fact]
    public void A_page_size_change_moves_the_cursor_with_it()
    {
        var options = Options with { OrdersPageSize = 20 };

        using var body = JsonDocument.Parse(Shape.Body(options, page: 3)!);

        Assert.Equal("40", body.RootElement.GetProperty("variables").GetProperty("after").GetString());
    }

    // ---- the money ----------------------------------------------------------

    /// <summary>
    /// The one bol value worth being certain about. <c>amount</c> is a number
    /// in euros and it is the price of ONE - the page showed "2 x EUR 21,24"
    /// against an amount of 21.24. Declared minor instead, every bol receipt
    /// would be a hundredth of the truth AND would still reconcile, because the
    /// same number feeds both sides of the check.
    /// </summary>
    [Fact]
    public void A_line_total_is_the_unit_price_times_the_quantity()
    {
        var order = Parse().Single(o => o.Id == "A000TEST002");

        var twoOf = order.Items.Single(i => i.Name.Contains("Prullenbak", StringComparison.Ordinal));

        Assert.Equal(2, twoOf.Quantity);
        Assert.Equal(new Money(2124, "EUR"), twoOf.UnitPrice);   // EUR 21.24 as minor units
        Assert.Equal(4248, twoOf.Total.Value);           // and twice that
        Assert.Equal("EUR", twoOf.Total.Currency);
    }

    [Fact]
    public void An_orders_total_is_the_sum_of_its_own_lines()
    {
        var order = Parse().Single(o => o.Id == "A000TEST002");

        // 2 x 21.24 plus 1 x 11.99.
        Assert.Equal(4248 + 1199, order.Total.Value);
        Assert.Equal(order.Items.Sum(i => i.Total.Value), order.Total.Value);
    }

    // ---- the orders ---------------------------------------------------------

    [Fact]
    public void Each_order_carries_its_reference_and_the_moment_it_was_placed()
    {
        var order = Parse().Single(o => o.Id == "A000TEST001");

        // bol states a local wall clock with no offset, so it is read in
        // Europe/Amsterdam - a near-midnight order dated a day out is worse
        // than one never matched.
        Assert.Equal(new DateTimeOffset(new DateTime(2026, 7, 29, 22, 33, 10, 329), TimeSpan.FromHours(2)), order.PlacedAt);

        var item = Assert.Single(order.Items);
        Assert.Equal("Wall mounts", item.Name);
        Assert.Equal(1, item.Quantity);
        Assert.Equal(3175, item.Total.Value);
    }

    /// <summary>
    /// bol is a marketplace and the seller is stated per LINE, not per order,
    /// so one order can genuinely carry several.
    /// </summary>
    [Fact]
    public void The_seller_comes_off_the_line_because_that_is_where_bol_states_it()
    {
        var orders = Parse();

        Assert.Equal("Snow & Outdoor", orders.Single(o => o.Id == "A000TEST001").SellerName);
        Assert.Equal("bol", orders.Single(o => o.Id == "A000TEST002").SellerName);
    }

    [Fact]
    public void Every_order_in_the_page_is_read()
    {
        Assert.Equal(["A000TEST001", "A000TEST002"], Parse().Select(o => o.Id));
    }

    // ---- what it refuses to guess -------------------------------------------

    /// <summary>
    /// An account that has never ordered is an answer. "You have never shopped
    /// here" is only allowed when bol says so itself.
    /// </summary>
    [Theory]
    [InlineData("""{"data":{"me":{"orders":[]}}}""")]
    [InlineData("""{"data":{"me":{"orders":null}}}""")]
    public void An_empty_history_is_an_answer_and_not_a_shape_change(string body)
    {
        Assert.Empty(Shape.Parse(body, Options, Zone));
    }

    [Theory]
    [InlineData("""{"data":{"me":{}}}""", "data.me.orders")]
    [InlineData("""{"data":{}}""", "data.me.orders")]
    [InlineData("""{"data":{"me":{"orders":{}}}}""", "not a list")]
    public void A_response_that_is_not_the_shape_we_know_is_provider_changed(string body, string says)
    {
        var error = Assert.Throws<ConnectorException>(() => Shape.Parse(body, Options, Zone));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains(says, error.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The failure this shape is most likely to meet in the wild: bol ships a
    /// new front end and stops recognising the hash we pin. GraphQL reports it
    /// in the body with a 200, so it has to be read before the data is trusted.
    /// </summary>
    [Fact]
    public void A_forgotten_persisted_query_names_the_setting_an_operator_must_change()
    {
        const string refused = """{"errors":[{"message":"PersistedQueryNotFound"}]}""";

        var error = Assert.Throws<ConnectorException>(() => Shape.Parse(refused, Options, Zone));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains("PersistedQueryNotFound", error.Detail, StringComparison.Ordinal);
        Assert.Contains("OrdersPersistedQueryHash", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_body_that_is_not_json_at_all_says_so()
    {
        var error = Assert.Throws<ConnectorException>(() => Shape.Parse("<html>nope</html>", Options, Zone));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains("not JSON", error.Detail, StringComparison.Ordinal);
    }

    // ---- the flag ------------------------------------------------------------

    /// <summary>
    /// bol states no order total, so ours is a sum of its own lines and
    /// reconciling it would compare a number with itself. Saying "derived" is
    /// what keeps the reconciliation flag meaning one thing across providers:
    /// a stated total was checked and agreed.
    /// </summary>
    [Fact]
    public void The_shape_declares_that_its_total_is_derived()
    {
        Assert.True(Shape.TotalIsDerived);

        // And the platform draws the right conclusion from it.
        var receipt = new Receipt
        {
            Id = "rcp_1",
            ExternalId = "A000TEST001",
            ContentHash = "h",
            Merchant = new Merchant { Id = "bol", Name = "bol.com" },
            PurchasedAt = DateTimeOffset.UtcNow,
            Total = new Money(3175, "EUR"),
            TotalIsDerived = true,
            Items =
            [
                new ReceiptItem { Name = "Wall mounts", Quantity = 1, Total = new Money(3175, "EUR") },
            ],
        };

        // The lines agree with the total exactly - they are where it came from -
        // and it is still not reconciled, because nothing independent was checked.
        Assert.False(receipt.WithReconciliation().Reconciled);
    }
}
