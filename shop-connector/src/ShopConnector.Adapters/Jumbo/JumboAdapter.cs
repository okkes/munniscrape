using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Connector.Kit.Adapters;
using Connector.Kit.Challenges;
using Connector.Kit.Errors;
using Connector.Kit.Jobs;
using Connector.Kit.Manifests;
using Connector.Kit.Normalization;
using Connector.Kit.Security;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.Jumbo;

/// <summary>
/// Jumbo - T3. A browser drives an Auth0 login, and the cookie it leaves
/// behind serves plain HTTP GraphQL until it dies about a day later.
///
/// Six defaults in this adapter were built from assumptions and are now
/// corrected against a working client and a live walk of the login chain.
/// The dangerous one was money: order amounts are decimal-string euros and
/// this adapter used to declare them minor units, which made every Jumbo
/// total a hundredth of the truth - and because the item unit was wrong the
/// same way, reconciliation passed. The unit is now declared per field from a
/// source, both of Jumbo's two conventions are written down side by side in
/// <see cref="JumboOptions"/>, and neither is ever inferred from a value.
///
/// The other five: there is no persisted-query handshake to speak; the
/// response paths are <c>data.onlineOrders.orders</c> and
/// <c>data.storeReceipts.receipts</c>; those two page independently and one
/// counter drives neither; line items come from <c>OrderPagesOrder</c>; and
/// in-store receipts have no structured items at all, only a receipt-printer
/// layout to read as text.
/// </summary>
public sealed class JumboAdapter : IProviderAdapter
{
    public const string ProviderId = "jumbo";
    public const string ReceiptsResource = "receipts";

    /// <summary>
    /// Built from the options, so the flow this manifest declares and the login
    /// this adapter performs cannot drift apart. A manifest promising a password
    /// form for a login that never asks for one is a consumer drawing two boxes
    /// nobody can fill.
    /// </summary>
    private readonly ProviderManifest Manifest;

    private readonly JumboOptions _options;
    private readonly IJumboGraphQlClient _graphql;
    private readonly IJumboReceiptLayoutParser _layout;
    private readonly TimeProvider _time;
    private readonly CaptchaGate _captcha;

    public JumboAdapter(
        JumboOptions? options = null, IJumboGraphQlClient? graphql = null, TimeProvider? time = null)
        : this(options, graphql, time, layout: null)
    {
    }

    internal JumboAdapter(
        JumboOptions? options,
        IJumboGraphQlClient? graphql,
        TimeProvider? time,
        IJumboReceiptLayoutParser? layout)
    {
        _options = options ?? new JumboOptions();
        _graphql = graphql ?? new HttpJumboGraphQlClient(_options);
        _layout = layout ?? new JumboPrintLayoutParser();
        _time = time ?? TimeProvider.System;

        Manifest = JumboManifest.Build();

        // The policy is shared with Albert Heijn and Lidl Plus; only the
        // selectors and the patience are Jumbo's. Auth0 Universal Login ships
        // hCaptcha, reCAPTCHA and Turnstile and activates one on risk score,
        // so a widget is the likely wall here rather than a picture.
        _captcha = new CaptchaGate(new CaptchaSpec
        {
            ProviderId = ProviderId,
            Interactive = _options.InteractiveCaptchaSelectors,
            Image = _options.ImageCaptchaSelectors,
            Input = _options.CaptchaInputSelectors,
            ProbeMs = _options.ProbeMs,
            ImageSeconds = _options.ChallengeSeconds,
            InteractiveSeconds = _options.InteractiveCaptchaSeconds,
            PollSeconds = _options.SettlePollSeconds,
        }, _time);
    }

    public ProviderManifest Describe() => Manifest;

