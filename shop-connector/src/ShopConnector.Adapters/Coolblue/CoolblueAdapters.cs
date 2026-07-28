using Connector.Kit.Adapters;

namespace ShopConnector.Adapters.Coolblue;

/// <summary>
/// The registration entry point for Coolblue.
///
/// It lives here rather than in <c>ShopAdapters</c> because that file is the
/// one shared surface several adapters are being added through at once, and a
/// factory in the provider's own folder is a merge nobody has to resolve.
///
/// To wire it in, <c>ShopAdapters.Real</c> gains one line:
///
/// <code>
///     CoolblueAdapters.Create(settings.Coolblue, time),
/// </code>
///
/// and <c>ShopAdapterOptions</c> gains the property it reads:
///
/// <code>
///     public CoolblueOptions Coolblue { get; init; } = new();
/// </code>
///
/// Two things follow from registering it, and both are deliberate:
///
/// * <c>ManifestTests.Registry_registers_every_shop_provider</c> asserts an
///   exact ordered list, and the registry sorts by id - so <c>"coolblue"</c>
///   belongs between <c>"ah"</c> and <c>"jumbo"</c> in that array.
/// * The provider will validate, describe itself honestly, and log in. It will
///   NOT return orders: <see cref="CoolblueFetchPlan"/> refuses until a human
///   has captured the endpoint. That is the intended shipped state, and the
///   manifest's notes key says so before a user connects.
/// </summary>
public static class CoolblueAdapters
{
    /// <summary>The single provider this folder registers.</summary>
    public static IProviderAdapter Create(CoolblueOptions? options = null, TimeProvider? time = null) =>
        new CoolblueAdapter(options, time);
}
