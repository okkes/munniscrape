using System.Net;
using System.Text.Json;
using Connector.Kit.Errors;
using Connector.Kit.Security;

namespace ShopConnector.Adapters.Support;

/// <summary>A token response, in the only shape this service cares about.</summary>
internal sealed record TokenSet
{
    public required string AccessToken { get; init; }

    /// <summary>
    /// Null when the provider did not re-issue one. A refresh that returns
    /// no new refresh token means the old one is still current, so it must
    /// be carried forward rather than dropped.
    /// </summary>
    public string? RefreshToken { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// Reads the OAuth2 shape, tolerating the camelCase spellings these
    /// providers use alongside the standard snake_case ones.
    /// </summary>
    public static TokenSet Parse(JsonElement root, string providerId, TimeProvider time)
    {
        var access = JsonAccess.StrOf(root, "access_token", "accessToken");
        if (string.IsNullOrWhiteSpace(access))
        {
            throw ConnectorException.ProviderChanged($"{providerId}: token response carries no access token");
        }

        var expiresIn = JsonAccess.Quantity(root, "expires_in", "expiresIn");

        return new TokenSet
        {
            AccessToken = access,
            // Blank is absence, not a token. A provider re-issuing nothing may
            // say so by omitting the field, by sending null, or by sending "" -
            // and the empty string is the dangerous one, because it is truthy
            // to `??` and therefore replaces a perfectly good refresh token
            // with nothing. See Tokens.Present.
            RefreshToken = Tokens.Present(JsonAccess.StrOf(root, "refresh_token", "refreshToken")),
            // Renew a minute early: a token that expires in flight costs a
            // wasted round trip and an avoidable 401 in the logs.
            ExpiresAt = expiresIn is > 60 ? time.GetUtcNow().AddSeconds((double)expiresIn.Value - 60) : null,
        };
    }
}

/// <summary>
/// The one rule about a stored token that is easy to get wrong.
///
/// An empty or whitespace token is not a credential; it is a provider - or an
/// older bundle - saying nothing. Treating it as a value is how a permanent
/// connection quietly becomes a one-hour one: the refresh succeeds, "" is
/// sealed into the new bundle in place of a live refresh token, and everything
/// keeps working until the next access token expires days later, at which
/// point the user is told to sign in again and nothing in the logs explains
/// why. Normalising at both boundaries - what a provider hands us, and what a
/// bundle hands us - means no code after this point has to remember.
/// </summary>
internal static class Tokens
{
    public static string? Present(string? token) => string.IsNullOrWhiteSpace(token) ? null : token;
}

/// <summary>
/// A bearer token plus the single refresh-and-retry both token providers
/// need. One refresh, one retry: a second rejection means the refresh token
/// is dead too, and repeatedly presenting a dead session is how a provider
/// starts treating a client as hostile.
/// </summary>
internal sealed class BearerSession
{
    private readonly string _providerId;
    private readonly TimeProvider _time;

    public BearerSession(SessionMaterial? material, string providerId, TimeProvider time)
    {
        _providerId = providerId;
        _time = time;

        AccessToken = Tokens.Present(material?.AccessToken);
        RefreshToken = Tokens.Present(material?.RefreshToken);
        ExpiresAt = material?.AccessTokenExpiresAt;

        if (AccessToken is null && RefreshToken is null)
        {
            throw ConnectorException.SessionExpired($"{providerId}: session material carries no token");
        }
    }

    public string? AccessToken { get; private set; }

    public string? RefreshToken { get; private set; }

    public DateTimeOffset? ExpiresAt { get; private set; }

    /// <summary>True once a refresh produced material the caller must persist.</summary>
    public bool Rotated { get; private set; }

    public delegate Task<TokenSet> Refresher(string refreshToken, CancellationToken ct);

    public async Task<HttpResponseMessage> SendAsync(
        HttpClient http, Func<string, HttpRequestMessage> factory, Refresher refresh,
        string what, CancellationToken ct)
    {
        if (AccessToken is null || (ExpiresAt is { } expiry && expiry <= _time.GetUtcNow()))
        {
            await RefreshAsync(refresh, ct).ConfigureAwait(false);
        }

        var response = await ProviderHttp.SendAsync(http, factory(AccessToken!), _providerId, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;

        response.Dispose();
        await RefreshAsync(refresh, ct).ConfigureAwait(false);

        var retry = await ProviderHttp.SendAsync(http, factory(AccessToken!), _providerId, ct).ConfigureAwait(false);
        if (retry.StatusCode != HttpStatusCode.Unauthorized) return retry;

        retry.Dispose();
        throw ConnectorException.SessionExpired($"{_providerId}: {what} still rejected after a refresh");
    }

    /// <summary>
    /// The material to seal into a new bundle, or null when nothing rotated
    /// and the caller's stored bundle is still current.
    /// </summary>
    public SessionMaterial? RefreshedMaterial(SessionMaterial? original) => Rotated
        ? (original ?? new SessionMaterial()) with
        {
            AccessToken = AccessToken,
            RefreshToken = RefreshToken,
            AccessTokenExpiresAt = ExpiresAt,
        }
        : null;

    private async Task RefreshAsync(Refresher refresh, CancellationToken ct)
    {
        if (RefreshToken is not { } token)
        {
            throw ConnectorException.SessionExpired($"{_providerId}: access token expired and no refresh token was stored");
        }

        var refreshed = await refresh(token, ct).ConfigureAwait(false);

        AccessToken = refreshed.AccessToken;
        RefreshToken = refreshed.RefreshToken ?? token;   // no re-issue means the old one still stands
        ExpiresAt = refreshed.ExpiresAt;
        Rotated = true;
    }
}
