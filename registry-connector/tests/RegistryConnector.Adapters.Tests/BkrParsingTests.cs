using Connector.Kit.Errors;
using Connector.Kit.Normalization;
using RegistryConnector.Adapters.Bkr;
using Xunit;

namespace RegistryConnector.Adapters.Tests;

/// <summary>
/// BKR's credit overview, read out of the page the portal prints.
///
/// The fixture's structure was captured live on 2026-08-04 and the history in
/// it is invented. What it preserves is every shape that would produce wrong
/// data quietly:
///
///   * the hidden #pdf-export block, which is where the parser reads from
///   * the visible rows appearing twice, once per tab
///   * "EUR 2.000" meaning two thousand
///   * a credit with no end dates at all, which is normal for revolving credit
///   * "Geen bijzonderheden gemeld", which is the absence of an arrears code
///     rather than one
/// </summary>
public sealed class BkrParsingTests
{
    private static readonly BkrOptions Options = new();

    private static string Overview() => Fixture.Read("bkr/overview.html");

    private static IReadOnlyList<CreditRegistration> Credits() =>
        BkrCreditParser.Parse(Overview(), Options, "ses_test");

    /// <summary>
    /// The reason this parser reads the print block instead of the list.
    ///
    /// The portal server-renders the "Alles" and "Lopend" tabs into the same
    /// page, so the visible rows appear twice - anything counting li.contract
    /// reports ten credits where there are five, and every amount is doubled
    /// in any sum built from them.
    /// </summary>
    [Fact]
    public void Each_credit_is_read_once_even_though_the_page_lists_it_twice()
    {
        var html = Overview();

        // The hazard is really in the fixture: the same credit IS in there
        // twice, so a parser reading rows would see it twice.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(html, "list-group-item contract").Count);

        var credits = Credits();

        Assert.Equal(4, credits.Count);
        Assert.Equal(credits.Count, credits.Select(c => c.ExternalId).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// Keyed on BKR's own contract number rather than the GUID the list links
    /// with. A re-issued GUID would duplicate every credit on the next sync;
    /// the contract number is what the register itself calls the agreement,
    /// and the portal will even route on it.
    /// </summary>
    [Fact]
    public void A_credit_is_keyed_on_the_registers_own_contract_number()
    {
        Assert.Equal(
            ["R10000001", "R10000002", "R10000003", "R10000004"],
            Credits().Select(c => c.ExternalId));

        Assert.DoesNotContain(Credits(), c => c.ExternalId.Contains('-', StringComparison.Ordinal));
    }

    /// <summary>
    /// The single most dangerous character on the page.
    ///
    /// "&#8364; 2.000" is two thousand euros. Read with the dot as a decimal
    /// point it becomes two, and nothing downstream can tell - the number is
    /// plausible, the currency is right, and reconciliation has nothing to
    /// check it against because a register states no lines.
    /// </summary>
    [Fact]
    public void A_dot_is_a_thousands_separator_and_never_a_decimal_point()
    {
        var revolving = Assert.Single(Credits(), c => c.ExternalId == "R10000001");

        Assert.Equal(200_000, revolving.Amount.Value);   // EUR 2.000,00 in cents
        Assert.Equal("EUR", revolving.Amount.Currency);

        // And a value with no grouping at all reads the same way.
        var small = Assert.Single(Credits(), c => c.ExternalId == "R10000002");
        Assert.Equal(99_600, small.Amount.Value);        // EUR 996,00
    }

    [Fact]
    public void Dutch_credit_types_are_mapped_and_the_registers_own_words_ride_along()
    {
        var credits = Credits();

        var revolving = Assert.Single(credits, c => c.ExternalId == "R10000001");
        Assert.Equal(CreditKind.Revolving, revolving.Kind);
        Assert.Equal("Doorlopend krediet", revolving.KindLabel);

        var instalment = Assert.Single(credits, c => c.ExternalId == "R10000002");
        Assert.Equal(CreditKind.Instalment, instalment.Kind);
        Assert.Equal("Aflopend krediet", instalment.KindLabel);
    }

    /// <summary>
    /// Status comes from the register stating an actual end date, never from
    /// comparing the expected one against today.
    ///
    /// A credit whose expected end has passed is not thereby settled - that is
    /// exactly what "Werkelijke einddatum" exists to say - and inferring it
    /// would report somebody's live debt as finished, which is the single
    /// worst thing this connector could get wrong.
    /// </summary>
    [Fact]
    public void A_credit_is_ended_only_when_the_register_states_an_actual_end_date()
    {
        var credits = Credits();

        var running = Assert.Single(credits, c => c.ExternalId == "R10000002");
        Assert.Equal(CreditStatus.Running, running.Status);

        // Its expected end is in 2028 - but the one below has an EXPECTED end
        // in the past and is only ended because an actual one is stated.
        Assert.Equal(new DateOnly(2028, 1, 15), running.EndsOn);

        // The case that decides it. This credit's EXPECTED end was 2022 and
        // it has no actual end, so it is a loan that ran over its term - still
        // owed, still running. Inferring "ended" from the date in the past
        // would report a live debt as settled.
        var overdue = Assert.Single(credits, c => c.ExternalId == "R10000004");
        Assert.Equal(CreditStatus.Running, overdue.Status);
        Assert.Equal(new DateOnly(2022, 3, 1), overdue.EndsOn);
        Assert.True(overdue.EndsOn < DateOnly.FromDateTime(DateTime.UtcNow), "the fixture's expected end must be in the past");

        var ended = Assert.Single(credits, c => c.ExternalId == "R10000003");
        Assert.Equal(CreditStatus.Ended, ended.Status);
        Assert.Equal(new DateOnly(2025, 8, 12), ended.EndsOn);
        Assert.Equal(new DateOnly(2023, 3, 1), ended.StartedOn);
    }

    /// <summary>
    /// Revolving credit states neither end date, and null is the honest answer
    /// rather than a date computed from a term it does not have.
    /// </summary>
    [Fact]
    public void A_credit_with_no_end_dates_states_none()
    {
        var revolving = Assert.Single(Credits(), c => c.ExternalId == "R10000001");

        Assert.Equal(CreditStatus.Running, revolving.Status);
        Assert.Null(revolving.EndsOn);
        Assert.Equal(new DateOnly(2025, 10, 23), revolving.StartedOn);
    }

    /// <summary>
    /// The most consequential field in the whole record, carried verbatim.
    ///
    /// And its absence carried as absence: "Geen bijzonderheden gemeld" is the
    /// register saying there is nothing to report, not a code called that. A
    /// consumer rendering it beside real A-codes would be showing a clean
    /// credit as though it were flagged.
    /// </summary>
    [Fact]
    public void An_arrears_code_is_verbatim_and_no_arrears_is_null()
    {
        var credits = Credits();

        var flagged = Assert.Single(credits, c => c.ExternalId == "R10000003");
        Assert.Equal("A2", flagged.ArrearsCode);

        Assert.All(
            credits.Where(c => c.ExternalId != "R10000003"),
            c => Assert.Null(c.ArrearsCode));
    }

    [Fact]
    public void The_consumers_own_record_is_read_from_the_page_that_has_no_credit_on_it()
    {
        Assert.Equal("T. Testpersoon", BkrCreditParser.ProfileOf(Overview(), Options));
    }

    /// <summary>
    /// A dead session and a rebuilt portal arrive identically - the portal
    /// bounces a signed-out visitor to B2C, and the page that comes back has
    /// no print block either way. Only the URL separates them, and they call
    /// for opposite responses: one is a sign-in the user can do something
    /// about, the other is an engineer's problem.
    ///
    /// BKR issues no refresh token, so a fetch on a dead session is normal
    /// rather than exceptional - it happens an hour after every connect.
    /// </summary>
    [Fact]
    public void Landing_back_on_the_sign_in_page_is_an_expired_session()
    {
        var adapter = new BkrAdapter(Options);

        var expired = Assert.Throws<ConnectorException>(() => adapter.Credits(
            "https://login.mijnkredietregistratie.nl/bkrconsp.onmicrosoft.com/oauth2/v2.0/authorize?...",
            "<html><body>Inloggen</body></html>",
            "ses_test"));

        Assert.Equal(ErrorCode.SessionExpired, expired.Code);
        Assert.NotEqual(ErrorCode.ProviderChanged, expired.Code);

        // And a page on the PORTAL with no print block is the other thing
        // entirely, which is what makes the URL check load-bearing.
        var changed = Assert.Throws<ConnectorException>(() => adapter.Credits(
            "https://portaal.mijnkredietregistratie.nl/",
            "<html><body>something else</body></html>",
            "ses_test"));

        Assert.Equal(ErrorCode.ProviderChanged, changed.Code);
    }

    [Fact]
    public void A_signed_in_page_is_parsed_through_the_same_seam()
    {
        var credits = new BkrAdapter(Options).Credits(
            "https://portaal.mijnkredietregistratie.nl/", Overview(), "ses_test");

        Assert.Equal(4, credits.Count);
    }

    // ---- what it refuses to guess -------------------------------------------

    /// <summary>
    /// A page without the print block is a shape change worth stopping for.
    /// Falling back to scraping the visible list would mean silently switching
    /// to a source with no dates and double the rows.
    /// </summary>
    [Fact]
    public void A_page_without_the_print_block_is_a_provider_change()
    {
        var error = Assert.Throws<ConnectorException>(
            () => BkrCreditParser.Parse("<html><body>signed out</body></html>", Options, "ses_test"));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains("pdf-export", error.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// "BKR has nothing on you" is a real answer and the one most people hope
    /// for - but it is only allowed when the page says so itself, never as the
    /// result of a parser that quietly stopped matching.
    /// </summary>
    [Fact]
    public void An_empty_register_is_an_answer_only_when_the_page_says_so()
    {
        const string block = """<div id="pdf-export"><div class="pdf-page"><h5>Uw kredietoverzicht</h5></div></div>""";

        var refused = Assert.Throws<ConnectorException>(
            () => BkrCreditParser.Parse(block, Options, "ses_test"));

        Assert.Equal(ErrorCode.ProviderChanged, refused.Code);

        var stated = BkrCreditParser.Parse(
            block + "<p>U heeft geen kredieten geregistreerd staan.</p>", Options, "ses_test");

        Assert.Empty(stated);
    }

    [Fact]
    public void A_credit_missing_its_amount_is_a_provider_change_and_never_a_zero()
    {
        const string html = """
            <div id="pdf-export"><div class="pdf-page">
              <div class="info-item"><div class="label">Overeenkomstnummer</div><div class="value">R1</div></div>
              <div class="info-item"><div class="label">Aanbieder</div><div class="value">Testbank</div></div>
            </div></div>
            """;

        var error = Assert.Throws<ConnectorException>(() => BkrCreditParser.Parse(html, Options, "ses_test"));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains("Bedrag", error.Detail, StringComparison.Ordinal);
    }
}
