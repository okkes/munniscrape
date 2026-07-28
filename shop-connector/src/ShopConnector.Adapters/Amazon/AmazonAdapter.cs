using System.Globalization;
using Connector.Kit.Adapters;
using Connector.Kit.Challenges;
using Connector.Kit.Errors;
using Connector.Kit.Jobs;
using Connector.Kit.Manifests;
using Connector.Kit.Normalization;
using Connector.Kit.Security;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.Amazon;

/// <summary>
/// amazon.nl - T3, browser or nothing.
///
/// There is no consumer order API. That is settled rather than assumed: three
/// independently maintained projects in three languages - a Python library, a
/// Chrome extension and a Playwright syncer - all scrape HTML, and one of them
/// writes an ANTLR grammar over page text to do it. Nobody writes an ANTLR
/// parser for a JSON endpoint.
///
/// Two decisions here are worth more than the code around them.
///
/// The first is that no language is forced. <c>azad</c> appends
/// <c>&amp;language=en_GB</c> to every non-English storefront to dodge locale
/// parsing, and it would be easy to copy. We do not have the problem it
/// solves - our money unit is declared and our parser reads Dutch - and the
/// parameter is unverified on <c>.nl</c>, so adopting it would put the happy
/// path on an unconfirmed query string whose failure mode is silent. See
/// <see cref="AmazonOptions.LanguageOverride"/>.
///
/// The second is that no challenge is ever solved. The reference library
/// ships first-class integrations for three paid captcha-solving services
/// because Amazon challenges constantly. This adapter ships none: a widget
/// goes to a human sitting at the browser, and an unattended agent says
/// <c>blocked_by_provider</c> immediately rather than pretending.
/// </summary>
public sealed class AmazonAdapter : IProviderAdapter
{
    public const string ProviderId = "amazon-nl";
    public const string ReceiptsResource = "receipts";

    private static readonly ProviderManifest Manifest = AmazonManifest.Build();

    private readonly AmazonOptions _options;
    private readonly TimeProvider _time;
    private readonly CaptchaGate _captcha;

