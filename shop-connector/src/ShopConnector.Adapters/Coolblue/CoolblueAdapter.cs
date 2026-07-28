using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Connector.Kit.Adapters;
using Connector.Kit.Errors;
using Connector.Kit.Jobs;
using Connector.Kit.Manifests;
using Connector.Kit.Normalization;
using Connector.Kit.Security;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.Coolblue;

/// <summary>
/// Coolblue - T2, and deliberately half of one.
///
/// The login is the cleanest front door of any retailer researched: a
/// standards-compliant OpenID Connect authorization-code flow with S256 PKCE
/// at <c>accounts.coolblue.nl</c>, an <c>offline_access</c> scope, a real
/// <c>refresh_token</c> grant, no bot wall and no captcha on the form. All of
/// that is CONFIRMED from Coolblue's own public discovery document and its live
/// unauthenticated redirect chain, and all of it is implemented here.
///
/// The order fetch is not, and it is not guessed either. No public prior art
/// exists, <c>/graphql</c> is a 404, <c>api.coolblue.nl</c> does not resolve,
/// and both documented Coolblue APIs are seller-side dead ends. So
/// <see cref="FetchAsync"/> refuses - before any session is touched and before
/// a single packet leaves the machine - with a message that names exactly what
/// to capture in devtools and which option each capture fills in. See
/// <see cref="CoolblueFetchPlan"/>.
///
/// That refusal is the point. A half-adapter whose missing half is precisely
/// specified is worth far more than a guessed whole one: a guessed endpoint and
/// a guessed money unit produce receipts that look plausible, reconcile
/// perfectly against each other, and are wrong by a factor of a hundred.
/// </summary>
public sealed class CoolblueAdapter : IProviderAdapter
{
    public const string ProviderId = "coolblue";
    public const string ReceiptsResource = "receipts";

    /// <summary>
    /// Statuses that mean Coolblue is refusing us rather than failing.
    ///
    /// 429 is here and it is NOT in the shared default, which maps it to
    /// rate_limited. The divergence is deliberate: Coolblue sits behind
    /// CloudFront, an AWS WAF rate-based rule answers 429 with no Retry-After
    /// to honour, and treating a bot wall as "try again shortly" is precisely
    /// how a client earns a permanent one. None of these is ever
    /// invalid_credentials - telling somebody their password is wrong when the
    /// truth is a wall sends them to reset a password that was fine and leaves
    /// the block undiagnosed.
    /// </summary>
    internal static readonly IReadOnlySet<int> BlockStatuses = new HashSet<int> { 403, 429, 502, 504 };

    private static readonly ProviderManifest Manifest = CoolblueManifest.Build();

    private readonly CoolblueOptions _options;
    private readonly TimeProvider _time;
    private readonly CaptchaGate _captcha;

    public CoolblueAdapter(CoolblueOptions? options = null, TimeProvider? time = null)
    {
        _options = options ?? new CoolblueOptions();
        _time = time ?? TimeProvider.System;

        // The policy is shared with Albert Heijn and Lidl; only the selectors
        // and the patience are Coolblue's. No wall has ever been seen on this
        // form - the gate is here so that if one appears the adapter can name
        // it in one poll instead of waiting out a whole job budget.
        _captcha = new CaptchaGate(new CaptchaSpec
        {
            ProviderId = ProviderId,
            Interactive = _options.InteractiveCaptchaSelectors,
            Image = _options.ImageCaptchaSelectors,
            Input = _options.CaptchaInputSelectors,
            ProbeMs = _options.ProbeMs,
            ImageSeconds = _options.ChallengeSeconds,
            InteractiveSeconds = _options.InteractiveCaptchaSeconds,
            PollSeconds = _options.RedirectPollSeconds,
        }, _time);
    }

    public ProviderManifest Describe() => Manifest;

