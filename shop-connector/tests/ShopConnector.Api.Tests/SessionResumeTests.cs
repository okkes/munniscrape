using System.Net;
using ShopConnector.Adapters.Mock;
using ShopConnector.Api.Tests.Infrastructure;

namespace ShopConnector.Api.Tests;

/// <summary>
/// api-spec §2.3 and §8 step 4: a stored bundle becomes a short-lived ticket.
///
/// The replay defence is the point of the whole custody design. The bundle
/// lives on the user's device, so it is the one credential an attacker can
/// realistically obtain - and it has to be useless to anyone but the subject it
/// was minted for.
/// </summary>
[Collection(ShopApiCollection.Name)]
public sealed class SessionResumeTests(ShopApiFactory factory)
{
    private const string Provider = MockStoreAdapters.Simple;

    private static readonly Dictionary<string, string> Credentials = new(StringComparer.Ordinal)
    {
        ["username"] = "shopper",
        ["password"] = "hunter2",
    };

    [Fact]
    public async Task Resume_exchanges_a_bundle_for_a_ticket_bound_to_the_session()
    {
        using var http = factory.CreateAuthorizedClient();
        var connection = await Flows.ConnectAsync(http, Provider, Flows.NewSubject("resume"), Credentials);

        using var request = Wire.Post($"/v1/{Provider}/sessions/resume",
            new { Subject = connection.Subject, Bundle = connection.Bundle });

        using var response = await http.SendAsync(request);
        var body = await response.JsonAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("tkt_", body.Text("ticket"), StringComparison.Ordinal);
        Assert.Equal(connection.SessionId, body.Text("session_id"));
        Assert.Equal("active", body.Text("state"));

        // Short on purpose: a ticket is a bearer capability over one connection,
        // and it never leaves the consuming server.
        Assert.InRange(body.GetProperty("expires_in").GetInt32(), 1, 3600);
    }

    [Fact]
    public async Task A_bundle_presented_for_another_subject_is_session_expired()
    {
        using var http = factory.CreateAuthorizedClient();
        var connection = await Flows.ConnectAsync(http, Provider, Flows.NewSubject("replay-owner"), Credentials);

        using var request = Wire.Post($"/v1/{Provider}/sessions/resume",
            new { Subject = Flows.NewSubject("replay-thief"), Bundle = connection.Bundle });

        using var response = await http.SendAsync(request);

        // The subject is authenticated data on the ciphertext, so the bundle
        // does not decrypt at all. Every rejection reason - wrong subject, wrong
        // provider, stale manifest version, expired - is the same answer, so a
        // caller probing bundles learns nothing from the difference.
        await ErrorEnvelope.AssertAsync(response, HttpStatusCode.Unauthorized, "session_expired");
    }

    [Fact]
    public async Task A_bundle_presented_on_another_providers_route_is_session_expired()
    {
        using var http = factory.CreateAuthorizedClient();
        var connection = await Flows.ConnectAsync(http, Provider, Flows.NewSubject("cross-provider"), Credentials);

        using var request = Wire.Post($"/v1/{MockStoreAdapters.Sms}/sessions/resume",
            new { Subject = connection.Subject, Bundle = connection.Bundle });

        using var response = await http.SendAsync(request);
        await ErrorEnvelope.AssertAsync(response, HttpStatusCode.Unauthorized, "session_expired");
    }

    [Fact]
    public async Task A_bundle_that_is_not_a_bundle_is_session_expired()
    {
        using var http = factory.CreateAuthorizedClient();

        using var request = Wire.Post($"/v1/{Provider}/sessions/resume",
            new { Subject = Flows.NewSubject("garbage"), Bundle = "sb_v1.dev.aaaa.bbbb.cccc" });

        using var response = await http.SendAsync(request);
        await ErrorEnvelope.AssertAsync(response, HttpStatusCode.Unauthorized, "session_expired");
    }

    [Fact]
    public async Task A_disconnected_session_can_no_longer_be_resumed()
    {
        using var http = factory.CreateAuthorizedClient();
        var connection = await Flows.ConnectAsync(http, Provider, Flows.NewSubject("disconnect"), Credentials);

        using (var deleted = await http.DeleteAsync($"/v1/{Provider}/sessions/{connection.SessionId}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        using var request = Wire.Post($"/v1/{Provider}/sessions/resume",
            new { Subject = connection.Subject, Bundle = connection.Bundle });

        using var response = await http.SendAsync(request);
        await ErrorEnvelope.AssertAsync(response, HttpStatusCode.Unauthorized, "session_expired");
    }
}
