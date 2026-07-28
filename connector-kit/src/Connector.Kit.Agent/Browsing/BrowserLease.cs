using Connector.Kit.Adapters;
using Connector.Kit.Challenges;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace Connector.Kit.Agent.Browsing;

/// <summary>
/// A browser that is only launched if an adapter actually asks for one.
///
/// The laziness is not an optimisation. A T1 adapter runs on an agent that has
/// browser binaries installed, and starting Chromium for a run that makes six
/// HTTP calls would burn seconds and hundreds of megabytes per job for
/// nothing. <see cref="Started"/> is also what the job runner keys artifact
/// capture off, so a pure-HTTP failure never tries to photograph a page that
/// does not exist.
/// </summary>
public sealed class BrowserLease : IChallengeSurface
{
    /// <summary>A valid, empty Playwright storage state.</summary>
    private const string EmptyStorageState = "{\"cookies\":[],\"origins\":[]}";

    /// <summary>
    /// How long measuring an element may take. Short on purpose: the element
    /// is one the caller has already found, so this is a re-measure and not a
    /// search, and a challenge that is not on screen now will not be in ten
    /// seconds either.
    /// </summary>
    private const float MeasureTimeoutMs = 5_000;

    private readonly BrowserLeaseOptions _options;
    private readonly ScreenshotRedactor _redactor;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _launchLock = new(1, 1);

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _page;
    private int _disposed;

