using Connector.Kit.Errors;
using ShopConnector.Adapters.Fixtures;
using ShopConnector.Adapters.Jumbo;
using ShopConnector.Adapters.Tests.Support;
using Xunit;

namespace ShopConnector.Adapters.Tests;

/// <summary>
/// The in-store receipt text parser.
///
/// This is the unpleasant half of Jumbo. <c>GetDigitalReceipt</c> answers with
/// a receipt-<em>printer</em> layout, not with line items, so the only way to
/// itemise a till receipt is to read a picture of a paper receipt written in
/// text. Text parsing is guesswork by nature, which is exactly why it sits
/// behind a seam with recorded fixtures: these tests are the contract, and a
/// till template change shows up here rather than in somebody's groceries.
/// </summary>
public sealed class JumboReceiptLayoutTests
{
    private static readonly JumboOptions Options = new();

    private static readonly JumboPrintLayoutParser Parser = new();

    private static string Layout(string fixture)
    {
        using var document = Fixture.Doc(fixture);
        return JumboStoreReceipts.Layout(document.RootElement, Options, "TX-fixture")
               ?? throw new InvalidOperationException($"fixture '{fixture}' carries no JSON layout");
    }

    [Fact]
    public void The_confirmed_walk_reaches_the_flat_list_of_text_lines()
    {
        Assert.Equal("documents.0.documents.0.printSections", Options.PrintSectionsPath);

        using var document = Fixture.Doc("jumbo/digital-receipt.json");
        var layout = JumboStoreReceipts.Layout(document.RootElement, Options, "TX-fixture");
        Assert.NotNull(layout);

        using var inner = System.Text.Json.JsonDocument.Parse(layout);
        var lines = JumboPrintLayoutParser.Flatten(inner.RootElement, Options);

        // Three header lines, seven in the item block, four in the tail.
        Assert.Equal(14, lines.Count);
        Assert.Contains(lines, l => l.Text.Contains("OMSCHRIJVING", StringComparison.Ordinal));
    }

    [Fact]
    public void A_printed_receipt_yields_its_items_its_total_and_how_it_was_paid()
    {
        var contents = Parser.Parse(Layout("jumbo/digital-receipt.json"), Options, "EUR");

        Assert.Null(contents.Shortfall);
        Assert.Equal(1106, contents.Total?.Value);
        Assert.Equal("card", contents.PaymentMethod);

        Assert.Equal(4, contents.Items.Count);
        Assert.Equal(
            new[] { "MELK HALFVOL", "BANANEN", "KIPFILET", "STATIEGELD" },
            contents.Items.Select(i => i.Name));

        // Dutch comma decimals, read as euros - the printed amounts are what a
        // human read at the till, so they are major units by construction.
        Assert.Equal(new[] { 188L, 189L, 699L, 30L }, contents.Items.Select(i => i.Total.Value));
    }

    /// <summary>
    /// <c>2 X 0,94</c> is not a purchase. It restates the line above it as a
    /// count and a unit price, and turning it into an item of its own would
    /// add 94 cents of groceries that nobody bought.
    /// </summary>
    [Fact]
    public void A_quantity_line_amends_the_item_above_it_rather_than_adding_one()
    {
        var contents = Parser.Parse(Layout("jumbo/digital-receipt.json"), Options, "EUR");

        var milk = contents.Items[0];
        Assert.Equal("MELK HALFVOL", milk.Name);
        Assert.Equal(2m, milk.Quantity);
        Assert.Equal(94, milk.UnitPrice?.Value);

        // The line's own total is untouched: 2 x 0,94 is the 1,88 printed
        // beside the description.
        Assert.Equal(188, milk.Total.Value);
    }

    /// <summary>
    /// The layout prints a promoted line at its discounted price and never
    /// prints the promotion's own value. A discount invented from the flag
    /// would be subtracted a second time and break the very reconciliation it
    /// was meant to explain.
    /// </summary>
    [Fact]
    public void A_promotion_flag_becomes_neither_an_item_nor_a_discount()
    {
        var contents = Parser.Parse(Layout("jumbo/digital-receipt.json"), Options, "EUR");

        Assert.DoesNotContain(contents.Items, i => i.Name == Options.PromotionFlag);
        Assert.All(contents.Items, i => Assert.Null(i.Discount));

        var chicken = Assert.Single(contents.Items, i => i.Name == "KIPFILET");
        Assert.Equal(699, chicken.Total.Value);
    }

    [Fact]
    public void The_items_sum_to_the_printed_total()
    {
        var contents = Parser.Parse(Layout("jumbo/digital-receipt.json"), Options, "EUR");

        Assert.Equal(contents.Total?.Value, contents.Items.Sum(i => i.Total.Value));
    }

    [Fact]
    public void Rows_after_the_total_are_not_items()
    {
        var contents = Parser.Parse(Layout("jumbo/digital-receipt.json"), Options, "EUR");

        // The VAT block prints two amounts of its own and the payment line
        // repeats the total. All three sit after the terminator.
        Assert.DoesNotContain(contents.Items, i => i.Name.StartsWith("BTW", StringComparison.Ordinal));
        Assert.DoesNotContain(contents.Items, i => i.Name.StartsWith("Betaald", StringComparison.Ordinal));
    }

