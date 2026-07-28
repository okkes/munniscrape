using Connector.Kit.Challenges;
using Connector.Kit.Manifests;
using Microsoft.Playwright;

namespace ShopConnector.Adapters.Support;

/// <summary>An element that matched, and the box the redactor may crop to.</summary>
internal sealed record PageMatch(CropRegion? Crop);

/// <summary>
/// The seam.
///
/// Everything a login page does after the form is submitted - a captcha, a
/// stated error, silence - happens on a page no fixture can produce, so the
/// decisions taken there were until now only reachable through a live
/// Chromium and a real account. This is the whole surface those decisions
/// need, which puts them in the offline suite: whether a wall is a widget or
/// a picture, whether to ask a human or refuse, which of two inputs a
/// username belongs in, and clearing the password out of the DOM before
/// anything photographs it.
///
/// Shared rather than per-provider: Albert Heijn and Lidl Plus meet the same
/// class of wall behind the same kind of markup, and two copies of this would
/// be two places for the redactor's rule to drift out of agreement with
/// itself. <see cref="PlaywrightLoginPage"/> is the only part a live run
/// exercises that a test cannot.
/// </summary>
internal interface ILoginPage
{
    /// <summary>The page's current URL. Quoted in diagnostics, never as a credential.</summary>
    string Url { get; }

    /// <summary>Navigates. Behind the seam so a whole login is drivable offline.</summary>
    Task GotoAsync(string url, CancellationToken ct);

    /// <summary>
    /// Blanks every input a submitted credential can still be sitting in.
    ///
    /// The redactor refuses to photograph a page while a secret-declared
    /// field holds content, which is correct and must stay that way; once the
    /// credentials have gone upstream the copy in the form is not needed by
    /// anything and is the only reason that refusal ever fires.
    /// </summary>
    Task ClearSecretsAsync(CancellationToken ct);

    /// <summary>The first candidate that matches, or null when none of them does.</summary>
    Task<PageMatch?> FindAsync(IReadOnlyList<string> selectors, int timeoutMs, CancellationToken ct);

    /// <summary>
    /// Types a value into the first matching input. False when nothing
    /// matched - the caller decides what a missing field means, because
    /// "fill something else instead" is never the answer.
    /// </summary>
    Task<bool> FillAsync(IReadOnlyList<string> selectors, string value, int timeoutMs, CancellationToken ct);

    /// <summary>Clicks the first matching element. False when nothing matched.</summary>
    Task<bool> ClickAsync(IReadOnlyList<string> selectors, int timeoutMs, CancellationToken ct);

    /// <summary>
    /// Types an answer into the first matching input and submits it. False
    /// when nothing matched, which means the answer went nowhere.
    /// </summary>
    Task<bool> AnswerAsync(IReadOnlyList<string> selectors, string value, int timeoutMs, CancellationToken ct);
}

