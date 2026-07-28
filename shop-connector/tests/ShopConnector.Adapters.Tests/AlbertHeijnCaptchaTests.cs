using Connector.Kit.Adapters;
using Connector.Kit.Challenges;
using Connector.Kit.Errors;
using Connector.Kit.Jobs;
using ShopConnector.Adapters.AlbertHeijn;
using ShopConnector.Adapters.Tests.Support;
using Xunit;

namespace ShopConnector.Adapters.Tests;

/// <summary>
/// The wall a real Albert Heijn login actually met, driven offline.
///
/// login.ah.nl is fronted by hCaptcha - confirmed by probing it - and hCaptcha
/// asks with pictures. The old answer to that was to tell whoever was sitting
/// at the agent to solve it in the browser window we had opened, which is a
/// usable instruction for about nobody: the person connecting an account is
/// holding a phone, in another building, and the agent is a process in a rack.
/// So the picture now travels to them instead. The challenge iframe is
/// photographed, relayed as an <see cref="ChallengeType.Image"/> whose
/// <see cref="ChallengeAnswerKind"/> is <see cref="ChallengeAnswerKind.Taps"/>,
/// and the points they tapped are replayed onto the same page with the mouse.
///
/// The line that does not move is which of us solves it. We carry a picture
/// out and gestures back; nobody here reads a grid, scores one, or asks a
/// service to. Every test below is about the carrying.
///
/// The live failure this suite was born from is still asserted, because it is
/// still one line away: the redactor refused to photograph a page whose
/// password field was full, the relay saw no bytes, and a job died of its
/// budget with nobody ever having been asked anything.
///
/// No browser and no network: the page, the redirect and the mouse are the
/// three seams a live login is met through.
/// </summary>
public sealed class AlbertHeijnCaptchaTests
{
    private const string Redirect = "appie://login-exit?code=arrived-after-the-wall";

    private static readonly DateTimeOffset Now = new(2026, 7, 26, 9, 0, 0, TimeSpan.Zero);

    private static readonly AlbertHeijnOptions Options = new();

    /// <summary>hCaptcha's grid, and its "I am human" box: two frames, one widget.</summary>
    private static string Grid => Options.CaptchaGridSelectors[0];

    private static string Checkbox => Options.CaptchaCheckboxSelectors[0];

    /// <summary>A widget whose parts we cannot name - the fallback list.</summary>
    private static string OpaqueWidget => Options.InteractiveCaptchaSelectors[2];

    private static AlbertHeijnAdapter Adapter() => new(Options, new FixedTimeProvider(Now));

    // ---- the grid: photographed out, tapped back --------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_hcaptcha_grid_is_relayed_as_a_picture_to_tap_and_never_as_an_app_approval(bool attended)
    {
        var page = StubLoginPage.Showing(Grid);
        var lease = new StubBrowserLease(page);
        var taps = new StubTapSurface(page);

        using var ctx = new FakeJobContext
        {
            // Both, and the same result: a phone in another room is a better
            // place to answer this than the chair in front of the agent, and
            // the account's owner is the right person either way. Attendance
            // is what the OLD answer turned on, and that is the whole point.
            Attended = attended,
            Browser = lease,
            Answer = _ => new TapAnswer
            {
                Taps = [new Tap(0.5, 0.25), new Tap(0.125, 0.875)],
                Submit = true,
            }.Format(),
        };

        var captured = await Adapter().SettleAsync(
            ctx, page, new StubRedirectWaiter(Redirect, afterWaits: 1), taps, CancellationToken.None);

        Assert.Equal(Redirect, captured);

        var challenge = Assert.Single(ctx.Asked);

        // An image with taps for an answer - not an AppApproval, which is what
        // "go and solve it where the browser is" was, and not a text box,
        // which no grid has ever been answerable with.
        Assert.Equal(ChallengeType.Image, challenge.Type);
        Assert.Equal(ChallengeAnswerKind.Taps, challenge.AnswerKind);
        Assert.NotEqual(ChallengeType.AppApproval, challenge.Type);
        Assert.False(challenge.IsPassive);

        // The picture, and the rectangle it is a picture of. The crop is what
        // lets the redactor drop the rest of the page rather than obscure it,
        // and it is also the frame the answer is measured against.
        Assert.NotEmpty(challenge.Image!);
        Assert.Equal(StubLoginPage.Box, challenge.Crop);
        Assert.Equal(StubLoginPage.Box, lease.LastCrop);

        // A key, never prose, and the same key a typed captcha uses: this is
        // not a different question, it is the same question answered with a
        // finger. Which control to draw is answer_kind's job.
        Assert.Equal("connect.challenge.captcha", challenge.PromptKey);

        // Long enough to cover somebody noticing a prompt on a phone.
        Assert.Equal(Now.AddSeconds(600), challenge.ExpiresAt);
        Assert.Equal(600, Options.InteractiveCaptchaSeconds);

        Assert.Contains(JobStep.AwaitingHuman, ctx.Steps);
        Assert.Equal(1, taps.Dispatches);
    }

