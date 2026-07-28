using System.Text.Json.Nodes;
using Connector.Kit.Errors;
using ShopConnector.Adapters.LidlPlus;
using ShopConnector.Adapters.Support;
using ShopConnector.Adapters.Tests.Support;
using Xunit;

namespace ShopConnector.Adapters.Tests;

/// <summary>
/// Lidl Plus's v2 list and v3 detail. The two endpoints sit on different API
/// versions, which is not a typo and must not be "fixed"; the fixtures are
/// recorded from both.
///
/// The load-bearing behaviour here is the discount sign. Lidl states a
/// discount POSITIVE, and the schema requires it negative so that summing is
/// unconditional - get that wrong and every promotion-heavy shop is marked
/// as failing reconciliation.
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
    public void Detail_negates_a_positively_stated_discount()
    {
        using var document = Fixture.Doc(DetailFixture);

        var items = LidlPlusTicketParser.ParseItems(document.RootElement, Options);

        Assert.Equal(4, items.Count);

        Assert.Equal("Bio volle melk 1L", items[0].Name);
        Assert.Equal(258, items[0].Total.Value);
        Assert.Equal(2m, items[0].Quantity);
        Assert.Equal(129, items[0].UnitPrice?.Value);

        // An empty discounts array is no discount, not a zero one.
        Assert.Null(items[0].Discount);

        var discounted = items[1];
        Assert.Equal("Kipfilet 500g", discounted.Name);
        Assert.Equal(549, discounted.Total.Value);

        var discount = discounted.Discount;
        Assert.NotNull(discount);

        // Lidl states "0,50"; the schema requires it negative.
        Assert.Equal(-50, discount.Amount.Value);
        Assert.Equal("Lidl Plus korting", discount.Label);
    }

    [Fact]
    public void Detail_takes_its_currency_from_the_object_the_provider_states_it_in()
    {
        using var document = Fixture.Doc(DetailFixture);

        var items = LidlPlusTicketParser.ParseItems(document.RootElement, Options);

        // Lidl nests it as {"code": "EUR", "symbol": "€"}. The symbol is never
        // read: "$" is at least four different currencies.
        Assert.All(items, item => Assert.Equal("EUR", item.Total.Currency));
    }

    [Fact]
    public void Detail_payment_states_the_card_tail_and_leaves_the_iban_null()
    {
        using var document = Fixture.Doc(DetailFixture);

        var payment = LidlPlusTicketParser.ParsePayment(document.RootElement);

        Assert.Equal("card", payment.Method);
        Assert.Equal("4821", payment.CardLast4);
        Assert.Null(payment.IbanTail);
    }

    [Fact]
    public void Items_net_of_discounts_reconcile_against_the_stated_total()
    {
        using var list = Fixture.Doc(ListFixture);
        using var detail = Fixture.Doc(DetailFixture);

        var ticket = LidlPlusTicketParser.ParseList(list.RootElement, Options, Dutch)[0];
        var items = LidlPlusTicketParser.ParseItems(detail.RootElement, Options);

        // 258 + 549 + 119 + 551 - 50 = 1427.
        var sum = items.Sum(i => i.Total.Value + (i.Discount?.Amount.Value ?? 0));
        Assert.Equal(ticket.Total.Value, sum);

        var receipt = ReceiptFactory.Build(
            "ses_test", ticket.Id, LidlPlusTicketParser.Merchant(ticket.StoreName),
            ticket.PurchasedAt, ticket.Total, LidlPlusTicketParser.ParsePayment(detail.RootElement), items);

        Assert.True(receipt.Reconciled);
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

    [Fact]
    public void A_nameless_item_line_is_skipped_rather_than_emitted_unnamed()
    {
        var root = Fixture.Object(DetailFixture);
        root["itemsLine"]![0]!.AsObject().Remove("name");

        using var document = Fixture.Reparse(root);

        var items = LidlPlusTicketParser.ParseItems(document.RootElement, Options);

        // The receipt then fails reconciliation rather than showing a blank
        // product, which is the honest outcome: something is missing and the
        // consumer is told so.
        Assert.Equal(3, items.Count);
        Assert.DoesNotContain(items, i => string.IsNullOrWhiteSpace(i.Name));
    }
}
