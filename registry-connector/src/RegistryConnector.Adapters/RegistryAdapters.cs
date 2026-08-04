using Connector.Kit.Adapters;
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
    /// Empty, for now, and deliberately so.
    ///
    /// BKR is the reason this service exists and it is not here yet, because
    /// nobody on this project has seen the inside of
    /// portaal.mijnkredietregistratie.nl - only a screenshot of the credit
    /// list. The last provider built from a plausible guess about markup was
    /// bol, and every guess in it was wrong in a way that cost a live account
    /// attempt to discover.
    ///
    /// So the order is: capture the portal, then write the adapter against
    /// what it actually says. The mocks below already exercise the shape BKR
    /// will have - a stored password plus a fresh authenticator code on every
    /// sync - so the service, the challenge relay and the consuming app are
    /// all provably working before the first real byte arrives.
    /// </summary>
    public static IReadOnlyList<IProviderAdapter> Real(
        RegistryAdapterOptions? options = null, TimeProvider? time = null)
    {
        _ = options;
        _ = time;

        return [];
    }
}

/// <summary>
/// Every provider fact that might need correcting after a registry moves,
/// in one bindable object - so an operator edit is a deploy rather than a
/// release.
/// </summary>
public sealed class RegistryAdapterOptions
{
}
