using System.Globalization;
using System.Net;
using System.Text;
using Connector.Kit.Agent.Transport;
using Connector.Kit.AgentProtocol;
using Connector.Kit.Challenges;
using Connector.Kit.Errors;

namespace Connector.Kit.Agent.Tests;

/// <summary>
/// The control plane as eleven routes over a stubbed handler.
///
/// Deliberately not a mock of <see cref="ControlPlaneClient"/>: that class is
/// sealed and concrete on purpose, and stubbing at the socket keeps its own
/// status-code rules - a 4xx renew is a verdict, a 5xx is a bad moment - in
/// the path under test.
/// </summary>
internal sealed class FakeControlPlane : HttpMessageHandler, IHttpClientFactory
{
    /// <summary>
    /// How long an empty live-input poll waits before answering 204.
    ///
    /// A real one holds the request for tens of seconds. Nought here would spin
    /// the input loop at the speed of the CPU and drown the very frame timings
    /// these tests exist to measure; a short window keeps the SHAPE of a long
    /// poll without the wait.
    /// </summary>
    private static readonly TimeSpan PollWindow = TimeSpan.FromMilliseconds(50);

    private readonly Lock _gate = new();
    private readonly List<JobFailRequest> _failures = [];
    private readonly List<JobResultRequest> _results = [];
    private readonly List<PostedFrame> _frames = [];
    private readonly Queue<LiveInputBatch> _pendingInput = new();

    private int _renewals;
    private int _challengesRaised;
    private ChallengeAnswer? _answer;
    private RaiseChallengeRequest? _raised;

    /// <summary>One live frame as it arrived on the wire.</summary>
    internal readonly record struct PostedFrame(long Sequence, string? Size, int Bytes);

    /// <summary>Answers the pending challenge, as a human eventually would.</summary>
    public void Answer(string value = "solved")
    {
        lock (_gate) _answer = new ChallengeAnswer { ChallengeId = "chl_test", Value = value };
    }

    /// <summary>Every live frame this agent posted, in order.</summary>
    public IReadOnlyList<PostedFrame> Frames { get { lock (_gate) return [.. _frames]; } }

    /// <summary>
    /// Set to refuse the live channel, as a control plane whose challenge has
    /// been answered or whose capability was revoked does.
    /// </summary>
    public bool LiveChannelGone { get; set; }

    /// <summary>Queues what the human did, for the next input poll to pick up.</summary>
    public void SendLiveInput(LiveInputBatch batch)
    {
        lock (_gate) _pendingInput.Enqueue(batch);
    }

    public int Renewals { get { lock (_gate) return _renewals; } }

    public int ChallengesRaised { get { lock (_gate) return _challengesRaised; } }

    /// <summary>The last challenge as it arrived on the wire, image and all.</summary>
    public RaiseChallengeRequest? Raised { get { lock (_gate) return _raised; } }

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

        // The live routes first: "/live/frame" also ends with "frame", and a
        // suffix match that ran in the wrong order would answer one route from
        // another's handler.
        if (path.EndsWith("/live/frame", StringComparison.Ordinal))
        {
            if (LiveChannelGone) return Empty(HttpStatusCode.Gone);

            var bytes = await request.Content!.ReadAsByteArrayAsync(ct);
            var sequence = long.Parse(
                Header(request, ControlPlaneClient.SequenceHeader) ?? "0", CultureInfo.InvariantCulture);

            lock (_gate)
            {
                _frames.Add(new PostedFrame(sequence, Header(request, ControlPlaneClient.SizeHeader), bytes.Length));
            }

            return Empty(HttpStatusCode.NoContent);
        }

        if (path.EndsWith("/live/input", StringComparison.Ordinal))
        {
            LiveInputBatch? batch;
            lock (_gate) batch = _pendingInput.Count == 0 ? null : _pendingInput.Dequeue();

            if (batch is not null) return Json(System.Text.Json.JsonSerializer.Serialize(batch, AgentJson.Options));

            await Task.Delay(PollWindow, ct);
            return Empty(HttpStatusCode.NoContent);
        }

        if (path.EndsWith("/renew", StringComparison.Ordinal))
        {
            lock (_gate) _renewals++;
            return Empty(HttpStatusCode.NoContent);
        }

        if (path.EndsWith("/progress", StringComparison.Ordinal)) return Empty(HttpStatusCode.NoContent);

        if (path.EndsWith("/challenge", StringComparison.Ordinal))
        {
            var raised = await ReadAsync<RaiseChallengeRequest>(request, ct);
            lock (_gate)
            {
                _challengesRaised++;
                _raised = raised;
            }

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

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

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
