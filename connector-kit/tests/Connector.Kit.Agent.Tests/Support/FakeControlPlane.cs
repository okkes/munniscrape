using System.Net;
using System.Text;
using Connector.Kit.Agent.Transport;
using Connector.Kit.AgentProtocol;
using Connector.Kit.Challenges;
using Connector.Kit.Errors;

namespace Connector.Kit.Agent.Tests;

/// <summary>
/// The control plane as nine routes over a stubbed handler.
///
/// Deliberately not a mock of <see cref="ControlPlaneClient"/>: that class is
/// sealed and concrete on purpose, and stubbing at the socket keeps its own
/// status-code rules - a 4xx renew is a verdict, a 5xx is a bad moment - in
/// the path under test.
/// </summary>
internal sealed class FakeControlPlane : HttpMessageHandler, IHttpClientFactory
{
    private readonly Lock _gate = new();
    private readonly List<JobFailRequest> _failures = [];
    private readonly List<JobResultRequest> _results = [];

    private int _renewals;
    private int _challengesRaised;
    private ChallengeAnswer? _answer;

    /// <summary>Answers the pending challenge, as a human eventually would.</summary>
    public void Answer(string value = "solved")
    {
        lock (_gate) _answer = new ChallengeAnswer { ChallengeId = "chl_test", Value = value };
    }

    public int Renewals { get { lock (_gate) return _renewals; } }

    public int ChallengesRaised { get { lock (_gate) return _challengesRaised; } }

    public JobFailRequest? Failure { get { lock (_gate) return _failures.Count == 0 ? null : _failures[^1]; } }

    public JobResultRequest? Result { get { lock (_gate) return _results.Count == 0 ? null : _results[^1]; } }

    /// <summary>The wire code of the last reported failure, or null on success.</summary>
    public string? FailureCode => Failure?.Code;

    public HttpClient CreateClient(string name) =>
        new(this, disposeHandler: false) { BaseAddress = new Uri("https://control-plane.test/") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // Real HTTP never completes synchronously, and a stub that does hides
        // ordering bugs the production path would hit.
        await Task.Yield();
        ct.ThrowIfCancellationRequested();

        var path = request.RequestUri!.AbsolutePath;

        if (path.EndsWith("/renew", StringComparison.Ordinal))
        {
            lock (_gate) _renewals++;
            return Empty(HttpStatusCode.NoContent);
        }

        if (path.EndsWith("/progress", StringComparison.Ordinal)) return Empty(HttpStatusCode.NoContent);

        if (path.EndsWith("/challenge", StringComparison.Ordinal))
        {
            lock (_gate) _challengesRaised++;
            return Json("""{"challenge_id":"chl_test"}""");
        }

        if (path.EndsWith("/answer", StringComparison.Ordinal))
        {
            ChallengeAnswer? answer;
            lock (_gate) answer = _answer;

            return answer is null
                ? Empty(HttpStatusCode.NoContent)
                : Json($$"""{"challenge_id":"{{answer.ChallengeId}}","value":"{{answer.Value}}"}""");
        }

        if (path.EndsWith("/result", StringComparison.Ordinal))
        {
            var result = await ReadAsync<JobResultRequest>(request, ct);
            lock (_gate) _results.Add(result);
            return Empty(HttpStatusCode.NoContent);
        }

        if (path.EndsWith("/fail", StringComparison.Ordinal))
        {
            var failure = await ReadAsync<JobFailRequest>(request, ct);
            lock (_gate) _failures.Add(failure);
            return Empty(HttpStatusCode.NoContent);
        }

        return Empty(HttpStatusCode.NotFound);
    }

    /// <summary>Asserts the last failure carried a given code.</summary>
    public bool FailedWith(ErrorCode code) =>
        string.Equals(FailureCode, ErrorCatalog.Wire(code), StringComparison.Ordinal);

    private static async Task<T> ReadAsync<T>(HttpRequestMessage request, CancellationToken ct)
    {
        var body = await request.Content!.ReadAsStringAsync(ct);
        return System.Text.Json.JsonSerializer.Deserialize<T>(body, AgentJson.Options)!;
    }

    private static HttpResponseMessage Empty(HttpStatusCode status) => new(status);

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
}
