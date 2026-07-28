using Connector.Kit.Adapters;
using Connector.Kit.Challenges;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.Tests.Support;

/// <summary>
/// A provider's login page, as a fixture.
///
/// A test says which of the option lists' candidates are on the page, so the
/// selectors an operator can edit are the same ones the behaviour is asserted
/// through. Every call that touches the page is recorded, because two of the
/// fixes this suite guards are orderings: the password leaves the DOM before
/// anything photographs it, and a username goes into the box its own shape
/// belongs in or into none at all.
///
/// Shared by Albert Heijn and Lidl Plus for the same reason
/// <see cref="ILoginPage"/> is: one stub that behaves like a page beats two
/// that behave like each adapter's expectations of one.
/// </summary>
internal sealed class StubLoginPage : ILoginPage
{
    /// <summary>The box a matched element reports, for the redactor's crop.</summary>
    public static readonly CropRegion Box = new(12, 34, 300, 80);

    private readonly HashSet<string> _present;
    private readonly List<string> _calls = [];
    private readonly List<string> _visited = [];
    private readonly List<string> _clicked = [];
    private readonly Dictionary<string, string> _filled = new(StringComparer.Ordinal);

    private StubLoginPage(HashSet<string> present) => _present = present;

    public string Url { get; init; } = "https://login.example.test/login";

    /// <summary>
    /// The page changing under the adapter, which is what a widget escalating
    /// looks like from here: the tick lands and a grid that was in nobody's
    /// markup a moment ago is suddenly on the page.
    ///
    /// Settable rather than init-only because <see cref="Showing"/> is the way
    /// a page is built, and the handler wants to <see cref="Reveal"/> on the
    /// very page it is being attached to.
    /// </summary>
    public Action<StubLoginPage, string>? WhenClicked { get; set; }

    /// <summary>
    /// True until the fields are cleared: the form was filled and submitted,
    /// so the password is still sitting in the DOM.
    /// </summary>
    public bool HoldsSecret { get; private set; } = true;

    public IReadOnlyList<string> Calls => _calls;

    /// <summary>Every URL the adapter navigated to, in order. The authorize call is the first.</summary>
    public IReadOnlyList<string> Visited => _visited;

    /// <summary>What went into which input, keyed by the candidate that matched.</summary>
    public IReadOnlyDictionary<string, string> Filled => _filled;

    public IReadOnlyList<string> Clicked => _clicked;

    /// <summary>What the human typed into the captcha's own box, if anything.</summary>
    public string? Answered { get; private set; }

    public static StubLoginPage Showing(params string[] selectors) =>
        new(new HashSet<string>(selectors, StringComparer.Ordinal));

    public void Record(string call) => _calls.Add(call);

    /// <summary>Puts a selector on the page.</summary>
    public void Reveal(params string[] selectors) => _present.UnionWith(selectors);

    /// <summary>Takes one off it.</summary>
    public void Hide(params string[] selectors) => _present.ExceptWith(selectors);

    public Task GotoAsync(string url, CancellationToken ct)
    {
        _visited.Add(url);
        return Task.CompletedTask;
    }

    public Task ClearSecretsAsync(CancellationToken ct)
    {
        Record("clear-secrets");
        HoldsSecret = false;
        return Task.CompletedTask;
    }

    public Task<PageMatch?> FindAsync(IReadOnlyList<string> selectors, int timeoutMs, CancellationToken ct) =>
        Task.FromResult<PageMatch?>(Match(selectors) is null ? null : new PageMatch(Box));

    /// <summary>
    /// Recorded, never simulated. Whether a real frame has finished drawing is
    /// a question only a browser answers, and a stub that pretended to know
    /// would be pinning its own guess. What the offline suite CAN pin is that
    /// the gate asks before it photographs, and in that order.
    /// </summary>
    public Task SettleAsync(IReadOnlyList<string> selectors, TimeSpan budget, CancellationToken ct)
    {
        Record("settle");
        return Task.CompletedTask;
    }

    public Task<bool> FillAsync(
        IReadOnlyList<string> selectors, string value, int timeoutMs, CancellationToken ct)
    {
        if (Match(selectors) is not { } hit) return Task.FromResult(false);

        Record("fill");
        _filled[hit] = value;
        return Task.FromResult(true);
    }

