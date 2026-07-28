using Connector.Kit.Challenges;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace Connector.Kit.Agent.Browsing;

/// <summary>
/// Everything replaying a live view's input needs from a browser, and nothing
/// else.
///
/// Narrow for the same reason <see cref="IPointerSurface"/> is: the rules being
/// implemented above it - what maps to what, and what is refused - are
/// arithmetic and refusals, and a seam this small is what lets them be proved
/// without a browser.
///
/// Note what the seam CANNOT express, because that is where half the safety
/// lives. There is no method that takes a URL, a selector, a script or a path.
/// There is no way to hold a key down. And <see cref="PressAsync"/> takes the
/// <see cref="LiveKey"/> enum rather than a string, so the only place a key
/// name exists as text is inside the implementation, one switch away from the
/// call.
/// </summary>
internal interface ILiveSurface
{
    Task MoveAsync(double x, double y, CancellationToken ct);

    Task DownAsync(CancellationToken ct);

    Task UpAsync(CancellationToken ct);

    /// <summary>Scrolls by a distance in page pixels at the pointer's current position.</summary>
    Task ScrollAsync(double deltaY, CancellationToken ct);

    Task InsertTextAsync(string text, CancellationToken ct);

    Task PressAsync(LiveKey key, CancellationToken ct);
}

/// <summary>
/// The one place a <see cref="LiveKey"/> becomes a string.
///
/// A <c>switch</c> over the enum returning a compile-time constant, and it has
/// to be visibly that, because it is the whole defence for the key channel.
/// Playwright's <c>PressAsync</c> takes a string and its own documentation says
/// shortcuts such as <c>Control+o</c> are supported - so a relayed value
/// reaching it is a file dialog, and a table lookup keyed on relayed data is
/// one refactor away from being one. There is no dictionary here, no
/// <c>ToString()</c>, no <c>Enum.GetName</c>: those all take their answer from
/// the input.
///
/// Null means "not a key this agent will press", which is what an enum value
/// from a future version of the contract, or an integer cast into the enum by a
/// deserialiser, arrives as. It is a refusal, never a fallback.
/// </summary>
internal static class LiveKeys
{
    public static string? LiteralFor(LiveKey key) => key switch
    {
        LiveKey.Backspace => "Backspace",
        LiveKey.Delete => "Delete",
        LiveKey.Enter => "Enter",
        LiveKey.Tab => "Tab",
        LiveKey.Escape => "Escape",
        LiveKey.ArrowUp => "ArrowUp",
        LiveKey.ArrowDown => "ArrowDown",
        LiveKey.ArrowLeft => "ArrowLeft",
        LiveKey.ArrowRight => "ArrowRight",
        LiveKey.Home => "Home",
        LiveKey.End => "End",
        _ => null,
    };
}

/// <summary>
/// The real surface: Playwright's mouse and keyboard, at the browser level.
///
/// <c>page.Mouse</c> and <c>page.Keyboard</c> rather than any locator, and that
/// is the premise of the whole feature exactly as it was for the tap relay: a
/// login form inside a cross-origin frame is reachable by no selector of ours,
/// but a mouse or key event is dispatched at the browser, so it arrives where a
/// real one would. We carry the picture out and the events back; the human
/// logged in.
/// </summary>
internal sealed class PlaywrightLiveSurface(IMouse mouse, IKeyboard keyboard) : ILiveSurface
{
    /// <summary>
    /// How long the button stays down for a press the human made as one
    /// gesture. Not used for <see cref="DownAsync"/>/<see cref="UpAsync"/>,
    /// which carry the human's own timing.
    /// </summary>
    private const float PressMs = 40;

    public Task MoveAsync(double x, double y, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Playwright takes floats. The coordinates were rounded to four
        // decimals of the frame on the way in, so the cast cannot move a point
        // across anything a human could see.
        return mouse.MoveAsync((float)x, (float)y);
    }

