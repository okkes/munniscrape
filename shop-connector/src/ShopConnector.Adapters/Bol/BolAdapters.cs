using Connector.Kit.Adapters;

namespace ShopConnector.Adapters.Bol;

/// <summary>
/// bol.com's entry in the registry.
///
/// A factory in this folder rather than a line edited into
/// <c>ShopAdapters.Real</c> directly: several providers were being added to
/// this assembly at once, and that one file is where they would all have
/// collided. Wiring it up is a single line there -
///
/// <code>
///     BolAdapters.Create(time: time),
/// </code>
///
/// - and, once <c>ShopAdapterOptions</c> can be edited safely, the operator
/// -facing form that lets every unconfirmed value in <see cref="BolOptions"/>
/// be corrected from configuration instead of a release:
///
/// <code>
///     BolAdapters.Create(settings.Bol, time),        // with: public BolOptions Bol { get; init; } = new();
/// </code>
/// </summary>
public static class BolAdapters
{
    public static IProviderAdapter Create(BolOptions? options = null, TimeProvider? time = null) =>
        new BolAdapter(options, time);
}
