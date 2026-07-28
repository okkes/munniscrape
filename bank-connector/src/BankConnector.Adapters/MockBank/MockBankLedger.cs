using System.Globalization;
using Connector.Kit.Normalization;
using Connector.Kit.Security;

namespace BankConnector.Adapters.MockBank;

public sealed record MockBankAccount
{
    public required string ExternalId { get; init; }

    public required AccountType Type { get; init; }

    public required string DisplayName { get; init; }

    public required long OpeningBalance { get; init; }

    public string Currency { get; init; } = "EUR";

    public string? Iban { get; init; }

    public string? MaskedNumber { get; init; }
}

public sealed record MockBankEntry
{
    public required string AccountExternalId { get; init; }

    public required string ExternalId { get; init; }

    public required DateOnly BookedAt { get; init; }

    /// <summary>Minor units, signed. Negative means money left the account.</summary>
    public required long Amount { get; init; }

    /// <summary>Minor units. The whole point of the fixture: it must chain.</summary>
    public required long BalanceAfter { get; init; }

    public string? Description { get; init; }

    public Counterparty? Counterparty { get; init; }

    public TransactionKind Kind { get; init; } = TransactionKind.Other;
}

/// <summary>
/// The mock fleet's data, generated once and identical forever.
///
/// Anchored to a fixed date rather than to "today" on purpose. A fixture
/// whose transactions move with the clock produces a different
/// <see cref="ContentHash"/> every day, which makes every idempotency
/// assertion in the suite either flaky or vacuous - and idempotency is
/// precisely what these fixtures exist to prove.
/// </summary>
public sealed class MockBankLedger
{
    /// <summary>The newest day in the fixture.</summary>
    public static readonly DateOnly Anchor = new(2026, 7, 26);

    /// <summary>Six months: enough to span several statement periods and a quarterly interest posting.</summary>
    public const int HistoryDays = 180;

    public const string CurrentIban = "NL18MOCK0123456789";
    public const string SavingsIban = "NL91MOCK0417164300";
    public const string CreditCardNumber = "5412 •••• •••• 4242";
    public const string SalaryIban = "NL44MOCK0009876543";
    public const string LandlordIban = "NL22MOCK0123409876";

    private static readonly MockBankAccount[] Definitions =
    [
        new()
        {
            ExternalId = CurrentIban,
            Type = AccountType.Current,
            DisplayName = "Betaalrekening",
            Iban = CurrentIban,
            OpeningBalance = 250_000,
        },
        new()
        {
            ExternalId = SavingsIban,
            Type = AccountType.Savings,
            DisplayName = "Oranje Spaarrekening",
            Iban = SavingsIban,
            OpeningBalance = 1_250_045,
        },
        new()
        {
            // A credit card has no IBAN, which is exactly why open banking
            // cannot see it and why this service exists.
            ExternalId = "mock-cc-4242",
            Type = AccountType.CreditCard,
            DisplayName = "Mock Card Gold",
            MaskedNumber = CreditCardNumber,
            OpeningBalance = -18_750,
        },
    ];

    // Declared before the ledgers below: static field initialisers run in
    // declaration order, and the generator reads this one.
    private static readonly string[] Merchants =
    [
        "JUMBO 1234 UTRECHT",
        "ALBERT HEIJN 8891",
        "LIDL 0442 AMERSFOORT",
        "BAKKERIJ VAN DIJK",
        "NS REIZIGERS",
        "MOCK APOTHEEK",
        "COFFEE COMPANY CS",
    ];

    private MockBankLedger(IReadOnlyList<MockBankEntry> entries)
    {
        Entries = entries;
        Accounts = Definitions;
        Reachable =
        [
            .. Definitions.Select(a => new ReachableAccount
            {
                ExternalId = a.ExternalId,
                Type = AccountTypes.Wire(a.Type),
                DisplayName = a.DisplayName,
            }),
        ];
    }

    /// <summary>The fixture the honest providers serve. Its balance chain holds.</summary>
    public static MockBankLedger Consistent { get; } = new(Generate());

