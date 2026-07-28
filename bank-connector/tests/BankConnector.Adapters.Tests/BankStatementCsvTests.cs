using BankConnector.Adapters.Parsing;
using Connector.Kit.Errors;
using Connector.Kit.Normalization;
using Xunit;

namespace BankConnector.Adapters.Tests;

/// <summary>
/// The same statement, exported twice.
///
/// A Dutch bank's export differs by the user's interface language, and the
/// two files differ in every mechanical way there is: delimiter, decimal
/// separator, date format, quoting, and whether the amount is signed or
/// paired with a separate Af/Bij column. They still describe one statement,
/// so they must normalise to one set of amounts, dates and counterparties -
/// and that equivalence is the assertion that catches a column-shift bug no
/// single-file test can see.
/// </summary>
public sealed class BankStatementCsvTests
{
    private const string SessionId = "ses_0123456789abcdef0123456789abcdef";
    private const string Account = "NL18ASNB0123456789";

    private static BankStatementCsvOptions Options() => new()
    {
        SessionId = SessionId,
        ProviderId = "test-bank",
        AccountExternalId = Account,
    };

    private static IReadOnlyList<Transaction> Dutch() =>
        BankStatementCsv.ReadTransactions(BankFixtures.Read(BankFixtures.StatementCsvDutch), Options());

    private static IReadOnlyList<Transaction> English() =>
        BankStatementCsv.ReadTransactions(BankFixtures.Read(BankFixtures.StatementCsvEnglish), Options());

    [Fact]
    public void A_comma_decimal_export_with_a_separate_direction_column_reads_correctly()
    {
        var rows = Dutch();

        Assert.Equal(5, rows.Count);

        // "1.425,00" with "Af": the grouping dot and the decimal comma are
        // resolved by position, and the direction column supplies the sign.
        Assert.Equal(-142_500, rows[0].Amount.Value);
        Assert.Equal(new DateOnly(2026, 6, 2), rows[0].BookedAt);
        Assert.Equal("WONINGSTICHTING DE KLEINE", rows[0].Counterparty?.Name);
        Assert.Equal("NL22RABO0123409876", rows[0].Counterparty?.Iban);
        Assert.Equal("Huur juni 2026", rows[0].Description);
        Assert.Equal(TransactionKind.DirectDebit, rows[0].Kind);
        Assert.Equal(41_730, rows[0].ResultingBalance?.Value);

        // "Bij" is a credit and must not be inverted.
        Assert.Equal(285_000, rows[3].Amount.Value);
        Assert.Equal(TransactionKind.Transfer, rows[3].Kind);
    }

    [Fact]
    public void A_dot_decimal_export_with_signed_amounts_reads_correctly()
    {
        var rows = English();

        Assert.Equal(5, rows.Count);
        Assert.Equal(-142_500, rows[0].Amount.Value);
        Assert.Equal(285_000, rows[3].Amount.Value);

        // A quoted description containing the delimiter must not shift a
        // single field - which is why the delimiter is scored by how many
        // KNOWN columns it resolves rather than by how many fields it yields.
        Assert.Equal("Rent, June 2026", rows[0].Description);
        Assert.Equal(41_730, rows[0].ResultingBalance?.Value);
    }

    [Fact]
    public void Both_languages_normalise_to_the_same_statement()
    {
        var dutch = Dutch();
        var english = English();

        Assert.Equal(dutch.Count, english.Count);

        // Everything the consumer matches and reconciles on is identical.
        // Only the free-text description differs, because that is the part
        // the bank itself localised.
        Assert.Equal(dutch.Select(t => t.BookedAt), english.Select(t => t.BookedAt));
        Assert.Equal(dutch.Select(t => t.Amount.Value), english.Select(t => t.Amount.Value));
        Assert.Equal(dutch.Select(t => t.Amount.Currency), english.Select(t => t.Amount.Currency));
        Assert.Equal(dutch.Select(t => t.ResultingBalance?.Value), english.Select(t => t.ResultingBalance?.Value));
        Assert.Equal(dutch.Select(t => t.Counterparty?.Name), english.Select(t => t.Counterparty?.Name));
        Assert.Equal(dutch.Select(t => t.Counterparty?.Iban), english.Select(t => t.Counterparty?.Iban));
        Assert.Equal(dutch.Select(t => t.Kind), english.Select(t => t.Kind));
        Assert.Equal(dutch.Select(t => t.AccountId), english.Select(t => t.AccountId));
    }

    [Fact]
    public void An_absent_counterparty_iban_stays_null_rather_than_empty()
    {
        var rows = Dutch();

        // An empty cell is the bank saying it has no counterparty account,
        // and an empty string would look like a real (broken) IBAN to
        // everything downstream.
        Assert.Equal("JUMBO 1234 UTRECHT", rows[1].Counterparty?.Name);
        Assert.Null(rows[1].Counterparty?.Iban);
    }

    [Fact]
    public void The_export_s_own_running_balance_is_verified_end_to_end()
    {
        // Where a bank hands over a redundant fact, check it. This is the
        // chain that catches a reordered file, a misread separator, an
        // inverted sign or a dropped row - all of which otherwise produce
        // believable garbage.
        BalanceChain.Verify(Dutch(), "test-bank");
        BalanceChain.Verify(English(), "test-bank");

        Assert.Null(BalanceChain.FindBreak(Dutch()));
    }

    [Fact]
    public void Row_order_is_preserved_rather_than_sorted()
    {
        var rows = Dutch();

        // Sorting first would hide the very reordering the chain check exists
        // to catch, so the file's own order stands.
        Assert.Equal(
            [
                new DateOnly(2026, 6, 2), new DateOnly(2026, 6, 5), new DateOnly(2026, 6, 11),
                new DateOnly(2026, 6, 24), new DateOnly(2026, 6, 28),
            ],
            rows.Select(t => t.BookedAt));
    }

