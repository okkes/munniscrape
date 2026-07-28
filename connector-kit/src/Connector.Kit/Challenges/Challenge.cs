using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Serialization;

namespace Connector.Kit.Challenges;

/// <summary>
/// A question the provider asked that only the human who owns the account
/// can answer. Relaying these - never solving them - is the line between
/// acting as a user's agent and abuse.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ChallengeType>))]
public enum ChallengeType
{
    /// <summary>A visual challenge, including a CAPTCHA. Relayed to the human, never solved.</summary>
    Image,

    /// <summary>A QR the human scans with the provider's own app.</summary>
    QrDisplay,

    /// <summary>A code WE show that the human types into their device or app.</summary>
    CodeDisplay,

    /// <summary>A code the human received out of band and types back to us.</summary>
    MfaCode,

    /// <summary>Nothing to type - the human approves in an app and we wait.</summary>
    AppApproval,

    /// <summary>The human picks from a list, e.g. which profile or account.</summary>
    SelectOption,

    /// <summary>The human logs in on the provider's own site and hands back the redirect.</summary>
    Redirect,

    /// <summary>
    /// The provider's own page, live, driven by the human on their own device.
    ///
    /// The others are one question with one answer. This is a conversation:
    /// pictures go out many times a second and pointer and key events come
    /// back, until the login is done or nobody is there any more. It rides the
    /// challenge machinery anyway because everything that machinery provides -
    /// an expiry, a parked job budget, a renderer chosen by the consumer, a
    /// visible degradation when the consumer has never heard of it - is
    /// exactly what this needs too.
    ///
    /// Passive in the same sense as <see cref="AppApproval"/>: there is no
    /// string to type back. What ends it is the login completing, which the
    /// adapter watches for itself.
    ///
    /// The reason it exists is custody. A provider served this way declares no
    /// credential fields at all, so nothing is typed into a form of ours,
    /// nothing is written to a job's inputs, and there is no password at rest
    /// anywhere in the platform - the human types it into the provider's own
    /// page, exactly as they would on their own laptop.
    /// </summary>
    LiveView,
}

/// <summary>
/// What the human's answer will BE, which is not implied by
/// <see cref="ChallengeType"/>: an <see cref="ChallengeType.Image"/> is a
/// picture with a box beside it at one provider and a grid of tiles at the
/// next, and those are answered with two different things.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ChallengeAnswerKind>))]
public enum ChallengeAnswerKind
{
    /// <summary>
    /// A string the human types, or an empty one for a passive challenge.
    ///
    /// First, and therefore the default: a payload that omits the field, a
    /// challenge raised before this kind existed, and a consumer that has
    /// never heard of it all keep meaning exactly what they meant.
    /// </summary>
    Text,

    /// <summary>
    /// Points on the relayed image that the human tapped, carried as
    /// <see cref="TapAnswer"/>.
    ///
    /// An image grid cannot be answered in text, and this is the whole of
    /// what we do about that: the picture goes out, the taps come back, and
    /// the human - not us - is the one who solved it.
    /// </summary>
    Taps,
}

/// <summary>
/// What an adapter raises. The platform owns everything that happens next:
/// redaction, upload, webhook, the consumer's UI, and the answer coming
/// back.
/// </summary>
public sealed record Challenge
{
    public required ChallengeType Type { get; init; }

    /// <summary>
    /// What the answer must contain, so a consumer knows to render a tappable
    /// image rather than a text box.
    ///
    /// Defaults to <see cref="ChallengeAnswerKind.Text"/>, which is what every
    /// challenge already raised means and what a payload without the field
    /// deserialises to. A consumer that ignores it degrades to a text box over
    /// the picture: the human types something, <see cref="TapAnswer.TryParse"/>
    /// refuses it, and the adapter reports a challenge nobody answered. That is
    /// harmless. Inferring taps from whatever string came back would not be -
    /// it would click a live page at coordinates no human chose.
    /// </summary>
    [JsonPropertyName("answer_kind")]
    public ChallengeAnswerKind AnswerKind { get; init; } = ChallengeAnswerKind.Text;

    /// <summary>Consumer-owned copy key. Never prose.</summary>
    [JsonPropertyName("prompt_key")]
    public string? PromptKey { get; init; }

    /// <summary>
    /// Mandatory. A challenge holds a live browser hostage; a stale one must
    /// fail the job cleanly and release its agent rather than pinning it for
    /// the full job timeout.
    /// </summary>
    [JsonPropertyName("expires_at")]
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>For <see cref="ChallengeType.CodeDisplay"/>: the code the human must enter elsewhere.</summary>
    public string? Code { get; init; }

    /// <summary>For <see cref="ChallengeType.MfaCode"/>: <c>sms</c>, <c>totp</c>, <c>email</c>, <c>app</c>.</summary>
    public string? Delivery { get; init; }

