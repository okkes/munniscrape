using Connector.Kit.Adapters;
using ShopConnector.Adapters.AlbertHeijn;
using ShopConnector.Adapters.Amazon;
using ShopConnector.Adapters.Bol;
using ShopConnector.Adapters.Coolblue;
using ShopConnector.Adapters.Jumbo;
using ShopConnector.Adapters.LidlPlus;
using ShopConnector.Adapters.MagentoGuest;
using ShopConnector.Adapters.Mock;
using ShopConnector.Adapters.Picnic;
using ShopConnector.Adapters.WooGuest;

namespace ShopConnector.Adapters;

/// <summary>
/// Everything the shopping connector knows how to talk to, bound in one
/// place from one settings object.
///
/// Providers are code, not rows - the registry is built at startup from this
/// and only a provider's health is ever state. A host constructs
/// <see cref="ProviderRegistry"/> over <see cref="All"/>, which validates
/// every manifest and refuses to boot on a bad one.
/// </summary>
public static class ShopAdapters
{
    /// <summary>
    /// Real providers plus the mock set. The mocks are registered in every
    /// environment on purpose: they are the only providers that can be
    /// exercised end to end without an account, and an environment that
    /// cannot demonstrate its own protocol is one nobody can debug.
    /// </summary>
    public static IReadOnlyList<IProviderAdapter> All(ShopAdapterOptions? options = null, TimeProvider? time = null) =>
        [.. Real(options, time), .. MockStoreAdapters.All(options?.Mock, time)];

    public static IReadOnlyList<IProviderAdapter> Real(
        ShopAdapterOptions? options = null, TimeProvider? time = null)
    {
        var settings = options ?? new ShopAdapterOptions();

        return
        [
            // Groceries. AH and Lidl drive a browser once; Jumbo's session is a
            // ~24h cookie, so it drives one whenever it has gone stale.
            new AlbertHeijnAdapter(settings.Ah, time),
            new LidlPlusAdapter(settings.Lidl, time),
            new JumboAdapter(settings.Jumbo, graphql: null, time),
            PicnicAdapters.Create(settings.Picnic, time),

            // General retail.
            BolAdapters.Create(settings.Bol, time),
            CoolblueAdapters.Create(settings.Coolblue, time),
            AmazonAdapters.Create(settings.Amazon, time),

            // Platform adapters, not retailers: one implementation serves any
            // shop running that platform, and neither needs a login at all -
            // an order number and an e-mail is the whole credential. The shop's
            // base URL is auth.config, which is why there is no host here.
            MagentoGuestAdapters.Create(settings.MagentoGuest, time),
            WooGuestAdapters.Create(settings.WooGuest, time),
        ];
    }
}

/// <summary>
/// The binding target for a host's configuration section. Every provider's
/// unconfirmed values - endpoints, client ids, selectors, money units - are
/// reachable from here, so correcting one after a provider changes is a
/// deploy-time edit rather than a release.
/// </summary>
public sealed record ShopAdapterOptions
{
    public AlbertHeijnOptions Ah { get; init; } = new();

    public LidlPlusOptions Lidl { get; init; } = new();

    public JumboOptions Jumbo { get; init; } = new();

    public PicnicOptions Picnic { get; init; } = new();

    public BolOptions Bol { get; init; } = new();

    public CoolblueOptions Coolblue { get; init; } = new();

    public AmazonOptions Amazon { get; init; } = new();

    public MagentoGuestOptions MagentoGuest { get; init; } = new();

    public WooGuestOptions WooGuest { get; init; } = new();

    public MockStoreOptions Mock { get; init; } = new();
}
