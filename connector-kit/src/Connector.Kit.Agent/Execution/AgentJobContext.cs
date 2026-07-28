using System.Net;
using System.Threading.Channels;
using Connector.Kit.Adapters;
using Connector.Kit.Agent.Browsing;
using Connector.Kit.Agent.Networking;
using Connector.Kit.Agent.Transport;
using Connector.Kit.AgentProtocol;
using Connector.Kit.Challenges;
using Connector.Kit.Errors;
using Connector.Kit.Jobs;
using Connector.Kit.Manifests;
using Connector.Kit.Security;
using Microsoft.Extensions.Logging;

namespace Connector.Kit.Agent.Execution;

/// <summary>
/// The adapter's window onto one run, and the only object an adapter author
/// ever holds.
///
/// Everything it does is deliberately one-way: progress goes out, an answer
/// comes back, and nothing an adapter can call reaches the database, the
/// consuming app, or the retry policy.
/// </summary>
public sealed class AgentJobContext : IJobContext, IAsyncDisposable
{
    private readonly LeasedJob _job;
    private readonly ControlPlaneClient _control;
    private readonly BrowserLease _browser;
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly ScreenshotRedactor _redactor;
    private readonly TimeProvider _time;
    private readonly TimeSpan _answerPollInterval;
    private readonly JobBudget? _budget;

    private readonly Channel<JobStep> _steps =
        Channel.CreateUnbounded<JobStep>(new UnboundedChannelOptions { SingleReader = true });

    private readonly List<JobStep> _reported = [];
    private readonly Task _pump;

    private int _credentialSubmitted;
    private JobStep _lastStep = JobStep.AgentAssigned;
    private int _disposed;

    public AgentJobContext(
        JobContextOptions options,
        ControlPlaneClient control,
        PolitenessGate gate,
        ILoggerFactory loggers,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(loggers);

        _job = options.Job;
        _control = control;
        _time = time ?? TimeProvider.System;
        _answerPollInterval = options.AnswerPollInterval;
        _budget = options.Budget;
        _logger = loggers.CreateLogger($"Connector.Kit.Agent.Job.{options.Job.Provider}");

        // Read off the browser options this job will actually be handed rather
        // than re-derived from configuration, so the flag cannot drift from the
        // window that is really on the screen.
        Attended = !options.Browser.Headless;

        WorkDirectory = Path.Combine(Path.GetFullPath(options.WorkRootDirectory), options.Job.JobId);
        Directory.CreateDirectory(WorkDirectory);

        _redactor = new ScreenshotRedactor(options.Manifest, _logger);
        _browser = new BrowserLease(
            options.Browser with { DownloadsPath = WorkDirectory },
            _redactor,
            _logger);

        _http = BuildHttpClient(gate, options);
        SecretValues =
        [
            .. options.Manifest.Auth.AllFields()
                .Where(f => f.Secret)
                .Select(f => options.Job.Inputs.TryGetValue(f.Key, out var v) ? v : null)
                .Where(v => !string.IsNullOrEmpty(v))
                .Select(v => v!),
        ];

        _pump = Task.Run(PumpProgressAsync);
    }

    public string SessionId => _job.SessionId;

    public string JobId => _job.JobId;

    public IReadOnlyDictionary<string, string> Inputs => _job.Inputs;

    public IReadOnlyDictionary<string, string> Config => _job.Config;

    public SessionMaterial? Material => _job.Material;

    public HttpClient Http => _http;

    public IBrowserLease Browser => _browser;

    public string WorkDirectory { get; }

    /// <summary>
    /// True only on a HEADED agent - hardware someone is sitting at, where the
    /// owner can reach the browser window themselves.
    ///
    /// Headless is the default everywhere, so "nobody is there" is the default
    /// answer, which is what an adapter meeting an interactive widget needs it
    /// to be. A pooled agent in a datacentre has no one to hand the mouse to.
    /// </summary>
    public bool Attended { get; }

    /// <summary>
    /// True once the adapter has reported a credential as submitted upstream.
    /// The job runner reads it to decide whether a failure may be described as
    /// something the control plane could safely retry.
    /// </summary>
    public bool HasSubmittedCredential => Volatile.Read(ref _credentialSubmitted) == 1;

    /// <summary>The secret input values for this job, for scrubbing outbound detail.</summary>
    internal IReadOnlyCollection<string> SecretValues { get; }

    public void Progress(JobStep step)
    {
        _lastStep = step;
        _steps.Writer.TryWrite(step);
    }

