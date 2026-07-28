using Connector.Kit.Adapters;
using Connector.Kit.Challenges;
using Connector.Kit.Jobs;
using Connector.Kit.Security;
using Microsoft.Playwright;

namespace BankConnector.Adapters.Tests.Support;

/// <summary>
/// The adapter's whole world, offline.
///
/// Both refusals are deliberate. <see cref="Browser"/> throws on any use, so
/// a mock that declares a browser tier for ROUTING purposes and then quietly
/// started Chromium would fail here rather than in a CI image. And the
/// default <see cref="Http"/> handler throws and counts, so "opened no
/// socket" is asserted rather than assumed.
/// </summary>
internal sealed class FakeJobContext : IJobContext, IDisposable
{
    private static readonly IReadOnlyDictionary<string, string> None =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private readonly List<JobStep> _steps = [];
    private readonly List<Challenge> _asked = [];
    private readonly Lazy<string> _workDirectory;

    public FakeJobContext(HttpMessageHandler? handler = null)
    {
        Handler = handler ?? new ThrowingHttpHandler();
        Http = new HttpClient(Handler, disposeHandler: false);

        _workDirectory = new Lazy<string>(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), "bank-adapter-tests", Guid.NewGuid().ToString("n"));
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
    /// How the human answers. Refusing by default, so an adapter that raises
    /// an unexpected challenge fails loudly rather than receiving an empty
    /// string and carrying on.
    /// </summary>
    public Func<Challenge, string> Answer { get; init; } =
        challenge => throw new InvalidOperationException($"unexpected challenge of type {challenge.Type}");

    public IReadOnlyList<JobStep> Steps => _steps;

    public IReadOnlyList<Challenge> Asked => _asked;

    /// <summary>
    /// The latch that decides whether a lost lease requeues a job or fails
    /// it permanently. Three retries locks a real bank account, so this is
    /// asserted directly rather than trusted.
    /// </summary>
    public bool CredentialWasSubmitted { get; private set; }

    public string WorkDirectory => _workDirectory.Value;

    public void Progress(JobStep step) => _steps.Add(step);

    public Task<ChallengeAnswer> AskAsync(Challenge challenge, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        ct.ThrowIfCancellationRequested();

        _asked.Add(challenge);

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

internal sealed class NoBrowserLease : IBrowserLease
{
    public bool Started => false;

    public Task<IPage> PageAsync(CancellationToken ct) => throw Refused();

    public Task<string> StorageStateAsync(CancellationToken ct) => throw Refused();

    public Task<byte[]> ScreenshotAsync(CropRegion? crop, CancellationToken ct) => throw Refused();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static InvalidOperationException Refused() => new("this test must not start a browser");
}

/// <summary>
/// Counts as well as throws: an adapter that swallowed the exception would
/// still be caught by the count, and "made zero network calls" is the claim.
/// </summary>
internal sealed class ThrowingHttpHandler : HttpMessageHandler
{
    public int Calls { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        Calls++;

        throw new InvalidOperationException(
            $"this adapter must not make network calls, but it requested {request.Method} {request.RequestUri}");
    }
}

/// <summary>
/// A clock pinned to the ledger's anchor, so a window is a fixed range
/// rather than a function of the day the suite happens to run.
/// </summary>
internal sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _now;

    public FixedTimeProvider(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;
}
