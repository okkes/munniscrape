using BankConnector.Adapters.Parsing;
using Connector.Kit.Errors;
using Connector.Kit.Normalization;
using Xunit;

namespace BankConnector.Adapters.Tests;

/// <summary>
/// What happens when the export's shape moves.
///
/// The whole taxonomy rests on one distinction. An OPTIONAL element that is
/// absent is a bank being a bank, and must parse. A REQUIRED element that is
/// missing, or a value that will not parse, means the export changed - and
/// that is <see cref="ErrorCode.ProviderChanged"/>, which pages an operator
/// and degrades the provider, never <see cref="ErrorCode.Internal"/>, which
/// would be retried forever, and never a plausible-looking statement.
/// </summary>
public sealed class Camt053DefensiveTests
{
    private static Camt053Options Options() => new()
    {
        SessionId = "ses_0123456789abcdef0123456789abcdef",
        ProviderId = "test-bank",
        AccountType = AccountType.Current,
    };

    private static string Asn() => BankFixtures.Read(BankFixtures.Camt053AsnCurrent);

    private static ConnectorException Rejects(string xml)
    {
        var error = Assert.Throws<ConnectorException>(() => Camt053Parser.Parse(xml, Options()));

        // Never Internal: a shape change needs a human to fix an adapter, and
        // a retriable code would just hide it behind a retry loop.
        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.False(error.Retriable);
        Assert.Contains(ErrorCode.ProviderChanged, ErrorCatalog.NeverRetry);

        Assert.NotNull(error.Detail);
        Assert.Contains("test-bank", error.Detail, StringComparison.Ordinal);
        return error;
    }