/// <summary>
/// The live page, driven through Playwright.
/// </summary>
internal sealed class PlaywrightLoginPage : ILoginPage
{
    /// <summary>
    /// Blanks the matched inputs everywhere on the page, shadow roots
    /// included - a modern login form is frequently inside one, and
    /// <c>document.querySelectorAll</c> does not pierce them. Deliberately
    /// the same walk the redactor's own probe makes, because clearing less
    /// than it inspects leaves the capture refused for a field nobody can see.
    /// </summary>
    private const string ClearScript = @"
(selectors) => {
  const roots = [document];
  const walk = (root, depth) => {
    if (depth > 8 || roots.length > 500) return;
    let all;
    try { all = root.querySelectorAll('*'); } catch (e) { return; }
    for (const el of all) {
      if (el.shadowRoot) { roots.push(el.shadowRoot); walk(el.shadowRoot, depth + 1); }
    }
  };
  walk(document, 0);

  let cleared = 0;
  for (const root of roots) {
    for (const selector of selectors) {
      let els;
      try { els = root.querySelectorAll(selector); } catch (e) { continue; }
      for (const el of els) {
        if (typeof el.value !== 'string' && !el.hasAttribute('value')) continue;
        // Through the native setter, and then announced: a framework that
        // holds its own copy of the value writes it straight back on the
        // next render, so a plain assignment can put the password back on
        // the page moments after we took it off.
        const setter = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(el), 'value');
        if (setter && setter.set) { setter.set.call(el, ''); } else { el.value = ''; }
        el.removeAttribute('value');
        el.dispatchEvent(new Event('input', { bubbles: true }));
        el.dispatchEvent(new Event('change', { bubbles: true }));
        cleared++;
      }
    }
  }
  return cleared;
}";

    private readonly IPage _page;
    private readonly string[] _secretSelectors;

    public PlaywrightLoginPage(IPage page, ProviderManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(manifest);

        _page = page;
        _secretSelectors = SecretSelectors(manifest);
    }

    public string Url => _page.Url;

    public async Task GotoAsync(string url, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await _page.GotoAsync(url).ConfigureAwait(false);
    }

    public async Task ClearSecretsAsync(CancellationToken ct)
    {
        foreach (var frame in _page.Frames)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await frame.EvaluateAsync(ClearScript, _secretSelectors).ConfigureAwait(false);
            }
            catch (Exception ex) when (PageOps.IsSelectorMiss(ex))
            {
                // A frame that cannot be scripted - detached mid-navigation,
                // or cross-origin - is one whose fields we cannot vouch for.
                // The redactor makes the same judgement and refuses to
                // photograph it, which is the safe end of this: no image,
                // rather than an image we cannot stand behind.
            }
        }
    }

    public async Task<PageMatch?> FindAsync(
        IReadOnlyList<string> selectors, int timeoutMs, CancellationToken ct)
    {
        var handle = await PageOps.FindAsync(_page, selectors, timeoutMs, ct).ConfigureAwait(false);
        if (handle is null) return null;

        return new PageMatch(await PageOps.CropAsync(handle).ConfigureAwait(false));
    }

    public async Task<bool> FillAsync(
        IReadOnlyList<string> selectors, string value, int timeoutMs, CancellationToken ct)
    {
        var input = await PageOps.FindAsync(_page, selectors, timeoutMs, ct).ConfigureAwait(false);
        if (input is null) return false;

        await input.FillAsync(value).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> ClickAsync(IReadOnlyList<string> selectors, int timeoutMs, CancellationToken ct)
    {
        var target = await PageOps.FindAsync(_page, selectors, timeoutMs, ct).ConfigureAwait(false);
        if (target is null) return false;

        await target.ClickAsync().ConfigureAwait(false);
        return true;
    }

    public async Task<bool> AnswerAsync(
        IReadOnlyList<string> selectors, string value, int timeoutMs, CancellationToken ct)
    {
        var input = await PageOps.FindAsync(_page, selectors, timeoutMs, ct).ConfigureAwait(false);
        if (input is null) return false;

        await input.FillAsync(value).ConfigureAwait(false);
        await input.PressAsync("Enter").ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Every selector a secret the manifest declares could be sitting in.
    ///
    /// A copy of the redactor's own rule rather than a call to it: the
    /// redactor lives in the agent, which an adapter deliberately cannot
    /// reach. The two lists have to agree, so the ordering is the same - the
    /// blanket password rules first, then the attributes a declared field
    /// plausibly appears under.
    /// </summary>
    private static string[] SecretSelectors(ProviderManifest manifest)
    {
        var selectors = new List<string> { "input[type=password]", "input[autocomplete*=password i]" };

        foreach (var field in manifest.Auth.AllFields().Where(f => f.Secret))
        {
            var key = Escape(field.Key);
            selectors.Add($"[name=\"{key}\"]");
            selectors.Add($"[id=\"{key}\"]");
            selectors.Add($"[data-testid=\"{key}\"]");
            selectors.Add($"[data-test=\"{key}\"]");

            if (!string.IsNullOrWhiteSpace(field.Autofill))
            {
                selectors.Add($"[autocomplete=\"{Escape(field.Autofill)}\"]");
            }
        }

        return [.. selectors.Distinct(StringComparer.Ordinal)];
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
