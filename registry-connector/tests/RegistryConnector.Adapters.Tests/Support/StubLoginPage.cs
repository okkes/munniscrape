using Connector.Kit.Browsing;

namespace RegistryConnector.Adapters.Tests;

/// <summary>
/// A sign-in page with only the boxes it was told it has.
///
/// The point is the WIZARD. BKR asks for the e-mail and the password on two
/// screens, so the password box genuinely does not exist until the first
/// button is clicked - and an adapter that filled both up front looked
/// perfectly correct offline against a stub that showed everything at once.
/// This one reveals the second screen only when the button is pressed, which
/// is what the live page does and what the first version failed against.
/// </summary>
internal sealed class StubLoginPage : ILoginPage
{
    private readonly HashSet<string> _present;
    private readonly Dictionary<string, string> _filled = new(StringComparer.Ordinal);
    private readonly List<string> _clicked = [];
    private readonly List<string> _visited = [];

    private StubLoginPage(HashSet<string> present) => _present = present;

    public string Url { get; set; } = "https://login.mijnkredietregistratie.nl/";

    /// <summary>Called on every click, so a test can advance the wizard.</summary>
    public Action<StubLoginPage, string>? WhenClicked { get; set; }

    /// <summary>True until the page is cleared for a photograph.</summary>
    public bool HoldsSecret { get; private set; } = true;

    public IReadOnlyDictionary<string, string> Filled => _filled;

    public IReadOnlyList<string> Clicked => _clicked;

    public IReadOnlyList<string> Visited => _visited;

    public static StubLoginPage Showing(params string[] selectors) =>
        new([.. selectors]);

    public void Reveal(params string[] selectors) => _present.UnionWith(selectors);

    public void Hide(params string[] selectors) => _present.ExceptWith(selectors);

    public Task GotoAsync(string url, CancellationToken ct)
    {
        _visited.Add(url);
        return Task.CompletedTask;
    }

    public Task ClearSecretsAsync(CancellationToken ct)
    {
        HoldsSecret = false;
        return Task.CompletedTask;
    }

    public Task<PageMatch?> FindAsync(IReadOnlyList<string> selectors, int timeoutMs, CancellationToken ct) =>
        Task.FromResult<PageMatch?>(Match(selectors) is null ? null : new PageMatch(null));

    public Task<bool> FillAsync(IReadOnlyList<string> selectors, string value, int timeoutMs, CancellationToken ct)
    {
        if (Match(selectors) is not { } hit) return Task.FromResult(false);

        _filled[hit] = value;
        return Task.FromResult(true);
    }

    public Task<bool> ClickAsync(IReadOnlyList<string> selectors, int timeoutMs, CancellationToken ct)
    {
        if (Match(selectors) is not { } hit) return Task.FromResult(false);

        _clicked.Add(hit);
        WhenClicked?.Invoke(this, hit);

        return Task.FromResult(true);
    }

    public Task<bool> AnswerAsync(IReadOnlyList<string> selectors, string value, int timeoutMs, CancellationToken ct) =>
        FillAsync(selectors, value, timeoutMs, ct);

    private string? Match(IReadOnlyList<string> selectors)
    {
        foreach (var selector in selectors)
        {
            if (_present.Contains(selector)) return selector;
        }

        return null;
    }
}

/// <summary>Reports the sign-in as landed after a stated number of checks.</summary>
internal sealed class StubSignedIn(string? url, int afterWaits) : IRedirectWaiter
{
    private int _waits;

    public int Waits => _waits;

    public Task<string?> WaitAsync(TimeSpan patience, CancellationToken ct) =>
        Task.FromResult(_waits++ >= afterWaits ? url : null);
}