    /// <summary>
    /// The same fixture with one resulting balance nudged by five euros.
    ///
    /// It exists so the balance-chain verifier can be proven to fire, and so
    /// the operator alert path has something realistic to fire on: a broken
    /// chain is what a real bank changing its export looks like from here.
    /// </summary>
    public static MockBankLedger BrokenChain { get; } = new(Corrupt(Generate()));

    public IReadOnlyList<MockBankAccount> Accounts { get; }

    /// <summary>Oldest-first within each account, accounts in declaration order.</summary>
    public IReadOnlyList<MockBankEntry> Entries { get; }

    public IReadOnlyList<ReachableAccount> Reachable { get; }

    public IReadOnlyList<Account> ToAccounts(string sessionId)
    {
        var accounts = new List<Account>(Accounts.Count);
        foreach (var definition in Accounts)
        {
            var last = Entries.LastOrDefault(e => string.Equals(e.AccountExternalId, definition.ExternalId, StringComparison.Ordinal));

            accounts.Add(BankRecords.NewAccount(
                sessionId,
                definition.ExternalId,
                definition.Type,
                definition.DisplayName,
                definition.Currency,
                definition.Iban,
                definition.MaskedNumber,
                BankRecords.BalanceOn(
                    new Money(last?.BalanceAfter ?? definition.OpeningBalance, definition.Currency),
                    last?.BookedAt ?? Anchor.AddDays(-HistoryDays))));
        }

        return accounts;
    }

    /// <summary>
    /// Entries for the named accounts inside the window, oldest-first and in
    /// the ledger's own order - the order the balance chain is checked in.
    /// </summary>
    public IReadOnlyList<Transaction> ToTransactions(
        string sessionId,
        IReadOnlyList<Account> accounts,
        FetchWindow window)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        var wanted = accounts.ToDictionary(a => a.ExternalId, a => a.Id, StringComparer.Ordinal);
        var transactions = new List<Transaction>();

        foreach (var entry in Entries)
        {
            if (!wanted.TryGetValue(entry.AccountExternalId, out var accountId)) continue;
            if (!window.Contains(entry.BookedAt)) continue;

            var currency = Accounts.First(a => string.Equals(a.ExternalId, entry.AccountExternalId, StringComparison.Ordinal)).Currency;

            transactions.Add(BankRecords.NewTransaction(
                sessionId,
                accountId,
                entry.ExternalId,
                entry.BookedAt,
                new Money(entry.Amount, currency),
                entry.BookedAt,
                entry.Counterparty,
                entry.Description,
                entry.Kind,
                new Money(entry.BalanceAfter, currency)));
        }

