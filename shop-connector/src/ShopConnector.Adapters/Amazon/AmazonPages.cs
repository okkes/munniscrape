using System.Net;
using Connector.Kit.Errors;
using Microsoft.Playwright;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.Amazon;

/// <summary>
/// One page as it came back: where we ended up, what status the navigation
/// carried, and the markup.
///
/// Status is kept alongside the HTML rather than thrown away, because on this
/// provider the status is half the diagnosis: a 200 can be a bot wall and a
/// 503 can be one too, and the two need different answers.
/// </summary>
internal readonly record struct AmazonPage(string Url, int Status, string Html);

/// <summary>
/// The seam.
///
/// Everything below this interface needs a real Chromium and a real account;
/// everything above it - the year walk, the pagination guard, the challenge
/// policy, the parsing, the money - is drivable from a recorded page. That
/// division is the only reason any of this is testable at all, because
/// amazon.nl cannot be exercised offline in any other way: there is no API to
/// stub.
/// </summary>
internal interface IAmazonPages
{
    Task<AmazonPage> OpenAsync(string url, CancellationToken ct);
}

/// <summary>
/// The live page source: navigate, and hand back what Amazon rendered.
/// </summary>
internal sealed class PlaywrightAmazonPages : IAmazonPages
{
    private readonly IPage _page;
    private readonly int _timeoutMs;

    public PlaywrightAmazonPages(IPage page, int timeoutMs)
    {
        ArgumentNullException.ThrowIfNull(page);

        _page = page;
        _timeoutMs = timeoutMs;
    }

