using Connector.Kit.Errors;
using Connector.Kit.Jobs;
using Connector.Kit.Normalization;

namespace BankConnector.Adapters;

/// <summary>
/// The wire spelling of an account type.
///
/// Written out rather than derived from <see cref="AccountType.ToString"/>
/// because the wire form is snake_case and part of the published contract
/// (<c>?accounts=savings,credit_card</c>), while the enum's name is
/// PascalCase. Deriving one from the other means a rename of a C# member
/// silently changes a public API.
/// </summary>
public static class AccountTypes
{
    /// <summary>The types a caller may name in an <c>accounts</c> parameter.</summary>
    public static readonly IReadOnlyList<string> Selectable = ["current", "savings", "credit_card"];

    public static string Wire(AccountType type) => type switch
    {
        AccountType.Current => "current",
        AccountType.Savings => "savings",
        AccountType.CreditCard => "credit_card",
        AccountType.Loan => "loan",
        _ => "unknown",
    };

    public static bool TryParse(string? wire, out AccountType type)
    {
        switch (wire)
        {
            case "current": type = AccountType.Current; return true;
            case "savings": type = AccountType.Savings; return true;
            case "credit_card": type = AccountType.CreditCard; return true;
            case "loan": type = AccountType.Loan; return true;
            default: type = AccountType.Unknown; return false;
        }
    }
}

/// <summary>
/// Resolves <c>?accounts=savings,credit_card</c> against what a session can
/// actually reach.
///
/// The parameter filters by account TYPE, not by account id, because the
/// caller usually does not know the ids yet - the ids are minted per
/// session and only exist after a login.
/// </summary>
public static class BankAccountFilter
{
    /// <summary>
    /// Omitting <c>accounts</c> means "everything this session can reach".
    /// Naming a type the session cannot reach is
    /// <see cref="ErrorCode.UnsupportedResource"/> with the reachable set in
    /// the detail - never a silent empty result, which reads to a caller as
    /// "you have no savings transactions" rather than "you asked wrong".
    /// </summary>
    public static IReadOnlyList<Account> Select(IReadOnlyList<Account> reachable, ResourceRequest request)
    {
        ArgumentNullException.ThrowIfNull(reachable);
        ArgumentNullException.ThrowIfNull(request);

        var wanted = request.Accounts;
        if (wanted.Count == 0) return reachable;

        var types = new HashSet<AccountType>();
        foreach (var wire in wanted)
        {
            if (!AccountTypes.TryParse(wire, out var type))
            {
                throw ConnectorException.Unsupported($"unknown account type '{wire}'; {Reachable(reachable)}");
            }

            if (!reachable.Any(a => a.Type == type))
            {
                throw ConnectorException.Unsupported($"account type '{wire}' is not reachable by this session; {Reachable(reachable)}");
            }

            types.Add(type);
        }

        // Filtering the reachable list rather than concatenating per type
        // keeps the provider's own account order and cannot emit a duplicate
        // when a caller names the same type twice.
        return [.. reachable.Where(a => types.Contains(a.Type))];
    }

    private static string Reachable(IReadOnlyList<Account> accounts) =>
        "reachable: " + string.Join(',', accounts.Select(a => AccountTypes.Wire(a.Type)).Distinct(StringComparer.Ordinal));
}