    public async Task<LoginResult> LoginAsync(IJobContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        ctx.Progress(JobStep.OpeningProvider);

        var page = await ctx.Browser.PageAsync(ct).ConfigureAwait(false);
        var login = new PlaywrightLoginPage(page, Manifest);
        var watcher = new JumboReturnWatcher(login, _options, _time);

        return await LoginAsync(ctx, login, watcher, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Types what it was given, and hands the page to the human the moment
    /// Jumbo asks something only they can answer.
    ///
    /// Both credentials are OPTIONAL, and that is the design rather than a
    /// convenience. Auth0 raises Cloudflare Turnstile on a risk score - some
    /// days it is there and some days it is not - and a Turnstile token is
    /// minted by its own JavaScript in the browser that rendered it, against
    /// that browser. It can never be relayed as a picture and never solved
    /// here. So the wall decides who finishes: on a quiet day the stored
    /// credential goes in and nobody is disturbed; on a walled day the same
    /// half-filled page is streamed to whoever owns the account and they carry
    /// on from where this stopped.
    ///
    /// A login with no credentials at all is the same path with nothing typed,
    /// which is what a first connect looks like.
    /// </summary>
    internal async Task<LoginResult> LoginAsync(
        IJobContext ctx, ILoginPage page, IRedirectWaiter watcher, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(watcher);

        // Minted once, at connect, and carried for the life of the session. A
        // device that changes identity every run looks like exactly what it is.
        var deviceId = ctx.Material?.DeviceId ?? Guid.NewGuid().ToString();

        var username = OptionalInput(ctx, "username");
        var password = OptionalInput(ctx, "password");

        // CONFIRMED-live: this 302s to /api/auth/login and on to Auth0. The
        // address this adapter used to open, /inloggen, is a 404.
        await page.GotoAsync(_options.LoginUrl, ct).ConfigureAwait(false);
        await DismissConsentAsync(page, ct).ConfigureAwait(false);

        // The username first, on its own. It is not a secret, so it may sit in
        // the box while the page is photographed - and it is the half a human
        // would otherwise have to go and look up.
        ctx.Progress(JobStep.Authenticating);

        var identified = username is not null
                         && await page.FillAsync(
                                _options.UsernameSelectors, username, _options.SelectorTimeoutMs, ct)
                            .ConfigureAwait(false);

        // Checked BEFORE the password goes anywhere near the DOM, and before
        // any click. Two things follow from that: a walled day never spends one
        // of the account's attempts, and the password is never typed into a
        // page this run is about to hand to a shutter.
        //
        // Interactive alone. A picture captcha is relayable - photographed out,
        // typed back - and the gate below already does that well; streaming a
        // whole page to answer one small picture would be the worse experience
        // for a wall we can already carry. What cannot be carried is a widget:
        // Turnstile mints its token in the browser that rendered it.
        var walled = (await _captcha.DetectAsync(page, ct).ConfigureAwait(false)).Kind
            is CaptchaKind.Interactive;

        var filled = identified
                     && !walled
                     && password is not null
                     && await FillPasswordAsync(ctx, page, password, ct).ConfigureAwait(false);

        if (!filled)
        {
            return await LiveLoginAsync(ctx, page, watcher, deviceId, ct).ConfigureAwait(false);
        }

        // Latch before the credentials leave the machine. After this a lost
        // lease fails the job rather than requeuing it: a retried login is how
        // an account gets locked.
        ctx.CredentialSubmitted();

        if (!await page.ClickAsync(_options.SubmitSelectors, _options.SelectorTimeoutMs, ct).ConfigureAwait(false))
        {
            throw Missing("login submit", _options.SubmitSelectors);
        }

        try
        {
            await SettleAsync(ctx, page, watcher, ct).ConfigureAwait(false);
        }
        catch (ConnectorException ex) when (ex.Code is ErrorCode.BlockedByProvider or ErrorCode.ProviderChanged)
        {
            // The form went in and Jumbo did not hand back a session: a wall
            // that appeared after the click, a one-time code, something none of
            // us has seen. Every one of those is answerable by the person who
            // owns the account, on the page in front of them - so the run is
            // handed over rather than failed.
            //
            // invalid_credentials deliberately does NOT come through here. A
            // stated wrong password is the one outcome streaming cannot fix,
            // and it has to reach the consumer so a stored credential is
            // dropped instead of re-submitted tomorrow.
            return await LiveLoginAsync(ctx, page, watcher, deviceId, ct).ConfigureAwait(false);
        }

        ctx.Progress(JobStep.Finalizing);
        return await HarvestAsync(ctx, deviceId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Puts the password in, on whichever screen Auth0 is asking for it.
    ///
    /// False when the box never appeared, which is not a failure: the page may
    /// have changed, and the human on a streamed view can see what this could
    /// not.
    /// </summary>
    private async Task<bool> FillPasswordAsync(
        IJobContext ctx, ILoginPage page, string password, CancellationToken ct)
    {
        // Auth0 can ask for the two credentials on two screens. Probed rather
        // than assumed, with a short budget: on a single-page form the probe
        // always misses, and that wait is pure latency on every login.
        if (await page.FillAsync(_options.PasswordSelectors, password, _options.PasswordProbeMs, ct)
                .ConfigureAwait(false))
        {
            return true;
        }

        // Advancing the wizard submits the identifier, so the account is
        // touched from here on: latch first, or a lease lost between the click
        // and the next line would requeue a login that already reached Jumbo.
        ctx.CredentialSubmitted();

        return await page.ClickAsync(_options.SubmitSelectors, _options.SelectorTimeoutMs, ct).ConfigureAwait(false)
               && await page.FillAsync(_options.PasswordSelectors, password, _options.SelectorTimeoutMs, ct)
                   .ConfigureAwait(false);
    }

    private static string? OptionalInput(IJobContext ctx, string key) =>
        ctx.Inputs.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    /// <summary>
    /// Jumbo's own login page, streamed to whoever owns the account.
    ///
    /// This adapter types nothing. It opens the page, starts watching for the
    /// session, and raises a live view; the human signs in on the page in front
    /// of them, meets whatever Auth0 puts in the way - the Turnstile box, a
    /// one-time code, something none of us has seen yet - and deals with it the
    /// way they would on their own laptop.
    ///
    /// The wall is why. A real connect met <c>captcha-provider="auth0_v2"</c>,
    /// which is Cloudflare Turnstile, and a Turnstile token is minted by its
    /// own JavaScript in the browser that rendered it. Relaying a picture of it
    /// buys nothing, because the answer is not a picture - so the browser goes
    /// to the human instead of the wall coming to us.
    ///
    /// What that also buys is the whole custody argument: no username and no
    /// password is asked for, so none is posted to the connector, written to a
    /// job's inputs, or held anywhere by anything here. The manifest declares
    /// no fields and the validator refuses one that does.
    ///
    /// <see cref="IJobContext.CredentialSubmitted"/> is deliberately NOT
    /// latched. It exists so a lost lease does not requeue a login whose
    /// password already counted against an account, and this path never submits
    /// one - the attempts are the human's own, on Jumbo's own page.
    /// </summary>
    /// <param name="deviceId">
    /// Carried in rather than minted here, so a run that typed first and then
    /// handed over keeps the identity it started with.
    /// </param>
    internal async Task<LoginResult> LiveLoginAsync(
        IJobContext ctx, ILoginPage page, IRedirectWaiter watcher, string deviceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(watcher);

        // Not navigated again: the caller has already opened the page, and on a
        // walled day it is carrying the username this adapter just typed.
        // Reloading would throw that away and make the human start over.
        //
        // Secrets out first, whatever route got here. The redactor refuses to
        // photograph a page while a field the manifest calls secret holds
        // content - correctly - so a password left in the box would relay a
        // live view of nothing at all. The password is deliberately not typed
        // before the wall check for exactly this reason; this is the belt to
        // that brace, for the two-screen path that may have filled one before
        // a wall appeared.
        await page.ClearSecretsAsync(ct).ConfigureAwait(false);

        ctx.Progress(JobStep.AwaitingHuman);

        // Raised and awaited on its own: the answer to a live view is the empty
        // one every passive challenge sends, and what actually ends this is the
        // session appearing. Whichever comes first wins - somebody who signs in
        // without ever touching the consumer's UI again still finishes.
        // Linked so the ask can be ENDED, not merely abandoned.
        using var view = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var asked = ctx.AskAsync(new Challenge
        {
            Type = ChallengeType.LiveView,
            PromptKey = MessageKeys.LiveLogin,
            ExpiresAt = _time.GetUtcNow().AddSeconds(_options.LiveLoginSeconds),
        }, view.Token);

        var poll = TimeSpan.FromSeconds(_options.SettlePollSeconds);
        var deadline = _time.GetUtcNow().AddSeconds(_options.LiveLoginSeconds);

        try
        {
            while (_time.GetUtcNow() < deadline)
            {
                ct.ThrowIfCancellationRequested();

                // Back on jumbo.com and off every login marker: the session
                // exists. The same terminal signal the typed path settles on,
                // which is why streaming needed no new way to know it is done.
                if (await watcher.WaitAsync(poll, ct).ConfigureAwait(false) is not null)
                {
                    ctx.Progress(JobStep.Finalizing);
                    return await HarvestAsync(ctx, deviceId, ct).ConfigureAwait(false);
                }

                if (asked.IsCompleted) break;
            }

            throw ConnectorException.Blocked(
                "jumbo: the live login ended without reaching a session; the sign-in was not completed");
        }
        finally
        {
            // However this ended, the sign-in the view existed for is over.
            // Cancelled rather than left running: this is what stops the
            // shutter, releases the browser and lets the job finish.
            await view.CancelAsync().ConfigureAwait(false);
            _ = asked.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// The cookies are the credential, so a login that left none behind did not
    /// happen however good the URL looked.
    /// </summary>
    private async Task<LoginResult> HarvestAsync(IJobContext ctx, string deviceId, CancellationToken ct)
    {
        var storageState = await ctx.Browser.StorageStateAsync(ct).ConfigureAwait(false);

        if (JumboCookies.Extract(storageState, _options.CookieDomainSuffix).Count == 0)
        {
            throw ConnectorException.ProviderChanged("jumbo: login left no jumbo.com cookies behind");
        }

        return new LoginResult
        {
            Material = new SessionMaterial { StorageState = storageState, DeviceId = deviceId },
            Account = new ProviderAccount { DisplayName = Manifest.Name },
            ExpiresAt = _time.GetUtcNow().AddSeconds(JumboManifest.SessionTtlSeconds),
        };
    }

    public async Task<FetchResult> FetchAsync(IJobContext ctx, ResourceRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(request);

        if (!string.Equals(request.ResourceId, ReceiptsResource, StringComparison.Ordinal))
        {
            throw ConnectorException.Unsupported($"jumbo: no resource '{request.ResourceId}'");
        }

        // Optional, not required. The confirmed working client sends no device
        // header at all, so refusing a fetch because a bundle carries no device
        // id would be sending a user back through a login for a header the API
        // may never read. The cookies are the credential, and the transport
        // says so if they are missing.
        var deviceId = ctx.Material?.DeviceId;

        ctx.Progress(JobStep.Downloading);

        var zone = RetailZones.Dutch;
        var cap = Manifest.Resource(ReceiptsResource)!.MaxRecordsPerFetch;

        var walk = await WalkAsync(ctx, deviceId, request, zone, cap, ct).ConfigureAwait(false);

        ctx.Progress(JobStep.Parsing);

        var receipts = new List<Receipt>(walk.Orders.Count + walk.StoreReceipts.Count);
        var complete = walk.Complete;
        var emitted = new HashSet<string>(StringComparer.Ordinal);

        foreach (var order in walk.Orders)
        {
            ct.ThrowIfCancellationRequested();
            receipts.Add(await OnlineReceiptAsync(ctx, deviceId, order, request, ct).ConfigureAwait(false));
            emitted.Add(order.OrderId);
        }

        foreach (var summary in walk.StoreReceipts)
        {
            ct.ThrowIfCancellationRequested();

            // A till record of an online order carries that order's id in its
            // transaction id. Emitting both would count one shop twice.
            if (_options.DeduplicateOnlineStoreReceipts &&
                string.Equals(summary.Source, _options.OnlineReceiptSource, StringComparison.OrdinalIgnoreCase) &&
                summary.OrderId is { } orderId && emitted.Contains(orderId))
            {
                continue;
            }

            var receipt = await StoreReceiptAsync(ctx, deviceId, summary, ct).ConfigureAwait(false);
            if (receipt is null)
            {
                // Only reachable with SkipUnreadableStoreReceipts on. The pass
                // is partial by definition, and saying so beats quietly
                // reporting fewer shops than the user made.
                complete = false;
                continue;
            }

            receipts.Add(receipt);
        }

        ctx.Progress(JobStep.Normalizing);

        return new FetchResult
        {
            Receipts = [.. receipts.OrderByDescending(r => r.PurchasedAt)],
            Complete = complete,
            // Nothing rotates. The Auth0 tokens are exchanged into the
            // user-session cookie by Jumbo's own backend and the browser never
            // holds a refresh token, so the stored bundle stays as it is until
            // it expires.
            Via = "graphql",
        };
    }

    // ---- login helpers -----------------------------------------------------

    /// <summary>
    /// Waits for the login to resolve into one of four outcomes: a session, a
    /// stated credential error, a challenge to meet, or nothing recognisable.
    ///
    /// The last case is deliberately <c>provider_changed</c> and not
    /// <c>invalid_credentials</c>. Guessing "wrong password" from silence
    /// sends the user to reset a password that was fine, and the platform
    /// never retries a credential error - so the mistake is also permanent for
    /// that session.
    /// </summary>
    internal async Task SettleAsync(
        IJobContext ctx, ILoginPage page, IRedirectWaiter watcher, CancellationToken ct)
    {
        var deadline = _time.GetUtcNow().AddSeconds(_options.LoginSettleSeconds);
        var poll = TimeSpan.FromSeconds(_options.SettlePollSeconds);
        var captchaSeen = false;
        var mfaAnswered = false;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // Back on jumbo.com and off every login marker: the session exists.
            if (await watcher.WaitAsync(poll, ct).ConfigureAwait(false) is not null) return;

            // The page states a credential failure itself. This is the only
            // path that may report invalid_credentials, and nothing retries it.
            if (await page.FindAsync(_options.LoginErrorSelectors, _options.ProbeMs, ct).ConfigureAwait(false)
                is not null)
            {
                throw ConnectorException.InvalidCredentials("jumbo: the login page stated a credential error");
            }

            if (!mfaAnswered &&
                await page.FindAsync(_options.MfaCodeSelectors, _options.ProbeMs, ct).ConfigureAwait(false)
                is not null)
            {
                mfaAnswered = true;
                await AnswerCodeAsync(ctx, page, ct).ConfigureAwait(false);

                // The settle budget measures how long Jumbo takes to answer,
                // so it restarts once a wait on a human is over: the minutes
                // they spent are not the provider being slow.
                deadline = _time.GetUtcNow().AddSeconds(_options.LoginSettleSeconds);
                continue;
            }

            if (!captchaSeen)
            {
                var wall = await _captcha.DetectAsync(page, ct).ConfigureAwait(false);
                if (wall.Kind is not CaptchaKind.None)
                {
                    captchaSeen = true;

                    // The gate relays a picture to whoever owns the account and
                    // refuses an interactive widget outright when nobody is
                    // sitting at this browser. It never solves one.
                    var outcome = await _captcha.FaceAsync(ctx, page, watcher, wall, ct).ConfigureAwait(false);
                    if (outcome.Redirect is not null) return;
                    if (!outcome.Handled) break;

                    deadline = _time.GetUtcNow().AddSeconds(_options.LoginSettleSeconds);
                    continue;
                }
            }

            if (_time.GetUtcNow() >= deadline) break;
        }

        throw captchaSeen
            ? ConnectorException.Blocked("jumbo: a captcha stood between the login and the session")
            : ConnectorException.ProviderChanged(
                $"jumbo: the login neither produced a session nor stated an error within " +
                $"{_options.LoginSettleSeconds}s; last url was '{page.Url}'");
    }

    /// <summary>
    /// Relays a one-time code. Never solves anything: the code is on the
    /// account owner's phone or in their mailbox, and only they can read it.
    /// </summary>
    private async Task AnswerCodeAsync(IJobContext ctx, ILoginPage page, CancellationToken ct)
    {
        ctx.Progress(JobStep.AwaitingHuman);

        // Delivery is deliberately unset. Auth0 sends the code the way the
        // account is configured, and telling somebody to watch a phone that
        // will never ring is a failure they cannot recover from on their own.
        var answer = await ctx.AskAsync(new Challenge
        {
            Type = ChallengeType.MfaCode,
            PromptKey = MessageKeys.VerificationCode,
            ExpiresAt = _time.GetUtcNow().AddSeconds(_options.ChallengeSeconds),
        }, ct).ConfigureAwait(false);

        if (!await page.AnswerAsync(_options.MfaCodeSelectors, answer.Value, _options.SelectorTimeoutMs, ct)
                .ConfigureAwait(false))
        {
            throw Missing("verification code field", _options.MfaCodeSelectors);
        }

        ctx.Progress(JobStep.Authenticating);
    }

    private async Task DismissConsentAsync(ILoginPage page, CancellationToken ct)
    {
        // Best effort: a consent wall left standing covers the form, and a
        // missing one is the normal case on a fresh profile.
        await page.ClickAsync(_options.ConsentSelectors, 2_000, ct).ConfigureAwait(false);
    }

    // ---- the two walks -----------------------------------------------------

    /// <summary>
    /// What one pass collected, before any detail call is spent on it.
    /// </summary>
    private sealed record Walk
    {
        public required IReadOnlyList<JumboOrderSummary> Orders { get; init; }

        public required IReadOnlyList<JumboStoreReceiptSummary> StoreReceipts { get; init; }

        public required bool Complete { get; init; }
    }

    /// <summary>
    /// One operation, two paginations, two cursors.
    ///
    /// Online orders page by <c>offset</c>/<c>limit</c> nested inside
    /// <c>ordersInput</c>; in-store receipts page by a top-level, zero-based
    /// <c>page</c>. They exhaust at different rates and a single counter
    /// drives neither, so each side advances its own and stops on its own -
    /// which is exactly what the repeated-page guard used to be papering over.
    /// </summary>
    private async Task<Walk> WalkAsync(
        IJobContext ctx, string? deviceId, ResourceRequest request, TimeZoneInfo zone, int cap, CancellationToken ct)
    {
        var orders = new List<JumboOrderSummary>();
        var receipts = new List<JumboStoreReceiptSummary>();
        var seenOrders = new HashSet<string>(StringComparer.Ordinal);
        var seenReceipts = new HashSet<string>(StringComparer.Ordinal);

        var ordersOffset = 0;
        var receiptsPage = 0;
        var ordersDone = false;
        var receiptsDone = false;
        var complete = true;

        for (var pass = 0; pass < _options.MaxPages && !(ordersDone && receiptsDone); pass++)
        {
            ct.ThrowIfCancellationRequested();

            using var document = await ExecuteAsync(ctx, deviceId, new JumboGraphQlRequest
            {
                OperationName = _options.ListOperationName,
                Query = _options.ListDocument,
                Variables = ListVariables(ordersOffset, receiptsPage),
            }, ct).ConfigureAwait(false);

            var root = document.RootElement;

            if (!ordersDone)
            {
                var rows = JumboOrders.Rows(root, _options);
                var fresh = 0;
                var older = 0;

                foreach (var row in rows)
                {
                    var summary = JumboOrders.ParseSummary(row, _options, zone);
                    if (summary is null) continue;              // unkeyable rows cannot be deduped
                    if (!seenOrders.Add(summary.OrderId)) continue;

                    fresh++;
                    if (request.Since is { } since && DateOnly.FromDateTime(summary.PurchasedAt.Date) < since) older++;
                    if (ReceiptFactory.InWindow(summary.PurchasedAt, request)) orders.Add(summary);
                }

                ordersOffset += _options.OrdersPageSize;

                var stated = JumboOrders.TotalCount(root, _options);

                ordersDone =
                    rows.Count == 0
                    // The offset is not advancing anything upstream; carrying
                    // on would be the same request twenty times against a
                    // defended endpoint.
                    || fresh == 0
                    // Newest first, so a page entirely below the window means
                    // every later page is too.
                    || older == rows.Count
                    || rows.Count < _options.OrdersPageSize
                    || (stated is { } total && seenOrders.Count >= total)
                    || orders.Count > cap;
            }

            if (!receiptsDone)
            {
                var rows = JumboStoreReceipts.Rows(root, _options);
                var fresh = 0;
                var older = 0;

                foreach (var row in rows)
                {
                    var summary = JumboStoreReceipts.ParseSummary(row, _options, zone);
                    if (summary is null) continue;
                    if (!seenReceipts.Add(summary.TransactionId)) continue;

                    fresh++;
                    if (request.Since is { } since && DateOnly.FromDateTime(summary.PurchasedAt.Date) < since) older++;
                    if (ReceiptFactory.InWindow(summary.PurchasedAt, request)) receipts.Add(summary);
                }

                receiptsPage++;

                // receiptOverview states totalResults, so this walk ends on a
                // number rather than on a heuristic.
                var stated = JumboStoreReceipts.TotalResults(root, _options);

                receiptsDone =
                    rows.Count == 0
                    || fresh == 0
                    || older == rows.Count
                    || rows.Count < _options.ReceiptsPageSize
                    || (stated is { } total && seenReceipts.Count >= total)
                    || receipts.Count > cap;
            }
        }

        // Ran out of passes with one side still going: there is more upstream
        // than this pass returned, and the caller must be told.
        if (!ordersDone || !receiptsDone) complete = false;

        var selectedOrders = orders.OrderByDescending(o => o.PurchasedAt).ToList();
        if (selectedOrders.Count > cap)
        {
            selectedOrders = [.. selectedOrders.Take(cap)];
            complete = false;
        }

        // An in-store receipt states no total anywhere except in its own
        // detail document, so each one costs a second round trip. Budgeted
        // newest-first: the oldest fall off the end and the pass reports
        // itself partial rather than hammering a defended endpoint.
        var room = Math.Max(0, Math.Min(cap - selectedOrders.Count, _options.StoreReceiptDetailBudget));
        var selectedReceipts = receipts.OrderByDescending(r => r.PurchasedAt).ToList();
        if (selectedReceipts.Count > room)
        {
            selectedReceipts = [.. selectedReceipts.Take(room)];
            complete = false;
        }

        return new Walk
        {
            Orders = selectedOrders,
            StoreReceipts = selectedReceipts,
            Complete = complete,
        };
    }

    // ---- building one receipt ---------------------------------------------

    private async Task<Receipt> OnlineReceiptAsync(
        IJobContext ctx, string? deviceId, JumboOrderSummary order, ResourceRequest request, CancellationToken ct)
    {
        var detail = new JumboOrderDetail();

        if (request.WantsItems)
        {
            // The detail is the expensive call and the one most likely to trip
            // protection, so it only happens when items were asked for. The
            // order's own total still comes from the list, which is what makes
            // the two a real reconciliation pair rather than one number
            // repeated back to itself.
            using var document = await ExecuteAsync(ctx, deviceId, new JumboGraphQlRequest
            {
                OperationName = _options.OrderDetailOperationName,
                Query = _options.OrderDetailDocument,
                Variables = OrderDetailVariables(order.OrderId),
            }, ct).ConfigureAwait(false);

            detail = JumboOrders.ParseDetail(
                document.RootElement, _options, order.Total.Currency, order.OrderId);
        }

        return ReceiptFactory.Build(
            ctx.SessionId,
            OrderExternalId(order.OrderId),
            JumboReceipts.Merchant(order.StoreName),
            order.PurchasedAt,
            order.Total,
            detail.Payment,
            detail.Items);
    }

    /// <summary>
    /// One in-store receipt, or null when it could not be read and the
    /// operator has said to keep going anyway.
    /// </summary>
    private async Task<Receipt?> StoreReceiptAsync(
        IJobContext ctx, string? deviceId, JumboStoreReceiptSummary summary, CancellationToken ct)
    {
        // Always fetched, whether or not items were asked for: the receipt
        // list states no total at all, so without this call there is nothing
        // to say about the purchase except that it happened.
        using var document = await ExecuteAsync(ctx, deviceId, new JumboGraphQlRequest
        {
            OperationName = _options.DigitalReceiptOperationName,
            Query = _options.DigitalReceiptDocument,
            Variables = new JsonObject { ["transactionId"] = summary.TransactionId },
        }, ct).ConfigureAwait(false);

        var storeName = JumboStoreReceipts.StoreName(document.RootElement, _options) ?? summary.StoreName;
        var layout = JumboStoreReceipts.Layout(document.RootElement, _options, summary.TransactionId);

        if (layout is null)
        {
            return Unreadable(summary.TransactionId,
                $"its receiptImage is not a '{_options.DigitalReceiptJsonType}' layout, and nothing else in " +
                "Jumbo's API states an in-store total");
        }

        var contents = _layout.Parse(layout, _options, _options.Currency);

        if (contents.Total is not { } total)
        {
            return Unreadable(summary.TransactionId,
                $"its printed layout yielded no total ({contents.Shortfall ?? "no reason given"})");
        }

        var receipt = ReceiptFactory.Build(
            ctx.SessionId,
            ReceiptExternalId(summary.TransactionId),
            JumboReceipts.Merchant(storeName),
            summary.PurchasedAt,
            total,
            ReceiptFactory.Payment(contents.PaymentMethod),
            contents.Items);

        // A layout we could only half-read must not claim to reconcile. An
        // empty item list reconciles trivially - there is nothing to check
        // against - and letting that stand would be the parser vouching for
        // work it did not do. The content hash covers the facts and not the
        // verdict, so overriding it here leaves the hash correct.
        return contents.Shortfall is null ? receipt : receipt with { Reconciled = false };
    }

    private Receipt? Unreadable(string transactionId, string why)
    {
        if (_options.SkipUnreadableStoreReceipts) return null;

        throw ConnectorException.ProviderChanged(
            $"jumbo: in-store receipt '{transactionId}' cannot be stated because {why}");
    }

    // ---- transport ---------------------------------------------------------

    private async Task<JsonDocument> ExecuteAsync(
        IJobContext ctx, string? deviceId, JumboGraphQlRequest request, CancellationToken ct)
    {
        var document = await _graphql.ExecuteAsync(ctx, request, deviceId, ct).ConfigureAwait(false);

        try
        {
            // GraphQL reports a failure in the body with a 200 status, so the
            // errors array is read before the data is trusted at all.
            JumboGraphQlErrors.Throw(document.RootElement);
        }
        catch
        {
            document.Dispose();
            throw;
        }

        return document;
    }

    /// <summary>
    /// CONFIRMED, exactly as the working client sends them. Note that
    /// <c>offset</c> and <c>limit</c> live <em>inside</em> <c>ordersInput</c>
    /// while <c>page</c> and <c>pageSize</c> are top level: they are two
    /// paginations over two different systems, not two spellings of one.
    /// </summary>
    internal JsonObject ListVariables(int offset, int page) => new()
    {
        ["ordersInput"] = new JsonObject
        {
            ["offset"] = offset,
            ["limit"] = _options.OrdersPageSize,
            ["direction"] = _options.OrdersDirection,
            ["sortBy"] = _options.OrdersSortBy,
            ["statusCategory"] = _options.OrdersStatusCategory,
        },
        ["page"] = page,
        ["pageSize"] = _options.ReceiptsPageSize,
    };

    /// <summary>
    /// <c>$orderId</c> is declared <c>Float!</c>, so it goes on the wire as a
    /// number. Sending the string Jumbo handed us back is a schema validation
    /// error, not a slower path.
    /// </summary>
    internal static JsonObject OrderDetailVariables(string orderId)
    {
        if (!double.TryParse(orderId, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
        {
            throw ConnectorException.ProviderChanged(
                $"jumbo: order id '{orderId}' is not a number, but OrderPagesOrder declares $orderId as Float!");
        }

        return new JsonObject { ["orderId"] = numeric };
    }

    /// <summary>
    /// Prefixed, because the two halves of Jumbo mint their own ids and an
    /// order id and a transaction id could otherwise collide in the dedupe
    /// set that keeps a re-fetch idempotent.
    /// </summary>
    internal static string OrderExternalId(string orderId) => $"order-{orderId}";

    internal static string ReceiptExternalId(string transactionId) => $"receipt-{transactionId}";

    private static string RequiredInput(IJobContext ctx, string key) =>
        ctx.Inputs.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw ConnectorException.InvalidRequest($"jumbo: '{key}' is required");

    private static ConnectorException Missing(string what, IReadOnlyList<string> selectors) =>
        ConnectorException.ProviderChanged(
            $"{ProviderId}: no element for the {what}; tried [{string.Join(", ", selectors)}]");
}

/// <summary>
/// The signal that says a Jumbo login worked.
///
/// There is no custom-scheme callback to catch here: the Auth0 code is
/// exchanged by Jumbo's own backend at <c>/api/auth/callback</c> and all the
/// browser ever sees is a cookie and a redirect back onto the shop. So
/// "signed in" is "the page is on jumbo.com and on none of the login
/// markers", which is also what the settle loop and the captcha gate both
/// need to watch while a human works.
/// </summary>
internal sealed class JumboReturnWatcher : IRedirectWaiter
{
    private const int PollMilliseconds = 250;

    private readonly ILoginPage _page;
    private readonly JumboOptions _options;
    private readonly TimeProvider _time;

    public JumboReturnWatcher(ILoginPage page, JumboOptions options, TimeProvider time)
    {
        _page = page;
        _options = options;
        _time = time;
    }

    /// <summary>
    /// Back on the shop's own domain and off every login marker. Both halves
    /// are needed: an <c>about:blank</c> or an error page is on neither, and
    /// <c>auth.jumbo.com</c> ends in the shop's domain but is the login.
    /// </summary>
    internal static bool IsSignedIn(string? url, JumboOptions options)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!url.Contains(options.CookieDomainSuffix, StringComparison.OrdinalIgnoreCase)) return false;

        return !options.LoginUrlMarkers.Any(m => url.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<string?> WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = _time.GetUtcNow() + timeout;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var url = _page.Url;
            if (IsSignedIn(url, _options)) return url;
            if (_time.GetUtcNow() >= deadline) return null;

            await Task.Delay(TimeSpan.FromMilliseconds(PollMilliseconds), _time, ct).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// The registration line for the shared adapter list.
///
/// A factory rather than an edit: the registry file is the one thing every
/// provider touches, and a factory here means adding Jumbo to it is a single
/// line somebody else can apply without merging this work.
/// </summary>
public static class JumboRegistration
{
    /// <summary>
    /// <c>JumboRegistration.Create(settings.Jumbo, time)</c> in
    /// <c>ShopAdapters.Real</c>.
    /// </summary>
    public static IProviderAdapter Create(JumboOptions? options = null, TimeProvider? time = null) =>
        new JumboAdapter(options, graphql: null, time);
}