    [Fact]
    public void A_layout_with_no_items_header_still_states_its_total_and_says_what_was_missing()
    {
        var contents = Parser.Parse(Layout("jumbo/digital-receipt-no-items.json"), Options, "EUR");

        Assert.Equal(1106, contents.Total?.Value);
        Assert.Empty(contents.Items);

        // Named, so the next person knows which half of the template moved.
        Assert.NotNull(contents.Shortfall);
        Assert.Contains("OMSCHRIJVING", contents.Shortfall, StringComparison.Ordinal);
    }

    [Fact]
    public void A_layout_with_no_total_line_states_no_total()
    {
        var contents = Parser.Parse(Layout("jumbo/digital-receipt-no-total.json"), Options, "EUR");

        Assert.Null(contents.Total);
        Assert.NotNull(contents.Shortfall);
        Assert.Contains("Totaal", contents.Shortfall, StringComparison.Ordinal);
    }

    /// <summary>
    /// A description that turns into a price is the quietest way to break a
    /// receipt, so the amount match is anchored: only a field that is nothing
    /// but a number is money.
    /// </summary>
    [Fact]
    public void A_description_containing_a_number_is_not_read_as_a_price()
    {
        var contents = Parser.Parse(
            Build(
                ["OMSCHRIJVING", "BEDRAG"],
                ["COCA COLA 1,5L", "2,19"],
                ["ALLEEN GELDIG OP 19-07-2026"],
                ["Totaal", "2,19"]),
            Options,
            "EUR");

        var item = Assert.Single(contents.Items);
        Assert.Equal("COCA COLA 1,5L", item.Name);
        Assert.Equal(219, item.Total.Value);
        Assert.Equal(contents.Total?.Value, contents.Items.Sum(i => i.Total.Value));
    }

    /// <summary>
    /// A till prints a credit with the sign on the right, and a big shop
    /// without a thousands separator. Both are money and both keep the
    /// receipt adding up.
    /// </summary>
    [Fact]
    public void A_trailing_minus_is_a_credit_and_a_four_digit_amount_is_still_an_amount()
    {
        var contents = Parser.Parse(
            Build(
                ["OMSCHRIJVING", "BEDRAG"],
                ["GROTE BOODSCHAP", "1234,56"],
                ["STATIEGELD RETOUR", "0,25-"],
                ["Totaal", "1234,31"]),
            Options,
            "EUR");

        Assert.Equal(new[] { 123456L, -25L }, contents.Items.Select(i => i.Total.Value));
        Assert.Equal(123431, contents.Total?.Value);
        Assert.Equal(contents.Total?.Value, contents.Items.Sum(i => i.Total.Value));
    }

    /// <summary>Builds a print layout the way a till would lay one out.</summary>
    private static string Build(params string[][] lines)
    {
        var textObjects = new System.Text.Json.Nodes.JsonArray();

        foreach (var fields in lines)
        {
            var texts = new System.Text.Json.Nodes.JsonArray();
            foreach (var field in fields)
            {
                texts.Add(new System.Text.Json.Nodes.JsonObject { ["text"] = field });
            }

            textObjects.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["textLines"] = new System.Text.Json.Nodes.JsonArray(
                    new System.Text.Json.Nodes.JsonObject { ["texts"] = texts }),
            });
        }

        return new System.Text.Json.Nodes.JsonObject
        {
            ["documents"] = new System.Text.Json.Nodes.JsonArray(
                new System.Text.Json.Nodes.JsonObject
                {
                    ["documents"] = new System.Text.Json.Nodes.JsonArray(
                        new System.Text.Json.Nodes.JsonObject
                        {
                            ["printSections"] = new System.Text.Json.Nodes.JsonArray(
                                new System.Text.Json.Nodes.JsonObject { ["textObjects"] = textObjects }),
                        }),
                }),
        }.ToJsonString();
    }

    [Fact]
    public void A_payload_that_is_not_json_at_all_raises_provider_changed()
    {
        var error = Assert.Throws<ConnectorException>(
            () => Parser.Parse("<html>blocked</html>", Options, "EUR"));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
    }

    [Fact]
    public void A_layout_whose_confirmed_walk_leads_nowhere_says_so()
    {
        var contents = Parser.Parse("""{"documents":[]}""", Options, "EUR");

        Assert.Null(contents.Total);
        Assert.Empty(contents.Items);
        Assert.NotNull(contents.Shortfall);
        Assert.Contains(Options.PrintSectionsPath, contents.Shortfall, StringComparison.Ordinal);
    }

    [Fact]
    public void The_fixtures_this_parser_is_pinned_to_are_all_present()
    {
        // The fixtures are part of the contract, not a test convenience: one
        // that rots has to break the offline suite immediately.
        foreach (var name in new[]
                 {
                     "jumbo/digital-receipt.json",
                     "jumbo/digital-receipt-no-items.json",
                     "jumbo/digital-receipt-no-total.json",
                     "jumbo/digital-receipt-image-only.json",
                 })
        {
            Assert.Contains(name, FixtureCatalog.Names);
        }
    }
}
