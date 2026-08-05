using RegistryConnector.Adapters.Fixtures;

namespace RegistryConnector.Adapters.Tests;

internal static class Fixture
{
    public static string Read(string name) => FixtureCatalog.Read(name);
}
