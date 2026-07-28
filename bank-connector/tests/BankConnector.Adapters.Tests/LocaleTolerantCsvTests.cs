using BankConnector.Adapters.Parsing;
using Connector.Kit.Errors;
using Connector.Kit.Normalization;
using Xunit;

namespace BankConnector.Adapters.Tests;

/// <summary>
/// The reader underneath the statement parser.
///
/// Columns are resolved by matching known ALIASES, never by position.
/// Position-based mapping keeps working after a bank inserts a column and
/// starts writing the wrong data into the right field, which is the
/// silent-corruption failure this whole codebase is arranged to avoid.
/// </summary>
public sealed class LocaleTolerantCsvTests
{
    private static CsvSchema Schema(CsvDateOrder order = CsvDateOrder.DayFirst) => BankStatementCsv.Schema(order);

    [Fact]
    public void The_delimiter_is_chosen_by_how_many_known_columns_it_resolves()
    {
        // A semicolon file whose description contains commas. Scoring by
        // "how many fields do I get" would pick the comma and shred it.
        const string Text = """
            Datum;Naam / Omschrijving;Af Bij;Bedrag (EUR)
            02-06-2026;"DE KLEINE, WONINGSTICHTING";Af;1.425,00
            """;

        var table = LocaleTolerantCsv.Read(Text, Schema());

        Assert.Equal(4, table.Headers.Count);
        Assert.Equal("DE KLEINE, WONINGSTICHTING", table[0].Text(BankStatementCsv.ColumnCounterpartyName));
    }

    [Theory]
    [InlineData(';')]
    [InlineData(',')]
    [InlineData('\t')]
    [InlineData('|')]
    public void Every_delimiter_a_dutch_bank_uses_is_recognised(char delimiter)
    {
        var text = string.Join(
            Environment.NewLine,
            string.Join(delimiter, "Datum", "Naam", "Bedrag (EUR)"),
            string.Join(delimiter, "02-06-2026", "WONINGSTICHTING", "-1425.00"));

        var table = LocaleTolerantCsv.Read(text, Schema());

        Assert.Equal(new DateOnly(2026, 6, 2), table[0].Date(BankStatementCsv.ColumnDate));
        Assert.Equal(-142_500, table[0].MinorUnits(BankStatementCsv.ColumnAmount));
    }

    [Theory]
    [InlineData("Bedrag (EUR)")]
    [InlineData("BEDRAG EUR")]
    [InlineData("bedrag")]
    [InlineData("  Bedrag  ")]
    [InlineData("Amount (EUR)")]
    [InlineData("Transactiebedrag")]
    public void Case_spacing_and_punctuation_carry_no_meaning(string header)
    {
        var text = $"Datum;{header}{Environment.NewLine}02-06-2026;1.425,00";

        var table = LocaleTolerantCsv.Read(text, Schema());

        Assert.True(table.HasColumn(BankStatementCsv.ColumnAmount));
        Assert.Equal(142_500, table[0].MinorUnits(BankStatementCsv.ColumnAmount));
    }

    [Fact]
    public void A_short_alias_cannot_steal_a_header_a_longer_one_owns()
    {
        const string Text = """
            Datum;Rekening;Tegenrekening;Bedrag (EUR)
            02-06-2026;NL18ASNB0123456789;NL22RABO0123409876;-1425.00
            """;

        var table = LocaleTolerantCsv.Read(Text, Schema());

        // "rekening" must not claim "Tegenrekening", which is why exact
        // normalised equality runs for every column before containment runs
        // for any of them.
        Assert.Equal("NL18ASNB0123456789", table[0].Text(BankStatementCsv.ColumnAccount));
        Assert.Equal("NL22RABO0123409876", table[0].Text(BankStatementCsv.ColumnCounterpartyIban));
    }