    public AmazonAdapter(AmazonOptions? options = null, TimeProvider? time = null)
    {
        _options = options ?? new AmazonOptions();
        _time = time ?? TimeProvider.System;

        // The policy is shared with Albert Heijn and Lidl Plus; only the
        // selectors and the patience are Amazon's. Sharing it is the point:
        // "never solve, ask whoever is at the browser, otherwise refuse" is
        // one decision, and three copies of it would be three chances to get
        // it wrong differently.
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

    // ---- login -------------------------------------------------------------

    public async Task<LoginResult> LoginAsync(IJobContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        // Checked before a browser is leased: launching Chromium to discover
        // that a required field is blank holds an agent for nothing.
        _ = RequiredInput(ctx, "username");
        _ = RequiredInput(ctx, "password");

        ctx.Progress(JobStep.OpeningProvider);
        var page = await ctx.Browser.PageAsync(ct).ConfigureAwait(false);

        // Deliberately NOT a RedirectWatcher.
        //
        // That helper exists to catch a navigation the browser is not allowed
        // to follow - appie://login-exit, com.lidlplus.app://callback - where
        // there is no page load to await and the event is seen once or never.
        // Amazon has no such callback: it finishes by landing on the order
        // list, which is an ordinary page the browser is sitting on. Watching
        // for that URL would latch on the adapter's OWN opening navigation to
        // exactly that address, and the login would then report success the
        // instant the password was submitted, before Amazon had answered
        // anything - skipping the one-time code and the wall entirely.
        // AlreadySignedInAsync is the honest signal, and this only supplies
        // the poll interval.
        var result = await LoginAsync(
            ctx, new PlaywrightLoginPage(page, Manifest), new NoRedirect(_time), ct).ConfigureAwait(false);

        var storageState = await ctx.Browser.StorageStateAsync(ct).ConfigureAwait(false);

        // CONFIRMED from the reference's constants:
        // COOKIES_SET_WHEN_AUTHENTICATED = ["x-main"]. A page that looks
        // signed in but left no such cookie has signed nobody in, and sealing
        // that bundle would hand the user a connection that fails on its first
        // scheduled fetch instead of failing here where they can see it.
        if (!AmazonCookies.HasAny(storageState, _options.AuthCookieNames))
        {
            throw ConnectorException.ProviderChanged(
                $"{ProviderId}: the login left no [{string.Join(", ", _options.AuthCookieNames)}] cookie behind");
        }

        return result with
        {
            Material = result.Material with { StorageState = storageState },
        };
    }

    /// <summary>
    /// The whole sign-in, behind the seam.
    ///
    /// Internal rather than private so the offline suite can drive it: every
    /// branch below decides how a human is treated - whether a wall is relayed
    /// or refused, whether silence is called a wrong password - and none of
    /// them is reachable without a live Chromium and a real Amazon account,
    /// which on this platform is a thing that may be spent about once.
    /// </summary>
    internal async Task<LoginResult> LoginAsync(
        IJobContext ctx, ILoginPage page, IRedirectWaiter watcher, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(page);

        var username = RequiredInput(ctx, "username");
        var password = RequiredInput(ctx, "password");

        // Deliberately NOT a hand-built /ap/signin URL.
        //
        // The reference hardcodes openid.assoc_handle=usflex - a US value -
        // and its own docstring admits it is not adjusted for other domains.
        // Navigating to the order list instead lets amazon.nl build its own
        // sign-in chain with its own parameters, which is both correct by
        // construction and one fewer unverified constant to carry.
        await page.GotoAsync(OrdersUrl(_time.GetUtcNow().Year, 0), ct).ConfigureAwait(false);

        // A consent wall left standing covers the form; a missing one is the
        // normal case on a fresh profile.
        _ = await page.ClickAsync(_options.ConsentSelectors, _options.ProbeMs, ct).ConfigureAwait(false);

        if (await AlreadySignedInAsync(page, ct).ConfigureAwait(false))
        {
            // The lease restored a session that still works. Nothing goes
            // upstream, so CredentialSubmitted is deliberately not latched:
            // no login attempt has been spent and this job is safe to requeue.
            ctx.Progress(JobStep.Finalizing);
            return Result();
        }

        ctx.Progress(JobStep.Authenticating);

        if (!await page.FillAsync(_options.UsernameSelectors, username, _options.SelectorTimeoutMs, ct)
                .ConfigureAwait(false))
        {
            throw Missing("e-mail field", _options.UsernameSelectors);
        }

        // Amazon asks for the two credentials on two SCREENS. Probed rather
        // than assumed, so a single-page form is still handled by the first
        // attempt and Amazon moving in either direction needs no release. The
        // probe is short on purpose: on the two-step page it always misses,
        // and that wait is pure latency on every single login.
        if (!await page.FillAsync(_options.PasswordSelectors, password, _options.PasswordProbeMs, ct)
                .ConfigureAwait(false))
        {
            // Advancing the wizard submits the e-mail address, so the account
            // is touched from here on. Latch first: a lease lost between the
            // click and the next line would otherwise requeue a login that
            // already reached Amazon.
            ctx.CredentialSubmitted();

            if (!await page.ClickAsync(_options.ContinueSelectors, _options.SelectorTimeoutMs, ct)
                    .ConfigureAwait(false))
            {
                throw Missing("continue button", _options.ContinueSelectors);
            }

            if (!await page.FillAsync(_options.PasswordSelectors, password, _options.SelectorTimeoutMs, ct)
                    .ConfigureAwait(false))
            {
                throw Missing("password field, on either the first or the second screen", _options.PasswordSelectors);
            }
        }

        // Idempotent, so the two-step path above having latched already is
        // fine. After this a lost lease fails the job rather than requeuing
        // it: a retried login is how an account gets locked, and Amazon may
        // already have sent a code.
        ctx.CredentialSubmitted();

        if (!await page.ClickAsync(_options.SubmitSelectors, _options.SelectorTimeoutMs, ct).ConfigureAwait(false))
        {
            throw Missing("sign-in submit", _options.SubmitSelectors);
        }

        await SettleAsync(ctx, page, watcher, ct).ConfigureAwait(false);

        ctx.Progress(JobStep.Finalizing);
        return Result();

        static LoginResult Result() => new()
        {
            // The storage state is attached by the public entry point, which
            // is the only place that can read a cookie jar.
            Material = new SessionMaterial(),
            Account = new ProviderAccount { DisplayName = Manifest.Name },
        };
    }

    /// <summary>
    /// Waits for the sign-in to resolve into one of five outcomes: the order
    /// list, a one-time code, a stated credential error, a wall, or nothing
    /// recognisable.
    ///
    /// The last case is deliberately not <c>invalid_credentials</c>. Guessing
    /// "wrong password" from silence sends somebody to reset a password that
    /// was fine - and because a credential error is never retried, the mistake
    /// is permanent for that session too.
    /// </summary>
    internal async Task SettleAsync(
        IJobContext ctx, ILoginPage page, IRedirectWaiter watcher, CancellationToken ct)
    {
        var deadline = _time.GetUtcNow().AddSeconds(_options.LoginSettleSeconds);
        var poll = TimeSpan.FromSeconds(_options.RedirectPollSeconds);
        var challengeSeen = false;
        var codeAnswered = false;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // The wait is the poll interval. A waiter that answers is a
            // provider whose sign-in ends somewhere the browser cannot follow;
            // Amazon's ends on an ordinary page, so on this provider it is the
            // page itself that says whether we are in.
            if (await watcher.WaitAsync(poll, ct).ConfigureAwait(false) is not null) return;
            if (await AlreadySignedInAsync(page, ct).ConfigureAwait(false)) return;

            // A refusal before a credential verdict, always. Amazon locking or
            // suspending an account is the provider refusing us; reporting it
            // as a bad password would send the user to change a password that
            // will not help and is not the problem.
            if (await page.FindAsync(_options.RefusalSelectors, _options.ProbeMs, ct).ConfigureAwait(false)
                is not null)
            {
                throw ConnectorException.Blocked(
                    $"{ProviderId}: the sign-in page states the account is locked or suspended; " +
                    "this is a refusal by Amazon, not a credential problem");
            }

            // The page states a credential failure itself. This is the ONLY
            // path that may report invalid_credentials.
            if (await page.FindAsync(_options.LoginErrorSelectors, _options.ProbeMs, ct).ConfigureAwait(false)
                is not null)
            {
                throw ConnectorException.InvalidCredentials(
                    $"{ProviderId}: the sign-in page stated a credential error");
            }

            if (!codeAnswered
                && await page.FindAsync(_options.OtpSelectors, _options.ProbeMs, ct).ConfigureAwait(false)
                is not null)
            {
                await SubmitCodeAsync(ctx, page, ct).ConfigureAwait(false);
                codeAnswered = true;

                // The budget measures how long AMAZON takes to answer, so it
                // restarts once a wait on a human is over: the minutes they
                // spent reading a text message are not the provider being
                // slow, and charging them to it fails a login that is about to
                // work.
                deadline = _time.GetUtcNow().AddSeconds(_options.LoginSettleSeconds);
                continue;
            }

            if (!challengeSeen)
            {
                var wall = await _captcha.DetectAsync(page, ct).ConfigureAwait(false);
                if (wall.Kind is not CaptchaKind.None)
                {
                    challengeSeen = true;

                    var outcome = await _captcha.FaceAsync(ctx, page, watcher, wall, ct).ConfigureAwait(false);
                    if (outcome.Redirect is not null) return;
                    if (!outcome.Handled) break;

                    deadline = _time.GetUtcNow().AddSeconds(_options.LoginSettleSeconds);
                    continue;
                }
            }

            if (_time.GetUtcNow() >= deadline) break;
        }

        throw challengeSeen
            ? ConnectorException.Blocked(
                $"{ProviderId}: a challenge stood between the sign-in and the order list and was not passed")
            : ConnectorException.ProviderChanged(
                $"{ProviderId}: the sign-in neither reached the order list nor stated an error; " +
                $"last url was '{page.Url}'");
    }

