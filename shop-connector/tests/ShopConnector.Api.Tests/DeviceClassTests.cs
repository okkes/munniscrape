using Connector.Kit.Adapters;
using Connector.Kit.Hosting;
using Connector.Kit.Hosting.Data;
using Connector.Kit.Hosting.Sessions;
using Connector.Kit.Security;
using Connector.Kit.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ShopConnector.Adapters.Mock;
using ShopConnector.Api.Tests.Infrastructure;

namespace ShopConnector.Api.Tests;

/// <summary>
/// api-spec §1.5 and §8.1: web custody.
///
/// A web client's bundle lives in <c>sessionStorage</c>, which is the weakest
/// place a credential can live - and is also the bound that actually holds,
/// because the bundle dies with the tab and a new tab has to sign in again.
/// So a web client gets the manifest's own lifetime, and a deployment that
/// wants a second deadline inside the tab's can configure one.
///
/// Asserted against the bundle's own contents rather than a field in the
/// response, which could be right while the ciphertext is wrong.
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

    private static readonly Dictionary<string, string> Credentials = new(StringComparer.Ordinal)
    {
        ["username"] = "shopper",
        ["password"] = "hunter2",
    };

    /// <summary>
    /// The exact lifetime, not a range.
    ///
    /// This asserted <c>InRange(1, 43_200)</c> under the name
    /// "capped_at_an_hour", so it passed at one hour, at twelve, and at
    /// anything between - which is to say it did not test the cap at all. A
    /// range is the right shape for a clock and the wrong shape for a policy:
    /// the number IS the policy here, and a test that accepts a whole day of
    /// them cannot fail when the policy changes underneath it.
    /// </summary>
    [Fact]
    public async Task A_web_client_gets_the_manifest_lifetime_because_the_tab_is_the_boundary()
    {
        using var http = factory.CreateAuthorizedClient();
        var connection = await Flows.ConnectAsync(
            http, Provider, Flows.NewSubject("web"), Credentials, deviceClass: "web");

        Assert.Equal(ManifestTtlSeconds, (int)LifetimeOf(connection).TotalSeconds);

        // The bound that is actually load-bearing: the bundle is ephemeral, so
        // it dies with the tab whatever its TTL says.
        var view = await Flows.ReadSessionAsync(http, Provider, connection.SessionId);
        Assert.Equal("ephemeral", view.Text("custody"));
    }

    /// <summary>
    /// A deployment that wants a second deadline inside the tab's own can still
    /// have one; it is the default that changed, not the mechanism.
    /// </summary>
    [Fact]
    public void A_configured_cap_still_shortens_a_web_bundle()
    {
        var options = new ConnectorOptions { Timeouts = { WebBundleMaxTtlSeconds = 3_600 } };
        var manifest = factory.Services.GetRequiredService<IProviderRegistry>().RequireManifest(Provider);

        // TtlSecondsFor reads the manifest and the options and nothing else, so
        // the collaborators it never reaches are not worth a fixture.
        var sessions = new SessionService(
            db: null!, registry: null!, codec: null!, tickets: null!, signals: null!,
            options: Options.Create(options), time: TimeProvider.System);

        Assert.Equal(3_600, sessions.TtlSecondsFor(manifest, DeviceClass.Web));
        Assert.Equal(ManifestTtlSeconds, sessions.TtlSecondsFor(manifest, DeviceClass.Native));
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
