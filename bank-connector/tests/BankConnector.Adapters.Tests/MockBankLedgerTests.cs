using BankConnector.Adapters.MockBank;
using Connector.Kit.Errors;
using Connector.Kit.Normalization;
using Xunit;

namespace BankConnector.Adapters.Tests;

/// <summary>
/// The mock fleet's data, held to the invariant it exists to demonstrate.
///
/// A bank hands us a redundant fact - the running balance after each row -
/// and an unchecked redundancy is a redundancy wasted. The consistent ledger
/// must pass the chain check; the deliberately broken one must fail it with
/// <see cref="ErrorCode.ProviderChanged"/>. Without both halves the check
/// could be silently inert and every test would still be green.
/// </summary>
public sealed class MockBankLedgerTests
{
    private const string SessionId = "ses_0123456789abcdef0123456789abcdef";
    private const string ProviderId = "mock-bank-simple";

    private static FetchWindow WholeHistory =>
        new(MockBankLedger.Anchor.AddDays(-MockBankLedger.HistoryDays), MockBankLedger.Anchor);

    private static IReadOnlyList<Transaction> Rows(MockBankLedger ledger, FetchWindow? window = null)
    {
        var accounts = ledger.ToAccounts(SessionId);
        return ledger.ToTransactions(SessionId, accounts, window ?? WholeHistory);
    }

    [Fact]
    public void The_consistent_ledger_s_balance_chain_holds_for_every_account()
    {
        var rows = Rows(MockBankLedger.Consistent);

        Assert.NotEmpty(rows);
        BankEmission.VerifyPerAccount(ProviderId, rows);
    }

