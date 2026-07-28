using Connector.Kit.Errors;
using Connector.Kit.Manifests;

namespace Connector.Kit.Adapters;

/// <summary>
/// Every provider a service knows about.
///
/// Both products register their own set into this and share every line of
/// the machinery around it. Providers are code, not rows: the registry is
/// built at startup and only a provider's HEALTH is ever state.
/// </summary>
public interface IProviderRegistry
{
    IReadOnlyList<ProviderManifest> Manifests { get; }

    bool TryGetManifest(string providerId, out ProviderManifest manifest);

    /// <summary>
    /// Throws <see cref="ErrorCode.UnsupportedResource"/> rather than
    /// returning null, so a caller naming an unknown provider gets a real
    /// error instead of a silent empty result.
    /// </summary>
    ProviderManifest RequireManifest(string providerId);

    /// <summary>
    /// The adapter implementation. Present only where the process is meant
    /// to run adapters - an agent always, the control plane only for
    /// inline-class providers.
    /// </summary>
    bool TryGetAdapter(string providerId, out IProviderAdapter adapter);

    /// <summary>
    /// A digest over every manifest, so a consumer can cache the catalogue
    /// and revalidate with an ETag instead of refetching it.
    /// </summary>
    string CatalogDigest { get; }
}

public sealed class ProviderRegistry : IProviderRegistry
{
    private readonly Dictionary<string, ProviderManifest> _manifests;
    private readonly Dictionary<string, IProviderAdapter> _adapters;

    public ProviderRegistry(IEnumerable<IProviderAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        _adapters = new Dictionary<string, IProviderAdapter>(StringComparer.Ordinal);
        _manifests = new Dictionary<string, ProviderManifest>(StringComparer.Ordinal);

        foreach (var adapter in adapters)
        {
            var manifest = adapter.Describe();
            ManifestValidator.Validate(manifest);

            if (!_manifests.TryAdd(manifest.Id, manifest))
            {
                throw new InvalidOperationException($"duplicate provider id '{manifest.Id}'");
            }

            _adapters[manifest.Id] = adapter;
        }

        Manifests = [.. _manifests.Values.OrderBy(m => m.Id, StringComparer.Ordinal)];
        CatalogDigest = ComputeDigest(Manifests);
    }

    public IReadOnlyList<ProviderManifest> Manifests { get; }

    public string CatalogDigest { get; }

    public bool TryGetManifest(string providerId, out ProviderManifest manifest) =>
        _manifests.TryGetValue(providerId, out manifest!);

    public ProviderManifest RequireManifest(string providerId) =>
        _manifests.TryGetValue(providerId, out var m)
            ? m
            : throw ConnectorException.Unsupported($"unknown provider '{providerId}'");

    public bool TryGetAdapter(string providerId, out IProviderAdapter adapter) =>
        _adapters.TryGetValue(providerId, out adapter!);

    private static string ComputeDigest(IReadOnlyList<ProviderManifest> manifests)
    {
        var seed = string.Join('|', manifests.Select(m => $"{m.Id}:{m.ManifestVersion}"));
        var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
        return "sha256:" + Convert.ToHexStringLower(digest);
    }
}