    public async Task<LoginResult> LoginAsync(IJobContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        // Everything that can fail without contacting anyone, checked before a
        // browser is leased. Launching Chromium to discover a blank field holds
        // an agent for nothing; the seam below checks again because it is the
        // entry point the offline suite drives.
        _ = RequiredInput(ctx, "username");
        _ = RequiredInput(ctx, "password");

        ctx.Progress(JobStep.OpeningProvider);
        var page = await ctx.Browser.PageAsync(ct).ConfigureAwait(false);

        // Attached before anything is typed, never after. Coolblue's redirect
        // URI is an ordinary https URL rather than a private scheme, so the
        // browser really does navigate to it - and it regularly lands while
        // the submit click is still being awaited.
        using var watcher = new RedirectWatcher(page, _options.RedirectUri, _time);

        var result = await LoginAsync(ctx, new PlaywrightLoginPage(page, Manifest), watcher, ct).ConfigureAwait(false);

        // Sealed alongside the tokens, and only here because only the live path
        // has a browser to ask. Which credential the order call needs is
        // unknown; this is the one that cannot be recovered later without a
        // second login, so it is captured while it exists.
        var storage = await StorageStateAsync(ctx, ct).ConfigureAwait(false);

        return storage is null ? result : result with { Material = result.Material with { StorageState = storage } };
    }

    /// <summary>
    /// The whole login, behind the seam.
    ///
    /// Internal rather than private so the offline suite can drive it: the PKCE
    /// pair, the state check on the callback and the credential latch all
    /// decide whether a real person can connect at all, and none of them is
    /// reachable without a live Chromium and a real account - which on this
    /// platform is a thing that may be spent exactly once and is never retried.
    /// </summary>
    internal async Task<LoginResult> LoginAsync(
        IJobContext ctx, ILoginPage page, IRedirectWaiter watcher, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(page);

        var username = RequiredInput(ctx, "username");
        var password = RequiredInput(ctx, "password");

        // Minted together and used minutes apart. The challenge travels in a
        // URL the browser opens; the verifier travels in a form body on the
        // other side of a whole page login. Holding them in one object is what
        // stops the second from being forgotten - which is exactly the bug that
        // failed every Lidl login before it was found.
        var pkce = PkceChallenge.Create();

        // state is CSRF protection for the callback and nonce binds the id
        // token. Both are in Coolblue's own authorize request, so both are
        // sent: dropping a parameter the real client sends is a way to get
        // answered differently for a reason nobody can reconstruct later.
        var state = Opaque();
        var nonce = Opaque();

        await page.GotoAsync(AuthorizeUrl(pkce, state, nonce), ct).ConfigureAwait(false);

        var redirect = await SignInAsync(ctx, page, watcher, username, password, ct).ConfigureAwait(false);
        var code = AuthorizationCode(redirect, state);

        var tokens = await ExchangeAsync(ctx, code, pkce.Verifier, ct).ConfigureAwait(false);

        ctx.Progress(JobStep.Finalizing);

        return new LoginResult
        {
            Material = new SessionMaterial
            {
                AccessToken = tokens.AccessToken,
                RefreshToken = tokens.RefreshToken,
                AccessTokenExpiresAt = tokens.ExpiresAt,
            },
            Account = await AccountAsync(ctx, tokens.AccessToken, ct).ConfigureAwait(false),
            // ExpiresAt is deliberately unset: the access token's own expiry is
            // an hour and says nothing about how long the CONNECTION lasts,
            // because the refresh token keeps it alive far past it. Claiming
            // the short one would make the consumer prompt for a reconnect
            // nobody needs.
        };
    }

    public async Task<FetchResult> FetchAsync(IJobContext ctx, ResourceRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(request);

        if (!string.Equals(request.ResourceId, ReceiptsResource, StringComparison.Ordinal))
        {
            throw ConnectorException.Unsupported($"{ProviderId}: no resource '{request.ResourceId}'");
        }

        // Checked first, on purpose: before the session material is read and
        // before a single packet leaves. A provider nobody has captured yet
        // must cost Coolblue nothing at all, and the refusal must be the same
        // whether or not this deployment happens to hold a valid session.
        var plan = CoolblueFetchPlan.Require(_options, request.WantsItems);

        ctx.Progress(JobStep.Downloading);

        // Only the bearer route can renew itself. A cookie is whatever the
        // browser was given and there is nothing to exchange it for, so a dead
        // one is session_expired rather than a refresh-and-retry.
        var session = plan.Credential == CoolblueOrderCredential.Bearer
            ? new BearerSession(ctx.Material, ProviderId, _time)
            : null;

        using var document = await OrdersAsync(ctx, plan, session, ct).ConfigureAwait(false);

        ctx.Progress(JobStep.Parsing);

        var orders = CoolblueOrderParser.Parse(
            document.RootElement, _options, plan, RetailZones.Dutch, request.WantsItems);

        var cap = Manifest.Resource(ReceiptsResource)!.MaxRecordsPerFetch;

        var windowed = orders
            .Where(o => ReceiptFactory.InWindow(o.PurchasedAt, request))
            .OrderByDescending(o => o.PurchasedAt)
            .ToList();

        var complete = windowed.Count <= cap;
        var selected = complete ? windowed : windowed.Take(cap).ToList();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var receipts = new List<Receipt>(selected.Count);

        foreach (var order in selected)
        {
            ct.ThrowIfCancellationRequested();
            if (!seen.Add(order.Id)) continue;

            // Through the factory, so every receipt leaves here with a
            // reconciliation verdict and a content hash on it. A receipt whose
            // lines do not sum to its stated total is flagged, never dropped -
            // dropping it hides a real purchase.
            receipts.Add(ReceiptFactory.Build(
                ctx.SessionId,
                order.Id,
                CoolblueOrderParser.Merchant(order.StoreName),
                order.PurchasedAt,
                order.Total,
                order.Payment,
                order.Items));
        }

        ctx.Progress(JobStep.Normalizing);

        return new FetchResult
        {
            Receipts = receipts,
            RefreshedMaterial = session?.RefreshedMaterial(ctx.Material),
            Complete = complete,
            // Which recipe answered, so the next breakage arrives half
            // diagnosed. Pagination is unknown, so a pass that hits the cap
            // reports itself partial rather than pretending to be the history.
            Via = plan.Credential == CoolblueOrderCredential.Bearer ? "orders-json/bearer" : "orders-json/cookie",
        };
    }