    /// <summary>Removes the first occurrence of a self-contained element.</summary>
    private static string Without(string xml, string fragment)
    {
        var index = xml.IndexOf(fragment, StringComparison.Ordinal);
        Assert.True(index >= 0, $"the fixture no longer contains '{fragment}'");
        return xml.Remove(index, fragment.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_export_is_a_shape_change(string xml) => Rejects(xml);

    [Fact]
    public void An_export_that_is_not_well_formed_xml_is_a_shape_change() =>
        Rejects("<Document><BkToCstmrStmt><Stmt>");

    [Fact]
    public void A_document_that_is_not_a_camt_053_statement_names_what_it_is_missing()
    {
        var error = Rejects("<Document xmlns=\"urn:iso:std:iso:20022:tech:xsd:camt.052.001.02\"><BkToCstmrAcctRpt/></Document>");

        Assert.Contains("BkToCstmrStmt", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_report_with_no_statement_is_a_shape_change()
    {
        var error = Rejects("<Document><BkToCstmrStmt><GrpHdr><MsgId>x</MsgId></GrpHdr></BkToCstmrStmt></Document>");

        Assert.Contains("no Stmt", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_account_that_identifies_itself_neither_way_is_a_shape_change()
    {
        // Neither IBAN nor the proprietary Othr/Id that credit-card and
        // non-payment accounts appear under - and those are exactly the
        // accounts this service exists to reach.
        var error = Rejects(Without(Asn(), "<IBAN>NL18ASNB0123456789</IBAN>"));

        Assert.Contains("Othr/Id", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_entry_with_no_amount_is_a_shape_change()
    {
        var error = Rejects(Without(Asn(), "<Amt Ccy=\"EUR\">1425.00</Amt>"));

        Assert.Contains("Amt", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_entry_with_no_debit_credit_indicator_is_a_shape_change()
    {
        // Defaulting to credit here would turn a rent payment into income.
        var error = Rejects(Without(Asn(), "<CdtDbtInd>DBIT</CdtDbtInd>"));

        Assert.Contains("CdtDbtInd", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_debit_credit_indicator_is_a_shape_change()
    {
        var error = Rejects(Asn().Replace(
            "<CdtDbtInd>DBIT</CdtDbtInd>", "<CdtDbtInd>SIDEWAYS</CdtDbtInd>", StringComparison.Ordinal));

        Assert.Contains("SIDEWAYS", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_entry_with_neither_booking_nor_value_date_is_a_shape_change()
    {
        var xml = Without(Asn(), """
        <BookgDt>
          <Dt>2026-06-28</Dt>
        </BookgDt>
""");

        // The last entry already has no ValDt, so removing its BookgDt leaves
        // an entry that cannot be dated at all - which is unusable rather
        // than merely sparse.
        var error = Rejects(xml);
        Assert.Contains("BookgDt", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_amount_that_will_not_parse_is_a_shape_change()
    {
        var error = Rejects(Asn().Replace(
            "<Amt Ccy=\"EUR\">1425.00</Amt>", "<Amt Ccy=\"EUR\">1.4.2.5</Amt>", StringComparison.Ordinal));

        Assert.Contains("Ntry/Amt", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_date_that_is_not_iso_is_a_shape_change()
    {
        var error = Rejects(Asn().Replace(
            "<Dt>2026-06-02</Dt>", "<Dt>02-06-2026</Dt>", StringComparison.Ordinal));

        Assert.Contains("2026", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_balance_with_no_date_is_a_shape_change()
    {
        var xml = Without(Asn(), """
        <Dt>
          <Dt>2026-06-01</Dt>
        </Dt>
""");

        // A balance without a date is not a balance: the consumer cannot tell
        // whether it precedes or follows the entries.
        var error = Rejects(xml);
        Assert.Contains("no date", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_inverted_indicator_is_caught_by_the_statement_s_own_balances()
    {
        // The one that matters most, because it produces a statement that
        // looks entirely plausible: five entries, sensible amounts, sensible
        // dates, and a salary posted as a payment.
        var xml = Asn().Replace(
            """
            <NtryRef>0000006-004</NtryRef>
                    <Amt Ccy="EUR">2850.00</Amt>
                    <CdtDbtInd>CRDT</CdtDbtInd>
            """,
            """
            <NtryRef>0000006-004</NtryRef>
                    <Amt Ccy="EUR">2850.00</Amt>
                    <CdtDbtInd>DBIT</CdtDbtInd>
            """,
            StringComparison.Ordinal);

        Assert.NotEqual(Asn(), xml);

        var error = Rejects(xml);
        Assert.Contains("does not reconcile", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dropped_entry_is_caught_by_the_statement_s_own_balances()
    {
        var xml = Asn();
        var start = xml.IndexOf("<Ntry>", StringComparison.Ordinal);
        var end = xml.IndexOf("</Ntry>", StringComparison.Ordinal) + "</Ntry>".Length;

        var error = Rejects(xml.Remove(start, end - start));

        Assert.Contains("does not reconcile", error.Detail, StringComparison.Ordinal);
        Assert.Contains("4 entries", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_transaction_code_degrades_rather_than_failing_the_run()
    {
        var xml = Asn().Replace(
            "<Cd>IC</Cd>", "<Cd>ZZ</Cd>", StringComparison.Ordinal);

        var statement = Assert.Single(Camt053Parser.Parse(xml, Options()));

        // A classification is a hint for the consumer's UI and never touches
        // an amount, so an unrecognised code is not worth failing a
        // statement over - the opposite of how an unparseable amount is
        // treated.
        Assert.Equal(TransactionKind.Other, statement.Transactions[0].Kind);
        Assert.Equal(-142_500, statement.Transactions[0].Amount.Value);
    }

    [Fact]
    public void An_element_the_parser_has_never_seen_is_ignored()
    {
        var xml = Asn().Replace(
            "<AddtlNtryInf>Huur juni 2026</AddtlNtryInf>",
            "<AddtlNtryInf>Huur juni 2026</AddtlNtryInf><SomethingNewIso20022Added><Nested>1</Nested></SomethingNewIso20022Added>",
            StringComparison.Ordinal);

        var statement = Assert.Single(Camt053Parser.Parse(xml, Options()));

        Assert.Equal(5, statement.Transactions.Count);
        Assert.Equal("Huur juni 2026", statement.Transactions[0].Description);
    }

    [Fact]
    public void An_empty_optional_element_is_treated_as_absent()
    {
        var xml = Asn().Replace(
            "<Nm>JUMBO 1234 UTRECHT</Nm>", "<Nm></Nm>", StringComparison.Ordinal);

        var statement = Assert.Single(Camt053Parser.Parse(xml, Options()));

        // A bank emitting <Nm/> is telling us it has no name, not that the
        // name is the empty string.
        Assert.Null(statement.Transactions[1].Counterparty);
    }

    [Fact]
    public void An_external_entity_in_a_statement_is_refused_outright()
    {
        // A statement file is data from a third party; an external entity in
        // one is a file-read primitive on the agent.
        const string Attack = """
            <?xml version="1.0"?>
            <!DOCTYPE Document [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
            <Document><BkToCstmrStmt><Stmt><Id>&xxe;</Id></Stmt></BkToCstmrStmt></Document>
            """;

        Rejects(Attack);
    }
}
