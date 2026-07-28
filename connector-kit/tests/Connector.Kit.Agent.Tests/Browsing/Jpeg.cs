namespace Connector.Kit.Agent.Tests;

/// <summary>
/// Reads a JPEG's own idea of how big it is.
///
/// Here because the size a frame ADVERTISES is what every returned coordinate
/// is multiplied by, so "the header says 390x844" is worth nothing unless the
/// pixels agree. Asking the encoder is the only way to check that; asking
/// Playwright again would just be asking the same thing twice.
/// </summary>
internal static class Jpeg
{
    /// <summary>True when these bytes begin the way a JPEG does and no other format does.</summary>
    public static bool Looks(byte[] bytes) =>
        bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;

    /// <summary>
    /// The dimensions from the frame header, walking the marker segments.
    ///
    /// Every start-of-frame marker is accepted, not only the baseline one:
    /// which of them an encoder emits is its business, and a parser that knew
    /// about only <c>SOF0</c> would report "not a JPEG" for a perfectly good
    /// progressive one and send a test hunting the wrong bug.
    /// </summary>
    public static (int Width, int Height) SizeOf(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (!Looks(bytes)) throw new ArgumentException("not a JPEG", nameof(bytes));

        var i = 2;
        while (i + 9 < bytes.Length)
        {
            if (bytes[i] != 0xFF)
            {
                i++;
                continue;
            }

            var marker = bytes[i + 1];
            var length = (bytes[i + 2] << 8) | bytes[i + 3];

            // C0-CF are the start-of-frame markers, minus C4 (Huffman tables),
            // C8 (reserved) and CC (arithmetic coding conditioning), which are
            // squatting in the same range and are not frames.
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                var height = (bytes[i + 5] << 8) | bytes[i + 6];
                var width = (bytes[i + 7] << 8) | bytes[i + 8];
                return (width, height);
            }

            if (length < 2) break;
            i += 2 + length;
        }

        throw new ArgumentException("this JPEG carries no frame header", nameof(bytes));
    }
}
