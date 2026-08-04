using System.Text.Json.Nodes;
using Connector.Kit.Errors;
using ShopConnector.Adapters.LidlPlus;
using ShopConnector.Adapters.Support;
using ShopConnector.Adapters.Tests.Support;
using Xunit;

namespace ShopConnector.Adapters.Tests;

/// <summary>
/// Lidl Plus's v2 list and v3 detail. The two endpoints sit on different API
/// versions, which is not a typo and must not be "fixed".
///
/// The detail fixture was replaced on 2026-08-04 with the shape a real one
/// has, and it is nothing like what was here before. There is no line-item
/// collection at all - no itemsLine, no items, no lines, no discounts array.
/// A v3 detail is "ticketType": "HTML" and carries the PAPER RECEIPT in
/// htmlPrintedReceipt, with each article's facts on data attributes of the
/// spans it is printed from.
///
/// The load-bearing case is now the weighed article. 0,850 kg of bananas at
/// 1,79/kg is printed as 1,52, and 0.850 x 1.79 is 1.5215 - so a parser that
/// multiplied instead of reading the printed total would put every receipt
/// containing loose produce a cent out and fail reconciliation for a reason
/// nobody could see on screen.
/// </summary>
public sealed class LidlPlusParsingTests
{
    private const string ListFixture = "lidl/tickets-page-1.json";
    private const string EmptyPageFixture = "lidl/tickets-page-2.json";
    private const string DetailFixture = "lidl/ticket-detail.json";

    private static readonly LidlPlusOptions Options = new();

    private static TimeZoneInfo Dutch => RetailZones.For("NL");

    [Fact]
    public void List_reads_comma_decimal_totals_into_minor_units()
    {
        using var document = Fixture.Doc(ListFixture);

        var tickets = LidlPlusTicketParser.ParseList(document.RootElement, Options, Dutch);

        Assert.Equal(2, tickets.Count);
        Assert.Equal("lidl-2026-07-18-8801", tickets[0].Id);
        Assert.Equal(1427, tickets[0].Total.Value);
        Assert.Equal("EUR", tickets[0].Total.Currency);
        Assert.Equal(845, tickets[1].Total.Value);
        Assert.Equal("Lidl Utrecht Kanaleneiland", tickets[0].StoreName);
    }

    [Fact]
    public void A_timestamp_without_an_offset_is_read_in_the_provider_s_own_zone()
    {
        using var document = Fixture.Doc(ListFixture);

        var ticket = LidlPlusTicketParser.ParseList(document.RootElement, Options, Dutch)[0];

        // "2026-07-18T18:04:00" states no offset. Interpreting it in the
        // agent's zone would date a receipt by where the fleet happens to
        // run; interpreting it in the provider's country gives +02:00 in
        // July, which is what the shop's clock actually said.
        Assert.Equal(TimeSpan.FromHours(2), ticket.PurchasedAt.Offset);
        Assert.Equal(new DateTimeOffset(2026, 7, 18, 18, 4, 0, TimeSpan.FromHours(2)), ticket.PurchasedAt);
    }

    [Fact]
    public void An_empty_page_is_a_real_answer_and_not_a_shape_change()
    {
        using var document = Fixture.Doc(EmptyPageFixture);

        // This is what terminates pagination; treating it as a failure would
        // make every complete fetch look broken.
        Assert.Empty(LidlPlusTicketParser.ParseList(document.RootElement, Options, Dutch));
    }

    [Fact]
    public void The_printed_receipt_is_where_the_lines_are()
    {
        var items = Items();

        Assert.Equal(3, items.Count);
        Assert.Equal(["Bruin brood", "Halfvolle melk", "Bananen"], items.Select(i => i.Name));

        // A plain article: one of it, at the price printed beside it.
        Assert.Equal(129, items[0].Total.Value);
        Assert.Equal(1m, items[0].Quantity);
        Assert.Equal(129, items[0].UnitPrice?.Value);
        Assert.Null(items[0].Discount);
    }