    /// <summary>
    /// Relays Amazon's one-time code. Never solves it, and never stores the
    /// TOTP secret that would let it: see the manifest for why that field does
    /// not exist.
    /// </summary>
    private async Task SubmitCodeAsync(IJobContext ctx, ILoginPage page, CancellationToken ct)
    {
        var delivery = await DeliveryAsync(page, ct).ConfigureAwait(false);

        ctx.Progress(JobStep.AwaitingHuman);

        var answer = await ctx.AskAsync(new Challenge
        {
            Type = ChallengeType.MfaCode,
            PromptKey = MessageKeys.VerificationCode,
            Delivery = delivery,
            Length = _options.CodeLength,
            ExpiresAt = _time.GetUtcNow().AddSeconds(_options.CodeChallengeSeconds),
        }, ct).ConfigureAwait(false);

        if (!await page.FillAsync(_options.OtpSelectors, answer.Value, _options.SelectorTimeoutMs, ct)
                .ConfigureAwait(false))
        {
            throw Missing("one-time code field", _options.OtpSelectors);
        }

        ctx.Progress(JobStep.Authenticating);

        if (!await page.ClickAsync(_options.OtpSubmitSelectors, _options.SelectorTimeoutMs, ct).ConfigureAwait(false))
        {
            // Some code forms submit on the last keystroke and have no button
            // at all, which is what the Enter carries.
            _ = await page.AnswerAsync(_options.OtpSelectors, answer.Value, _options.SelectorTimeoutMs, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Which way Amazon says it sent the code, or null.
    ///
    /// Null rather than a guess. A prompt that says "the code we texted you"
    /// to somebody whose code is in an authenticator app sends them to stare
    /// at a phone that will never buzz, and they cannot recover from that on
    /// their own because the challenge expires while they wait. A generic
    /// prompt is worse copy and a better outcome.
    /// </summary>
    private async Task<string?> DeliveryAsync(ILoginPage page, CancellationToken ct)
    {
        if (await page.FindAsync(_options.OtpAppSelectors, _options.ProbeMs, ct).ConfigureAwait(false) is not null)
        {
            return "app";
        }

        if (await page.FindAsync(_options.OtpSmsSelectors, _options.ProbeMs, ct).ConfigureAwait(false) is not null)
        {
            return "sms";
        }

        return await page.FindAsync(_options.OtpEmailSelectors, _options.ProbeMs, ct).ConfigureAwait(false) is not null
            ? "email"
            : null;
    }

    private async Task<bool> AlreadySignedInAsync(ILoginPage page, CancellationToken ct)
    {
        if (!Any(page.Url, _options.SignedInUrlMarkers)) return false;

        // The URL alone is not enough: Amazon serves its sign-in chain and its
        // WAF challenge from paths that can still carry the order-list return
        // URL. A page with a password box on it is not the order list.
        return await page.FindAsync(_options.PasswordSelectors, _options.ProbeMs, ct).ConfigureAwait(false) is null
               && await page.FindAsync(_options.UsernameSelectors, _options.ProbeMs, ct).ConfigureAwait(false) is null;
    }

    // ---- fetch -------------------------------------------------------------

    public async Task<FetchResult> FetchAsync(IJobContext ctx, ResourceRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(request);

        if (!string.Equals(request.ResourceId, ReceiptsResource, StringComparison.Ordinal))
        {
            throw ConnectorException.Unsupported($"{ProviderId}: no resource '{request.ResourceId}'");
        }

        var material = ctx.Material
                       ?? throw ConnectorException.SessionExpired($"{ProviderId}: the job carries no session");

        if (string.IsNullOrWhiteSpace(material.StorageState))
        {
            // There is no refresh grant on this provider - the credential is a
            // cookie jar - so a fetch without one cannot recover on its own.
            throw ConnectorException.SessionExpired($"{ProviderId}: the session carries no browser state");
        }

        var page = await ctx.Browser.PageAsync(ct).ConfigureAwait(false);

        var result = await FetchAsync(
            ctx,
            request,
            new PlaywrightAmazonPages(page, _options.SelectorTimeoutMs),
            new PlaywrightLoginPage(page, Manifest),
            new NoRedirect(_time),
            ct).ConfigureAwait(false);

        // Amazon rotates its session cookies constantly. Persisting the jar we
        // finished with is what keeps the next fetch from arriving with a
        // state Amazon has already superseded.
        var storageState = await ctx.Browser.StorageStateAsync(ct).ConfigureAwait(false);

        return result with { RefreshedMaterial = material with { StorageState = storageState } };
    }

    /// <summary>
    /// The order walk, behind the seam. Everything above
    /// <see cref="IAmazonPages"/> is drivable from a recorded page, which is
    /// the only way any of this is testable: there is no API to stub.
    /// </summary>
    internal async Task<FetchResult> FetchAsync(
        IJobContext ctx,
        ResourceRequest request,
        IAmazonPages pages,
        ILoginPage? wall,
        IRedirectWaiter? redirects,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(pages);

        ctx.Progress(JobStep.Downloading);

        var zone = RetailZones.Dutch;
        var cap = Manifest.Resource(ReceiptsResource)!.MaxRecordsPerFetch;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_time.GetUtcNow(), zone).DateTime);
        var until = request.Until ?? today;
        var since = request.Since ?? until.AddYears(-1);

        var collected = new List<AmazonOrderSummary>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sawCard = false;
        var sawEmptyMarker = false;
        var complete = true;

        var stop = false;

        for (var year = until.Year; year >= since.Year && until.Year - year < _options.MaxYears && !stop; year--)
        {
            for (var index = 0; index < _options.MaxPagesPerYear && !stop; index++)
            {
                ct.ThrowIfCancellationRequested();

                var (fetched, dom) = await OpenAsync(
                    ctx, pages, OrdersUrl(year, index * _options.PageSize),
                    $"the {year} order list", wall, redirects, ct).ConfigureAwait(false);

                ctx.Progress(JobStep.Parsing);

                var rows = AmazonOrderParser.ParseList(dom, _options, zone);
                if (rows.Count == 0)
                {
                    if (Any(fetched.Html, _options.EmptyHistoryMarkers)) sawEmptyMarker = true;
                    break;
                }

                sawCard = true;

                // A page that repeats the previous one means startIndex is not
                // advancing anything upstream. Carrying on would be the same
                // request twenty times against a site that challenges when it
                // is bored - which is how a working session gets escalated
                // into a block.
                var fresh = rows.Where(row => seen.Add(row.Id)).ToList();
                if (fresh.Count == 0) break;

                collected.AddRange(fresh.Where(row => ReceiptFactory.InWindow(row.PurchasedAt, request)));

                // The list runs newest first, so a page entirely older than the
                // window means every later page - and every earlier year - is
                // too. Stopping here is what keeps three years of history from
                // being walked on every sync.
                if (rows.All(row => DateOnly.FromDateTime(row.PurchasedAt.Date) < since)) stop = true;

                // One page past the cap is enough to know the pass is partial;
                // more would be work the caller is about to discard.
                if (collected.Count > cap)
                {
                    complete = false;
                    stop = true;
                }

                if (!AmazonOrderParser.HasNextPage(dom, _options)) break;
            }
        }

        // An empty result has two very different causes and they must not look
        // alike: a customer with no orders in the window, and a card selector
        // that has expired. The second one silently reports "you have bought
        // nothing", which is the most believable wrong answer this adapter
        // could give.
        if (!sawCard && !sawEmptyMarker)
        {
            throw ConnectorException.ProviderChanged(
                $"{ProviderId}: the order list showed neither an order nor an empty-history notice; " +
                $"tried cards [{string.Join(", ", _options.OrderCardSelectors)}]");
        }

        var ordered = collected.OrderByDescending(row => row.PurchasedAt).ToList();
        if (ordered.Count > cap)
        {
            ordered = [.. ordered.Take(cap)];
            complete = false;
        }

        var receipts = new List<Receipt>(ordered.Count);

        foreach (var summary in ordered)
        {
            ct.ThrowIfCancellationRequested();

            IReadOnlyList<ReceiptItem> items = [];
            var payment = ReceiptFactory.Payment();

            if (request.WantsItems && summary.InvoiceUrl is { } invoiceUrl)
            {
                var (_, dom) = await OpenAsync(
                    ctx, pages, invoiceUrl, $"the invoice for '{summary.Id}'", wall, redirects, ct)
                    .ConfigureAwait(false);

                var invoice = AmazonOrderParser.ParseInvoice(dom, _options);
                items = invoice.Items;
                payment = invoice.Payment;
            }

            // The list card's total against the invoice's own lines is the one
            // redundancy Amazon gives us, and ReceiptFactory spends it: every
            // receipt leaves here carrying a reconciliation verdict, and one
            // that does not add up is flagged rather than dropped.
            receipts.Add(ReceiptFactory.Build(
                ctx.SessionId,
                summary.Id,
                MerchantOf(),
                summary.PurchasedAt,
                summary.Total,
                payment,
                items));
        }

        ctx.Progress(JobStep.Normalizing);

        return new FetchResult
        {
            Receipts = receipts,
            Complete = complete,
            Via = request.WantsItems ? "orders-html+print-invoice" : "orders-html",
        };
    }

    public async Task LogoutAsync(IJobContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        // Only if a browser is already running. Launching Chromium purely to
        // sign out would hold an agent for a courtesy, and a user
        // disconnecting must always succeed locally whatever Amazon does.
        if (!ctx.Browser.Started) return;

        try
        {
            ctx.Progress(JobStep.LoggingOut);

            var page = await ctx.Browser.PageAsync(ct).ConfigureAwait(false);
            _ = await new PlaywrightAmazonPages(page, _options.SelectorTimeoutMs)
                .OpenAsync(_options.BaseUrl.TrimEnd('/') + _options.SignOutPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ConnectorException || PageOps.IsSelectorMiss(ex))
        {
            // Logged by the platform, never fatal: a user disconnecting must
            // always succeed locally whatever Amazon does about it.
        }
    }

    // ---- plumbing ----------------------------------------------------------

    /// <summary>
    /// Fetches a page and refuses to hand back anything that is not order
    /// history.
    ///
    /// A wall met here is faced exactly once - by whoever is sitting at the
    /// browser, or not at all - and the proof that it came down is that the
    /// same URL reads as data on the second attempt. An unattended agent never
    /// enters that branch: there is nobody to ask, and hanging until the job's
    /// budget runs out is the failure this rule exists to prevent.
    /// </summary>
    private async Task<(AmazonPage Page, HtmlNode Dom)> OpenAsync(
        IJobContext ctx, IAmazonPages pages, string url, string what,
        ILoginPage? wall, IRedirectWaiter? redirects, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var fetched = await pages.OpenAsync(url, ct).ConfigureAwait(false);
            var dom = HtmlParser.Parse(fetched.Html);
            var verdict = AmazonGuard.Inspect(fetched, dom, _options);

            if (verdict.Kind == AmazonPageKind.Ok) return (fetched, dom);

            var facable = verdict.Kind is AmazonPageKind.Interactive or AmazonPageKind.Image;
            if (attempt > 0 || !facable || !ctx.Attended || wall is null || redirects is null)
            {
                throw AmazonGuard.Failure(verdict, fetched, what, _options);
            }

            var kind = verdict.Kind == AmazonPageKind.Image ? CaptchaKind.Image : CaptchaKind.Interactive;
            var crop = await wall.FindAsync(
                kind == CaptchaKind.Image ? _options.ImageCaptchaSelectors : _options.InteractiveCaptchaSelectors,
                _options.ProbeMs, ct).ConfigureAwait(false);

            var outcome = await _captcha
                .FaceAsync(ctx, wall, redirects, new CaptchaWall(kind, crop?.Crop), ct).ConfigureAwait(false);

            if (!outcome.Handled) throw AmazonGuard.Failure(verdict, fetched, what, _options);
        }
    }

    /// <summary>
    /// The order list for one year and one offset.
    ///
    /// <c>language</c> is appended only when an operator has switched it on -
    /// see <see cref="AmazonOptions.LanguageOverride"/> for why the default is
    /// to leave the storefront in Dutch.
    /// </summary>
    internal string OrdersUrl(int year, int startIndex)
    {
        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["timeFilter"] = _options.TimeFilterTemplate.Replace(
                "{year}", year.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal),
            ["startIndex"] = startIndex.ToString(CultureInfo.InvariantCulture),
        };

        if (!string.IsNullOrWhiteSpace(_options.LanguageOverride))
        {
            query["language"] = _options.LanguageOverride;
        }

        return UrlBuilder.WithQuery(_options.BaseUrl.TrimEnd('/') + _options.OrdersPath, query);
    }

