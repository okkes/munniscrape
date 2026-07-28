using System.Security.Cryptography;
using Connector.Kit.Agent.Transport;
using Connector.Kit.Challenges;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace Connector.Kit.Agent.Browsing;

/// <summary>
/// How fast the shutter runs, and when it stops.
///
/// The cadence numbers are the argument, so they are here with their reasons
/// rather than scattered through the loop.
/// </summary>
internal sealed record LiveViewOptions
{
    /// <summary>
    /// The shutter interval while the human is touching the page.
    ///
    /// 80 ms is a CEILING of 12.5 frames a second and will often be missed
    /// downward, which is the point: the loop starts the next shutter when the
    /// last one finished, so a slow page degrades to whatever it can manage
    /// instead of building a queue of pictures that arrive late and are then
    /// answered against a box that has moved. The design's own measurement put
    /// a ~33 ms floor on a synthetic page and warned of several times that on a
    /// real one with nested third-party frames.
    /// </summary>
    public TimeSpan BurstInterval { get; init; } = TimeSpan.FromMilliseconds(80);

    /// <summary>
    /// How long a burst lasts after the last event was replayed.
    ///
    /// 1.2 s, which is roughly the gap a person leaves between words. Shorter
    /// and the stream drops to the idle tier in the middle of a password, so
    /// the human watches their own typing arrive half a second late and types
    /// it again.
    /// </summary>
    public TimeSpan BurstWindow { get; init; } = TimeSpan.FromMilliseconds(1200);

