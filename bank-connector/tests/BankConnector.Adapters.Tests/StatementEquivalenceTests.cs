using BankConnector.Adapters.Parsing;
using Connector.Kit.Normalization;
using Xunit;

namespace BankConnector.Adapters.Tests;

/// <summary>
/// One statement, three exports.
///
/// The ASN camt.053 fixture and both CSV fixtures describe the SAME June
/// statement. That is the strongest assertion available offline: two
/// completely independent parsers, fed two completely different file
/// formats, have to agree on every amount, sign and date. A bug in either
/// one shows up here even when each file's own internal checks pass.
/// </summary>
public sealed class StatementEquivalenceTests
{
    private const string SessionId = "ses_0123456789abcdef0123456789abcdef";
    private const string Account = "NL18ASNB0123456789";

    private static IReadOnlyList<Transaction> FromCamt() => Assert.Single(Camt053Parser.Parse(
        BankFixtures.Read(BankFixtures.Camt053AsnCurrent),
        new Camt053Options
        {
            SessionId = SessionId,
            ProviderId = "test-bank",
            AccountType = AccountType.Current,
        })).Transactions;

    private static IReadOnlyList<Transaction> FromCsv(string fixture) => BankStatementCsv.ReadTransactions(
        BankFixtures.Read(fixture),
        new BankStatementCsvOptions
        {
            SessionId = SessionId,
            ProviderId = "test-bank",
            AccountExternalId = Account,
        });

    [Fact]
    public void Every_format_agrees_on_the_amounts_and_their_signs()
    {
        long[] expected = [-142_500, -4_231, -1_890, 285_000, -1_240];

        Assert.Equal(expected, FromCamt().Select(t => t.Amount.Value));
        Assert.Equal(expected, FromCsv(BankFixtures.StatementCsvDutch).Select(t => t.Amount.Value));
        Assert.Equal(expected, FromCsv(BankFixtures.StatementCsvEnglish).Select(t => t.Amount.Value));
    }

    [Fact]
    public void Every_format_agrees_on_the_booking_dates()
    {
        DateOnly[] expected =
        [
            new(2026, 6, 2), new(2026, 6, 5), new(2026, 6, 11), new(2026, 6, 24), new(2026, 6, 28),
        ];

        Assert.Equal(expected, FromCamt().Select(t => t.BookedAt));
        Assert.Equal(expected, FromCsv(BankFixtures.StatementCsvDutch).Select(t => t.BookedAt));
        Assert.Equal(expected, FromCsv(BankFixtures.StatementCsvEnglish).Select(t => t.BookedAt));
    }

    [Fact]
    public void Every_format_agrees_on_the_transaction_kinds()
    {
        TransactionKind[] expected =
        [
            TransactionKind.DirectDebit, TransactionKind.CardPayment, TransactionKind.CardPayment,
            TransactionKind.Transfer, TransactionKind.CardPayment,
        ];

        // The camt file states these as proprietary Prtry codes and the CSVs
        // as a mutation-code column; both resolve to the same classification.
        Assert.Equal(expected, FromCamt().Select(t => t.Kind));
        Assert.Equal(expected, FromCsv(BankFixtures.StatementCsvDutch).Select(t => t.Kind));
    }

    [Fact]
    public void The_two_formats_attach_every_transaction_to_the_same_account()
    {
        var accountId = BankRecords.AccountId(SessionId, Account);

        // The id is derived from (session, external_id), so a camt export and
        // a CSV export of one account converge on one account id and the
        // consumer sees a single account rather than two.
        Assert.All(FromCamt(), t => Assert.Equal(accountId, t.AccountId));
        Assert.All(FromCsv(BankFixtures.StatementCsvDutch), t => Assert.Equal(accountId, t.AccountId));
    }

    [Fact]
    public void Only_the_batched_entry_differs_and_it_differs_honestly()
    {
        var camt = FromCamt();
        var csv = FromCsv(BankFixtures.StatementCsvDutch);

        // camt.053 states the batch as two TxDtls under one entry, so there
        // is no single counterparty to name and inventing one would be a
        // fabrication. The CSV has only a name column, so the bank's own
        // batch label lands in it. Both are correct; the difference is the
        // formats', not the parsers'.
        Assert.Null(camt[2].Counterparty);
        Assert.Equal("VERZAMELBOEKING", csv[2].Counterparty?.Name);

        // Everywhere the formats state the same fact, they agree.
        foreach (var index in (int[])[0, 1, 3, 4])
        {
            Assert.Equal(camt[index].Counterparty?.Name, csv[index].Counterparty?.Name);
            Assert.Equal(camt[index].Counterparty?.Iban, csv[index].Counterparty?.Iban);
        }
    }

    [Fact]
    public void The_csv_running_balance_lands_where_the_camt_closing_balance_does()
    {
        var statement = Assert.Single(Camt053Parser.Parse(
            BankFixtures.Read(BankFixtures.Camt053AsnCurrent),
            new Camt053Options { SessionId = SessionId, ProviderId = "test-bank", AccountType = AccountType.Current }));

        var csv = FromCsv(BankFixtures.StatementCsvDutch);

        // The camt statement's CLBD and the CSV's last "saldo na mutatie" are
        // the same number arrived at two different ways: one stated by the
        // bank, one accumulated row by row.
        Assert.NotNull(statement.Closing);
        Assert.Equal(319_369, statement.Closing.Amount.Value);
        Assert.Equal(statement.Closing.Amount.Value, csv[^1].ResultingBalance?.Value);

        Assert.NotNull(statement.Opening);
        Assert.Equal(
            csv[^1].ResultingBalance?.Value,
            statement.Opening.Amount.Value + csv.Sum(t => t.Amount.Value));
    }
}
