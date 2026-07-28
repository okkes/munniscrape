using System.Text.Json;
using System.Text.Json.Nodes;
using Connector.Kit.Errors;
using Connector.Kit.Normalization;
using ShopConnector.Adapters.Picnic;
using ShopConnector.Adapters.Support;
using ShopConnector.Adapters.Tests.Support;
using Xunit;

namespace ShopConnector.Adapters.Tests;

/// <summary>
/// The parse layer, driven straight off the recorded payloads.
///
/// This is the suite that has to catch Picnic changing shape before a user
/// does - and, more importantly than any of it, the one that pins the money
/// unit. Cents are declared, not sniffed: getting that wrong is silent, it
/// survives every plausibility check a human would apply, and it has already
/// happened once on this platform.
/// </summary>
public sealed class PicnicParsingTests
{
    private static readonly PicnicOptions Options = new();

    /// <summary>One order out of a delivery detail, detached from its document.</summary>
    private static JsonElement Order(string fixture, int index = 0)
    {
        using var document = Fixture.Doc(fixture);
        return document.RootElement.GetProperty("orders")[index].Clone();
    }

    // ---- money --------------------------------------------------------------

    [Fact]
    public void Amounts_are_read_as_cents_because_picnic_documents_cents()
    {
        var items = PicnicDeliveryParser.ParseItems(Order("picnic/delivery-detail.json"), Options);

        // Five order lines plus the deposit Picnic charges on top of them.
        Assert.Equal(6, items.Count);

        // 235 cents is EUR 2.35 - the value goes through untouched under
        // MoneyUnit.Minor. If this were ever read as MajorDecimal every total
        // would be a hundred times the truth, and nothing downstream would
        // notice: the item unit would be wrong the same way, so reconciliation
        // would still pass.
        Assert.Equal(new long[] { 235, 270, 199, 239, 579, 35 }, items.Select(i => i.Total.Value));
        Assert.All(items, i => Assert.Equal("EUR", i.Total.Currency));

        Assert.Equal(MoneyUnit.Minor, Options.LineUnit);
        Assert.Equal(MoneyUnit.Minor, Options.TotalUnit);
        Assert.Equal(MoneyUnit.Minor, Options.ArticleUnit);
        Assert.Equal(MoneyUnit.Minor, Options.DiscountUnit);
    }

    [Fact]
    public void The_lines_net_of_promotions_plus_the_deposit_are_exactly_the_stated_total()
    {
        var order = Order("picnic/delivery-detail.json");
        var items = PicnicDeliveryParser.ParseItems(order, Options);

        // The identity confirmed against a recorded response:
        //   total_price = SUM(line.display_price) - total_savings + total_deposit
        var gross = items.Sum(i => i.Total.Value);
        var savings = items.Sum(i => i.Discount?.Amount.Value ?? 0);

        Assert.Equal(1522 + 35, gross);
        Assert.Equal(-88, savings);
        Assert.Equal(88, order.GetProperty("total_savings").GetInt64());

        // ...which is the receipt's own total, to the cent.
        Assert.Equal(order.GetProperty("total_price").GetInt64(), gross + savings);
    }

    [Fact]
    public void A_deposit_line_exists_because_picnic_charges_it_on_top()
    {
        var items = PicnicDeliveryParser.ParseItems(Order("picnic/delivery-detail-split.json"), Options);

        var deposit = Assert.Single(items, i => i.Name == "deposit");

        // Without this line the items cannot sum to the total, and every
        // receipt with a single returnable bottle on it would be flagged.
        Assert.Equal(285, deposit.Total.Value);

        // Picnic's own count: one bag plus ten bottles.
        Assert.Equal(11m, deposit.Quantity);
        Assert.Null(deposit.Discount);
    }

    [Fact]
    public void An_order_with_no_deposit_gets_no_deposit_line()
    {
        var items = PicnicDeliveryParser.ParseItems(Order("picnic/delivery-detail-split.json", index: 1), Options);

        Assert.DoesNotContain(items, i => i.Name == "deposit");
        Assert.Equal(3, items.Count);
    }

    // ---- items --------------------------------------------------------------

    [Fact]
    public void A_discount_is_negative_and_carries_picnics_own_wording()
    {
        var items = PicnicDeliveryParser.ParseItems(Order("picnic/delivery-detail.json"), Options);

        var promoted = Assert.Single(items, i => i.Name == "Tante Fanny flammkuchendeeg");

        // Picnic states the discount indirectly: a PRICE decorator carries what
        // was actually charged (159) beside the line's gross (199). The
        // difference is the discount, and the schema requires it negative so
        // that summing is unconditional.
        Assert.Equal(199, promoted.Total.Value);
        Assert.NotNull(promoted.Discount);
        Assert.Equal(-40, promoted.Discount.Amount.Value);
        Assert.Equal("20% korting", promoted.Discount.Label);

        Assert.Null(Assert.Single(items, i => i.Name == "Anta Flu keelpastilles eucalyptus").Discount);
    }