    /// <summary>
    /// The shutter interval when nobody has touched anything.
    ///
    /// 500 ms. A login form is static until somebody touches it, and 2 frames a
    /// second is still fast enough to notice a cookie banner appearing or an
    /// error message being drawn - which are the only things that move on their
    /// own - for about four percent of the burst tier's cost.
    /// </summary>
    public TimeSpan IdleInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The longest a stream stays silent when nothing on the page has changed.
    ///
    /// Change suppression means a static form sends nothing at all, which is
    /// correct and is also indistinguishable, from the viewer's side, from a
    /// stream that has died. One frame every 5 s is a few hundred bytes a
    /// second and gives a viewer that attached late something to draw.
    /// </summary>
    public TimeSpan MaxSilence { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// JPEG quality. 60 is where the design's measurements were taken and it is
    /// a deliberate compromise: below about 80 the 11-pixel small print on a
    /// Dutch login page starts to ring, and above it the byte count runs away
    /// at twelve frames a second. It is the number most worth revisiting once
    /// somebody has looked at a real provider's page on a real phone.
    /// </summary>
    public int JpegQuality { get; init; } = 60;

    /// <summary>
    /// Consecutive transport failures before the stream gives up on a leg.
    ///
    /// Not one: a home line drops a request and the human is still typing. Not
    /// unbounded either - a control plane that has never heard of these routes
    /// answers every frame the same way, and hammering it twelve times a second
    /// for the length of a login is the failure mode this bounds.
    /// </summary>
    public int MaxConsecutiveFailures { get; init; } = 5;

    /// <summary>How often the cost of a frame is written down. See <see cref="LiveViewMeter"/>.</summary>
    public TimeSpan MeterInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The exact hosts this stream may photograph. Empty means "wherever the
    /// page already is when the stream opens", which is the strictest rule
    /// available without a manifest to read - see
    /// <see cref="LiveViewSession"/>'s remarks.
    /// </summary>
    public IReadOnlyList<string> Origins { get; init; } = [];

    /// <summary>Backstop between input polls when the control plane is unwell.</summary>
    public TimeSpan InputRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The shortest an empty input poll may take before the next one starts.
    ///
    /// The route is specified as a long poll, so this should never fire. It is
    /// here because the failure if it is wrong is not a slow stream but a
    /// core at 100%: a control plane that answers 204 immediately turns this
    /// loop into a spin, and the first symptom is the frame timings this whole
    /// exercise exists to measure being wrong for a reason nobody can see.
    /// </summary>
    public TimeSpan EmptyPollFloor { get; init; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>
/// One live view, for the length of one challenge: photograph the page many
/// times a second, post each frame, and replay what the human sends back.
///
/// <b>Two loops, not one.</b> The input leg is a long poll bounded by the
/// control plane's own window - tens of seconds - so running it inline would
/// stall the shutter for essentially the whole session. They share a stop and
/// one nudge: replaying an event puts the shutter into its burst tier, because
/// the only moment a login form changes is just after somebody touched it.
///
/// <b>Stopping is terminal.</b> <see cref="Stop"/> latches through an
/// <see cref="Interlocked.Exchange(ref int, int)"/> and is checked after every
/// await, so no path resumes a stream that has been stopped. Four things stop
/// it today: the caller's token, the page navigating off the allowed origin,
/// the control plane saying the channel is gone, and a run of transport
/// failures.
///
/// <b>The origin latch, and what it is not.</b> The design wants an exact-host
/// allowlist the adapter declares in its manifest, refused if it contains a
/// wildcard, because a stream that keeps running after a successful login is a
/// live remote view of an authenticated account. The manifest half of that does
/// not exist yet. What this does instead is strictly narrower and needs nothing
/// from anybody: with no allowlist it pins to the host the page is on when the
/// stream opens, and stops the moment the main frame goes anywhere else. That
/// is observed rather than claimed, which is the stronger kind of check; what
/// it cannot do is allow a login that legitimately crosses to an identity
/// provider, and that is exactly what the manifest list is for.
///
/// <b>Nothing here is stored.</b> One frame exists at a time, on the stack, and
/// what outlives it is a 32-byte hash used to notice that the next one is
/// identical. Not the previous frame's pixels: holding a picture of somebody's
/// login page in agent memory to compare against is the same mistake as writing
/// it to a row, one order of magnitude smaller.
/// </summary>
internal sealed class LiveViewSession : IAsyncDisposable
{
    private readonly IPage _page;
    private readonly ScreenshotRedactor _redactor;
    private readonly ControlPlaneClient _control;
    private readonly ILiveSurface _surface;
    private readonly string _jobId;
    private readonly LiveViewOptions _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly LiveViewMeter _meter;
    private readonly HashSet<string> _origins;

    /// <summary>
    /// Wakes the shutter out of its idle wait the instant an event lands.
    /// Bounded at one so a burst of events cannot bank wake-ups that fire long
    /// after the human stopped.
    /// </summary>
    private readonly SemaphoreSlim _nudge = new(0, 1);

    private CancellationTokenSource? _cts;
    private Task? _capture;
    private Task? _input;
    private int _stopped;

    /// <summary>
    /// The size of the last frame that actually left this machine, packed into
    /// one long so width and height can never be read from two different
    /// frames. Zero until the first frame is posted, which is what makes
    /// "input before any picture" a refusal rather than a guess.
    /// </summary>
    private long _frameSize;

    private long _burstUntilTicks;

    public LiveViewSession(
        IPage page,
        ScreenshotRedactor redactor,
        ControlPlaneClient control,
        string jobId,
        LiveViewOptions options,
        ILogger logger,
        TimeProvider time,
        ILiveSurface? surface = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(redactor);
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        _page = page;
        _redactor = redactor;
        _control = control;
        _jobId = jobId;
        _options = options;
        _logger = logger;
        _time = time;
        _surface = surface ?? new PlaywrightLiveSurface(page.Mouse, page.Keyboard);
        _meter = new LiveViewMeter(time, options.MeterInterval);

        _origins = BuildOrigins(options.Origins, page, logger);
    }

    /// <summary>True once anything has stopped this stream. Never goes back.</summary>
    public bool Stopped => Volatile.Read(ref _stopped) == 1;

    /// <summary>
    /// Frames that reached the control plane. Written by the shutter loop and
    /// read once the loops have finished, which is what decides whether a
    /// closing measurement is worth writing down at all.
    /// </summary>
    public long FramesPosted { get; private set; }

    /// <summary>
    /// Starts both loops. Returns immediately; the stream lives until
    /// <see cref="DisposeAsync"/>, the token, or a latch.
    /// </summary>
    public void Start(CancellationToken ct)
    {
        if (_cts is not null) throw new InvalidOperationException("this live view has already been started");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        _logger.LogInformation(
            "job {JobId}: live view open on {Origins} at up to {Fps:0.#} fps", _jobId, Describe(_origins), Fps());

        // The navigation latch is wired before the first shutter, so a page
        // that leaves while the browser is still launching is caught too.
        _page.FrameNavigated += OnFrameNavigated;

        _capture = Task.Run(() => CaptureLoopAsync(_cts.Token), CancellationToken.None);
        _input = Task.Run(() => InputLoopAsync(_cts.Token), CancellationToken.None);
    }

    /// <summary>
    /// Stops the stream for good. Safe to call from anywhere and any number of
    /// times; only the first call is heard, and there is no way to restart.
    /// </summary>
    public void Stop(string reason)
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1) return;

        _logger.LogInformation("job {JobId}: live view stopped - {Reason}", _jobId, reason);

        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Disposal got there first, which is one of the ways a stream ends.
        }
    }