    [Fact]
    public void An_optional_column_absent_from_an_export_is_simply_absent()
    {
        const string Text = """
            Datum;Bedrag (EUR)
            02-06-2026;-1425.00
            """;

        var table = LocaleTolerantCsv.Read(Text, Schema());

        Assert.False(table.HasColumn(BankStatementCsv.ColumnBalanceAfter));
        Assert.Null(table[0].Text(BankStatementCsv.ColumnBalanceAfter));
        Assert.Null(table[0].DateOrNull(BankStatementCsv.ColumnValueDate));
    }

    [Fact]
    public void A_required_cell_that_is_blank_names_the_row_and_the_column()
    {
        const string Text = """
            Datum;Bedrag (EUR)
            02-06-2026;
            """;

        var table = LocaleTolerantCsv.Read(Text, Schema());

        var error = Assert.Throws<ConnectorException>(() => table[0].Require(BankStatementCsv.ColumnAmount));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains("row 2", error.Detail, StringComparison.Ordinal);
        Assert.Contains("'amount'", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_short_row_is_a_bank_omitting_trailing_empties()
    {
        const string Text = """
            Datum;Naam;Bedrag (EUR);Saldo na mutatie
            02-06-2026;WONINGSTICHTING;-1425.00
            """;

        var table = LocaleTolerantCsv.Read(Text, Schema());

        Assert.Null(table[0].Text(BankStatementCsv.ColumnBalanceAfter));
        Assert.Equal(-142_500, table[0].MinorUnits(BankStatementCsv.ColumnAmount));
    }

    [Fact]
    public void A_long_row_is_a_quoting_bug_and_is_refused()
    {
        const string Text = """
            Datum;Bedrag (EUR)
            02-06-2026;-1425.00;something else entirely
            """;

        // Guessing which field moved is exactly the mistake alias matching
        // exists to prevent.
        var error = Assert.Throws<ConnectorException>(() => LocaleTolerantCsv.Read(Text, Schema()));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains("row 2", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_quoted_newline_is_part_of_a_field_and_not_a_row_boundary()
    {
        const string Text = "Datum;Mededelingen;Bedrag (EUR)\n02-06-2026;\"Huur juni\nen servicekosten\";-1425.00";

        var table = LocaleTolerantCsv.Read(Text, Schema());

        // Treating it as a row boundary would shift every subsequent field by
        // one column.
        Assert.Single(table);
        Assert.Equal("Huur juni\nen servicekosten", table[0].Text(BankStatementCsv.ColumnDescription));
    }

    [Fact]
    public void A_doubled_quote_inside_a_quoted_field_is_one_quote()
    {
        const string Text = """
            Datum;Mededelingen;Bedrag (EUR)
            02-06-2026;"Huur ""juni"" 2026";-1425.00
            """;

        var table = LocaleTolerantCsv.Read(Text, Schema());

        Assert.Equal(@"Huur ""juni"" 2026", table[0].Text(BankStatementCsv.ColumnDescription));
    }

    [Theory]
    [InlineData("1.425,00", 142_500)]
    [InlineData("1,425.00", 142_500)]
    [InlineData("1425,00", 142_500)]
    [InlineData("1425.00", 142_500)]
    [InlineData("-1425.00", -142_500)]
    [InlineData("0,01", 1)]
    [InlineData("1.234.567,89", 123_456_789)]
    public void Both_decimal_separators_resolve_without_a_locale(string raw, long expected)
    {
        var text = $"Datum;Bedrag (EUR){Environment.NewLine}02-06-2026;\"{raw}\"";

        var table = LocaleTolerantCsv.Read(text, Schema());

        // Whichever separator comes last is the decimal one; the other is a
        // thousands grouping. Both readings are unambiguous, so neither is
        // guessed.
        Assert.Equal(expected, table[0].MinorUnits(BankStatementCsv.ColumnAmount));
    }

    [Theory]
    [InlineData("Af", -142_500)]
    [InlineData("AF", -142_500)]
    [InlineData("Debet", -142_500)]
    [InlineData("D", -142_500)]
    [InlineData("Bij", 142_500)]
    [InlineData("Credit", 142_500)]
    [InlineData("C", 142_500)]
    public void The_direction_column_supplies_the_sign_in_every_spelling_banks_use(string indicator, long expected)
    {
        var text = $"Datum;Af Bij;Bedrag (EUR){Environment.NewLine}02-06-2026;{indicator};1.425,00";

        var table = LocaleTolerantCsv.Read(text, Schema());

        Assert.Equal(
            expected,
            table[0].SignedMinorUnits(BankStatementCsv.ColumnAmount, BankStatementCsv.ColumnDirection));
    }

    [Fact]
    public void A_missing_direction_cell_is_refused_rather_than_assumed()
    {
        const string Text = """
            Datum;Af Bij;Bedrag (EUR)
            02-06-2026;;1.425,00
            """;

        var table = LocaleTolerantCsv.Read(Text, Schema());

        var error = Assert.Throws<ConnectorException>(() => table[0].SignedMinorUnits(
            BankStatementCsv.ColumnAmount, BankStatementCsv.ColumnDirection));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
    }

    [Theory]
    [InlineData(CsvDateOrder.DayFirst, "03-04-2026", 4)]
    [InlineData(CsvDateOrder.MonthFirst, "03-04-2026", 3)]
    public void The_date_order_is_declared_and_never_inferred(CsvDateOrder order, string raw, int expectedMonth)
    {
        var text = $"Datum;Bedrag (EUR){Environment.NewLine}{raw};-1425.00";

        var table = LocaleTolerantCsv.Read(text, Schema(order));

        // 03-04-2026 is a real date under both readings and they mean
        // different months. There is no signal in the file that resolves it.
        Assert.Equal(expectedMonth, table[0].Date(BankStatementCsv.ColumnDate).Month);
    }

    [Theory]
    [InlineData("2026-06-02")]
    [InlineData("20260602")]
    [InlineData("02-06-2026")]
    [InlineData("02/06/2026")]
    [InlineData("2-6-2026")]
    [InlineData("02.06.2026")]
    public void Every_day_first_date_format_a_dutch_export_uses_is_read(string raw)
    {
        var text = $"Datum;Bedrag (EUR){Environment.NewLine}{raw};-1425.00";

        var table = LocaleTolerantCsv.Read(text, Schema());

        Assert.Equal(new DateOnly(2026, 6, 2), table[0].Date(BankStatementCsv.ColumnDate));
    }

    [Fact]
    public void A_byte_order_mark_does_not_hide_the_first_header()
    {
        const string Text = "﻿Datum;Bedrag (EUR)\n02-06-2026;-1425.00";

        var table = LocaleTolerantCsv.Read(Text, Schema());

        // Online banking exports are commonly UTF-8 with a BOM, and a BOM
        // glued to the first header makes the date column unresolvable.
        Assert.True(table.HasColumn(BankStatementCsv.ColumnDate));
        Assert.Equal(new DateOnly(2026, 6, 2), table[0].Date(BankStatementCsv.ColumnDate));
    }

    [Fact]
    public void A_declared_minor_unit_is_not_multiplied_again()
    {
        var text = $"Datum;Bedrag (EUR){Environment.NewLine}02-06-2026;142500";

        var table = LocaleTolerantCsv.Read(text, Schema());

        // The unit is the caller's declaration, not a property of the value:
        // 142500 is 1425.00 under Minor and 14250000 under MajorString, and
        // nothing here inspects the digits to decide.
        Assert.Equal(142_500, table[0].MinorUnits(BankStatementCsv.ColumnAmount, MoneyUnit.Minor));
        Assert.Equal(14_250_000, table[0].MinorUnits(BankStatementCsv.ColumnAmount, MoneyUnit.MajorString));
    }
}
