using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Serialization;

namespace Connector.Kit.Challenges;

/// <summary>
/// What a human may do to a live view.
///
/// This is the whole vocabulary, and being able to write it out is the point.
/// The relay it grows out of had a grammar so narrow it was a guarantee: taps
/// were fractions in [0,1] and a terminal marker, so a compromised consumer
/// could not express "navigate" or "read the cookies" because there were no
/// words for either. Typing a password needs more than that, so the guarantee
/// has to be re-made rather than assumed.
///
/// It is re-made in two places. HERE, by leaving out every event that names a
/// destination: nothing in this file carries a URL, a selector, a script, or a
/// file path, so navigation, evaluation and upload are not merely rejected -
/// they are unsayable. And on the agent, which bounds a pointer to the streamed
/// rectangle and refuses a key that is not in <see cref="LiveKey"/>.
///
/// That is weaker than the tap grammar was and it should be read as weaker. It
/// buys back what a login actually needs and nothing beyond it.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<LiveInputKind>))]
public enum LiveInputKind
{
    /// <summary>The pointer moved. Hover states and drags need it.</summary>
    Move,

    /// <summary>Primary button pressed. There is no secondary button on purpose - see the class remarks.</summary>
    Down,

    /// <summary>Primary button released.</summary>
    Up,

    /// <summary>A wheel or a swipe, in fractions of the view.</summary>
    Scroll,

    /// <summary>Printable text, typed into whatever the page has focused.</summary>
    Text,

    /// <summary>One of the named keys. Never a chord, never a modifier held down.</summary>
    Key,
}

/// <summary>
/// The keys a human may press. A closed set, and short deliberately.
///
/// Modifiers are absent and their absence is load-bearing. Playwright's
/// <c>PressAsync</c> takes a STRING and reads "Control+o" as a chord, so a
/// relayed key name reaching it would be a way to open a file dialog; and
/// <c>DownAsync</c> latches a modifier that a later click would pick up, which
/// is how Ctrl+click opens a new tab. So no relayed value is ever passed to
/// <c>PressAsync</c> - this enum is mapped to a literal on the agent - and
/// there is no way to hold a key down at all.
///
/// What is left is what filling in a form needs: move between fields, correct a
/// mistake, submit.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<LiveKey>))]
public enum LiveKey
{
    Backspace,
    Delete,
    Enter,
    Tab,
    Escape,
    ArrowUp,
    ArrowDown,
    ArrowLeft,
    ArrowRight,
    Home,
    End,
}

/// <summary>
/// One thing the human did, in fractions of the picture they did it to.
///
/// Fractions rather than pixels for the same reason the tap relay used them:
/// the human's screen and the agent's viewport are different sizes, and a
/// coordinate that means something on one is meaningless on the other. A
/// fraction survives the difference, and survives the phone being rotated
/// mid-login.
/// </summary>
public sealed record LiveInput
{
    public required LiveInputKind Kind { get; init; }

    /// <summary>0 at the left edge of the picture, 1 at the right. Ignored by Text and Key.</summary>
    public double X { get; init; }

    /// <summary>0 at the top edge, 1 at the bottom. Ignored by Text and Key.</summary>
    public double Y { get; init; }

    /// <summary>
    /// For <see cref="LiveInputKind.Text"/>: what was typed. Never logged, and
    /// never anything but literal characters - it is delivered with Playwright's
    /// <c>InsertText</c>, which types without interpreting a key name.
    /// </summary>
    public string? Text { get; init; }

    /// <summary>For <see cref="LiveInputKind.Key"/>.</summary>
    public LiveKey? Key { get; init; }

    /// <summary>For <see cref="LiveInputKind.Scroll"/>: fractions of the view to travel.</summary>
    public double DeltaY { get; init; }

    /// <summary>
    /// The consumer's own counter, so the agent can drop a duplicate and order
    /// what arrives out of order. A stream over an unreliable phone connection
    /// will do both.
    /// </summary>
    public long Sequence { get; init; }

    /// <summary>
    /// Whether this event can be delivered to a page at all, before anything
    /// touches a browser.
    ///
    /// Coordinates outside the picture are refused rather than clamped: a
    /// clamped tap is a click somewhere the human did not choose, and the whole
    /// design rests on never doing that.
    /// </summary>
    public bool IsWellFormed()
    {
        // The kind itself, first and before anything reads it.
        //
        // This was missing, and the hole was not theoretical. The enum
        // converter accepts an INTEGER as well as a name, so `"kind": 99`
        // deserialises to an undefined value, passed the checks below because
        // its coordinates were fine, reached the agent's dispatch switch, and
        // threw out of a `default:` arm commented "unreachable". The throw
        // escaped the replay loop and faulted it for the rest of the login:
        // frames kept arriving, every tap and keystroke did nothing, and the
        // only trace was one debug line at disposal. An unknown name was
        // refused all along; an unknown NUMBER was not.
        if (!System.Enum.IsDefined(Kind)) return false;

        if (Kind is LiveInputKind.Text)
        {
            return !string.IsNullOrEmpty(Text) && Text.Length <= MaxTextLength && !HasControlCharacters(Text);
        }

        if (Kind is LiveInputKind.Key) return Key is not null && System.Enum.IsDefined(Key.Value);

        if (Kind is LiveInputKind.Scroll) return IsFraction(X) && IsFraction(Y) && double.IsFinite(DeltaY);

        return IsFraction(X) && IsFraction(Y);
    }

    /// <summary>
    /// A paste is one event, a held key is not. Long enough for a password
    /// manager, short enough that nothing useful can be smuggled through as a
    /// single keystroke.
    /// </summary>
    public const int MaxTextLength = 256;

