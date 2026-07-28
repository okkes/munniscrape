using Connector.Kit.Challenges;

namespace Connector.Kit.Agent.Browsing;

/// <summary>
/// A photograph of one region of the page, and the box it was cut from.
///
/// The box is the whole point. A relayed challenge comes back as
/// <see cref="Tap"/>s - fractions of the picture the human touched - and the
/// only way to turn a fraction back into a page pixel is to still know the
/// rectangle that picture was cut from. Handing the two out together is what
/// stops them drifting apart: a caller that photographs an element and then
/// measures it again has already lost, because a captcha widget re-lays itself
/// between the two calls and every tap is then off by the difference.
///
/// A refusal is <see cref="Refused"/>, which carries no bytes and no box. That
/// is deliberately NOT the same thing as a black image: the redactor's masking
/// produces a real PNG with a real box in which some element is blacked out,
/// and the human can see what was hidden and answer around it. A refusal means
/// no image was produced at all, nothing was relayed, and there is nothing for
/// a tap to be measured against - which is why <see cref="Captured"/>, and not
/// the look of the pixels, is the question every caller asks.
/// </summary>
public readonly record struct RegionCapture
{
    private readonly byte[]? _png;

    private RegionCapture(byte[] png, CropRegion region)
    {
        _png = png;
        Region = region;
    }

    /// <summary>
    /// No image and no box - what every refusal returns.
    ///
    /// Also the <c>default</c> value, on purpose: a capture nobody assigned and
    /// a capture the redactor refused mean the same thing, and neither can be
    /// mistaken for a picture.
    /// </summary>
    public static RegionCapture Refused => default;

    /// <summary>
    /// A capture, or <see cref="Refused"/> when either half is missing.
    ///
    /// The pair is validated here rather than trusted, so that
    /// <see cref="Captured"/> can carry a real invariant: bytes exist AND the
    /// box they came from has a positive area. Anything less cannot be mapped
    /// back into and must not look as if it could.
    /// </summary>
    public static RegionCapture Of(byte[] png, CropRegion region)
    {
        ArgumentNullException.ThrowIfNull(png);

        if (png.Length == 0) return Refused;
        if (region.Width <= 0 || region.Height <= 0) return Refused;

        return new RegionCapture(png, region);
    }

    /// <summary>The redacted PNG. Empty for a refusal, never null.</summary>
    public byte[] Png => _png ?? [];

    /// <summary>
    /// The box the PNG was cut from, in CSS pixels relative to the viewport -
    /// the same units <c>page.Mouse</c> takes, which is what makes
    /// <see cref="Tap.ToPagePixels"/> land where the human meant.
    /// </summary>
    public CropRegion Region { get; }

    /// <summary>True when there is an image AND a box to measure it against.</summary>
    public bool Captured => _png is { Length: > 0 };

    /// <summary>
    /// The smallest whole-pixel box that contains a browser bounding box, or
    /// null when there is nothing photographable there.
    ///
    /// Rounds OUTWARD - floor the origin, ceil the far edge - because a
    /// captcha's tiles run to the edge of its frame and a box that is truncated
    /// on both sides loses up to a pixel of the last row. Growing it by less
    /// than two pixels costs nothing: the taps are normalised against the box
    /// that is actually captured, so as long as the same box goes to the clip
    /// and to <see cref="Tap.ToPagePixels"/>, the extra margin cancels out.
    ///
    /// A negative origin is refused rather than clamped. An element scrolled
    /// half off the top of the viewport cannot be clipped by Playwright at all,
    /// and clamping would silently photograph a different rectangle than the
    /// one the taps would later be mapped into.
    /// </summary>
    internal static CropRegion? Enclosing(double x, double y, double width, double height)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) || !double.IsFinite(height))
        {
            return null;
        }

        if (width <= 0 || height <= 0) return null;
        if (x < 0 || y < 0) return null;

        // Nothing on a page is this big. The bound is not a policy, it is what
        // keeps a nonsense measurement from becoming a negative CropRegion when
        // the cast to int wraps.
        const double MaxPixel = 100_000;
        if (x + width > MaxPixel || y + height > MaxPixel) return null;

        var left = (int)Math.Floor(x);
        var top = (int)Math.Floor(y);
        var right = (int)Math.Ceiling(x + width);
        var bottom = (int)Math.Ceiling(y + height);

        return new CropRegion(left, top, right - left, bottom - top);
    }
}
