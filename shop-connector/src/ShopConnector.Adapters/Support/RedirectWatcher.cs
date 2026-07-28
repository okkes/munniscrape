using Microsoft.Playwright;

namespace ShopConnector.Adapters.Support;

/// <summary>
/// The one signal that says a native-client OAuth login worked.
///
/// An interface for the same reason <see cref="ILoginPage"/> is one: the
/// waiting is the half of the login that decides whether a job finishes or
/// hangs, and it is unreachable offline while it is bolted to a live page's
/// events.
/// </summary>
internal interface IRedirectWaiter
{
    /// <summary>
    /// The captured URL, or null when the wait ran out. Null is not a
    /// failure: the caller still has to tell "still loading" apart from "a
    /// captcha is in the way", and only the page can answer that.
    /// </summary>
    Task<string?> WaitAsync(TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// Catches a redirect the browser is not allowed to follow.
///
/// A native OAuth client finishes its login by sending the browser to its own
/// scheme - <c>appie://login-exit</c>, <c>com.lidlplus.app://callback</c> -
/// which Chromium cannot navigate to. There is therefore no page load to
/// await, and which surface the attempt shows up on is a coin flip that
/// depends on the Chromium build: the 302's <c>Location</c> header, a request
/// that is created and then fails, a frame navigation, or nothing at all
/// except a change to the page's own URL. All four are watched, because the
/// entire login turns on this one event and there is no second chance to
/// observe it.
///
/// The listeners must exist <em>before</em> the form is submitted. A watcher
/// attached afterwards is a race against a redirect that frequently arrives
/// first.
/// </summary>
internal sealed class RedirectWatcher : IRedirectWaiter, IDisposable
{
    private const int PollMilliseconds = 250;

    private readonly IPage _page;
    private readonly string _prefix;
    private readonly TimeProvider _time;

    private readonly TaskCompletionSource<string> _captured =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RedirectWatcher(IPage page, string redirectUri, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(page);

        _page = page;
        _prefix = redirectUri;
        _time = time;

        _page.Response += OnResponse;
        _page.Request += OnRequest;
        _page.RequestFailed += OnRequest;
        _page.FrameNavigated += OnFrame;
    }

    public async Task<string?> WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = _time.GetUtcNow() + timeout;

        while (true)
        {
            if (_captured.Task.IsCompleted) return await _captured.Task.ConfigureAwait(false);

            try
            {
                // The last resort. On some builds a blocked scheme raises no
                // event at all and only the page's own URL moves.
                Offer(_page.Url);
            }
            catch (Exception ex) when (PageOps.IsSelectorMiss(ex))
            {
                // The page can close under us mid-redirect; the event
                // handlers stay the primary signal either way.
            }

            if (_captured.Task.IsCompleted) return await _captured.Task.ConfigureAwait(false);
            if (_time.GetUtcNow() >= deadline) return null;

            var poll = Task.Delay(TimeSpan.FromMilliseconds(PollMilliseconds), _time, ct);
            await Task.WhenAny(_captured.Task, poll).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _page.Response -= OnResponse;
        _page.Request -= OnRequest;
        _page.RequestFailed -= OnRequest;
        _page.FrameNavigated -= OnFrame;
    }

    /// <summary>
    /// The most dependable of the four: the header is readable whether or not
    /// Chromium then refuses to act on it.
    /// </summary>
    private void OnResponse(object? sender, IResponse response)
    {
        try
        {
            foreach (var header in response.Headers)
            {
                if (string.Equals(header.Key, "location", StringComparison.OrdinalIgnoreCase))
                {
                    Offer(header.Value);
                }
            }
        }
        catch (Exception ex) when (PageOps.IsSelectorMiss(ex))
        {
            // A response whose headers are already gone tells us nothing, and
            // throwing out of an event handler would take the dispatch loop
            // with it.
        }
    }

    private void OnRequest(object? sender, IRequest request) => Offer(request.Url);

    private void OnFrame(object? sender, IFrame frame) => Offer(frame.Url);

    private void Offer(string? url)
    {
        if (url is not null && url.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase))
        {
            _captured.TrySetResult(url);
        }
    }
}
