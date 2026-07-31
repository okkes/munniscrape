using System.Net;
using Connector.Kit.Jobs;
using Connector.Kit.Manifests;
using ShopConnector.Adapters.Mock;
using ShopConnector.Api.Tests.Infrastructure;

namespace ShopConnector.Api.Tests;

/// <summary>
/// What disconnecting reaches, and what it does not.
///
/// <c>DELETE /sessions/{id}</c> called <c>LogoutAsync</c> on every provider,
/// gated on nothing but session state. Fourteen of the sixteen adapters
/// inherit the interface's do-nothing default, so each of those Disconnects
/// minted a job row, took a lease and spent an agent round trip to reach a
/// method that returns a completed task - while the consuming app told the
/// user, unconditionally, that it had "logged out upstream".
///
/// The manifest now says which it is, and these assert both halves. The local
/// purge is unconditional either way: a user disconnecting must always succeed
/// here, whatever the provider does or does not do about it.
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

    [Fact]
    public async Task A_provider_that_logs_out_upstream_gets_a_logout_job()
    {
        const string provider = RotatingStoreAdapter.ProviderId;

        using var http = factory.CreateAuthorizedClient();
        var connection = await Flows.ConnectAsync(
            http, provider, Flows.NewSubject("logout-declared"), RotatingCredentials);

        Assert.Equal(LogoutSupport.Session, await LogoutSupportOfAsync(http, provider));

        using var disconnect = await http.DeleteAsync($"/v1/{provider}/sessions/{connection.SessionId}");
        Assert.Equal(HttpStatusCode.NoContent, disconnect.StatusCode);

        // Exactly one, and it is the logout. The login job is the other row on
        // this session, so a count alone would not tell them apart.
        Assert.Equal(1, LogoutJobs(connection.SessionId));
    }

    [Fact]
    public async Task A_provider_that_logs_out_nowhere_gets_no_job_at_all()
    {
        const string provider = MockStoreAdapters.Simple;

        using var http = factory.CreateAuthorizedClient();
        var connection = await Flows.ConnectAsync(
            http, provider, Flows.NewSubject("logout-none"), MockCredentials);

        Assert.Equal(LogoutSupport.None, await LogoutSupportOfAsync(http, provider));

        using var disconnect = await http.DeleteAsync($"/v1/{provider}/sessions/{connection.SessionId}");

        // The user's side is identical - this is not a degraded disconnect.
        Assert.Equal(HttpStatusCode.NoContent, disconnect.StatusCode);

        // And nothing was queued to tell a provider that has no way of being
        // told. This is the row, the lease and the agent round trip that used
        // to be spent reaching Task.CompletedTask.
        Assert.Equal(0, LogoutJobs(connection.SessionId));
    }

    /// <summary>
    /// The purge happens either way. A provider having nothing to say upstream
    /// must never leave a connection half-removed here - the session is
    /// disabled and every credential the control plane touched is wiped.
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

        using (var disconnect = await http.DeleteAsync($"/v1/{provider}/sessions/{connection.SessionId}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, disconnect.StatusCode);
        }

        // The row survives on purpose - it is how a user is told what happened
        // to a connection - but it holds nothing usable any more. The login's
        // own inputs were already gone: a job going terminal clears them, so
        // the plaintext window is the run itself and not the row's lifetime.
        var wiped = Db.Read(factory, db => db.Jobs
            .Where(j => j.SessionId == connection.SessionId)
            .Select(j => new { j.InputsJson, j.MaterialJson })
            .ToList());

        Assert.All(wiped, row =>
        {
            Assert.Null(row.InputsJson);
            Assert.Null(row.MaterialJson);
        });

        using var poll = await http.GetAsync($"/v1/{provider}/login/{connection.SessionId}");
        var body = await poll.JsonAsync();
        Assert.Equal("disabled", body.Text("state"));
    }

    /// <summary>
    /// Why nothing in the shipped fleet declares a logout, recorded as a test
    /// rather than as a note somebody has to find.
    ///
    /// <c>DELETE /sessions/{id}</c> enqueues the logout job with no material -
    /// there is nowhere to get it, because every provider here is
    /// <see cref="SecretCustody.Client"/> and the token lives in the sealed
    /// bundle on the user's own device. Picnic's <c>LogoutAsync</c> therefore
    /// throws <c>session_expired</c> building its session and swallows it in
    /// its own best-effort catch; Amazon's returns at <c>!ctx.Browser.Started</c>
    /// on a context that has just been created. Both are silent.
    ///
    /// The consuming app already POSTs its bundle on Disconnect and the
    /// endpoint ignores it, so the missing half is small - but opening a
    /// credential in order to spend it is a decision, not a tidy-up.
    /// </summary>
    [Fact]
    public async Task A_logout_job_is_handed_no_credential_to_log_out_with()
    {
        const string provider = RotatingStoreAdapter.ProviderId;

        using var http = factory.CreateAuthorizedClient();
        var connection = await Flows.ConnectAsync(
            http, provider, Flows.NewSubject("logout-material"), RotatingCredentials);

        using var disconnect = await http.DeleteAsync($"/v1/{provider}/sessions/{connection.SessionId}");
        Assert.Equal(HttpStatusCode.NoContent, disconnect.StatusCode);

        var logout = Db.Read(factory, db => db.Jobs
            .Single(j => j.SessionId == connection.SessionId && j.Kind == JobKind.Logout));

        Assert.Null(logout.MaterialJson);
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

    private int LogoutJobs(string sessionId) =>
        Db.Read(factory, db => db.Jobs.Count(j => j.SessionId == sessionId && j.Kind == JobKind.Logout));
}
