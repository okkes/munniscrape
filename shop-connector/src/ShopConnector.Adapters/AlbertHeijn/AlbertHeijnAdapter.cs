using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Connector.Kit.Adapters;
using Connector.Kit.Challenges;
using Connector.Kit.Errors;
using Connector.Kit.Jobs;
using Connector.Kit.Manifests;
using Connector.Kit.Normalization;
using Connector.Kit.Security;
using Microsoft.Playwright;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.AlbertHeijn;

/// <summary>
/// Albert Heijn - T2. A browser drives the OAuth login exactly once and the
/// refresh token serves every fetch after that over plain HTTP.
///
/// The predecessor asked the human to log in on AH's page, read a failed
/// <c>appie://</c> navigation out of their browser's console and paste it
/// back. There is no console on a phone, so that flow was unusable for most
/// of the people it was for. The agent now types the credentials and catches
/// the redirect itself; the paste survives only as the last resort, for the
/// case where the agent's browser meets a wall the human's would not.
/// </summary>
public sealed class AlbertHeijnAdapter : IProviderAdapter
{
    public const string ProviderId = "ah";
    public const string ReceiptsResource = "receipts";

    /// <summary>Undeclared on purpose - see <see cref="ObtainCodeAsync"/>.</summary>
    private const string RedirectInput = "redirect_url";

    /// <summary>Short probes: the settle loop runs them on every pass.</summary>
    private const int PageProbeMs = 500;

    private readonly ProviderManifest Manifest;

    private readonly AlbertHeijnOptions _options;
    private readonly TimeProvider _time;
    private readonly CaptchaGate _captcha;

    public AlbertHeijnAdapter(AlbertHeijnOptions? options = null, TimeProvider? time = null)
    {
        _options = options ?? new AlbertHeijnOptions();
        _time = time ?? TimeProvider.System;

        // Built from the options, so the flow the manifest declares and the
        // login this adapter actually performs cannot drift apart. A manifest
        // promising a password form for a login that never asks for one is a
        // consumer drawing two boxes nobody can fill.
        Manifest = AlbertHeijnManifest.Build(_options.LiveLogin);

        // The policy is shared; only the selectors and the patience are AH's.
        // Naming hCaptcha's two frames is what opts this provider into the
        // relay: the gate cannot tell a grid from the widget around it without
        // a claim about the markup, and that claim is a provider's to make.
        _captcha = new CaptchaGate(new CaptchaSpec
        {
            ProviderId = ProviderId,
            Interactive = _options.InteractiveCaptchaSelectors,
            Grid = _options.CaptchaGridSelectors,
            Checkbox = _options.CaptchaCheckboxSelectors,
            Image = _options.ImageCaptchaSelectors,
            Input = _options.CaptchaInputSelectors,
            ProbeMs = PageProbeMs,
            ImageSeconds = _options.ChallengeSeconds,
            InteractiveSeconds = _options.InteractiveCaptchaSeconds,
            RelayRounds = _options.CaptchaRelayRounds,
            PollSeconds = _options.RedirectPollSeconds,
        }, _time);
    }

    public ProviderManifest Describe() => Manifest;

    public async Task<LoginResult> LoginAsync(IJobContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var code = await ObtainCodeAsync(ctx, ct).ConfigureAwait(false);
        var tokens = await ExchangeAsync(ctx, code, ct).ConfigureAwait(false);

        ctx.Progress(JobStep.Finalizing);

        return new LoginResult
        {
            Material = new SessionMaterial
            {
                AccessToken = tokens.AccessToken,
                RefreshToken = tokens.RefreshToken,
                AccessTokenExpiresAt = tokens.ExpiresAt,
            },
            // A brand name is not prose and needs no translation. There is no
            // external id to show: nothing in the login response identifies
            // the member.
            Account = new ProviderAccount { DisplayName = Manifest.Name },
            // ExpiresAt is deliberately unset: the access token's own expiry
            // says nothing about how long the connection lasts, and the
            // refresh token keeps it alive well past it. Claiming the short
            // one would make the consumer prompt for a reconnect that is not
            // needed.
        };
    }

