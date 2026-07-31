using System.Net;
using System.Net.Http.Json;
using Connector.Kit.Hosting.Data;
using Connector.Kit.Jobs;
using Connector.Kit.Manifests;
using ShopConnector.Adapters.Mock;
using ShopConnector.Api.Tests.Infrastructure;

namespace ShopConnector.Api.Tests;

/// <summary>
/// What disconnecting reaches, and what it does not.
///
/// <c>DELETE /sessions/{id}</c> called <c>LogoutAsync</c> on every provider,
/// gated on nothing but session state - and could never have worked. Custody is
/// the user's device, so the control plane holds no credential; the logout job
/// was enqueued carrying none, and the two adapters that implement a logout
/// both failed silently on it. Most of the rest inherit a do-nothing default
/// and were costing a job row, a lease and an agent round trip to reach
/// <c>Task.CompletedTask</c>, while the consuming app told the user it had
/// "logged out upstream" every single time.
///
/// So there are two rules now, and both are asserted here: the manifest says
/// whether anything upstream happens at all, and the credential to make it
/// happen has to be handed back by whoever holds it. Neither may ever fail the
/// disconnect - the user asked to remove a connection, not to prove they can
/// still authenticate.
/// </summary>
[Collection(ShopApiCollection.Name)]
public sealed class DisconnectLogoutTests(ShopApiFactory factory)
{
    private static readonly Dictionary<string, string> MockCredentials = new(StringComparer.Ordinal)
    {
        ["username"] = "shopper",
        ["password"] = "hunter2",
    };

    private static readonly Dictionary<string, string> RotatingCredentials = new(StringComparer.Ordinal)
    {
        ["username"] = RotatingStoreAdapter.Username,
        ["password"] = RotatingStoreAdapter.Password,
    };

    // ---- the credential ----------------------------------------------------

