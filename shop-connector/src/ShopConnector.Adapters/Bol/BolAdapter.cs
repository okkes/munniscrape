using Connector.Kit.Adapters;
using Connector.Kit.Challenges;
using Connector.Kit.Errors;
using Connector.Kit.Jobs;
using Connector.Kit.Manifests;
using Connector.Kit.Normalization;
using Connector.Kit.Security;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.Bol;

/// <summary>
/// bol.com - the largest marketplace in the Netherlands, and the one this
/// project was most likely to build wrong.
///
/// <b>api.bol.com is not this.</b> bol's documented, well-clienced, pleasant
/// REST API is a SELLER API. It authenticates with <c>client_credentials</c>,
/// a grant that carries no user context at all, so <c>GET /retailer/orders</c>
/// answers "which orders were placed with this merchant" - a different
/// person's data, returned confidently, in the right shape. Every bol-related
/// library on GitHub is a client for it, including one whose README says it
/// "gets the orders from your bol.com account" and whose source is
/// <c>client_credentials</c> against the retailer host. Nothing in this file
/// touches api.bol.com, and nothing ever should.
///
/// The consumer route is a browser login and a cookie: <c>login.bol.com/wsp/login</c>
/// posts <c>j_username</c> and <c>j_password</c> - Spring Security's own field
/// names - and sets <c>JSESSIONID</c>. That makes this T2: a browser signs in
/// once, and every fetch afterwards is plain HTTP carrying the cookie jar.
///
/// One thing about the fetch is genuinely unknown and cannot be settled
/// without an account: whether the order list is server-rendered HTML or a
/// shell around a JSON endpoint. Both are implemented behind
/// <see cref="IBolOrdersShape"/>, an option chooses, HTML is the default
/// because the page is known to exist, and every failure names the shape it
/// tried so the first live run diagnoses itself.
/// </summary>
public sealed class BolAdapter : IProviderAdapter
{
    public const string ProviderId = "bol";
    public const string ReceiptsResource = "receipts";

    private static readonly ProviderManifest Manifest = BolManifest.Build();

    private readonly BolOptions _options;
    private readonly TimeProvider _time;
    private readonly CaptchaGate _captcha;

    public BolAdapter(BolOptions? options = null, TimeProvider? time = null)
    {
        _options = options ?? new BolOptions();
        _time = time ?? TimeProvider.System;

        // The policy is shared with Albert Heijn and Lidl Plus; only the
        // selectors and the patience are bol's. A widget goes to whoever is
        // sitting at the browser, a picture is relayed, and an unattended
        // agent says so at once instead of waiting out its budget.
        _captcha = new CaptchaGate(new CaptchaSpec
        {
            ProviderId = ProviderId,
            Interactive = _options.InteractiveCaptchaSelectors,
            Image = _options.ImageCaptchaSelectors,
            Input = _options.CaptchaInputSelectors,
            ProbeMs = _options.ProbeMs,
            ImageSeconds = _options.ChallengeSeconds,
            InteractiveSeconds = _options.InteractiveCaptchaSeconds,
            PollSeconds = _options.PollSeconds,
        }, _time);
    }

    public ProviderManifest Describe() => Manifest;

