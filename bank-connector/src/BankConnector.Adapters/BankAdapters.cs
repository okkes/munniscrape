using BankConnector.Adapters.MockBank;
using Connector.Kit.Adapters;

namespace BankConnector.Adapters;

/// <summary>
/// The bank connector's provider set.
///
/// Providers are code, not rows: the registry is built at startup from this
/// list and only a provider's HEALTH is ever state. A host composes the
/// fleet it wants - the control plane registers only inline-class providers
/// it can actually run, an agent registers everything it has binaries for.
/// </summary>
public static class BankAdapters
{
    /// <summary>
    /// The complete mock fleet. Kept forever, not deleted once real
    /// providers land: it is what a demo user connects to, what CI runs
    /// against, and the only way to exercise every runtime tier and
    /// challenge type without five live bank accounts.
    /// </summary>
    public static IReadOnlyList<IProviderAdapter> MockFleet(TimeProvider? time = null) =>
    [
        new MockBankSimpleAdapter(time),
        new MockBankScaAdapter(time),
        new MockBankSlowAdapter(time),
        new MockBankBrokenAdapter(time),
        new MockBankPersistentAdapter(time),
    ];

    /// <summary>
    /// The subset a browserless host can run: the providers whose manifest
    /// declares <c>agent.required: false</c>. Registering more than this in
    /// the control plane would mean leasing a job no local process can
    /// serve.
    /// </summary>
    public static IReadOnlyList<IProviderAdapter> InlineOnly(TimeProvider? time = null) =>
        [.. MockFleet(time).Where(a => !a.Describe().Agent.Required)];

    public static IProviderRegistry MockRegistry(TimeProvider? time = null) =>
        new ProviderRegistry(MockFleet(time));
}
