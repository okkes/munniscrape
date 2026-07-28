using System.Security.Cryptography;
using System.Text;
using Connector.Kit.Challenges;
using Connector.Kit.Manifests;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace Connector.Kit.Agent.Browsing;

/// <summary>
/// The last thing that runs before an image leaves this machine.
///
/// Three layers, in order of strength:
///
/// 1. <b>Refuse.</b> If any field the manifest declares <c>secret</c>, or any
///    password input at all, still holds content, no image is produced. This
///    is a check on the live DOM, not on adapter discipline - a field being
///    marked secret is what makes redaction possible, which is why the
///    manifest validator refuses a password field that is not marked.
/// 2. <b>Mask.</b> Those same elements are blacked out by the renderer even
///    when they are empty, in every frame, so a value that appears between the
///    check and the capture is still not in the pixels.
/// 3. <b>Clip.</b> When the adapter declares a crop region, only that region
///    is rendered at all. Everything outside it is absent rather than merely
///    obscured.
///
/// Anything it cannot verify produces NO image. An empty result is the safe
/// answer, never an error.
/// </summary>
public sealed class ScreenshotRedactor
{
    private const float CaptureTimeoutMs = 10_000;

    /// <summary>
    /// Reports which secret-bearing elements currently hold content. Walks
    /// shadow roots because a modern login form is frequently inside one, and
    /// <c>document.querySelectorAll</c> does not pierce them.
    /// </summary>
    private const string SecretProbeScript = @"
(keys) => {
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

  const filled = (el) => {
    const v = (el.value !== undefined && el.value !== null) ? el.value : el.getAttribute('value');
    return typeof v === 'string' && v.length > 0;
  };

  const dirty = [];
  const scan = (selector, label) => {
    for (const root of roots) {
      let els;
      try { els = root.querySelectorAll(selector); } catch (e) { continue; }
      for (const el of els) { if (filled(el)) { dirty.push(label); return; } }
    }
  };

  scan('input[type=password]', 'password-input');
  scan('input[autocomplete*=password i]', 'password-autocomplete');
  for (const probe of keys) { scan(probe[1], probe[0]); }
  return dirty;
}";

    /// <summary>
    /// A structural skeleton of the page. Only its HASH ever leaves the agent,
    /// so it may be as detailed as it likes - but it deliberately carries no
    /// text and no attribute values, because a digest that changed whenever an
    /// account number did would be useless for spotting a shape change.
    /// </summary>
    private const string SkeletonScript = @"
() => {
  const parts = [];
  const walk = (node, depth) => {
    if (depth > 24 || parts.length > 4000) return;
    for (const el of node.children) {
      let tag = el.tagName.toLowerCase();
      if (tag === 'input' || tag === 'button') tag += ':' + (el.getAttribute('type') || '');
      const testId = el.getAttribute('data-testid') || el.getAttribute('data-test') || '';
      const role = el.getAttribute('role') || '';
      parts.push(depth + '|' + tag + '|' + testId + '|' + role);
      walk(el, depth + 1);
    }
  };
  walk(document.documentElement, 0);
  return parts.join('\n');
}";

    private readonly ILogger _logger;

    /// <summary>
    /// <c>[key, selector]</c> pairs. Passed as plain arrays rather than objects
    /// so the shape does not depend on how Playwright happens to name
    /// properties when it serialises an argument.
    /// </summary>
    private readonly string[][] _probes;

    private readonly string _maskSelector;

    public ScreenshotRedactor(ProviderManifest manifest, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _probes =
        [
            .. manifest.Auth.AllFields()
                .Where(f => f.Secret)
                .DistinctBy(f => f.Key, StringComparer.Ordinal)
                .Select(f => new[] { f.Key, SelectorFor(f) }),
        ];

        _maskSelector = string.Join(", ",
            new[] { "input[type=password]", "input[autocomplete*=password i]" }
                .Concat(_probes.Select(p => p[1])));
    }

    /// <summary>
    /// Whether the page can be photographed right now. Used both before a
    /// capture and again before any adapter-supplied image is posted, because
    /// the adapter holds a real <see cref="IPage"/> and could have produced
    /// those bytes by any route.
    /// </summary>
    public async Task<bool> IsSafeToCaptureAsync(IPage page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);
        ct.ThrowIfCancellationRequested();

