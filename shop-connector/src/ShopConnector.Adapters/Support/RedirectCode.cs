using System.Text.RegularExpressions;

namespace ShopConnector.Adapters.Support;

/// <summary>
/// Lifts the authorization code out of whatever the caller hands back.
///
/// Shared because every OAuth provider here ends the same way: the browser is
/// sent to a scheme it cannot open - <c>appie://login-exit</c>,
/// <c>com.lidlplus.app://callback</c> - and something on the consumer's side
/// reports where it was sent. What arrives depends entirely on what caught it:
///
///   * a native shell intercepting its own scheme hands over the whole URL
///   * a human copying an address bar hands over the whole URL, sometimes
///     with a stray space or a trailing newline
///   * a consumer that already parsed the query hands over the bare code
///
/// All three are the same intent, so all three are accepted rather than making
/// a person's successful sign-in fail on the shape of a paste.
/// </summary>
internal static partial class RedirectCode
{
    [GeneratedRegex(@"[?&]code=([^&\s]+)")]
    private static partial Regex InUrl { get; }

    /// <summary>
    /// The unreserved set from RFC 3986, which is what an authorization code
    /// is drawn from. Eight characters is well below any real code and well
    /// above anything typed by accident.
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z0-9._~-]{8,}$")]
    private static partial Regex Bare { get; }

    public static string? Extract(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;

        var trimmed = candidate.Trim();

        var match = InUrl.Match(trimmed);
        if (match.Success) return Uri.UnescapeDataString(match.Groups[1].Value);

        return Bare.IsMatch(trimmed) ? trimmed : null;
    }
}
