using System.Net;
using System.Net.Sockets;
using System.Text;
using Connector.Kit.Errors;

namespace ShopConnector.Adapters.Support;

/// <summary>
/// The shop a multi-tenant platform adapter has been pointed at.
///
/// Every other provider in this service talks to one host that the adapter
/// hard-codes. A platform adapter does not: one adapter serves every shop
/// running Magento or WooCommerce, so the host arrives from the user as
/// configuration. That is a different threat model, and this is where the
/// difference is handled - the control plane is about to make a server-side
/// request to an address a caller chose, which is a confused deputy waiting
/// to happen unless somebody says no to the addresses that are not shops.
/// </summary>
internal static class ShopBaseUrl
{
    /// <summary>
    /// Link-local, where a cloud instance metadata service lives. Named
    /// rather than folded into the private-range check because this is the
    /// one an SSRF is actually aimed at.
    /// </summary>
    private static readonly IPAddress MetadataV4 = IPAddress.Parse("169.254.169.254");

    /// <summary>
    /// Hostnames that only ever resolve inside a machine or a cluster. A
    /// shop is on the public internet; anything here is our own estate.
    /// </summary>
    private static readonly string[] InternalSuffixes =
    [
        "localhost", ".localhost", ".local", ".internal", ".intranet", ".cluster.local",
    ];

    /// <summary>
    /// Parses what the user typed into a base URL we are willing to call.
    ///
    /// A bare host is accepted and promoted to https, because "wibra.nl" is
    /// what a person types and refusing it teaches nothing. Plain http is
    /// refused by default: a WooCommerce order key is a bearer capability,
    /// and putting one in a cleartext query string hands the order to
    /// anybody on the path.
    /// </summary>
    public static Uri Parse(string? raw, string providerId, string fieldKey, bool allowPlainHttp = false)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw ConnectorException.InvalidRequest($"{providerId}: '{fieldKey}' is required");
        }

        var text = raw.Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            throw ConnectorException.InvalidRequest($"{providerId}: '{fieldKey}' is not a URL");
        }

        var isHttps = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal);
        var isHttp = string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal);

        if (!isHttps && !(isHttp && allowPlainHttp))
        {
            throw ConnectorException.InvalidRequest(
                $"{providerId}: '{fieldKey}' must be https - an order key in a cleartext query string is a leaked order");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            // Credentials in the host part are never a shop, and they would
            // travel to whatever the host part really points at.
            throw ConnectorException.InvalidRequest($"{providerId}: '{fieldKey}' must not carry userinfo");
        }

        if (string.IsNullOrEmpty(uri.Host)) throw ConnectorException.InvalidRequest($"{providerId}: '{fieldKey}' has no host");

        EnsurePublic(uri, providerId, fieldKey);
        return uri;
    }

    /// <summary>
    /// Appends a path to the shop's base, keeping any prefix the shop lives
    /// under. <c>new Uri(base, path)</c> would throw away <c>/shop</c> in
    /// <c>https://example.nl/shop</c>, which is a real deployment.
    /// </summary>
    public static string Endpoint(Uri baseUrl, string path)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        var root = baseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return path.StartsWith('/') ? root + path : root + "/" + path;
    }

    /// <summary>
    /// The shop's identity for a <see cref="Connector.Kit.Normalization.Merchant"/>.
    ///
    /// A platform adapter must not label every receipt "magento": the user
    /// bought from a shop, not from a platform, and a consumer that cannot
    /// tell two shops apart has lost the only fact that makes the record
    /// useful. The host is the one stable, unique name we have.
    /// </summary>
    public static string MerchantId(Uri baseUrl)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return baseUrl.Host.ToLowerInvariant();
    }

    private static void EnsurePublic(Uri uri, string providerId, string fieldKey)
    {
        var host = uri.Host.TrimEnd('.').ToLowerInvariant();

        foreach (var suffix in InternalSuffixes)
        {
            var hit = suffix.StartsWith('.')
                ? host.EndsWith(suffix, StringComparison.Ordinal)
                : string.Equals(host, suffix, StringComparison.Ordinal);

            if (hit) throw Refused(providerId, fieldKey);
        }

        if (!IPAddress.TryParse(uri.Host, out var address)) return;

        if (IPAddress.IsLoopback(address) ||
            address.Equals(MetadataV4) ||
            address.IsIPv6LinkLocal ||
            address.IsIPv6SiteLocal ||
            address.IsIPv6UniqueLocal ||
            IsPrivateV4(address))
        {
            throw Refused(providerId, fieldKey);
        }
    }

    private static bool IsPrivateV4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;

        var octets = address.GetAddressBytes();
        return octets[0] switch
        {
            10 => true,
            127 => true,
            169 when octets[1] == 254 => true,
            172 when octets[1] >= 16 && octets[1] <= 31 => true,
            192 when octets[1] == 168 => true,
            _ => false,
        };
    }

    private static ConnectorException Refused(string providerId, string fieldKey) =>
        ConnectorException.InvalidRequest(
            $"{providerId}: '{fieldKey}' points inside our own network, not at a shop");
}

