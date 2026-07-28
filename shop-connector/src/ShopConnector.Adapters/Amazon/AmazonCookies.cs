using System.Text.Json;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.Amazon;

/// <summary>
/// The one question worth asking of a Playwright storage state on this
/// provider: did the sign-in actually leave an authenticated session behind?
///
/// Amazon's sign-in chain is long enough that a page can look finished while
/// nothing was signed in - a challenge that was silently abandoned, a
/// redirect back to the order list that is really the sign-in return URL. The
/// reference names the marker outright:
/// <c>COOKIES_SET_WHEN_AUTHENTICATED = ["x-main"]</c>. Checking it is what
/// turns "the page looked right" into "there is a session", and the difference
/// is a bundle that fails on its first scheduled fetch instead of failing at
/// connect time where a human can see it.
/// </summary>
internal static class AmazonCookies
{
    public static bool HasAny(string? storageState, IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        if (string.IsNullOrWhiteSpace(storageState) || names.Count == 0) return false;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(storageState);
        }
        catch (JsonException)
        {
            // A storage state we cannot read is a storage state we cannot
            // vouch for. False, never an optimistic true.
            return false;
        }

        using (document)
        {
            foreach (var cookie in JsonAccess.Array(document.RootElement, "cookies"))
            {
                var name = JsonAccess.StrOf(cookie, "name");
                if (name is not null && names.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
            }
        }

        return false;
    }
}
