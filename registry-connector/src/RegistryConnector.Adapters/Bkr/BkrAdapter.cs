using Connector.Kit.Adapters;
using Connector.Kit.Challenges;
using Connector.Kit.Errors;
using Connector.Kit.Jobs;
using Connector.Kit.Manifests;
using Connector.Kit.Security;
using RegistryConnector.Adapters.Support;

namespace RegistryConnector.Adapters.Bkr;

/// <summary>
/// BKR - the Dutch credit register - through its consumer portal.
///
/// Sign-in is Azure AD B2C (tenant <c>bkrconsp.onmicrosoft.com</c>, policy
/// <c>B2C_1A_SignUp_SignIn_SmsOrTotp</c>) and asks for a second factor EVERY
/// time. There is no refresh token: B2C hands the portal an id_token by
/// form_post and the portal keeps a cookie, so every sync is a fresh sign-in.
///
/// That shape decides the whole design. How much of it a person has to watch
/// depends only on what they chose to store:
///
///   nothing               the page is streamed from its first screen
///   username + password   both are typed, then the page is streamed for the
///                         second factor - which covers a texted code, an
///                         authenticator code, and any screen offering a
///                         choice between them
///   ...plus the seed      the code is computed here and nobody is disturbed
///
/// The last of those is a deliberate weakening of the user's second factor and
/// is never the default. It is worth being plain about what it buys: BKR is a
/// standing position rather than a feed, so this is a monthly sync, and what
/// is removed is one prompt a month. See connect.bkr.totp for what the user is
/// told before they choose it.
/// </summary>
public sealed class BkrAdapter : IProviderAdapter
{
    public const string ProviderId = "bkr";
    public const string CreditsResource = "credits";

    private static readonly ProviderManifest Manifest = BkrManifest.Build();

    private readonly BkrOptions _options;
    private readonly TimeProvider _time;

    public BkrAdapter(BkrOptions? options = null, TimeProvider? time = null)
    {
        _options = options ?? new BkrOptions();
        _time = time ?? TimeProvider.System;
    }

    public ProviderManifest Describe() => Manifest;

