using Connector.Kit.Challenges;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace Connector.Kit.Agent.Browsing;

/// <summary>
/// Everything replaying a tap needs from a browser: a point to click, and a
/// wait between two of them.
///
/// Narrow on purpose. The rule being implemented - where a normalised tap
/// lands, and what must NOT be clicked - is arithmetic and refusals, and a
/// seam this small is what lets that be proved without a browser at all.
/// </summary>
internal interface IPointerSurface
{
    Task ClickAsync(double x, double y, CancellationToken ct);

    Task PauseAsync(TimeSpan gap, CancellationToken ct);
}

/// <summary>
/// The real surface: Playwright's mouse.
///
/// <c>page.Mouse</c> and not <c>frame.ClickAsync</c>, and that is the premise
/// of this whole feature. An hCaptcha challenge lives in a cross-origin iframe
/// whose contents no selector of ours can reach, but a mouse event is
/// dispatched at the browser level, at a point in the page - so it arrives
/// inside that iframe exactly as a real one would. We carry the picture out
/// and the taps back; the human solved it.
/// </summary>
internal sealed class MousePointer(IMouse mouse, TimeProvider time) : IPointerSurface
{
    /// <summary>
    /// How long the button stays down. A press is an event pair with a
    /// duration; dispatching down and up in the same instant is not a shorter
    /// click, it is a shape no pointing device produces, and a widget that
    /// listens for both may see only one.
    /// </summary>
    private const float PressMs = 40;

    public Task ClickAsync(double x, double y, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Playwright takes floats. The taps were rounded to four decimals of
        // the image on the way in, so the cast cannot move one across a tile
        // boundary of any grid a human can see.
        return mouse.ClickAsync((float)x, (float)y, new MouseClickOptions { Delay = PressMs });
    }

    public Task PauseAsync(TimeSpan gap, CancellationToken ct) => Task.Delay(gap, time, ct);
}

/// <summary>
/// Turns the human's taps back into clicks on the page they were taken from.
///
/// The mapping itself is <see cref="Tap.ToPagePixels"/> - it lives on the wire
/// type so the two ends cannot disagree about what a coordinate means. What
/// lives here is everything around it: the refusals, the order, and the gap.
/// </summary>
internal static class TapReplay
{
    /// <summary>
    /// The gap between two taps.
    ///
    /// Not an evasion measure and not a disguise: three clicks in the same
    /// millisecond are three events a widget's own handlers cannot process as
    /// separate selections, and the page needs the time to paint the tile it
    /// just accepted before the next one arrives. It is the same reason a real
    /// finger cannot do it either.
    /// </summary>
    internal static readonly TimeSpan DefaultGap = TimeSpan.FromMilliseconds(180);

    /// <summary>
    /// Replays <paramref name="taps"/> into the region
    /// <paramref name="capture"/> came from, and reports how many clicks were
    /// actually dispatched.
    ///
    /// Zero is a real answer, not an error: it is what a refusal returns, and
    /// the caller's cue that the challenge went unanswered. Every refusal
    /// happens BEFORE the first click, because the one outcome worth more
    /// engineering than any other is never half-clicking a live page.
    /// </summary>
    internal static async Task<int> ReplayAsync(
        IReadOnlyList<Tap> taps,
        RegionCapture capture,
        (int Width, int Height)? viewport,
        IPointerSurface pointer,
        TimeSpan gap,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(taps);
        ArgumentNullException.ThrowIfNull(pointer);
        ArgumentNullException.ThrowIfNull(logger);

        // No image ever left this machine, so nobody saw anything and these
        // taps cannot be answers to it. This is the redactor's refusal arriving
        // here: it produces no clicks, exactly as it produced no picture.
        if (!capture.Captured)
        {
            logger.LogWarning(
                "no taps replayed: no image was captured for this challenge, so there is nothing they answer");
            return 0;
        }

        if (taps.Count == 0) return 0;

        var region = capture.Region;

        // The crop is measured against the viewport, and so is the mouse. A box
        // that no longer fits inside it means the page scrolled or re-laid
        // itself since the capture - the taps are then answers to a picture of
        // somewhere else, and clicking them would hit whatever has since moved
        // under those coordinates.
        if (viewport is { } screen && !Fits(region, screen))
        {
            logger.LogWarning(
                "no taps replayed: the captured region {X},{Y} {W}x{H} does not fit the {VW}x{VH} viewport any more",
                region.X, region.Y, region.Width, region.Height, screen.Width, screen.Height);
            return 0;
        }

        // Mapped in full before anything is clicked. A tap is a fraction of the
        // image by construction, so a point outside the box it was measured
        // against cannot happen today - which is precisely why the check is
        // cheap enough to keep: it is the one place where a future change on
        // either side of the wire would otherwise become a click somewhere
        // nobody chose.
        var points = new (double X, double Y)[taps.Count];
        for (var i = 0; i < taps.Count; i++)
        {
            var point = taps[i].ToPagePixels(region);
            if (!Inside(point, region))
            {
                logger.LogWarning(
                    "no taps replayed: tap {Index} maps to {X},{Y}, which is outside the region it was measured against",
                    i, point.X, point.Y);
                return 0;
            }

            points[i] = point;
        }

        for (var i = 0; i < points.Length; i++)
        {
            ct.ThrowIfCancellationRequested();

            // Between the taps, never before the first and never after the
            // last: the gap belongs to the interval, not to the click.
            if (i > 0) await pointer.PauseAsync(gap, ct).ConfigureAwait(false);

            await pointer.ClickAsync(points[i].X, points[i].Y, ct).ConfigureAwait(false);
        }

        // Counts and the box, never the coordinates: where on a captcha grid a
        // particular human tapped is theirs.
        logger.LogInformation(
            "replayed {Count} taps into the region at {X},{Y}", points.Length, region.X, region.Y);

        return points.Length;
    }

    private static bool Fits(CropRegion region, (int Width, int Height) screen) =>
        region.X >= 0
        && region.Y >= 0
        && region.X + region.Width <= screen.Width
        && region.Y + region.Height <= screen.Height;

    private static bool Inside((double X, double Y) point, CropRegion region) =>
        point.X >= region.X
        && point.Y >= region.Y
        && point.X <= region.X + region.Width
        && point.Y <= region.Y + region.Height;
}
