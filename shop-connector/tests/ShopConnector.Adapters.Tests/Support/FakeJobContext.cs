using Connector.Kit.Adapters;
using Connector.Kit.Challenges;
using Connector.Kit.Jobs;
using Connector.Kit.Security;
using Microsoft.Playwright;

namespace ShopConnector.Adapters.Tests.Support;

/// <summary>
/// The adapter's whole world, offline.
///
/// Everything an adapter is allowed to observe, and nothing else. The
/// interesting parts are the two refusals: <see cref="Browser"/> throws on
/// use, so a T1 adapter that quietly started Chromium fails the test rather
/// than the CI image, and the default <see cref="Http"/> handler throws, so
/// "made no network call" is asserted rather than assumed.
/// </summary>
internal sealed class FakeJobContext : IJobContext, IDisposable
{
    private static readonly IReadOnlyDictionary<string, string> None =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private readonly List<JobStep> _steps = [];
    private readonly List<Challenge> _asked = [];
    private readonly List<CancellationToken> _askTokens = [];
    private readonly Lazy<string> _workDirectory;

    public FakeJobContext(HttpMessageHandler? handler = null)
    {
        Handler = handler ?? new ThrowingHttpHandler();
        Http = new HttpClient(Handler, disposeHandler: false);

        _workDirectory = new Lazy<string>(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), "shop-adapter-tests", Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(path);
            return path;
        });
    }

    public HttpMessageHandler Handler { get; }

    public string SessionId { get; init; } = "ses_0123456789abcdef0123456789abcdef";

    public string JobId { get; init; } = "job_0123456789abcdef0123456789abcdef";

    public IReadOnlyDictionary<string, string> Inputs { get; init; } = None;

    public IReadOnlyDictionary<string, string> Config { get; init; } = None;

    public SessionMaterial? Material { get; init; }

    public HttpClient Http { get; }

    public IBrowserLease Browser { get; init; } = new NoBrowserLease();

    /// <summary>
    /// Whether a human can see and touch this job's browser. False by
    /// default, exactly as the contract's own default is: a pooled agent has
    /// nobody to ask, and a test that wants the attended path has to say so.
    /// </summary>
    public bool Attended { get; init; }

    /// <summary>
    /// How the human answers. Defaults to refusing, so an adapter that
    /// raises an unexpected challenge fails loudly instead of receiving an
    /// empty string and carrying on.
    /// </summary>
    public Func<Challenge, string> Answer { get; init; } =
        challenge => throw new InvalidOperationException($"unexpected challenge of type {challenge.Type}");

    /// <summary>
    /// When true a challenge is recorded and then never answered.
    ///
    /// The case a passive challenge exists for: the human acts somewhere we
    /// cannot observe - solving a captcha in the browser window in front of
    /// them - and has no reason ever to come back to the consumer's UI and
    /// click anything. An adapter must be able to finish without them.
    /// </summary>
    public bool AnswersNothing { get; init; }

    public IReadOnlyList<JobStep> Steps => _steps;

    public IReadOnlyList<Challenge> Asked => _asked;

    /// <summary>
    /// The token each ask was made under, in the same order as
    /// <see cref="Asked"/>.
    ///
    /// The difference between ending a passive challenge and walking away from
    /// it. A live view is a browser, a shutter and an occupied agent; the
    /// adapter that abandons its ask leaves all three running for the rest of
    /// the window after the login has already succeeded, and an observer
    /// outside the adapter cannot tell that apart from a clean finish. A
    /// cancelled token here is the proof it was ended.
    /// </summary>
    public IReadOnlyList<CancellationToken> AskTokens => _askTokens;

    /// <summary>
    /// The latch that decides whether a lost lease requeues a job or fails
    /// it. Asserted directly, because "a login that may already have counted
    /// is never retried" is the rule that matters most in the platform.
    /// </summary>
    public bool CredentialWasSubmitted { get; private set; }

    public string WorkDirectory => _workDirectory.Value;

    public void Progress(JobStep step) => _steps.Add(step);

    public Task<ChallengeAnswer> AskAsync(Challenge challenge, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        ct.ThrowIfCancellationRequested();

        _asked.Add(challenge);
        _askTokens.Add(ct);

        if (AnswersNothing)
        {
            // Still cancellable: an adapter that regressed into awaiting an
            // answer nobody sends fails its test rather than hanging the
            // suite on it.
            var pending = new TaskCompletionSource<ChallengeAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => pending.TrySetCanceled(ct));
            return pending.Task;
        }

        return Task.FromResult(new ChallengeAnswer
        {
            ChallengeId = $"chl_{_asked.Count:D2}",
            Value = Answer(challenge),
        });
    }

    public void CredentialSubmitted() => CredentialWasSubmitted = true;

    public void Dispose()
    {
        Http.Dispose();
        Handler.Dispose();

        if (_workDirectory.IsValueCreated && Directory.Exists(_workDirectory.Value))
        {
            Directory.Delete(_workDirectory.Value, recursive: true);
        }
    }
}

/// <summary>
/// A browser lease that refuses to exist.
///
/// A T1 adapter must never touch this, and a fixture test for a T2/T3
/// adapter drives the parse layer rather than the page - so any call here is
/// a test reaching for the one thing it was written to avoid.
/// </summary>
internal sealed class NoBrowserLease : IBrowserLease
{
    public bool Started => false;

    public Task<IPage> PageAsync(CancellationToken ct) => throw Refused();

    public Task<string> StorageStateAsync(CancellationToken ct) => throw Refused();

    public Task<byte[]> ScreenshotAsync(CropRegion? crop, CancellationToken ct) => throw Refused();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static InvalidOperationException Refused() =>
        new("this test must not start a browser");
}

/// <summary>
/// A clock that does not move.
///
/// Only <c>GetUtcNow</c> is overridden: <c>CreateTimer</c> stays on the real
/// system timer, so any test that needs a delay to elapse must drive the
/// delay to zero rather than pretend time passed.
/// </summary>
internal sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _now;

    public FixedTimeProvider(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;
}
