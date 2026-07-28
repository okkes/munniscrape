using System.Text.Json.Nodes;
using Connector.Kit.Errors;
using ShopConnector.Adapters.AlbertHeijn;
using ShopConnector.Adapters.Support;
using ShopConnector.Adapters.Tests.Support;
using Xunit;

namespace ShopConnector.Adapters.Tests;

/// <summary>
/// Albert Heijn's recorded GraphQL payloads, parsed.
///
/// Two things are load-bearing here and both are asserted rather than left to
/// a reader. An <c>errors</c> array arrives with a 200 status, so a parser
/// that trusts the status code returns an empty history that reads as "you
/// have never shopped here". And AH states discounts in their own array with
/// nothing linking one to a product, so they have to be folded in as their
/// own lines or every promotion-heavy receipt fails its own arithmetic.
/// </summary>
public sealed class AlbertHeijnParsingTests
{
    private const string ListFixture = "ah/receipts-list.json";
    private const string DetailFixture = "ah/receipt-detail.json";
    private const string ErrorsFixture = "ah/graphql-errors.json";
    private const string EmptyFixture = "ah/receipts-empty.json";

    private static readonly AlbertHeijnOptions Options = new();

    private static JsonArray RowsOf(JsonObject document) =>
        document["data"]!["posReceiptsPage"]!["posReceipts"]!.AsArray();

    // ---- the list ----------------------------------------------------------

    [Fact]
    public void List_reads_totals_in_minor_units_from_a_nested_euro_number()
    {
        using var document = Fixture.Doc(ListFixture);

        var summaries = AlbertHeijnReceiptParser.ParseList(document.RootElement, Options);

        Assert.Equal(2, summaries.Count);
        Assert.Equal("ah-2026-07-19-4711", summaries[0].Id);

        // 6.36 EUR under MoneyUnit.MajorDecimal, nested in totalAmount.amount,
        // is 636 cents. Never a float, never a decimal that has been through a
        // locale.
        Assert.Equal(636, summaries[0].Total.Value);
        Assert.Equal("EUR", summaries[0].Total.Currency);
        Assert.Equal(2314, summaries[1].Total.Value);
    }

    [Fact]
    public void List_carries_a_real_offset_even_when_ah_states_none()
    {
        using var document = Fixture.Doc(ListFixture);

        var summaries = AlbertHeijnReceiptParser.ParseList(document.RootElement, Options);

        // The first row states its offset and it is honoured.
        Assert.Equal(new DateTimeOffset(2026, 7, 19, 17, 42, 0, TimeSpan.FromHours(2)), summaries[0].PurchasedAt);

        // The second is a bare local time. Read in Europe/Amsterdam rather
        // than the agent's own zone: a near-midnight purchase dated a day out
        // is worse than one that was never matched.
        Assert.Equal(TimeSpan.FromHours(2), summaries[1].PurchasedAt.Offset);
        Assert.Equal(new DateTimeOffset(2026, 7, 12, 9, 15, 0, TimeSpan.FromHours(2)), summaries[1].PurchasedAt);
    }

    [Fact]
    public void An_empty_page_is_an_empty_history_and_not_an_error()
    {
        using var document = Fixture.Doc(EmptyFixture);

        Assert.Empty(AlbertHeijnReceiptParser.ParseList(document.RootElement, Options));
    }

    [Fact]
    public void A_null_page_is_an_answer_and_an_absent_one_is_a_shape_change()
    {
        using var nulled = Fixture.Reparse(new JsonObject
        {
            ["data"] = new JsonObject { ["posReceiptsPage"] = null },
        });

        // An account with nothing on it. A fact, not a failure.
        Assert.Empty(AlbertHeijnReceiptParser.ParseList(nulled.RootElement, Options));

        using var missing = Fixture.Reparse(new JsonObject { ["data"] = new JsonObject() });

        var error = Assert.Throws<ConnectorException>(
            () => AlbertHeijnReceiptParser.ParseList(missing.RootElement, Options));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains("posReceiptsPage", error.Detail, StringComparison.Ordinal);
    }

    // ---- the errors array --------------------------------------------------