    public async Task<LoginResult> LoginAsync(IJobContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        // Everything that can fail without contacting anyone, checked before
        // a browser is leased: launching Chromium to discover a blank field
        // holds an agent for nothing.
        _ = RequiredInput(ctx, "username");
        _ = RequiredInput(ctx, "password");

        ctx.Progress(JobStep.OpeningProvider);
        var browserPage = await ctx.Browser.PageAsync(ct).ConfigureAwait(false);
        var page = new PlaywrightLoginPage(browserPage, Manifest);

        return await LoginAsync(ctx, page, new BolSessionWatcher(ctx, page, _options, _time), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The whole login, behind the seam.
    ///
    /// Internal rather than private so the offline suite can drive it. Every
    /// branch below decides how a human is treated - whether they are asked
    /// for a code, told to pass a widget, told their password is wrong, or
    /// told nothing at all - and on a provider nobody here has an account
    /// for, a live attempt is a thing that can be spent exactly once and is
    /// never retried.
    /// </summary>
    internal async Task<LoginResult> LoginAsync(
        IJobContext ctx, ILoginPage page, IRedirectWaiter signedIn, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(page);

        var username = RequiredInput(ctx, "username");
        var password = RequiredInput(ctx, "password");

        // Deliberately the protected page, not the login form: bol's own 302
        // chain takes us to the form and brings us back, which is the flow a
        // shopper takes and the cleanest signal that we are in.
        await page.GotoAsync(_options.LoginStartUrl, ct).ConfigureAwait(false);
        await page.ClickAsync(_options.ConsentSelectors, _options.ProbeMs, ct).ConfigureAwait(false);

        ctx.Progress(JobStep.Authenticating);

        if (!await page.FillAsync(_options.UsernameSelectors, username, _options.SelectorTimeoutMs, ct)
                .ConfigureAwait(false))
        {
            // The redirect chain did not end at a form. The form's own address
            // is one of the few things about bol that IS confirmed, so going
            // straight at it costs one navigation and is worth far more than
            // failing on a 302 that changed shape.
            await page.GotoAsync(_options.LoginFormUrl, ct).ConfigureAwait(false);
            await page.ClickAsync(_options.ConsentSelectors, _options.ProbeMs, ct).ConfigureAwait(false);

            if (!await page.FillAsync(_options.UsernameSelectors, username, _options.SelectorTimeoutMs, ct)
                    .ConfigureAwait(false))
            {
                throw Missing("e-mail field, on either the account page's redirect or the login form itself",
                    _options.UsernameSelectors);
            }
        }

        // bol's is a classic single-screen Spring form: both names are on the
        // one page. A miss here is therefore a real shape change and not the
        // two-step wizard Lidl turned out to have - so it is reported rather
        // than worked around, with nothing typed anywhere and no attempt spent.
        if (!await page.FillAsync(_options.PasswordSelectors, password, _options.SelectorTimeoutMs, ct)
                .ConfigureAwait(false))
        {
            throw Missing("password field", _options.PasswordSelectors);
        }

        // Latch before the credentials leave the machine. After this a lost
        // lease fails the job rather than requeuing it: a retried login is how
        // an account gets locked, and bol may already have sent a code.
        ctx.CredentialSubmitted();

        if (!await page.ClickAsync(_options.SubmitSelectors, _options.SelectorTimeoutMs, ct).ConfigureAwait(false))
        {
            throw Missing("login submit", _options.SubmitSelectors);
        }

        await SettleAsync(ctx, page, signedIn, ct).ConfigureAwait(false);

        ctx.Progress(JobStep.Finalizing);

        var storageState = await ctx.Browser.StorageStateAsync(ct).ConfigureAwait(false);

        // Checked here rather than left for the first fetch to discover. The
        // orders request needs cookies scoped to www.bol.com specifically -
        // bol sets JSESSIONID on login.bol.com, and a browser would not carry
        // that across hosts unless the cookie asked it to. A login that
        // "succeeded" and left nothing usable is a login that failed.
        if (BolCookies.For(storageState, _options.OrdersHost).Count == 0)
        {
            throw ConnectorException.ProviderChanged(
                $"{ProviderId}: the login left no cookies scoped to '{_options.OrdersHost}', so no fetch could " +
                "carry a session; bol's session cookie may have moved host or been renamed " +
                $"(looking for any of [{string.Join(", ", _options.SessionCookieNames)}])");
        }

        return new LoginResult
        {
            Material = new SessionMaterial { StorageState = storageState },
            Account = new ProviderAccount { DisplayName = Manifest.Name },
            // Stated from this login rather than left to the manifest, so the
            // consumer's countdown starts when the session did. The number is
            // a conservative guess - see BolManifest.SessionTtlSeconds.
            ExpiresAt = _time.GetUtcNow().AddSeconds(BolManifest.SessionTtlSeconds),
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

        // No credential is available during a fetch, so a dead cookie cannot
        // be renewed here - it needs a new login job, which is exactly what
        // session_expired asks the platform for.
        var cookies = BolCookies.Header(ctx.Material?.StorageState, _options.OrdersHost)
                      ?? throw ConnectorException.SessionExpired(
                          $"{ProviderId}: the stored browser session carries no cookies for " +
                          $"'{_options.OrdersHost}'");

        var shape = BolShapes.For(_options.OrdersShape);
        var zone = RetailZones.Dutch;
        var cap = Manifest.Resource(ReceiptsResource)!.MaxRecordsPerFetch;

        ctx.Progress(JobStep.Downloading);

        var collected = new List<BolOrder>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var complete = true;

        // True while the walk is still ending because the page budget ran out
        // rather than because the history did. The difference is what the
        // caller's `Complete` means, and getting it backwards tells a consumer
        // it has the whole history when it has twenty pages of it.
        var budgetRanOut = true;

        for (var page = 1; page <= _options.MaxPages; page++)
        {
            ct.ThrowIfCancellationRequested();

            var body = await ReadPageAsync(ctx, shape, cookies, page, ct).ConfigureAwait(false);

            ctx.Progress(JobStep.Parsing);

            var orders = shape.Parse(body, _options, zone);
            if (orders.Count == 0)
            {
                budgetRanOut = false;
                break;
            }

            var fresh = 0;
            var older = 0;

            foreach (var order in orders)
            {
                if (!seen.Add(order.Id)) continue;
                fresh++;

                if (request.Since is { } since && DateOnly.FromDateTime(order.PlacedAt.Date) < since) older++;
                if (ReceiptFactory.InWindow(order.PlacedAt, request)) collected.Add(order);
            }

            // A page that repeats the previous one means the pagination
            // parameter is not the one bol takes - which is entirely possible,
            // because its name is a guess. Without this guard that presents as
            // the same page fetched twenty times against a live account.
            if (fresh == 0)
            {
                budgetRanOut = false;
                break;
            }

            // Newest first, so a page entirely below the window means every
            // later page is too. Stopping here is what keeps two years of
            // history from being walked on every sync.
            if (older == orders.Count)
            {
                budgetRanOut = false;
                break;
            }

            // One page past the cap is enough to know the pass is partial;
            // more would be work the caller is about to discard.
            if (collected.Count > cap)
            {
                budgetRanOut = false;
                break;
            }
        }

        var ordered = collected.OrderByDescending(o => o.PlacedAt).ToList();
        if (ordered.Count > cap)
        {
            // More remains than one pass may return. The caller gets a partial
            // pass and comes back for the rest.
            ordered = [.. ordered.Take(cap)];
            complete = false;
        }
        else if (budgetRanOut)
        {
            complete = false;
        }

        ctx.Progress(JobStep.Normalizing);

        var receipts = new List<Receipt>(ordered.Count);
        foreach (var order in ordered)
        {
            receipts.Add(ReceiptFactory.Build(
                ctx.SessionId,
                order.Id,
                Merchant(order.SellerName),
                order.PlacedAt,
                order.Total,
                order.Payment,
                // A caller that did not ask for items gets none, and a receipt
                // with no items reconciles trivially rather than falsely: there
                // is nothing to check a total against.
                request.WantsItems ? order.Items : []));
        }

        return new FetchResult
        {
            Receipts = receipts,
            Complete = complete,
            // Nothing rotates: bol issues no token, so the stored bundle stays
            // exactly as it is until it expires.
            Via = shape.Name,
        };
    }

    internal static Merchant Merchant(string? sellerName) => new()
    {
        Id = ProviderId,
        Name = "bol.com",
        // bol is a marketplace: most orders are fulfilled by a third-party
        // seller, and which one is genuinely useful to the person reading
        // their spending back. Null when the page names nobody - bol ships a
        // large share of its own orders - never a guess.
        StoreName = sellerName,
    };

    // ---- transport -----------------------------------------------------------

    private async Task<string> ReadPageAsync(
        IJobContext ctx, IBolOrdersShape shape, string cookies, int page, CancellationToken ct)
    {
        var url = shape.Url(_options, page);
        var what = $"orders page {page} ({shape.Name} shape)";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Cookie", cookies);
        request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", shape.Accept);
        request.Headers.TryAddWithoutValidation("Accept-Language", _options.AcceptLanguage);

        // The account page is reached from the account page, which is what a
        // browser would say. Best effort: an operator who edits the start URL
        // into something unparseable should lose a header, not the fetch.
        if (Uri.TryCreate(_options.LoginStartUrl, UriKind.Absolute, out var referrer))
        {
            request.Headers.Referrer = referrer;
        }

        using var response = await ProviderHttp.SendAsync(ctx.Http, request, ProviderId, ct).ConfigureAwait(false);

        // 401 is the session being over, which is a new login job. Everything
        // in BlockStatuses is bol refusing us rather than failing - and none
        // of it is ever a wrong password: telling a user to reset a password
        // that was fine leaves the real block undiagnosed. The body is kept on
        // failure because a block page usually names its own vendor.
        await ProviderHttp.EnsureSuccessAsync(response, ProviderId, what, _options.BlockStatuses, ct)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        Guard(body, what);
        return body;
    }

    /// <summary>
    /// The two things a 200 can be other than the page we asked for.
    ///
    /// The wall is checked first on purpose. Mistaking a wall for a dead
    /// session starts a fresh login straight into the wall - spending a
    /// credential against something that was never going to answer - which is
    /// the more expensive of the two wrong readings.
    /// </summary>
    private void Guard(string body, string what)
    {
        foreach (var marker in _options.BotWallMarkers)
        {
            if (!body.Contains(marker, StringComparison.OrdinalIgnoreCase)) continue;

            throw ConnectorException.Blocked(
                $"{ProviderId}: {what} answered 200 with a bot-protection interstitial (matched '{marker}'). " +
                "bol ran no such vendor when this adapter was written, so this is a change worth telling " +
                "an operator about - and it is not a credential problem");
        }

        foreach (var marker in _options.LoginPageMarkers)
        {
            if (!body.Contains(marker, StringComparison.Ordinal)) continue;

            throw ConnectorException.SessionExpired(
                $"{ProviderId}: {what} answered with the login form (matched '{marker}'); the stored session " +
                "is over and bol offers consumers no refresh grant, so a human has to sign in again");
        }
    }

    // ---- login ---------------------------------------------------------------

    /// <summary>
    /// Waits for the login to resolve into one of five outcomes: signed in, a
    /// one-time code to relay, a captcha, a stated credential error, or
    /// nothing recognisable.
    ///
    /// The last case is deliberately <c>provider_changed</c> and never
    /// <c>invalid_credentials</c>. Guessing "wrong password" from silence
    /// sends a user to reset a password that was fine, and a credential error
    /// is never retried - so the mistake is permanent for that session too.
    /// </summary>
    private async Task SettleAsync(
        IJobContext ctx, ILoginPage page, IRedirectWaiter signedIn, CancellationToken ct)
    {
        var deadline = _time.GetUtcNow().AddSeconds(_options.LoginSettleSeconds);
        var poll = TimeSpan.FromSeconds(_options.PollSeconds);
        var captchaSeen = false;
        var codeAnswered = false;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // First, because a device bol already trusts goes straight
            // through and everything below would be a probe against a page
            // that has already moved on.
            if (await signedIn.WaitAsync(poll, ct).ConfigureAwait(false) is not null) return;

            // A generic notice is not a credential verdict. Lidl's login
            // answers an automated browser with exactly such a notice and
            // reading it as a bad password was a real bug, so anything
            // non-specific belongs here and reports what it is.
            if (await page.FindAsync(_options.BlockedNoticeSelectors, _options.ProbeMs, ct).ConfigureAwait(false)
                is not null)
            {
                await page.ClearSecretsAsync(ct).ConfigureAwait(false);

                throw ConnectorException.Blocked(
                    $"{ProviderId}: the login was refused with a notice that names no cause. The page carries a " +
                    "reCAPTCHA site key, so the likeliest reading is a risk-based verdict on an automated " +
                    "browser rather than anything about the account - an agent on a real residential line is " +
                    "the route that passes it");
            }

            // The one path that may report invalid_credentials, and only
            // because the page said so itself.
            if (await page.FindAsync(_options.LoginErrorSelectors, _options.ProbeMs, ct).ConfigureAwait(false)
                is not null)
            {
                await page.ClearSecretsAsync(ct).ConfigureAwait(false);
                throw ConnectorException.InvalidCredentials($"{ProviderId}: the login page stated a credential error");
            }

            if (!codeAnswered &&
                await page.FindAsync(_options.VerificationCodeSelectors, _options.ProbeMs, ct).ConfigureAwait(false)
                    is not null)
            {
                await RelayCodeAsync(ctx, page, ct).ConfigureAwait(false);
                codeAnswered = true;

                // The budget measures how long bol takes to answer, so it
                // restarts once a wait on a human is over: the minutes they
                // spent finding a code are not the provider being slow, and
                // charging them to it fails a login that is about to work.
                deadline = _time.GetUtcNow().AddSeconds(_options.LoginSettleSeconds);
                continue;
            }

            if (!captchaSeen)
            {
                var wall = await _captcha.DetectAsync(page, ct).ConfigureAwait(false);
                if (wall.Kind is not CaptchaKind.None)
                {
                    captchaSeen = true;

                    var outcome = await _captcha.FaceAsync(ctx, page, signedIn, wall, ct).ConfigureAwait(false);
                    if (outcome.Redirect is not null) return;
                    if (!outcome.Handled) break;

                    deadline = _time.GetUtcNow().AddSeconds(_options.LoginSettleSeconds);
                    continue;
                }
            }

            if (_time.GetUtcNow() >= deadline) break;
        }

        await page.ClearSecretsAsync(ct).ConfigureAwait(false);

        throw captchaSeen
            ? ConnectorException.Blocked($"{ProviderId}: a captcha stood between the login and the account page")
            : ConnectorException.ProviderChanged(
                $"{ProviderId}: the login neither completed nor stated anything recognisable within " +
                $"{_options.LoginSettleSeconds}s; last url was '{page.Url}'");
    }

    /// <summary>
    /// Relays the one-time code bol is assumed to send on a new device. Never
    /// solves anything: the code is in the account owner's mailbox or on their
    /// phone, and only they can read it.
    /// </summary>
    private async Task RelayCodeAsync(IJobContext ctx, ILoginPage page, CancellationToken ct)
    {
        var delivery = await DeliveryAsync(page, ct).ConfigureAwait(false);

        ctx.Progress(JobStep.AwaitingHuman);

        var answer = await ctx.AskAsync(new Challenge
        {
            Type = ChallengeType.MfaCode,
            PromptKey = BolMessageKeys.VerificationCode,
            Delivery = delivery,
            // Null unless a live run has actually seen the screen: a wrong
            // length makes the consumer reject a valid code before it is ever
            // tried, and the user waits for another one.
            Length = _options.CodeLength,
            ExpiresAt = _time.GetUtcNow().AddSeconds(_options.CodeChallengeSeconds),
        }, ct).ConfigureAwait(false);

        if (!await page.FillAsync(_options.VerificationCodeSelectors, answer.Value, _options.SelectorTimeoutMs, ct)
                .ConfigureAwait(false))
        {
            throw Missing("verification code field", _options.VerificationCodeSelectors);
        }

        ctx.Progress(JobStep.Authenticating);

        if (!await page.ClickAsync(_options.VerificationSubmitSelectors, _options.SelectorTimeoutMs, ct)
                .ConfigureAwait(false))
        {
            // Some code forms submit on the last keystroke and have no button
            // at all, which is what the Enter carries.
            await page.AnswerAsync(_options.VerificationCodeSelectors, answer.Value, _options.SelectorTimeoutMs, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Which way the code was sent, for the prompt the human reads.
    ///
    /// bol's credential is an e-mail address, so a mailbox is the default -
    /// but the page's own word beats the inference whenever it gives one,
    /// because telling somebody to watch a phone that will never ring is the
    /// failure they cannot recover from: the challenge expires while they wait.
    /// </summary>
    private async Task<string> DeliveryAsync(ILoginPage page, CancellationToken ct)
    {
        if (await page.FindAsync(_options.EmailDeliverySelectors, _options.ProbeMs, ct).ConfigureAwait(false)
            is not null)
        {
            return "email";
        }

        if (await page.FindAsync(_options.SmsDeliverySelectors, _options.ProbeMs, ct).ConfigureAwait(false)
            is not null)
        {
            return "sms";
        }

        // What the user typed is the best evidence left: bol signs people in
        // with an address, so a mailbox is where it can reach them.
        return "email";
    }

    private static string RequiredInput(IJobContext ctx, string key) =>
        ctx.Inputs.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw ConnectorException.InvalidRequest($"{ProviderId}: '{key}' is required");

    private static ConnectorException Missing(string what, IReadOnlyList<string> selectors) =>
        ConnectorException.ProviderChanged(
            $"{ProviderId}: no element for the {what}; tried [{string.Join(", ", selectors)}]");
}

/// <summary>
/// "Are we in yet?", for a provider whose success is a cookie rather than a
/// redirect.
///
/// Albert Heijn and Lidl Plus finish by sending the browser to a scheme
/// Chromium cannot open, so <see cref="RedirectWatcher"/> catches that one
/// event. bol has no such moment: it just stops being the login page and
/// starts being the account page. The signal is therefore the cookie jar,
/// and wearing <see cref="IRedirectWaiter"/> is what lets the shared
/// <see cref="CaptchaGate"/> - which watches "did the provider move on while
/// the human worked?" throughout an interactive challenge - be used here
/// unchanged rather than reimplemented with a subtly different idea of
/// success.
/// </summary>
internal sealed class BolSessionWatcher : IRedirectWaiter
{
    private readonly IJobContext _ctx;
    private readonly ILoginPage _page;
    private readonly BolOptions _options;
    private readonly TimeProvider _time;

    public BolSessionWatcher(IJobContext ctx, ILoginPage page, BolOptions options, TimeProvider time)
    {
        _ctx = ctx;
        _page = page;
        _options = options;
        _time = time;
    }

    public async Task<string?> WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        // Counted rather than clock-bounded. A caller holding a frozen
        // TimeProvider - which every test in this repo does - would otherwise
        // never reach a deadline computed from it, and the loop would spin
        // until the job's own budget ran out.
        var interval = Math.Max(1, _options.SessionPollMs);
        var passes = Math.Max(1, (int)(timeout.TotalMilliseconds / interval));

        for (var pass = 0; pass < passes; pass++)
        {
            ct.ThrowIfCancellationRequested();

            if (await SignedInAsync(ct).ConfigureAwait(false)) return _page.Url;
            if (pass + 1 >= passes) break;

            await Task.Delay(TimeSpan.FromMilliseconds(interval), _time, ct).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<bool> SignedInAsync(CancellationToken ct)
    {
        // Still on the login host means nothing has happened yet, and it costs
        // no round trip to know that.
        if (_page.Url.Contains(_options.LoginHostMarker, StringComparison.OrdinalIgnoreCase)) return false;

        var storageState = await _ctx.Browser.StorageStateAsync(ct).ConfigureAwait(false);
        return BolCookies.HasSession(storageState, _options);
    }
}