    [Fact]
    public void The_broken_ledger_fails_the_chain_and_names_the_offending_row()
    {
        var rows = Rows(MockBankLedger.BrokenChain);

        var error = Assert.Throws<ConnectorException>(
            () => BankEmission.VerifyPerAccount("mock-bank-broken", rows));

        // provider_changed degrades the provider and pages an operator, and
        // is never retried - a broken chain is what a bank changing its
        // export looks like from here.
        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.False(error.Retriable);
        Assert.Contains("balance chain broken", error.Detail, StringComparison.Ordinal);
        Assert.Contains(MockBankLedger.CurrentIban, error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void The_two_ledgers_differ_in_exactly_one_stated_balance()
    {
        var consistent = MockBankLedger.Consistent.Entries;
        var broken = MockBankLedger.BrokenChain.Entries;

        Assert.Equal(consistent.Count, broken.Count);

        var differences = consistent.Zip(broken).Where(pair => pair.First != pair.Second).ToList();

        // One nudged balance and nothing else: the fixture proves the check
        // fires on a realistic corruption rather than on a mangled file.
        var difference = Assert.Single(differences);
        Assert.Equal(difference.First.Amount, difference.Second.Amount);
        Assert.Equal(difference.First.BalanceAfter + 500, difference.Second.BalanceAfter);
    }

    [Fact]
    public void The_corruption_sits_inside_a_realistic_fetch_window()
    {
        var recent = new FetchWindow(MockBankLedger.Anchor.AddDays(-104), MockBankLedger.Anchor);

        // A default 90-day window widened by the 14-day settlement lag. A
        // corruption older than that would simply not be fetched, and the
        // provider would look healthy - which would defeat the fixture.
        Assert.Throws<ConnectorException>(
            () => BankEmission.VerifyPerAccount("mock-bank-broken", Rows(MockBankLedger.BrokenChain, recent)));
    }

    [Fact]
    public void The_chain_must_be_verified_per_account_and_not_over_the_concatenation()
    {
        var rows = Rows(MockBankLedger.Consistent);

        // Grouped, it holds. Concatenated, the balance jumps from one
        // account's closing figure to another's at every seam - so a naive
        // whole-list check would fail on data that is perfectly correct, and
        // whoever "fixed" that would probably delete the check.
        BankEmission.VerifyPerAccount(ProviderId, rows);
        Assert.Throws<ConnectorException>(() => BalanceChain.Verify(rows, ProviderId));
    }

    [Fact]
    public void Every_account_the_fixture_reaches_has_transactions_and_a_stated_balance()
    {
        var accounts = MockBankLedger.Consistent.ToAccounts(SessionId);
        var rows = Rows(MockBankLedger.Consistent);

        Assert.Equal(3, accounts.Count);
        Assert.All(accounts, account =>
        {
            Assert.NotNull(account.Balance);
            Assert.Equal("EUR", account.Currency);
            Assert.Contains(rows, t => t.AccountId == account.Id);
        });

        // A credit card has no IBAN, which is exactly why open banking cannot
        // see it and why this service exists.
        var card = Assert.Single(accounts, a => a.Type == AccountType.CreditCard);
        Assert.Null(card.Iban);
        Assert.Equal(MockBankLedger.CreditCardNumber, card.MaskedNumber);
    }

    [Fact]
    public void The_last_row_of_each_account_matches_the_balance_the_account_reports()
    {
        var accounts = MockBankLedger.Consistent.ToAccounts(SessionId);
        var rows = Rows(MockBankLedger.Consistent);

        foreach (var account in accounts)
        {
            var last = rows.Last(t => t.AccountId == account.Id);
            Assert.Equal(account.Balance?.Amount.Value, last.ResultingBalance?.Value);
        }
    }

    [Fact]
    public void The_fixture_is_anchored_so_ids_and_hashes_never_move()
    {
        var first = Rows(MockBankLedger.Consistent);
        var second = Rows(MockBankLedger.Consistent);

        // Anchored to a fixed date rather than to "today": a fixture whose
        // transactions move with the clock produces a different content hash
        // every day, which makes every idempotency assertion in the suite
        // either flaky or vacuous.
        Assert.Equal(first.Select(t => t.Id), second.Select(t => t.Id));
        Assert.Equal(first.Select(t => t.ContentHash), second.Select(t => t.ContentHash));
        Assert.Equal(new DateOnly(2026, 7, 26), MockBankLedger.Anchor);
    }

    [Fact]
    public void The_window_is_inclusive_at_both_ends_and_excludes_everything_outside_it()
    {
        var window = new FetchWindow(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30));

        var rows = Rows(MockBankLedger.Consistent, window);

        Assert.NotEmpty(rows);
        Assert.All(rows, t => Assert.True(window.Contains(t.BookedAt), $"{t.BookedAt} is outside the window"));
        Assert.Contains(rows, t => t.BookedAt == new DateOnly(2026, 6, 1));
    }

    [Fact]
    public void Paging_keeps_a_contiguous_oldest_first_prefix_so_the_chain_survives()
    {
        var rows = Rows(MockBankLedger.Consistent);

        var (page, complete) = BankEmission.Page(rows, 10);

        // Dropping rows out of the middle would break the chain, and dropping
        // the oldest would leave the caller with a gap it can never discover.
        Assert.Equal(10, page.Count);
        Assert.False(complete);
        Assert.Equal(rows.Take(10).Select(t => t.ExternalId), page.Select(t => t.ExternalId));
        BankEmission.VerifyPerAccount(ProviderId, page);

        var (whole, everything) = BankEmission.Page(rows, rows.Count);
        Assert.True(everything);
        Assert.Equal(rows.Count, whole.Count);
    }

    [Fact]
    public void The_reachable_set_names_the_account_types_a_caller_may_filter_on()
    {
        var reachable = MockBankLedger.Consistent.Reachable;

        // Sealed into the bundle for cheap discovery without a provider round
        // trip, and spelled in the wire form the accounts parameter takes.
        Assert.Equal(["current", "savings", "credit_card"], reachable.Select(a => a.Type));
        Assert.All(reachable, a => Assert.Contains(a.Type, AccountTypes.Selectable));
    }
}
