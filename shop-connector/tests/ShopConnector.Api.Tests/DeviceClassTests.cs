using Connector.Kit.Adapters;
using Connector.Kit.Security;
using Microsoft.Extensions.DependencyInjection;
using ShopConnector.Adapters.Mock;
using ShopConnector.Api.Tests.Infrastructure;

namespace ShopConnector.Api.Tests;

/// <summary>
/// api-spec §1.5 and §8.1: the whole web-custody policy in one number.
///
/// A web client's bundle lives in a browser tab, which is the weakest place a
/// credential can live, so its lifetime is capped at an hour regardless of what
/// the provider's manifest promises. This is the reason web is supportable at
/// all, so it is asserted against the bundle's own contents rather than against
/// a field in the response that could be right while the ciphertext is wrong.
/// </summary>
[Collection(ShopApiCollection.Name)]
public sealed class DeviceClassTests(ShopApiFactory factory)
{
    private const string Provider = MockStoreAdapters.Simple;

    /// <summary>The manifest's own TTL: 30 days.</summary>
    private const int ManifestTtlSeconds = 2_592_000;

    /// <summary>
    /// Must track ConnectorOptions.WebBundleMaxTtlSeconds. It caps the sealed
    /// WRAPPER, never the credential inside: a refreshable provider's token
    /// outlives this by weeks, so a shorter cap only forces a sign-in that
    /// re-seals the same token.
    /// </summary>
    private const int WebCapSeconds = 43_200;

    private static readonly Dictionary<string, string> Credentials = new(StringComparer.Ordinal)
    {
        ["username"] = "shopper",
        ["password"] = "hunter2",
    };

    [Fact]
    public async Task A_web_client_gets_a_bundle_capped_at_an_hour()
    {
        using var http = factory.CreateAuthorizedClient();
        var connection = await Flows.ConnectAsync(
            http, Provider, Flows.NewSubject("web"), Credentials, deviceClass: "web");

        var life = LifetimeOf(connection);
        Assert.InRange(life.TotalSeconds, 1, WebCapSeconds);

        var view = await Flows.ReadSessionAsync(http, Provider, connection.SessionId);
        Assert.Equal("ephemeral", view.Text("custody"));
    }

    [Fact]
    public async Task A_native_client_gets_the_full_manifest_lifetime()
    {
        using var http = factory.CreateAuthorizedClient();
        var connection = await Flows.ConnectAsync(
            http, Provider, Flows.NewSubject("native"), Credentials, deviceClass: "native");

        // The manifest promises 30 days and the provider is not web, so the cap
        // never applies. If it did, a native user would silently re-authenticate
        // every hour and nobody would know why.
        Assert.Equal(ManifestTtlSeconds, (int)LifetimeOf(connection).TotalSeconds);

        var view = await Flows.ReadSessionAsync(http, Provider, connection.SessionId);
        Assert.Null(view.TextOrNull("custody"));
    }

    [Fact]
    public async Task An_absent_device_class_header_is_treated_as_native()
    {
        using var http = factory.CreateAuthorizedClient();
        var connection = await Flows.ConnectAsync(http, Provider, Flows.NewSubject("default-device"), Credentials);

        // The historical default, and the safe one: the web rules only ever
        // shorten a bundle's life, so guessing web would break native clients
        // while guessing native breaks nothing.
        Assert.Equal(ManifestTtlSeconds, (int)LifetimeOf(connection).TotalSeconds);
    }

    /// <summary>
    /// Opens the bundle with the service's own key ring and measures what the
    /// ciphertext actually says. The response's <c>expires_at</c> is a claim;
    /// this is the fact.
    /// </summary>
    private TimeSpan LifetimeOf(Connection connection)
    {
        var codec = factory.Services.GetRequiredService<SealedBundleCodec>();
        var manifest = factory.Services.GetRequiredService<IProviderRegistry>().RequireManifest(Provider);

        var payload = codec.Open(
            connection.Bundle,
            new BundleBinding(Provider, connection.Subject, manifest.ManifestVersion));

        return payload.ExpiresAt - payload.IssuedAt;
    }
}
