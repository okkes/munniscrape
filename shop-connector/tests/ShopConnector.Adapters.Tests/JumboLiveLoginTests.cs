using Connector.Kit.Challenges;
using Connector.Kit.Errors;
using Connector.Kit.Jobs;
using Connector.Kit.Security;
using ShopConnector.Adapters.Jumbo;
using ShopConnector.Adapters.Tests.Support;
using Xunit;

namespace ShopConnector.Adapters.Tests;

/// <summary>
/// The login Jumbo actually performs: type what we were given, and hand the
/// page over the moment Auth0 asks something only a human can answer.
///
/// The wall is why it is shaped this way. A real connect on 2026-07-31 sat on
/// <c>auth.jumbo.com/u/login</c> for the full 180 seconds and failed
/// <c>provider_changed</c>, with no challenge raised at all - the page was
/// carrying Auth0's <c>auth0_v2</c> captcha, which is Cloudflare Turnstile.
/// Detecting it would only have changed the error: a Turnstile token is minted
/// by its own JavaScript in the browser that rendered it, against that browser,
/// so there is no picture to relay out and no tap to replay back.
///
/// Auth0 raises it on a risk score, so it is there some days and not others.
/// That is the whole reason both credentials are optional rather than absent:
/// on a quiet day the stored credential goes in and nobody is disturbed, and on
/// a walled day the same half-filled page is streamed to whoever owns the
/// account.
/// </summary>
public sealed class JumboLiveLoginTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 26, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Back on jumbo.com and off every login marker: the session exists.</summary>
    private const string Signed = "https://www.jumbo.com/mijn-jumbo/bestellingen";

    /// <summary>Turnstile, as it appears in the markup Auth0 renders.</summary>
    private static string Wall => new JumboOptions().InteractiveCaptchaSelectors
        .First(s => s.Contains("cloudflare", StringComparison.Ordinal));

    private static JumboAdapter Adapter(JumboOptions? options = null) =>
        new(options ?? new JumboOptions(), new FakeJumboGraphQl(), new FixedTimeProvider(Now));

    /// <summary>The form, as it stands on a quiet day.</summary>
    private static StubLoginPage FormPage(params string[] extra) =>
        StubLoginPage.Showing([.. new[] { "input#username", "input#password", "button[type='submit']" }, .. extra]);

    /// <summary>Nothing on it: what a page looks like to a login that types nothing.</summary>
    private static StubLoginPage BarePage() => StubLoginPage.Showing();

    private static FakeJobContext Context(
        StubLoginPage page, bool withCredentials, string? storageState = null) => new()
        {
            AnswersNothing = true,
            Browser = new JumboBrowserLease(page, storageState),
            Inputs = withCredentials
                ? JumboFixtures.Credentials
                : new Dictionary<string, string>(StringComparer.Ordinal),
        };

    private static StubRedirectWaiter Arrives(int afterWaits = 0) => new(Signed, afterWaits);

    private static StubRedirectWaiter Never() => new(redirect: null, afterWaits: 0);

    // ---- the quiet day: nobody is disturbed --------------------------------

    [Fact]
    public async Task Credentials_and_no_wall_sign_in_without_asking_anybody()
    {
        var page = FormPage();
        using var ctx = Context(page, withCredentials: true);

        var result = await Adapter().LoginAsync(ctx, page, Arrives(), CancellationToken.None);

        // Jumbo's own login chain, which 302s to Auth0.
        Assert.Equal("https://www.jumbo.com/account/inloggen", Assert.Single(page.Visited));

        // Typed and submitted by the adapter.
        Assert.Equal(JumboFixtures.Credentials["username"], page.Filled["input#username"]);
        Assert.Equal(JumboFixtures.Credentials["password"], page.Filled["input#password"]);
        Assert.Contains("button[type='submit']", page.Clicked);

        // And the human was never involved. This is the case that makes a
        // stored credential worth having at all.
        Assert.Empty(ctx.Asked);

        Assert.NotNull(result.Material.StorageState);
        Assert.Equal(Now.AddSeconds(86_400), result.ExpiresAt);
    }

    [Fact]
    public async Task A_submitted_credential_latches_so_a_lost_lease_never_replays_it()
    {
        var page = FormPage();
        using var ctx = Context(page, withCredentials: true);

        await Adapter().LoginAsync(ctx, page, Arrives(), CancellationToken.None);

        // A retried login is how an account gets locked.
        Assert.True(ctx.CredentialWasSubmitted);
    }

    // ---- the walled day: the human finishes --------------------------------

    [Fact]
    public async Task A_wall_before_the_click_hands_over_without_spending_an_attempt()
    {
        var page = FormPage(Wall);
        using var ctx = Context(page, withCredentials: true);

        var result = await Adapter().LoginAsync(ctx, page, Arrives(), CancellationToken.None);

        // The username is in the box, so the human inherits the half they
        // would otherwise have to go and look up.
        Assert.Equal(JumboFixtures.Credentials["username"], page.Filled["input#username"]);

        // The password is NOT, and that is deliberate twice over. The wall is
        // checked before it would be typed, so it never enters a DOM this run
        // is about to hand to a shutter - and the redactor refuses to
        // photograph a page holding a secret, so a filled box would have
        // relayed a live view of nothing at all.
        Assert.DoesNotContain("input#password", page.Filled.Keys);
        Assert.False(page.HoldsSecret);

        // Nothing was submitted either, so no attempt was spent against the
        // account and nothing was latched - the sign-in that follows is
        // entirely theirs.
        Assert.DoesNotContain("button[type='submit']", page.Clicked);
        Assert.False(ctx.CredentialWasSubmitted);

        var challenge = Assert.Single(ctx.Asked);
        Assert.Equal(ChallengeType.LiveView, challenge.Type);
        Assert.True(challenge.IsPassive);

        Assert.NotNull(result.Material.StorageState);
    }

    [Fact]
    public async Task No_credentials_at_all_streams_the_page_from_the_start()
    {
        var page = BarePage();
        using var ctx = Context(page, withCredentials: false);

        var result = await Adapter().LoginAsync(ctx, page, Arrives(), CancellationToken.None);

        // What a first connect looks like. Nothing is typed, because nothing
        // was given.
        Assert.Empty(page.Filled);
        Assert.Empty(page.Clicked);
        Assert.False(ctx.CredentialWasSubmitted);

        Assert.Equal(ChallengeType.LiveView, Assert.Single(ctx.Asked).Type);
        Assert.NotNull(result.Material.StorageState);
    }

    /// <summary>
    /// The wall that appears AFTER the click, a one-time code, or anything
    /// nobody has seen. Every one of those is answerable by the person who owns
    /// the account, so the run is handed over rather than failed.
    /// </summary>
    [Fact]
    public async Task A_submit_that_reaches_no_session_hands_over_rather_than_failing()
    {
        var page = FormPage();
        using var ctx = Context(page, withCredentials: true);

        // Submitted, and the settle budget is spent without a session -
        // afterWaits:1 is the one poll that budget affords, so the redirect
        // arrives to the LIVE VIEW rather than to the automatic path.
        var result = await Adapter(new JumboOptions { LoginSettleSeconds = 0 })
            .LoginAsync(ctx, page, Arrives(afterWaits: 1), CancellationToken.None);

        Assert.Contains("button[type='submit']", page.Clicked);
        Assert.Equal(ChallengeType.LiveView, Assert.Single(ctx.Asked).Type);
        Assert.NotNull(result.Material.StorageState);
    }

    // ---- the one thing streaming cannot fix --------------------------------

    /// <summary>
    /// A stated wrong password must NOT be handed to a human to retype: it has
    /// to reach the consumer, so a stored credential is dropped rather than
    /// re-submitted by machine tomorrow. That is the failure mode a scheduled
    /// login has that a human-driven one does not.
    /// </summary>
    [Fact]
    public async Task A_stated_wrong_password_is_reported_and_never_escalated()
    {
        var page = FormPage(new JumboOptions().LoginErrorSelectors[0]);
        using var ctx = Context(page, withCredentials: true);

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().LoginAsync(ctx, page, Never(), CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidCredentials, error.Code);

        // Nobody was asked to fix a password that is simply wrong.
        Assert.Empty(ctx.Asked);
        Assert.False(error.Retriable);
    }

    // ---- what the live view promises ---------------------------------------

    [Fact]
    public async Task The_live_view_carries_the_configured_window_and_is_asked_once()
    {
        var page = BarePage();
        using var ctx = Context(page, withCredentials: false);

        await Adapter(new JumboOptions { LiveLoginSeconds = 240 })
            .LoginAsync(ctx, page, Arrives(afterWaits: 2), CancellationToken.None);

        var challenge = Assert.Single(ctx.Asked);

        Assert.Equal(Now.AddSeconds(240), challenge.ExpiresAt);
        Assert.Equal("connect.challenge.live_login", challenge.PromptKey);

        // Not a picture. Relaying one is exactly what Turnstile cannot be
        // answered by.
        Assert.Null(challenge.Image);
        Assert.NotEqual(ChallengeType.Image, challenge.Type);
    }

    [Fact]
    public async Task The_view_is_ended_when_the_session_appears_and_not_merely_abandoned()
    {
        var page = BarePage();
        using var ctx = Context(page, withCredentials: false);

        await Adapter().LoginAsync(ctx, page, Arrives(), CancellationToken.None);

        // Without this the shutter keeps photographing, the agent stays
        // occupied, and the human looks at a frozen picture of a step they
        // already finished.
        Assert.True(Assert.Single(ctx.AskTokens).IsCancellationRequested);
    }

    [Fact]
    public async Task A_live_view_that_runs_out_is_blocked_and_never_a_wrong_password()
    {
        var page = BarePage();
        using var ctx = Context(page, withCredentials: false);

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter(new JumboOptions { LiveLoginSeconds = 0 })
                .LoginAsync(ctx, page, Never(), CancellationToken.None));

        // Nobody typed a password into anything of ours, so calling this a
        // credential failure would send the user to reset one that was fine.
        Assert.Equal(ErrorCode.BlockedByProvider, error.Code);
        Assert.NotEqual(ErrorCode.InvalidCredentials, error.Code);
        Assert.True(Assert.Single(ctx.AskTokens).IsCancellationRequested);
    }

    /// <summary>
    /// The URL is not the credential. A page that looks signed in but left no
    /// jumbo.com cookies produced nothing this connector can fetch with.
    /// </summary>
    [Fact]
    public async Task A_session_that_left_no_cookies_behind_is_a_shape_change()
    {
        var page = BarePage();
        using var ctx = Context(page, withCredentials: false, storageState: """{"cookies":[],"origins":[]}""");

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().LoginAsync(ctx, page, Arrives(), CancellationToken.None));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Equal("jumbo: login left no jumbo.com cookies behind", error.Detail);
    }

    [Fact]
    public async Task A_reconnect_keeps_the_device_it_first_signed_in_as()
    {
        var page = FormPage(Wall);

        using var ctx = new FakeJobContext
        {
            AnswersNothing = true,
            Browser = new JumboBrowserLease(page),
            Inputs = JumboFixtures.Credentials,
            Material = new SessionMaterial { DeviceId = "dev-first-time" },
        };

        var result = await Adapter().LoginAsync(ctx, page, Arrives(), CancellationToken.None);

        // Carried across the hand-over too: a run that typed first and then
        // gave the page to a human is still the same device.
        Assert.Equal("dev-first-time", result.Material.DeviceId);
    }

    [Fact]
    public void The_shipped_window_is_fifteen_minutes()
    {
        Assert.Equal(900, new JumboOptions().LiveLoginSeconds);
    }
}
