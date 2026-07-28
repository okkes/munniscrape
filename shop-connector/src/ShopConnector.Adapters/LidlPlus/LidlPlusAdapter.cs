using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Connector.Kit.Adapters;
using Connector.Kit.Challenges;
using Connector.Kit.Errors;
using Connector.Kit.Jobs;
using Connector.Kit.Manifests;
using Connector.Kit.Normalization;
using Connector.Kit.Security;
using ShopConnector.Adapters.Support;

namespace ShopConnector.Adapters.LidlPlus;

/// <summary>
/// Lidl Plus - T2. A browser drives the OAuth login exactly once, relaying
/// the one-time code to the human through the challenge protocol, and the
/// resulting refresh token serves every fetch after that over plain HTTP.
/// No password is ever stored and no browser is ever started again.
///
/// Three things here were wrong badly enough to fail every real login, and
/// all three are corrected against <c>yagueto/lidl-plus</c> as read on
/// 2026-07-28: the authorize call sent no PKCE challenge and the exchange no
/// verifier; the Basic secret was an empty string; and the login only knew
/// how to fill a phone box, on a page that has one for an e-mail beside it.
/// </summary>
public sealed class LidlPlusAdapter : IProviderAdapter
{
    public const string ProviderId = "lidl";
    public const string ReceiptsResource = "receipts";

    private const string CountryKey = "country";
    private const string LanguageKey = "language";

    private static readonly ProviderManifest Manifest = LidlPlusManifest.Build();

    private readonly LidlPlusOptions _options;
    private readonly TimeProvider _time;
    private readonly CaptchaGate _captcha;

