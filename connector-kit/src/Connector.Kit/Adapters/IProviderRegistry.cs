using System.Security.Cryptography;
using System.Text.Json;
using Connector.Kit.Errors;
using Connector.Kit.Jobs;
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

    /// <param name="offerRawPayloads">
    /// Whether resources may offer <c>include=raw</c> - the provider's own
    /// document beside each record.
    /// </param>
    /// <remarks>
    /// False strips the value from every resource that declares it, which is
    /// the whole gate and needs no second one: the catalogue then never
    /// advertises raw, and <c>ParamBinder</c> already refuses an
    /// <c>include</c> value a resource does not list. A caller asking anyway
    /// gets <c>invalid_request</c> naming the value, rather than a silent
    /// empty field it would read as the provider sending nothing.
    /// <para>
    /// Withheld in production because raw is strictly the more sensitive of
    /// the two - normalisation drops the fields nobody asked for and this puts
    /// them back - and because the argument that neither service becomes the
    /// honeypot holding everyone's purchase history is easier to make when the
    /// unabridged version is not on offer at all. Diagnosing a shape change is
    /// what a development deployment is for.
    /// </para>
    /// </remarks>
    public ProviderRegistry(IEnumerable<IProviderAdapter> adapters, bool offerRawPayloads = true)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        _adapters = new Dictionary<string, IProviderAdapter>(StringComparer.Ordinal);
        _manifests = new Dictionary<string, ProviderManifest>(StringComparer.Ordinal);

        foreach (var adapter in adapters)
        {
            var described = adapter.Describe();
            var manifest = offerRawPayloads ? described : WithoutRawPayloads(described);

            // Validated after the strip, so what is served is what was
            // checked.
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

    /// <summary>
    /// The same manifest with <c>raw</c> taken off every <c>include</c> it
    /// appears on, and everything else untouched.
    ///
    /// A resource that never offered it is returned as-is, so this changes no
    /// digest it does not have to: the catalogue ETag moves only for the
    /// providers that actually lost a value.
    /// </summary>
    private static ProviderManifest WithoutRawPayloads(ProviderManifest manifest) => manifest with
    {
        Resources = [.. manifest.Resources.Select(WithoutRawPayloads)],
    };

    private static ResourceSpec WithoutRawPayloads(ResourceSpec resource) => resource with
    {
        Params = [.. resource.Params.Select(WithoutRawPayloads)],
    };

    private static ParamSpec WithoutRawPayloads(ParamSpec param) =>
        param.Key == ResourceRequest.IncludeParam
        && param.Values is { } values
        && values.Contains(ResourceRequest.RawInclude, StringComparer.Ordinal)
            ? param with
            {
                Values = [.. values.Where(v => v != ResourceRequest.RawInclude)],
            }
            : param;

    public bool TryGetAdapter(string providerId, out IProviderAdapter adapter) =>
        _adapters.TryGetValue(providerId, out adapter!);

    /// <summary>
    /// Over the whole catalogue as a consumer receives it, not over its
    /// version numbers.
    ///
    /// It hashed <c>id:manifest_version</c> pairs, which made the ETag wrong
    /// for every change that deliberately does not bump a version - and most
    /// do not, because <c>ManifestVersion</c> is sealed into bundles as AAD, so
    /// moving it to bust a cache would invalidate every credential a user
    /// holds. Adding a challenge type, a resource param, or withholding raw in
    /// production all left the digest identical, and a consumer revalidating
    /// against it kept rendering a form the service had stopped accepting.
    ///
    /// Serialised through the wire options, so this hashes exactly the bytes
    /// the catalogue endpoint returns: two processes on the same build agree,
    /// and any change a consumer could see moves it.
    /// </summary>
    private static string ComputeDigest(IReadOnlyList<ProviderManifest> manifests)
    {
        var seed = JsonSerializer.SerializeToUtf8Bytes(manifests, ConnectorWireJson.Options);
        var digest = SHA256.HashData(seed);
        return "sha256:" + Convert.ToHexStringLower(digest);
    }
}