    [Fact]
    public async Task A_declared_logout_given_the_bundle_gets_a_job_that_can_actually_use_it()
    {
        const string provider = RotatingStoreAdapter.ProviderId;

        using var http = factory.CreateAuthorizedClient();
        var connection = await Flows.ConnectAsync(
            http, provider, Flows.NewSubject("logout-armed"), RotatingCredentials);

        Assert.Equal(LogoutSupport.Session, await LogoutSupportOfAsync(http, provider));

        using var disconnect = await DeleteAsync(http, provider, connection.SessionId, connection.Bundle);
        Assert.Equal(HttpStatusCode.NoContent, disconnect.StatusCode);

        var logout = Assert.Single(LogoutJobs(connection.SessionId));

        // The whole point. This was null for every logout this platform has
        // ever run, so PicnicAdapter.LogoutAsync threw session_expired building
        // its session and swallowed it in its own best-effort catch.
        Assert.NotNull(logout.MaterialJson);

        // The token this connection actually holds, not merely "something".
        Assert.Contains(
            $"rot-access-0-{connection.SessionId}", logout.MaterialJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_declared_logout_with_no_bundle_offered_is_skipped_rather_than_queued_blind()
    {
        const string provider = RotatingStoreAdapter.ProviderId;

        using var http = factory.CreateAuthorizedClient();
        var connection = await Flows.ConnectAsync(
            http, provider, Flows.NewSubject("logout-unarmed"), RotatingCredentials);

        // The old shape of this call, and the reason nothing ever happened.
        using var disconnect = await http.DeleteAsync($"/v1/{provider}/sessions/{connection.SessionId}");

        Assert.Equal(HttpStatusCode.NoContent, disconnect.StatusCode);
        Assert.Empty(LogoutJobs(connection.SessionId));
    }

    /// <summary>
    /// A bundle that does not open is a logout that cannot happen - never a
    /// disconnect that may be refused.
    /// </summary>
    [Theory]
    [InlineData("not-a-bundle")]
    [InlineData("")]
    public async Task A_bundle_that_does_not_open_still_removes_the_connection(string bundle)
    {
        const string provider = RotatingStoreAdapter.ProviderId;

        using var http = factory.CreateAuthorizedClient();
        var connection = await Flows.ConnectAsync(
            http, provider, Flows.NewSubject("logout-garbage"), RotatingCredentials);

        using var disconnect = await DeleteAsync(http, provider, connection.SessionId, bundle);

        Assert.Equal(HttpStatusCode.NoContent, disconnect.StatusCode);
        Assert.Empty(LogoutJobs(connection.SessionId));
        Assert.Equal("disabled", await StateOfAsync(http, provider, connection.SessionId));
    }

    // ---- the declaration ---------------------------------------------------

    [Fact]
    public async Task A_provider_that_logs_out_nowhere_gets_no_job_even_holding_the_bundle()
    {
        const string provider = MockStoreAdapters.Simple;

        using var http = factory.CreateAuthorizedClient();
        var connection = await Flows.ConnectAsync(
            http, provider, Flows.NewSubject("logout-none"), MockCredentials);

        Assert.Equal(LogoutSupport.None, await LogoutSupportOfAsync(http, provider));

        using var disconnect = await DeleteAsync(http, provider, connection.SessionId, connection.Bundle);

        // The user's side is identical - this is not a degraded disconnect.
        Assert.Equal(HttpStatusCode.NoContent, disconnect.StatusCode);

        // And nothing was queued to tell a provider that has no way of being
        // told. This is the row, the lease and the agent round trip that used
        // to be spent reaching Task.CompletedTask.
        Assert.Empty(LogoutJobs(connection.SessionId));
    }

    // ---- the purge ---------------------------------------------------------

    /// <summary>
    /// The purge happens either way, and it happens AFTER the bundle is opened
    /// and BEFORE the job is queued. Both orderings are load-bearing: a purged
    /// session's bundle no longer opens, and the purge blanks the material on
    /// every job the session has - which silently disarmed the logout job when
    /// it was created first.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_local_purge_happens_whether_or_not_anything_is_told(bool declaresLogout)
    {
        var provider = declaresLogout ? RotatingStoreAdapter.ProviderId : MockStoreAdapters.Simple;
        var credentials = declaresLogout ? RotatingCredentials : MockCredentials;

        using var http = factory.CreateAuthorizedClient();
        var connection = await Flows.ConnectAsync(
            http, provider, Flows.NewSubject("logout-purge"), credentials);

        using (var disconnect = await DeleteAsync(http, provider, connection.SessionId, connection.Bundle))
        {
            Assert.Equal(HttpStatusCode.NoContent, disconnect.StatusCode);
        }

        // The login's own credentials are gone. They were already cleared when
        // it went terminal - the plaintext window is the run, not the row's
        // lifetime - and the purge is what guarantees it for anything left.
        var login = Db.Read(factory, db => db.Jobs
            .Single(j => j.SessionId == connection.SessionId && j.Kind == JobKind.Login));

        Assert.Null(login.InputsJson);
        Assert.Null(login.MaterialJson);

        // The session row survives on purpose: it is how a user is told what
        // happened to a connection.
        Assert.Equal("disabled", await StateOfAsync(http, provider, connection.SessionId));
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// A DELETE carrying the bundle, exactly as the consuming app sends it.
    /// Awaited inside, because disposing the request before the send completes
    /// takes its content with it.
    /// </summary>
    private static async Task<HttpResponseMessage> DeleteAsync(
        HttpClient http, string provider, string sessionId, string? bundle)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete, $"/v1/{provider}/sessions/{sessionId}")
        {
            Content = JsonContent.Create(new { bundle }),
        };

        return await http.SendAsync(request);
    }

    /// <summary>Read from the catalogue, so this asserts what a consumer is actually told.</summary>
    private static async Task<LogoutSupport> LogoutSupportOfAsync(HttpClient http, string provider)
    {
        using var response = await http.GetAsync($"/v1/providers/{provider}");
        var body = await response.JsonAsync();

        return body.Text("logout") switch
        {
            "session" => LogoutSupport.Session,
            "account" => LogoutSupport.Account,
            _ => LogoutSupport.None,
        };
    }

    private static async Task<string?> StateOfAsync(HttpClient http, string provider, string sessionId)
    {
        using var response = await http.GetAsync($"/v1/{provider}/login/{sessionId}");
        var body = await response.JsonAsync();
        return body.Text("state");
    }

    private List<JobRow> LogoutJobs(string sessionId) =>
        Db.Read(factory, db => db.Jobs
            .Where(j => j.SessionId == sessionId && j.Kind == JobKind.Logout)
            .ToList());
}
