using System.Net;
using System.Text.Json;
using ShopConnector.Adapters.Mock;
using ShopConnector.Api.Tests.Infrastructure;

namespace ShopConnector.Api.Tests;

/// <summary>
/// api-spec §2.6: the kill switch.
///
/// Pausing a provider has to stop work for every user of it, and it has to say
/// so honestly - "connections are paused, we're fixing it" instead of a mystery
/// spinner. The check runs before any adapter does, which is the point: a
/// paused provider is one we have decided not to talk to.
/// </summary>
[Collection(ShopApiCollection.Name)]
public sealed class ProviderStatusTests(ShopApiFactory factory)
{
    /// <summary>
    /// The broken mock, chosen because nothing else in the suite connects to it -
    /// pausing a provider is global state, and a shared host means a test that
    /// borrowed a busy provider would break its neighbours.
    /// </summary>
    private const string Provider = MockStoreAdapters.Broken;

    private const string ReasonKey = "connect.provider.paused_for_maintenance";

    private static readonly Dictionary<string, string> Credentials = new(StringComparer.Ordinal)
    {
        ["username"] = "shopper",
        ["password"] = "hunter2",
    };

    [Fact]
    public async Task A_paused_provider_refuses_work_and_says_so_in_the_catalogue()
    {
        using var http = factory.CreateAuthorizedClient();

        // Connect while the provider is healthy: this mock only breaks on fetch,
        // so the ticket below is a real one and the refusal that follows is the
        // kill switch rather than the adapter.
        var connection = await Flows.ConnectAsync(http, Provider, Flows.NewSubject("paused"), Credentials);
        var ticket = await Flows.ResumeAsync(http, Provider, connection);

        try
        {
            using (var paused = await SetStatusAsync(http, "paused", ReasonKey))
            {
                Assert.Equal(HttpStatusCode.OK, paused.StatusCode);
                var status = await paused.JsonAsync();
                Assert.Equal("paused", status.Text("state"));
                Assert.Equal(ReasonKey, status.Text("reason_key"));
            }

            // A consumer reading the catalogue can now degrade honestly, with a
            // key it owns the copy for rather than prose we invented.
            var catalogued = await StatusInCatalogueAsync(http);
            Assert.Equal("paused", catalogued.Text("state"));
            Assert.Equal(ReasonKey, catalogued.Text("reason_key"));

            // An existing ticket is not a way past the switch.
            using (var fetch = await Flows.FetchAsync(http, $"/v1/{Provider}/receipts?since=2026-06-01", ticket))
            {
                // provider_unavailable, not provider_changed: the adapter never ran.
                await ErrorEnvelope.AssertAsync(fetch, HttpStatusCode.ServiceUnavailable, "provider_unavailable");
            }

            // Neither is a fresh connection.
            using var request = Wire.Post($"/v1/{Provider}/login",
                new { Subject = Flows.NewSubject("paused-login"), Inputs = Credentials });

            using var login = await http.SendAsync(request);
            await ErrorEnvelope.AssertAsync(login, HttpStatusCode.ServiceUnavailable, "provider_unavailable");
        }
        finally
        {
            using var restored = await SetStatusAsync(http, "healthy", reasonKey: null);
            Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        }

        // And once the switch is off, the same ticket works again.
        using var resumed = await Flows.FetchAsync(http, $"/v1/{Provider}/receipts?since=2026-06-01", ticket);

        // This provider always reports a shape change on fetch - which is the
        // point: the run reached the adapter this time.
        await ErrorEnvelope.AssertAsync(resumed, HttpStatusCode.BadGateway, "provider_changed");
    }

    private static async Task<HttpResponseMessage> SetStatusAsync(HttpClient http, string state, string? reasonKey)
    {
        using var request = Wire.Post($"/v1/admin/providers/{Provider}/status",
            new { State = state, ReasonKey = reasonKey });

        return await http.SendAsync(request);
    }

    private static async Task<JsonElement> StatusInCatalogueAsync(HttpClient http)
    {
        using var response = await http.GetAsync($"/v1/providers/{Provider}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("status");
    }
}