        return transactions;
    }

    /// <summary>
    /// Deterministic by construction: every amount is a pure function of the
    /// day, and the running balance is accumulated forward, so the chain is
    /// exact rather than approximately right.
    /// </summary>
    private static List<MockBankEntry> Generate()
    {
        var entries = new List<MockBankEntry>(256);
        var start = Anchor.AddDays(-HistoryDays);

        foreach (var account in Definitions)
        {
            var balance = account.OpeningBalance;
            var sequence = 0;

            for (var day = start; day <= Anchor; day = day.AddDays(1))
            {
                foreach (var (amount, description, counterparty, kind) in EventsFor(account, day, balance))
                {
                    balance += amount;
                    sequence++;

                    entries.Add(new MockBankEntry
                    {
                        AccountExternalId = account.ExternalId,
                        ExternalId = string.Create(CultureInfo.InvariantCulture, $"{account.ExternalId}:{sequence:D5}"),
                        BookedAt = day,
                        Amount = amount,
                        BalanceAfter = balance,
                        Description = description,
                        Counterparty = counterparty,
                        Kind = kind,
                    });
                }
            }
        }

        return entries;
    }

    private static IEnumerable<(long Amount, string Description, Counterparty? Counterparty, TransactionKind Kind)> EventsFor(
        MockBankAccount account, DateOnly day, long balance)
    {
        // A cheap spread that is a pure function of the date, so the same
        // day always produces the same amount without a random source.
        var jitter = (day.DayOfYear * 137) % 4_800;

        switch (account.Type)
        {
            case AccountType.Current:
                if (day.Day == 1)
                {
                    yield return (-142_500, "Huur", new Counterparty { Name = "WONINGSTICHTING DE KLEINE", Iban = LandlordIban }, TransactionKind.DirectDebit);
                }

                if (day.Day == 12)
                {
                    yield return (-8_950, "Energie maandtermijn", new Counterparty { Name = "MOCK ENERGIE NV" }, TransactionKind.DirectDebit);
                }

                if (day.Day == 24)
                {
                    yield return (285_000, "Salaris", new Counterparty { Name = "MOCK EMPLOYER BV", Iban = SalaryIban }, TransactionKind.Transfer);
                }

                if (day.Day == 25)
                {
                    yield return (-50_000, "Naar Oranje Spaarrekening", new Counterparty { Name = "O DOKER", Iban = SavingsIban }, TransactionKind.Transfer);
                }

                if (day.DayOfWeek == DayOfWeek.Saturday)
                {
                    yield return (-(3_200 + jitter), "Betaalautomaat", Merchant(day), TransactionKind.CardPayment);
                }

                if (day.DayOfWeek == DayOfWeek.Wednesday)
                {
                    yield return (-(850 + (jitter % 1_400)), "Betaalautomaat", Merchant(day), TransactionKind.CardPayment);
                }

                break;

            case AccountType.Savings:
                if (day.Day == 25)
                {
                    yield return (50_000, "Van Betaalrekening", new Counterparty { Name = "O DOKER", Iban = CurrentIban }, TransactionKind.Transfer);
                }

                // Quarterly interest on the last day of the quarter, at
                // 1.5% annual. Integer arithmetic throughout: a fixture that
                // rounds is a fixture whose chain drifts.
                if (day.Month % 3 == 0 && day.Day == DateTime.DaysInMonth(day.Year, day.Month))
                {
                    yield return (balance * 15 / 4_000, "Rente", null, TransactionKind.Interest);
                }

                break;

            case AccountType.CreditCard:
                if (day.DayOfWeek is DayOfWeek.Monday or DayOfWeek.Thursday)
                {
                    yield return (-(1_100 + (jitter % 3_900)), "Card purchase", Merchant(day), TransactionKind.CardPayment);
                }

                // The monthly settlement clears whatever is outstanding, so
                // the balance genuinely returns to zero the way a real card
                // does after a direct debit.
                if (day.Day == 5 && balance < 0)
                {
                    yield return (-balance, "Automatische incasso creditcard", new Counterparty { Name = "O DOKER", Iban = CurrentIban }, TransactionKind.DirectDebit);
                }

                break;

            case AccountType.Loan:
            case AccountType.Unknown:
            default:
                break;
        }
    }

    private static Counterparty Merchant(DateOnly day) =>
        new() { Name = Merchants[day.DayOfYear % Merchants.Length] };

    /// <summary>
    /// Breaks the chain five euros' worth, near the RECENT end of the
    /// current account but not at the very last row.
    ///
    /// Recent because a fetch window is a suffix of history and a corruption
    /// older than the default window would simply not be fetched - the
    /// provider would then look healthy, which defeats the fixture. Not the
    /// last row because a break is a property of a PAIR: the verifier needs
    /// a row after the corrupted one to compare against.
    /// </summary>
    private static List<MockBankEntry> Corrupt(List<MockBankEntry> entries)
    {
        var current = entries
            .Select((entry, index) => (entry, index))
            .Where(x => string.Equals(x.entry.AccountExternalId, CurrentIban, StringComparison.Ordinal))
            .Select(x => x.index)
            .ToList();

        var target = current[^3];
        entries[target] = entries[target] with { BalanceAfter = entries[target].BalanceAfter + 500 };
        return entries;
    }
}