    public async Task<FetchResult> FetchAsync(IJobContext ctx, ResourceRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(request);

        if (!string.Equals(request.ResourceId, ReceiptsResource, StringComparison.Ordinal))
        {
            throw ConnectorException.Unsupported($"ah: no resource '{request.ResourceId}'");
        }

        var session = new BearerSession(ctx.Material, ProviderId, _time);
        ctx.Progress(JobStep.Downloading);

        var cap = Manifest.Resource(ReceiptsResource)!.MaxRecordsPerFetch;
        var (summaries, complete) = await ListAsync(ctx, session, request, cap, ct).ConfigureAwait(false);

        ctx.Progress(JobStep.Parsing);

        var receipts = new List<Receipt>(summaries.Count);

        foreach (var summary in summaries)
        {
            ct.ThrowIfCancellationRequested();

            IReadOnlyList<ReceiptItem> items = [];
            var payment = ReceiptFactory.Payment();

            if (request.WantsItems)
            {
                // The detail is the expensive call and the one most likely to
                // trip protection, so it only happens when items were asked
                // for.
                using var detail = await DetailAsync(ctx, session, summary.Id, ct).ConfigureAwait(false);

                items = AlbertHeijnReceiptParser.ParseItems(detail.RootElement, _options);
                payment = AlbertHeijnReceiptParser.ParsePayment(detail.RootElement, _options);
            }

            // The list's total against the detail's own lines is the one
            // redundancy AH gives us, and ReceiptFactory spends it: every
            // receipt leaves here with a reconciliation verdict on it.
            receipts.Add(ReceiptFactory.Build(
                ctx.SessionId,
                summary.Id,
                // The confirmed operations select no store, so there is no
                // store name to state. Null, not a guess.
                AlbertHeijnReceiptParser.Merchant(null),
                summary.PurchasedAt,
                summary.Total,
                payment,
                items));
        }

        ctx.Progress(JobStep.Normalizing);

        return new FetchResult
        {
            Receipts = receipts,
            RefreshedMaterial = session.RefreshedMaterial(ctx.Material),
            Complete = complete,
            Via = "graphql",
        };
    }

    /// <summary>Delegates: the same paste arrives here and at Lidl.</summary>
    internal static string? ExtractCode(string? candidate) => Support.RedirectCode.Extract(candidate);

    /// <summary>
    /// Confirmed shape: only the client id is substituted, and the redirect
    /// URI goes in verbatim exactly as the reference sends it.
    /// </summary>
    internal string AuthorizeUrl() => _options.AuthorizeUrlTemplate
        .Replace("{client_id}", Uri.EscapeDataString(_options.ClientId), StringComparison.Ordinal)
        .Replace("{redirect_uri}", _options.RedirectUri, StringComparison.Ordinal);

    // ---- login -------------------------------------------------------------

