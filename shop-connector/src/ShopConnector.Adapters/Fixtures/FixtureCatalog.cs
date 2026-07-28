using System.Reflection;

namespace ShopConnector.Adapters.Fixtures;

/// <summary>
/// The recorded provider payloads, addressed as <c>"{provider}/{name}.json"</c>.
///
/// These exist so that parsing is tested fully offline, in CI, with no live
/// account - which is the only thing that catches a shape change on the
/// parse side before a user does. Adapter maintenance is the ongoing cost of
/// this service, so the fixtures are part of the contract, not a test
/// convenience: the mock providers read their data from here too, which
/// means a fixture that rots breaks the offline suite immediately.
/// </summary>
public static class FixtureCatalog
{
    private const string Prefix = "ShopConnector.Adapters.Fixtures.";

    private static readonly Assembly Owner = typeof(FixtureCatalog).Assembly;

    private static readonly Dictionary<string, string> Resources = Build();

    /// <summary>Every fixture, in <c>"ah/receipts-list.json"</c> form. Sorted.</summary>
    public static IReadOnlyList<string> Names { get; } = [.. Resources.Keys.Order(StringComparer.Ordinal)];

    public static string Read(string name)
    {
        using var stream = Open(name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static Stream Open(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!Resources.TryGetValue(name.Replace('\\', '/'), out var resource))
        {
            throw new FileNotFoundException(
                $"no fixture '{name}'; known fixtures are [{string.Join(", ", Names)}]", name);
        }

        return Owner.GetManifestResourceStream(resource)
               ?? throw new FileNotFoundException($"fixture '{name}' is registered but unreadable", name);
    }

    /// <summary>
    /// MSBuild flattens a fixture's folders into dots, so the friendly name
    /// is reconstructed by treating the last two segments as
    /// <c>file.extension</c>. That is why a fixture file name may contain
    /// exactly one dot.
    /// </summary>
    private static Dictionary<string, string> Build()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var resource in Owner.GetManifestResourceNames())
        {
            if (!resource.StartsWith(Prefix, StringComparison.Ordinal)) continue;

            var segments = resource[Prefix.Length..].Split('.');
            if (segments.Length < 2) continue;

            var file = $"{segments[^2]}.{segments[^1]}";
            var folders = segments[..^2];
            map[folders.Length == 0 ? file : $"{string.Join('/', folders)}/{file}"] = resource;
        }

        return map;
    }
}
