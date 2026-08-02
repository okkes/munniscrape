using System.Text.Json;
using Connector.Kit.Errors;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.Bol;

/// <summary>
/// The cookies out of a Playwright storage state, scoped the way a browser
/// would scope them.
///
/// This is the whole of bol's credential: a Java session cookie, no bearer
/// token, no refresh grant, nothing to exchange. So the two things that
/// matter are that it survives the round trip into a sealed bundle, and that
/// it is sent to the host it belongs to and no other.
///
/// The host match is deliberate rather than a suffix test. bol's login sets
/// <c>JSESSIONID</c> on <c>login.bol.com</c>; the orders page is on
/// <c>www.bol.com</c>. A browser would not send the first host's cookies to
/// the second unless the cookie says so with a leading dot, and neither does
/// this - because a fetch that only works when we over-send is a fetch that
/// is lying about which cookie authenticates it, and the first live run is
/// the only chance to learn that cheaply.
/// </summary>
internal static class BolCookies
{
    /// <summary>
    /// The <c>Cookie</c> header for a request to <paramref name="host"/>, or
    /// null when the stored session has nothing scoped to it.
    /// </summary>
    public static string? Header(string? storageState, string host)
    {
        var pairs = For(storageState, host);
        return pairs.Count == 0 ? null : string.Join("; ", pairs.Select(p => $"{p.Key}={p.Value}"));
    }

    /// <summary>
    /// One named cookie's value, or null when the session does not carry it.
    /// </summary>
    /// <remarks>
    /// bol double-submits its CSRF token - the value sits in a cookie and has
    /// to be echoed in a header - so one cookie genuinely needs reading on its
    /// own rather than only as part of the jar.
    /// </remarks>
    public static string? Value(string? storageState, string host, string name)
    {
        foreach (var pair in For(storageState, host))
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)) return pair.Value;
        }

        return null;
    }

    /// <summary>Every cookie a browser would send to <paramref name="host"/>.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> For(string? storageState, string host)
    {
        var cookies = new List<KeyValuePair<string, string>>();

        foreach (var (name, value, domain) in Read(storageState))
        {
            if (!DomainMatches(host, domain)) continue;
            cookies.Add(new KeyValuePair<string, string>(name, value));
        }

        return cookies;
    }

    /// <summary>
    /// Whether the stored state carries a cookie that looks like a session at
    /// all, anywhere under bol.com. Used to decide that a login landed, where
    /// the host scoping above is not yet the question.
    /// </summary>
    public static bool HasSession(string? storageState, BolOptions options)
    {
        var suffix = options.CookieDomainSuffix;

        foreach (var (name, _, domain) in Read(storageState))
        {
            var bare = domain.TrimStart('.');
            if (!string.Equals(bare, suffix, StringComparison.OrdinalIgnoreCase) &&
                !bare.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (options.SessionCookieNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>RFC 6265 domain matching, which is what a browser does.</summary>
    private static bool DomainMatches(string host, string domain)
    {
        var bare = domain.TrimStart('.');

        if (string.Equals(host, bare, StringComparison.OrdinalIgnoreCase)) return true;

        // A leading dot is the cookie asking to be sent to subdomains too.
        return domain.StartsWith('.') && host.EndsWith("." + bare, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<(string Name, string Value, string Domain)> Read(string? storageState)
    {
        if (string.IsNullOrWhiteSpace(storageState)) return [];

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(storageState);
        }
        catch (JsonException ex)
        {
            throw new ConnectorException(ErrorCode.SessionExpired, "bol: stored browser session is unreadable", ex);
        }

        using (document)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var cookies = new List<(string, string, string)>();

            foreach (var cookie in JsonAccess.Array(document.RootElement, "cookies"))
            {
                var name = JsonAccess.StrOf(cookie, "name");
                var value = JsonAccess.StrOf(cookie, "value");
                var domain = JsonAccess.StrOf(cookie, "domain");
                if (name is null || value is null || domain is null) continue;

                // A negative expiry marks a session cookie, which is exactly
                // the kind a Spring form login issues - it must not be
                // filtered out as "already expired".
                var expires = JsonAccess.Quantity(cookie, "expires");
                if (expires is > 0 && expires < now) continue;

                cookies.Add((name, value, domain));
            }

            return cookies;
        }
    }
}
