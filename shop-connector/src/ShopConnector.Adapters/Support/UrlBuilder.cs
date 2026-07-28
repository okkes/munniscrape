using System.Text;

namespace ShopConnector.Adapters.Support;

internal static class UrlBuilder
{
    /// <summary>
    /// Appends an escaped query. Hand-rolled because the alternatives live
    /// in ASP.NET packages this library has no business referencing, and
    /// because every value here (a redirect URI, a scope list) needs
    /// escaping that string concatenation gets wrong.
    /// </summary>
    public static string WithQuery(string baseUrl, IReadOnlyDictionary<string, string> query)
    {
        if (query.Count == 0) return baseUrl;

        var builder = new StringBuilder(baseUrl);
        builder.Append(baseUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?');

        var first = true;
        foreach (var (key, value) in query)
        {
            if (!first) builder.Append('&');
            first = false;

            builder.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
        }

        return builder.ToString();
    }
}