        if (page.IsClosed)
        {
            _logger.LogDebug("no capture: the page is closed");
            return false;
        }

        foreach (var frame in page.Frames)
        {
            ct.ThrowIfCancellationRequested();

            string[] dirty;
            try
            {
                dirty = await frame.EvaluateAsync<string[]>(SecretProbeScript, _probes).ConfigureAwait(false);
            }
            catch (PlaywrightException ex)
            {
                // A frame we cannot read is a frame we cannot vouch for, and a
                // maybe-safe image is worth strictly less than no image.
                _logger.LogWarning(ex, "no capture: frame {Url} could not be inspected", Describe(frame));
                return false;
            }

            if (dirty.Length > 0)
            {
                // The field KEYS are manifest metadata, never values.
                _logger.LogInformation(
                    "no capture: {Fields} still hold content", string.Join(", ", dirty.Distinct(StringComparer.Ordinal)));
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A redacted PNG, or an empty array when the page could not be verified.
    /// Callers treat empty as "no image"; it is never an error.
    /// </summary>
    public async Task<byte[]> CaptureAsync(IPage page, CropRegion? crop, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (!await IsSafeToCaptureAsync(page, ct).ConfigureAwait(false)) return [];

        var options = new PageScreenshotOptions
        {
            Type = ScreenshotType.Png,
            Animations = ScreenshotAnimations.Disabled,
            Caret = ScreenshotCaret.Hide,
            Mask = MaskLocators(page),
            MaskColor = "#000000",
            Timeout = CaptureTimeoutMs,
        };

        if (crop is { } region)
        {
            if (region.Width <= 0 || region.Height <= 0)
            {
                _logger.LogWarning("no capture: crop region {W}x{H} is empty", region.Width, region.Height);
                return [];
            }

            options.Clip = new Clip
            {
                X = region.X,
                Y = region.Y,
                Width = region.Width,
                Height = region.Height,
            };
        }

        try
        {
            return await page.ScreenshotAsync(options).ConfigureAwait(false);
        }
        catch (PlaywrightException ex)
        {
            // Deliberately no retry without the masks: the fallback that drops
            // a safety layer is worse than no image.
            _logger.LogWarning(ex, "no capture: the screenshot itself failed");
            return [];
        }
    }

    /// <summary>
    /// A hash of the page's shape, for failure artifacts. Null when the page
    /// cannot be read.
    /// </summary>
    public async Task<string?> DomDigestAsync(IPage page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);
        ct.ThrowIfCancellationRequested();

        if (page.IsClosed) return null;

        try
        {
            var skeleton = await page.MainFrame.EvaluateAsync<string>(SkeletonScript).ConfigureAwait(false);
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(skeleton));
            return "sha256:" + Convert.ToHexStringLower(digest);
        }
        catch (PlaywrightException ex)
        {
            _logger.LogDebug(ex, "no dom digest: the page could not be walked");
            return null;
        }
    }

    private IReadOnlyList<ILocator> MaskLocators(IPage page)
    {
        var masks = new List<ILocator>(page.Frames.Count);
        foreach (var frame in page.Frames)
        {
            masks.Add(frame.Locator(_maskSelector));
        }

        return masks;
    }

    /// <summary>
    /// The attributes a field key plausibly appears under. Adapters name their
    /// fields after the provider's own inputs, so this hits far more often
    /// than not - and the password-input rule covers the rest.
    /// </summary>
    private static string SelectorFor(FieldSpec field)
    {
        var key = Escape(field.Key);
        var selectors = new List<string>
        {
            $"[name=\"{key}\"]",
            $"[id=\"{key}\"]",
            $"[data-testid=\"{key}\"]",
            $"[data-test=\"{key}\"]",
        };

        if (!string.IsNullOrWhiteSpace(field.Autofill))
        {
            selectors.Add($"[autocomplete=\"{Escape(field.Autofill)}\"]");
        }

        return string.Join(", ", selectors);
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string Describe(IFrame frame) => string.IsNullOrEmpty(frame.Url) ? "<about:blank>" : frame.Url;
}
