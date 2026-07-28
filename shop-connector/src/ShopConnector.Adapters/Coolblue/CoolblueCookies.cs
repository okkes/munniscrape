using System.Text.Json;
using Connector.Kit.Errors;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.Coolblue;

/// <summary>
/// The session cookies out of a Playwright storage state, for the day the
/// capture shows that Coolblue's order call is authenticated by its
/// <c>Coolblue-Session</c> cookie rather than by the OIDC access token.
///
/// Sent through the job's own politeness-limited client rather than the
/// browser's request context, because anything else bypasses the rate limiter.
/// </summary>
internal static class CoolblueCookies
{
    public static string? Header(string? storageState, string domainSuffix)
    {
        var pairs = Extract(storageState, domainSuffix);
        return pairs.Count == 0 ? null : string.Join("; ", pairs.Select(p => $"{p.Key}={p.Value}"));
    }

    public static IReadOnlyList<KeyValuePair<string, string>> Extract(string? storageState, string domainSuffix)
    {
        if (string.IsNullOrWhiteSpace(storageState)) return [];

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(storageState);
        }
        catch (JsonException ex)
        {
            throw new ConnectorException(
                ErrorCode.SessionExpired, $"{CoolblueAdapter.ProviderId}: stored browser session is unreadable", ex);
        }

        using (document)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var cookies = new List<KeyValuePair<string, string>>();

            foreach (var cookie in JsonAccess.Array(document.RootElement, "cookies"))
            {
                var name = JsonAccess.StrOf(cookie, "name");
                var value = JsonAccess.StrOf(cookie, "value");
                var domain = JsonAccess.StrOf(cookie, "domain");
                if (name is null || value is null || domain is null) continue;

                if (!domain.TrimStart('.').EndsWith(domainSuffix, StringComparison.OrdinalIgnoreCase)) continue;

                // A negative expiry marks a session cookie, and a session
                // cookie is exactly what a login issues - it must not be
                // filtered out as "already expired".
                var expires = JsonAccess.Quantity(cookie, "expires");
                if (expires is > 0 && expires < now) continue;

                cookies.Add(new KeyValuePair<string, string>(name, value));
            }

            return cookies;
        }
    }
}