    /// <summary>
    /// Presses the PRIMARY button, and the only reason that is true is that
    /// <c>Button</c> is never set: <c>MouseDownOptions</c> carries a button and
    /// a click count and nothing else - no modifiers - so there is no way from
    /// here to hold Ctrl while clicking, which is how a link opens in a new
    /// tab. A secondary button is refused just as structurally: a context menu
    /// renders in browser chrome, outside the page's own rendering surface, so
    /// it appears in no frame at all and is then navigable with the arrow keys
    /// this grammar does allow.
    /// </summary>
    public Task DownAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return mouse.DownAsync();
    }

    public Task UpAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return mouse.UpAsync();
    }

    /// <summary>
    /// <c>WheelAsync</c> takes no position and dispatches wherever the pointer
    /// currently is, so the caller moves first. Getting that wrong scrolls
    /// whatever the last click happened to be over, which on a login page is
    /// usually the field the human was typing in.
    /// </summary>
    public Task ScrollAsync(double deltaY, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return mouse.WheelAsync(0, (float)deltaY);
    }

    /// <summary>
    /// <c>InsertTextAsync</c> and never <c>TypeAsync</c> or <c>PressAsync</c>:
    /// it inserts literal characters and interprets nothing, so no arrangement
    /// of the relayed string can name a key. The cost is real and is written
    /// down rather than discovered - insert-text fires no key events, so a
    /// provider whose submit button enables on <c>keydown</c> will not enable,
    /// and the failure looks to the human like a wrong password.
    /// </summary>
    public Task InsertTextAsync(string text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return keyboard.InsertTextAsync(text);
    }

    /// <summary>
    /// The call site the whole key channel is designed around: the argument
    /// handed to Playwright is a constant chosen by a switch over the enum, and
    /// no part of it came from the wire.
    /// </summary>
    public Task PressAsync(LiveKey key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var literal = LiveKeys.LiteralFor(key)
            ?? throw new ArgumentOutOfRangeException(nameof(key), key, "not a key this agent presses");

        return keyboard.PressAsync(literal, new KeyboardPressOptions { Delay = PressMs });
    }
}

/// <summary>
/// Turns what the human did on their own screen back into events on the page
/// they were looking at.
///
/// The mapping itself is <see cref="Tap.ToPagePixels"/> - the same function the
/// still relay uses, on the wire type, so the two features cannot drift apart
/// about what a coordinate means. A streamed frame is the whole viewport, so
/// the region it maps into is <c>(0, 0, width, height)</c>, which is the case
/// that method's own documentation names.
///
/// What lives here is everything around it: the refusals, and the order.
/// </summary>
internal static class LiveInputReplay
{
    /// <summary>
    /// The furthest one scroll event may travel, in multiples of the frame's
    /// height.
    ///
    /// A distance, not a position, which is why it is clamped where a
    /// coordinate would be refused: a page clamps its own scroll at the end of
    /// the document, so no human can tell twenty screens from two hundred, and
    /// the only thing an unbounded value changes is that a large enough double
    /// becomes <c>float.Infinity</c> on the way to Playwright and throws
    /// halfway through a batch - which is the one outcome the whole-or-nothing
    /// rule exists to prevent.
    /// </summary>
    private const double MaxScrollScreens = 20;