    /// <summary>
    /// The authorize request, with every parameter Coolblue's own client sends
    /// plus the PKCE pair it also sends.
    /// </summary>
    internal string AuthorizeUrl(PkceChallenge pkce, string state, string nonce)
    {
        ArgumentNullException.ThrowIfNull(pkce);

        return UrlBuilder.WithQuery(_options.AuthorizeUrl,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["client_id"] = _options.ClientId,
                ["response_type"] = "code",
                ["redirect_uri"] = _options.RedirectUri,
                // offline_access is in here and it is the whole T2 story: it is
                // what yields the refresh token, and the refresh token is what
                // makes this provider unattended.
                ["scope"] = _options.Scope,
                // RFC 7636. The server records this hash against the code it is
                // about to issue and refuses to exchange that code for anything
                // unless the verifier is produced - so omitting it does not
                // weaken the login, it fails it.
                ["code_challenge"] = pkce.Challenge,
                ["code_challenge_method"] = PkceChallenge.Method,
                ["state"] = state,
                ["nonce"] = nonce,
                ["ui_locales"] = _options.UiLocales,
            });
    }

    /// <summary>
    /// The code out of the callback, once the callback has proved it belongs to
    /// this login.
    /// </summary>
    internal static string AuthorizationCode(string redirectUrl, string expectedState)
    {
        var query = Pairs(new Uri(redirectUrl).Query.TrimStart('?'));

        if (query.TryGetValue("error", out var error))
        {
            var described = query.TryGetValue("error_description", out var description)
                ? $" ({description})"
                : string.Empty;

            // Deliberately NOT invalid_credentials. A wrong password never
            // leaves the login form - the authorization server only reaches the
            // callback once it has accepted the human - so an error here is
            // about this client, this redirect URI or this scope list, and
            // telling the user to change their password would be a lie they
            // cannot recover from, because a credential error is never retried.
            throw ConnectorException.ProviderChanged(
                $"{ProviderId}: the authorize request was refused with '{error}'{described}");
        }

        // The whole point of minting a state. A callback carrying somebody
        // else's state is not ours to spend, so the code is not exchanged at
        // all rather than exchanged hopefully.
        if (!query.TryGetValue("state", out var returned) ||
            !string.Equals(returned, expectedState, StringComparison.Ordinal))
        {
            throw ConnectorException.ProviderChanged(
                $"{ProviderId}: the callback's state did not match the one this login minted; " +
                "the authorization code was not exchanged");
        }

        return query.TryGetValue("code", out var code) && code.Length > 0
            ? code
            : throw ConnectorException.ProviderChanged($"{ProviderId}: the callback carried no authorization code");
    }

    /// <summary>
    /// The browser's cookies and local storage, when there is a browser and the
    /// options want them.
    ///
    /// Best effort by design: the tokens are already in hand at this point and
    /// the credential latch has closed, so failing the whole login over an
    /// optional extra would strand a user who has successfully signed in and
    /// cannot be retried.
    /// </summary>
    internal async Task<string?> StorageStateAsync(IJobContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (!_options.CaptureStorageState || !ctx.Browser.Started) return null;

        try
        {
            var state = await ctx.Browser.StorageStateAsync(ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(state) ? null : state;
        }
        catch (Exception ex) when (PageOps.IsSelectorMiss(ex))
        {
            // A page that closed under us, or a context that will not
            // serialise. The login stands; the extra does not.
            return null;
        }
    }

    // ---- login -------------------------------------------------------------

    /// <summary>
    /// Types the credentials into the one form that holds both and returns the
    /// callback URL.
    /// </summary>
    private async Task<string> SignInAsync(
        IJobContext ctx, ILoginPage page, IRedirectWaiter watcher,
        string username, string password, CancellationToken ct)
    {
        // Best effort: a consent wall left standing covers the form, and its
        // absence is the normal case on a fresh profile.
        _ = await page.ClickAsync(_options.ConsentSelectors, _options.ProbeMs, ct).ConfigureAwait(false);

        ctx.Progress(JobStep.Authenticating);

        if (!await page.FillAsync(_options.UsernameSelectors, username, _options.SelectorTimeoutMs, ct)
                .ConfigureAwait(false))
        {
            throw Missing("username field", _options.UsernameSelectors);
        }

        // One screen, both boxes - CONFIRMED from the capture of the authorize
        // landing page. Unlike Lidl there is no identifier-then-password
        // wizard, so a missing password box is a real shape change and never a
        // cue to click something and hope: clicking Next on a form that has no
        // second step submits the identifier alone and spends a login attempt.
        if (!await page.FillAsync(_options.PasswordSelectors, password, _options.SelectorTimeoutMs, ct)
                .ConfigureAwait(false))
        {
            throw Missing("password field", _options.PasswordSelectors);
        }

        // Latch before the credentials leave the machine. After this a lost
        // lease fails the job rather than requeuing it: a retried login is how
        // an account gets locked.
        ctx.CredentialSubmitted();

        if (!await page.ClickAsync(_options.SubmitSelectors, _options.SelectorTimeoutMs, ct).ConfigureAwait(false))
        {
            throw Missing("login submit", _options.SubmitSelectors);
        }

        return await SettleAsync(ctx, page, watcher, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for the login to resolve into one of four outcomes: the redirect,
    /// a stated credential error, a captcha, or nothing recognisable.
    ///
    /// The last case is deliberately not <c>invalid_credentials</c>. Guessing
    /// "wrong password" from silence sends the user to reset a password that
    /// was fine, and a credential error is never retried - so the mistake is
    /// permanent for that session too.
    ///
    /// There is no paste-the-redirect fallback here, and that is not an
    /// omission: Albert Heijn needs one because its callback is an
    /// <c>appie://</c> scheme the browser cannot follow, whereas Coolblue
    /// redirects to an ordinary https page. There is nothing for a human to
    /// read out of a console and hand back.
    /// </summary>
    internal async Task<string> SettleAsync(
        IJobContext ctx, ILoginPage page, IRedirectWaiter watcher, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(watcher);

        var deadline = _time.GetUtcNow().AddSeconds(_options.LoginSettleSeconds);
        var poll = TimeSpan.FromSeconds(_options.RedirectPollSeconds);
        var captchaSeen = false;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (await watcher.WaitAsync(poll, ct).ConfigureAwait(false) is { } captured) return captured;

            // The page states a credential failure itself. This is the only
            // path in the adapter that may report invalid_credentials, and
            // nothing retries it - by construction rather than by discipline.
            if (await page.FindAsync(_options.LoginErrorSelectors, _options.ProbeMs, ct).ConfigureAwait(false)
                is not null)
            {
                throw ConnectorException.InvalidCredentials(
                    $"{ProviderId}: the login page stated a credential error");
            }

            if (!captchaSeen)
            {
                var wall = await _captcha.DetectAsync(page, ct).ConfigureAwait(false);

                if (wall.Kind is not CaptchaKind.None)
                {
                    captchaSeen = true;

                    var outcome = await _captcha.FaceAsync(ctx, page, watcher, wall, ct).ConfigureAwait(false);
                    if (outcome.Redirect is { } finished) return finished;
                    if (!outcome.Handled) break;

                    // The settle budget measures how long Coolblue takes to
                    // answer, so it restarts once a wait on a human is over:
                    // the minutes they spent are not the provider being slow.
                    deadline = _time.GetUtcNow().AddSeconds(_options.LoginSettleSeconds);
                    continue;
                }
            }

            if (_time.GetUtcNow() >= deadline) break;
        }

        throw captchaSeen
            ? ConnectorException.Blocked(
                $"{ProviderId}: a captcha stood between the login and its redirect. None has ever been observed " +
                "on this form, so this is new and worth capturing")
            : ConnectorException.ProviderChanged(
                $"{ProviderId}: the login neither redirected to '{_options.RedirectUri}' nor stated an error; " +
                $"last url was '{page.Url}'");
    }

    /// <summary>
    /// The account label, so a user can tell two connections apart.
    ///
    /// The userinfo endpoint is CONFIRMED from the discovery document and is
    /// plain OIDC, so with the <c>email</c> and <c>profile</c> scopes it
    /// answers with something recognisable. Best effort in every sense: a
    /// display label is not worth failing a login that has already produced
    /// tokens and cannot be retried.
    /// </summary>
    private async Task<ProviderAccount> AccountAsync(IJobContext ctx, string accessToken, CancellationToken ct)
    {
        // A brand name is not prose and needs no translation.
        var fallback = new ProviderAccount { DisplayName = Manifest.Name };
        if (!_options.FetchUserInfo) return fallback;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _options.UserInfoUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await ProviderHttp.SendAsync(ctx.Http, request, ProviderId, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return fallback;

            using var document = await ProviderHttp
                .ReadJsonAsync(response, ProviderId, "userinfo", ct).ConfigureAwait(false);

            var root = document.RootElement;
            var name = JsonAccess.StrOf(root, "name", "given_name", "preferred_username", "email");

            return new ProviderAccount
            {
                DisplayName = string.IsNullOrWhiteSpace(name) ? fallback.DisplayName : name,
                // openid:customerid is in the scope list, so a customer id is
                // the value most likely to be stable across a name change.
                ExternalId = JsonAccess.StrOf(root, "customerid", "customer_id", "sub"),
            };
        }
        catch (ConnectorException)
        {
            return fallback;
        }
    }

    // ---- transport ---------------------------------------------------------

    private Task<TokenSet> ExchangeAsync(IJobContext ctx, string code, string verifier, CancellationToken ct) =>
        PostTokenAsync(ctx, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = _options.RedirectUri,
            // The other half of the proof the authorize call opened. The server
            // hashes this and compares it to the challenge stored against the
            // code; without it the exchange is refused outright.
            ["code_verifier"] = verifier,
        }, "token exchange", ct);

    private Task<TokenSet> RefreshAsync(IJobContext ctx, string refreshToken, CancellationToken ct) =>
        PostTokenAsync(ctx, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }, "token refresh", ct);

    private async Task<TokenSet> PostTokenAsync(
        IJobContext ctx, Dictionary<string, string> form, string what, CancellationToken ct)
    {
        // A public client names itself in the body; a confidential one in a
        // Basic header. Webshop redirects to an https URI from a browser and
        // sends S256 PKCE, which is the public shape - PKCE is what replaces a
        // secret - so the body is the default and the header is the override.
        if (_options.ClientSecret is null) form["client_id"] = _options.ClientId;

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenUrl)
        {
            Content = new FormUrlEncodedContent(form),
        };

        if (_options.ClientSecret is { } secret)
        {
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.ClientId}:{secret}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await ProviderHttp.SendAsync(ctx.Http, request, ProviderId, ct).ConfigureAwait(false);

        // Checked before the 400/401 reading below: a wall answers a token
        // endpoint the same way it answers anything else, and calling that a
        // dead session would send the user round a reconnect loop that cannot
        // succeed.
        if (BlockStatuses.Contains((int)response.StatusCode))
        {
            throw ConnectorException.Blocked($"{ProviderId}: {what} refused with {(int)response.StatusCode}");
        }

        // A rejected code or a dead refresh token is the session ending, not a
        // wrong password: whatever the human typed, Coolblue already accepted
        // it to issue the code.
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
        {
            throw ConnectorException.SessionExpired($"{ProviderId}: {what} rejected ({(int)response.StatusCode})");
        }

        ProviderHttp.EnsureSuccess(response, ProviderId, what);

        using var document = await ProviderHttp.ReadJsonAsync(response, ProviderId, what, ct).ConfigureAwait(false);
        return TokenSet.Parse(document.RootElement, ProviderId, _time);
    }

    private async Task<JsonDocument> OrdersAsync(
        IJobContext ctx, CoolblueFetchPlan plan, BearerSession? session, CancellationToken ct)
    {
        HttpResponseMessage response;

        if (session is not null)
        {
            response = await session.SendAsync(ctx.Http,
                token => Request(plan, token, cookies: null),
                (refresh, token) => RefreshAsync(ctx, refresh, token), "orders", ct).ConfigureAwait(false);
        }
        else
        {
            var cookies = CoolblueCookies.Header(ctx.Material?.StorageState, _options.CookieDomainSuffix)
                          ?? throw ConnectorException.SessionExpired(
                              $"{ProviderId}: the fetch is configured for cookie credentials but the stored " +
                              $"browser session carries no {_options.CookieDomainSuffix} cookies");

            using var message = Request(plan, accessToken: null, cookies);
            response = await ProviderHttp.SendAsync(ctx.Http, message, ProviderId, ct).ConfigureAwait(false);
        }

        using (response)
        {
            // The body is kept on failure: an error body is the single most
            // useful thing a provider hands back, and discarding it turns a
            // five-second fix into an afternoon against a live account.
            await ProviderHttp.EnsureSuccessAsync(response, ProviderId, "orders", BlockStatuses, ct)
                .ConfigureAwait(false);

            await EnsureJsonAsync(response, ct).ConfigureAwait(false);

            return await ProviderHttp.ReadJsonAsync(response, ProviderId, "orders", ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A 200 that is not JSON has exactly two readings, and calling either of
    /// them a parse failure buries the diagnosis.
    ///
    /// A bounce to the login page means the session is finished, and the user
    /// has to sign in again. An edge interstitial means we are being refused,
    /// and signing in again would change nothing. The login reading is tried
    /// first because Coolblue's own pages legitimately mention their CDN.
    /// </summary>
    private async Task EnsureJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;

        // No stated type at all: let the JSON parser have its say rather than
        // guess from a body we have not read.
        if (mediaType is null) return;
        if (mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)) return;

        var body = await SnippetAsync(response, ct).ConfigureAwait(false);

        if (Mentions(body, _options.LoginPageMarkers))
        {
            throw ConnectorException.SessionExpired(
                $"{ProviderId}: the orders call landed on the login page rather than answering JSON");
        }

        if (Mentions(body, _options.BlockPageMarkers))
        {
            throw ConnectorException.Blocked(
                $"{ProviderId}: the orders call answered an interstitial rather than JSON, which is what an " +
                "edge challenge looks like");
        }

        throw ConnectorException.ProviderChanged(
            $"{ProviderId}: the orders call answered '{mediaType}' rather than JSON. If the account page turns " +
            "out to be server-rendered HTML this adapter cannot read it - it parses JSON only");
    }

    /// <summary>
    /// The app headers, plus whatever the capture showed the real call carries.
    /// Sent because the endpoint needs them, not to disguise anything.
    /// </summary>
    private HttpRequestMessage Request(CoolblueFetchPlan plan, string? accessToken, string? cookies)
    {
        var request = new HttpRequestMessage(plan.Method, plan.Url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        if (cookies is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookies);
        }

        foreach (var (name, value) in _options.ExtraHeaders)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return request;
    }

    // ---- small helpers -----------------------------------------------------

    private static string RequiredInput(IJobContext ctx, string key) =>
        ctx.Inputs.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw ConnectorException.InvalidRequest($"{ProviderId}: '{key}' is required");

    private static ConnectorException Missing(string what, IReadOnlyList<string> selectors) =>
        ConnectorException.ProviderChanged(
            $"{ProviderId}: no element for the {what}; tried [{string.Join(", ", selectors)}]");

    /// <summary>
    /// 128 bits from the cryptographic generator. state and nonce are both
    /// anti-replay values: one predictable enough to guess is one that
    /// protects nothing.
    /// </summary>
    private static string Opaque() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));

    private static bool Mentions(string body, IReadOnlyList<string> markers) =>
        markers.Any(marker => body.Contains(marker, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Enough of a body to classify it. Bounded because a block page can be a
    /// megabyte of HTML, and best effort because the status and the content
    /// type are the finding - the body was a bonus.
    /// </summary>
    private static async Task<string> SnippetAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return text.Length > 4_000 ? text[..4_000] : text;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return string.Empty;
        }
    }

    private static Dictionary<string, string> Pairs(string encoded)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var pair in encoded.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0) continue;

            map[Uri.UnescapeDataString(pair[..separator])] =
                Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' '));
        }

        return map;
    }
}
