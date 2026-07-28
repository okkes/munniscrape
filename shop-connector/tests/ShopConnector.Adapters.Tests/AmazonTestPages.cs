using System.Text.Json;
using Connector.Kit.Adapters;
using Connector.Kit.Challenges;
using ShopConnector.Adapters.Amazon;
using ShopConnector.Adapters.Fixtures;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.Tests;

/// <summary>
/// A recorded amazon.nl page.
///
/// The fixtures for this provider are page captures rather than API payloads,
/// because amazon.nl has no API: <c>{ url, status, html }</c>, with the markup
/// stored one line per array entry so a recorded page still reads as HTML in a
/// diff. The status is kept alongside the body deliberately - on this provider
/// a 200 can be a bot wall and a 503 can be one too, and the two need
/// different answers.
/// </summary>
internal static class AmazonFixture
{
    public static AmazonPage Page(string name)
    {
        using var document = JsonDocument.Parse(FixtureCatalog.Read($"amazon/{name}.json"));
        var root = document.RootElement;

        var html = string.Join(
            '\n', root.GetProperty("html").EnumerateArray().Select(line => line.GetString()));

        return new AmazonPage(
            root.GetProperty("url").GetString()!,
            root.GetProperty("status").GetInt32(),
            html);
    }
}

/// <summary>
/// amazon.nl, answering from its recorded pages.
///
/// A URL the test did not map is an assertion failure rather than an empty
/// page: the walk deciding to fetch somewhere unexpected - a year nobody asked
/// for, a page past the end - is exactly the bug this stands in for.
/// </summary>
internal sealed class StubAmazonPages : IAmazonPages
{
    private readonly Dictionary<string, string> _map = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<string>> _once = new(StringComparer.Ordinal);
    private readonly List<string> _opened = [];

    /// <summary>Every URL the adapter asked for, in order.</summary>
    public IReadOnlyList<string> Opened => _opened;

    public string this[string url]
    {
        set => _map[url] = value;
    }

    public void Replace(string url, string fixture) => _map[url] = fixture;

    /// <summary>
    /// Serves <paramref name="fixture"/> for the next request to
    /// <paramref name="url"/> only, and the mapped page after that. How a wall
    /// behaves: it stands once, and the page is there when it comes down.
    /// </summary>
    public void Once(string url, string fixture)
    {
        if (!_once.TryGetValue(url, out var queue))
        {
            queue = new Queue<string>();
            _once[url] = queue;
        }

        queue.Enqueue(fixture);
    }

    public Task<AmazonPage> OpenAsync(string url, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _opened.Add(url);

        if (_once.TryGetValue(url, out var queue) && queue.Count > 0)
        {
            return Task.FromResult(AmazonFixture.Page(queue.Dequeue()));
        }

        if (!_map.TryGetValue(url, out var fixture))
        {
            throw new InvalidOperationException(
                $"the adapter opened '{url}', which this test did not record; " +
                $"mapped urls are [{string.Join(", ", _map.Keys)}]");
        }

        // The capture keeps the URL it was RECORDED at, not the one that was
        // asked for. That is the point: a fetch of the order list that comes
        // back from /ap/signin is how a dead session presents, and a stub that
        // echoed the request would hide it.
        return Task.FromResult(AmazonFixture.Page(fixture));
    }
}

/// <summary>
/// Amazon's sign-in page as a fixture.
///
/// The shared <c>StubLoginPage</c> covers a form whose shape does not change;
/// Amazon's does. It asks for the e-mail address, and only then - on a
/// different screen - for the password, so a stub that shows both from the
/// start cannot tell the two-screen path from the one-screen path, and the
/// ordering that matters most (the credential is latched BEFORE the first
/// button that sends anything upstream) is unreachable.
/// </summary>
internal sealed class AmazonStubPage : ILoginPage
{
    public const string OrderList = "https://www.amazon.nl/your-orders/orders?timeFilter=year-2026&startIndex=0";

    public static readonly CropRegion Box = new(20, 40, 320, 90);

    private readonly HashSet<string> _present;
    private readonly Dictionary<string, string[]> _reveals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _signInOnClick = new(StringComparer.Ordinal);
    private string? _signInOnAnswer;
    private readonly List<string> _clicked = [];
    private readonly List<string> _calls = [];
    private readonly Dictionary<string, string> _filled = new(StringComparer.Ordinal);
    private readonly List<string> _visited = [];

    private AmazonStubPage(HashSet<string> present) => _present = present;

    public string Url { get; set; } = "https://www.amazon.nl/ap/signin";

    /// <summary>True until the fields are cleared: the password is still in the DOM.</summary>
    public bool HoldsSecret { get; private set; } = true;