    public Task<bool> ClickAsync(IReadOnlyList<string> selectors, int timeoutMs, CancellationToken ct)
    {
        if (Match(selectors) is not { } hit) return Task.FromResult(false);

        Record("click");
        _clicked.Add(hit);
        WhenClicked?.Invoke(this, hit);
        return Task.FromResult(true);
    }

    public Task<bool> AnswerAsync(
        IReadOnlyList<string> selectors, string value, int timeoutMs, CancellationToken ct)
    {
        if (Match(selectors) is null) return Task.FromResult(false);

        Record("answer");
        Answered = value;
        return Task.FromResult(true);
    }

    /// <summary>
    /// The first candidate that is on the page - the same most-specific-first
    /// order the real helper resolves in, so a test that puts two of a list's
    /// candidates on the page learns which one an adapter actually reaches.
    /// </summary>
    private string? Match(IReadOnlyList<string> selectors) => selectors.FirstOrDefault(_present.Contains);
}

/// <summary>
/// The redirect, arriving on the pass a test says it does - or never, which
/// is what a wall nobody can pass looks like from here.
/// </summary>
internal sealed class StubRedirectWaiter : IRedirectWaiter
{
    private readonly string? _redirect;
    private readonly int _afterWaits;

    public StubRedirectWaiter(string? redirect, int afterWaits)
    {
        _redirect = redirect;
        _afterWaits = afterWaits;
    }

    /// <summary>How many polls the login cost. The live AH hang was 300s of these.</summary>
    public int Waits { get; private set; }

    public Task<string?> WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        Waits++;
        return Task.FromResult(Waits > _afterWaits ? _redirect : null);
    }
}

/// <summary>
/// A browser lease that keeps the redactor's rule.
///
/// The refusal is reproduced rather than assumed: a page still holding a
/// secret produces no bytes at all, exactly as <c>ScreenshotRedactor</c> does
/// on the agent. A test that gets a picture back has therefore proved the
/// password was gone first.
/// </summary>
internal sealed class StubBrowserLease : IBrowserLease
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47];

    private readonly StubLoginPage _page;

    public StubBrowserLease(StubLoginPage page) => _page = page;

    public bool Started => true;

    public int Captures { get; private set; }

    public CropRegion? LastCrop { get; private set; }

    /// <summary>
    /// The redactor's other refusal: it declines whatever it cannot verify,
    /// not only a page still holding a secret. A relay with no bytes has
    /// nothing to show anybody, and this is how a test says so without
    /// pretending the password is still in the form.
    /// </summary>
    public bool Refuses { get; init; }

    public Task<byte[]> ScreenshotAsync(CropRegion? crop, CancellationToken ct)
    {
        Captures++;
        LastCrop = crop;
        _page.Record("screenshot");

        return Task.FromResult(Refuses || _page.HoldsSecret ? Array.Empty<byte>() : Png);
    }

    public Task<Microsoft.Playwright.IPage> PageAsync(CancellationToken ct) => throw Refused();

    public Task<string> StorageStateAsync(CancellationToken ct) => throw Refused();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static InvalidOperationException Refused() =>
        new("this test drives the page through the adapter's seam and must not start a browser");
}

/// <summary>
/// Where a replayed tap lands, recorded rather than dispatched.
///
/// The live surface is the browser's own mouse, which is the one part of the
/// relay no fixture can stand in for - so what is asserted here is everything
/// up to it: that a click was asked for at all, and at which page pixel. Every
/// point is kept, in order, because the order is part of the answer.
/// </summary>
internal sealed class StubTapSurface : ITapSurface
{
    private readonly StubLoginPage? _page;
    private readonly List<(double X, double Y)> _points = [];

    public StubTapSurface(StubLoginPage? page = null) => _page = page;

    /// <summary>How many times taps were replayed - one per answered grid.</summary>
    public int Dispatches { get; private set; }

    /// <summary>Every point clicked, in page pixels, across every round.</summary>
    public IReadOnlyList<(double X, double Y)> Points => _points;

    public Task TapAsync(IReadOnlyList<(double X, double Y)> points, CancellationToken ct)
    {
        Dispatches++;
        _points.AddRange(points);

        // Into the page's own log, so the one ordering that matters - the
        // password is gone before the picture is taken, and the picture is
        // taken before anything is clicked - is asserted as a sequence.
        _page?.Record("tap");

        return Task.CompletedTask;
    }
}
