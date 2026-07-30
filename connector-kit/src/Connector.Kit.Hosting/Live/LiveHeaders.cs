using System.Globalization;
using Connector.Kit.Challenges;
using Connector.Kit.Errors;
using Microsoft.AspNetCore.Http;

namespace Connector.Kit.Hosting.Live;

/// <summary>
/// How a frame describes itself on the wire, written once.
///
/// The bytes are the body, so everything about them has to travel beside them:
/// which frame this is, and what shape it was taken at. Both ends need the
/// shape - the consumer to turn a tap on its own screen back into a fraction
/// of the picture, and the platform to notice a provider that re-rendered at a
/// different size mid-login instead of silently misplacing every event after
/// it.
/// </summary>
public static class LiveHeaders
{
    /// <summary>Monotonic per job. The consumer sends the last one it saw back as <c>after</c>.</summary>
    public const string Sequence = "X-Live-Sequence";

    /// <summary><c>390x844</c>. Pixels, because a size is the one thing that cannot be a fraction of itself.</summary>
    public const string Size = "X-Live-Size";

    /// <summary>
    /// Whose page the frame is of: <c>https://login.ah.nl</c>. Optional, and
    /// its absence means unknown rather than acceptable.
    /// </summary>
    public const string Origin = "X-Live-Origin";


    /// <summary>
    /// A frame is never worth re-reading and must never be written down by
    /// anything between here and the human's screen.
    /// </summary>
    public const string NoStore = "no-store";

    /// <summary>
    /// The only content type a frame slot ever holds, and the only one a frame
    /// is ever served as.
    ///
    /// Fixed rather than echoed from the POST on purpose: a response that
    /// repeated a content type the caller chose would let whatever the agent
    /// uploaded be re-interpreted by the consumer's browser as something other
    /// than a picture.
    /// </summary>
    public const string Jpeg = "image/jpeg";

    /// <summary>
    /// Reads <c>X-Live-Sequence</c>, or refuses. There is no sensible default:
    /// a frame with no sequence cannot be compared to the one already held, so
    /// it can neither replace it nor be skipped.
    /// </summary>
    public static long RequireSequence(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var raw = request.Headers[Sequence].ToString();
        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence)
            ? sequence
            : throw ConnectorException.InvalidRequest($"{Sequence} must be a whole number");
    }

    /// <summary>Reads <c>X-Live-Size</c> as <c>&lt;width&gt;x&lt;height&gt;</c>, or refuses.</summary>
    public static (int Width, int Height) RequireSize(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var raw = request.Headers[Size].ToString();
        var parts = raw.Split('x');

        if (parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height)
            && width > 0 && height > 0)
        {
            return (width, height);
        }

        throw ConnectorException.InvalidRequest($"{Size} must be '<width>x<height>' in whole pixels");
    }

    public static string FormatSize(int width, int height) =>
        string.Create(CultureInfo.InvariantCulture, $"{width}x{height}");

    public static string FormatSequence(long sequence) =>
        sequence.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads <c>X-Live-Origin</c>, or null. Optional rather than required: an
    /// agent that cannot establish an origin must still be able to deliver the
    /// picture, because a frame with an unknown origin is exactly the frame the
    /// human most needs to see labelled as such.
    ///
    /// Normalised here rather than trusted, because the value describes a page
    /// this platform does not control and a login can be navigated anywhere:
    ///
    ///   * origin only, so a query string carrying a token cannot ride along;
    ///   * http and https only - <c>about:blank</c>, <c>data:</c> and
    ///     <c>file:</c> are not somebody's sign-in page and must not be
    ///     displayed as though they were;
    ///   * PUNYCODE, which is the part that matters. Rendered as unicode,
    ///     <c>https://аh.nl</c> with a Cyrillic а is indistinguishable from the
    ///     real thing at any font size, and this string exists specifically to
    ///     be squinted at by somebody about to type their password. Browsers
    ///     show the ASCII form for exactly this reason and so does this.
    /// </summary>
    public static string? ReadOrigin(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Re-normalised rather than trusted, even though the agent normalised it
        // before sending. The agent is a separate process on somebody else's
        // machine, and "the other end already checked" is not a property this
        // side can assert about a header.
        return LiveOrigin.Normalize(request.Headers[Origin].ToString());
    }

    /// <summary>
    /// Reads the <c>after</c> query parameter, which every live poll takes and
    /// neither requires.
    ///
    /// Absent means "I have seen nothing", and that has to be a value below
    /// every sequence a caller can legally hold rather than zero - a consumer
    /// numbering its events from zero would otherwise never have its first
    /// event delivered, and would look like a broken pointer rather than a
    /// disagreement about counting.
    /// </summary>
    public static long After(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var raw = request.Query["after"].ToString();
        if (string.IsNullOrWhiteSpace(raw)) return long.MinValue;

        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var after)
            ? after
            : throw ConnectorException.InvalidRequest("after must be a whole number");
    }

    /// <summary>
    /// Reads the frame itself, bounded.
    ///
    /// The cap is enforced while reading rather than from
    /// <c>Content-Length</c>: a body may arrive without one, or with one that
    /// lies, and "we asked nicely how big it was" is not a bound on what ends
    /// up in memory.
    /// </summary>
    public static async Task<byte[]> ReadFrameAsync(HttpRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ContentLength > LiveChannel.MaxFrameBytes) throw TooLarge();

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];

        while (true)
        {
            var read = await request.Body.ReadAsync(chunk, ct);
            if (read == 0) break;

            if (buffer.Length + read > LiveChannel.MaxFrameBytes) throw TooLarge();
            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
        }

        return buffer.Length == 0
            ? throw ConnectorException.InvalidRequest("a live frame POST carries the image bytes as its body")
            : buffer.ToArray();
    }

    private static ConnectorException TooLarge() =>
        ConnectorException.InvalidRequest(
            $"a live frame is at most {LiveChannel.MaxFrameBytes} bytes");
}