    public IReadOnlyList<string> Visited => _visited;

    public IReadOnlyList<string> Clicked => _clicked;

    public IReadOnlyList<string> Calls => _calls;

    public IReadOnlyDictionary<string, string> Filled => _filled;

    public string? Answered { get; private set; }

    public static AmazonStubPage Showing(params string[] selectors) =>
        new(new HashSet<string>(selectors, StringComparer.Ordinal));

    /// <summary>
    /// The second screen: these selectors do not exist until
    /// <paramref name="trigger"/> has been clicked.
    /// </summary>
    public AmazonStubPage RevealingAfter(string trigger, params string[] selectors)
    {
        _reveals[trigger] = selectors;
        return this;
    }

    /// <summary>
    /// Clicking <paramref name="trigger"/> lands the browser on the order
    /// list: the URL changes and the whole sign-in form is gone. That pair is
    /// the only thing the adapter treats as "we are in", so a stub that moved
    /// one without the other would let a half-finished login pass.
    /// </summary>
    public AmazonStubPage SignedInAfter(string trigger, string url = OrderList)
    {
        _signInOnClick[trigger] = url;
        return this;
    }

    /// <summary>The same, for a captcha whose answer submits the form.</summary>
    public AmazonStubPage SignedInAfterAnswer(string url = OrderList)
    {
        _signInOnAnswer = url;
        return this;
    }

    /// <summary>
    /// The human acted somewhere we cannot observe - they passed a widget in
    /// the window in front of them - and the provider carried on.
    /// </summary>
    public void SignIn(string url = OrderList)
    {
        _present.Clear();
        Url = url;
    }

    public Task GotoAsync(string url, CancellationToken ct)
    {
        _visited.Add(url);
        return Task.CompletedTask;
    }

    public Task ClearSecretsAsync(CancellationToken ct)
    {
        _calls.Add("clear-secrets");
        HoldsSecret = false;
        return Task.CompletedTask;
    }

    public Task<PageMatch?> FindAsync(IReadOnlyList<string> selectors, int timeoutMs, CancellationToken ct) =>
        Task.FromResult<PageMatch?>(Match(selectors) is null ? null : new PageMatch(Box));

    public Task<bool> FillAsync(IReadOnlyList<string> selectors, string value, int timeoutMs, CancellationToken ct)
    {
        if (Match(selectors) is not { } hit) return Task.FromResult(false);

        _calls.Add("fill");
        _filled[hit] = value;
        return Task.FromResult(true);
    }

    public Task<bool> ClickAsync(IReadOnlyList<string> selectors, int timeoutMs, CancellationToken ct)
    {
        if (Match(selectors) is not { } hit) return Task.FromResult(false);

        _calls.Add("click");
        _clicked.Add(hit);

        if (_reveals.TryGetValue(hit, out var revealed))
        {
            foreach (var selector in revealed) _present.Add(selector);
        }

        if (_signInOnClick.TryGetValue(hit, out var url)) SignIn(url);

        return Task.FromResult(true);
    }

    public Task<bool> AnswerAsync(IReadOnlyList<string> selectors, string value, int timeoutMs, CancellationToken ct)
    {
        if (Match(selectors) is null) return Task.FromResult(false);

        _calls.Add("answer");
        Answered = value;

        if (_signInOnAnswer is { } url) SignIn(url);

        return Task.FromResult(true);
    }

    private string? Match(IReadOnlyList<string> selectors) => selectors.FirstOrDefault(_present.Contains);
}

/// <summary>
/// A browser lease that keeps the redactor's rule: a page still holding a
/// secret produces no bytes at all, exactly as the agent's screenshot redactor
/// does. A test that gets a picture back has therefore proved the password
/// left the DOM first.
/// </summary>
internal sealed class AmazonStubBrowser : IBrowserLease
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47];

    private readonly AmazonStubPage _page;

    public AmazonStubBrowser(AmazonStubPage page) => _page = page;

    public bool Started => true;

    public int Captures { get; private set; }

    public CropRegion? LastCrop { get; private set; }

    public Task<byte[]> ScreenshotAsync(CropRegion? crop, CancellationToken ct)
    {
        Captures++;
        LastCrop = crop;

        return Task.FromResult(_page.HoldsSecret ? Array.Empty<byte>() : Png);
    }

    public Task<Microsoft.Playwright.IPage> PageAsync(CancellationToken ct) => throw Refused();

    public Task<string> StorageStateAsync(CancellationToken ct) => throw Refused();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static InvalidOperationException Refused() =>
        new("this test drives the page through the adapter's seam and must not start a browser");
}