    private static bool IsFraction(double value) => double.IsFinite(value) && value is >= 0 and <= 1;

    /// <summary>
    /// Control characters are how a "typed" string becomes something else - a
    /// newline that submits a form the human was still filling in, an escape
    /// sequence that means something to a terminal further down a log pipeline.
    /// Enter is available as a key and says so.
    /// </summary>
    private static bool HasControlCharacters(string text)
    {
        foreach (var ch in text)
        {
            if (char.IsControl(ch)) return true;
        }

        return false;
    }
}

/// <summary>
/// A batch, because one event per request would spend more time in HTTP
/// headers than in the browser, and a drag is thirty moves.
/// </summary>
public sealed record LiveInputBatch
{
    /// <summary>In the order the human made them; the agent replays them in that order.</summary>
    public IReadOnlyList<LiveInput> Events { get; init; } = [];

    /// <summary>
    /// A bound on one request, so a batch cannot become a way to keep an agent
    /// busy indefinitely.
    /// </summary>
    public const int MaxEvents = 64;

    public bool IsWellFormed() =>
        Events.Count is > 0 and <= MaxEvents && Events.All(e => e.IsWellFormed());
}

/// <summary>
/// One picture of the live view, and the shape it was taken at.
///
/// The size travels with the bytes because the consumer needs it to turn a tap
/// on its own screen back into a fraction, and because a provider that
/// re-renders at a different size mid-login would otherwise misplace every
/// event silently.
/// </summary>
public sealed record LiveFrame
{
    /// <summary>
    /// Monotonic per JOB, and the scope is the whole of it.
    ///
    /// This said "per session" and the wire said per job, so the two were built
    /// to the two readings and neither could see the other's. The connector
    /// keeps one slot per job and refuses a frame whose sequence is not higher
    /// than the one it holds; an agent counting from zero for each live view
    /// therefore had a SECOND view's frames silently discarded - answered 200,
    /// nothing logged - while the consumer went on displaying the last picture
    /// of the previous step. A human types a password into a stale login page
    /// and nothing anywhere says so.
    ///
    /// Per job, so a second view continues the count rather than restarting it.
    /// A consumer showing frame 40 ignores 39 arriving late.
    /// </summary>
    public required long Sequence { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>
    /// JPEG. Never PNG - though not for the reason first assumed: measured, a
    /// flat login form is actually SMALLER as PNG (6.3 KB against 9.1 KB), and
    /// it is the photographic case that decides this. A captcha with a picture
    /// in it is 14 KB as JPEG and 194 KB as PNG, and the stream has to survive
    /// its worst frame rather than its best.
    /// </summary>
    public required byte[] Bytes { get; init; }

    /// <summary>
    /// Whose page this is a picture of: <c>https://login.ah.nl</c>. Null when
    /// the agent could not establish one, which must read as "unknown" and
    /// never as "fine".
    ///
    /// This is the only anti-phishing affordance the streamed login has. The
    /// human is looking at a photograph of a page on a machine they cannot
    /// see, so there is no address bar and no padlock; without this, the sole
    /// claim about whose sign-in form they are typing a password into is the
    /// provider name THIS SIDE put above it, which is not evidence of anything.
    ///
    /// Per FRAME rather than per challenge, and that is the whole point. A
    /// login navigates - Albert Heijn goes from a form to an SMS step - and an
    /// origin captured once when the challenge was raised would still be on
    /// screen after the page moved somewhere else. An observation attached to
    /// one picture can only ever be stale by one frame.
    ///
    /// The ORIGIN, never the URL: a full address carries query parameters, and
    /// those carry tokens.
    /// </summary>
    public string? Origin { get; init; }
}

/// <summary>
/// What counts as an origin worth showing a human who is about to type a
/// password into a photograph.
///
/// Here rather than beside the wire header, because BOTH ends need it and they
/// must not disagree: the agent normalises what it observed before sending, and
/// the control plane normalises again on the way in - a separate process on
/// somebody else's machine having already checked is not a property this side
/// can assert about a header it received.
/// </summary>
public static class LiveOrigin
{
    /// <summary>
    /// A host is bounded at 253 characters by DNS, so anything past this is not
    /// a hostname anybody typed.
    /// </summary>
    public const int MaxLength = 300;

    /// <summary>
    /// The displayable origin of an absolute URL, or null when there is not one
    /// worth displaying. Null must reach the human as "unknown"; the one thing
    /// this may never do is return something reassuring about a page it could
    /// not identify.
    ///
    ///   * Origin only, so a query string carrying a token cannot ride along.
    ///   * http and https only. <c>about:blank</c>, <c>data:</c> and
    ///     <c>file:</c> are not somebody's sign-in page and must not be
    ///     displayed as though they were.
    ///   * Punycode, which is the part that earns its keep. Rendered as
    ///     unicode, <c>https://аh.nl</c> with a Cyrillic а is
    ///     indistinguishable from the real thing at any font size - and this
    ///     string exists precisely to be squinted at. Browsers show the ASCII
    ///     form for this exact reason; so does this.
    /// </summary>
    public static string? Normalize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > MaxLength) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return null;
        if (parsed.Scheme is not ("http" or "https")) return null;
        if (string.IsNullOrEmpty(parsed.IdnHost)) return null;

        var origin = parsed.IsDefaultPort
            ? string.Concat(parsed.Scheme, "://", parsed.IdnHost)
            : string.Concat(parsed.Scheme, "://", parsed.IdnHost, ":", parsed.Port.ToString(CultureInfo.InvariantCulture));

        return origin.Length > MaxLength ? null : origin;
    }
}
