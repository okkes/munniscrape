using System.Net;
using System.Text.Json;
using Connector.Kit.Errors;

namespace ShopConnector.Adapters.Support;

/// <summary>
/// The one place a provider's HTTP status becomes a connector error.
///
/// Getting this mapping wrong is a real user-facing bug rather than a
/// tidiness issue: telling somebody their password is wrong when the truth
/// is bot protection sends them to reset a password that was fine, and
/// leaves the actual block undiagnosed. So a status only becomes
/// <see cref="ErrorCode.InvalidCredentials"/> where a caller has proved it
/// means that - never here.
/// </summary>
internal static class ProviderHttp
{
    /// <summary>
    /// Statuses a defended retail endpoint returns when it is refusing us
    /// rather than failing. 502 and 504 belong here because an edge that
    /// tarpits a request reports it as an upstream failure.
    /// </summary>
    public static readonly IReadOnlySet<int> RetailBlockStatuses = new HashSet<int> { 403, 502, 504 };

    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient http, HttpRequestMessage request, string providerId, CancellationToken ct)
    {
        try
        {
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ConnectorException(ErrorCode.ProviderUnavailable, $"{providerId}: transport failure", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // Cancellation we did not ask for is the client's own timeout.
            throw new ConnectorException(ErrorCode.ProviderUnavailable, $"{providerId}: request timed out", ex);
        }
    }

    /// <summary>
    /// Maps a non-success status. <paramref name="blockedStatuses"/> lets a
    /// provider declare which statuses mean "refusing us" for it
    /// specifically; everything else follows the default reading.
    /// </summary>
    public static ConnectorException Failure(
        HttpStatusCode status, string providerId, string what, IReadOnlySet<int>? blockedStatuses = null)
    {
        var code = (int)status;
        if (blockedStatuses is not null && blockedStatuses.Contains(code))
        {
            return ConnectorException.Blocked($"{providerId}: {what} refused with {code}");
        }

        return code switch
        {
            401 => ConnectorException.SessionExpired($"{providerId}: {what} rejected the session"),
            403 => ConnectorException.Blocked($"{providerId}: {what} refused with 403"),
            404 => ConnectorException.ProviderChanged($"{providerId}: {what} is gone (404)"),
            408 => new ConnectorException(ErrorCode.ProviderUnavailable, $"{providerId}: {what} timed out upstream"),
            429 => new ConnectorException(ErrorCode.RateLimited, $"{providerId}: {what} rate limited"),
            >= 500 => new ConnectorException(ErrorCode.ProviderUnavailable, $"{providerId}: {what} returned {code}"),
            _ => ConnectorException.ProviderChanged($"{providerId}: {what} returned an unexpected {code}"),
        };
    }

    public static void EnsureSuccess(
        HttpResponseMessage response, string providerId, string what, IReadOnlySet<int>? blockedStatuses = null)
    {
        if (response.IsSuccessStatusCode) return;
        throw Failure(response.StatusCode, providerId, what, blockedStatuses);
    }

    /// <summary>
    /// Same, but keeps what the provider actually said.
    ///
    /// An error body is the single most useful thing a provider ever hands
    /// back - a GraphQL 400 names the field it did not recognise, and often
    /// suggests the right one - and discarding it turns a five-second fix into
    /// an afternoon of guessing against a live account. The body is operator
    /// -facing detail, never shown to an end user, and is truncated because
    /// a block page can be a megabyte of HTML.
    ///
    /// Bounded, best effort, and never allowed to mask the original failure:
    /// if reading the body throws, the status-only error still goes out.
    /// </summary>
    public static async Task EnsureSuccessAsync(
        HttpResponseMessage response, string providerId, string what,
        IReadOnlySet<int>? blockedStatuses, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var failure = Failure(response.StatusCode, providerId, what, blockedStatuses);

        string? body = null;
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text))
            {
                body = text.Length > 600 ? string.Concat(text.AsSpan(0, 600), "...") : text;
                body = body.ReplaceLineEndings(" ");
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The status is the finding; the body was a bonus.
        }

        throw body is null
            ? failure
            : new ConnectorException(failure.Code, $"{failure.Detail}; provider said: {body}");
    }

    /// <summary>
    /// The caller owns the returned document's lifetime; every
    /// <see cref="JsonElement"/> read from it dies with it.
    /// </summary>
    public static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response, string providerId, string what, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        try
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            // Non-JSON where JSON was promised is almost always an
            // interstitial - a block page or a login redirect - so it is a
            // shape change worth paging someone about, not a parse bug.
            throw new ConnectorException(ErrorCode.ProviderChanged, $"{providerId}: {what} did not return JSON", ex);
        }
    }
}
