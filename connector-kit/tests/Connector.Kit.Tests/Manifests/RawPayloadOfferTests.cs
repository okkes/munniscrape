using Connector.Kit.Adapters;
using Connector.Kit.Jobs;
using Connector.Kit.Manifests;
using Xunit;

namespace Connector.Kit.Tests;

/// <summary>
/// Whether the catalogue offers <c>include=raw</c> at all.
///
/// Raw is the provider's own document beside each record, and it is strictly
/// the more sensitive of the two: normalisation drops the fields nobody asked
/// for and this puts them back. The README states the policy - "connectors are
/// pipes, not stores... raw provider payloads are off in production" - and the
/// registry is where it is kept, because withholding it from the CATALOGUE is
/// what stops a consumer offering a control that production would refuse.
///
/// One mechanism does both halves. Once the value is off the resource,
/// <c>ParamBinder</c>'s existing enum check refuses a request that asks for it
/// anyway, naming the value rather than returning a silent empty field.
/// </summary>
public sealed class RawPayloadOfferTests
{
    private static ProviderManifest Manifest(params string[] includeValues) => Make.Manifest() with
    {
        Resources =
        [
            new ResourceSpec
            {
                Id = "receipts",
                Returns = ResourceShape.Receipt,
                Params =
                [
                    new ParamSpec { Key = "since", Type = ParamType.Date, Required = true },
                    new ParamSpec
                    {
                        Key = ResourceRequest.IncludeParam,
                        Type = ParamType.Enum,
                        Values = includeValues,
                        Multi = true,
                    },
                ],
            },
        ],
    };

    private static IReadOnlyList<string> IncludeValues(IProviderRegistry registry) =>
        registry.RequireManifest("mock-provider").Resource("receipts")!
            .Param(ResourceRequest.IncludeParam)!.Values!;

    [Fact]
    public void A_development_catalogue_offers_raw_where_the_adapter_declared_it()
    {
        var registry = new ProviderRegistry(
            [new StubAdapter(Manifest("items", "raw"))], offerRawPayloads: true);

        Assert.Equal(["items", "raw"], IncludeValues(registry));
    }

    [Fact]
    public void A_production_catalogue_offers_everything_except_raw()
    {
        var registry = new ProviderRegistry(
            [new StubAdapter(Manifest("items", "raw"))], offerRawPayloads: false);

        // The value is gone and nothing else is. Dropping `items` too would
        // take the line items with it, which is ordinary data a consumer is
        // entitled to.
        Assert.Equal(["items"], IncludeValues(registry));
    }

    /// <summary>
    /// The default is the permissive one because a registry built by hand - in
    /// a test, or in an agent - is not a production control plane. Production
    /// says so explicitly, at the one place that knows.
    /// </summary>
    [Fact]
    public void Raw_is_offered_unless_the_caller_says_otherwise()
    {
        var registry = new ProviderRegistry([new StubAdapter(Manifest("items", "raw"))]);

        Assert.Contains(ResourceRequest.RawInclude, IncludeValues(registry));
    }

    [Fact]
    public void A_provider_that_never_offered_raw_is_untouched_either_way()
    {
        var offered = new ProviderRegistry([new StubAdapter(Manifest("items"))], offerRawPayloads: true);
        var withheld = new ProviderRegistry([new StubAdapter(Manifest("items"))], offerRawPayloads: false);

        Assert.Equal(["items"], IncludeValues(offered));
        Assert.Equal(["items"], IncludeValues(withheld));

        // Same catalogue, so a consumer's ETag does not move for a provider
        // that lost nothing.
        Assert.Equal(offered.CatalogDigest, withheld.CatalogDigest);
    }

    /// <summary>
    /// A provider that DID offer it must move its digest, or a consumer holding
    /// a cached catalogue would go on showing a control the service has stopped
    /// accepting.
    /// </summary>
    [Fact]
    public void Withholding_raw_changes_the_catalogue_a_consumer_caches()
    {
        var offered = new ProviderRegistry([new StubAdapter(Manifest("items", "raw"))], offerRawPayloads: true);
        var withheld = new ProviderRegistry([new StubAdapter(Manifest("items", "raw"))], offerRawPayloads: false);

        Assert.NotEqual(offered.CatalogDigest, withheld.CatalogDigest);
    }

    private sealed class StubAdapter(ProviderManifest manifest) : IProviderAdapter
    {
        public ProviderManifest Describe() => manifest;

        public Task<LoginResult> LoginAsync(IJobContext ctx, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<FetchResult> FetchAsync(IJobContext ctx, ResourceRequest request, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