    /// <summary>
    /// The case that decides whether this parser can be trusted at all.
    ///
    /// Lidl prints the weight and the price per kilo as attributes and the
    /// LINE TOTAL as text. 0,850 x 1,79 = 1,5215, and the till charged 1,52 -
    /// so the total has to be read rather than computed, or every shop with
    /// loose produce in it lands a cent out and reconciliation fails.
    /// </summary>
    [Fact]
    public void A_weighed_article_takes_the_total_the_till_printed()
    {
        var bananas = Assert.Single(Items(), i => i.Name == "Bananen");

        Assert.Equal(0.850m, bananas.Quantity);
        Assert.Equal(179, bananas.UnitPrice?.Value);
        Assert.Equal(152, bananas.Total.Value);

        // The printed figure. For these numbers a computed one would agree -
        // 0,850 x 1,79 is 1,5215, which rounds to the same 1,52 - so this
        // assertion alone does not prove the total is READ. The test below
        // does that.
        Assert.Equal(152, bananas.Total.Value);
    }

    /// <summary>
    /// The line total is READ, never recomputed from the attributes.
    ///
    /// Constructed rather than captured, because the receipts seen so far
    /// happen not to distinguish the two: 0,850 x 1,79 rounds to the printed
    /// 1,52 either way. That is luck, not a guarantee. Whatever the till
    /// printed is what the customer was actually charged, and how it got
    /// there - rounding, truncation, a promotion applied per kilo - is its
    /// business. A parser that multiplied would silently disagree with the
    /// bank statement the day the two differ, and reconciliation would fail
    /// pointing at nothing.
    /// </summary>
    [Fact]
    public void The_line_total_is_the_printed_one_even_when_the_attributes_disagree()
    {
        // 0,500 kg at 3,00 would compute to 1,50. The till printed 1,49.
        const string printed =
            """
            <span id="purchase_list_line_2" class="article" data-art-id="0009999" data-art-description="Druiven" data-art-quantity="0,500" data-unit-price="3,00" data-tax-type="B">Druiven</span><span id="purchase_list_line_2" class="article" data-art-id="0009999" data-art-description="Druiven" data-art-quantity="0,500" data-unit-price="3,00" data-tax-type="B">   </span><span id="purchase_list_line_2" class="article" data-art-id="0009999" data-art-description="Druiven" data-art-quantity="0,500" data-unit-price="3,00" data-tax-type="B">1,49</span>
            """;

        var item = Assert.Single(LidlPrintedReceipt.Items(printed, Options, "EUR"));

        Assert.Equal(149, item.Total.Value);
        Assert.Equal(0.500m, item.Quantity);
        Assert.Equal(300, item.UnitPrice?.Value);

        // The number a parser doing the arithmetic itself would have produced.
        Assert.NotEqual(150, item.Total.Value);
    }

    /// <summary>
    /// A markdown is printed on its own line under the article it reduces,
    /// with no attributes of its own - so it has to be attached to the article
    /// above it or the items stop summing to the stated total.
    /// </summary>
    [Fact]
    public void A_markdown_line_attaches_to_the_article_it_was_printed_under()
    {
        var milk = Assert.Single(Items(), i => i.Name == "Halfvolle melk");

        var discount = milk.Discount;
        Assert.NotNull(discount);

        // Negative, which is what makes summing unconditional.
        Assert.Equal(-20, discount.Amount.Value);
        Assert.Equal("In prijs verlaagd", discount.Label);

        // And the line total stays as printed: the discount carries the
        // difference rather than being baked into the price.
        Assert.Equal(115, milk.Total.Value);

        // It belongs to the milk and to nothing else.
        Assert.All(Items().Where(i => i.Name != "Halfvolle melk"), i => Assert.Null(i.Discount));
    }

    [Fact]
    public void The_tender_block_states_the_method_and_the_card_tail()
    {
        using var document = Fixture.Doc(DetailFixture);
        var printed = document.RootElement.GetProperty("htmlPrintedReceipt").GetString();

        var payment = LidlPrintedReceipt.Payment(printed, Options);

        // "Bankpas" is what a Dutch till prints for a debit card, and the
        // mapping every other adapter here uses does not know the word.
        Assert.Equal("card", payment.Method);
        Assert.Equal("4321", payment.CardLast4);
        Assert.Null(payment.IbanTail);
    }