    [Fact]
    public void Quantity_comes_from_the_decorator_and_not_from_the_array_length()
    {
        var items = PicnicDeliveryParser.ParseItems(Order("picnic/delivery-detail.json"), Options);

        var yoghurt = Assert.Single(items, i => i.Name == "Picnic halfvolle yoghurt");

        // Two yoghurts, stated by a QUANTITY decorator beside a SINGLE article
        // entry. Reading the array length would report one - wrong in a way
        // nothing downstream could catch, because a quantity is not money and
        // reconciliation would still pass.
        Assert.Equal(2m, yoghurt.Quantity);
        Assert.Equal(270, yoghurt.Total.Value);

        // Picnic's documented "base per-unit price in cents". Stated because it
        // is the provider's own per-unit figure - and deliberately NOT used to
        // derive the line total, which it does not multiply out to: 2 x 139 is
        // 278, and 270 was charged.
        Assert.Equal(139L, yoghurt.UnitPrice?.Value);
    }

    [Fact]
    public void The_payment_states_the_iban_tail_and_an_explicit_null_card()
    {
        var payment = PicnicDeliveryParser.ParsePayment(Order("picnic/delivery-detail.json"));

        Assert.Equal("ideal", payment.Method);
        Assert.Equal("2173", payment.IbanTail);

        // Explicit, not omitted: Picnic never states a card tail, and the
        // consumer needs to know its match will be weaker rather than reading
        // the gap as an oversight.
        Assert.Null(payment.CardLast4);
    }

    [Fact]
    public void A_direct_debit_lands_in_other_because_the_schema_has_no_word_for_it()
    {
        var payment = PicnicDeliveryParser.ParsePayment(Order("picnic/delivery-detail-split.json"));

        // The schema's vocabulary is card / cash / ideal / other. The IBAN tail
        // beside it carries the identifying detail either way.
        Assert.Equal("other", payment.Method);
        Assert.Equal("4408", payment.IbanTail);
    }

    [Fact]
    public void An_order_with_no_transaction_info_states_nulls_rather_than_nothing()
    {
        var order = Fixture.Object("picnic/delivery-detail.json");
        order["orders"]!.AsArray()[0]!.AsObject().Remove("transaction_info");

        using var document = Fixture.Reparse(order);
        var payment = PicnicDeliveryParser.ParsePayment(document.RootElement.GetProperty("orders")[0]);

        Assert.Null(payment.Method);
        Assert.Null(payment.IbanTail);
        Assert.Null(payment.CardLast4);
    }

    // ---- time ---------------------------------------------------------------

    [Fact]
    public void Purchased_at_is_when_the_order_was_placed_not_when_the_van_arrived()
    {
        using var document = Fixture.Doc("picnic/deliveries-summary.json");
        var summaries = PicnicDeliveryParser.ParseSummary(document.RootElement, Options, RetailZones.Dutch);

        var order = Assert.Single(summaries, s => s.OrderId == "001-201-7788");

        // Picnic is a delivery service, so an order has two moments and they are
        // routinely a day apart. creation_time is the checkout - the moment the
        // basket became a committed order at a fixed price. delivery_time is a
        // fact about a van, and dating a purchase by it puts it on a day the
        // user did nothing.
        Assert.Equal(new DateTimeOffset(2026, 7, 17, 20, 14, 7, 412, TimeSpan.FromHours(2)), order.PurchasedAt);
        Assert.Equal(17, order.PurchasedAt.Day);

        // The delivery landed on the 18th, and that is not when it was bought.
        Assert.Equal(
            "2026-07-18T19:48:21.005+02:00",
            document.RootElement[0].GetProperty("delivery_time").GetProperty("start").GetString());
    }

    [Fact]
    public void The_offset_picnic_states_is_honoured_rather_than_reinterpreted()
    {
        using var document = Fixture.Doc("picnic/deliveries-summary.json");
        var summaries = PicnicDeliveryParser.ParseSummary(document.RootElement, Options, RetailZones.Dutch);

        // Never a bare date. Every timestamp here carries +02:00 of its own, and
        // an order placed at 00:18 local would land on the previous day if the
        // offset were dropped or the machine's zone were used instead.
        Assert.All(summaries, s => Assert.Equal(TimeSpan.FromHours(2), s.PurchasedAt.Offset));

        var midnight = Assert.Single(summaries, s => s.OrderId == "002-330-9902");
        Assert.Equal(11, midnight.PurchasedAt.Day);
        Assert.Equal(10, midnight.PurchasedAt.UtcDateTime.Day);
    }

    [Fact]
    public void One_delivery_can_hold_more_than_one_purchase()
    {
        using var document = Fixture.Doc("picnic/deliveries-summary.json");
        var summaries = PicnicDeliveryParser.ParseSummary(document.RootElement, Options, RetailZones.Dutch);

        // Picnic lets a household add a second order to a slot up to the
        // cut-off. Each has its own id, its own creation time and its own total,
        // so each is its own receipt - keying on the delivery would merge two
        // purchases into one and lose a total.
        Assert.Equal(4, summaries.Count);

        var shared = summaries.Where(s => s.DeliveryId == "dlv-1145-bbb").ToList();
        Assert.Equal(2, shared.Count);
        Assert.Equal(new long[] { 6290, 2455 }, shared.Select(s => s.Total.Value));
    }

