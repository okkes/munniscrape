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

    /// <summary>
    /// The live frame counter for THIS JOB, which is what the wire says it is.
    ///
    /// It lives here rather than in the session because a job can raise more
    /// than one live view - a login, then a step-up - and the connector holds
    /// one slot per job that refuses any frame not numbered higher than the one
    /// it has. A per-session counter therefore made the second view's every
    /// frame vanish at the connector, answered 200, while the consumer went on
    /// showing the last picture of the first. This context is built once per
    /// job, so it is the smallest thing that outlives a session.
    /// </summary>
    private readonly LiveFrameSequence _frames = new();

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

        // The geometry is logged because a tap answer is meaningless without
        // the box it was measured against. When taps land in the wrong place
        // the question is always which of three rectangles disagreed - the one
        // photographed, the one drawn on the human's screen, and the one the
        // answer is mapped back into - and only the first is knowable here.
        // Fractions and pixel counts are not secrets; the image itself has
        // already been through the redactor.
        _logger.LogInformation(
            "job {JobId}: raised challenge {ChallengeId} type={Type} answer_kind={AnswerKind} " +
            "crop={Crop} image={ImageBytes}B expires={ExpiresAt:O}",
            JobId,
            raised.ChallengeId,
            challenge.Type,
            challenge.AnswerKind,
            challenge.Crop is { } box
                ? $"{box.Width}x{box.Height}@{box.X},{box.Y}"
                : "none",
            image?.Length ?? 0,
            challenge.ExpiresAt);

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

        // A live view is a conversation rather than a question, and the
        // PLATFORM opens and closes it - never the adapter. That is the
        // provenance rule ImageForAsync already applies to adapter-supplied
        // bytes, carried to a far larger capability: an adapter author cannot
        // forget the stop latch because they never touch it, and cannot ask for
        // one on a challenge the platform did not classify as a live view.
        //
        // Bound to the challenge's own deadline, so an expiry stops the shutter
        // by the same clock that stops the wait.
        await using var live = challenge.Type is ChallengeType.LiveView
            ? await OpenLiveViewAsync(deadline.Token).ConfigureAwait(false)
            : null;

        try
        {
            while (true)
            {
                try
                {
                    var answer = await _control
                        .PollAnswerAsync(JobId, deadline.Token, raised.ChallengeId).ConfigureAwait(false);

                    if (answer is not null)
                    {
                        // Belt and braces over the query parameter: an older
                        // control plane ignores it and answers with whatever
                        // is newest, and taking somebody else's answer for our
                        // own would type an SMS code into a captcha.
                        if (!string.Equals(answer.ChallengeId, raised.ChallengeId, StringComparison.Ordinal))
                        {
                            _logger.LogWarning(
                                "job {JobId}: ignoring an answer for challenge {Other}; this asked {Mine}",
                                JobId, answer.ChallengeId, raised.ChallengeId);
                            continue;
                        }

                        LogAnswer(challenge, answer);
                        return answer;
                    }
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
    /// What came back, in the only detail that is safe to write down.
    ///
    /// A tap answer is coordinates on a picture the redactor already cleared,
    /// so it can be logged in full - and it has to be, because "the taps
    /// landed in the wrong square" is unanswerable without knowing which
    /// fractions arrived and which pixels they became. Every other answer is
    /// secret-tainted by default: an MFA code, a one-time password, whatever a
    /// provider decided to ask for. Those get a length and nothing else.
    /// </summary>
    private void LogAnswer(Challenge challenge, ChallengeAnswer answer)
    {
        if (challenge.AnswerKind != ChallengeAnswerKind.Taps)
        {
            _logger.LogInformation(
                "job {JobId}: answered {Type} challenge with {Length} character(s)",
                JobId, challenge.Type, answer.Value?.Length ?? 0);
            return;
        }

        if (!TapAnswer.TryParse(answer.Value, out var taps))
        {
            // The consumer ignored answer_kind and sent a string. Worth a
            // warning rather than a debug line: nothing will be clicked, the
            // adapter will report an unanswered challenge, and the reason will
            // otherwise be invisible from either end.
            _logger.LogWarning(
                "job {JobId}: a taps challenge came back as something else ({Length} character(s)); " +
                "nothing will be clicked. The consumer is ignoring answer_kind.",
                JobId, answer.Value?.Length ?? 0);
            return;
        }

        // Mapped here purely so the log carries both halves. The adapter maps
        // them again against a FRESHLY measured box before dispatching, and
        // the difference between these pixels and where the click actually
        // lands is exactly the re-layout this design has to survive.
        var mapped = challenge.Crop is { } box
            ? string.Join(" ", taps.Taps.Select(t =>
            {
                var (x, y) = t.ToPagePixels(box);
                return $"({t.X:0.####},{t.Y:0.####})->{x:0}x{y:0}";
            }))
            : string.Join(" ", taps.Taps.Select(t => $"({t.X:0.####},{t.Y:0.####})"));

        _logger.LogInformation(
            "job {JobId}: answered with {Count} tap(s), submit={Submit}, against crop {Crop}: {Mapped}",
            JobId,
            taps.Taps.Count,
            taps.Submit,
            challenge.Crop is { } c ? $"{c.Width}x{c.Height}@{c.X},{c.Y}" : "none",
            mapped.Length == 0 ? "(none)" : mapped);
    }

    /// <summary>
    /// Opens the stream for a <see cref="ChallengeType.LiveView"/>, or null
    /// when there is nothing to photograph.
    ///
    /// Every failure here produces a live view that does not open, never a job
    /// that fails. The challenge underneath is passive - the same machinery
    /// <see cref="ChallengeType.AppApproval"/> uses - so a stream that could not
    /// start degrades to a wait the adapter's own success watch still ends,
    /// which is the degradation the consumer would get anyway if it had never
    /// heard of a live view.
    /// </summary>
    private async Task<LiveViewSession?> OpenLiveViewAsync(CancellationToken ct)
    {
        if (!_browser.Started)
        {
            // Deliberately not launching one. A browser started here would open
            // on about:blank, which is not a login page and not on any origin
            // the stream is allowed to photograph.
            _logger.LogWarning(
                "job {JobId}: a live view was raised with no browser running, so there is nothing to photograph",
                JobId);
            return null;
        }

        try
        {
            var page = await _browser.PageAsync(ct).ConfigureAwait(false);
            var session = new LiveViewSession(
                page, _redactor, _control, JobId, _frames, new LiveViewOptions(), _logger, _time);

            session.Start(ct);
            return session;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "job {JobId}: the live view could not be opened", JobId);
            return null;
        }
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
        // A live view carries no still, ever, whoever offered one. The raise
        // payload's image is written to the challenge row and lives there for
        // the length of the job - and "a live view frame is never stored" is
        // not a habit, it is what makes the custody claim true. The stream
        // starts a fraction of a second later on a channel that keeps nothing.
        if (challenge.Type is ChallengeType.LiveView) return null;

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