    /// <summary>For <see cref="ChallengeType.MfaCode"/>: expected length, when known.</summary>
    public int? Length { get; init; }

    /// <summary>For <see cref="ChallengeType.SelectOption"/>.</summary>
    public IReadOnlyList<ChallengeOption>? Options { get; init; }

    /// <summary>For <see cref="ChallengeType.Redirect"/>: where to send the human.</summary>
    public string? Url { get; init; }

    /// <summary>
    /// For <see cref="ChallengeType.Redirect"/>: the pattern the human's
    /// browser will be sent to but cannot open, e.g. <c>appie://login-exit*</c>.
    /// </summary>
    [JsonPropertyName("return_pattern")]
    public string? ReturnPattern { get; init; }

    /// <summary>
    /// PNG bytes, already redacted. Never populated while a field marked
    /// secret holds content - see <see cref="ScreenshotRedactor"/>.
    /// </summary>
    [JsonIgnore]
    public byte[]? Image { get; init; }

    /// <summary>
    /// The region of the page the challenge occupies. Everything outside it
    /// is obscured before the image leaves the agent.
    /// </summary>
    [JsonIgnore]
    public CropRegion? Crop { get; init; }

    public bool IsExpired(DateTimeOffset now) => ExpiresAt <= now;

    /// <summary>True when the human types nothing and we simply wait.</summary>
    [JsonIgnore]
    public bool IsPassive => Type is ChallengeType.AppApproval or ChallengeType.LiveView;
}

public sealed record ChallengeOption
{
    public required string Value { get; init; }

    public required string Label { get; init; }
}

public readonly record struct CropRegion(int X, int Y, int Width, int Height);

/// <summary>
/// One tap on the relayed image, in NORMALISED coordinates - a fraction of
/// the image's width and height, never pixels.
///
/// This is the decision the whole feature rests on. Nobody agrees on the size
/// of that picture: the agent captures it at the browser's device scale
/// factor, and the client renders it at whatever its screen allows, so a
/// phone scales a 400px tile grid into a 360pt column. A pixel coordinate
/// measured on the client's copy therefore lands in the wrong square once the
/// agent maps it back. A fraction lands in the same square whatever either
/// end did to the bitmap.
/// </summary>
public readonly record struct Tap
{
    /// <summary>
    /// The wire's precision, and so the type's.
    ///
    /// 1/10 000 of the image is far finer than anything a finger can express -
    /// a tile in a 3x3 grid is 0.33 wide - and pinning it here is what makes a
    /// formatted tap survive its own round trip: a coordinate carrying more
    /// digits in memory than the wire can hold would compare unequal to the
    /// value it was just parsed from, and the two ends would be "agreeing" on
    /// numbers that are not the same.
    /// </summary>
    private const int Digits = 4;

    public Tap(double x, double y)
    {
        X = Normalised(x);
        Y = Normalised(y);
    }

    /// <summary>0 is the image's left edge, 1 its right.</summary>
    public double X { get; }

    /// <summary>0 is the image's top edge, 1 its bottom.</summary>
    public double Y { get; }

    /// <summary>
    /// This tap in page pixels, for the region the relayed image was cropped
    /// from - what the agent hands to the mouse.
    ///
    /// Lives here rather than on the agent so the mapping cannot disagree with
    /// the encoding: the crop's origin is part of the answer's meaning, and an
    /// agent that forgot to add it would click the top-left of the viewport
    /// with perfectly valid coordinates. When a whole page was relayed rather
    /// than a crop, pass the viewport as <c>new CropRegion(0, 0, w, h)</c>.
    /// </summary>
    public (double X, double Y) ToPagePixels(CropRegion region) =>
        (region.X + (X * region.Width), region.Y + (Y * region.Height));

    internal string Format() => string.Create(CultureInfo.InvariantCulture, $"{X:0.####},{Y:0.####}");

    private static double Normalised(double value, [CallerArgumentExpression(nameof(value))] string? name = null)
    {
        // Written as a range test rather than two comparisons because it is
        // also the NaN check: every comparison against NaN is false, so a
        // coordinate that arrived as one is rejected here instead of becoming
        // a click at an undefined point.
        if (value is not (>= 0 and <= 1))
        {
            throw new ArgumentOutOfRangeException(name, value,
                "a tap is a fraction of the relayed image, so each coordinate must be within 0..1");
        }

        return Math.Round(value, Digits, MidpointRounding.AwayFromZero);
    }
}