    public async Task<AmazonPage> OpenAsync(string url, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        IResponse? response;
        try
        {
            response = await _page.GotoAsync(url, new PageGotoOptions
            {
                Timeout = _timeoutMs,
                // The order list is server-rendered; waiting for the network
                // to go idle on an Amazon page waits for advertising.
                WaitUntil = WaitUntilState.DOMContentLoaded,
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (PageOps.IsSelectorMiss(ex))
        {
            throw new ConnectorException(
                ErrorCode.ProviderUnavailable, $"{AmazonAdapter.ProviderId}: '{url}' did not load", ex);
        }

        var html = await _page.ContentAsync().ConfigureAwait(false);

        // A null response means Playwright served the navigation from a
        // same-document change and has no status to report. Zero is passed
        // through honestly rather than invented as 200: the guard reads it as
        // "no status was stated" and judges on the markup alone.
        return new AmazonPage(_page.Url, response?.Status ?? 0, html);
    }
}

/// <summary>What a fetched page turned out to be.</summary>
internal enum AmazonPageKind
{
    /// <summary>Data. Parse it.</summary>
    Ok,

    /// <summary>The sign-in chain. During a fetch that is a dead session.</summary>
    SignIn,

    /// <summary>A refusal with nothing to solve. blocked_by_provider.</summary>
    Blocked,

    /// <summary>The status itself was the answer; map it through the platform's table.</summary>
    HttpFailure,

    /// <summary>A widget: WAF or ACIC. Only a human at the browser can pass it.</summary>
    Interactive,

    /// <summary>A picture and a box: the one captcha a relay can carry.</summary>
    Image,
}

internal readonly record struct AmazonVerdict(AmazonPageKind Kind, string? Marker);

/// <summary>
/// Reads a fetched page and says what it is, before anything tries to parse
/// it as order history.
///
/// The ordering below is the whole of it. Bot protection is judged BEFORE the
/// sign-in chain, because Amazon serves its WAF challenge from inside that
/// chain and "you were challenged" is a different fact from "your session
/// died". And a challenge is never <c>invalid_credentials</c> under any
/// circumstances: telling somebody their password is wrong when the truth is
/// a bot wall sends them to reset a password that was fine and leaves the real
/// problem undiagnosed.
/// </summary>
internal static class AmazonGuard
{
    public static AmazonVerdict Inspect(AmazonPage page, HtmlNode dom, AmazonOptions options)
    {
        ArgumentNullException.ThrowIfNull(dom);
        ArgumentNullException.ThrowIfNull(options);

        // A stated non-success status is decided by the platform's own table,
        // widened by this provider's block list.
        if (page.Status is > 0 and (< 200 or >= 300))
        {
            return new AmazonVerdict(AmazonPageKind.HttpFailure, page.Status.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        }

        if (Marker(page.Html, options.BlockedBodyMarkers) is { } refusal)
        {
            return new AmazonVerdict(AmazonPageKind.Blocked, refusal);
        }

        if (HtmlQuery.First(dom, options.InteractiveCaptchaSelectors) is not null)
        {
            return new AmazonVerdict(AmazonPageKind.Interactive, "challenge container");
        }

        // Interactive before image, always: the ACIC page draws pictures of
        // its own ("choose all the buckets"), and classifying a widget as a
        // photograph asks a human for something nobody can give.
        var picture = HtmlQuery.First(dom, options.ImageCaptchaSelectors);
        if (picture is not null)
        {
            // A picture with nowhere to type the answer is as far out of reach
            // as a widget, so it is treated as one rather than relayed to
            // somebody who would have nothing to do with it.
            var input = HtmlQuery.First(dom, options.CaptchaInputSelectors);
            return new AmazonVerdict(
                input is null ? AmazonPageKind.Interactive : AmazonPageKind.Image, "captcha image");
        }

        if (Marker(page.Html, options.ChallengeBodyMarkers) is { } challenge)
        {
            // A JS challenge with no DOM we recognise. Nothing here can be
            // relayed, so it goes down the same path as a widget.
            return new AmazonVerdict(AmazonPageKind.Interactive, challenge);
        }

        if (Contains(page.Url, options.SignInUrlMarkers) is { } signIn)
        {
            return new AmazonVerdict(AmazonPageKind.SignIn, signIn);
        }

        return new AmazonVerdict(AmazonPageKind.Ok, null);
    }

    /// <summary>
    /// Turns a verdict into the exception the platform expects. Returned
    /// rather than thrown so the caller's control flow stays visible: every
    /// site that gives up on a page does it with a <c>throw</c> of its own.
    /// </summary>
    public static ConnectorException Failure(
        AmazonVerdict verdict, AmazonPage page, string what, AmazonOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return verdict.Kind switch
        {
            AmazonPageKind.HttpFailure => ProviderHttp.Failure(
                (HttpStatusCode)page.Status, AmazonAdapter.ProviderId, what, options.BlockStatuses),

            AmazonPageKind.Blocked => ConnectorException.Blocked(
                $"{AmazonAdapter.ProviderId}: {what} was refused by bot protection " +
                $"(marker '{verdict.Marker}'); this is a wall, not a credential problem"),

            AmazonPageKind.SignIn => ConnectorException.SessionExpired(
                $"{AmazonAdapter.ProviderId}: {what} landed on the sign-in chain " +
                $"('{verdict.Marker}'); the stored session is no longer accepted"),

            AmazonPageKind.Interactive or AmazonPageKind.Image => ConnectorException.Blocked(
                $"{AmazonAdapter.ProviderId}: {what} met a challenge ('{verdict.Marker}') and no human is " +
                "attending this browser; the widget can only be passed in a browser somebody can see and touch"),

            _ => new ConnectorException(
                ErrorCode.Internal, $"{AmazonAdapter.ProviderId}: {what} produced no verdict"),
        };
    }

    private static string? Marker(string html, IReadOnlyList<string> markers)
    {
        foreach (var marker in markers)
        {
            if (html.Contains(marker, StringComparison.OrdinalIgnoreCase)) return marker;
        }

        return null;
    }

    private static string? Contains(string url, IReadOnlyList<string> markers)
    {
        foreach (var marker in markers)
        {
            if (url.Contains(marker, StringComparison.OrdinalIgnoreCase)) return marker;
        }

        return null;
    }
}
