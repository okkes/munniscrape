using System.Globalization;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Connector.Kit.Hosting.Data;

/// <summary>
/// Every timestamp is stored as a fixed-width UTC instant in ISO-8601 text.
///
/// This is not a stylistic choice. Sqlite refuses to ORDER BY a
/// <see cref="DateTimeOffset"/> at all - its native encoding keeps the offset,
/// which makes byte order and chronological order different things - so the
/// queue's "oldest job first", the newest-challenge lookup and the staged-row
/// ordering would all work on Postgres and throw in a test. Normalising to a
/// single zero-offset form of fixed length makes lexicographic order exactly
/// chronological order on both providers, keeps range comparisons correct,
/// and leaves the column readable to a human with a SQL prompt.
///
/// The invariant this depends on: nothing writes a non-UTC value. Every
/// timestamp in the platform comes from <c>TimeProvider.GetUtcNow()</c>, and
/// the conversion normalises anything that did not.
/// </summary>
public sealed class UtcInstantConverter() : ValueConverter<DateTimeOffset, string>(
    value => value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture),
    stored => DateTimeOffset.ParseExact(stored, Format, CultureInfo.InvariantCulture, Styles))
{
    /// <summary>Fixed width, always zero-offset - both properties are load-bearing for ordering.</summary>
    internal const string Format = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

    internal const DateTimeStyles Styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

    /// <summary>Length of <see cref="Format"/> rendered. Used to size the column.</summary>
    public const int StoredLength = 28;
}