    /// <summary>
    /// Albert Heijn's own login page, streamed to whoever owns the account.
    ///
    /// This adapter types nothing. It opens the page, starts watching for the
    /// redirect, and raises a live view; the human signs in on the page in
    /// front of them, meets whatever AH puts in the way - a captcha, an SMS
    /// code, an app approval, something none of us has seen yet - and deals
    /// with it the way they would on their own laptop. The login finishes when
    /// the redirect arrives, and the redirect is the only thing this waits for.
    ///
    /// What that buys is the whole reason for it: no username and no password
    /// are asked for, so none is posted to the connector, written to a job's
    /// inputs, or held anywhere by anything here. The manifest declares no
    /// fields and the validator refuses one that does.
    ///
    /// <see cref="IJobContext.CredentialSubmitted"/> is deliberately NOT
    /// latched. It exists so a lost lease does not requeue a login whose
    /// password already counted against an account, and this adapter never
    /// submits one - the attempts are the human's own, on the provider's own
    /// page, exactly as if they had opened ah.nl themselves. Latching here
    /// would fail jobs permanently to protect against something that cannot
    /// happen.
    /// </summary>
    private async Task<string> LiveCodeAsync(IJobContext ctx, CancellationToken ct)
    {
        ctx.Progress(JobStep.OpeningProvider);

        var page = await ctx.Browser.PageAsync(ct).ConfigureAwait(false);
        await page.GotoAsync(AuthorizeUrl()).ConfigureAwait(false);

        // Attached before the human can touch anything. AH's redirect arrives
        // the instant the last step passes, and a watcher attached afterwards
        // misses the one event the whole login turns on.
        using var watcher = new RedirectWatcher(page, _options.RedirectUri, _time);

        ctx.Progress(JobStep.AwaitingHuman);

        // Raised and awaited on its own: the answer to a live view is the empty
        // one every passive challenge sends, and what actually ends this is the
        // redirect. Whichever comes first wins - somebody who signs in without
        // ever touching the consumer's UI again still finishes.
        var asked = ctx.AskAsync(new Challenge
        {
            Type = ChallengeType.LiveView,
            PromptKey = MessageKeys.LiveLogin,
            ExpiresAt = _time.GetUtcNow().AddSeconds(_options.LiveLoginSeconds),
        }, ct);

        var poll = TimeSpan.FromSeconds(_options.RedirectPollSeconds);
        var deadline = _time.GetUtcNow().AddSeconds(_options.LiveLoginSeconds);

        while (_time.GetUtcNow() < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (await watcher.WaitAsync(poll, ct).ConfigureAwait(false) is { } captured)
            {
                // Nobody will answer the live view now, and its expiry would
                // otherwise surface later as an unobserved failure on a job
                // that succeeded.
                _ = asked.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);

                ctx.Progress(JobStep.Authenticating);

                return ExtractCode(captured)
                       ?? throw ConnectorException.ProviderChanged(
                           "ah: the callback carried no authorization code");
            }

            if (asked.IsCompleted) break;
        }

