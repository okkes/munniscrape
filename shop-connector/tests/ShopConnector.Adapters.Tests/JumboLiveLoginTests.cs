using Connector.Kit.Challenges;
using Connector.Kit.Errors;
using Connector.Kit.Jobs;
using ShopConnector.Adapters.Jumbo;
using ShopConnector.Adapters.Tests.Support;
using Xunit;

namespace ShopConnector.Adapters.Tests;

/// <summary>
/// The login Jumbo actually performs now, driven offline.
///
/// The typed login could not finish. A real connect on 2026-07-31 sat on
/// <c>auth.jumbo.com/u/login</c> for the full 180 seconds and failed
/// <c>provider_changed</c>; the page was carrying Auth0's <c>auth0_v2</c>
/// captcha, which is Cloudflare Turnstile. No challenge was ever raised,
/// because no selector matched the widget - but detecting it would only have
/// changed the error, not the outcome: a Turnstile token is minted by its own
/// JavaScript in the browser that rendered it, against that browser. There is
/// no picture to relay out and no tap to replay back.
///
/// So the browser goes to the human instead of the wall coming to us. What
/// that buys is the same thing it bought Albert Heijn: no username and no
/// password is asked for, so none is posted to the connector, written to a
/// job's inputs, or held anywhere by anything here.
/// </summary>
public sealed class JumboLiveLoginTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 26, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Back on jumbo.com and off every login marker: the session exists.</summary>
    private const string Signed = "https://www.jumbo.com/mijn-jumbo/bestellingen";

    private static JumboAdapter Adapter(JumboOptions? options = null) =>
        new(options ?? new JumboOptions(), new FakeJumboGraphQl(), new FixedTimeProvider(Now));

    /// <summary>
    /// Nothing on it. The streamed login looks for no selector at all, and a
    /// page offering none is how that is asserted rather than assumed.
    /// </summary>
    private static StubLoginPage Page() => StubLoginPage.Showing();

    private static FakeJobContext Context(StubLoginPage page, string? storageState = null) =>
        new()
        {
            AnswersNothing = true,
            Browser = new JumboBrowserLease(page, storageState),
        };

    private static StubRedirectWaiter Arrives(int afterWaits = 0) => new(Signed, afterWaits);

    private static StubRedirectWaiter Never() => new(redirect: null, afterWaits: 0);

    // ---- the shape of the thing --------------------------------------------

    [Fact]
    public void The_streamed_login_is_what_jumbo_ships()
    {
        var options = new JumboOptions();

        Assert.True(options.LiveLogin);
        Assert.Equal(900, options.LiveLoginSeconds);
    }

    [Fact]
    public async Task The_streamed_login_opens_jumbos_own_page_and_finishes_on_the_session_alone()
    {
        var page = Page();
        using var ctx = Context(page);

        var result = await Adapter().LiveLoginAsync(ctx, page, Arrives(), CancellationToken.None);

        // Jumbo's own login chain, once. It 302s to /api/auth/login and on to
        // Auth0; the address the adapter used to open is a 404.
        Assert.Equal("https://www.jumbo.com/account/inloggen", Assert.Single(page.Visited));

        // The cookies are the credential.
        Assert.NotNull(result.Material.StorageState);
        Assert.Equal("Jumbo", result.Account!.DisplayName);

        // 24 hours, stated from this login rather than left to the manifest, so
        // the consumer's countdown starts now.
        Assert.Equal(Now.AddSeconds(86_400), result.ExpiresAt);

        Assert.Equal([JobStep.AwaitingHuman, JobStep.Finalizing], ctx.Steps);
    }

    [Fact]
    public async Task The_streamed_login_types_nothing_even_when_credentials_were_handed_to_it()
    {
        var page = Page();

        // A consumer that sent them anyway. The manifest declares no fields and
        // the validator refuses one that does, so this cannot arrive through
        // the control plane - but the promise is that the path does not use a
        // password, not that nobody ever supplies one.
        using var ctx = new FakeJobContext
        {
            AnswersNothing = true,
            Browser = new JumboBrowserLease(page),
            Inputs = JumboFixtures.Credentials,
        };

        await Adapter().LiveLoginAsync(ctx, page, Arrives(), CancellationToken.None);

        Assert.Empty(page.Filled);
        Assert.Empty(page.Clicked);
        Assert.Empty(page.Calls);

        // No password entered the DOM, so there is nothing to clear out of it.
        Assert.True(page.HoldsSecret);

        // Deliberately NOT latched. The latch exists so a lost lease does not
        // requeue a login whose password already counted against an account,
        // and the attempts here are the human's own on Jumbo's own page.
        Assert.False(ctx.CredentialWasSubmitted);
    }

    [Fact]
    public async Task The_human_is_asked_once_for_a_passive_live_view_and_never_for_a_string()
    {
        var page = Page();
        using var ctx = Context(page);

        await Adapter().LiveLoginAsync(ctx, page, Arrives(afterWaits: 2), CancellationToken.None);

        // Once, not once per poll.
        var challenge = Assert.Single(ctx.Asked);

        Assert.Equal(ChallengeType.LiveView, challenge.Type);
        Assert.Equal("connect.challenge.live_login", challenge.PromptKey);
        Assert.True(challenge.IsPassive);
        Assert.Equal(ChallengeAnswerKind.Text, challenge.AnswerKind);

        // Not a picture. Relaying one is exactly what Turnstile cannot be
        // answered by, and offering it would put a box in front of somebody
        // that nothing they type into can work.
        Assert.Null(challenge.Image);
        Assert.NotEqual(ChallengeType.Image, challenge.Type);
    }

    [Theory]
    [InlineData(900)]
    [InlineData(240)]
    public async Task The_window_the_human_gets_is_the_configured_one(int seconds)
    {
        var page = Page();
        using var ctx = Context(page);

        await Adapter(new JumboOptions { LiveLoginSeconds = seconds })
            .LiveLoginAsync(ctx, page, Arrives(), CancellationToken.None);

        Assert.Equal(Now.AddSeconds(seconds), Assert.Single(ctx.Asked).ExpiresAt);
    }

    // ---- letting go ---------------------------------------------------------

    [Fact]
    public async Task The_view_is_ended_when_the_session_appears_and_not_merely_abandoned()
    {
        var page = Page();
        using var ctx = Context(page);

        await Adapter().LiveLoginAsync(ctx, page, Arrives(), CancellationToken.None);

        // Without this the shutter keeps photographing for the rest of the
        // window, the agent stays occupied, and the human sits looking at a
        // frozen picture of a step they already finished.
        Assert.True(Assert.Single(ctx.AskTokens).IsCancellationRequested);
    }

    [Fact]
    public async Task The_view_is_ended_when_the_window_runs_out_too()
    {
        var page = Page();
        using var ctx = Context(page);
        var watcher = Never();

        await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter(new JumboOptions { LiveLoginSeconds = 0 })
                .LiveLoginAsync(ctx, page, watcher, CancellationToken.None));

        Assert.True(Assert.Single(ctx.AskTokens).IsCancellationRequested);
        Assert.Equal(0, watcher.Waits);
    }

    [Fact]
    public async Task A_human_who_closes_the_view_ends_the_login_rather_than_polling_out_the_window()
    {
        var page = Page();

        using var ctx = new FakeJobContext
        {
            Browser = new JumboBrowserLease(page),
            Answer = _ => string.Empty,
        };

        var watcher = Never();

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().LiveLoginAsync(ctx, page, watcher, CancellationToken.None));

        Assert.Equal(ErrorCode.BlockedByProvider, error.Code);
        Assert.Equal(1, watcher.Waits);
    }

    // ---- what the ending is called -----------------------------------------

    [Fact]
    public async Task A_login_that_never_reached_a_session_is_blocked_and_never_a_wrong_password()
    {
        var page = Page();
        using var ctx = Context(page);

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter(new JumboOptions { LiveLoginSeconds = 0 })
                .LiveLoginAsync(ctx, page, Never(), CancellationToken.None));

        // Nobody typed a password into anything of ours, so calling this a
        // credential failure would send the user to reset one that was fine -
        // and a credential error is never retried, so the mistake would be
        // permanent for that session too.
        Assert.Equal(ErrorCode.BlockedByProvider, error.Code);
        Assert.NotEqual(ErrorCode.InvalidCredentials, error.Code);
        Assert.Equal(
            "jumbo: the live login ended without reaching a session; the sign-in was not completed",
            error.Detail);
    }

    /// <summary>
    /// The URL is not the credential. A page that looks signed in but left no
    /// jumbo.com cookies behind produced nothing this connector can fetch with.
    /// </summary>
    [Fact]
    public async Task A_session_that_left_no_cookies_behind_is_a_shape_change()
    {
        var page = Page();
        using var ctx = Context(page, storageState: """{"cookies":[],"origins":[]}""");

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().LiveLoginAsync(ctx, page, Arrives(), CancellationToken.None));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Equal("jumbo: login left no jumbo.com cookies behind", error.Detail);

        // Still ended, because the sign-in is over either way.
        Assert.True(Assert.Single(ctx.AskTokens).IsCancellationRequested);
    }

    [Fact]
    public async Task The_session_finishes_it_even_though_nobody_ever_answers_the_view()
    {
        var page = Page();

        // The case a passive challenge exists for: the human acts where we
        // cannot observe - on the streamed page itself - and has no reason to
        // come back to the consumer's UI and click anything.
        using var ctx = Context(page);
        var watcher = Arrives(afterWaits: 2);

        var result = await Adapter().LiveLoginAsync(ctx, page, watcher, CancellationToken.None);

        Assert.NotNull(result.Material.StorageState);
        Assert.Equal(3, watcher.Waits);
        Assert.Single(ctx.Asked);
    }

    /// <summary>
    /// A device id is minted once and carried for the life of the session: a
    /// device that changes identity every run looks like exactly what it is.
    /// </summary>
    [Fact]
    public async Task A_reconnect_keeps_the_device_it_first_signed_in_as()
    {
        var page = Page();

        using var ctx = new FakeJobContext
        {
            AnswersNothing = true,
            Browser = new JumboBrowserLease(page),
            Material = new Connector.Kit.Security.SessionMaterial { DeviceId = "dev-first-time" },
        };

        var result = await Adapter().LiveLoginAsync(ctx, page, Arrives(), CancellationToken.None);

        Assert.Equal("dev-first-time", result.Material.DeviceId);
    }
}
