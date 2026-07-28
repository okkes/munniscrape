using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Connector.Kit.Hosting.Endpoints;

namespace ShopConnector.Api.Tests.Infrastructure;

/// <summary>What a caller holds after a successful connect: the spec's step 3 result.</summary>
internal sealed record Connection(string Subject, string SessionId, string Bundle);

/// <summary>
/// The worked example from api-spec §8, as reusable steps. Every test that
/// needs data starts from a real login rather than a seeded row, because a
/// seeded session would skip the two things most likely to break: the bundle
/// and the subject binding.
/// </summary>
internal static class Flows
{
    private static readonly TimeSpan SettleBudget = TimeSpan.FromSeconds(45);

    /// <summary>A subject unique to the caller, so tests never share a session by accident.</summary>
    public static string NewSubject(string label) => $"u_{label}_{Guid.NewGuid():N}";

    /// <summary>
    /// Login through to a bundle, for a provider that raises no challenge.
    /// Tolerates both outcomes: an inline provider usually finishes inside the
    /// login request, but the caller is entitled to a 202 and a poll.
    /// </summary>
    public static async Task<Connection> ConnectAsync(
        HttpClient http,
        string provider,
        string subject,
        IReadOnlyDictionary<string, string> inputs,
        string? deviceClass = null)
    {
        var (sessionId, bundle) = await StartLoginAsync(http, provider, subject, inputs, deviceClass);
        bundle ??= await AwaitBundleAsync(http, provider, sessionId);
        return new Connection(subject, sessionId, bundle);
    }

    /// <summary>
    /// Posts the login and returns the session plus whatever bundle the response
    /// already carried. Split out so a challenge test can drive the middle of
    /// the flow itself.
    /// </summary>
    public static async Task<(string SessionId, string? Bundle)> StartLoginAsync(
        HttpClient http,
        string provider,
        string subject,
        IReadOnlyDictionary<string, string> inputs,
        string? deviceClass = null)
    {
        ArgumentNullException.ThrowIfNull(http);

        using var request = Wire.Post($"/v1/{provider}/login", new { subject, inputs });
        if (deviceClass is not null) request.AddHeader(RequestContext.DeviceClassHeader, deviceClass);

        using var response = await http.SendAsync(request);
        var body = await response.JsonAsync();

        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted,
            $"login for {provider} answered {(int)response.StatusCode}: {body}");

        return (body.Text("session_id"), body.TextOrNull("bundle"));
    }

    /// <summary>
    /// Polls the login until the connector hands over a bundle.
    ///
    /// It takes the first bundle it sees rather than waiting for
    /// <c>state == active</c>: the bundle is delivered exactly once and cleared
    /// in the same breath, so a poll that ignored one would destroy it.
    /// </summary>
    public static async Task<string> AwaitBundleAsync(HttpClient http, string provider, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(http);

        var clock = Stopwatch.StartNew();
        var state = "unknown";

        while (clock.Elapsed < SettleBudget)
        {
            var view = await ReadSessionAsync(http, provider, sessionId);
            if (view.TextOrNull("bundle") is { } bundle) return bundle;

            state = view.Text("state");
            if (state is "failed" or "expired" or "blocked" or "disabled")
            {
                Assert.Fail($"login for {provider} ended as '{state}' with no bundle: {view}");
            }

            await Task.Delay(100);
        }

        Assert.Fail($"login for {provider} was still '{state}' after {SettleBudget.TotalSeconds:0}s");
        throw new UnreachableException();
    }

    /// <summary>
    /// Polls until the session reports the wanted state. Separate from
    /// <see cref="AwaitBundleAsync"/> because the bundle can land one save
    /// before the state does, and a test that wanted both would otherwise have
    /// to choose which race to lose.
    /// </summary>
    public static async Task<JsonElement> AwaitStateAsync(HttpClient http, string provider, string sessionId, string state)
    {
        ArgumentNullException.ThrowIfNull(http);

        var clock = Stopwatch.StartNew();
        var view = default(JsonElement);

        while (clock.Elapsed < SettleBudget)
        {
            view = await ReadSessionAsync(http, provider, sessionId);
            if (string.Equals(view.Text("state"), state, StringComparison.Ordinal)) return view;

            await Task.Delay(100);
        }

        Assert.Fail($"session {sessionId} never reached '{state}'; last seen: {view}");
        throw new UnreachableException();
    }

    public static async Task<JsonElement> ReadSessionAsync(HttpClient http, string provider, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(http);

        using var response = await http.GetAsync($"/v1/{provider}/login/{sessionId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    /// <summary>api-spec §8 step 4: exchange a stored bundle for a short-lived ticket.</summary>
    public static async Task<string> ResumeAsync(HttpClient http, string provider, Connection connection)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(connection);

        using var request = Wire.Post($"/v1/{provider}/sessions/resume",
            new { subject = connection.Subject, bundle = connection.Bundle });

        using var response = await http.SendAsync(request);
        var body = await response.JsonAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return body.Text("ticket");
    }

    public static async Task<HttpResponseMessage> FetchAsync(HttpClient http, string url, string ticket)
    {
        ArgumentNullException.ThrowIfNull(http);

        using var request = Wire.Get(url);
        request.AddHeader(RequestContext.TicketHeader, ticket);
        return await http.SendAsync(request);
    }

    /// <summary>
    /// A fetch, followed through to its data whichever way the platform chose
    /// to answer.
    ///
    /// A fetch that finishes inside its window returns 200 with the page; one
    /// that does not returns 202 and a job to poll. BOTH are correct, and
    /// which one arrives depends on how loaded the machine is - the inline
    /// runner simply has less CPU when five test projects run at once. A test
    /// that asserts 200 therefore passes alone and fails in the full suite,
    /// which is what <c>ReceiptsTests</c> did: it was reporting load, not a
    /// defect, and a suite that cries wolf under load is one people stop
    /// believing.
    ///
    /// Mirrors what <see cref="ConnectAsync"/> already does for login.
    /// </summary>
    public static async Task<JsonElement> FetchPageAsync(HttpClient http, string provider, string url, string ticket)
    {
        ArgumentNullException.ThrowIfNull(http);

        JsonElement body;
        using (var response = await FetchAsync(http, url, ticket))
        {
            body = await response.JsonAsync();

            if (response.StatusCode == HttpStatusCode.OK) return body;

            Assert.True(
                response.StatusCode == HttpStatusCode.Accepted,
                $"fetch answered {(int)response.StatusCode}: {body}");
        }

        // 202: the job carries the data once it settles.
        var jobId = body.Text("job_id");
        var clock = Stopwatch.StartNew();
        var state = "unknown";

        while (clock.Elapsed < SettleBudget)
        {
            using var poll = await http.GetAsync($"/v1/{provider}/jobs/{jobId}");
            var view = await poll.JsonAsync();
            state = view.Text("state");

            if (string.Equals(state, "succeeded", StringComparison.Ordinal)) return view;

            Assert.False(
                state is "failed" or "expired",
                $"fetch job {jobId} ended as '{state}': {view}");

            await Task.Delay(100);
        }

        Assert.Fail($"fetch job {jobId} was still '{state}' after {SettleBudget.TotalSeconds:0}s");
        throw new UnreachableException();
    }

    public static async Task<HttpResponseMessage> PostWithTicketAsync(HttpClient http, string url, object body, string ticket)
    {
        ArgumentNullException.ThrowIfNull(http);

        using var request = Wire.Post(url, body);
        request.AddHeader(RequestContext.TicketHeader, ticket);
        return await http.SendAsync(request);
    }
}
