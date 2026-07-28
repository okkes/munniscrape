using Connector.Kit.Manifests;

namespace BankConnector.Adapters;

/// <summary>
/// The two resources every bank provider offers, in one place.
///
/// A bank connector's public surface is genuinely uniform - accounts and
/// transactions, filtered by type and date - so a per-provider copy of
/// these specs would only be an opportunity for one provider to spell a
/// parameter differently and break the generic route handler.
/// </summary>
public static class BankResources
{
    public const string Accounts = "accounts";
    public const string Transactions = "transactions";

    /// <summary>
    /// Cheap discovery. Served from the bundle's reachable set without
    /// touching the provider whenever that data is still fresh, which is
    /// why the expected duration is a couple of seconds rather than a
    /// browser login.
    /// </summary>
    public static ResourceSpec AccountsSpec() => new()
    {
        Id = Accounts,
        Returns = ResourceShape.Account,
        TypicalDurationSeconds = 5,
        MaxRecordsPerFetch = 50,
    };

    public static ResourceSpec TransactionsSpec(
        int maxHistoryDays = 540,
        int typicalDurationSeconds = 90,
        int maxRecordsPerFetch = 200) => new()
    {
        Id = Transactions,
        Returns = ResourceShape.Transaction,
        MaxHistoryDays = maxHistoryDays,
        TypicalDurationSeconds = typicalDurationSeconds,
        MaxRecordsPerFetch = maxRecordsPerFetch,
        Params =
        [
            // Filtering by TYPE rather than by id: the caller usually has
            // not seen the ids yet, because ids are minted per session.
            new ParamSpec
            {
                Key = "accounts",
                Type = ParamType.Enum,
                Multi = true,
                Required = false,
                Values = AccountTypes.Selectable,
            },
            new ParamSpec { Key = "since", Type = ParamType.Date, Required = false },
            new ParamSpec { Key = "until", Type = ParamType.Date, Required = false },
        ],
    };
}

public static class BankLimits
{
    /// <summary>
    /// Dutch card payments settle late: a purchase on the 19th can surface
    /// on the 21st still dated the 19th. Fourteen days covers the worst
    /// case we have seen (a holiday weekend plus a foreign-currency
    /// clearing) with room to spare, and the cost of being generous is one
    /// slightly larger export, deduplicated for free by content hash.
    /// </summary>
    public const int DutchCardSettlementLagDays = 14;

    /// <summary>
    /// Six hours is the platform default - human-plausible, not a firehose.
    /// A mock provider touches no network at all, so politeness towards it
    /// is meaningless and a short floor is what makes a demo or an
    /// integration test usable.
    /// </summary>
    public const int MockMinIntervalSeconds = 60;

    public static ProviderLimits ForBank(
        int minIntervalSeconds = 21_600,
        int maxHistoryDays = 540,
        int settlementLagDays = DutchCardSettlementLagDays) => new()
    {
        MinIntervalSeconds = minIntervalSeconds,
        Concurrency = 1,
        MaxHistoryDays = maxHistoryDays,
        SettlementLagDays = settlementLagDays,
    };
}
