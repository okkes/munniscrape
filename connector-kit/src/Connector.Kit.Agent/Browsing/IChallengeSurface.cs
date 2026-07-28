using Connector.Kit.Adapters;
using Connector.Kit.Challenges;
using Microsoft.Playwright;

namespace Connector.Kit.Agent.Browsing;

/// <summary>
/// The two things relaying an INTERACTIVE challenge needs from a browser:
/// photograph exactly this thing and tell me the box it was in, then click
/// exactly where the human tapped inside that box.
///
/// Both halves exist because of the same fact. An hCaptcha challenge is a grid
/// of images inside a cross-origin iframe: its contents cannot be read or
/// clicked by any selector we own, and its answer is a sequence of taps rather
/// than a string. So the picture travels to the human's own phone, the taps
/// travel back as fractions of that picture, and the agent maps them into page
/// pixels and dispatches them with <c>page.Mouse</c>, which is a browser-level
/// event and therefore reaches inside the iframe. Nothing here solves
/// anything; it only carries.
///
/// <b>Why this is a separate interface.</b> It belongs on
/// <see cref="IBrowserLease"/>, which is what an adapter actually holds - but
/// that type lives in <c>Connector.Kit</c> and this change is scoped to the
/// agent. Until these four members are lifted onto <see cref="IBrowserLease"/>
/// (as DEFAULT implementations returning <see cref="RegionCapture.Refused"/>
/// and <c>0</c>, so the non-agent leases keep compiling), an adapter cannot
/// reach them: <c>ShopConnector.Adapters</c> references <c>Connector.Kit</c>
/// only. <see cref="RegionCapture"/> moves with them.
/// </summary>
public interface IChallengeSurface : IBrowserLease
{
    /// <summary>
    /// Photograph exactly this box, and hand back the box with the picture.
    ///
    /// For a caller that has already measured the element itself - and so still
    /// owns the rectangle the taps will be mapped into.
    /// </summary>
    Task<RegionCapture> CaptureRegionAsync(CropRegion region, CancellationToken ct);

    /// <summary>
    /// Photograph exactly this element: find it, scroll it into view, measure
    /// it, and clip to what it occupies.
    /// </summary>
    Task<RegionCapture> CaptureElementAsync(string selector, CancellationToken ct);

    /// <summary>
    /// Photograph exactly this frame, through the <c>&lt;iframe&gt;</c> element
    /// that hosts it.
    ///
    /// The case this was built for. A cross-origin captcha frame is reachable
    /// as an <see cref="IFrame"/> - by its URL - long before any selector of
    /// ours can name the element it lives in, and the frame's own contents are
    /// not reachable at all.
    /// </summary>
    Task<RegionCapture> CaptureFrameAsync(IFrame frame, CancellationToken ct);

    /// <summary>
    /// Click, in order, where the human tapped on <paramref name="capture"/>,
    /// and report how many clicks were dispatched.
    ///
    /// Zero means nothing was clicked - a refused capture, an empty answer, or
    /// a page that has moved under the box since. It is never a partial replay:
    /// every refusal is decided before the first click.
    ///
    /// <see cref="TapAnswer.Submit"/> is not this method's business. Which
    /// control means "verify" is provider knowledge, and on hCaptcha it is
    /// inside the same iframe the human was just looking at.
    /// </summary>
    Task<int> ReplayTapsAsync(IReadOnlyList<Tap> taps, RegionCapture capture, CancellationToken ct);
}
