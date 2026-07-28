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
    /// How long ONE live frame may take. Far shorter than a still's, because a
    /// stream that stalls for ten seconds is a stream nobody is watching any
    /// more - and there is another shutter along in eighty milliseconds, so
    /// giving up early costs a frame rather than a picture.
    /// </summary>
    private const float LiveFrameTimeoutMs = 5_000;

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
    /// Marks a secret-bearing element invisible for the duration of one
    /// capture, remembering whatever inline visibility it had so the page can
    /// be handed back exactly as it was found.
    ///
    /// <c>visibility:hidden</c> and never <c>display:none</c>: hidden keeps the
    /// element's box, so nothing on the page moves. A reflow here would shift
    /// the very widget the crop was measured against, and every tap answered
    /// against that picture would land at the difference.
    ///
    /// Returns the number of elements it could not conceal. Anything above
    /// zero means the caller must fall back to masking.
    /// </summary>
    private const string ConcealScript = @"
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

  let missed = 0;
  const conceal = (selector) => {
    for (const root of roots) {
      let els;
      try { els = root.querySelectorAll(selector); } catch (e) { continue; }
      for (const el of els) {
        try {
          if (!el.hasAttribute('data-connector-vis')) {
            el.setAttribute('data-connector-vis', el.style.visibility || '');
          }
          el.style.setProperty('visibility', 'hidden', 'important');
          if (getComputedStyle(el).visibility !== 'hidden') missed++;
        } catch (e) { missed++; }
      }
    }
  };

  conceal('input[type=password]');
  conceal('input[autocomplete*=password i]');
  for (const probe of keys) { conceal(probe[1]); }
  return missed;
}";

    /// <summary>
    /// Re-checks, after the shutter, that every secret-bearing element was
    /// still concealed AND still empty. A page that re-rendered mid-capture and
    /// restored its own styling is the one way a dropped mask could have let
    /// something through, so the bytes are thrown away rather than trusted.
    /// </summary>
    private const string ConcealedProbeScript = @"
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

  const bad = [];
  const check = (selector, label) => {
    for (const root of roots) {
      let els;
      try { els = root.querySelectorAll(selector); } catch (e) { continue; }
      for (const el of els) {
        let shown = true;
        try { shown = getComputedStyle(el).visibility !== 'hidden'; } catch (e) { shown = true; }
        const v = (el.value !== undefined && el.value !== null) ? el.value : el.getAttribute('value');
        const filled = typeof v === 'string' && v.length > 0;
        if (shown || filled) { bad.push(label); return; }
      }
    }
  };

  check('input[type=password]', 'password-input');
  check('input[autocomplete*=password i]', 'password-autocomplete');
  for (const probe of keys) { check(probe[1], probe[0]); }
  return bad;
}";

    /// <summary>Puts back exactly the inline visibility each element arrived with.</summary>
    private const string RevealScript = @"