    /// <summary>
    /// Latches the credential flag and pushes it upstream immediately.
    ///
    /// The contract says it is reported on the next progress post; making the
    /// call itself produce one is what stops a login that submits a password
    /// and then dies from looking, to the control plane, like a job that never
    /// got that far - which is the difference between a failed login and a
    /// locked account on the retry.
    /// </summary>
    public void CredentialSubmitted()
    {
        if (Interlocked.Exchange(ref _credentialSubmitted, 1) == 1) return;

        _logger.LogInformation("job {JobId}: a credential has been submitted upstream", JobId);
        _steps.Writer.TryWrite(_lastStep);
    }

    /// <summary>
    /// Relays a question to the human who owns the account and waits.
    ///
    /// The single call that implements the whole challenge protocol. Redaction,
    /// upload, webhook, the consumer's UI and the answer coming back are all on
    /// this side of the line.
    /// </summary>
    public async Task<ChallengeAnswer> AskAsync(Challenge challenge, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(challenge);

        var now = _time.GetUtcNow();
        if (challenge.IsExpired(now))
        {
            throw new ConnectorException(ErrorCode.ChallengeExpired,
                $"challenge of type {challenge.Type} expired at {challenge.ExpiresAt:O}, before it was raised");
        }

        var image = await ImageForAsync(challenge, ct).ConfigureAwait(false);
        var raised = await _control.RaiseChallengeAsync(JobId, new RaiseChallengeRequest
        {
            Type = challenge.Type,
            AnswerKind = challenge.AnswerKind,
            PromptKey = challenge.PromptKey,
            ExpiresAt = challenge.ExpiresAt,
            Code = challenge.Code,
            Delivery = challenge.Delivery,
            Length = challenge.Length,
            Options = challenge.Options,
            Url = challenge.Url,
            ReturnPattern = challenge.ReturnPattern,
            ImageBase64 = image is null ? null : Convert.ToBase64String(image),
        }, ct).ConfigureAwait(false);

        Progress(JobStep.AwaitingHuman);
        _logger.LogInformation(
            "job {JobId}: raised challenge {ChallengeId} of type {Type}, expiring {ExpiresAt:O}",
            JobId, raised.ChallengeId, challenge.Type, challenge.ExpiresAt);

        // The challenge's own expiry bounds the wait, not the job timeout: a
        // stale challenge holds a live browser hostage, and releasing the agent
        // promptly is worth more than one more poll.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(challenge.ExpiresAt - now);

        // And the job's budget stops here for as long as that wait lasts. It
        // exists to catch an adapter that is stuck, not one that is politely
        // waiting for the person who owns the account; letting both clocks run
        // means whichever is shorter wins, and the human loses.
        using var parked = _budget?.Park();

        try
        {
            while (true)
            {
                try
                {
                    var answer = await _control.PollAnswerAsync(JobId, deadline.Token).ConfigureAwait(false);
                    if (answer is not null) return answer;
                }
                catch (ControlPlaneException ex) when (ex.IsTransient)
                {
                    // A human answering an SMS code takes minutes, and a home
                    // line drops connections in that time. Dropping the
                    // challenge over a blip would waste the code they just
                    // received; the deadline still bounds the wait.
                    _logger.LogDebug(ex, "job {JobId}: answer poll blipped", JobId);
                }

                await Task.Delay(_answerPollInterval, _time, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ConnectorException(ErrorCode.MfaTimeout,
                $"nobody answered challenge {raised.ChallengeId} before {challenge.ExpiresAt:O}");
        }
    }

    /// <summary>
    /// Waits for every queued progress post to land. Called before the
    /// terminal result or failure so the control plane has the credential
    /// latch before it decides what may be retried.
    /// </summary>
    public async Task FlushProgressAsync()
    {
        _steps.Writer.TryComplete();
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "the progress pump ended badly");
        }
    }

