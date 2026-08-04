using Connector.Kit.Jobs;
using RegistryConnector.Adapters.Mock;

namespace RegistryConnector.Adapters.Tests;

internal static class Requests
{
    /// <summary>
    /// No date range, because a register has none to give: it states what is
    /// true now.
    /// </summary>
    public static ResourceRequest Credits() => new()
    {
        ResourceId = MockRegistryAdapter.CreditsResource,
        Selections = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
    };
}
