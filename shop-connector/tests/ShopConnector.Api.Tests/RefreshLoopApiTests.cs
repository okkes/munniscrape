using System.Globalization;
using System.Net;
using System.Text.Json;
using Connector.Kit.Jobs;
using ShopConnector.Adapters.Mock;
using ShopConnector.Api.Tests.Infrastructure;

namespace ShopConnector.Api.Tests;

/// <summary>
/// api-spec §8 steps 3-5, run round and round: the loop the whole custody
/// design exists for.
///
/// The connector returns access + refresh to the CLIENT to store. Next time
/// the client wants receipts it provides them back, the connector refreshes
/// silently, hands over a ROTATED bundle to persist, and nobody signs in
/// again. Every part of that is tested elsewhere; what is tested here is that
/// the parts join up - that the bundle coming OUT of one fetch is the bundle
/// that goes INTO the next, over and over, with no human in the middle.
///
/// The wire can only show so much: bundles are ciphertext, so no test here can
/// look at a token. What it can show is that the loop never breaks and never
/// asks. The refresh semantics underneath - proactive refresh on expiry,
/// refresh-and-retry on 401, the refresh token carried forward when a provider
/// re-issues none, and a dead grant giving up after exactly one attempt - are
/// proved against a real OAuth-shaped upstream in
/// <c>ShopConnector.Adapters.Tests.RefreshLoopTests</c>.
/// </summary>
[Collection(ShopApiCollection.Name)]
public sealed class RefreshLoopApiTests(ShopApiFactory factory)
{
    private static readonly Dictionary<string, string> RotatingCredentials = new(StringComparer.Ordinal)
    {
        ["username"] = RotatingStoreAdapter.Username,
        ["password"] = RotatingStoreAdapter.Password,
    };

    private static readonly Dictionary<string, string> SimpleCredentials = new(StringComparer.Ordinal)
    {
        ["username"] = "shopper",
        ["password"] = "hunter2",
    };

    /// <summary>The shipped mock fixture is anchored to July 2026.</summary>
    private const string FixtureWindow = "2026-06-01";

    /// <summary>
    /// How long a straggling progress report gets to land. Generous on
    /// purpose: the point is to stop measuring the scheduler, not to measure
    /// it more precisely.
    /// </summary>
    private static readonly TimeSpan ProgressBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Waits for an asynchronous progress report to reach ANY of this
    /// connection's jobs.
    ///
    /// Deliberately not "the latest job". <c>Db.LatestJob</c> orders by
    /// <c>CreatedAt</c> and breaks ties on <c>Id</c>, and an id is random hex -
    /// so when this connection's login and its two fetches land inside one
    /// timestamp tick, which row is "latest" is decided by a coin toss, and on
    /// the tosses that named the LOGIN job this assertion read an empty
    /// steps_done and failed. The claim being made was never about one
    /// particular row anyway: it is that typed progress survives the guarded
    /// update at all.
    ///
    /// Fails with every row it saw, so a real regression - the guarded update
    /// discarding every report - still reads as a failure and not as a timeout
    /// of unknown cause.
    /// </summary>
    private async Task AssertProgressRecordedAsync(string sessionId, string step)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        List<string> seen = [];

        while (clock.Elapsed < ProgressBudget)
        {
            seen = Db.Read(factory, db => db.Jobs
                .Where(j => j.SessionId == sessionId)
                .Select(j => j.StepsDoneJson)
                .ToList());

            if (seen.Exists(s => s.Contains(step, StringComparison.OrdinalIgnoreCase))) return;
            await Task.Delay(100);
        }