    public LidlPlusAdapter(LidlPlusOptions? options = null, TimeProvider? time = null)
    {
        _options = options ?? new LidlPlusOptions();
        _time = time ?? TimeProvider.System;

        // The policy is shared with Albert Heijn; only the selectors and the
        // patience are Lidl's.
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

        var settings = LidlSettings.From(ctx.Config, ctx.Material);

        // The default, and on Lidl the only one that works: the HUMAN's own
        // browser performs the sign-in and hands back the authorization code.
        //
        // Lidl scores the browser with reCAPTCHA Enterprise and refuses an
        // automated one - proven live on 2026-07-28, with correct credentials
        // and correct selectors, bouncing back to the identifier screen with a
        // generic notice. That verdict is about the browser, not the account,
        // so no amount of adapter work moves it. A real device passes it
        // because it IS a real device.
        //
        // Three things fall out, and each is worth having on its own:
        // the connector never sees the password; no agent and no browser are
        // needed on our side at all; and it works on a phone, where a headed
        // Chromium on some server is no use to anybody.
        if (_options.ClientSideAuthorization)
        {
            return await ClientAuthorizeAsync(ctx, settings, ct).ConfigureAwait(false);
        }

        // Everything that can fail without contacting anyone, checked before a
        // browser is leased. Launching Chromium to discover that a required
        // field is blank holds an agent for nothing; the overload below checks
        // again because it is the entry point the offline suite drives.
        _ = RequiredInput(ctx, "username");
        _ = RequiredInput(ctx, "password");

        ctx.Progress(JobStep.OpeningProvider);
        var page = await ctx.Browser.PageAsync(ct).ConfigureAwait(false);

        // Before anything is typed, never after: Lidl finishes by sending the
        // browser to a scheme Chromium cannot open, so there is no page load
        // to await and a listener attached later is a race against the one
        // event the whole login turns on.
        using var watcher = new RedirectWatcher(page, _options.RedirectUri, _time);

        return await LoginAsync(ctx, new PlaywrightLoginPage(page, Manifest), watcher, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The whole login, behind the seam.
    ///
    /// Internal rather than private so the offline suite can drive it: the
    /// PKCE pair, the choice between two inputs and the delivery on the code
    /// challenge all decide whether a real person can connect at all, and
    /// none of them was reachable without a live Chromium and real
    /// credentials - which on this platform is a thing that may be spent
    /// exactly once.
    /// </summary>
    internal async Task<LoginResult> LoginAsync(
        IJobContext ctx, ILoginPage page, IRedirectWaiter watcher, CancellationToken ct)
    {
        var settings = LidlSettings.From(ctx.Config, ctx.Material);
        var username = RequiredInput(ctx, "username");
        var password = RequiredInput(ctx, "password");

        // Minted before the authorize call and kept until the exchange. The
        // two halves travel apart - the challenge in a URL the browser opens,
        // the verifier in a form body an hour of page-driving later - and
        // holding them in one object is what stops the second from being
        // forgotten, which is precisely how it was missing before.
        var pkce = PkceChallenge.Create();

        await page.GotoAsync(AuthorizeUrl(pkce, settings), ct).ConfigureAwait(false);

        var redirect = await SignInAsync(ctx, page, watcher, username, password, ct).ConfigureAwait(false);

        var code = AuthorizationCode(redirect);
        var tokens = await ExchangeAsync(ctx, code, pkce.Verifier, ct).ConfigureAwait(false);

        ctx.Progress(JobStep.Finalizing);

        return new LoginResult
        {
            Material = new SessionMaterial
            {
                AccessToken = tokens.AccessToken,
                RefreshToken = tokens.RefreshToken,
                AccessTokenExpiresAt = tokens.ExpiresAt,
                // Also carried here, not only in the bundle's config block:
                // every ticket URL and header needs them, and a fetch that
                // arrives without config would otherwise be unserviceable.
                Extra = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [CountryKey] = settings.Country,
                    [LanguageKey] = settings.Language,
                },
            },
            Account = new ProviderAccount { DisplayName = Manifest.Name },
        };
    }

    /// <summary>
    /// The sign-in, performed by the human in their own browser.
    ///
    /// The PKCE verifier lives in this stack frame for the whole exchange, and
    /// that is the entire state management: <see cref="IJobContext.AskAsync"/>
    /// parks the job until the answer arrives, so there is nothing to persist
    /// between raising the challenge and redeeming the code. The job budget
    /// stops counting while parked, so a human who takes five minutes is not
    /// racing a timeout.
    /// </summary>
    private async Task<LoginResult> ClientAuthorizeAsync(
        IJobContext ctx, LidlSettings settings, CancellationToken ct)
    {
        ctx.Progress(JobStep.AwaitingHuman);

        var pkce = PkceChallenge.Create();

        var answer = await ctx.AskAsync(
            new Challenge
            {
                Type = ChallengeType.Redirect,
                PromptKey = MessageKeys.SignInOnProviderSite,
                Url = AuthorizeUrl(pkce, settings),

                // What the consumer watches for. A native shell intercepts this
                // in its webview and the human sees nothing; a browser cannot
                // follow a custom scheme at all, which is why the demo asks for
                // a paste and a phone does not.
                ReturnPattern = _options.RedirectUri + "*",
                ExpiresAt = _time.GetUtcNow().AddSeconds(_options.ClientAuthorizationSeconds),
            },
            ct).ConfigureAwait(false);

        var code = RedirectCode.Extract(answer.Value)
            ?? throw ConnectorException.InvalidRequest(
                "lidl: the returned address carries no authorization code; expected one on " +
                $"'{_options.RedirectUri}'");

        // The code has already been spent by the time anything can go wrong
        // here, so a lost lease must not re-run this login: an authorization
        // code is single-use and the retry would fail on a code Lidl has
        // already burned.
        ctx.CredentialSubmitted();
        ctx.Progress(JobStep.Authenticating);

        var tokens = await ExchangeAsync(ctx, code, pkce.Verifier, ct).ConfigureAwait(false);

        ctx.Progress(JobStep.Finalizing);

        return new LoginResult
        {
            Material = new SessionMaterial
            {
                AccessToken = tokens.AccessToken,
                RefreshToken = tokens.RefreshToken,
                AccessTokenExpiresAt = tokens.ExpiresAt,
                Extra = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [CountryKey] = settings.Country,
                    [LanguageKey] = settings.Language,
                },
            },
            Account = new ProviderAccount { DisplayName = Manifest.Name },
        };
    }

