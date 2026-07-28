using BankConnector.Adapters.Parsing;
using Connector.Kit.Normalization;
using Xunit;

namespace BankConnector.Adapters.Tests;

/// <summary>
/// CAMT.053 against the two reference dialects this assembly ships: a bank
/// that populates almost every optional element, and one that populates
/// almost none.
///
/// The point of two fixtures is that the differences between them are all
/// FIXTURE problems, not code branches: a different namespace version, a
/// missing account currency, a different entry reference element, proprietary
/// transaction codes instead of the ISO tree, and a batched entry. A parser
/// that only survives one of them survives one bank.
/// </summary>
public sealed class Camt053ParserTests
{
    private const string SessionId = "ses_0123456789abcdef0123456789abcdef";

    private static Camt053Options Options(AccountType type = AccountType.Unknown, string? displayName = null) => new()
    {
        SessionId = SessionId,
        ProviderId = "test-bank",
        AccountType = type,
        DisplayName = displayName,
    };

    private static Camt053Statement Ing() => Assert.Single(Camt053Parser.Parse(
        BankFixtures.Read(BankFixtures.Camt053IngSavings), Options(AccountType.Savings)));

    private static Camt053Statement Asn() => Assert.Single(Camt053Parser.Parse(
        BankFixtures.Read(BankFixtures.Camt053AsnCurrent), Options(AccountType.Current)));

    // ---- the rich dialect: camt.053.001.02 ---------------------------------

    [Fact]
    public void The_account_comes_from_the_elements_the_bank_actually_populated()
    {
        var statement = Ing();

        Assert.Equal("NL91INGB0417164300-2026-06", statement.StatementId);
        Assert.Equal("NL91INGB0417164300", statement.Account.ExternalId);
        Assert.Equal("NL91INGB0417164300", statement.Account.Iban);
        Assert.Equal("EUR", statement.Account.Currency);

        // Acct/Nm, which this bank fills in and the sparse one does not.
        Assert.Equal("Oranje Spaarrekening", statement.Account.DisplayName);

        // CAMT.053 has no notion of "savings": the standard identifies an
        // account but never says what kind it is, so the adapter declares it.
        Assert.Equal(AccountType.Savings, statement.Account.Type);
    }