    [Fact]
    public async Task The_taps_land_in_page_pixels_measured_from_the_rectangle_that_was_photographed()
    {
        var page = StubLoginPage.Showing(Grid);
        var taps = new StubTapSurface(page);

        // The two corners and the middle: enough to pin both halves of the
        // mapping, since an origin that was forgotten and a scale that was
        // wrong look identical from any single point.
        using var ctx = new FakeJobContext
        {
            Browser = new StubBrowserLease(page),
            Answer = _ => new TapAnswer
            {
                Taps = [new Tap(0, 0), new Tap(0.5, 0.25), new Tap(1, 1)],
                Submit = true,
            }.Format(),
        };

        await Adapter().SettleAsync(
            ctx, page, new StubRedirectWaiter(Redirect, afterWaits: 1), taps, CancellationToken.None);

        // Box is (12, 34) 300x80. A normalised tap is a fraction of the
        // RELAYED IMAGE, so 0 is that rectangle's own left edge and not the
        // viewport's - an agent that dropped the origin would click the top
        // left of the page with perfectly valid coordinates.
        Assert.Equal(new[] { (12d, 34d), (162d, 54d), (312d, 114d) }, taps.Points);

        // In the order the human made them, because some grids ask for tiles
        // in sequence and the order cannot be recovered once it is dropped.
        Assert.Equal(1, taps.Dispatches);
    }

    [Fact]
    public async Task The_password_is_cleared_out_of_the_dom_before_anything_photographs_the_page()
    {
        var page = StubLoginPage.Showing(Grid);
        var taps = new StubTapSurface(page);

        using var ctx = new FakeJobContext
        {
            Browser = new StubBrowserLease(page),
            Answer = _ => new TapAnswer { Taps = [new Tap(0.5, 0.5)], Submit = true }.Format(),
        };

        await Adapter().SettleAsync(
            ctx, page, new StubRedirectWaiter(Redirect, afterWaits: 1), taps, CancellationToken.None);

        // The order is the whole fix, and it is now three deep. The redactor
        // refuses - correctly, and this stub refuses with it - to photograph a
        // page while a secret field holds content, so a capture attempted
        // first comes back empty, nobody is ever asked anything, and nothing
        // is ever clicked.
        Assert.Equal(new[] { "clear-secrets", "screenshot", "tap" }, page.Calls);
        Assert.False(page.HoldsSecret);

        // Bytes, therefore: the picture the human tapped exists.
        var challenge = Assert.Single(ctx.Asked);
        Assert.NotEmpty(challenge.Image!);
    }

    [Fact]
    public async Task A_capture_the_redactor_refuses_is_blocked_by_provider_and_nobody_is_asked_to_tap_anything()
    {
        var page = StubLoginPage.Showing(Grid);
        var taps = new StubTapSurface(page);

        // The redactor's other refusal: it declines whatever it cannot verify,
        // not only a page still holding a password.
        var lease = new StubBrowserLease(page) { Refuses = true };

        using var ctx = new FakeJobContext { Attended = true, Browser = lease };

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().SettleAsync(
                ctx, page, new StubRedirectWaiter(redirect: null, afterWaits: 0), taps, CancellationToken.None));

        // Named, and immediate. Showing somebody a blank rectangle is worse
        // than admitting we are stuck, and clicking a grid nobody looked at is
        // the line this whole design exists on the right side of.
        Assert.Equal(ErrorCode.BlockedByProvider, error.Code);
        Assert.Contains("photographed", error.Detail, StringComparison.Ordinal);

