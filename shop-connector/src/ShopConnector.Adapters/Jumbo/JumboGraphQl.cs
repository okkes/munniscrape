using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Connector.Kit.Adapters;
using Connector.Kit.Errors;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.Jumbo;

/// <summary>
/// One GraphQL call.
///
/// There is no persisted-query variant and no hash. The working client posts
/// <c>{"query", "variables", "operationName"}</c> and never sends an
/// <c>extensions.persistedQuery</c> object; Jumbo's router accepts arbitrary
/// documents from an authenticated session. The APQ machinery that used to
/// live here was built around a handshake that is not in play, and blocking
/// the adapter on "capturing a persisted query hash" would have been waiting
/// for something that does not exist.
/// </summary>
public sealed record JumboGraphQlRequest
{
    public required string OperationName { get; init; }

    public required string Query { get; init; }

    public JsonObject Variables { get; init; } = new();
}

/// <summary>
/// The seam.
///
/// The transport is the one part that cannot be exercised offline - it needs
/// a live cookie against a defended endpoint - so it sits behind an interface
/// and everything above it runs against recorded payloads in CI.
/// </summary>
public interface IJumboGraphQlClient
{
    /// <summary>
    /// Runs one operation. <paramref name="deviceId"/> is optional: the
    /// confirmed working client sends no device header at all, so a session
    /// without one is served rather than refused.
    ///
    /// The caller owns the returned document's lifetime.
    /// </summary>
    Task<JsonDocument> ExecuteAsync(
        IJobContext ctx, JumboGraphQlRequest request, string? deviceId, CancellationToken ct);
}

/// <summary>
/// The real transport: the cookies from the browser login, sent through the
/// job's politeness-limited client rather than the browser's own request
/// context, because anything else bypasses the rate limiter.
/// </summary>
public sealed class HttpJumboGraphQlClient : IJumboGraphQlClient
{
    private readonly JumboOptions _options;

    public HttpJumboGraphQlClient(JumboOptions? options = null) => _options = options ?? new JumboOptions();

    public async Task<JsonDocument> ExecuteAsync(
        IJobContext ctx, JumboGraphQlRequest request, string? deviceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(request);

        var cookies = JumboCookies.Header(ctx.Material?.StorageState, _options.CookieDomainSuffix)
                      ?? throw ConnectorException.SessionExpired(
                          "jumbo: the stored browser session carries no jumbo.com cookies");

        using var message = new HttpRequestMessage(HttpMethod.Post, _options.GraphQlUrl)
        {
            Content = new StringContent(Body(request).ToJsonString(), Encoding.UTF8, "application/json"),
        };

        // The app headers the router needs to route, at the values the
        // confirmed working client sends. Sent because the API requires them,
        // not to disguise anything.
        message.Headers.TryAddWithoutValidation("apollographql-client-name", _options.ClientNameHeader);
        message.Headers.TryAddWithoutValidation("apollographql-client-version", _options.ClientVersionHeader);
        message.Headers.TryAddWithoutValidation("x-source", _options.SourceHeader);
        message.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
        message.Headers.TryAddWithoutValidation("Cookie", cookies);
        message.Headers.TryAddWithoutValidation("Accept", "application/json");

        // Optional, and known not to be required: the working client sends no
        // device header. A stable one is still carried when the session has
        // one, because a device that changes identity every run is a fraud
        // signal - but its absence is not a reason to refuse a fetch.
        if (_options.SendDeviceIdHeader && !string.IsNullOrWhiteSpace(deviceId))
        {
            message.Headers.TryAddWithoutValidation("jmb-device-id", deviceId);
        }

        using var response = await ProviderHttp
            .SendAsync(ctx.Http, message, JumboAdapter.ProviderId, ct).ConfigureAwait(false);

        // Akamai fronts www.jumbo.com. 403, 502 and 504 are it refusing us,
        // never a wrong password - and a fetch never had a password to be
        // wrong. Reporting invalid_credentials here would send a user to reset
        // a password that was fine and leave the block undiagnosed. The body
        // is kept because a GraphQL 400 names the field it rejected, which is
        // the whole diagnosis.
        await ProviderHttp.EnsureSuccessAsync(
            response, JumboAdapter.ProviderId, "graphql", ProviderHttp.RetailBlockStatuses, ct).ConfigureAwait(false);

        return await ProviderHttp
            .ReadJsonAsync(response, JumboAdapter.ProviderId, "graphql", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The body, in the only form Jumbo's router is known to take. No
    /// <c>extensions</c> block: there is no persisted-query handshake here and
    /// nothing to register.
    /// </summary>
    internal static JsonObject Body(JumboGraphQlRequest request) => new()
    {
        ["operationName"] = request.OperationName,
        ["query"] = request.Query,
        // Deep-cloned: a JsonNode may only have one parent, so reusing the
        // caller's object would detach it from their request.
        ["variables"] = request.Variables.DeepClone(),
    };
}

/// <summary>
/// The cookies out of a Playwright storage state. This is the whole of
/// Jumbo's credential on the web client: the Auth0 tokens are exchanged into
/// <c>user-session</c> server-side and the browser never holds them, so there
/// is nothing here to refresh or exchange.
/// </summary>
internal static class JumboCookies
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
            throw new ConnectorException(ErrorCode.SessionExpired, "jumbo: stored browser session is unreadable", ex);
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

                // A negative expiry marks a session cookie, which is exactly
                // the kind Jumbo's login issues - it must not be filtered out
                // as "already expired".
                var expires = JsonAccess.Quantity(cookie, "expires");
                if (expires is > 0 && expires < now) continue;

                cookies.Add(new KeyValuePair<string, string>(name, value));
            }

            return cookies;
        }
    }
}

/// <summary>
/// GraphQL transports a failure in the body with a 200 status, so the errors
/// array has to be read before the data is trusted.
/// </summary>
internal static class JumboGraphQlErrors
{
    public static void Throw(JsonElement root)
    {
        var errors = JsonAccess.Array(root, "errors");
        if (errors.Count == 0) return;

        var first = errors[0];
        var code = JsonAccess.TryProp(first, out var extensions, "extensions")
            ? JsonAccess.StrOf(extensions, "code")
            : null;
        var message = JsonAccess.StrOf(first, "message") ?? code ?? "unspecified";

        // An expired cookie reads as an auth error here, and "sign in again"
        // is the honest answer.
        //
        // Note what that is NOT saying. Jumbo's Auth0 tenant does advertise
        // refresh_token and offline_access, so a refresh path exists at the
        // OIDC layer - what does not exist is a refresh token in our hands,
        // because the web client's callback is server-side and the browser
        // only ever sees the resulting cookie. The behaviour is right; the old
        // comment's reason ("Jumbo has no refresh path") was wrong, and it
        // would have stopped anyone from revisiting a mobile-style public
        // client that does hand one over.
        if (Mentions(code, message, "UNAUTHENTICATED", "UNAUTHORIZED", "FORBIDDEN", "NOT_AUTHENTICATED"))
        {
            throw ConnectorException.SessionExpired($"jumbo: graphql rejected the session ({message})");
        }

        throw ConnectorException.ProviderChanged($"jumbo: graphql returned an error ({message})");
    }

    private static bool Mentions(string? code, string message, params string[] needles) =>
        needles.Any(n =>
            (code?.Contains(n, StringComparison.OrdinalIgnoreCase) ?? false) ||
            message.Contains(n, StringComparison.OrdinalIgnoreCase));
}