    /// <summary>
    /// Replays <paramref name="events"/> onto the page the frame was taken
    /// from, and reports how many were actually dispatched.
    ///
    /// Zero is a real answer and not an error: it is what every refusal
    /// returns. Every refusal is decided BEFORE the first event is dispatched,
    /// for the reason the tap relay gives - never half-clicking a live page -
    /// and here there is a second reason that is worse. A batch is
    /// <c>[Down, Move, Move, Up]</c>; refusing only the offending event in the
    /// middle of that leaves the button held down on a page the human is still
    /// looking at, with no event left to release it. The contract already makes
    /// the batch the unit - <see cref="LiveInputBatch.IsWellFormed"/> is "all
    /// of them" - and this agrees with it rather than inventing a per-event
    /// rule the connector does not share.
    /// </summary>
    /// <param name="frame">
    /// The size of the picture these fractions were measured against - the
    /// CAPTURED size, carried with the frame, never the viewport asked for
    /// again afterwards.
    /// </param>
    internal static async Task<int> ReplayAsync(
        IReadOnlyList<LiveInput> events,
        (int Width, int Height) frame,
        ILiveSurface surface,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(logger);

        if (events.Count == 0) return 0;

        // No picture, nothing to be a fraction OF. This is the redactor's
        // refusal arriving here exactly as it arrives in the tap relay: if no
        // frame was ever posted, nobody saw anything, and these events cannot
        // be answers to it.
        if (frame.Width <= 0 || frame.Height <= 0)
        {
            logger.LogWarning(
                "no live input replayed: no frame has been posted, so {Count} event(s) answer no picture",
                events.Count);
            return 0;
        }

        var region = new CropRegion(0, 0, frame.Width, frame.Height);

        // Everything is resolved before anything is dispatched: the whole batch
        // is checked, every coordinate is mapped, and every key becomes a
        // literal chosen by a switch. A key that this build has no literal for
        // is refused HERE, where refusing costs a batch, rather than at the
        // call site, where it would have to throw over a half-dispatched one.
        var points = new (double X, double Y)[events.Count];
        var scrolls = new double[events.Count];

        for (var i = 0; i < events.Count; i++)
        {
            var input = events[i];

            if (!input.IsWellFormed())
            {
                // Kind and index, never the value: a malformed text event is
                // still someone's typing.
                logger.LogWarning(
                    "no live input replayed: event {Index} of {Count} is a malformed {Kind}", i, events.Count, input.Kind);
                return 0;
            }

            if (input.Kind is LiveInputKind.Key && LiveKeys.LiteralFor(input.Key!.Value) is null)
            {
                logger.LogWarning(
                    "no live input replayed: event {Index} names a key this agent does not press", i);
                return 0;
            }

            if (input.Kind is LiveInputKind.Text or LiveInputKind.Key) continue;

            var point = new Tap(input.X, input.Y).ToPagePixels(region);
            if (!Inside(point, region))
            {
                // Unreachable while IsWellFormed bounds a fraction to [0,1],
                // and kept for the reason the tap relay keeps its twin: this is
                // the one place where a future change on either side of the
                // wire would otherwise become an event somewhere nobody chose.
                logger.LogWarning(
                    "no live input replayed: event {Index} maps outside the {W}x{H} frame it was measured against",
                    i, frame.Width, frame.Height);
                return 0;
            }

            points[i] = point;
            if (input.Kind is LiveInputKind.Scroll)
            {
                scrolls[i] = Math.Clamp(input.DeltaY, -MaxScrollScreens, MaxScrollScreens) * frame.Height;
            }
        }

        var dispatched = 0;

        for (var i = 0; i < events.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var input = events[i];
            try
            {
                // No gap between events, unlike the tap relay's 180 ms. Those
                // were a batch of answers to a still picture, synthesised into
                // a click sequence no human had timed; these arrived carrying
                // the human's own rhythm, and re-spacing them would make a drag
                // take longer than the drag did.
                await DispatchAsync(input, points[i], scrolls[i], surface, ct).ConfigureAwait(false);
                dispatched++;
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
            {
                // A text event's failure is logged WITHOUT the exception, on
                // purpose. Relayed characters must never reach a string that
                // can become a failure detail, and the usual protection does
                // not apply here: the scrubber is built from the job's inputs,
                // which are empty for a streamed login precisely because the
                // password never entered the platform. So we construct our own
                // message, from a length, and drop the reference.
                if (input.Kind is LiveInputKind.Text)
                {
                    logger.LogWarning(
                        "live input: a text event of {Length} character(s) was not delivered; " +
                        "the rest of the batch is dropped",
                        input.Text?.Length ?? 0);
                }
                else
                {
                    logger.LogWarning(
                        ex, "live input: a {Kind} event was not delivered; the rest of the batch is dropped", input.Kind);
                }

                break;
            }
        }

        // Counts and kinds, never coordinates and never text. Where on a login
        // form a particular human put their finger is theirs, and it is also a
        // fair guess at which field they were filling in.
        logger.LogDebug("replayed {Dispatched} of {Count} live input event(s)", dispatched, events.Count);

        return dispatched;
    }

    /// <summary>
    /// One event, dispatched whole.
    ///
    /// A press and a release move first, because Playwright's mouse has no
    /// coordinates on either: they act wherever the pointer already is, which
    /// after any pause is wherever the last event left it.
    /// </summary>
    private static async Task DispatchAsync(
        LiveInput input, (double X, double Y) point, double scroll, ILiveSurface surface, CancellationToken ct)
    {
        switch (input.Kind)
        {
            case LiveInputKind.Move:
                await surface.MoveAsync(point.X, point.Y, ct).ConfigureAwait(false);
                break;

            case LiveInputKind.Down:
                await surface.MoveAsync(point.X, point.Y, ct).ConfigureAwait(false);
                await surface.DownAsync(ct).ConfigureAwait(false);
                break;

            case LiveInputKind.Up:
                await surface.MoveAsync(point.X, point.Y, ct).ConfigureAwait(false);
                await surface.UpAsync(ct).ConfigureAwait(false);
                break;

            case LiveInputKind.Scroll:
                await surface.MoveAsync(point.X, point.Y, ct).ConfigureAwait(false);
                await surface.ScrollAsync(scroll, ct).ConfigureAwait(false);
                break;

            case LiveInputKind.Text:
                await surface.InsertTextAsync(input.Text!, ct).ConfigureAwait(false);
                break;

            case LiveInputKind.Key:
                await surface.PressAsync(input.Key!.Value, ct).ConfigureAwait(false);
                break;

            default:
                // Unreachable: the pre-pass refuses anything IsWellFormed does
                // not recognise. Present so that adding a verb to the grammar
                // without adding an arm here is a refusal rather than silence.
                throw new ArgumentOutOfRangeException(nameof(input), input.Kind, "not a live input kind this agent replays");
        }
    }

    private static bool Inside((double X, double Y) point, CropRegion region) =>
        point.X >= region.X
        && point.Y >= region.Y
        && point.X <= region.X + region.Width
        && point.Y <= region.Y + region.Height;
}