    public BrowserLease(
        BrowserLeaseOptions options, ScreenshotRedactor redactor, ILogger logger, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(redactor);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _redactor = redactor;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public bool Started => Volatile.Read(ref _page) is not null;

    /// <summary>True when this lease is driving a persistent profile (T4).</summary>
    public bool IsPersistent => _options.ProfileDirectory is not null;

    public async Task<IPage> PageAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _page) is { } existing) return existing;

        await _launchLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            if (_page is not null) return _page;

            _playwright = await Playwright.CreateAsync().ConfigureAwait(false);
            _context = _options.ProfileDirectory is { } profile
                ? await LaunchPersistentAsync(_playwright, profile).ConfigureAwait(false)
                : await LaunchEphemeralAsync(_playwright).ConfigureAwait(false);

            _context.SetDefaultTimeout(_options.OperationTimeoutMs);
            _context.SetDefaultNavigationTimeout(_options.NavigationTimeoutMs);

            // A persistent context opens with a page already; an ephemeral one
            // does not.
            var page = _context.Pages.Count > 0
                ? _context.Pages[0]
                : await _context.NewPageAsync().ConfigureAwait(false);

            Volatile.Write(ref _page, page);
            return page;
        }
        finally
        {
            _launchLock.Release();
        }
    }

    /// <summary>
    /// The cookies and local storage to seal into the bundle - the cheap 80%
    /// win of session reuse.
    ///
    /// Returns an empty state for a persistent profile, deliberately. For a T4
    /// provider the bundle is only <c>{agent_id, profile_id}</c> and carries no
    /// secret at all; exporting the profile's cookies here would put the
    /// credential this design keeps in the user's house on the wire.
    /// </summary>
    public async Task<string> StorageStateAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (IsPersistent)
        {
            _logger.LogWarning(
                "storage state was requested for a persistent profile and refused; " +
                "a browser_persistent session must stay on this machine");
            return EmptyStorageState;
        }

        if (_context is null) return EmptyStorageState;

        return await _context.StorageStateAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// A redacted screenshot, or an empty array when the page cannot be
    /// verified safe. See <see cref="ScreenshotRedactor"/>.
    /// </summary>
    public async Task<byte[]> ScreenshotAsync(CropRegion? crop, CancellationToken ct)
    {
        var page = Volatile.Read(ref _page);
        if (page is null) return [];

        return await _redactor.CaptureAsync(page, crop, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Photographs exactly this box and hands back the box with the picture.
    ///
    /// The same redactor governs, unchanged: it refuses to photograph a page
    /// that still holds a secret, and a captcha is not an exception to that. It
    /// is why the Albert Heijn password is cleared out of the DOM before any
    /// capture - the fix for a refusal is to stop holding the secret, never to
    /// soften the refusal. A refusal arrives here as no bytes and becomes
    /// <see cref="RegionCapture.Refused"/>: no image, and therefore nothing a
    /// tap could later be measured against.
    /// </summary>
    public async Task<RegionCapture> CaptureRegionAsync(CropRegion region, CancellationToken ct)
    {
        var page = Volatile.Read(ref _page);
        if (page is null) return RegionCapture.Refused;

        return await ClipAsync(page, region, "the requested box", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Photographs exactly this element - found, scrolled into view, measured,
    /// and clipped to what it occupies.
    /// </summary>
    public async Task<RegionCapture> CaptureElementAsync(string selector, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);

        var page = Volatile.Read(ref _page);
        if (page is null) return RegionCapture.Refused;

        var element = page.Locator(selector);

        CropRegion? box;
        try
        {
            await element.ScrollIntoViewIfNeededAsync(
                new LocatorScrollIntoViewIfNeededOptions { Timeout = MeasureTimeoutMs }).ConfigureAwait(false);

            var measured = await element.BoundingBoxAsync(
                new LocatorBoundingBoxOptions { Timeout = MeasureTimeoutMs }).ConfigureAwait(false);

            box = measured is null ? null : RegionCapture.Enclosing(measured.X, measured.Y, measured.Width, measured.Height);
        }
        catch (Exception ex) when (IsMeasureMiss(ex))
        {
            _logger.LogWarning(ex, "no capture: '{Selector}' could not be measured", selector);
            return RegionCapture.Refused;
        }

        return await ClipAsync(page, box, $"'{selector}'", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Photographs exactly this frame, through the <c>&lt;iframe&gt;</c>
    /// element that hosts it.
    ///
    /// The case the whole feature exists for. A cross-origin captcha frame can
    /// be found by its URL among <c>page.Frames</c> when no selector of ours
    /// names the element around it, and its contents cannot be reached at all -
    /// so the frame is photographed from the outside, as a box on the page.
    /// </summary>
    public async Task<RegionCapture> CaptureFrameAsync(IFrame frame, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var page = Volatile.Read(ref _page);
        if (page is null) return RegionCapture.Refused;

        CropRegion? box;
        try
        {
            var host = await frame.FrameElementAsync().ConfigureAwait(false);
            await host.ScrollIntoViewIfNeededAsync(
                new ElementHandleScrollIntoViewIfNeededOptions { Timeout = MeasureTimeoutMs }).ConfigureAwait(false);

            var measured = await host.BoundingBoxAsync().ConfigureAwait(false);
            box = measured is null ? null : RegionCapture.Enclosing(measured.X, measured.Y, measured.Width, measured.Height);
        }
        catch (Exception ex) when (IsMeasureMiss(ex))
        {
            // A frame that detached while we were measuring it is the normal
            // way a captcha ends - including the way it ends when the human
            // has already passed it.
            _logger.LogWarning(ex, "no capture: the frame {Url} could not be measured", Describe(frame));
            return RegionCapture.Refused;
        }

        return await ClipAsync(page, box, $"the frame {Describe(frame)}", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Clicks where the human tapped, in their order, and reports how many
    /// clicks were dispatched.
    ///
    /// Zero means nothing was clicked, which is what every refusal returns -
    /// including a lease with no browser at all. See
    /// <see cref="TapReplay.ReplayAsync"/> for the rest of the rules.
    /// </summary>
    public async Task<int> ReplayTapsAsync(IReadOnlyList<Tap> taps, RegionCapture capture, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(taps);

        var page = Volatile.Read(ref _page);
        if (page is null) return 0;

        // Null when the context runs at the window's own size - a headed agent,
        // typically. There is then no viewport to check the box against, and
        // inventing one would refuse taps that are perfectly reachable.
        var viewport = page.ViewportSize is { } size ? (size.Width, size.Height) : ((int, int)?)null;

        return await TapReplay.ReplayAsync(
            taps,
            capture,
            viewport,
            new MousePointer(page.Mouse, _time),
            _options.TapGap,
            _logger,
            ct).ConfigureAwait(false);
    }

    /// <summary>The page's shape as a hash, for a failure artifact.</summary>
    public async Task<string?> DomDigestAsync(CancellationToken ct)
    {
        var page = Volatile.Read(ref _page);
        if (page is null) return null;

        return await _redactor.DomDigestAsync(page, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The one place a region capture happens, so the box that is clipped is
    /// necessarily the box that comes back - which is the entire basis of the
    /// coordinate mapping, and not a property worth re-deriving per overload.
    /// Anything the redactor declines becomes <see cref="RegionCapture.Refused"/>
    /// rather than an empty picture.
    /// </summary>
    private async Task<RegionCapture> ClipAsync(IPage page, CropRegion? box, string what, CancellationToken ct)
    {
        if (box is not { } region)
        {
            _logger.LogWarning("no capture: {What} occupies nothing photographable", what);
            return RegionCapture.Refused;
        }

        var png = await _redactor.CaptureAsync(page, region, ct).ConfigureAwait(false);
        if (png.Length == 0) return RegionCapture.Refused;

        return RegionCapture.Of(png, region);
    }

    /// <summary>
    /// "That element is not there", whatever Playwright chose to call it.
    ///
    /// Playwright for .NET raises <see cref="TimeoutException"/> - NOT a
    /// <see cref="PlaywrightException"/> - when a wait expires, so catching
    /// only the latter lets a challenge that simply moved take the whole login
    /// down instead of returning no image.
    /// </summary>
    private static bool IsMeasureMiss(Exception ex) => ex is PlaywrightException or TimeoutException;

    private static string Describe(IFrame frame) => string.IsNullOrEmpty(frame.Url) ? "<about:blank>" : frame.Url;

    private async Task<IBrowserContext> LaunchPersistentAsync(IPlaywright playwright, string profileDirectory)
    {
        Directory.CreateDirectory(profileDirectory);

        if (_options.StorageState is not null)
        {
            // The profile IS the state. Layering a bundle's storage state on
            // top would resurrect a session the profile has since replaced.
            _logger.LogDebug("ignoring the bundle's storage state: this lease drives a persistent profile");
        }

        var launch = new BrowserTypeLaunchPersistentContextOptions
        {
            Headless = _options.Headless,
            Args = _options.Args.Count > 0 ? _options.Args : null,
            AcceptDownloads = true,
            DownloadsPath = _options.DownloadsPath,
            Locale = _options.Locale,
            TimezoneId = _options.TimezoneId,
        };

        if (Device(playwright) is { } device)
        {
            launch.UserAgent = device.UserAgent;
            launch.ViewportSize = device.ViewportSize;
            launch.DeviceScaleFactor = device.DeviceScaleFactor;
            launch.IsMobile = device.IsMobile;
            launch.HasTouch = device.HasTouch;
        }

        _logger.LogInformation("launching a persistent browser profile at {Directory}", profileDirectory);
        return await playwright.Chromium.LaunchPersistentContextAsync(profileDirectory, launch).ConfigureAwait(false);
    }

    private async Task<IBrowserContext> LaunchEphemeralAsync(IPlaywright playwright)
    {
        _browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = _options.Headless,
            Args = _options.Args.Count > 0 ? _options.Args : null,
            DownloadsPath = _options.DownloadsPath,
        }).ConfigureAwait(false);

        var context = Device(playwright) is { } device
            ? new BrowserNewContextOptions(device)
            : new BrowserNewContextOptions();

        context.AcceptDownloads = true;
        context.StorageState = _options.StorageState;
        if (_options.Locale is not null) context.Locale = _options.Locale;
        if (_options.TimezoneId is not null) context.TimezoneId = _options.TimezoneId;

        _logger.LogInformation(
            "launching a browser (session reuse: {Reuse})", _options.StorageState is null ? "no" : "yes");

        return await _browser.NewContextAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// An honest device descriptor, or null for plain desktop Chromium.
    ///
    /// These are real strings from real browsers on real hardware, chosen when
    /// a provider serves a different site to a phone - not a disguise. Nothing
    /// else about the fingerprint is touched: no <c>navigator.webdriver</c>
    /// patching, no canvas noise, no stealth plugin. Crossing that line costs
    /// the whole quarantine argument in front of anyone who asks.
    /// </summary>
    private BrowserNewContextOptions? Device(IPlaywright playwright)
    {
        if (_options.DeviceName is not { } name) return null;

        if (playwright.Devices.TryGetValue(name, out var device)) return device;

        _logger.LogWarning("unknown playwright device '{Device}'; falling back to desktop chromium", name);
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        // Teardown never throws over a job's real outcome: a browser that
        // failed to close cleanly must not turn a successful fetch into a
        // failure, nor mask the exception that is already on its way up.
        await Quietly(async () =>
        {
            if (_context is not null) await _context.CloseAsync().ConfigureAwait(false);
        }, "closing the browser context").ConfigureAwait(false);

        await Quietly(async () =>
        {
            if (_browser is not null) await _browser.CloseAsync().ConfigureAwait(false);
        }, "closing the browser").ConfigureAwait(false);

        try
        {
            _playwright?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "disposing playwright failed");
        }

        _launchLock.Dispose();
        Volatile.Write(ref _page, null);
    }

    private async Task Quietly(Func<Task> action, string what)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{What} failed", what);
        }
    }
}

/// <summary>
/// Everything the job runner knows about how this job's browser should be
/// shaped. Built per job, never shared.
/// </summary>
public sealed record BrowserLeaseOptions
{
    public bool Headless { get; init; } = true;

    /// <summary>A storage state from the session material. Null on a first login.</summary>
    public string? StorageState { get; init; }

    /// <summary>T4 only: the profile directory this job must drive.</summary>
    public string? ProfileDirectory { get; init; }

    /// <summary>An honest Playwright device descriptor name, e.g. <c>Pixel 5</c>.</summary>
    public string? DeviceName { get; init; }

    /// <summary>
    /// The per-job work directory. Downloads land here so a statement a
    /// provider hands us is deleted with the job rather than left on disk.
    /// </summary>
    public string? DownloadsPath { get; init; }

    public string? Locale { get; init; }

    public string? TimezoneId { get; init; }

    /// <summary>
    /// The gap between two replayed taps. See <see cref="TapReplay.DefaultGap"/>
    /// for why there is one at all.
    /// </summary>
    public TimeSpan TapGap { get; init; } = TapReplay.DefaultGap;

    public float NavigationTimeoutMs { get; init; } = 45_000;

    public float OperationTimeoutMs { get; init; } = 30_000;

    public IReadOnlyList<string> Args { get; init; } = [];
}
