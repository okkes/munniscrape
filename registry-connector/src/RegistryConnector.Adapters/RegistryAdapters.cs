using Connector.Kit.Adapters;
using RegistryConnector.Adapters.Bkr;
using RegistryConnector.Adapters.Mock;

namespace RegistryConnector.Adapters;

/// <summary>
/// Everything the registry connector knows how to talk to, bound in one place
/// from one settings object.
///
/// A registry is somewhere that holds an official record ABOUT you rather than
/// a stream of things you did. BKR is the first one being built; a pension
/// overview or a student-debt balance is the same shape and belongs here
/// rather than in a fourth service.
///
/// Providers are code, not rows - the registry is built at startup from this
/// and only a provider's health is ever state.
/// </summary>
public static class RegistryAdapters
{
    /// <summary>
    /// Real providers plus the mock set. The mocks are registered in every
    /// environment on purpose: they are the only providers that can be
    /// exercised end to end without an account, and an environment that
    /// cannot demonstrate its own protocol is one nobody can debug.
    /// </summary>
    public static IReadOnlyList<IProviderAdapter> All(
        RegistryAdapterOptions? options = null, TimeProvider? time = null) =>
        [.. Real(options, time), .. MockRegistryAdapters.All(time)];

    /// <summary>
    /// BKR, the Dutch credit register.
    ///
    /// Built against a live capture of the portal rather than a guess about
    /// its markup - which is why it took a sign-in to write and not an
    /// afternoon of plausible selectors.
    /// </summary>
    public static IReadOnlyList<IProviderAdapter> Real(
        RegistryAdapterOptions? options = null, TimeProvider? time = null)
    {
        var settings = options ?? new RegistryAdapterOptions();

        return [new BkrAdapter(settings.Bkr, time)];
    }
}

/// <summary>
/// Every provider fact that might need correcting after a registry moves,
/// in one bindable object - so an operator edit is a deploy rather than a
/// release.
/// </summary>
public sealed class RegistryAdapterOptions
{
    public BkrOptions? Bkr { get; set; }
}