        Assert.Empty(ctx.Asked);
        Assert.Equal(0, taps.Dispatches);
    }

    [Fact]
    public async Task A_typed_answer_to_a_grid_clicks_nothing_at_all()
    {
        var page = StubLoginPage.Showing(Grid);
        var taps = new StubTapSurface(page);

        // A consumer that ignored answer_kind, drew a text box over the
        // picture and sent back what the human typed into it. This is the
        // degradation the contract promises, and the promise is that nothing
        // happens - a tap inferred from prose is a click at a point no human
        // chose.
        using var ctx = new FakeJobContext { Browser = new StubBrowserLease(page), Answer = _ => "7GQX" };

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().SettleAsync(
                ctx, page, new StubRedirectWaiter(redirect: null, afterWaits: 0), taps, CancellationToken.None));

        Assert.Equal(0, taps.Dispatches);
        Assert.Equal(ErrorCode.BlockedByProvider, error.Code);

        // The grid, then the last resort: finish on AH's own page and hand
        // back the redirect. "7GQX" is not one of those either.
        Assert.Equal([ChallengeType.Image, ChallengeType.Redirect], ctx.Asked.Select(c => c.Type));
    }

    [Fact]
    public async Task A_grid_that_never_goes_away_stops_after_its_rounds_instead_of_asking_forever()
    {
        var page = StubLoginPage.Showing(Grid);
        var taps = new StubTapSurface(page);

        using var ctx = new FakeJobContext
        {
            Browser = new StubBrowserLease(page),
            Answer = _ => new TapAnswer { Taps = [new Tap(0.5, 0.5)], Submit = true }.Format(),
        };

        // The redirect that never comes, and a widget that keeps asking. The
        // exit condition here would otherwise belong to hCaptcha's JavaScript.
        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().SettleAsync(
                ctx, page, new StubRedirectWaiter(redirect: null, afterWaits: 0), taps, CancellationToken.None));

        Assert.Equal(Options.CaptchaRelayRounds, ctx.Asked.Count);
        Assert.Equal(4, Options.CaptchaRelayRounds);

        // Unattended, so there is nobody at the browser to hand it to either.
        Assert.Equal(ErrorCode.BlockedByProvider, error.Code);
    }

    [Fact]
    public async Task A_human_who_is_not_finished_gets_the_next_picture_without_a_wasted_poll()
    {
        var page = StubLoginPage.Showing(Grid);
        var taps = new StubTapSurface(page);
        var round = 0;

        using var ctx = new FakeJobContext
        {
            Browser = new StubBrowserLease(page),

            // Taps now, finished later: the marker left off means the human is
            // still choosing, which is a legal thing for a client to send.
            Answer = _ => new TapAnswer
            {
                Taps = [new Tap(0.5, 0.5)],
                Submit = round++ > 0,
            }.Format(),
        };

        var watcher = new StubRedirectWaiter(Redirect, afterWaits: 1);

        var captured = await Adapter().SettleAsync(ctx, page, watcher, taps, CancellationToken.None);

        Assert.Equal(Redirect, captured);
        Assert.Equal(2, ctx.Asked.Count);
        Assert.Equal(2, taps.Dispatches);

        // One poll before the wall was found and one after the round the human
        // called finished - and none in between, because a grid somebody is
        // halfway through has given the provider nothing to move on from.
        Assert.Equal(2, watcher.Waits);
    }

    // ---- the checkbox: one real click on one real control -----------------

    [Fact]
    public async Task A_checkbox_on_its_own_is_ticked_and_nobody_is_asked_anything()
    {
        var page = StubLoginPage.Showing(Checkbox);
        var lease = new StubBrowserLease(page);
        var taps = new StubTapSurface(page);

        using var ctx = new FakeJobContext { Browser = lease };

        var captured = await Adapter().SettleAsync(
            ctx, page, new StubRedirectWaiter(Redirect, afterWaits: 1), taps, CancellationToken.None);

        Assert.Equal(Redirect, captured);

        // Clicking it is the gesture the control exists to receive, and
        // frequently the whole of what the widget asks. There is nothing to
        // relay yet: no picture has been drawn.
        Assert.Equal(new[] { "clear-secrets", "click" }, page.Calls);
        Assert.Equal(Checkbox, Assert.Single(page.Clicked));

        Assert.Empty(ctx.Asked);
        Assert.Equal(0, lease.Captures);
        Assert.Equal(0, taps.Dispatches);
    }

    [Fact]
    public async Task A_checkbox_that_escalates_into_a_grid_relays_the_grid_and_is_never_ticked_twice()
    {
        // hCaptcha decides after the tick: sometimes it is satisfied, and
        // sometimes it draws the pictures. The box stays on the page either
        // way - which is exactly why a second tick would undo the first.
        var page = StubLoginPage.Showing(Checkbox);
        page.WhenClicked = (clicked, _) => clicked.Reveal(Grid);

        var taps = new StubTapSurface(page);

        using var ctx = new FakeJobContext
        {
            Browser = new StubBrowserLease(page),
            Answer = _ => new TapAnswer { Taps = [new Tap(0.5, 0.5)], Submit = true }.Format(),
        };

        var captured = await Adapter().SettleAsync(
            ctx, page, new StubRedirectWaiter(Redirect, afterWaits: 2), taps, CancellationToken.None);

        Assert.Equal(Redirect, captured);

        // Tick, then picture, then taps - and the box, still on the page and
        // still matching its selector, is left alone.
        Assert.Equal(new[] { "clear-secrets", "click", "screenshot", "tap" }, page.Calls);
        Assert.Equal(Checkbox, Assert.Single(page.Clicked));

        var challenge = Assert.Single(ctx.Asked);
        Assert.Equal(ChallengeType.Image, challenge.Type);
        Assert.Equal(ChallengeAnswerKind.Taps, challenge.AnswerKind);
        Assert.Equal(1, taps.Dispatches);
    }

    [Fact]
    public async Task A_tick_that_does_not_take_relays_the_box_so_the_human_can_press_it_themselves()
    {
        // Our click lands in the middle of the frame, and the middle of
        // hCaptcha's checkbox frame is the words beside the box. Nothing here
        // guesses how far left the box is - an offset measured off somebody
        // else's widget is wrong the week they restyle it, and wrong
        // invisibly. The frame is a picture, so it is relayed like one.
        var page = StubLoginPage.Showing(Checkbox);
        var lease = new StubBrowserLease(page);
        var taps = new StubTapSurface(page);

        using var ctx = new FakeJobContext
        {
            Browser = lease,
            Answer = _ => new TapAnswer { Taps = [new Tap(0.05, 0.5)], Submit = true }.Format(),
        };

        var captured = await Adapter().SettleAsync(
            ctx, page, new StubRedirectWaiter(Redirect, afterWaits: 2), taps, CancellationToken.None);

        Assert.Equal(Redirect, captured);
        Assert.Equal(new[] { "clear-secrets", "click", "screenshot", "tap" }, page.Calls);

        // Ticked once and then asked - not ticked again, which would undo it.
        Assert.Equal(Checkbox, Assert.Single(page.Clicked));

        var challenge = Assert.Single(ctx.Asked);
        Assert.Equal(ChallengeAnswerKind.Taps, challenge.AnswerKind);
        Assert.Equal(StubLoginPage.Box, challenge.Crop);

        // Against the box the CHECKBOX frame occupies, which is the rectangle
        // that was photographed - not the grid's, which is not on the page.
        Assert.Equal([(27d, 74d)], taps.Points);
    }

    // ---- the walls this seam must not have changed ------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_plain_image_captcha_is_still_relayed_as_a_picture_and_answered_in_text(bool attended)
    {
        // A picture and a box to type the answer into - the captcha a relay
        // has always been able to carry, to a human anywhere.
        var page = StubLoginPage.Showing(Options.ImageCaptchaSelectors[0], Options.CaptchaInputSelectors[0]);
        var lease = new StubBrowserLease(page);
        var taps = new StubTapSurface(page);

        using var ctx = new FakeJobContext
        {
            Attended = attended,
            Browser = lease,
            Answer = _ => "7GQX",
        };

        var captured = await Adapter().SettleAsync(
            ctx, page, new StubRedirectWaiter(Redirect, afterWaits: 1), taps, CancellationToken.None);

        Assert.Equal(Redirect, captured);

        var challenge = Assert.Single(ctx.Asked);
        Assert.Equal(ChallengeType.Image, challenge.Type);

        // Text, and stated rather than assumed: the same challenge type now
        // carries two kinds of answer, and a consumer that drew a tappable
        // grid over this one would be showing the wrong control.
        Assert.Equal(ChallengeAnswerKind.Text, challenge.AnswerKind);

        Assert.Equal("connect.challenge.captcha", challenge.PromptKey);
        Assert.Equal(Now.AddSeconds(300), challenge.ExpiresAt);
        Assert.Equal(StubLoginPage.Box, challenge.Crop);
        Assert.Equal(StubLoginPage.Box, lease.LastCrop);

        // The human's answer goes into the captcha's own box, and nothing is
        // clicked anywhere. The credentials are deliberately not resubmitted:
        // a second submission of a password that may already have counted is
        // how an account gets locked.
        Assert.Equal("7GQX", page.Answered);
        Assert.Equal(0, taps.Dispatches);
    }

    [Fact]
    public async Task A_widget_with_no_grid_and_nobody_at_the_browser_is_refused_at_once()
    {
        var page = StubLoginPage.Showing(OpaqueWidget);
        var taps = new StubTapSurface(page);

        using var ctx = new FakeJobContext { Browser = new StubBrowserLease(page) };

        // The redirect that never comes. If the adapter waited for one, this
        // test would hang exactly as the live run did.
        var watcher = new StubRedirectWaiter(redirect: null, afterWaits: 0);

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().SettleAsync(ctx, page, watcher, taps, CancellationToken.None));

        // Blocked, not internal and not invalid_credentials: nothing is wrong
        // with the password and nothing is wrong with us. There is no grid to
        // photograph and nobody at this browser to show it to.
        Assert.Equal(ErrorCode.BlockedByProvider, error.Code);
        Assert.Contains("unattended", error.Detail, StringComparison.Ordinal);

        // Not one question, because there is nobody to answer it. A challenge
        // raised here is a job pinned to an agent until its budget expires -
        // which is the failure this whole path exists to remove.
        Assert.Empty(ctx.Asked);
        Assert.Equal(0, taps.Dispatches);

        // Immediately: one poll of the redirect, then the refusal. The live
        // failure was 300 seconds of exactly this loop.
        Assert.Equal(1, watcher.Waits);
    }

    [Fact]
    public async Task A_widget_with_no_grid_still_asks_the_human_at_the_browser_to_pass_it_there()
    {
        var page = StubLoginPage.Showing(OpaqueWidget);
        var lease = new StubBrowserLease(page);

        using var ctx = new FakeJobContext { Attended = true, Browser = lease, Answer = _ => string.Empty };

        var captured = await Adapter().SettleAsync(
            ctx, page, new StubRedirectWaiter(Redirect, afterWaits: 2), new StubTapSurface(page),
            CancellationToken.None);

        Assert.Equal(Redirect, captured);

        var challenge = Assert.Single(ctx.Asked);

        // The surviving fallback, and passive because it has to be: there is
        // no picture to send and nothing to type back, so the redirect is the
        // only proof the wall came down.
        Assert.Equal(ChallengeType.AppApproval, challenge.Type);
        Assert.True(challenge.IsPassive);
        Assert.Equal(ChallengeAnswerKind.Text, challenge.AnswerKind);
        Assert.Null(challenge.Image);
        Assert.Equal("connect.challenge.captcha_in_browser", challenge.PromptKey);

        // Nothing is relayed for it, so nothing is captured either - but the
        // job may still fail later, and the artifact the runner takes on the
        // way out must not be refused over a password nobody needs any more.
        Assert.Equal(new[] { "clear-secrets" }, page.Calls);
        Assert.Equal(0, lease.Captures);
    }

    [Fact]
    public async Task A_widget_solved_silently_at_the_browser_still_finishes_on_the_redirect()
    {
        var page = StubLoginPage.Showing(OpaqueWidget);

        // Nobody ever answers the challenge - which is the normal case, not
        // the edge one: the person solving the widget in the browser has no
        // reason to go back to the consumer's UI and click anything.
        using var ctx = new FakeJobContext
        {
            Attended = true,
            Browser = new StubBrowserLease(page),
            AnswersNothing = true,
        };

        // A ceiling, so a regression into awaiting the answer fails here
        // rather than hanging the suite.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var captured = await Adapter().SettleAsync(
            ctx, page, new StubRedirectWaiter(Redirect, afterWaits: 1), cts.Token);

        Assert.Equal(Redirect, captured);
        Assert.Single(ctx.Asked);
    }

    [Fact]
    public async Task A_page_that_states_a_credential_error_still_ends_the_login_as_one()
    {
        var page = StubLoginPage.Showing(Options.LoginErrorSelectors[0]);
        using var ctx = new FakeJobContext { Browser = new StubBrowserLease(page) };

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().SettleAsync(
                ctx, page, new StubRedirectWaiter(redirect: null, afterWaits: 0), CancellationToken.None));

        // The only path allowed to say this, and nothing retries it: guessing
        // "wrong password" from silence sends a user to reset a password that
        // was fine.
        Assert.Equal(ErrorCode.InvalidCredentials, error.Code);
        Assert.Empty(ctx.Asked);
    }

    [Fact]
    public async Task A_login_with_no_wall_at_all_just_returns_the_redirect()
    {
        var page = StubLoginPage.Showing();
        using var ctx = new FakeJobContext { Browser = new StubBrowserLease(page) };

        var captured = await Adapter().SettleAsync(
            ctx, page, new StubRedirectWaiter(Redirect, afterWaits: 0), new StubTapSurface(page),
            CancellationToken.None);

        Assert.Equal(Redirect, captured);
        Assert.Empty(ctx.Asked);
        Assert.Empty(page.Calls);
    }
}