    [Fact]
    public void Ids_are_deterministic_and_two_identical_rows_do_not_collapse()
    {
        var first = Dutch();
        var second = Dutch();

        // A CSV export carries no transaction reference at all, so the id is
        // synthesized - and it has to be stable across runs or every re-fetch
        // duplicates the whole statement.
        Assert.Equal(first.Select(t => t.ExternalId), second.Select(t => t.ExternalId));
        Assert.Equal(first.Select(t => t.ContentHash), second.Select(t => t.ContentHash));
        Assert.Equal(5, first.Select(t => t.ExternalId).Distinct(StringComparer.Ordinal).Count());

        const string Repeated = """
            Datum;Naam / Omschrijving;Code;Af Bij;Bedrag (EUR)
            05-06-2026;COFFEE COMPANY CS;BA;Af;3,50
            05-06-2026;COFFEE COMPANY CS;BA;Af;3,50
            """;

        // Two identical purchases on one day are common and must stay two
        // records, which is what the occurrence counter is for.
        var repeated = BankStatementCsv.ReadTransactions(Repeated, Options());
        Assert.Equal(2, repeated.Count);
        Assert.NotEqual(repeated[0].ExternalId, repeated[1].ExternalId);
    }

    [Fact]
    public void The_synthesized_id_changes_when_the_row_s_content_does()
    {
        var baseline = Dutch()[0].ExternalId;

        var altered = BankStatementCsv.ReadTransactions(
            BankFixtures.Read(BankFixtures.StatementCsvDutch)
                .Replace("1.425,00", "1.430,00", StringComparison.Ordinal),
            Options());

        Assert.NotEqual(baseline, altered[0].ExternalId);
    }

    // ---- defensive parsing -------------------------------------------------

    [Fact]
    public void An_unresolvable_required_column_names_the_column_and_the_headers_it_saw()
    {
        const string Renamed = """
            Boekdatum;Mutatie;Omschrijving
            02-06-2026;1.425,00;Huur
            """;

        var error = Assert.Throws<ConnectorException>(() => BankStatementCsv.ReadTransactions(Renamed, Options()));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.NotNull(error.Detail);

        // Naming the column is what makes the repair one alias in a list
        // rather than an afternoon with a hex editor.
        Assert.Contains("'amount'", error.Detail, StringComparison.Ordinal);
        Assert.Contains("bedrag", error.Detail, StringComparison.Ordinal);
        Assert.Contains("Mutatie", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_date_column_names_the_date_column()
    {
        const string NoDate = """
            Bedrag (EUR);Omschrijving
            1.425,00;Huur
            """;

        var error = Assert.Throws<ConnectorException>(() => BankStatementCsv.ReadTransactions(NoDate, Options()));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains("'date'", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_direction_indicator_is_refused_rather_than_defaulted()
    {
        const string Strange = """
            Datum;Naam / Omschrijving;Af Bij;Bedrag (EUR)
            02-06-2026;WONINGSTICHTING DE KLEINE;Zijwaarts;1.425,00
            """;

        var error = Assert.Throws<ConnectorException>(() => BankStatementCsv.ReadTransactions(Strange, Options()));

        // Defaulting to credit would turn a rent payment into income, and it
        // would look entirely plausible on the way past a reviewer.
        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains("Zijwaarts", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_amount_that_will_not_parse_names_the_row_and_the_column()
    {
        const string Malformed = """
            Datum;Naam / Omschrijving;Af Bij;Bedrag (EUR)
            02-06-2026;WONINGSTICHTING DE KLEINE;Af;1.4.2,5,0
            """;

        var error = Assert.Throws<ConnectorException>(() => BankStatementCsv.ReadTransactions(Malformed, Options()));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains("row 2", error.Detail, StringComparison.Ordinal);
        Assert.Contains("'amount'", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_date_in_the_wrong_order_is_refused_rather_than_reinterpreted()
    {
        const string MonthFirst = """
            Datum;Naam / Omschrijving;Af Bij;Bedrag (EUR)
            2026-06-31;WONINGSTICHTING DE KLEINE;Af;1.425,00
            """;

        // 06-31 is not a date under either reading; a parser that shrugged
        // would date the row by whatever it managed to salvage.
        var error = Assert.Throws<ConnectorException>(() => BankStatementCsv.ReadTransactions(MonthFirst, Options()));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
    }

    [Fact]
    public void An_export_with_no_account_column_and_no_adapter_supplied_account_is_refused()
    {
        const string NoAccount = """
            Datum;Naam / Omschrijving;Af Bij;Bedrag (EUR)
            02-06-2026;WONINGSTICHTING DE KLEINE;Af;1.425,00
            """;

        var error = Assert.Throws<ConnectorException>(() => BankStatementCsv.ReadTransactions(
            NoAccount, new BankStatementCsvOptions { SessionId = SessionId, ProviderId = "test-bank" }));

        // The transactions would have nowhere to attach.
        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains("account", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_export_is_a_shape_change()
    {
        var error = Assert.Throws<ConnectorException>(() => BankStatementCsv.ReadTransactions("   ", Options()));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
    }

    [Fact]
    public void A_header_only_export_is_a_valid_empty_statement()
    {
        // A month with no transactions is normal, and refusing it would make
        // an honest account look broken.
        Assert.Empty(BankStatementCsv.ReadTransactions(
            "Datum;Naam / Omschrijving;Af Bij;Bedrag (EUR)", Options()));
    }
}