() => {
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

  for (const root of roots) {
    let els;
    try { els = root.querySelectorAll('[data-connector-vis]'); } catch (e) { continue; }
    for (const el of els) {
      try {
        const previous = el.getAttribute('data-connector-vis') || '';
        el.style.removeProperty('visibility');
        if (previous) el.style.visibility = previous;
        el.removeAttribute('data-connector-vis');
      } catch (e) { /* leaving one hidden is safe; showing it again is not */ }
    }
  }
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

        if (crop is { } size && (size.Width <= 0 || size.Height <= 0))
        {
            _logger.LogWarning("no capture: crop region {W}x{H} is empty", size.Width, size.Height);
            return [];
        }

        // Conceal rather than mask, when the page lets us.
        //
        // A mask is painted by Playwright into the browser's TOP LAYER - an
        // `x-pw-glass` element declared `popover: manual` - from the target's
        // `getBoundingClientRect()`. Nothing on the page can be stacked above
        // it. So when a provider fronts its login with an overlay, as Albert
        // Heijn's hCaptcha does, the mask for the password field UNDERNEATH
        // that overlay is painted straight across the question the human is
        // being asked, and the challenge arrives unanswerable.
        //
        // Hiding the element ourselves reaches the same end by a route that
        // does not paint: an element with `visibility: hidden` contributes no
        // pixels of its own, whatever appears in it and whoever is stacked
        // above it. That is a property we set and then re-check, not one
        // inferred from a hit test - `elementFromPoint` answers "what is
        // topmost and hit-testable", which a translucent modal backdrop
        // satisfies while remaining perfectly see-through, so it can never
        // license dropping a mask.
        //
        // Masking stays as the fallback for any page that will not be
        // concealed. An unreadable challenge is a bad answer; a photographed
        // password is not an answer at all.
        var concealed = await ConcealAsync(page, ct).ConfigureAwait(false);

        try
        {
            var options = new PageScreenshotOptions
            {
                Type = ScreenshotType.Png,
                Animations = ScreenshotAnimations.Disabled,
                Caret = ScreenshotCaret.Hide,
                MaskColor = "#000000",
                Timeout = CaptureTimeoutMs,
            };

            if (!concealed) options.Mask = MaskLocators(page);

            if (crop is { } region)
            {
                options.Clip = new Clip
                {
                    X = region.X,
                    Y = region.Y,
                    Width = region.Width,
                    Height = region.Height,
                };
            }

            byte[] png;
            try
            {
                png = await page.ScreenshotAsync(options).ConfigureAwait(false);
            }
            catch (PlaywrightException ex)
            {
                // Deliberately no retry without the masks: the fallback that
                // drops a safety layer is worse than no image.
                _logger.LogWarning(ex, "no capture: the screenshot itself failed");
                return [];
            }

            // The shutter and the checks around it are not one instant. A page
            // that re-rendered mid-capture could have restored its own styling
            // and filled a field in the same tick, and with the masks dropped
            // there would be nothing else standing between that value and the
            // picture. Cheaper to throw the bytes away than to be wrong.
            if (concealed && !await StillConcealedAsync(page, ct).ConfigureAwait(false)) return [];

            return png;
        }
        finally
        {
            await RevealAsync(page).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A JPEG of the whole visible page for a LIVE VIEW, and the size it was
    /// taken at. Read the name as a warning.
    ///
    /// <b>This method does not carry the guarantees the rest of this class
    /// exists to provide.</b> It is a method beside <see cref="CaptureAsync"/>
    /// and never a flag on it, so that no caller reaches the live path by
    /// passing an argument, and so that a reader of <see cref="CaptureAsync"/>
    /// never has to wonder which rules were in force. Nothing on the artifact
    /// or relay paths changes because of it.
    ///
    /// What it drops, and why the drop is not a weakening:
    ///
    /// <b>Layer 1, the refusal, does not apply.</b> Refusing to photograph a
    /// page whose password box holds content is right when WE typed that
    /// password: the value is ours, it is already at rest elsewhere, and a
    /// picture of it buys nobody anything. A live view is the opposite
    /// situation - the human is deliberately typing into the provider's own
    /// page, watching this stream to do it - so the refusal would produce a
    /// black rectangle at exactly the moment the feature exists for, and the
    /// person typing would have no way to know why. What makes that acceptable
    /// is the custody change around it and not a softer rule: a provider served
    /// this way declares no credential fields, so nothing of ours was typed in,
    /// the frames are never stored, and they go to one attached viewer.
    ///
    /// <b>Layer 2, concealment, does not run - and that is a gap rather than a
    /// decision.</b> The design's R5 (conceal every secret-bearing element the
    /// human has NOT focused, and re-check that the visible set is exactly the
    /// authorised one) needs focus tracking and a slow re-check cadence that do
    /// not exist yet, and it cannot run per frame at four DOM walks a shutter
    /// anyway. Until it does, everything the page renders is streamed. That is
    /// what the owner asked for - the whole visible page, cookie banners and
    /// error messages included - and it is why a live view may only be opened
    /// on a provider that declares no secret fields at all.
    ///
    /// The encoder settings are the measured ones and each is load-bearing:
    ///
    /// <list type="bullet">
    /// <item><b>JPEG, never PNG</b> - and the measurement behind that is worth
    /// stating honestly rather than as a slogan, because it only holds one way
    /// round. Measured here at 390x844: a flat login form is 6.3 KB as PNG and
    /// 9.1 KB as JPEG q60, so PNG WINS on the page this feature is named after.
    /// Put a photograph on that page - a hero image, a gradient, the captcha
    /// tiles this pipeline also carries - and it is 194 KB as PNG against 14 KB
    /// as JPEG. The encoder is fixed to JPEG because the bad case has to be
    /// survivable at twelve frames a second and the good case merely costs 3 KB;
    /// choosing per stream, by taking frame zero twice, is the better answer and
    /// is not built.</item>
    /// <item><b><c>Scale = Css</c>.</b> One image pixel is one CSS pixel, which
    /// is what lets a returned fraction be multiplied by the frame's own size
    /// and land on the page - the device scale factor drops out of the
    /// coordinate mapping entirely instead of having to be carried and agreed
    /// on. (Note the default is <c>device</c>, which is why the still relay's
    /// <see cref="CaptureAsync"/> ships more bytes than it needs on a retina
    /// agent. Not changed here: a lower-resolution captcha is harder for a
    /// human to read, and that path is proven.)</item>
    /// <item><b>No <c>Clip</c>.</b> The whole visible page is streamed, by the
    /// owner's decision, because a human filling in a login has to be able to
    /// see the cookie banner in front of it and the error message under
    /// it.</item>
    /// <item><b><c>Animations</c> left at the default.</b> The relay disables
    /// them because a still of a half-drawn widget is unanswerable; a stream
    /// heals that by itself 80 ms later, and fighting a page's animations
    /// continuously at 12 frames a second is a cost with nothing to buy.</item>
    /// <item><b><c>Caret = Hide</c>.</b> A blinking caret changes the pixels
    /// about twice a second forever, which alone would defeat the change
    /// detection the whole cadence rests on. The cost is real and is not paid
    /// for yet: the human cannot see where they are typing until the focused
    /// element's rectangle is shipped beside the frame and the client draws its
    /// own.</item>
    /// </list>
    /// </summary>
    internal async Task<LiveCapture> CaptureLiveFrameAsync(IPage page, int quality, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentOutOfRangeException.ThrowIfLessThan(quality, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quality, 100);
        ct.ThrowIfCancellationRequested();

        if (page.IsClosed)
        {
            _logger.LogDebug("no live frame: the page is closed");
            return LiveCapture.Refused;
        }

        // The size has to be the size the bytes really are, because every event
        // the human sends back is a fraction that gets multiplied by it.
        // `Scale = Css` below makes one image pixel one CSS pixel, so the
        // viewport IS the frame - and a context with no viewport, which is a
        // headed window sized by whoever is sitting at it, has no such number.
        // Refused rather than guessed: a guessed size is every subsequent click
        // landing somewhere nobody chose.
        if (page.ViewportSize is not { } viewport || viewport.Width <= 0 || viewport.Height <= 0)
        {
            _logger.LogWarning(
                "no live frame: this browser context has no viewport, so a frame would carry no size to map against");
            return LiveCapture.Refused;
        }

        try
        {
            var jpeg = await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Type = ScreenshotType.Jpeg,
                Quality = quality,
                Scale = ScreenshotScale.Css,
                Caret = ScreenshotCaret.Hide,
                Timeout = LiveFrameTimeoutMs,
            }).ConfigureAwait(false);

            return LiveCapture.Of(jpeg, viewport.Width, viewport.Height);
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            // Debug and not warning: a missed shutter in a stream is one frame
            // the viewer never sees, the next one is 80 ms away, and a page
            // that is navigating misses several in a row as a matter of course.
            // Logging each at warning would bury the stop that matters.
            _logger.LogDebug(ex, "no live frame: the shutter failed");
            return LiveCapture.Refused;
        }
    }

    /// <summary>
    /// Hides every secret-bearing element on the page. False when even one
    /// frame or one element would not go, which puts the caller back on masks.
    /// </summary>
    private async Task<bool> ConcealAsync(IPage page, CancellationToken ct)
    {
        var all = true;

        foreach (var frame in page.Frames)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var missed = await frame.EvaluateAsync<int>(ConcealScript, _probes).ConfigureAwait(false);
                if (missed > 0)
                {
                    _logger.LogDebug(
                        "{Missed} element(s) in frame {Url} would not be concealed; masking instead",
                        missed, Describe(frame));
                    all = false;
                }
            }
            catch (PlaywrightException ex)
            {
                _logger.LogDebug(ex, "frame {Url} could not be concealed; masking instead", Describe(frame));
                all = false;
            }
        }

        return all;
    }

    /// <summary>
    /// True when every secret-bearing element is still both hidden and empty.
    /// </summary>
    private async Task<bool> StillConcealedAsync(IPage page, CancellationToken ct)
    {
        foreach (var frame in page.Frames)
        {
            ct.ThrowIfCancellationRequested();

            string[] bad;
            try
            {
                bad = await frame.EvaluateAsync<string[]>(ConcealedProbeScript, _probes).ConfigureAwait(false);
            }
            catch (PlaywrightException ex)
            {
                _logger.LogWarning(ex, "discarding the capture: frame {Url} could not be re-checked", Describe(frame));
                return false;
            }

            if (bad.Length > 0)
            {
                // Field KEYS are manifest metadata, never values.
                _logger.LogWarning(
                    "discarding the capture: {Fields} became visible or filled while it was being taken",
                    string.Join(", ", bad.Distinct(StringComparer.Ordinal)));
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Hands the page back as it was found. Never takes the caller's token:
    /// a cancelled capture must still leave a usable login form behind.
    /// </summary>
    private async Task RevealAsync(IPage page)
    {
        foreach (var frame in page.Frames)
        {
            try
            {
                await frame.EvaluateAsync(RevealScript).ConfigureAwait(false);
            }
            catch (PlaywrightException ex)
            {
                _logger.LogDebug(ex, "frame {Url} could not be restored after a capture", Describe(frame));
            }
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