    /// <summary>
    /// What makes a broken adapter fixable: a redacted screenshot and a hash of
    /// the page's shape. Null when no browser ever started, or when the page
    /// could not be verified safe to photograph.
    /// </summary>
    public async Task<FailureArtifacts?> CaptureArtifactsAsync(CancellationToken ct)
    {
        if (!_browser.Started) return null;

        try
        {
            var screenshot = await _browser.ScreenshotAsync(null, ct).ConfigureAwait(false);
            var digest = await _browser.DomDigestAsync(ct).ConfigureAwait(false);

            if (screenshot.Length == 0 && digest is null) return null;

            return new FailureArtifacts
            {
                ScreenshotBase64 = screenshot.Length > 0 ? Convert.ToBase64String(screenshot) : null,
                DomDigest = digest,
            };
        }
        catch (Exception ex)
        {
            // Artifact capture is diagnostics. It never gets to mask the real
            // failure it was called to describe.
            _logger.LogDebug(ex, "job {JobId}: failure artifacts could not be captured", JobId);
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        await FlushProgressAsync().ConfigureAwait(false);
        await _browser.DisposeAsync().ConfigureAwait(false);
        _http.Dispose();

        DeleteWorkDirectory();
    }

    /// <summary>
    /// A private client per job, with its own cookie jar.
    ///
    /// Pooling the handler across jobs would share cookies between two users'
    /// sessions with the same provider, which is a correctness bug before it is
    /// a privacy one. Connection reuse is worth far less than that isolation:
    /// jobs are minutes apart, not milliseconds.
    /// </summary>
    private static HttpClient BuildHttpClient(PolitenessGate gate, JobContextOptions options)
    {
        var transport = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };

        var limiter = new PolitenessLimiter(
            gate,
            options.Job.Provider,
            TimeSpan.FromMilliseconds(Math.Max(0, options.Job.Limits.PolitenessMs)))
        {
            InnerHandler = transport,
        };

        return new HttpClient(limiter, disposeHandler: true) { Timeout = options.HttpTimeout };
    }

    /// <summary>
    /// Decides what image, if any, accompanies a challenge.
    ///
    /// Adapter-supplied bytes are re-verified against the live page before they
    /// go anywhere. The adapter holds a real <c>IPage</c>, so it could have
    /// produced them by any route, and "before any image leaves the machine"
    /// has to mean exactly that.
    /// </summary>
    private async Task<byte[]?> ImageForAsync(Challenge challenge, CancellationToken ct)
    {
        if (challenge.Image is { Length: > 0 } supplied)
        {
            if (!_browser.Started) return supplied;

            var page = await _browser.PageAsync(ct).ConfigureAwait(false);
            if (await _redactor.IsSafeToCaptureAsync(page, ct).ConfigureAwait(false)) return supplied;

            _logger.LogWarning(
                "job {JobId}: dropping the adapter's challenge image; the page is not verifiably safe to capture",
                JobId);
            return null;
        }

        var wantsImage = challenge.Crop is not null
                         || challenge.Type is ChallengeType.Image or ChallengeType.QrDisplay;

        if (!wantsImage || !_browser.Started) return null;

        var captured = await _browser.ScreenshotAsync(challenge.Crop, ct).ConfigureAwait(false);
        return captured.Length > 0 ? captured : null;
    }

    private async Task PumpProgressAsync()
    {
        await foreach (var step in _steps.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            var report = new ProgressReport
            {
                Step = step,
                StepsDone = [.. _reported],
                CredentialSubmitted = Volatile.Read(ref _credentialSubmitted) == 1,
            };

            if (!_reported.Contains(step)) _reported.Add(step);

            await PostProgressAsync(report).ConfigureAwait(false);
        }
    }

    private async Task PostProgressAsync(ProgressReport report)
    {
        // Retried because this is the only carrier for the credential latch,
        // and a dropped post there is the difference between the control plane
        // refusing a retry and re-running a login that may already have counted.
        const int Attempts = 3;

        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _control.ProgressAsync(JobId, report, timeout.Token).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is ControlPlaneException or OperationCanceledException)
            {
                if (attempt == Attempts || (ex as ControlPlaneException)?.IsTransient == false)
                {
                    _logger.LogWarning(ex, "job {JobId}: progress step {Step} did not land", JobId, report.Step);
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), _time).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Deleted in every path, including failure. A downloaded bank statement
    /// left on an agent's disk is precisely the residue this design exists to
    /// avoid, and the failure path is the one where it would actually happen.
    /// </summary>
    private void DeleteWorkDirectory()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(WorkDirectory)) Directory.Delete(WorkDirectory, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Chromium can still hold a handle for a moment after teardown.
                if (attempt == 2)
                {
                    _logger.LogError(ex, "job {JobId}: work directory {Directory} could not be deleted", JobId, WorkDirectory);
                    return;
                }

                Thread.Sleep(200);
            }
        }
    }
}

/// <summary>Everything a job context needs that is not a shared service.</summary>
public sealed record JobContextOptions
{
    public required LeasedJob Job { get; init; }

    public required ProviderManifest Manifest { get; init; }

    public required string WorkRootDirectory { get; init; }

    public BrowserLeaseOptions Browser { get; init; } = new();

    /// <summary>Backstop between long-polls for a challenge answer.</summary>
    public TimeSpan AnswerPollInterval { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The run's working-time budget, suspended while the context is parked in
    /// <see cref="AgentJobContext.AskAsync"/>. Null leaves the context with no
    /// budget to suspend, which is only ever the case outside the job runner.
    /// </summary>
    internal JobBudget? Budget { get; init; }
}
