using Connector.Kit.Errors;
using Connector.Kit.Normalization;
using Xunit;

namespace Connector.Kit.Tests;

/// <summary>
/// A bank's running balances are its own arithmetic restated. Checking them
/// is free and catches the four failures that otherwise produce believable
/// garbage: rows out of order, a misread separator, an inverted debit flag,
/// and a dropped row.
/// </summary>
public sealed class BalanceChainTests
{
    /// <summary>Oldest first, as the contract requires. 100.00 -> 90.00 -> 140.00.</summary>
    private static List<Transaction> Consistent() =>
    [
        Make.Tx("t1", -2_500, resultingBalance: 10_000),
        Make.Tx("t2", -1_000, resultingBalance: 9_000),
        Make.Tx("t3", 5_000, resultingBalance: 14_000),
    ];

    [Fact]
    public void A_consistent_chain_passes()
    {
        Assert.Null(BalanceChain.FindBreak(Consistent()));
        BalanceChain.Verify(Consistent(), "ing");
    }

    [Fact]
    public void An_inverted_sign_is_detected()
    {
        var rows = Consistent();
        rows[2] = rows[2] with { Amount = Money.Eur(-5_000) };

        Assert.Equal(2, BalanceChain.FindBreak(rows));
    }

    [Fact]
    public void A_reordered_row_is_detected()
    {
        var rows = Consistent();
        (rows[1], rows[2]) = (rows[2], rows[1]);

        // Newest-first output handed to an oldest-first check is a common
        // adapter mistake and it must not pass silently.
        Assert.Equal(1, BalanceChain.FindBreak(rows));
    }

    [Fact]
    public void A_dropped_row_is_detected()
    {
        var rows = Consistent();
        rows.RemoveAt(1);

        // The failure a paginating fetch produces when a page boundary eats
        // a row - invisible in the output, obvious in the chain.
        Assert.Equal(1, BalanceChain.FindBreak(rows));
    }

    [Fact]
    public void A_misread_decimal_separator_is_detected()
    {
        var rows = Consistent();
        rows[1] = rows[1] with { Amount = Money.Eur(-100_000) };   // "1.000,00" read as 1000.00 euro

        Assert.Equal(1, BalanceChain.FindBreak(rows));
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, null)]        // the provider's own rounding, tolerated
    [InlineData(-1, null)]
    [InlineData(2, 1)]
    [InlineData(-2, 1)]
    public void Tolerates_exactly_one_cent(long drift, int? expectedBreak)
    {
        var rows = Consistent();
        rows[1] = rows[1] with { ResultingBalance = Money.Eur(9_000 + drift) };

        // A two-cent drift after a one-cent drift would cascade, so only the
        // first break is reported and the rest of the list is not guessed at.
        var actual = BalanceChain.FindBreak(rows);
        Assert.Equal(expectedBreak, actual);
    }

    [Fact]
    public void A_missing_balance_is_skipped_and_never_carried_forward()
    {
        var rows = Consistent();
        rows[1] = rows[1] with { ResultingBalance = null };
        rows[2] = rows[2] with { ResultingBalance = Money.Eur(999_999) };

        // Carrying row 0's balance across the gap would "prove" row 2 wrong
        // using a number the provider never stated. Both adjacent pairs are
        // skipped instead: an unchecked fact is not a failed check.
        Assert.Null(BalanceChain.FindBreak(rows));
    }

    [Fact]
    public void A_chain_that_states_no_balances_at_all_is_simply_unchecked()
    {
        // CAMT.053 states no per-entry balance. That is not a defect and
        // must not fail every bank whose export is an ISO statement.
        List<Transaction> rows =
        [
            Make.Tx("t1", -2_500),
            Make.Tx("t2", -1_000),
            Make.Tx("t3", 5_000),
        ];

        Assert.Null(BalanceChain.FindBreak(rows));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void A_chain_with_fewer_than_two_rows_has_no_pair_to_check(int count)
    {
        var rows = Consistent().Take(count).ToList();

        Assert.Null(BalanceChain.FindBreak(rows));
        BalanceChain.Verify(rows, "ing");
    }

    [Fact]
    public void Verify_refuses_to_emit_data_it_cannot_vouch_for()
    {
        var rows = Consistent();
        rows.RemoveAt(1);

        var ex = Assert.Throws<ConnectorException>(() => BalanceChain.Verify(rows, "asn"));

        // ProviderChanged, not Internal: the export's shape or ordering
        // moved and an adapter needs fixing. It also degrades the provider,
        // which is what stops the next user hitting the same bad data.
        Assert.Equal(ErrorCode.ProviderChanged, ex.Code);
        Assert.False(ex.Retriable);
        Assert.Contains("asn", ex.Message, StringComparison.Ordinal);
        Assert.Contains("t3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_a_null_list_rather_than_reporting_success()
    {
        Assert.Throws<ArgumentNullException>(() => BalanceChain.FindBreak(null!));
    }

    [Fact]
    public void The_first_row_is_never_checked_against_an_opening_balance_we_do_not_have()
    {
        // Row 0's own amount has nothing before it. Inventing an opening
        // balance to check it against would be checking our own arithmetic.
        var rows = Consistent();
        rows[0] = rows[0] with { Amount = Money.Eur(-999_999) };

        Assert.Null(BalanceChain.FindBreak(rows));
    }
}
