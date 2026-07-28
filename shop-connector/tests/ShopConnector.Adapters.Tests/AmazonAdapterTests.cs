using Connector.Kit.Challenges;
using Connector.Kit.Errors;
using Connector.Kit.Jobs;
using Connector.Kit.Manifests;
using Connector.Kit.Normalization;
using Connector.Kit.Security;
using ShopConnector.Adapters.Amazon;
using ShopConnector.Adapters.Fixtures;
using ShopConnector.Adapters.Support;
using ShopConnector.Adapters.Tests.Support;
using Xunit;

namespace ShopConnector.Adapters.Tests;

/// <summary>
/// amazon.nl, offline.
///
/// There is no API to stub on this provider - three independently maintained
/// projects all scrape HTML because no JSON consumer endpoint exists - so
/// every test below runs against a recorded PAGE: the Dutch order list, the
/// print invoice, and each of the walls Amazon puts in front of them. Nothing
/// here opens a socket or starts a browser.
///
/// The suite is weighted towards the two things that fail SILENTLY. Money,
/// because the best public reference turns "€ 12,50" into 1250.0 and does not
/// throw; and locale, because that same reference cannot read a Dutch month
/// name and hands back a null date rather than an error. Both produce output
/// that looks entirely reasonable.
/// </summary>
public sealed class AmazonAdapterTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 28, 2, 0, 0, TimeSpan.Zero);

    private static readonly AmazonOptions Options = new();

    private const string Year2026Page1 =
        "https://www.amazon.nl/your-orders/orders?timeFilter=year-2026&startIndex=0";

    private const string Year2026Page2 =
        "https://www.amazon.nl/your-orders/orders?timeFilter=year-2026&startIndex=10";

    private const string Invoice402 =
        "https://www.amazon.nl/gp/css/summary/print.html?orderID=402-1234567-1234567";

    private const string Invoice403 =
        "https://www.amazon.nl/gp/css/summary/print.html?orderID=403-7654321-7654321";

    private const string Invoice404 =
        "https://www.amazon.nl/gp/css/summary/print.html?orderID=404-1111111-2222222";

    private static AmazonAdapter Adapter(AmazonOptions? options = null) =>
        new(options ?? Options, new FixedTimeProvider(Now));

    // ---- the manifest ------------------------------------------------------

    [Fact]
    public void Manifest_validates()
    {
        // The validator refuses a bad manifest at startup, so a manifest that
        // cannot boot the host must not pass the suite either.
        ManifestValidator.Validate(Adapter().Describe());
    }

    [Fact]
    public void Manifest_admits_what_this_provider_actually_is()
    {
        var manifest = Adapter().Describe();

        Assert.Equal("amazon-nl", manifest.Id);
        Assert.Equal("NL", manifest.Country);

        // Browser or nothing: there is no consumer API to fall back to.
        Assert.Equal(ProviderRuntime.BrowserInteractive, manifest.Runtime);
        Assert.True(manifest.Agent.Required, "a browser runtime cannot run inline");
        Assert.Equal("residential", manifest.Agent.Egress?.Kind);

        // The load-bearing admission. Amazon's WAF and ACIC walls are
        // interactive widgets, which means a human at the browser or nothing;
        // claiming unattended operation would make the consuming app offer
        // scheduled syncing and then fail it at three in the morning.
        Assert.False(manifest.Unattended, "a wall can arrive with nobody watching");
        Assert.False(manifest.Auth.Session.Refreshable, "the credential is a cookie jar with no refresh grant");
        Assert.False(manifest.Auth.Reauth.Cheap);
        Assert.Equal(SecretCustody.Client, manifest.SecretCustody);

        // All three, and each for a different wall: the one-time code, the
        // legacy picture captcha a relay can carry, and the widget that can
        // only be passed in a browser somebody can see and touch.
        Assert.Contains(ChallengeType.MfaCode, manifest.Auth.Challenges);
        Assert.Contains(ChallengeType.Image, manifest.Auth.Challenges);
        Assert.Contains(ChallengeType.AppApproval, manifest.Auth.Challenges);
    }

    [Fact]
    public void Manifest_asks_for_a_password_and_never_for_the_second_factor()
    {
        var fields = Adapter().Describe().Auth.AllFields().ToList();

        var password = Assert.Single(fields, f => f.Type == FieldType.Password);
        Assert.True(password.Secret, "a password that is not marked secret is logged and screenshotted");

        // The reference library accepts a TOTP shared secret and auto-solves
        // Amazon's one-time code with it. A service holding both factors has
        // not connected to an account on somebody's behalf; it has taken the
        // account over. The code is relayed to the human instead.
        Assert.DoesNotContain(fields, f =>
            f.Key.Contains("otp", StringComparison.OrdinalIgnoreCase)
            || f.Key.Contains("totp", StringComparison.OrdinalIgnoreCase)
            || f.Key.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_order_url_leaves_the_storefront_in_dutch_unless_an_operator_says_otherwise()
    {
        // azad appends &language=en_GB on every non-English storefront to dodge
        // locale parsing. We do not have the problem it solves - our unit is
        // declared and our parser reads Dutch - and the parameter is
        // unverified on .nl, so the default must not depend on it.
        var plain = Adapter().OrdersUrl(2026, 0);

        Assert.Equal(Year2026Page1, plain);
        Assert.DoesNotContain("language", plain, StringComparison.Ordinal);

        // It is still one edit away, because a live run may find Dutch
        // rendering has a shape the parser cannot read.
        var forced = Adapter(Options with { LanguageOverride = "en_GB" }).OrdersUrl(2026, 10);
        Assert.Contains("language=en_GB", forced, StringComparison.Ordinal);
        Assert.Contains("startIndex=10", forced, StringComparison.Ordinal);
    }

    // ---- money: the trap ---------------------------------------------------

    [Theory]
    // The reference's own worst case. re.sub("[a-zA-Z$£€₹,]+", "", "€ 12,50")
    // is "1250", and float("1250") is a hundred times the real order.
    [InlineData("€ 12,50", 1_250)]
    [InlineData("€ 1.234,56", 123_456)]
    [InlineData("EUR 1.234,56", 123_456)]
    // English rendering, which is what a forced language would produce.
    [InlineData("€1,234.56", 123_456)]
    [InlineData("€ 0,00", 0)]
    [InlineData("-€ 9,99", -999)]
    [InlineData("€ -9,99", -999)]
    // Whole euros with grouping and no cents. Handed to the kit's parser alone
    // this reads as one thousandth of the real figure - see the test below,
    // which pins that.
    [InlineData("€ 1.234", 123_400)]
    [InlineData("€ 1,234", 123_400)]
    public void Dutch_amounts_are_read_as_major_units_in_euros(string rendered, long expectedMinorUnits)
    {
        var money = AmazonMoney.Parse(rendered, MoneyUnit.MajorString, "EUR", "total", Options);

        Assert.Equal(expectedMinorUnits, money.Value);
        Assert.Equal("EUR", money.Currency);
    }

    [Fact]
    public void The_grouping_guard_is_the_one_thing_the_kit_cannot_decide_alone()
    {
        // With both separators present the kit resolves the string perfectly:
        // whichever comes last is the decimal point.
        Assert.Equal(123_456, MoneyParser.ToMinor("1.234,56", MoneyUnit.MajorString));
        Assert.Equal(123_456, MoneyParser.ToMinor("1,234.56", MoneyUnit.MajorString));

        // With ONE separator it reads that separator as a decimal point, which
        // is right for "12,50" and a factor of a thousand out for a whole-euro
        // amount written with grouping. That is not a bug in the kit - the
        // string really is ambiguous - it is why the adapter resolves the
        // shape before handing the digits over.
        Assert.Equal(123, MoneyParser.ToMinor("1.234", MoneyUnit.MajorString));
        Assert.Equal(123_400, AmazonMoney.Parse("€ 1.234", MoneyUnit.MajorString, "EUR", "total", Options).Value);
    }

    [Fact]
    public void The_declared_unit_governs_and_is_never_inferred_from_the_value()
    {
        // The same string, two declarations, two answers. Nothing about "1234"
        // says which it is, which is exactly why the field declares it and the
        // parser never guesses.
        Assert.Equal(123_400, AmazonMoney.Parse("€ 1234", MoneyUnit.MajorString, "EUR", "t", Options).Value);
        Assert.Equal(1_234, AmazonMoney.Parse("€ 1234", MoneyUnit.Minor, "EUR", "t", Options).Value);

        // And the one this adapter declares for every field it reads. Amazon
        // publishes no number anywhere; every amount is a rendered currency
        // string in major units.
        Assert.Equal(MoneyUnit.MajorString, Options.OrderTotalUnit);
        Assert.Equal(MoneyUnit.MajorString, Options.ItemAmountUnit);
        Assert.Equal(MoneyUnit.MajorString, Options.InvoiceTotalUnit);
        Assert.Equal(MoneyUnit.MajorString, Options.DiscountAmountUnit);
        Assert.Equal("EUR", Options.Currency);
    }

    [Theory]
    [InlineData("$ 12.50")]
    [InlineData("£ 12.50")]
    [InlineData("USD 1,234.56")]
    public void An_amount_in_another_currency_stops_the_parse_instead_of_becoming_euros(string rendered)
    {
        // amazon.nl bills in EUR and the currency is declared, never inferred
        // from a symbol - "$" is at least four different currencies. Recording
        // a dollar figure as euros is the silent kind of wrong.
        var error = Assert.Throws<ConnectorException>(
            () => AmazonMoney.Parse(rendered, MoneyUnit.MajorString, "EUR", "order total", Options));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains("another currency", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hyphen_in_a_label_does_not_turn_a_charge_into_a_credit()
    {
        // "Btw-bedrag" and "Actie-korting" both contain a hyphen. Scanning the
        // whole line for one subtracts the tax and leaves the discount correct
        // by luck, and the receipt still looks perfectly plausible.
        var tax = AmazonMoney.Row("Btw-bedrag: € 214,26", MoneyUnit.MajorString, "EUR", "t", Options);
        Assert.Equal(21_426, tax.Amount?.Value);
        Assert.Equal("Btw-bedrag", tax.Label);

        var discount = AmazonMoney.Row("Actie-korting: -€ 9,99", MoneyUnit.MajorString, "EUR", "t", Options);
        Assert.Equal(-999, discount.Amount?.Value);
        Assert.Equal("Actie-korting", discount.Label);
    }

    // ---- dates and offsets -------------------------------------------------

    [Fact]
    public void A_dutch_month_name_is_read_and_carries_a_real_amsterdam_offset()
    {
        // dateutil.parser.parse(..., fuzzy=True) - what the reference uses -
        // does not know "mei" and returns None, so the order silently loses
        // its date.
        var summer = AmazonDate.Parse("Besteld op 14 mei 2026", RetailZones.Dutch, "order date");
        var winter = AmazonDate.Parse("Besteld op 3 januari 2026", RetailZones.Dutch, "order date");

        Assert.Equal(new DateTimeOffset(2026, 5, 14, 0, 0, 0, TimeSpan.FromHours(2)), summer);
        Assert.Equal(new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.FromHours(1)), winter);

        // Never a bare date. A near-midnight purchase read in the agent's own
        // zone lands on the wrong day for anybody running outside CET, and a
        // receipt matched to the wrong day is worse than one not matched.
        Assert.Equal(TimeSpan.FromHours(2), summer.Offset);
        Assert.Equal(TimeSpan.FromHours(1), winter.Offset);
    }

    [Theory]
    [InlineData("12 mei 2026", 2026, 5, 12)]
    [InlineData("12 May 2026", 2026, 5, 12)]
    [InlineData("May 12, 2026", 2026, 5, 12)]
    [InlineData("2 maart 2026", 2026, 3, 2)]
    [InlineData("28 augustus 2025", 2025, 8, 28)]
    [InlineData("1 okt. 2025", 2025, 10, 1)]
    [InlineData("2026-05-12", 2026, 5, 12)]
    public void Both_languages_are_read_so_the_language_switch_changes_nothing(
        string rendered, int year, int month, int day)
    {
        var parsed = AmazonDate.Parse(rendered, RetailZones.Dutch, "order date");

        Assert.Equal(new DateOnly(year, month, day), DateOnly.FromDateTime(parsed.Date));
    }

    [Fact]
    public void An_unreadable_date_is_a_shape_change_and_not_a_null()
    {
        var error = Assert.Throws<ConnectorException>(
            () => AmazonDate.Parse("Besteld op ergens", RetailZones.Dutch, "order date"));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
    }

    // ---- the order list ----------------------------------------------------

    [Fact]
    public void The_dutch_order_list_parses_into_summaries()
    {
        var rows = AmazonOrderParser.ParseList(Dom("orders-2026-p1"), Options, RetailZones.Dutch);

        Assert.Equal(2, rows.Count);

        Assert.Equal("402-1234567-1234567", rows[0].Id);
        Assert.Equal(123_456, rows[0].Total.Value);
        Assert.Equal("EUR", rows[0].Total.Currency);
        Assert.Equal(new DateTimeOffset(2026, 5, 14, 0, 0, 0, TimeSpan.FromHours(2)), rows[0].PurchasedAt);
        Assert.Equal(Invoice402, rows[0].InvoiceUrl);

        Assert.Equal("403-7654321-7654321", rows[1].Id);
        Assert.Equal(1_250, rows[1].Total.Value);
        Assert.Equal(new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.FromHours(1)), rows[1].PurchasedAt);
    }

    [Fact]
    public void A_missing_order_total_is_provider_changed_and_names_the_selectors()
    {
        // The id and the date still resolve, so the card looks parseable right
        // up to the moment the money is needed - which is how a redesign
        // actually presents.
        var error = Assert.Throws<ConnectorException>(
            () => AmazonOrderParser.ParseList(Dom("orders-missing-total"), Options, RetailZones.Dutch));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains("order total on '402-1234567-1234567'", error.Detail, StringComparison.Ordinal);
        Assert.Contains("yohtmlc-order-total", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Pagination_stops_where_the_page_says_it_stops()
    {
        Assert.True(AmazonOrderParser.HasNextPage(Dom("orders-2026-p1"), Options));

        // The last page still renders a "Vorige" anchor carrying startIndex=.
        // Reading any such anchor as "next" costs one extra request per year
        // against a site that challenges when it is bored.
        Assert.False(AmazonOrderParser.HasNextPage(Dom("orders-2026-p2"), Options));
    }

    // ---- the print invoice -------------------------------------------------

    [Fact]
    public void The_print_invoice_parses_into_lines_charges_and_a_discount()
    {
        var invoice = AmazonOrderParser.ParseInvoice(Dom("invoice-402"), Options);

        Assert.Equal(4, invoice.Items.Count);

        var headphones = invoice.Items[0];
        Assert.Equal("Sennheiser HD 560S Koptelefoon, bedraad, zwart", headphones.Name);
        Assert.Equal(2m, headphones.Quantity);
        Assert.Equal(59_900, headphones.UnitPrice?.Value);
        Assert.Equal(119_800, headphones.Total.Value);

        var cable = invoice.Items[1];
        Assert.Equal("USB-C kabel 2 m, zwart", cable.Name);
        Assert.Equal(1m, cable.Quantity);
        Assert.Equal(4_156, cable.Total.Value);

        // The provider's own label, never prose we invented: a connector does
        // not emit user-facing English, not even for a synthetic line.
        var shipping = invoice.Items[2];
        Assert.Equal("Verzendkosten", shipping.Name);
        Assert.Equal(499, shipping.Total.Value);

        // Modelled as a discount rather than a negative product, because that
        // is the construct the normalized record has for it.
        var promotion = invoice.Items[3];
        Assert.Equal("Actiekorting", promotion.Name);
        Assert.Equal(0, promotion.Total.Value);
        Assert.Equal(-999, promotion.Discount?.Amount.Value);

        Assert.Equal("card", invoice.Payment.Method);
        Assert.Equal("1234", invoice.Payment.CardLast4);
        Assert.Equal(123_456, invoice.StatedTotal?.Value);
    }

    [Fact]
    public void The_btw_row_decomposes_the_total_and_is_never_added_to_it()
    {
        // A Dutch consumer price is quoted including BTW, so the invoice's tax
        // row explains the total rather than adding to it. Emitting it as a
        // line would double-count the VAT on every single receipt - and the
        // receipt would still look like a receipt.
        var invoice = AmazonOrderParser.ParseInvoice(Dom("invoice-402"), Options);

        Assert.DoesNotContain(invoice.Items, item => item.Name.Contains("Btw", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(invoice.Items, item => item.Name.Contains("vóór", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(invoice.Items,
            item => item.Name.Contains("Subtotaal", StringComparison.OrdinalIgnoreCase));

        // A switch rather than an assumption, because whether Amazon ever
        // renders a tax-exclusive breakdown is unconfirmed.
        var exclusive = AmazonOrderParser.ParseInvoice(
            Dom("invoice-402"), Options with { TaxIsIncludedInItemPrices = false });

        Assert.Contains(exclusive.Items, item => item.Name.Equals("Btw", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Artikelen (Subtotaal)", "Subtotal")]
    [InlineData("Subtotaal", "Subtotal")]
    [InlineData("Totaal vóór btw", "TotalBeforeTax")]
    [InlineData("Btw", "Tax")]
    [InlineData("Verzendkosten", "Shipping")]
    [InlineData("Actiekorting", "Promotion")]
    [InlineData("Cadeaubon", "GiftCard")]
    [InlineData("Totaal", "GrandTotal")]
    [InlineData("Grand Total", "GrandTotal")]
    public void Label_priority_keeps_the_subtotal_from_becoming_the_total(string label, string expected)
    {
        // "subtotaal" CONTAINS "totaal" and "totaal vóór btw" CONTAINS "btw".
        // One contains-match against one list files both of them wrongly, and
        // the receipt then reconciles against a number that is not its total.
        Assert.Equal(expected, AmazonOrderParser.Categorize(label, Options).ToString());
    }

    // ---- the whole fetch ---------------------------------------------------

    [Fact]
    public async Task A_year_of_dutch_orders_becomes_normalized_receipts()
    {
        var pages = OrderPages();
        using var ctx = new FakeJobContext { Material = Material() };

        var result = await Adapter().FetchAsync(
            ctx, Requests.Receipts(Requests.Day(2026, 1, 1), Requests.Day(2026, 12, 31)),
            pages, wall: null, redirects: null, CancellationToken.None);

        Assert.True(result.Complete);
        Assert.Equal("orders-html+print-invoice", result.Via);
        Assert.Equal(3, result.Receipts.Count);

        var big = result.Receipts[0];
        Assert.Equal("402-1234567-1234567", big.ExternalId);
        Assert.Equal(new DateTimeOffset(2026, 5, 14, 0, 0, 0, TimeSpan.FromHours(2)), big.PurchasedAt);
        Assert.Equal(123_456, big.Total.Value);
        Assert.Equal("EUR", big.Total.Currency);
        Assert.Equal("amazon-nl", big.Merchant.Id);
        Assert.Equal("card", big.Payment?.Method);
        Assert.Equal("1234", big.Payment?.CardLast4);
        Assert.Equal(4, big.Items.Count);

        // The one redundancy Amazon gives us, spent: the list card stated the
        // total, the invoice stated the lines, and they agree.
        Assert.True(big.Reconciled, "the invoice lines must sum to the total the list card stated");
        Assert.All(result.Receipts, receipt => Assert.True(receipt.Reconciled));

        // Newest first, with a real offset on every one of them.
        DateOnly[] days = [new(2026, 5, 14), new(2026, 3, 2), new(2026, 1, 3)];
        Assert.Equal(days, result.Receipts.Select(r => DateOnly.FromDateTime(r.PurchasedAt.Date)));
        Assert.All(result.Receipts, receipt => Assert.NotEqual(TimeSpan.Zero, receipt.PurchasedAt.Offset));

        // Idempotency is not optional; a re-run must not duplicate.
        Assert.All(result.Receipts, receipt => Assert.NotEqual(string.Empty, receipt.ContentHash));
        Assert.Equal(3, result.Receipts.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count());

        string[] expected =
        [
            Year2026Page1, Year2026Page2, Invoice402, Invoice404, Invoice403,
        ];

        Assert.Equal(expected, pages.Opened);
    }

    [Fact]
    public async Task Without_items_no_invoice_is_ever_opened()
    {
        var pages = OrderPages();
        using var ctx = new FakeJobContext { Material = Material() };

        var result = await Adapter().FetchAsync(
            ctx, Requests.Receipts(Requests.Day(2026, 1, 1), Requests.Day(2026, 12, 31), items: false),
            pages, wall: null, redirects: null, CancellationToken.None);

        Assert.Equal("orders-html", result.Via);
        Assert.Equal(3, result.Receipts.Count);
        Assert.All(result.Receipts, receipt => Assert.Empty(receipt.Items));
        Assert.DoesNotContain(pages.Opened, url => url.Contains("print.html", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_receipt_that_does_not_reconcile_is_flagged_and_never_dropped()
    {
        // The invoice is internally consistent and one line short of what the
        // order list said the order cost. Nothing on the page looks wrong.
        var pages = OrderPages();
        pages.Replace(Invoice403, "invoice-403-short");

        using var ctx = new FakeJobContext { Material = Material() };

        var result = await Adapter().FetchAsync(
            ctx, Requests.Receipts(Requests.Day(2026, 1, 1), Requests.Day(2026, 12, 31)),
            pages, wall: null, redirects: null, CancellationToken.None);

        var short403 = Assert.Single(
            result.Receipts, r => string.Equals(r.ExternalId, "403-7654321-7654321", StringComparison.Ordinal));

        Assert.False(short403.Reconciled);
        Assert.Equal(1_250, short403.Total.Value);                          // the list card's figure is kept
        Assert.Equal(1_000, short403.Items.Sum(item => item.Total.Value));

        // Still emitted. Dropping it would hide a real purchase; trusting it
        // silently would hand over a total we know disagrees with its lines.
        Assert.Equal(3, result.Receipts.Count);
    }

    [Fact]
    public async Task An_empty_year_is_an_empty_answer_and_not_an_error()
    {
        var pages = new StubAmazonPages
        {
            ["https://www.amazon.nl/your-orders/orders?timeFilter=year-2025&startIndex=0"] = "orders-2025-empty",
        };

        using var ctx = new FakeJobContext { Material = Material() };

        var result = await Adapter().FetchAsync(
            ctx, Requests.Receipts(Requests.Day(2025, 1, 1), Requests.Day(2025, 12, 31)),
            pages, wall: null, redirects: null, CancellationToken.None);

        Assert.Empty(result.Receipts);
        Assert.True(result.Complete);
    }

    [Fact]
    public async Task A_redesigned_list_is_a_shape_change_and_never_an_empty_history()
    {
        // The most believable wrong answer this adapter could give is "you have
        // bought nothing". An expired card selector and a genuinely empty year
        // must not look alike.
        var pages = new StubAmazonPages { [Year2026Page1] = "orders-redesigned" };

        using var ctx = new FakeJobContext { Material = Material() };

        var error = await Assert.ThrowsAsync<ConnectorException>(() => Adapter().FetchAsync(
            ctx, Requests.Receipts(Requests.Day(2026, 1, 1), Requests.Day(2026, 12, 31)),
            pages, wall: null, redirects: null, CancellationToken.None));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains("empty-history notice", error.Detail, StringComparison.Ordinal);
        Assert.Contains("js-order-card", error.Detail, StringComparison.Ordinal);
    }

    // ---- bot protection ----------------------------------------------------

    [Theory]
    [InlineData("blocked-403")]
    [InlineData("blocked-429")]
    public async Task Bot_protection_statuses_are_blocked_by_provider_and_never_a_credential_problem(string fixture)
    {
        // 429 matters most here. The platform's default reading of it is
        // rate_limited - retriable, "wait a moment" - and from a bot wall that
        // is wrong in the dangerous direction: retrying into it is how a
        // working session gets escalated to a hard block.
        var pages = new StubAmazonPages { [Year2026Page1] = fixture };

        using var ctx = new FakeJobContext { Material = Material() };

        var error = await Assert.ThrowsAsync<ConnectorException>(() => Adapter().FetchAsync(
            ctx, Requests.Receipts(Requests.Day(2026, 1, 1), Requests.Day(2026, 12, 31)),
            pages, wall: null, redirects: null, CancellationToken.None));

        Assert.Equal(ErrorCode.BlockedByProvider, error.Code);
        Assert.NotEqual(ErrorCode.InvalidCredentials, error.Code);
        Assert.NotEqual(ErrorCode.RateLimited, error.Code);
    }

    [Theory]
    [InlineData("challenge-acic", "Interactive")]
    [InlineData("challenge-waf", "Interactive")]
    [InlineData("captcha-image", "Image")]
    [InlineData("blocked-403", "HttpFailure")]
    [InlineData("blocked-429", "HttpFailure")]
    [InlineData("signin", "SignIn")]
    [InlineData("orders-2026-p1", "Ok")]
    public void Every_wall_amazon_serves_is_recognised_for_what_it_is(string fixture, string expected)
    {
        var page = AmazonFixture.Page(fixture);

        Assert.Equal(expected, AmazonGuard.Inspect(page, HtmlParser.Parse(page.Html), Options).Kind.ToString());
    }

    [Fact]
    public async Task An_unattended_agent_refuses_a_widget_at_once_and_nobody_is_asked()
    {
        // Nobody can reach a headless browser in a pool, so there is nothing to
        // wait for. Saying so immediately is what lets a consumer tell the user
        // to connect this one from a machine they are sitting at - and it is
        // the failure that hanging until the job budget expires replaces.
        var pages = new StubAmazonPages { [Year2026Page1] = "challenge-acic" };

        using var ctx = new FakeJobContext { Material = Material(), Attended = false };

        var error = await Assert.ThrowsAsync<ConnectorException>(() => Adapter().FetchAsync(
            ctx, Requests.Receipts(Requests.Day(2026, 1, 1), Requests.Day(2026, 12, 31)),
            pages, wall: null, redirects: null, CancellationToken.None));

        Assert.Equal(ErrorCode.BlockedByProvider, error.Code);
        Assert.Empty(ctx.Asked);
    }

    [Fact]
    public async Task An_attended_browser_is_asked_to_pass_the_widget_and_the_page_is_read_again()
    {
        // The only honest way past an ACIC puzzle: the person sitting at the
        // browser passes it themselves. Nothing is solved here and no solving
        // service is called - the reference ships three, we ship none - and the
        // proof that the wall came down is that the same URL reads as order
        // history on the next attempt.
        var pages = OrderPages();
        pages.Once(Year2026Page1, "challenge-acic");

        var wall = AmazonStubPage.Showing("#aa-challenge-page-captcha-container");
        using var ctx = new FakeJobContext
        {
            Material = Material(),
            Attended = true,
            Browser = new AmazonStubBrowser(wall),
            Answer = _ => string.Empty,
        };

        var result = await Adapter().FetchAsync(
            ctx, Requests.Receipts(Requests.Day(2026, 1, 1), Requests.Day(2026, 12, 31)),
            pages, wall, new StubRedirectWaiter(null, int.MaxValue), CancellationToken.None);

        var asked = Assert.Single(ctx.Asked);
        Assert.Equal(ChallengeType.AppApproval, asked.Type);
        Assert.True(asked.IsPassive, "there is nothing for the human to type back at a widget");
        Assert.Equal(3, result.Receipts.Count);

        // Asked once and only once: a wall that is still standing on the retry
        // ends the job instead of looping on a human who already tried.
        Assert.Equal(2, pages.Opened.Count(url => url == Year2026Page1));
    }

    [Fact]
    public async Task Landing_on_the_sign_in_chain_during_a_fetch_is_a_dead_session()
    {
        var pages = new StubAmazonPages { [Year2026Page1] = "signin" };

        using var ctx = new FakeJobContext { Material = Material() };

        var error = await Assert.ThrowsAsync<ConnectorException>(() => Adapter().FetchAsync(
            ctx, Requests.Receipts(Requests.Day(2026, 1, 1), Requests.Day(2026, 12, 31)),
            pages, wall: null, redirects: null, CancellationToken.None));

        // Not invalid_credentials: no credential was submitted during a fetch,
        // and there is nothing for the user to correct except reconnecting.
        Assert.Equal(ErrorCode.SessionExpired, error.Code);
    }

    [Fact]
    public async Task A_fetch_with_no_browser_state_asks_for_a_new_login_rather_than_guessing()
    {
        using var ctx = new FakeJobContext { Material = new SessionMaterial() };

        var error = await Assert.ThrowsAsync<ConnectorException>(() => Adapter().FetchAsync(
            ctx, Requests.Receipts(Requests.Day(2026, 1, 1)), CancellationToken.None));

        Assert.Equal(ErrorCode.SessionExpired, error.Code);
        Assert.False(ctx.Browser.Started, "no browser is worth starting for a session that is not there");
    }

    [Fact]
    public async Task An_unknown_resource_is_refused_before_anything_is_opened()
    {
        using var ctx = new FakeJobContext { Material = Material() };

        var error = await Assert.ThrowsAsync<ConnectorException>(() => Adapter().FetchAsync(
            ctx, new ResourceRequest { ResourceId = "invoices" }, CancellationToken.None));

        Assert.Equal(ErrorCode.UnsupportedResource, error.Code);
    }

    // ---- the session marker ------------------------------------------------

    [Fact]
    public void The_authenticated_cookie_is_what_decides_a_login_worked()
    {
        // CONFIRMED from the reference's constants:
        // COOKIES_SET_WHEN_AUTHENTICATED = ["x-main"]. Amazon's sign-in chain
        // is long enough that a page can look finished with nothing behind it.
        Assert.True(AmazonCookies.HasAny(
            FixtureCatalog.Read("amazon/storage-state.json"), Options.AuthCookieNames));

        Assert.False(AmazonCookies.HasAny(
            """{"cookies":[{"name":"session-id","value":"x"}]}""", Options.AuthCookieNames));

        // A state we cannot read is a state we cannot vouch for.
        Assert.False(AmazonCookies.HasAny("not json at all", Options.AuthCookieNames));
        Assert.False(AmazonCookies.HasAny(null, Options.AuthCookieNames));
    }

    // ---- helpers -----------------------------------------------------------

    private static HtmlNode Dom(string fixture) => HtmlParser.Parse(AmazonFixture.Page(fixture).Html);

    private static SessionMaterial Material() =>
        new() { StorageState = FixtureCatalog.Read("amazon/storage-state.json") };

    private static StubAmazonPages OrderPages() => new()
    {
        [Year2026Page1] = "orders-2026-p1",
        [Year2026Page2] = "orders-2026-p2",
        [Invoice402] = "invoice-402",
        [Invoice403] = "invoice-403",
        [Invoice404] = "invoice-404",
    };
}
