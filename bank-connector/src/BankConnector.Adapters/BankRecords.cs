using Connector.Kit;
using Connector.Kit.Normalization;

namespace BankConnector.Adapters;

/// <summary>
/// Builds normalised records for bank adapters.
///
/// Ids and content hashes are minted here rather than in each adapter
/// because together they ARE the idempotency contract: the id must be
/// derivable from <c>(session, external_id)</c> so a re-run produces the
/// same ids, and the hash must cover exactly the fields that make a record
/// what it is. An adapter that improvises either one breaks re-fetch
/// overlap - and re-fetch overlap is what lets every adapter widen its
/// window to catch late-settling rows (see <see cref="BankWindow"/>).
/// </summary>
public static class BankRecords
{
    /// <summary>
    /// The id a transaction must reference. Exposed so an adapter that
    /// parses transactions before it has built the account record still
    /// produces a matching <see cref="Transaction.AccountId"/>.
    /// </summary>
    public static string AccountId(string sessionId, string externalId) =>
        Ids.ForRecord(Ids.Account, sessionId, externalId);

    public static Account NewAccount(
        string sessionId,
        string externalId,
        AccountType type,
        string displayName,
        string currency,
        string? iban = null,
        string? maskedNumber = null,
        Balance? balance = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);

        var account = new Account
        {
            Id = AccountId(sessionId, externalId),
            ExternalId = externalId,
            Type = type,
            DisplayName = displayName,
            Currency = currency.ToUpperInvariant(),
            Iban = iban,
            MaskedNumber = maskedNumber,
            Balance = balance,
        };

        return account with { ContentHash = ContentHash.Of(account) };
    }

    public static Transaction NewTransaction(
        string sessionId,
        string accountId,
        string externalId,
        DateOnly bookedAt,
        Money amount,
        DateOnly? valueAt = null,
        Counterparty? counterparty = null,
        string? description = null,
        TransactionKind kind = TransactionKind.Other,
        Money? resultingBalance = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);

        var transaction = new Transaction
        {
            Id = Ids.ForRecord(Ids.Transaction, sessionId, externalId),
            ExternalId = externalId,
            AccountId = accountId,
            BookedAt = bookedAt,
            ValueAt = valueAt,
            Amount = amount,
            Counterparty = counterparty,
            Description = description,
            Kind = kind,
            ResultingBalance = resultingBalance,
        };

        return transaction with { ContentHash = ContentHash.Of(transaction) };
    }

    /// <summary>
    /// A balance stamped at a stated moment. Bank exports date balances to a
    /// day, not an instant, so the offset is made explicit here rather than
    /// left to whatever local time zone the agent happens to run in.
    /// </summary>
    public static Balance BalanceOn(Money amount, DateOnly asOf) =>
        new() { Amount = amount, AsOf = new DateTimeOffset(asOf.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) };
}
