using Connector.Kit.Adapters;
using Connector.Kit.Challenges;
using Connector.Kit.Jobs;
using Connector.Kit.Security;
using Microsoft.Playwright;

namespace RegistryConnector.Adapters.Tests;

/// <summary>
/// A job, without a platform under it.
///
/// Deliberately minimal: the registry providers are HTTP-and-fixtures, so
/// there is no browser to stub. A test that needs one is a test about a
/// provider this service does not have yet.
/// </summary>
internal sealed class FakeJobContext : IJobContext, IDisposable
{
    private readonly List<Challenge> _asked = [];

    public string SessionId { get; init; } = "ses_registry_fixture";

    public string JobId { get; init; } = "job_registry_fixture";

    public IReadOnlyDictionary<string, string> Inputs { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["username"] = "fixture@example.test",
            ["password"] = "hunter2",
        };

    public IReadOnlyDictionary<string, string> Config { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public SessionMaterial? Material { get; init; }

    public HttpClient Http { get; } = new();

    public IBrowserLease Browser => throw new NotSupportedException("no registry provider drives a browser yet");

    public string WorkDirectory { get; } = Path.GetTempPath();

    public bool Attended { get; init; } = true;

    /// <summary>Every challenge the adapter raised, in order.</summary>
    public IReadOnlyList<Challenge> Asked => _asked;

    /// <summary>What the human types back. Null answers nothing.</summary>
    public Func<Challenge, string?>? Answer { get; init; }

    /// <summary>
    /// Never answers, which is what a streamed sign-in really looks like: the
    /// person who signs in on the page in front of them has no reason to come
    /// back to the consumer's UI, so what ends the challenge is the session
    /// appearing rather than an answer arriving.
    /// </summary>
    public bool AnswersNothing { get; init; }

    public IReadOnlyList<JobStep> Steps { get; } = [];

    public bool CredentialWasSubmitted { get; private set; }

    public void Progress(JobStep step)
    {
    }

    public void CredentialSubmitted() => CredentialWasSubmitted = true;

    public Task<ChallengeAnswer> AskAsync(Challenge challenge, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(challenge);

        _asked.Add(challenge);

        if (AnswersNothing) return new TaskCompletionSource<ChallengeAnswer>().Task.WaitAsync(ct);

        return Task.FromResult(new ChallengeAnswer
        {
            ChallengeId = "chl_fixture",
            Value = Answer?.Invoke(challenge) ?? string.Empty,
        });
    }

    public void Dispose() => Http.Dispose();
}