    [Fact]
    public void A_graphql_errors_array_raises_provider_changed_carrying_the_first_message()
    {
        using var document = Fixture.Doc(ErrorsFixture);

        var error = Assert.Throws<ConnectorException>(
            () => AlbertHeijnReceiptParser.ThrowOnErrors(document.RootElement));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains("Member is not entitled", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_errors_payload_parses_as_nothing_at_all_which_is_why_the_check_comes_first()
    {
        using var document = Fixture.Doc(ErrorsFixture);

        // This is the trap, demonstrated: HTTP 200, a null page, and a parser
        // that never looked at "errors" hands back an empty history.
        Assert.Empty(AlbertHeijnReceiptParser.ParseList(document.RootElement, Options));
    }

    [Fact]
    public void A_response_without_errors_passes_the_check_untouched()
    {
        using var document = Fixture.Doc(ListFixture);

        AlbertHeijnReceiptParser.ThrowOnErrors(document.RootElement);
    }

    // ---- the detail --------------------------------------------------------

    [Fact]
    public void Detail_folds_each_discount_in_as_its_own_negative_line()
    {
        using var document = Fixture.Doc(DetailFixture);

        var items = AlbertHeijnReceiptParser.ParseItems(document.RootElement, Options);

        // Three products and the bonus AH states separately.
        Assert.Equal(4, items.Count);

        Assert.Equal("AH Melk halfvol 1L", items[0].Name);
        Assert.Equal(238, items[0].Total.Value);
        Assert.Equal(2m, items[0].Quantity);
        Assert.Equal(119, items[0].UnitPrice?.Value);

        Assert.Equal("AH Volkorenbrood heel", items[1].Name);
        Assert.Equal(189, items[1].Total.Value);

        // Sold by weight: the quantity is 1.200 kg and the unit price is AH's
        // own, never a total divided by a weight.
        Assert.Equal("Bananen los", items[2].Name);
        Assert.Equal(239, items[2].Total.Value);
        Assert.Equal(1.2m, items[2].Quantity);
        Assert.Equal(199, items[2].UnitPrice?.Value);

        // Its own line, because nothing in the payload says which product it
        // belongs to. Attaching it to one would be inventing a fact.
        var bonus = items[3];
        Assert.Equal("BONUS 2e halve prijs", bonus.Name);
        Assert.Equal(-30, bonus.Total.Value);
        Assert.Null(bonus.Discount);
    }

    [Fact]
    public void A_discount_stated_positive_is_still_folded_in_negative()
    {
        var document = Fixture.Object(DetailFixture);
        document["data"]!["posReceiptDetails"]!["discounts"]![0]!["amount"]!["amount"] = 0.30;

        using var reparsed = Fixture.Reparse(document);

        // Providers state a discount as either sign; the schema wants it
        // negative so that summing a receipt is unconditional.
        var items = AlbertHeijnReceiptParser.ParseItems(reparsed.RootElement, Options);
        Assert.Equal(-30, items[3].Total.Value);
    }

    [Fact]
    public void A_discount_with_no_name_raises_provider_changed_rather_than_vanishing()
    {
        var document = Fixture.Object(DetailFixture);
        document["data"]!["posReceiptDetails"]!["discounts"]![0]!.AsObject().Remove("name");

        using var reparsed = Fixture.Reparse(document);

        // Dropping it would leave the receipt failing its own arithmetic with
        // nothing to explain why.
        var error = Assert.Throws<ConnectorException>(
            () => AlbertHeijnReceiptParser.ParseItems(reparsed.RootElement, Options));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
    }

    [Fact]
    public void Detail_states_the_method_and_nulls_the_tails_it_was_never_given()
    {
        using var document = Fixture.Doc(DetailFixture);

        var payment = AlbertHeijnReceiptParser.ParsePayment(document.RootElement, Options);

        Assert.Equal("card", payment.Method);

        // The confirmed operation selects nothing that could carry a card
        // number or an IBAN, so both are null by construction. Explicit, not
        // omitted: the consumer needs to know its match will be weaker.
        Assert.Null(payment.CardLast4);
        Assert.Null(payment.IbanTail);
    }

    [Fact]
    public void A_split_tender_reports_the_leg_a_bank_transaction_will_match()
    {
        var document = Fixture.Object(DetailFixture);
        document["data"]!["posReceiptDetails"]!["payments"] = new JsonArray
        {
            new JsonObject { ["method"] = "MAESTRO", ["amount"] = new JsonObject { ["amount"] = 2.00 } },
            new JsonObject { ["method"] = "CONTANT", ["amount"] = new JsonObject { ["amount"] = 4.36 } },
        };

        using var reparsed = Fixture.Reparse(document);

        Assert.Equal("cash", AlbertHeijnReceiptParser.ParsePayment(reparsed.RootElement, Options).Method);
    }

    [Fact]
    public void A_detail_that_denies_the_receipt_is_a_shape_change_not_an_empty_basket()
    {
        using var document = Fixture.Reparse(new JsonObject
        {
            ["data"] = new JsonObject { ["posReceiptDetails"] = null },
        });

        var error = Assert.Throws<ConnectorException>(
            () => AlbertHeijnReceiptParser.ParseItems(document.RootElement, Options));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
    }

    // ---- the two together --------------------------------------------------

    [Fact]
    public void Items_and_discounts_reconcile_against_the_list_s_stated_total()
    {
        using var list = Fixture.Doc(ListFixture);
        using var detail = Fixture.Doc(DetailFixture);

        var summary = AlbertHeijnReceiptParser.ParseList(list.RootElement, Options)[0];
        var items = AlbertHeijnReceiptParser.ParseItems(detail.RootElement, Options);

        // 238 + 189 + 239 - 30 = 636, and the detail states no total of its
        // own - which is what makes the two operations a real check rather
        // than one number repeated.
        var sum = items.Sum(i => i.Total.Value + (i.Discount?.Amount.Value ?? 0));
        Assert.Equal(636, sum);
        Assert.Equal(636, summary.Total.Value);

        var receipt = ReceiptFactory.Build(
            "ses_test", summary.Id, AlbertHeijnReceiptParser.Merchant(null),
            summary.PurchasedAt, summary.Total,
            AlbertHeijnReceiptParser.ParsePayment(detail.RootElement, Options), items);

        Assert.True(receipt.Reconciled);
        Assert.StartsWith("sha256:", receipt.ContentHash, StringComparison.Ordinal);
        Assert.StartsWith("rcp_", receipt.Id, StringComparison.Ordinal);
    }

    // ---- defensive parsing -------------------------------------------------

    [Fact]
    public void An_unexpected_extra_field_is_tolerated()
    {
        var document = Fixture.Object(ListFixture);
        var row = RowsOf(document)[0]!.AsObject();
        row["loyaltyPointsEarnedThisVisit"] = 240;
        row["experimentBucket"] = "b";
        row["nested"] = new JsonObject { ["whatever"] = true };

        using var reparsed = Fixture.Reparse(document);

        // Retailers add fields without telling anyone. A parser that refuses
        // one it has never seen breaks on a change that meant nothing.
        var summaries = AlbertHeijnReceiptParser.ParseList(reparsed.RootElement, Options);
        Assert.Equal(636, summaries[0].Total.Value);
    }

    [Fact]
    public void A_missing_timestamp_raises_provider_changed()
    {
        var document = Fixture.Object(ListFixture);
        RowsOf(document)[0]!.AsObject().Remove("dateTime");

        using var reparsed = Fixture.Reparse(document);

        var error = Assert.Throws<ConnectorException>(
            () => AlbertHeijnReceiptParser.ParseList(reparsed.RootElement, Options));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains("dateTime", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_total_raises_provider_changed_rather_than_a_free_receipt()
    {
        var document = Fixture.Object(ListFixture);
        RowsOf(document)[0]!.AsObject().Remove("totalAmount");

        using var reparsed = Fixture.Reparse(document);

        var error = Assert.Throws<ConnectorException>(
            () => AlbertHeijnReceiptParser.ParseList(reparsed.RootElement, Options));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);

        // The field is named, so the repair is one line.
        Assert.Contains("totalAmount", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_money_raises_provider_changed()
    {
        var document = Fixture.Object(ListFixture);
        RowsOf(document)[0]!["totalAmount"]!["amount"] = "six euros thirty-six";

        using var reparsed = Fixture.Reparse(document);

        var error = Assert.Throws<ConnectorException>(
            () => AlbertHeijnReceiptParser.ParseList(reparsed.RootElement, Options));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains("six euros thirty-six", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_money_field_of_the_wrong_json_type_raises_provider_changed()
    {
        var document = Fixture.Object(ListFixture);
        RowsOf(document)[0]!["totalAmount"]!["amount"] = true;

        using var reparsed = Fixture.Reparse(document);

        // A boolean where an amount belongs is a shape change, not a value to
        // coerce.
        var error = Assert.Throws<ConnectorException>(
            () => AlbertHeijnReceiptParser.ParseList(reparsed.RootElement, Options));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
    }

    [Fact]
    public void A_row_with_no_id_is_skipped_because_it_cannot_be_deduped()
    {
        var document = Fixture.Object(ListFixture);
        RowsOf(document)[0]!.AsObject().Remove("id");

        using var reparsed = Fixture.Reparse(document);

        // Dedupe is (session_id, external_id); a row with no id would emit a
        // fresh duplicate on every single fetch.
        var summaries = AlbertHeijnReceiptParser.ParseList(reparsed.RootElement, Options);
        Assert.Single(summaries);
        Assert.Equal("ah-2026-07-12-4655", summaries[0].Id);
    }

    // ---- the escape hatch --------------------------------------------------

    [Theory]
    [InlineData("appie://login-exit?code=abc123def456", "abc123def456")]
    [InlineData("appie://login-exit?state=x&code=abc123def456&other=1", "abc123def456")]
    [InlineData("abc123def456", "abc123def456")]
    [InlineData("  abc123def456  ", "abc123def456")]
    public void A_captured_redirect_yields_its_authorization_code(string captured, string expected) =>
        Assert.Equal(expected, AlbertHeijnAdapter.ExtractCode(captured));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("I could not open the link")]
    [InlineData("short")]
    public void A_capture_that_carries_no_code_yields_null(string captured) =>
        Assert.Null(AlbertHeijnAdapter.ExtractCode(captured));
}