    [Fact]
    public void Items_net_of_discounts_reconcile_against_the_stated_total()
    {
        using var detail = Fixture.Doc(DetailFixture);

        var items = Items();
        var total = detail.RootElement.GetProperty("totalAmount").GetDecimal();

        // 1,29 + 1,15 - 0,20 + 1,52 = 3,76.
        var sum = items.Sum(i => i.Total.Value + (i.Discount?.Amount.Value ?? 0));
        Assert.Equal(376, sum);
        Assert.Equal((long)(total * 100), sum);

        var receipt = ReceiptFactory.Build(
            "ses_test", "22000263862026080411111", LidlPlusTicketParser.Merchant("Testdorp"),
            DateTimeOffset.UtcNow, new Connector.Kit.Normalization.Money(sum, "EUR"),
            LidlPrintedReceipt.Payment(
                detail.RootElement.GetProperty("htmlPrintedReceipt").GetString(), Options),
            items);

        Assert.True(receipt.Reconciled);
    }

    /// <summary>
    /// The detail states a store OBJECT - id "NL0263", name "Delft" - and the
    /// old read asked for a string, so every receipt in the demo was titled
    /// with the branch CODE. The receipt itself prints "Lidl Delft".
    /// </summary>
    [Fact]
    public void The_store_is_named_rather_than_numbered()
    {
        using var document = Fixture.Doc(DetailFixture);
        var store = document.RootElement.GetProperty("store");

        Assert.Equal("Testdorp", store.GetProperty("name").GetString());
        Assert.Equal("NL0999", store.GetProperty("id").GetString());
    }

    private static IReadOnlyList<Connector.Kit.Normalization.ReceiptItem> Items()
    {
        using var document = Fixture.Doc(DetailFixture);
        var printed = document.RootElement.GetProperty("htmlPrintedReceipt").GetString();

        return LidlPrintedReceipt.Items(printed, Options, "EUR");
    }

    // ---- defensive parsing -------------------------------------------------

    [Fact]
    public void An_unexpected_extra_field_is_tolerated()
    {
        var root = Fixture.Object(ListFixture);
        root["nextCursor"] = "opaque";
        root["tickets"]![0]!.AsObject()["loyaltyCampaignIds"] = new JsonArray(7, 9);

        using var document = Fixture.Reparse(root);

        var tickets = LidlPlusTicketParser.ParseList(document.RootElement, Options, Dutch);
        Assert.Equal(1427, tickets[0].Total.Value);
    }

    [Fact]
    public void A_missing_date_raises_provider_changed()
    {
        var root = Fixture.Object(ListFixture);
        root["tickets"]![0]!.AsObject().Remove("date");

        using var document = Fixture.Reparse(root);

        var error = Assert.Throws<ConnectorException>(
            () => LidlPlusTicketParser.ParseList(document.RootElement, Options, Dutch));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains("ticket.date", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_total_raises_provider_changed()
    {
        var root = Fixture.Object(ListFixture);
        root["tickets"]![0]!.AsObject().Remove("totalAmount");

        using var document = Fixture.Reparse(root);

        var error = Assert.Throws<ConnectorException>(
            () => LidlPlusTicketParser.ParseList(document.RootElement, Options, Dutch));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
    }

    [Fact]
    public void Malformed_money_raises_provider_changed()
    {
        var root = Fixture.Object(ListFixture);
        root["tickets"]![0]!.AsObject()["totalAmount"] = "14,2,7";

        using var document = Fixture.Reparse(root);

        var error = Assert.Throws<ConnectorException>(
            () => LidlPlusTicketParser.ParseList(document.RootElement, Options, Dutch));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains("14,2,7", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unrecognisable_envelope_raises_provider_changed()
    {
        using var document = Fixture.Reparse(new JsonObject { ["receiptsMovedTo"] = "somewhere else" });

        var error = Assert.Throws<ConnectorException>(
            () => LidlPlusTicketParser.ParseList(document.RootElement, Options, Dutch));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
    }

    /// <summary>
    /// A line with no article attributes is not an article. Headers,
    /// separators and the totals block all sit in the same markup, and a
    /// parser that took any line with a number on it would emit "Totaal" as
    /// something somebody bought.
    /// </summary>
    [Fact]
    public void Only_lines_carrying_an_article_id_become_items()
    {
        var items = Items();

        Assert.DoesNotContain(items, i => i.Name.Contains("Totaal", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(items, i => i.Name.Contains("Aantal", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(items, i => i.Name.Contains("OMSCHRIJVING", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(items, i => i.Name.Contains("Lidl", StringComparison.OrdinalIgnoreCase));

        // And the markdown line is a discount rather than a fourth item.
        Assert.DoesNotContain(items, i => i.Name.Contains("verlaagd", StringComparison.OrdinalIgnoreCase));
    }

}