    [Fact]
    public void Opening_and_closing_balances_are_read_with_their_dates()
    {
        var statement = Ing();

        Assert.NotNull(statement.Opening);
        Assert.Equal(1_250_045, statement.Opening.Amount.Value);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), statement.Opening.AsOf);

        Assert.NotNull(statement.Closing);
        Assert.Equal(1_325_462, statement.Closing.Amount.Value);
        Assert.Equal(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero), statement.Closing.AsOf);

        // The account carries the closing figure: it is the one a consumer
        // should show.
        Assert.Equal(statement.Closing, statement.Account.Balance);
    }

    [Fact]
    public void Debits_are_negative_and_credits_are_positive()
    {
        var statement = Ing();

        Assert.Equal(4, statement.Transactions.Count);

        // CRDT, DBIT, CRDT, CRDT. A silently inverted sign is the single most
        // damaging thing a bank parser can do, which is why the direction is
        // read from CdtDbtInd and never inferred from anything else.
        Assert.Equal([50_000, -25_000, 50_000, 417], statement.Transactions.Select(t => t.Amount.Value));
        Assert.All(statement.Transactions, t => Assert.Equal("EUR", t.Amount.Currency));
    }

    [Fact]
    public void Opening_plus_every_entry_equals_closing()
    {
        var statement = Ing();

        // The redundancy CAMT.053 actually offers, and the reason a dropped
        // entry or a flipped indicator cannot pass unnoticed.
        Assert.NotNull(statement.Opening);
        Assert.NotNull(statement.Closing);
        Assert.Equal(
            statement.Closing.Amount.Value,
            statement.Opening.Amount.Value + statement.Transactions.Sum(t => t.Amount.Value));
    }

    [Fact]
    public void Booking_and_value_dates_are_both_carried()
    {
        var statement = Ing();
        var first = statement.Transactions[0];

        Assert.Equal(new DateOnly(2026, 6, 3), first.BookedAt);
        Assert.Equal(new DateOnly(2026, 6, 3), first.ValueAt);
        Assert.Equal(new DateOnly(2026, 6, 30), statement.Transactions[3].BookedAt);
    }

    [Fact]
    public void The_counterparty_is_the_creditor_on_a_debit_and_the_debtor_on_a_credit()
    {
        var statement = Ing();

        var credit = statement.Transactions[0];
        Assert.Equal("O DOKER", credit.Counterparty?.Name);
        Assert.Equal("NL18ASNB0123456789", credit.Counterparty?.Iban);

        var debit = statement.Transactions[1];
        Assert.Equal("O DOKER", debit.Counterparty?.Name);
        Assert.Equal("NL18ASNB0123456789", debit.Counterparty?.Iban);
    }

    [Fact]
    public void The_remitter_s_own_message_is_preferred_over_the_bank_s_rendering()
    {
        var statement = Ing();

        // RmtInf/Ustrd is what a user recognises on their own statement.
        Assert.Equal("Maandelijkse inleg", statement.Transactions[0].Description);
        Assert.Equal("Opname naar betaalrekening", statement.Transactions[1].Description);

        // The interest entry has no RmtInf at all, so AddtlNtryInf stands in.
        Assert.Equal("Rente tweede kwartaal 2026", statement.Transactions[3].Description);
    }

    [Fact]
    public void Iso_transaction_codes_are_classified()
    {
        var statement = Ing();

        Assert.Equal(TransactionKind.Transfer, statement.Transactions[0].Kind);   // Fmly RCDT
        Assert.Equal(TransactionKind.Transfer, statement.Transactions[1].Kind);   // Fmly ICDT
        Assert.Equal(TransactionKind.Interest, statement.Transactions[3].Kind);   // Domn INTR
    }

    [Fact]
    public void The_account_servicer_reference_is_the_external_id()
    {
        var statement = Ing();

        Assert.Equal(
            ["INGB2026060300012", "INGB2026061500047", "INGB2026062400091", "INGB2026063000004"],
            statement.Transactions.Select(t => t.ExternalId));

        // The first entry's EndToEndId is the ISO placeholder NOTPROVIDED.
        // Treating that as an id would collapse every such row onto one, so
        // the stated AcctSvcrRef has to win.
        Assert.DoesNotContain(statement.Transactions, t => t.ExternalId == "NOTPROVIDED");
    }

    [Fact]
    public void No_per_entry_balance_is_invented()
    {
        var statement = Ing();

        // CAMT.053 states none. Deriving one from the opening figure and then
        // "verifying" it would check our own arithmetic against itself.
        Assert.All(statement.Transactions, t => Assert.Null(t.ResultingBalance));

        // Which also means the chain check has nothing to object to, rather
        // than passing on numbers we made up.
        Assert.Null(BalanceChain.FindBreak(statement.Transactions));
    }

    [Fact]
    public void Ids_and_hashes_are_deterministic_so_a_re_parse_deduplicates()
    {
        var first = Ing();
        var second = Ing();

        Assert.Equal(first.Account.Id, second.Account.Id);
        Assert.Equal(first.Account.ContentHash, second.Account.ContentHash);
        Assert.Equal(first.Transactions.Select(t => t.Id), second.Transactions.Select(t => t.Id));
        Assert.Equal(first.Transactions.Select(t => t.ContentHash), second.Transactions.Select(t => t.ContentHash));

        Assert.All(first.Transactions, t => Assert.Equal(first.Account.Id, t.AccountId));
        Assert.All(first.Transactions, t => Assert.StartsWith("txn_", t.Id, StringComparison.Ordinal));
    }

    // ---- the sparse dialect: camt.053.001.08 -------------------------------

    [Fact]
    public void A_different_message_version_is_read_by_the_same_parser()
    {
        var statement = Asn();

        // The namespace encodes the message version. Binding to it would mean
        // a parser per version for a schema whose element names did not
        // change.
        Assert.Equal("0000006", statement.StatementId);
        Assert.Equal(5, statement.Transactions.Count);
    }

    [Fact]
    public void A_statement_with_no_account_currency_takes_it_from_the_first_entry()
    {
        var statement = Asn();

        Assert.Equal("EUR", statement.Account.Currency);
        Assert.All(statement.Transactions, t => Assert.Equal("EUR", t.Amount.Currency));
    }

    [Fact]
    public void A_statement_with_no_account_name_falls_back_to_the_identifier()
    {
        var statement = Asn();

        Assert.Equal("NL18ASNB0123456789", statement.Account.DisplayName);
    }

    [Fact]
    public void An_adapter_supplied_display_name_overrides_the_bank_s()
    {
        var statement = Assert.Single(Camt053Parser.Parse(
            BankFixtures.Read(BankFixtures.Camt053AsnCurrent),
            Options(AccountType.Current, displayName: "Betaalrekening")));

        Assert.Equal("Betaalrekening", statement.Account.DisplayName);
    }

    [Fact]
    public void Proprietary_dutch_mutation_codes_are_classified()
    {
        var statement = Asn();

        // Banks that predate the ISO code set still emit two-letter codes
        // under Prtry, and dropping those on the floor would leave every
        // Dutch transaction classified as "other".
        Assert.Equal(TransactionKind.DirectDebit, statement.Transactions[0].Kind);   // IC
        Assert.Equal(TransactionKind.CardPayment, statement.Transactions[1].Kind);   // BA
        Assert.Equal(TransactionKind.Transfer, statement.Transactions[3].Kind);      // OV
    }

    [Fact]
    public void The_party_moved_under_a_pty_wrapper_is_still_found()
    {
        var statement = Asn();

        // From camt.053.001.08 the party sits under an extra Pty element to
        // make room for an agent identification. Both shapes are in
        // production simultaneously.
        Assert.Equal("WONINGSTICHTING DE KLEINE", statement.Transactions[0].Counterparty?.Name);
        Assert.Equal("NL22RABO0123409876", statement.Transactions[0].Counterparty?.Iban);

        // The older, unwrapped shape, in the same file and with no account.
        Assert.Equal("JUMBO 1234 UTRECHT", statement.Transactions[1].Counterparty?.Name);
        Assert.Null(statement.Transactions[1].Counterparty?.Iban);
    }

    [Fact]
    public void A_batched_entry_gets_no_counterparty_because_it_has_no_single_one()
    {
        var statement = Asn();
        var batched = statement.Transactions[2];

        // Two TxDtls under one entry. Picking the first would be an
        // invention; the batch label in AddtlNtryInf is what the bank
        // actually said.
        Assert.Null(batched.Counterparty);
        Assert.Equal("Verzamelboeking 2 betalingen", batched.Description);
        Assert.Equal(-1_890, batched.Amount.Value);
    }

    [Fact]
    public void Repeated_unstructured_remittance_lines_are_joined()
    {
        var statement = Asn();

        // Banks split a long message across several Ustrd elements, and
        // taking only the first silently truncates what the user wrote.
        Assert.Equal("Salaris juni 2026 periode 06", statement.Transactions[3].Description);
    }

    [Fact]
    public void An_entry_with_no_value_date_still_books()
    {
        var statement = Asn();
        var last = statement.Transactions[4];

        // Some banks date an entry only by booking; absent is optional, not
        // a shape change.
        Assert.Equal(new DateOnly(2026, 6, 28), last.BookedAt);
        Assert.Null(last.ValueAt);
    }

    [Fact]
    public void The_entry_reference_element_may_be_ntryref_instead_of_acctsvcrref()
    {
        var statement = Asn();

        Assert.Equal(
            ["0000006-001", "0000006-002", "0000006-003", "0000006-004", "0000006-005"],
            statement.Transactions.Select(t => t.ExternalId));
    }

    [Fact]
    public void A_closing_balance_dated_by_timestamp_is_read_as_its_day()
    {
        var statement = Asn();

        Assert.NotNull(statement.Closing);
        Assert.Equal(319_369, statement.Closing.Amount.Value);
        Assert.Equal(new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero), statement.Closing.AsOf);

        Assert.NotNull(statement.Opening);
        Assert.Equal(184_230, statement.Opening.Amount.Value);
        Assert.Equal(
            statement.Closing.Amount.Value,
            statement.Opening.Amount.Value + statement.Transactions.Sum(t => t.Amount.Value));
    }
}