/// <summary>
/// Telling "the edge is refusing us" apart from "the shop answered".
///
/// Both platform adapters call hosts nobody vetted, and a long tail of them
/// sit behind Cloudflare, Akamai, Wordfence or Sucuri. Reporting one of
/// those as a credential failure would tell a user their order number is
/// wrong when the truth is that a bot wall never let the request through -
/// they would go and re-read an e-mail that was correct, and the actual
/// block would stay undiagnosed. So the evidence is looked for explicitly.
///
/// Note what is deliberately <em>not</em> evidence: <c>server: cloudflare</c>
/// on its own. A large share of ordinary shops are proxied by Cloudflare and
/// answer perfectly; treating the header as a wall would fail every one of
/// them.
/// </summary>
internal static class BotWall
{
    /// <summary>
    /// Statuses that mean "refusing us" for an unauthenticated read of a
    /// public shop endpoint.
    ///
    /// 429 is here rather than left to map to <c>rate_limited</c> because
    /// neither platform's core rate-limits the route we call: WooCommerce's
    /// Store API order route has no limiter, and Magento's <c>/graphql</c>
    /// has none either. A 429 therefore comes from something the host bolted
    /// on to keep bots out, which is a wall and not a queue.
    ///
    /// 401 and 404 are absent on purpose: both platforms use them for their
    /// own "that reference does not match an order" answer, so they are read
    /// from the body before any status mapping happens.
    /// </summary>
    public static readonly IReadOnlySet<int> Statuses = new HashSet<int> { 403, 429, 503 };

    /// <summary>Headers only an interstitial or a mitigation sets.</summary>
    private static readonly string[] Headers =
    [
        "cf-mitigated", "cf-chl-bypass", "x-sucuri-block", "x-iinfo", "akamai-grn",
    ];

    /// <summary>
    /// Body markers, lower-cased. Every one of these is a challenge page or
    /// a block page, not a shop.
    /// </summary>
    private static readonly string[] Bodies =
    [
        "just a moment", "attention required", "checking your browser", "cf-browser-verification",
        "access denied", "request unsuccessful", "incapsula", "wordfence", "you have been blocked",
        "enable javascript and cookies to continue",
    ];

    public static bool Suspects(HttpResponseMessage response, string? body)
    {
        ArgumentNullException.ThrowIfNull(response);

        foreach (var header in Headers)
        {
            if (response.Headers.Contains(header)) return true;
        }

        if (string.IsNullOrEmpty(body)) return false;

        // Only the head of the body: a block page states its business in the
        // title, and a real order payload is not going to be searched at
        // length for words that would only ever be a false positive.
        var head = (body.Length > 2_048 ? body[..2_048] : body).ToLowerInvariant();

        foreach (var marker in Bodies)
        {
            if (head.Contains(marker, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>
    /// Reads a response body with a ceiling.
    ///
    /// A shop is an address the user chose, so the honest assumption is that
    /// it may answer with a gigabyte. The bodies these adapters expect are a
    /// few kilobytes; anything past the ceiling is not an order.
    /// </summary>
    public static async Task<string> ReadBoundedAsync(
        HttpResponseMessage response, int maxBytes, string providerId, string what, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(response);

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var buffer = new byte[8_192];
        var collected = new MemoryStream();

        while (collected.Length <= maxBytes)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) return Encoding.UTF8.GetString(collected.GetBuffer(), 0, (int)collected.Length);

            collected.Write(buffer, 0, read);
        }

        throw ConnectorException.ProviderChanged(
            $"{providerId}: {what} returned more than {maxBytes} bytes; that is not an order");
    }
}