    public async ValueTask DisposeAsync()
    {
        Stop("the challenge is over");

        _page.FrameNavigated -= OnFrameNavigated;

        // Teardown never throws over a job's real outcome, exactly as the
        // browser lease's does not: a stream that ended badly must not turn a
        // completed login into a failure.
        foreach (var loop in new[] { _capture, _input })
        {
            if (loop is null) continue;

            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "job {JobId}: a live view loop ended badly", _jobId);
            }
        }

        if (FramesPosted > 0)
        {
            if (_meter.Close() is { HasSamples: true } trailing) _logger.Report(_jobId, trailing);

            _logger.LogInformation("job {JobId}: live view sent {Total}", _jobId, _meter.Total());
        }

        _cts?.Dispose();
        _nudge.Dispose();
    }

    private async Task CaptureLoopAsync(CancellationToken ct)
    {
        var sequence = 0L;
        var lastHash = Array.Empty<byte>();
        var lastPostedAt = _time.GetUtcNow();
        var failures = 0;

        try
        {
            while (!ct.IsCancellationRequested && !Stopped)
            {
                // Cheap, local, and checked every frame rather than trusted to
                // the event: FrameNavigated is a notification and a
                // notification can be missed, but no frame may leave for a page
                // that is not where it is supposed to be.
                if (!IsAllowed(_page.Url))
                {
                    Stop("the page navigated off the origin the stream opened on");
                    return;
                }

                var shutterAt = _time.GetTimestamp();
                var capture = await _redactor
                    .CaptureLiveFrameAsync(_page, _options.JpegQuality, ct).ConfigureAwait(false);
                var shutter = _time.GetElapsedTime(shutterAt);

                if (!capture.Captured)
                {
                    _meter.Missed(shutter);
                    ReportIfDue();
                    await WaitAsync(ct).ConfigureAwait(false);
                    continue;
                }

                // Hashing the ENCODED bytes, which the design measured to be
                // byte-identical across a gap on a page that did not change.
                // The hash and not the frame: 32 bytes of nothing, rather than
                // a picture of a login page kept alive to compare against.
                var hash = SHA256.HashData(capture.Jpeg);
                var unchanged = lastHash.Length > 0 && hash.AsSpan().SequenceEqual(lastHash);

                if (unchanged && _time.GetUtcNow() - lastPostedAt < _options.MaxSilence)
                {
                    _meter.Suppressed(shutter);
                    ReportIfDue();
                    await WaitAsync(ct).ConfigureAwait(false);
                    continue;
                }

                sequence++;
                var frame = new LiveFrame
                {
                    Sequence = sequence,
                    Width = capture.Width,
                    Height = capture.Height,
                    Bytes = capture.Jpeg,
                };

                var postAt = _time.GetTimestamp();
                bool accepted;
                try
                {
                    accepted = await _control.PostLiveFrameAsync(_jobId, frame, ct).ConfigureAwait(false);
                }
                catch (ControlPlaneException ex)
                {
                    _meter.Failed(shutter);
                    ReportIfDue();

                    if (++failures >= _options.MaxConsecutiveFailures)
                    {
                        _logger.LogWarning(
                            ex, "job {JobId}: {Count} live frames in a row did not land", _jobId, failures);
                        Stop("frames stopped reaching the control plane");
                        return;
                    }

                    _logger.LogDebug(ex, "job {JobId}: live frame {Sequence} did not land", _jobId, sequence);
                    await WaitAsync(ct).ConfigureAwait(false);
                    continue;
                }

                var post = _time.GetElapsedTime(postAt);

                if (!accepted)
                {
                    Stop("the control plane no longer has a live channel for this job");
                    return;
                }

                failures = 0;
                lastHash = hash;
                lastPostedAt = _time.GetUtcNow();

                // Published only after the bytes are accepted, so an event that
                // arrives before any picture has one meaning - refuse - rather
                // than being mapped against a frame nobody received.
                Volatile.Write(ref _frameSize, ((long)capture.Width << 32) | (uint)capture.Height);
                FramesPosted++;

                _meter.Posted(shutter, post, frame.Bytes.Length);
                ReportIfDue();

                await WaitAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The stream ending is not an error, whichever end ended it.
        }
    }

    private async Task InputLoopAsync(CancellationToken ct)
    {
        var cursor = 0L;
        var failures = 0;

        try
        {
            while (!ct.IsCancellationRequested && !Stopped)
            {
                var polledAt = _time.GetTimestamp();

                LiveInputBatch? batch;
                try
                {
                    batch = await _control.PollLiveInputAsync(_jobId, cursor, ct).ConfigureAwait(false);
                    failures = 0;
                }
                catch (ControlPlaneException ex)
                {
                    if (++failures >= _options.MaxConsecutiveFailures)
                    {
                        _logger.LogWarning(
                            ex, "job {JobId}: {Count} live input polls in a row failed", _jobId, failures);
                        Stop("the input channel stopped answering");
                        return;
                    }

                    _logger.LogDebug(ex, "job {JobId}: a live input poll blipped", _jobId);
                    await Task.Delay(_options.InputRetryDelay, _time, ct).ConfigureAwait(false);
                    continue;
                }

                // 204: the window closed with nobody touching anything, which
                // is the normal state of a login form. Ask again - but never
                // faster than the floor, because a control plane that answers
                // immediately would otherwise turn this into a spin.
                if (batch is null || batch.Events.Count == 0)
                {
                    var spent = _time.GetElapsedTime(polledAt);
                    if (spent < _options.EmptyPollFloor)
                    {
                        await Task.Delay(_options.EmptyPollFloor - spent, _time, ct).ConfigureAwait(false);
                    }

                    continue;
                }

                var newest = batch.Events.Max(e => e.Sequence);

                // The cursor moves on EVERY batch received, delivered or not.
                // A batch that is refused but does not move the cursor is a
                // batch the control plane hands over again, forever, at the
                // speed of a long poll - and the events inside it were already
                // counted against the human once.
                var stale = newest <= cursor;
                cursor = Math.Max(cursor, newest);

                if (stale)
                {
                    // Loud, because the alternative to noticing this is a live
                    // view that looks broken from the outside: the human taps,
                    // nothing happens, and no line anywhere says why.
                    _logger.LogWarning(
                        "job {JobId}: dropping {Count} live input event(s) with nothing newer than {Cursor}; " +
                        "a re-delivered Enter is a second credential submission",
                        _jobId, batch.Events.Count, cursor);
                    continue;
                }

                if (!batch.IsWellFormed())
                {
                    _logger.LogWarning(
                        "job {JobId}: refusing a live input batch of {Count} event(s); it is not well formed",
                        _jobId, batch.Events.Count);
                    continue;
                }

                var size = Volatile.Read(ref _frameSize);
                var frame = ((int)(size >> 32), (int)(size & 0xFFFFFFFF));

                var dispatched = await LiveInputReplay
                    .ReplayAsync(batch.Events, frame, _surface, _logger, ct).ConfigureAwait(false);

                // Something moved, so the picture is about to be worth taking.
                if (dispatched > 0) Nudge();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Puts the shutter into its burst tier and wakes it now, so the first
    /// frame after a keystroke is 80 ms away rather than up to half a second.
    /// </summary>
    private void Nudge()
    {
        Volatile.Write(ref _burstUntilTicks, (_time.GetUtcNow() + _options.BurstWindow).UtcTicks);

        try
        {
            _nudge.Release();
        }
        catch (SemaphoreFullException)
        {
            // The shutter is already owed a wake-up. Banking a second one would
            // fire it after the human had stopped, at the cost of a frame.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Sleeps for one cadence interval, or until an event lands.
    ///
    /// The wait is on the semaphore rather than a timer so that the burst
    /// begins on the keystroke instead of at the end of whatever idle interval
    /// happened to be running. Wall clock and not the injected
    /// <see cref="TimeProvider"/>, deliberately: this is the cadence, a virtual
    /// clock cannot tell anyone what a frame costs, and the whole point of the
    /// exercise is a real number.
    /// </summary>
    private async Task WaitAsync(CancellationToken ct)
    {
        var burst = _time.GetUtcNow().UtcTicks < Volatile.Read(ref _burstUntilTicks);
        var delay = burst ? _options.BurstInterval : _options.IdleInterval;

        await _nudge.WaitAsync(delay, ct).ConfigureAwait(false);
    }

    private void ReportIfDue()
    {
        if (_meter.TryClose() is { } window) _logger.Report(_jobId, window);
    }

    private void OnFrameNavigated(object? sender, IFrame frame)
    {
        // Only the main frame. A login page's third-party frames navigate
        // constantly - that is what a captcha widget does all day - and
        // latching on those would stop every stream within a second.
        if (frame != _page.MainFrame) return;

        if (!IsAllowed(frame.Url)) Stop("the page navigated off the origin the stream opened on");
    }

    /// <summary>
    /// Whether a URL is one this stream may photograph.
    ///
    /// Exact host, never a suffix match. A rule that accepted anything ending
    /// in the allowed host accepts <c>login.ah.nl.example.com</c>, and a rule
    /// that accepted subdomains keeps streaming after a successful login has
    /// moved the browser to the account pages.
    /// </summary>
    private bool IsAllowed(string? url)
    {
        if (_origins.Count == 0) return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return false;

        return _origins.Contains(parsed.Host);
    }

    /// <summary>
    /// The hosts this stream may run on: what was declared, or - when nothing
    /// was - the one the page is already on.
    ///
    /// A wildcard is refused rather than expanded, and the refusal is an empty
    /// set, which stops the stream before its first frame. An adapter that
    /// declared <c>*.ah.nl</c> would keep streaming after the login succeeded,
    /// and the whole point of the latch is that it does not.
    /// </summary>
    private static HashSet<string> BuildOrigins(IReadOnlyList<string> declared, IPage page, ILogger logger)
    {
        var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var origin in declared)
        {
            if (string.IsNullOrWhiteSpace(origin)) continue;

            if (origin.Contains('*', StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "live view: refusing the wildcard origin '{Origin}'; a stream that follows a login " +
                    "wherever it goes is a remote view of an authenticated account",
                    origin);
                return [];
            }

            origins.Add(origin);
        }

        if (origins.Count > 0) return origins;

        if (Uri.TryCreate(page.Url, UriKind.Absolute, out var current) && !string.IsNullOrEmpty(current.Host))
        {
            origins.Add(current.Host);
            return origins;
        }

        logger.LogWarning(
            "live view: no origin was declared and the page is not on one, so nothing will be streamed");
        return [];
    }

    private double Fps() => 1000d / Math.Max(1, _options.BurstInterval.TotalMilliseconds);

    private static string Describe(HashSet<string> origins) =>
        origins.Count == 0 ? "(nothing)" : string.Join(", ", origins);
}