    public async Task<FetchResult> FetchAsync(IJobContext ctx, ResourceRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(request);

        if (!string.Equals(request.ResourceId, ReceiptsResource, StringComparison.Ordinal))
        {
            throw ConnectorException.Unsupported($"lidl: no resource '{request.ResourceId}'");
        }

        var settings = LidlSettings.From(ctx.Config, ctx.Material);
        var zone = RetailZones.For(settings.Country);
        var session = new BearerSession(ctx.Material, ProviderId, _time);

        ctx.Progress(JobStep.Downloading);

        var cap = Manifest.Resource(ReceiptsResource)!.MaxRecordsPerFetch;
        var summaries = await ListAsync(ctx, session, settings, zone, request, cap, ct).ConfigureAwait(false);

        var complete = summaries.Count <= cap;
        var selected = complete ? summaries : summaries.Take(cap).ToList();

        ctx.Progress(JobStep.Parsing);

        // Memoized per pass: a list that paginates under us repeats rows,
        // and a detail call is the expensive part.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var receipts = new List<Receipt>(selected.Count);

        foreach (var summary in selected)
        {
            ct.ThrowIfCancellationRequested();
            if (!seen.Add(summary.Id)) continue;

            IReadOnlyList<ReceiptItem> items = [];
            var payment = ReceiptFactory.Payment();
            var total = summary.Total;
            var storeName = summary.StoreName;

            if (request.WantsItems)
            {
                using var detail = await DetailAsync(ctx, session, settings, summary.Id, ct).ConfigureAwait(false);

                items = LidlPlusTicketParser.ParseItems(detail.RootElement, _options);
                payment = LidlPlusTicketParser.ParsePayment(detail.RootElement);
                total = MoneyReader.Optional(detail.RootElement, _options.AmountUnit, total.Currency,
                    "ticket.detail.total", "totalAmount", "total") ?? total;
                storeName = JsonAccess.StrOf(detail.RootElement, "storeName", "store", "storeCode") ?? storeName;
            }

            receipts.Add(ReceiptFactory.Build(
                ctx.SessionId,
                summary.Id,
                LidlPlusTicketParser.Merchant(storeName),
                summary.PurchasedAt,
                total,
                payment,
                items));
        }

        ctx.Progress(JobStep.Normalizing);

        return new FetchResult
        {
            Receipts = receipts,
            RefreshedMaterial = session.RefreshedMaterial(ctx.Material),
            Complete = complete,
            Via = "tickets-v2",
        };
    }

