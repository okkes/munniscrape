using Connector.Kit.Adapters;

namespace ShopConnector.Adapters.Picnic;

/// <summary>
/// Picnic's construction point.
///
/// It lives here rather than in <c>ShopAdapters</c> so that adding a provider
/// touches one folder: the shared registry is the file every provider has to
/// pass through, and the file everyone edits at once is the file that breaks.
/// Registration is a single line in <c>ShopAdapters.Real</c>:
///
/// <code>new PicnicAdapter(settings.Picnic, time),</code>
///
/// with <c>public PicnicOptions Picnic { get; init; } = new();</c> added to
/// <c>ShopAdapterOptions</c>, so that every unconfirmed value - the token TTL,
/// the invalid-credential codes, the API version segment - is reachable from a
/// host's configuration section and correctable without a release.
/// </summary>
public static class PicnicAdapters
{
    public const string Id = PicnicAdapter.ProviderId;

    public static IProviderAdapter Create(PicnicOptions? options = null, TimeProvider? time = null) =>
        new PicnicAdapter(options, time);
}
