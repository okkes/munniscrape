using BankConnector.Adapters;
using Connector.Kit.Adapters;
using Connector.Kit.Errors;
using Connector.Kit.Jobs;
using Connector.Kit.Manifests;
using ShopConnector.Adapters;
using Xunit;

namespace Connector.Kit.Tests;

/// <summary>
/// The validator is only worth something if the manifests actually shipped
/// pass it. A validator exercised solely against manifests written by its
/// own test is a validator that agrees with itself.
/// </summary>
public sealed class ShippedManifestTests
{
    private static IReadOnlyList<IProviderAdapter> Bank => BankAdapters.MockFleet();

    private static IReadOnlyList<IProviderAdapter> Shop => ShopAdapters.All();

    public static TheoryData<string> BankProviders() => ProviderIds(BankAdapters.MockFleet());

    public static TheoryData<string> ShopProviders() => ProviderIds(ShopAdapters.All());

    [Theory]
    [MemberData(nameof(BankProviders))]
    public void Every_bank_manifest_validates(string providerId)
    {
        ManifestValidator.Validate(Describe(Bank, providerId));
    }

    [Theory]
    [MemberData(nameof(ShopProviders))]
    public void Every_shop_manifest_validates(string providerId)
    {
        ManifestValidator.Validate(Describe(Shop, providerId));
    }

    [Fact]
    public void Both_products_actually_ship_providers()
    {
        // Guards the theories above: a fleet that emptied out would make
        // every one of them vacuously pass by never running.
        Assert.NotEmpty(Bank);
        Assert.NotEmpty(Shop);
    }

    [Theory]
    [MemberData(nameof(BankProviders))]
    public void Every_bank_manifest_declares_the_right_kind(string providerId)
    {
        Assert.Equal(ProviderKind.Bank, Describe(Bank, providerId).Kind);
    }

    [Theory]
    [MemberData(nameof(ShopProviders))]
    public void Every_shop_manifest_declares_the_right_kind(string providerId)
    {
        Assert.Equal(ProviderKind.Store, Describe(Shop, providerId).Kind);
    }

    [Theory]
    [MemberData(nameof(BankProviders))]
    public void Describe_is_stable_for_every_bank_provider(string providerId)
    {
        AssertDescribeIsStable(Bank, providerId);
    }

    [Theory]
    [MemberData(nameof(ShopProviders))]
    public void Describe_is_stable_for_every_shop_provider(string providerId)
    {
        AssertDescribeIsStable(Shop, providerId);
    }

    [Fact]
    public void A_registry_builds_over_each_product_and_validates_on_the_way_in()
    {
        // ProviderRegistry validates every manifest in its constructor, so a
        // bad one refuses to boot rather than failing at a user's login.
        AssertRegistry(new ProviderRegistry(Bank));
        AssertRegistry(new ProviderRegistry(Shop));
    }

    [Fact]
    public void A_duplicate_provider_id_refuses_to_build()
    {
        var one = Bank[0];

        Assert.Throws<InvalidOperationException>(() => new ProviderRegistry([one, one]));
    }

    [Fact]
    public void An_unknown_provider_is_an_error_and_not_an_empty_result()
    {
        var registry = new ProviderRegistry(Shop);

        var ex = Assert.Throws<ConnectorException>(() => registry.RequireManifest("does-not-exist"));

        Assert.Equal(ErrorCode.UnsupportedResource, ex.Code);
        Assert.False(registry.TryGetManifest("does-not-exist", out _));
        Assert.False(registry.TryGetAdapter("does-not-exist", out _));
    }

    [Fact]
    public void The_catalogue_digest_moves_with_a_manifest_version()
    {
        // This is what a consumer revalidates its cached catalogue against,
        // so it has to move whenever a bundle-invalidating version moves.
        var registry = new ProviderRegistry(Shop);
        var bumped = new ProviderRegistry([.. Shop.Select(a => (IProviderAdapter)new VersionBump(a))]);

        Assert.NotEqual(registry.CatalogDigest, bumped.CatalogDigest);
    }