    /// <summary>
    /// The four parameters the reference confirms, plus the PKCE pair it also
    /// sends. Lidl's page may well take a country or locale hint too, but
    /// adding one on a hunch is how an authorize request starts getting
    /// rejected for a reason nobody can reconstruct later.
    /// </summary>
    /// <summary>
    /// OBSERVED 2026-07-28: <c>Country</c> and <c>language</c> are REQUIRED.
    ///
    /// Without them accounts.lidl.com does not render a login page at all - it
    /// redirects to <c>/error?id=…</c> and shows "There's been an error". The
    /// same URL with them renders normally, which is how this was established:
    /// two requests differing in exactly these two parameters.
    ///
    /// They were known to be in the reference and deliberately left out as
    /// speculative, on the reasoning that an unexplained authorize parameter
    /// is worse than a missing one. That reasoning was right in general and
    /// wrong here, and the live page settled it.
    ///
    /// Note the shapes: <c>Country</c> is capitalised and bare ("NL"),
    /// <c>language</c> is lower-case and a full locale ("nl-NL"). Neither is a
    /// typo; sending "nl" for the language is another /error redirect.
    /// </summary>
    internal string AuthorizeUrl(PkceChallenge pkce, LidlSettings settings)
    {
        ArgumentNullException.ThrowIfNull(pkce);
        ArgumentNullException.ThrowIfNull(settings);

        return UrlBuilder.WithQuery(_options.AuthorizeUrl,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["client_id"] = _options.ClientId,
                ["response_type"] = "code",
                ["redirect_uri"] = _options.RedirectUri,
                ["scope"] = _options.Scope,
                ["Country"] = settings.Country,
                ["language"] = LidlSettings.Locale(settings),
                // RFC 7636. The server records this hash against the
                // authorization code it is about to issue and will refuse to
                // exchange that code for anything unless the verifier is
                // produced - so omitting it does not weaken the login, it
                // fails it.
                ["code_challenge"] = pkce.Challenge,
                ["code_challenge_method"] = PkceChallenge.Method,
            });
    }

    /// <summary>
    /// Which of Lidl's two inputs a username belongs in.
    ///
    /// The reference takes the choice as a parameter - <c>"p"</c> means phone,
    /// anything else means e-mail - and fills one of two differently named
    /// boxes. The consumer's form has one box, so the value's own shape has
    /// to decide instead. One character does it: the manifest's pattern
    /// admits exactly two shapes and only the e-mail can contain an '@', so
    /// this test cannot disagree with the validation the consumer already
    /// ran. A second regex here would be one definition of "e-mail" too many,
    /// and the one that drifts is the one that fills the wrong box.
    /// </summary>
    internal static bool LooksLikeEmail(string username) =>
        username.Contains('@', StringComparison.Ordinal);

    private static string RequiredInput(IJobContext ctx, string key) =>
        ctx.Inputs.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw ConnectorException.InvalidRequest($"lidl: '{key}' is required");

    private static ConnectorException Missing(string what, IReadOnlyList<string> selectors) =>
        ConnectorException.ProviderChanged(
            $"{ProviderId}: no element for the {what}; tried [{string.Join(", ", selectors)}]");

    // ---- login -------------------------------------------------------------

    /// <summary>
    /// Types the credentials, meets whatever the page answers with, and
    /// returns the callback URL.
    /// </summary>
    private async Task<string> SignInAsync(
        IJobContext ctx, ILoginPage page, IRedirectWaiter watcher,
        string username, string password, CancellationToken ct)
    {
        ctx.Progress(JobStep.Authenticating);

        var email = LooksLikeEmail(username);
        var selectors = email ? _options.EmailSelectors : _options.PhoneSelectors;
        var what = email ? "e-mail field" : "phone field";

        // Never the other box on a miss. Typing an address into the phone
        // input spends a real login attempt against a defended page, gets
        // rejected for a reason the user cannot act on, and - because a
        // failed login is never retried here - ends their connect attempt
        // permanently. Saying which input was missing is worth far more.
        if (!await page.FillAsync(selectors, username, _options.SelectorTimeoutMs, ct).ConfigureAwait(false))
        {
            throw Missing(what, selectors);
        }

        // Lidl asks for the two credentials on two SCREENS: the identifier,
        // then Next, then the password. A live attempt died here -
        // "no element for the password field" - with the e-mail already
        // filled, and the submit button being called `role_next` is the same
        // story from the other side.
        //
        // Probed rather than assumed. A single-page form is still handled by
        // the first attempt, so this survives Lidl moving in either
        // direction, and a provider changing its login layout must not need a
        // release. The probe is deliberately short: on a two-step page it
        // always misses, and that wait is pure latency on every single login.
        if (!await page.FillAsync(_options.PasswordSelectors, password, _options.PasswordProbeMs, ct)
                .ConfigureAwait(false))
        {
            // Advancing the wizard submits the identifier, so the account is
            // touched from here on: latch first, or a lease lost between the
            // click and the next line would requeue a login that already
            // reached Lidl.
            ctx.CredentialSubmitted();

            if (!await page.ClickAsync(_options.SubmitSelectors, _options.SelectorTimeoutMs, ct).ConfigureAwait(false))
            {
                throw Missing("identifier submit", _options.SubmitSelectors);
            }

            if (!await page.FillAsync(_options.PasswordSelectors, password, _options.SelectorTimeoutMs, ct)
                    .ConfigureAwait(false))
            {
                // Both layouts have now been tried, so this is a real shape
                // change rather than the wizard we were expecting.
                throw Missing("password field, on either the first or the second step", _options.PasswordSelectors);
            }
        }

        // Latch before the credentials leave the machine. After this a lost
        // lease fails the job rather than requeuing it: a retried login is
        // how an account gets locked, and Lidl may already have sent a code.
        // Idempotent, so the two-step path above having latched already is
        // fine.
        ctx.CredentialSubmitted();

        // The password screen has its OWN button. Clicking the identifier
        // step's "Volgende" again here would submit the wrong form.
        if (!await page.ClickAsync(_options.PasswordSubmitSelectors, _options.SelectorTimeoutMs, ct)
                .ConfigureAwait(false))
        {
            throw Missing("password submit", _options.PasswordSubmitSelectors);
        }

        var field = await page.FindAsync(_options.VerificationCodeSelectors, _options.SelectorTimeoutMs, ct)
            .ConfigureAwait(false);

        if (field is null)
        {
            // Checked before the captcha probe and before the redirect wait,
            // because it is the one outcome that is already decided. Lidl
            // bounces an automated browser back to the e-mail screen with a
            // generic notice; waiting on a redirect afterwards spends the
            // whole timeout on an answer we already have, and then reports
            // the wrong thing.
            if (await page.FindAsync(_options.BlockedNoticeSelectors, _options.ProbeMs, ct)
                    .ConfigureAwait(false) is not null)
            {
                throw ConnectorException.Blocked(
                    "lidl: the login was refused and returned to the identifier screen with a generic notice. " +
                    "The page carries reCAPTCHA Enterprise, so this is most likely a score-based verdict on an " +
                    "automated browser rather than anything about the account - the same credentials sign in by " +
                    "hand. An agent attached to a real browser profile is the route that can pass it.");
            }

            // No code box otherwise has two very different causes. Lidl skips
            // the step entirely on a device it already trusts, in which case
            // the redirect is already on its way; or something is standing in
            // front of the form. Probing for a wall only here keeps the cost
            // off the happy path, and the redirect wait below catches the
            // rest.
            if (await FaceCaptchaAsync(ctx, page, watcher, ct).ConfigureAwait(false) is { } finished)
            {
                return finished;
            }
        }
        else
        {
            await SubmitCodeAsync(ctx, page, email, ct).ConfigureAwait(false);
        }

        return await AwaitRedirectAsync(page, watcher, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Relays the one-time code. Never solves anything: the code is on the
    /// account owner's phone or in their mailbox, and only they can read it.
    /// </summary>
    private async Task SubmitCodeAsync(IJobContext ctx, ILoginPage page, bool email, CancellationToken ct)
    {
        var delivery = await DeliveryAsync(page, email, ct).ConfigureAwait(false);

        ctx.Progress(JobStep.AwaitingHuman);

        var answer = await ctx.AskAsync(new Challenge
        {
            Type = ChallengeType.MfaCode,
            PromptKey = MessageKeys.VerificationCode,
            Delivery = delivery,
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
            // The reference clicks role_next here, and that is the first
            // candidate - but some code forms submit on the last keystroke
            // and have no button at all, which is what the Enter carries.
            await page.AnswerAsync(_options.VerificationCodeSelectors, answer.Value, _options.SelectorTimeoutMs, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Which way the code was sent, for the prompt the human reads.
    ///
    /// Decided per login rather than declared once. The manifest used to
    /// state <c>sms</c> unconditionally, which tells somebody who signed in
    /// with an e-mail address to go and watch a phone that will never ring
    /// while the code sits in their inbox - and they cannot recover from that
    /// on their own, because the challenge expires while they wait.
    /// </summary>
    private async Task<string> DeliveryAsync(ILoginPage page, bool email, CancellationToken ct)
    {
        // The page's own word beats the inference when it gives one: a user
        // who signed in with a phone number can still have Lidl mail the
        // code. The reference passes the mode in rather than reading it back,
        // so these markers are unconfirmed - which is why they are only
        // allowed to override, never to be required.
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

        // Otherwise, what the user typed is the best evidence there is: Lidl
        // sends the code the way it can reach them.
        return email ? "email" : "sms";
    }

    /// <summary>
    /// Lidl has not been observed fronting its login with a widget, but the
    /// page is defended and Albert Heijn's turned out to be - so the same
    /// policy applies rather than a hopeful silence: a picture is relayed, a
    /// widget goes to whoever is sitting at the browser, and an unattended
    /// agent says so at once instead of waiting out its budget.
    ///
    /// Returns the redirect when the wall came down while we watched.
    /// </summary>
    private async Task<string?> FaceCaptchaAsync(
        IJobContext ctx, ILoginPage page, IRedirectWaiter watcher, CancellationToken ct)
    {
        var wall = await _captcha.DetectAsync(page, ct).ConfigureAwait(false);
        if (wall.Kind is CaptchaKind.None) return null;

        var outcome = await _captcha.FaceAsync(ctx, page, watcher, wall, ct).ConfigureAwait(false);
        if (outcome.Redirect is { } finished) return finished;

        if (!outcome.Handled)
        {
            throw ConnectorException.Blocked($"{ProviderId}: a captcha stood between the login and its redirect");
        }

        return null;
    }

    private async Task<string> AwaitRedirectAsync(ILoginPage page, IRedirectWaiter watcher, CancellationToken ct)
    {
        var deadline = _time.GetUtcNow().AddSeconds(_options.RedirectTimeoutSeconds);
        var poll = TimeSpan.FromSeconds(_options.RedirectPollSeconds);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (await watcher.WaitAsync(poll, ct).ConfigureAwait(false) is { } captured) return captured;
            if (_time.GetUtcNow() >= deadline) break;
        }

        throw ConnectorException.ProviderChanged(
            $"{ProviderId}: the login never redirected to '{_options.RedirectUri}'; last url was '{page.Url}'");
    }

    private static string AuthorizationCode(string redirectUrl)
    {
        var query = new Uri(redirectUrl).Query;
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0) continue;
            if (!pair.AsSpan(0, separator).Equals("code", StringComparison.Ordinal)) continue;

            return Uri.UnescapeDataString(pair[(separator + 1)..]);
        }

        throw ConnectorException.ProviderChanged("lidl: the callback carried no authorization code");
    }

    private Task<TokenSet> ExchangeAsync(IJobContext ctx, string code, string verifier, CancellationToken ct) =>
        PostTokenAsync(ctx, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = _options.RedirectUri,
            // The other half of the proof the authorize call opened. The
            // server hashes this and compares it to the challenge it stored
            // against the code; without it the exchange is refused outright.
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
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenUrl)
        {
            Content = new FormUrlEncodedContent(form),
        };

        // "LidlPlusNativeClient:secret", confirmed from the reference. The
        // secret half used to be empty, which is a different client as far as
        // the token endpoint is concerned.
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.ClientId}:{_options.ClientSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

        using var response = await ProviderHttp.SendAsync(ctx.Http, request, ProviderId, ct).ConfigureAwait(false);

        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.BadRequest)
        {
            throw ConnectorException.SessionExpired($"lidl: {what} rejected ({(int)response.StatusCode})");
        }

        ProviderHttp.EnsureSuccess(response, ProviderId, what);

        using var document = await ProviderHttp.ReadJsonAsync(response, ProviderId, what, ct).ConfigureAwait(false);
        return TokenSet.Parse(document.RootElement, ProviderId, _time);
    }

    private async Task<List<LidlTicketSummary>> ListAsync(
        IJobContext ctx, BearerSession session, LidlSettings settings, TimeZoneInfo zone,
        ResourceRequest request, int cap, CancellationToken ct)
    {
        var collected = new List<LidlTicketSummary>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var page = 1; page <= _options.MaxPages; page++)
        {
            ct.ThrowIfCancellationRequested();

            var url = _options.TicketsBaseUrl.TrimEnd('/') + _options.TicketListPathTemplate
                .Replace("{country}", settings.Country, StringComparison.Ordinal)
                .Replace("{page}", page.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

            using var response = await session.SendAsync(ctx.Http,
                token => Request(HttpMethod.Get, url, token, settings),
                (refresh, token) => RefreshAsync(ctx, refresh, token), "ticket list", ct).ConfigureAwait(false);

            ProviderHttp.EnsureSuccess(response, ProviderId, "ticket list", ProviderHttp.RetailBlockStatuses);

            using var document = await ProviderHttp
                .ReadJsonAsync(response, ProviderId, "ticket list", ct).ConfigureAwait(false);

            var rows = LidlPlusTicketParser.ParseList(document.RootElement, _options, zone);
            if (rows.Count == 0) break;

            // A page that repeats the previous one means pageNumber is not
            // advancing anything upstream; carrying on would just be the
            // same request twenty times against a defended endpoint.
            var fresh = rows.Where(r => seen.Add(r.Id)).ToList();
            if (fresh.Count == 0) break;

            collected.AddRange(fresh.Where(r => ReceiptFactory.InWindow(r.PurchasedAt, request)));

            // The list runs newest first, so a page entirely older than the
            // window means every later page is too. Stopping here is what
            // keeps a two-year history from being walked on every sync.
            if (request.Since is { } since && rows.All(r => DateOnly.FromDateTime(r.PurchasedAt.Date) < since)) break;

            // One page beyond the cap is enough to know the pass is partial;
            // fetching more would be work the caller is about to discard.
            if (collected.Count > cap) break;
        }

        return [.. collected.OrderByDescending(r => r.PurchasedAt)];
    }

    private async Task<JsonDocument> DetailAsync(
        IJobContext ctx, BearerSession session, LidlSettings settings, string ticketId, CancellationToken ct)
    {
        var url = _options.TicketsBaseUrl.TrimEnd('/') + _options.TicketDetailPathTemplate
            .Replace("{country}", settings.Country, StringComparison.Ordinal)
            .Replace("{id}", Uri.EscapeDataString(ticketId), StringComparison.Ordinal);

        using var response = await session.SendAsync(ctx.Http,
            token => Request(HttpMethod.Get, url, token, settings),
            (refresh, token) => RefreshAsync(ctx, refresh, token), "ticket detail", ct).ConfigureAwait(false);

        ProviderHttp.EnsureSuccess(response, ProviderId, "ticket detail", ProviderHttp.RetailBlockStatuses);

        return await ProviderHttp.ReadJsonAsync(response, ProviderId, "ticket detail", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The app headers the ticket API routes on. They are sent because the
    /// API needs them, not to disguise anything - the honest client identity
    /// the platform's posture allows and nothing beyond it.
    /// </summary>
    private HttpRequestMessage Request(HttpMethod method, string url, string accessToken, LidlSettings settings)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("App-Version", _options.AppVersionHeader);
        request.Headers.TryAddWithoutValidation("Operating-System", _options.OperatingSystemHeader);
        request.Headers.TryAddWithoutValidation("App", _options.AppHeader);
        request.Headers.TryAddWithoutValidation("Accept-Language", settings.Language);
        request.Headers.TryAddWithoutValidation("Country", settings.Country);
        return request;
    }
}

/// <summary>
/// Country and language, resolved once per job. They arrive in the bundle's
/// config block; the material copy is the fallback for a caller that
/// resumed without one.
/// </summary>
internal sealed record LidlSettings(string Country, string Language)
{
    /// <summary>
    /// The authorize call wants a full locale ("nl-NL"), not the bare language
    /// ("nl") that every header and ticket URL uses. Sending the bare one is
    /// an /error redirect, so the two spellings are kept apart deliberately
    /// rather than one being derived loosely from the other at each call site.
    /// </summary>
    public static string Locale(LidlSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return $"{settings.Language}-{settings.Country}";
    }

    public static LidlSettings From(IReadOnlyDictionary<string, string> config, SessionMaterial? material)
    {
        var country = Pick(config, material, "country");
        var language = Pick(config, material, "language");

        if (!LidlPlusManifest.Countries.Contains(country, StringComparer.Ordinal))
        {
            throw ConnectorException.InvalidRequest($"lidl: country '{country}' is not one of the supported values");
        }

        if (!LidlPlusManifest.Languages.Contains(language, StringComparer.Ordinal))
        {
            throw ConnectorException.InvalidRequest($"lidl: language '{language}' is not one of the supported values");
        }

        return new LidlSettings(country, language);
    }

    private static string Pick(IReadOnlyDictionary<string, string> config, SessionMaterial? material, string key)
    {
        if (config.TryGetValue(key, out var fromConfig) && !string.IsNullOrWhiteSpace(fromConfig))
        {
            return Normalize(key, fromConfig);
        }

        if (material?.Extra.TryGetValue(key, out var fromMaterial) == true && !string.IsNullOrWhiteSpace(fromMaterial))
        {
            return Normalize(key, fromMaterial);
        }

        throw ConnectorException.InvalidRequest($"lidl: config '{key}' is required");
    }

    /// <summary>
    /// Country uppercase, language lowercase - both appear verbatim in URLs
    /// and headers, and Lidl is case-sensitive about them.
    /// </summary>
    private static string Normalize(string key, string value) => key == "country"
        ? value.Trim().ToUpperInvariant()
        : value.Trim().ToLowerInvariant();
}