    public async Task<LoginResult> LoginAsync(IJobContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        ctx.Progress(JobStep.OpeningProvider);

        var page = await ctx.Browser.PageAsync(ct).ConfigureAwait(false);
        var login = new PlaywrightLoginPage(page, Manifest);
        var watcher = new BkrSignedInWatcher(login, _options, _time);

        return await LoginAsync(ctx, login, watcher, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The login, behind the page seam so the offline suite can drive all
    /// three tiers without a browser or an account.
    /// </summary>
    internal async Task<LoginResult> LoginAsync(
        IJobContext ctx, ILoginPage page, IRedirectWaiter watcher, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(watcher);

        var username = Optional(ctx, "username");
        var password = Optional(ctx, "password");
        var seed = ReadSeed(ctx);

        await page.GotoAsync(_options.PortalUrl, ct).ConfigureAwait(false);

        ctx.Progress(JobStep.Authenticating);

        // Tier one: nothing to type. A first connect, or somebody who would
        // rather not hand over a password at all.
        if (username is null || password is null)
        {
            return await LiveSignInAsync(ctx, page, watcher, ct).ConfigureAwait(false);
        }

        try
        {
            return await TypedSignInAsync(ctx, page, watcher, username, password, seed, ct).ConfigureAwait(false);
        }
        catch (ConnectorException ex)
            when (ex.Code is ErrorCode.ProviderChanged or ErrorCode.BlockedByProvider or ErrorCode.MfaFailed)
        {
            // The page moved, the register refused us, or a computed code was
            // rejected - a drifted clock, a mistyped seed, a second factor
            // that turned out to be SMS after all. None of those is worth
            // ending a connect attempt over while a human is right there.
            return await LiveSignInAsync(ctx, page, watcher, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Types what it was given, then either computes the second factor or asks
    /// for it.
    /// </summary>
    private async Task<LoginResult> TypedSignInAsync(
        IJobContext ctx, ILoginPage page, IRedirectWaiter watcher,
        string username, string password, TotpSecret? seed, CancellationToken ct)
    {
        if (!await page.FillAsync(_options.UsernameSelectors, username, _options.SelectorTimeoutMs, ct)
                .ConfigureAwait(false))
        {
            throw Missing("the e-mail box", _options.UsernameSelectors);
        }

        // BKR asks for the two credentials on two SCREENS: the e-mail, then
        // "Start inzage", then the password on a page that shows the e-mail
        // back read-only. CONFIRMED live - the password box does not exist
        // until the first button is clicked, so filling both up front finds
        // nothing and reports the page as changed.
        //
        // Probed rather than assumed. A single-page form would be handled by
        // the first attempt, so this survives BKR moving in either direction
        // and a layout change does not need a release. The probe is short
        // because on the real page it always misses, and that wait is pure
        // latency on every sign-in.
        if (!await page.FillAsync(_options.PasswordSelectors, password, _options.ProbeMs, ct).ConfigureAwait(false))
        {
            // Advancing the wizard submits the e-mail, so the account is
            // touched from here. Latched first: a lease lost between the click
            // and the next line would requeue a sign-in that already reached
            // BKR, and a retried sign-in is how an account gets locked.
            ctx.CredentialSubmitted();

            if (!await page.ClickAsync(_options.SubmitSelectors, _options.SelectorTimeoutMs, ct)
                    .ConfigureAwait(false))
            {
                throw Missing("the 'Start inzage' button", _options.SubmitSelectors);
            }

            if (!await page.FillAsync(_options.PasswordSelectors, password, _options.SelectorTimeoutMs, ct)
                    .ConfigureAwait(false))
            {
                // Both layouts have now been tried, so this is a real shape
                // change rather than the wizard we were expecting.
                throw Missing(
                    "the password box, on either the first or the second screen", _options.PasswordSelectors);
            }
        }

        // Idempotent, so the two-screen path above having latched already is
        // fine.
        ctx.CredentialSubmitted();

        if (!await page.ClickAsync(_options.SubmitSelectors, _options.SelectorTimeoutMs, ct).ConfigureAwait(false))
        {
            throw Missing("the sign-in button", _options.SubmitSelectors);
        }

        // Tier two: no seed, so the six digits have to come from a person.
        // Asked for rather than streamed - a code box is a question the
        // challenge protocol already carries well, and streaming a whole
        // browser to type six digits is the worse experience.
        var code = seed is null
            ? await AskForCodeAsync(ctx, ct).ConfigureAwait(false)
            : seed.Now(_time);

        if (!await page.FillAsync(_options.CodeSelectors, code, _options.SelectorTimeoutMs, ct).ConfigureAwait(false))
        {
            throw Missing("the code box", _options.CodeSelectors);
        }

        if (!await page.ClickAsync(_options.CodeSubmitSelectors, _options.SelectorTimeoutMs, ct).ConfigureAwait(false))
        {
            throw Missing("the code's submit button", _options.CodeSubmitSelectors);
        }

        ctx.Progress(JobStep.Finalizing);

        var landed = await watcher.WaitAsync(TimeSpan.FromSeconds(_options.SignInSeconds), ct).ConfigureAwait(false);

        if (landed is null)
        {
            // Never "wrong password": a code the register refused and a
            // password it refused look identical from here, and telling
            // somebody to reset a password that was fine leaves the real
            // problem undiagnosed. The caller turns this into a streamed
            // hand-over, where the page itself can say which it was.
            throw new ConnectorException(
                ErrorCode.MfaFailed,
                $"{ProviderId}: the sign-in did not reach the portal after the code was submitted");
        }

        return Signed(ctx);
    }

    /// <summary>
    /// BKR's own page, streamed to whoever owns the account.
    ///
    /// Ends when the browser reaches the portal, which is the same terminal
    /// signal the typed path settles on - so somebody who signs in and never
    /// returns to the consumer's UI still finishes.
    /// </summary>
    private async Task<LoginResult> LiveSignInAsync(
        IJobContext ctx, ILoginPage page, IRedirectWaiter watcher, CancellationToken ct)
    {
        // Whatever route arrived here, the page must hold no secret before it
        // is photographed: the redactor refuses to shoot a page while a field
        // the manifest calls secret has content in it, so a password left in
        // the box would relay a live view of nothing at all.
        await page.ClearSecretsAsync(ct).ConfigureAwait(false);

        ctx.CredentialSubmitted();
        ctx.Progress(JobStep.AwaitingHuman);

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

                if (await watcher.WaitAsync(poll, ct).ConfigureAwait(false) is not null)
                {
                    ctx.Progress(JobStep.Finalizing);
                    return Signed(ctx);
                }

                if (asked.IsCompleted) break;
            }

            throw ConnectorException.Blocked(
                $"{ProviderId}: the live sign-in ended without reaching the portal");
        }
        finally
        {
            await view.CancelAsync().ConfigureAwait(false);
            _ = asked.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
        }
    }

    private async Task<string> AskForCodeAsync(IJobContext ctx, CancellationToken ct)
    {
        ctx.Progress(JobStep.AwaitingHuman);

        var answer = await ctx.AskAsync(new Challenge
        {
            Type = ChallengeType.MfaCode,
            PromptKey = MessageKeys.BkrCode,
            Length = Totp.DefaultDigits,
            ExpiresAt = _time.GetUtcNow().AddSeconds(_options.CodeChallengeSeconds),
        }, ct).ConfigureAwait(false);

        var code = answer.Value?.Trim();

        return string.IsNullOrWhiteSpace(code)
            ? throw new ConnectorException(ErrorCode.MfaFailed, $"{ProviderId}: no code was given")
            : code;
    }

    public async Task<FetchResult> FetchAsync(IJobContext ctx, ResourceRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(request);

        if (!string.Equals(request.ResourceId, CreditsResource, StringComparison.Ordinal))
        {
            throw ConnectorException.Unsupported($"{ProviderId}: no resource '{request.ResourceId}'");
        }

        ctx.Progress(JobStep.Downloading);

        // One page. The portal's own print block carries every credit's full
        // detail and the consumer's own record, so walking a detail page per
        // credit would be five extra requests for data already in hand.
        var page = await ctx.Browser.PageAsync(ct).ConfigureAwait(false);
        await page.GotoAsync(_options.PortalUrl, new() { Timeout = _options.SelectorTimeoutMs }).ConfigureAwait(false);

        var html = await page.ContentAsync().ConfigureAwait(false);

        ctx.Progress(JobStep.Parsing);

        var credits = BkrCreditParser.Parse(html, _options, ctx.SessionId);

        ctx.Progress(JobStep.Normalizing);

        return new FetchResult
        {
            Registrations = credits,
            Complete = true,
            Via = "portal",
            Raw = request.WantsRaw
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal),
        };
    }

    private LoginResult Signed(IJobContext ctx) => new()
    {
        Material = new SessionMaterial { DeviceId = ctx.Material?.DeviceId ?? Guid.NewGuid().ToString() },
        Account = new ProviderAccount { DisplayName = Manifest.Name },
        ExpiresAt = _time.GetUtcNow().AddSeconds(BkrManifest.SessionTtlSeconds),
    };

    /// <summary>
    /// The stored seed, if there is one. A seed that cannot be read is a
    /// refusal rather than a silent fall back to asking: somebody who pasted
    /// their authenticator's export deserves to be told it was not usable,
    /// not to be quietly prompted for six digits forever.
    /// </summary>
    private static TotpSecret? ReadSeed(IJobContext ctx)
    {
        var raw = Optional(ctx, "totp");
        return raw is null ? null : TotpSecretReader.Read(raw);
    }

    private static string? Optional(IJobContext ctx, string key) =>
        ctx.Inputs.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static ConnectorException Missing(string what, IReadOnlyList<string> selectors) =>
        ConnectorException.ProviderChanged(
            $"{ProviderId}: no element for {what}; tried [{string.Join(", ", selectors)}]");
}