        throw ConnectorException.Blocked(
            "ah: the live login ended without reaching the callback; the sign-in was not completed");
    }

    private async Task<string> ObtainCodeAsync(IJobContext ctx, CancellationToken ct)
    {
        // The escape hatch, and the only path that starts no browser: a host
        // that already holds a redirect - a native client that intercepted
        // the appie:// scheme itself - hands it straight over.
        //
        // Deliberately not a manifest field: it would put a third box on a
        // login form that exists to have two. The consequence is that the
        // control plane's endpoint refuses it as an undeclared input, so this
        // path belongs to a host driving the adapter directly. It costs one
        // lookup and it is the difference between needing an agent and not.
        if (ctx.Inputs.TryGetValue(RedirectInput, out var supplied) && ExtractCode(supplied) is { } handed)
        {
            ctx.Progress(JobStep.Authenticating);
            ctx.CredentialSubmitted();          // an OAuth code is single-use
            return handed;
        }

        if (_options.LiveLogin) return await LiveCodeAsync(ctx, ct).ConfigureAwait(false);

        var username = RequiredInput(ctx, "username");
        var password = RequiredInput(ctx, "password");

        ctx.Progress(JobStep.OpeningProvider);
        var page = await ctx.Browser.PageAsync(ct).ConfigureAwait(false);
        await page.GotoAsync(AuthorizeUrl()).ConfigureAwait(false);
        await DismissConsentAsync(page, ct).ConfigureAwait(false);

        ctx.Progress(JobStep.Authenticating);
        await PageOps.FillAsync(page, _options.UsernameSelectors, username, ProviderId, "e-mail field",
            _options.SelectorTimeoutMs, ct).ConfigureAwait(false);
        await PageOps.FillAsync(page, _options.PasswordSelectors, password, ProviderId, "password field",
            _options.SelectorTimeoutMs, ct).ConfigureAwait(false);

        // Before the click, never after: AH's redirect regularly lands while
        // the click is still being awaited, and a listener attached later
        // misses the one event the whole login turns on.
        using var watcher = new RedirectWatcher(page, _options.RedirectUri, _time);

        // Latch before the credentials leave the machine. After this a lost
        // lease fails the job rather than requeuing it: a retried login is
        // how an account gets locked.
        ctx.CredentialSubmitted();
        await PageOps.ClickAsync(page, _options.SubmitSelectors, ProviderId, "login submit",
            _options.SelectorTimeoutMs, ct).ConfigureAwait(false);

        // The same page instance, twice over: the login state lives in it, and
        // so does the widget the taps have to land in. A second page - or the
        // lease's next one - would be a different browsing context holding
        // none of it.
        var redirect = await SettleAsync(
            ctx, new PlaywrightLoginPage(page, Manifest), watcher, new MouseTapSurface(page), ct)
            .ConfigureAwait(false);

        // The URL itself is never quoted back: it carries a live credential.
        return ExtractCode(redirect)
               ?? throw ConnectorException.ProviderChanged("ah: the callback carried no authorization code");
    }

    /// <summary>
    /// Waits for the login to resolve into one of four outcomes: the
    /// redirect, a stated credential error, a captcha, or nothing
    /// recognisable.
    ///
    /// The last case is deliberately not <c>invalid_credentials</c>. Guessing
    /// "wrong password" from silence sends the user to reset a password that
    /// was fine, and a credential error is never retried - so the mistake is
    /// permanent for that session too.
    ///
    /// Internal rather than private so the offline suite can drive it through
    /// <see cref="ILoginPage"/>: every branch below decides how a human is
    /// treated, and none of them was reachable without a live account.
    /// </summary>
    internal Task<string> SettleAsync(
        IJobContext ctx, ILoginPage page, IRedirectWaiter watcher, CancellationToken ct) =>
        SettleAsync(ctx, page, watcher, taps: null, ct);

    /// <summary>
    /// As above, with somewhere for a relayed tap to land.
    /// </summary>
    /// <param name="taps">
    /// The page's mouse. Null means no relay is possible - a caller with no
    /// browser - and hCaptcha falls back to the person at the keyboard, or to
    /// a refusal when there is nobody there.
    /// </param>
    internal async Task<string> SettleAsync(
        IJobContext ctx, ILoginPage page, IRedirectWaiter watcher, ITapSurface? taps, CancellationToken ct)
    {
        var deadline = _time.GetUtcNow().AddSeconds(_options.LoginSettleSeconds);
        var poll = TimeSpan.FromSeconds(_options.RedirectPollSeconds);
        var captchaSeen = false;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (await watcher.WaitAsync(poll, ct).ConfigureAwait(false) is { } captured) return captured;

            // The page states a credential failure itself. This is the only
            // path that may report invalid_credentials, and nothing retries
            // it - by construction rather than by discipline here.
            var stated = await page.FindAsync(_options.LoginErrorSelectors, PageProbeMs, ct).ConfigureAwait(false);
            if (stated is not null)
            {
                throw ConnectorException.InvalidCredentials("ah: the login page stated a credential error");
            }

            if (!captchaSeen)
            {
                var wall = await _captcha.DetectAsync(page, ct).ConfigureAwait(false);

                if (wall.Kind is not CaptchaKind.None)
                {
                    captchaSeen = true;

                    var outcome = await _captcha
                        .FaceAsync(ctx, page, watcher, wall, taps, ct).ConfigureAwait(false);
                    if (outcome.Redirect is { } finished) return finished;
                    if (!outcome.Handled) break;

                    // The settle budget measures how long AH takes to answer,
                    // so it restarts once a wait on a human is over: the
                    // minutes they spent are not the provider being slow, and
                    // charging them to it fails a login that is about to work.
                    deadline = _time.GetUtcNow().AddSeconds(_options.LoginSettleSeconds);
                    continue;
                }
            }

            if (_time.GetUtcNow() >= deadline) break;
        }

        return await AskTheHumanAsync(ctx, captchaSeen, page.Url, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The last resort: the human finishes on AH's own page, in their own
    /// browser, and hands back the redirect. Their IP and their browser are
    /// the ones most likely to get past whatever stopped the agent - which is
    /// exactly the case this fallback exists for, and the only one it is
    /// reached in.
    /// </summary>
    private async Task<string> AskTheHumanAsync(
        IJobContext ctx, bool captchaSeen, string lastUrl, CancellationToken ct)
    {
        if (!_options.RedirectFallbackEnabled) throw Stuck(captchaSeen, lastUrl);

        ctx.Progress(JobStep.AwaitingHuman);

        var answer = await ctx.AskAsync(new Challenge
        {
            Type = ChallengeType.Redirect,
            PromptKey = MessageKeys.PasteRedirect,
            Url = AuthorizeUrl(),
            ReturnPattern = _options.RedirectUri + "*",
            ExpiresAt = _time.GetUtcNow().AddSeconds(_options.RedirectChallengeSeconds),
        }, ct).ConfigureAwait(false);

        return answer.Value.Length > 0 && ExtractCode(answer.Value) is { } code
            ? code
            : throw Stuck(captchaSeen, lastUrl);
    }

    private static ConnectorException Stuck(bool captchaSeen, string lastUrl) => captchaSeen
        ? ConnectorException.Blocked("ah: a captcha stood between the login and its redirect")
        : ConnectorException.ProviderChanged(
            $"ah: the login neither redirected nor stated an error; last url was '{lastUrl}'");

    private static string RequiredInput(IJobContext ctx, string key) =>
        ctx.Inputs.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw ConnectorException.InvalidRequest($"ah: '{key}' is required");

    private async Task DismissConsentAsync(IPage page, CancellationToken ct)
    {
        // Best effort: a consent wall left standing covers the form, and a
        // missing one is the normal case on a fresh profile.
        var accept = await PageOps.FindAsync(page, _options.ConsentSelectors, 2_000, ct).ConfigureAwait(false);
        if (accept is not null) await accept.ClickAsync().ConfigureAwait(false);
    }

    // ---- transport ---------------------------------------------------------

    private Task<TokenSet> ExchangeAsync(IJobContext ctx, string code, CancellationToken ct) =>
        PostTokenAsync(ctx, _options.TokenPath,
            new JsonObject
            {
                ["clientId"] = _options.ClientId,
                ["code"] = code,
            },
            "token exchange", ct);

    private Task<TokenSet> RefreshAsync(IJobContext ctx, string refreshToken, CancellationToken ct) =>
        PostTokenAsync(ctx, _options.RefreshPath,
            new JsonObject
            {
                ["clientId"] = _options.ClientId,
                ["refreshToken"] = refreshToken,
            },
            "token refresh", ct);

    private async Task<TokenSet> PostTokenAsync(
        IJobContext ctx, string path, JsonObject body, string what, CancellationToken ct)
    {
        using var request = Request(_options.ApiBaseUrl.TrimEnd('/') + path, accessToken: null, body.ToJsonString());

        using var response = await ProviderHttp.SendAsync(ctx.Http, request, ProviderId, ct).ConfigureAwait(false);

        // A rejected code or a dead refresh token is the session ending, not
        // a wrong password: whatever the human typed, AH already accepted it
        // to issue the code.
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.BadRequest)
        {
            throw ConnectorException.SessionExpired($"ah: {what} rejected ({(int)response.StatusCode})");
        }

        ProviderHttp.EnsureSuccess(response, ProviderId, what);

        using var document = await ProviderHttp.ReadJsonAsync(response, ProviderId, what, ct).ConfigureAwait(false);
        return TokenSet.Parse(document.RootElement, ProviderId, _time);
    }

    private async Task<(List<AhReceiptSummary> Rows, bool Complete)> ListAsync(
        IJobContext ctx, BearerSession session, ResourceRequest request, int cap, CancellationToken ct)
    {
        var pageSize = Math.Clamp(_options.PageSize, 1, cap);
        var collected = new List<AhReceiptSummary>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var complete = true;

        for (var offset = 0; ; offset += pageSize)
        {
            ct.ThrowIfCancellationRequested();

            using var document = await GraphQlAsync(ctx, session, _options.ListQuery, new JsonObject
            {
                ["offset"] = offset,
                ["limit"] = pageSize,
            }, "receipt list", ct).ConfigureAwait(false);

            var rows = AlbertHeijnReceiptParser.ParseList(document.RootElement, _options);
            if (rows.Count == 0) break;

            var fresh = 0;
            var older = 0;

            foreach (var row in rows)
            {
                if (!seen.Add(row.Id)) continue;
                fresh++;

                if (request.Since is { } since && DateOnly.FromDateTime(row.PurchasedAt.Date) < since) older++;
                if (ReceiptFactory.InWindow(row.PurchasedAt, request)) collected.Add(row);
            }

            // A page that repeats the previous one means the offset is not
            // advancing anything upstream; carrying on would be the same
            // request twenty times against a defended endpoint.
            if (fresh == 0) break;

            // The list runs newest first, so a page entirely older than the
            // window means every later page is too. Stopping here is what
            // keeps two years of history from being walked on every sync.
            if (older == rows.Count) break;

            // A short page is the end of the history.
            if (rows.Count < pageSize) break;

            // One page past the cap is enough to know the pass is partial;
            // more would be work the caller is about to discard.
            if (collected.Count > cap) break;
        }

        var ordered = collected.OrderByDescending(r => r.PurchasedAt).ToList();

        if (ordered.Count > cap)
        {
            // More remains than one pass may return. The caller gets a
            // partial pass and comes back for the rest under a fresh cursor.
            ordered = [.. ordered.Take(cap)];
            complete = false;
        }

        return (ordered, complete);
    }

    private Task<JsonDocument> DetailAsync(
        IJobContext ctx, BearerSession session, string receiptId, CancellationToken ct) =>
        GraphQlAsync(ctx, session, _options.DetailQuery, new JsonObject { ["id"] = receiptId }, "receipt detail", ct);

    private async Task<JsonDocument> GraphQlAsync(
        IJobContext ctx, BearerSession session, string query, JsonObject variables, string what, CancellationToken ct)
    {
        var url = _options.ApiBaseUrl.TrimEnd('/') + _options.GraphQlPath;

        // Confirmed: query and variables, and no operationName field.
        // Serialised once - the factory below runs a second time on a refresh
        // and must not depend on a node that has already been consumed.
        var body = new JsonObject { ["query"] = query, ["variables"] = variables }.ToJsonString();

        using var response = await session.SendAsync(ctx.Http,
            token => Request(url, token, body),
            (refresh, token) => RefreshAsync(ctx, refresh, token), what, ct).ConfigureAwait(false);

        // 403, 502 and 504 are bot protection refusing us, never a wrong
        // password. The body is kept on failure because a GraphQL 400 names
        // the field it rejected, which is the whole diagnosis.
        await ProviderHttp.EnsureSuccessAsync(
            response, ProviderId, what, ProviderHttp.RetailBlockStatuses, ct).ConfigureAwait(false);

        var document = await ProviderHttp.ReadJsonAsync(response, ProviderId, what, ct).ConfigureAwait(false);

        try
        {
            // GraphQL reports a failure in the body with a 200 status, so the
            // errors array is read before the data is trusted at all.
            AlbertHeijnReceiptParser.ThrowOnErrors(document.RootElement);
        }
        catch
        {
            document.Dispose();
            throw;
        }

        return document;
    }

    /// <summary>
    /// The header block <c>appie-go</c> sets on every api.ah.nl call. It is
    /// sent because the API needs it to route and to answer at all, not to
    /// disguise anything - the honest client identity the platform's posture
    /// allows and nothing beyond it. Omitting it is what the adapter used to
    /// do, and it is one of the reasons a real login failed.
    /// </summary>
    private HttpRequestMessage Request(string url, string? accessToken, string jsonBody)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
        };

        request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
        // The same literal as the client id in the reference; one option so
        // the two cannot drift apart in a way AH would notice.
        request.Headers.TryAddWithoutValidation("x-client-name", _options.ClientId);
        request.Headers.TryAddWithoutValidation("x-client-version", _options.ClientVersion);
        request.Headers.TryAddWithoutValidation("x-application", _options.Application);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return request;
    }
}