    [Fact]
    public void The_two_products_do_not_share_a_provider_id()
    {
        // Separate services, separate databases, separate subject salts. A
        // shared id would be the first thing to make them look correlatable.
        var bank = Bank.Select(a => a.Describe().Id).ToHashSet(StringComparer.Ordinal);
        var shop = Shop.Select(a => a.Describe().Id).ToHashSet(StringComparer.Ordinal);

        Assert.False(bank.Overlaps(shop));
    }

    private static TheoryData<string> ProviderIds(IEnumerable<IProviderAdapter> adapters)
    {
        var data = new TheoryData<string>();
        foreach (var adapter in adapters) data.Add(adapter.Describe().Id);
        return data;
    }

    private static IProviderAdapter Adapter(IEnumerable<IProviderAdapter> fleet, string providerId) =>
        fleet.Single(a => string.Equals(a.Describe().Id, providerId, StringComparison.Ordinal));

    private static ProviderManifest Describe(IEnumerable<IProviderAdapter> fleet, string providerId) =>
        Adapter(fleet, providerId).Describe();

    private static void AssertDescribeIsStable(IEnumerable<IProviderAdapter> fleet, string providerId)
    {
        var adapter = Adapter(fleet, providerId);
        var first = adapter.Describe();
        var second = adapter.Describe();

        // Describe answers every catalogue request. One that rebuilt state,
        // read a clock or did I/O would turn a cheap cached GET into work,
        // and would move the catalogue digest under a consumer's ETag.
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.ManifestVersion, second.ManifestVersion);
        Assert.Equal(first.Runtime, second.Runtime);
        Assert.Equal(first.SecretCustody, second.SecretCustody);
        Assert.Equal(first.Unattended, second.Unattended);
        Assert.Equal(first.Auth.Session.TtlSeconds, second.Auth.Session.TtlSeconds);
        Assert.Equal(
            first.Resources.Select(r => r.Id).ToArray(),
            second.Resources.Select(r => r.Id).ToArray());
    }

    private static void AssertRegistry(ProviderRegistry registry)
    {
        Assert.NotEmpty(registry.Manifests);
        Assert.Matches("^sha256:[0-9a-f]{64}$", registry.CatalogDigest);

        // Ordered by id so the digest is a function of the manifests and not
        // of the order a host happened to register them in.
        Assert.Equal(
            registry.Manifests.Select(m => m.Id).Order(StringComparer.Ordinal).ToArray(),
            registry.Manifests.Select(m => m.Id).ToArray());

        foreach (var manifest in registry.Manifests)
        {
            Assert.True(registry.TryGetManifest(manifest.Id, out var found));
            Assert.Same(manifest, found);
            Assert.True(registry.TryGetAdapter(manifest.Id, out _));
            Assert.Same(manifest, registry.RequireManifest(manifest.Id));
        }
    }

    /// <summary>
    /// The same provider, one manifest version later. A double, not a stub:
    /// only <see cref="Describe"/> is ever reached, and the two run methods
    /// throw rather than pretending to have done something.
    /// </summary>
    private sealed class VersionBump : IProviderAdapter
    {
        private readonly ProviderManifest _manifest;

        public VersionBump(IProviderAdapter inner)
        {
            var manifest = inner.Describe();
            _manifest = manifest with { ManifestVersion = manifest.ManifestVersion + 1 };
        }

        public ProviderManifest Describe() => _manifest;

        public Task<LoginResult> LoginAsync(IJobContext ctx, CancellationToken ct) =>
            throw new NotSupportedException("catalogue-only double");

        public Task<FetchResult> FetchAsync(IJobContext ctx, ResourceRequest request, CancellationToken ct) =>
            throw new NotSupportedException("catalogue-only double");
    }
}
