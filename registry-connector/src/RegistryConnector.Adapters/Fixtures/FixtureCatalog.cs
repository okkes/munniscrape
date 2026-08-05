using System.Reflection;

namespace RegistryConnector.Adapters.Fixtures;

/// <summary>
/// Recorded provider payloads, embedded in the assembly.
///
/// Embedded rather than read from disk so a parse test needs no file paths and
/// cannot drift from the assembly it tests; also copied to the output so an
/// operator diffing a live capture against the recorded shape can just open
/// the file.
/// </summary>
public static class FixtureCatalog
{
    private static readonly Assembly Owner = typeof(FixtureCatalog).Assembly;

    private const string Root = "RegistryConnector.Adapters.Fixtures.";

    public static string Read(string name)
    {
        using var stream = Open(name);
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }

    private static Stream Open(string name)
    {
        var resource = Root + name.Replace('/', '.');

        return Owner.GetManifestResourceStream(resource)
               ?? throw new FileNotFoundException(
                   $"no fixture '{name}'; known fixtures are [{string.Join(", ", Known())}]", name);
    }

    private static IEnumerable<string> Known() =>
        Owner.GetManifestResourceNames()
            .Where(n => n.StartsWith(Root, StringComparison.Ordinal))
            .Select(n => n[Root.Length..]);
}
