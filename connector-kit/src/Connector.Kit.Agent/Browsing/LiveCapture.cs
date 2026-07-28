namespace Connector.Kit.Agent.Browsing;

/// <summary>
/// One photograph of the whole visible page, and the size it really is.
///
/// The size is here for the same reason <see cref="RegionCapture"/> carries its
/// box: everything the human sends back is a FRACTION, and a fraction is
/// meaningless without the picture it was measured against. Handing the two out
/// together is what stops them drifting - a caller that photographs a page and
/// then asks the browser how big it is has already lost, because a phone that
/// rotated between the two calls answers about a different picture and every
/// event after it lands at the difference.
///
/// A refusal is <see cref="Refused"/>, which is also <c>default</c>, so a
/// capture nobody assigned and a capture the shutter declined mean the same
/// thing and neither can be mistaken for a frame.
/// </summary>
internal readonly record struct LiveCapture
{
    private readonly byte[]? _jpeg;

    private LiveCapture(byte[] jpeg, int width, int height)
    {
        _jpeg = jpeg;
        Width = width;
        Height = height;
    }

    /// <summary>No bytes and no size - what every refusal returns.</summary>
    public static LiveCapture Refused => default;

    /// <summary>
    /// A frame, or <see cref="Refused"/> when either half is missing, so
    /// <see cref="Captured"/> can carry a real invariant: bytes exist AND the
    /// rectangle they cover has a positive area.
    /// </summary>
    public static LiveCapture Of(byte[] jpeg, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(jpeg);

        if (jpeg.Length == 0) return Refused;
        if (width <= 0 || height <= 0) return Refused;

        return new LiveCapture(jpeg, width, height);
    }

    /// <summary>JPEG bytes. Empty for a refusal, never null.</summary>
    public byte[] Jpeg => _jpeg ?? [];

    /// <summary>The frame's width in CSS pixels, which is also its width in image pixels.</summary>
    public int Width { get; }

    /// <summary>The frame's height in CSS pixels, which is also its height in image pixels.</summary>
    public int Height { get; }

    public bool Captured => _jpeg is { Length: > 0 };
}