        Assert.Fail(
            $"no '{step}' in any steps_done for {sessionId} after {ProgressBudget.TotalSeconds:0}s; " +
            $"saw [{string.Join(", ", seen)}]. All empty means ProgressAsync is discarding every report.");
    }

    // ── the owner's sentence, on the wire ────────────────────────────────

    [Fact]
    public async Task The_rotated_bundle_serves_the_next_fetch_and_the_connector_keeps_no_copy_of_it()
    {
        const string provider = RotatingStoreAdapter.ProviderId;

        using var http = factory.CreateAuthorizedClient();
        var connection = await Flows.ConnectAsync(http, provider, Flows.NewSubject("loop-once"), RotatingCredentials);

        // Fetch one: the bundle the login handed over is enough, and the
        // provider rotated underneath, so a new one comes back with the data.
        var first = await FetchAsync(http, provider, connection);
        Assert.NotEmpty(first.GetProperty("data").EnumerateArray());

        var rotated = RotatedBundleOf(first);
        Assert.NotEqual(connection.Bundle, rotated);

        // Handed over exactly once. The connector does not keep a copy of a
        // credential it has already delivered - a bundle still sitting in the
        // control plane after the caller has it is a secret at rest with no
        // reason to exist.
        Assert.Null(PendingBundle(connection.SessionId));

        // THE SENTENCE: "next time when the client wants the receipts, the
        // client can provide these things". It provides the rotated bundle,
        // and that is the whole of what it has to do.
        var second = await FetchAsync(http, provider, connection with { Bundle = rotated });
        Assert.NotEmpty(second.GetProperty("data").EnumerateArray());

        var again = RotatedBundleOf(second);
        Assert.NotEqual(rotated, again);
        Assert.NotEqual(connection.Bundle, again);

        // Typed progress survived. It is only a courtesy field, but it is
        // written by the same guarded statement that stops a straggling report
        // resurrecting a finished job, so an empty one here would mean that
        // guard was throwing every report away.
        //
        // Polled rather than read once. Progress is reported ASYNCHRONOUSLY -
        // the inline runner drains a channel - which is the whole reason
        // ProgressAsync had to become a guarded conditional update in the first
        // place. So a report is routinely still in flight when the fetch that
        // produced it has already returned, and reading the column the instant
        // the response lands is a race this test would lose only on a busy
        // machine. It did.
        await AssertProgressRecordedAsync(connection.SessionId, "downloading");

        // Not one of those fetches went near a human. One login job for the
        // life of this connection, and the session never left `active`.
        Assert.Equal(1, LoginJobs(connection.SessionId));
        Assert.Equal("active", (await Flows.ReadSessionAsync(http, provider, connection.SessionId)).Text("state"));
    }

    // ── a provider that rotated nothing ──────────────────────────────────

    [Fact]
    public async Task A_fetch_that_rotated_nothing_returns_no_session_block_and_the_stored_bundle_stays_current()
    {
        const string provider = MockStoreAdapters.Simple;

        using var http = factory.CreateAuthorizedClient();
        var connection = await Flows.ConnectAsync(http, provider, Flows.NewSubject("loop-static"), SimpleCredentials);

        var first = await FetchAsync(http, provider, connection, FixtureWindow);

        // No session block at all. `rotated: false` would be a different and
        // worse contract: the caller would have to decide whether to persist
        // it, and the honest answer is that there is nothing to persist.
        Assert.False(first.TryGetProperty("session", out _), "an unrotated fetch must not re-issue a bundle");
        Assert.Null(PendingBundle(connection.SessionId));

        // And the bundle the caller is still holding has not been quietly
        // invalidated by the fetch it just served.
        var second = await FetchAsync(http, provider, connection, FixtureWindow);

        Assert.NotEmpty(second.GetProperty("data").EnumerateArray());
        Assert.False(second.TryGetProperty("session", out _));
        Assert.Equal(1, LoginJobs(connection.SessionId));
    }

    // ── the end of the road ──────────────────────────────────────────────

    /// <summary>
    /// The end of the road. Every way a session can stop being usable - the
    /// refresh grant revoked upstream, the session disconnected, the bundle
    /// aged out, the manifest moved under it - arrives at the caller as the
    /// same <c>session_expired</c> with <c>user_action: reauth</c>, because
    /// there is exactly one thing the user can do about any of them and a
    /// caller probing bundles must learn nothing from the difference.
    ///
    /// Disconnect is the wire-reachable way to reach that state offline; the
    /// dead refresh grant itself is driven against a real token endpoint in
    /// the adapter suite. What matters here is what the platform does with it:
    /// answer once, and enqueue nothing.
    /// </summary>
    [Fact]
    public async Task A_session_that_can_no_longer_be_refreshed_answers_reauth_once_and_queues_no_work()
    {
        const string provider = RotatingStoreAdapter.ProviderId;

        using var http = factory.CreateAuthorizedClient();
        var connection = await Flows.ConnectAsync(http, provider, Flows.NewSubject("loop-dead"), RotatingCredentials);

        using (var disconnect = await http.DeleteAsync($"/v1/{provider}/sessions/{connection.SessionId}"))
        {
            Assert.Equal(HttpStatusCode.NoContent, disconnect.StatusCode);
        }

        var jobsBefore = AllJobs(connection.SessionId);

        // Three attempts, because a client whose sync failed retries - and a
        // dead session must cost the provider nothing every time it does.
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            using var resume = Wire.Post($"/v1/{provider}/sessions/resume",
                new { subject = connection.Subject, bundle = connection.Bundle });

            using var response = await http.SendAsync(resume);
            var error = await ErrorEnvelope.AssertAsync(response, HttpStatusCode.Unauthorized, "session_expired");

            // The single instruction the consumer can act on, and the flag
            // that stops anything downstream from retrying on its own.
            Assert.Equal("reauth", error.Text("user_action"));
            Assert.False(error.GetProperty("retriable").GetBoolean());
        }

        // The one-shot form skips the ticket and lands in the same place.
        using (var oneShot = Wire.Post($"/v1/{provider}/{RotatingStoreAdapter.ReceiptsResource}:fetch", new
        {
            subject = connection.Subject,
            bundle = connection.Bundle,
            @params = new Dictionary<string, object>(StringComparer.Ordinal) { ["since"] = FixtureWindow },
        }))
        {
            using var response = await http.SendAsync(oneShot);
            await ErrorEnvelope.AssertAsync(response, HttpStatusCode.Unauthorized, "session_expired");
        }

        // No retry storm: four rejections, and not one of them created a job.
        // A dead session that queued work would send an adapter upstream with
        // a credential the provider has already refused - which is how a
        // client stops being merely broken and starts looking hostile.
        Assert.Equal(jobsBefore, AllJobs(connection.SessionId));
    }

    // ── a month of it ────────────────────────────────────────────────────

    /// <summary>
    /// Thirty syncs, one sign-in. Each pass persists what it was handed and
    /// presents it next time, exactly as a device would, so a single dropped
    /// or replayed bundle breaks the chain and fails the test.
    /// </summary>
    [Fact]
    public async Task A_month_of_fetches_never_asks_the_human_to_sign_in_again()
    {
        const string provider = RotatingStoreAdapter.ProviderId;

        using var http = factory.CreateAuthorizedClient();
        var connection = await Flows.ConnectAsync(http, provider, Flows.NewSubject("loop-month"), RotatingCredentials);

        var seen = new HashSet<string>(StringComparer.Ordinal) { connection.Bundle };

        for (var day = 1; day <= 30; day++)
        {
            var page = await FetchAsync(http, provider, connection);

            Assert.NotEmpty(page.GetProperty("data").EnumerateArray());

            // Nothing from the previous pass is still holding the connection.
            // Per-session concurrency is 1, so one job left stuck in a
            // non-terminal state would not fail this loop - it would silently
            // stop it, with every later fetch queued and never leased. That is
            // how the progress/completion race in EfLeasedJobQueue was found.
            Assert.Equal(0, UnfinishedJobs(connection.SessionId));

            // No challenge ever reaches the caller on a refresh path: a fetch
            // that asked a question would be a fetch the user has to be
            // present for, which is precisely what holding a refresh token
            // buys them out of.
            Assert.False(page.TryGetProperty("challenge", out _), $"day {day} stopped to ask a question");

            var rotated = RotatedBundleOf(page);
            Assert.True(seen.Add(rotated), $"day {day} re-issued a bundle the caller had already seen");

            connection = connection with { Bundle = rotated };
        }

        // One login, thirty fetches, and the session is still simply active.
        // `needs_reauth` here would mean the loop had quietly asked the human
        // to start again somewhere in the middle.
        Assert.Equal(1, LoginJobs(connection.SessionId));

        var view = await Flows.ReadSessionAsync(http, provider, connection.SessionId);
        Assert.Equal("active", view.Text("state"));

        // Each rotation re-seals the session, so the connection's own deadline
        // keeps moving out ahead of the fetches. A session that expired on the
        // manifest TTL measured from the FIRST login would force a re-login on
        // a user who had been syncing daily all along.
        Assert.True(
            view.GetProperty("expires_at").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddDays(29),
            "a month of successful syncs must not leave the connection about to expire");
    }

    // ── the client, as a device would behave ─────────────────────────────

    /// <summary>
    /// One sync: exchange the stored bundle for a ticket, ask for the data.
    /// Returns the page whichever way the platform chose to answer it, since a
    /// loaded machine legitimately answers 202 and a poll.
    /// </summary>
    private static async Task<JsonElement> FetchAsync(
        HttpClient http, string provider, Connection connection, string? since = null)
    {
        var ticket = await Flows.ResumeAsync(http, provider, connection);

        var window = since ?? DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return await Flows.FetchPageAsync(http, provider, $"/v1/{provider}/receipts?since={window}", ticket);
    }

    /// <summary>The bundle a caller is told to persist, with the contract around it asserted.</summary>
    private static string RotatedBundleOf(JsonElement page)
    {
        Assert.True(page.TryGetProperty("session", out var session), "a rotated fetch must re-issue a bundle");
        Assert.True(session.GetProperty("rotated").GetBoolean(), "rotated must be true when a bundle is re-issued");

        var bundle = session.Text("bundle");
        Assert.StartsWith("sb_v1.", bundle, StringComparison.Ordinal);
        return bundle;
    }

    private string? PendingBundle(string sessionId) =>
        Db.Read(factory, db => db.Sessions.Where(s => s.Id == sessionId).Select(s => s.PendingBundle).Single());

    private int LoginJobs(string sessionId) =>
        Db.Read(factory, db => db.Jobs.Count(j => j.SessionId == sessionId && j.Kind == JobKind.Login));

    private int AllJobs(string sessionId) =>
        Db.Read(factory, db => db.Jobs.Count(j => j.SessionId == sessionId));

    /// <summary>
    /// Jobs that are neither queued nor terminal. One of these left behind by
    /// a finished run blocks the session's next job for ever, and blocks it
    /// silently - there is no wire surface for "your sync is wedged", which is
    /// exactly why this is asserted from the tables.
    /// </summary>
    private int UnfinishedJobs(string sessionId) =>
        Db.Read(factory, db => db.Jobs.Count(j => j.SessionId == sessionId
                                                  && (j.State == JobState.Leased
                                                      || j.State == JobState.Running
                                                      || j.State == JobState.AwaitingInput)));
}