/// <summary>
/// The human's taps, and the one encoding they ride
/// <see cref="ChallengeAnswer.Value"/> in.
///
/// Two decisions are frozen here, because the agent and the client each
/// implement one half of them and a disagreement is a click in the wrong
/// square:
///
/// <b>The list is ordered.</b> Some grids ask for tiles in a given sequence,
/// and order is information the client already has and cannot be recovered
/// once dropped. Replaying taps in the order they were made is also simply
/// what a human did, which is the safest thing to reproduce.
///
/// <b>"Submit" is a terminal marker in the same answer, not a second
/// challenge.</b> A separate submit round would double the relay latency for
/// every grid - and an image captcha's token goes stale while we wait - but
/// the deciding reason is that a bare list cannot express one legal answer:
/// a grid where NOTHING matches, which the human answers by pressing verify
/// with nothing selected. With a marker, an empty tap list and an empty tap
/// list that is finished are different sentences. A client that wants to send
/// taps now and finish later simply leaves the marker off.
/// </summary>
public sealed record TapAnswer
{
    /// <summary>
    /// What makes a tap answer unmistakable inside a field that also carries
    /// SMS codes and redirect URLs, and versioned so a second grammar can be
    /// added without guessing which one a string is.
    /// </summary>
    public const string Prefix = "tap.v1:";

    private const string SubmitMarker = "submit";

    private const char Separator = ';';

    /// <summary>
    /// Ordered, and empty by default - which is a real answer, not a missing
    /// one. See <see cref="Submit"/>.
    /// </summary>
    public IReadOnlyList<Tap> Taps { get; init; } = [];

    /// <summary>
    /// True when the human is done and the page's verify control should be
    /// pressed after the taps are replayed.
    /// </summary>
    public bool Submit { get; init; }

    /// <summary>The encoded form, for <see cref="ChallengeAnswer.Value"/>.</summary>
    public string Format()
    {
        var builder = new StringBuilder(Prefix);

        for (var i = 0; i < Taps.Count; i++)
        {
            if (i > 0) builder.Append(Separator);
            builder.Append(Taps[i].Format());
        }

        if (Submit)
        {
            if (Taps.Count > 0) builder.Append(Separator);
            builder.Append(SubmitMarker);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Reads an encoded tap answer, or says no.
    ///
    /// Deliberately strict about everything except surrounding whitespace: a
    /// lenient parser here would accept a producer that is wrong about the
    /// format and turn the disagreement into clicks. False means "this is not
    /// a tap answer" - which is exactly what a text answer from a consumer
    /// that ignored <see cref="Challenge.AnswerKind"/> is, and the adapter
    /// treats it as a challenge nobody answered.
    /// </summary>
    public static bool TryParse(string? value, [NotNullWhen(true)] out TapAnswer? answer)
    {
        answer = null;
        if (value is null) return false;

        var text = value.Trim();
        if (!text.StartsWith(Prefix, StringComparison.Ordinal)) return false;

        var taps = new List<Tap>();
        var submit = false;
        var payload = text[Prefix.Length..].Trim();

        if (payload.Length > 0)
        {
            foreach (var part in payload.Split(Separator))
            {
                // Terminal means terminal: a tap after the marker is one the
                // page can no longer receive, so the producer is wrong about
                // the format rather than merely untidy.
                if (submit) return false;

                var token = part.Trim();
                if (string.Equals(token, SubmitMarker, StringComparison.Ordinal))
                {
                    submit = true;
                    continue;
                }

                var point = token.Split(',');
                if (point.Length != 2) return false;
                if (!TryCoordinate(point[0], out var x) || !TryCoordinate(point[1], out var y)) return false;

                taps.Add(new Tap(x, y));
            }
        }

        answer = new TapAnswer { Taps = taps, Submit = submit };
        return true;
    }

    /// <summary>
    /// Invariant culture, always. Half of Europe writes <c>0,5</c>, and a
    /// client that formatted a coordinate in its own locale would either be
    /// rejected here or - worse, if the separator were anything but the one
    /// splitting the pair - parse as a different point.
    /// </summary>
    private static bool TryCoordinate(string token, out double value) =>
        double.TryParse(token.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && value is >= 0 and <= 1;
}

/// <summary>The human's answer travelling back to a waiting adapter.</summary>
public sealed record ChallengeAnswer
{
    [JsonPropertyName("challenge_id")]
    public required string ChallengeId { get; init; }

    /// <summary>
    /// Empty for a passive challenge - the answer is simply that the human
    /// acted somewhere we cannot observe.
    ///
    /// A string for every answer kind, including taps: a wire type that
    /// changed shape for one challenge kind would change a payload every
    /// consumer already parses, for a case almost none of them will ever
    /// raise. Taps are encoded by <see cref="TapAnswer.Format"/> and read back
    /// by <see cref="TapAnswer.TryParse"/>, so an empty string still means "no
    /// answer" while <c>tap.v1:</c> means an answer that contains no taps -
    /// which is a different thing.
    /// </summary>
    public string Value { get; init; } = string.Empty;
}