    private static Merchant MerchantOf() => new() { Id = ProviderId, Name = Manifest.Name };

    private static string RequiredInput(IJobContext ctx, string key) =>
        ctx.Inputs.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw ConnectorException.InvalidRequest($"{ProviderId}: '{key}' is required");

    private static ConnectorException Missing(string what, IReadOnlyList<string> selectors) =>
        ConnectorException.ProviderChanged(
            $"{ProviderId}: no element for the {what}; tried [{string.Join(", ", selectors)}]");

    private static bool Any(string haystack, IReadOnlyList<string> needles) =>
        needles.Any(needle => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// There is no redirect to watch for during a fetch.
///
/// <see cref="CaptchaGate"/> watches a callback URL while a human passes a
/// widget, because on an OAuth provider that redirect is the proof. Amazon has
/// no such URL: the proof that a wall came down is that the page we were
/// already asking for reads as order history on the next attempt. Waiting the
/// full poll rather than returning instantly is what keeps the gate's loop
/// from spinning while somebody walks back to their desk.
/// </summary>
internal sealed class NoRedirect : IRedirectWaiter
{
    private readonly TimeProvider _time;

    public NoRedirect(TimeProvider time) => _time = time;

    public async Task<string?> WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        await Task.Delay(timeout, _time, ct).ConfigureAwait(false);
        return null;
    }
}

/// <summary>
/// The registration point.
///
/// A factory rather than an edit to <c>ShopAdapters.cs</c>: that file is
/// shared with every other provider being written tonight, and a merge there
/// costs more than an indirection here.
/// </summary>
public static class AmazonAdapters
{
    public static IProviderAdapter Create(AmazonOptions? options = null, TimeProvider? time = null) =>
        new AmazonAdapter(options, time);
}