    [Fact]
    public void An_empty_history_is_an_empty_list_and_not_an_error()
    {
        using var document = Fixture.Doc("picnic/deliveries-empty.json");

        Assert.Empty(PicnicDeliveryParser.ParseSummary(document.RootElement, Options, RetailZones.Dutch));
    }

    // ---- shapes we cannot parse --------------------------------------------

    [Fact]
    public void A_summary_that_is_not_an_array_is_a_shape_change()
    {
        using var document = JsonDocument.Parse("""{"message":"moved"}""");

        var error = Assert.Throws<ConnectorException>(
            () => PicnicDeliveryParser.ParseSummary(document.RootElement, Options, RetailZones.Dutch));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains("array", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_order_with_no_total_price_is_a_shape_change_that_names_the_field()
    {
        var summary = Fixture.Array("picnic/deliveries-summary.json");
        summary[0]!["orders"]!.AsArray()[0]!.AsObject().Remove("total_price");

        using var document = Fixture.Reparse(summary);

        var error = Assert.Throws<ConnectorException>(
            () => PicnicDeliveryParser.ParseSummary(document.RootElement, Options, RetailZones.Dutch));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);

        // Naming what was missing is the whole value of the error: it turns an
        // afternoon of guessing against a live account into a five-second fix.
        Assert.Contains("total_price", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_order_with_no_creation_time_anywhere_is_a_shape_change()
    {
        var summary = Fixture.Array("picnic/deliveries-summary.json");
        summary[0]!.AsObject().Remove("creation_time");
        summary[0]!["orders"]!.AsArray()[0]!.AsObject().Remove("creation_time");

        using var document = Fixture.Reparse(summary);

        var error = Assert.Throws<ConnectorException>(
            () => PicnicDeliveryParser.ParseSummary(document.RootElement, Options, RetailZones.Dutch));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains("creation_time", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_order_with_no_id_is_a_shape_change()
    {
        var summary = Fixture.Array("picnic/deliveries-summary.json");
        summary[0]!["orders"]!.AsArray()[0]!.AsObject().Remove("id");

        using var document = Fixture.Reparse(summary);

        var error = Assert.Throws<ConnectorException>(
            () => PicnicDeliveryParser.ParseSummary(document.RootElement, Options, RetailZones.Dutch));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
    }

    [Fact]
    public void A_priced_line_with_no_product_name_is_a_shape_change()
    {
        var detail = Fixture.Object("picnic/delivery-detail.json");
        detail["orders"]!.AsArray()[0]!["items"]!.AsArray()[0]!["items"]!.AsArray()[0]!.AsObject().Remove("name");

        using var document = Fixture.Reparse(detail);

        var error = Assert.Throws<ConnectorException>(
            () => PicnicDeliveryParser.ParseItems(document.RootElement.GetProperty("orders")[0], Options));

        // Skipping the line would silently drop money out of a receipt that
        // still states its total, and inventing a name would report a product
        // Picnic never sold.
        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains("name", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_detail_with_no_orders_array_is_a_shape_change()
    {
        var detail = Fixture.Object("picnic/delivery-detail.json");
        detail.Remove("orders");

        using var document = Fixture.Reparse(detail);

        var error = Assert.Throws<ConnectorException>(
            () => PicnicDeliveryParser.OrdersById(document.RootElement));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains("orders", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Extra_fields_are_tolerated_because_retailers_add_them_without_telling_anyone()
    {
        var detail = Fixture.Object("picnic/delivery-detail.json");
        var order = detail["orders"]!.AsArray()[0]!.AsObject();
        order["loyalty_programme"] = "picnic-plus";
        order["items"]!.AsArray()[0]!.AsObject()["sustainability_score"] = 4;

        using var document = Fixture.Reparse(detail);
        var items = PicnicDeliveryParser.ParseItems(document.RootElement.GetProperty("orders")[0], Options);

        Assert.Equal(6, items.Count);
    }

    [Fact]
    public void An_unrecognised_price_decorator_is_left_out_rather_than_guessed_at()
    {
        // A PRICE decorator ABOVE the line's own gross is not a discount, and
        // inventing a positive one would corrupt the sum quietly. Leaving it out
        // lets reconciliation flag the receipt, which is the loud outcome.
        var detail = Fixture.Object("picnic/delivery-detail.json");
        var line = detail["orders"]!.AsArray()[0]!["items"]!.AsArray()[2]!.AsObject();
        line["decorators"] = new JsonArray(
            new JsonObject { ["type"] = "PRICE", ["display_price"] = 250 });

        using var document = Fixture.Reparse(detail);
        var items = PicnicDeliveryParser.ParseItems(document.RootElement.GetProperty("orders")[0], Options);

        Assert.Null(Assert.Single(items, i => i.Name == "Tante Fanny flammkuchendeeg").Discount);
    }
}
