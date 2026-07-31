using Connector.Kit.Challenges;
using Connector.Kit.Errors;
using Connector.Kit.Jobs;
using ShopConnector.Adapters.AlbertHeijn;
using ShopConnector.Adapters.Tests.Support;
using Xunit;

namespace ShopConnector.Adapters.Tests;

/// <summary>
/// The login Albert Heijn actually performs, driven offline.
///
/// <see cref="AlbertHeijnOptions.LiveLogin"/> ships true, so this is the path
/// every real connect takes - and until this file existed it was the one path
/// nothing asserted. Every case in <see cref="AlbertHeijnAdapterTests"/> passes
/// <c>liveLogin: false</c> and exercises the typed form, which production
/// reaches only if an operator turns the default off.
///
/// What is pinned here is the whole reason the streamed login exists, because
/// all of it is one line away from being untrue: that this adapter types
/// nothing, asks for nothing, latches no credential - and that when the sign-in
/// is over the view is ENDED rather than left photographing a page nobody is
/// looking at. An agent runs one job at a time, so a login that never lets go
/// wedges every later one behind it.
///
/// No browser and no network: the page and the redirect are the two seams a
/// live login is met through.
/// </summary>
public sealed class AlbertHeijnLiveLoginTests
{
    private const string Code = "signed-in-on-ahs-own-page";

    private const string Redirect = $"appie://login-exit?code={Code}";

    private static readonly DateTimeOffset Now = new(2026, 7, 26, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The shipped options, deliberately: a test that hand-built its own would
    /// stop describing the connect an actual user gets.
    /// </summary>
    private static AlbertHeijnAdapter Adapter(AlbertHeijnOptions? options = null) =>
        new(options ?? new AlbertHeijnOptions(), new FixedTimeProvider(Now));

    /// <summary>The redirect on the first poll - a human who signed in promptly.</summary>
    private static StubRedirectWaiter Arrives(int afterWaits = 0) => new(Redirect, afterWaits);

    /// <summary>A wall nobody passes: the callback never comes.</summary>
    private static StubRedirectWaiter Never() => new(redirect: null, afterWaits: 0);

    /// <summary>
    /// Nothing on it. The streamed login looks for no selector at all, and a
    /// page offering none is how that is asserted rather than assumed.
    /// </summary>
    private static StubLoginPage Page() => StubLoginPage.Showing();

    // ---- the shape of the thing --------------------------------------------

    [Fact]
    public async Task The_streamed_login_opens_ahs_own_page_and_finishes_on_the_redirect_alone()
    {
        var page = Page();
        using var ctx = new FakeJobContext { AnswersNothing = true };

        var code = await Adapter().LiveCodeAsync(ctx, page, Arrives(), CancellationToken.None);

        // The code, not the URL: the callback carries a live credential and is
        // never handed on whole.
        Assert.Equal(Code, code);

        // AH's own authorize endpoint, once. Nowhere else was navigated to.
        Assert.Equal(Adapter().AuthorizeUrl(), Assert.Single(page.Visited));

        // OpeningProvider belongs to the caller that leases the browser; from
        // the seam inwards the trail is the wait and then the exchange.
        Assert.Equal([JobStep.AwaitingHuman, JobStep.Authenticating], ctx.Steps);
    }

    [Fact]
    public async Task The_streamed_login_types_nothing_even_when_credentials_were_handed_to_it()
    {
        var page = Page();

        // A consumer that sent them anyway. The manifest declares no fields
        // and the validator refuses one that does, so this cannot arrive
        // through the control plane - but the promise is that the path does
        // not use a password, not that nobody ever supplies one.
        using var ctx = new FakeJobContext
        {
            AnswersNothing = true,
            Inputs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["username"] = "shopper@example.com",
                ["password"] = "hunter2",
            },
        };

        var code = await Adapter().LiveCodeAsync(ctx, page, Arrives(), CancellationToken.None);

        Assert.Equal(Code, code);

        // Nothing was typed, clicked or looked for. The human signs in on the
        // page in front of them; this adapter only watches for the redirect.
        Assert.Empty(page.Filled);
        Assert.Empty(page.Clicked);
        Assert.Empty(page.Calls);

        // No password ever entered the DOM, so there is nothing to clear out
        // of it - the refusal the redactor exists for cannot fire here at all.
        Assert.True(page.HoldsSecret);
        Assert.DoesNotContain("clear-secrets", page.Calls);

        // Deliberately NOT latched. The latch exists so a lost lease does not
        // requeue a login whose password already counted against an account,
        // and the attempts here are the human's own on AH's own page. Latching
        // would fail jobs permanently to protect against something that cannot
        // happen.
        Assert.False(ctx.CredentialWasSubmitted);
    }

    [Fact]
    public async Task The_human_is_asked_once_for_a_passive_live_view_and_never_for_a_string()
    {
        var page = Page();
        using var ctx = new FakeJobContext { AnswersNothing = true };

        await Adapter().LiveCodeAsync(ctx, page, Arrives(afterWaits: 2), CancellationToken.None);

        // Once, not once per poll: three passes over the loop and still a
        // single ask.
        var challenge = Assert.Single(ctx.Asked);

        Assert.Equal(ChallengeType.LiveView, challenge.Type);
        Assert.Equal("connect.challenge.live_login", challenge.PromptKey);

        // Passive in the same sense as an app approval: there is no string to
        // type back, and what ends it is the login completing.
        Assert.True(challenge.IsPassive);
        Assert.Equal(ChallengeAnswerKind.Text, challenge.AnswerKind);

        // Not a picture and not a paste. Those are the other two ways this
        // adapter can ask, and confusing them puts the wrong UI in front of
        // somebody mid-login.
        Assert.Null(challenge.Image);
        Assert.Null(challenge.Url);
        Assert.Null(challenge.ReturnPattern);
        Assert.NotEqual(ChallengeType.Image, challenge.Type);
        Assert.NotEqual(ChallengeType.Redirect, challenge.Type);
    }

    [Theory]
    [InlineData(900)]
    [InlineData(240)]
    public async Task The_window_the_human_gets_is_the_configured_one(int seconds)
    {
        var page = Page();
        using var ctx = new FakeJobContext { AnswersNothing = true };

        await Adapter(new AlbertHeijnOptions { LiveLoginSeconds = seconds })
            .LiveCodeAsync(ctx, page, Arrives(), CancellationToken.None);

        // The option reaches the expiry the consumer renders a clock from. A
        // challenge holds a live browser hostage, so this is the bound that
        // decides when an agent is released.
        Assert.Equal(Now.AddSeconds(seconds), Assert.Single(ctx.Asked).ExpiresAt);
    }

    [Fact]
    public void The_shipped_window_is_fifteen_minutes_and_the_shipped_login_is_the_streamed_one()
    {
        var options = new AlbertHeijnOptions();

        // Generous on purpose: it has to cover reading an SMS, finding a
        // password manager, and a captcha with several rounds in it.
        Assert.Equal(900, options.LiveLoginSeconds);

        // The default this whole file is about. If this ever flips, the typed
        // form becomes production again and its suite becomes the live one.
        Assert.True(options.LiveLogin);
    }

    // ---- letting go ---------------------------------------------------------

    [Fact]
    public async Task The_view_is_ended_when_the_redirect_lands_and_not_merely_abandoned()
    {
        var page = Page();
        using var ctx = new FakeJobContext { AnswersNothing = true };

        await Adapter().LiveCodeAsync(ctx, page, Arrives(), CancellationToken.None);

        // The sign-in is done, so the view is over. Without this the shutter
        // keeps photographing for the rest of the window, the agent stays
        // occupied, and the human sits looking at a frozen picture of a step
        // they already finished.
        Assert.True(Assert.Single(ctx.AskTokens).IsCancellationRequested);
    }

    [Fact]
    public async Task The_view_is_ended_when_the_window_runs_out_too()
    {
        var page = Page();
        using var ctx = new FakeJobContext { AnswersNothing = true };
        var watcher = Never();

        await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter(new AlbertHeijnOptions { LiveLoginSeconds = 0 })
                .LiveCodeAsync(ctx, page, watcher, CancellationToken.None));

        // The case where somebody IS still looking at the picture. Disposing
        // the linked source merely unlinks it from the job's token, which
        // orphans the ask instead of ending it.
        Assert.True(Assert.Single(ctx.AskTokens).IsCancellationRequested);

        // A window of zero is spent before it is polled: the deadline is
        // checked ahead of the wait, so nothing is asked of the provider.
        Assert.Equal(0, watcher.Waits);
    }

    [Fact]
    public async Task A_human_who_closes_the_view_ends_the_login_rather_than_polling_out_the_window()
    {
        var page = Page();

        // The answer to a passive challenge is the empty string, and it
        // arriving at all means the consumer's UI is done with this view.
        using var ctx = new FakeJobContext { Answer = _ => string.Empty };
        var watcher = Never();

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().LiveCodeAsync(ctx, page, watcher, CancellationToken.None));

        Assert.Equal(ErrorCode.BlockedByProvider, error.Code);

        // One pass and out. Without the completed-ask guard this spins for the
        // full fifteen minutes with nobody at the other end of it.
        Assert.Equal(1, watcher.Waits);
    }

    // ---- what the ending is called -----------------------------------------

    [Fact]
    public async Task A_login_that_never_reached_the_callback_is_blocked_and_never_a_wrong_password()
    {
        var page = Page();
        using var ctx = new FakeJobContext { AnswersNothing = true };

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter(new AlbertHeijnOptions { LiveLoginSeconds = 0 })
                .LiveCodeAsync(ctx, page, Never(), CancellationToken.None));

        Assert.Equal(ErrorCode.BlockedByProvider, error.Code);

        // Nobody typed a password into anything of ours, so calling this a
        // credential failure would send the user to reset a password that was
        // fine - and a credential error is never retried, so the mistake would
        // be permanent for that session too.
        Assert.NotEqual(ErrorCode.InvalidCredentials, error.Code);
        Assert.Equal(
            "ah: the live login ended without reaching the callback; the sign-in was not completed",
            error.Detail);
    }

    [Fact]
    public async Task A_callback_carrying_no_code_is_a_shape_change_and_still_ends_the_view()
    {
        var page = Page();
        using var ctx = new FakeJobContext { AnswersNothing = true };

        // The redirect arrived, so the sign-in finished - but the one thing
        // the whole login is for is not on it.
        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().LiveCodeAsync(
                ctx, page, new StubRedirectWaiter("appie://login-exit?state=xyz", afterWaits: 0),
                CancellationToken.None));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Equal("ah: the callback carried no authorization code", error.Detail);

        // The sign-in is over either way, so the view goes with it.
        Assert.True(Assert.Single(ctx.AskTokens).IsCancellationRequested);
    }

    [Fact]
    public async Task The_redirect_finishes_the_login_even_though_nobody_ever_answers_the_view()
    {
        var page = Page();

        // The case a passive challenge exists for: the human acts where we
        // cannot observe - on the streamed page itself - and has no reason to
        // come back to the consumer's UI and click anything.
        using var ctx = new FakeJobContext { AnswersNothing = true };
        var watcher = Arrives(afterWaits: 2);

        var code = await Adapter().LiveCodeAsync(ctx, page, watcher, CancellationToken.None);

        Assert.Equal(Code, code);
        Assert.Equal(3, watcher.Waits);
    }

    [Fact]
    public async Task The_view_is_asked_under_the_jobs_own_token_so_a_cancelled_job_ends_it()
    {
        var page = Page();
        using var ctx = new FakeJobContext { AnswersNothing = true };
        var watcher = Never();

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Adapter().LiveCodeAsync(ctx, page, watcher, cts.Token));

        // Linked, not independent: a job that is cancelled has to be able to
        // END the ask rather than leave it holding a browser until it expires.
        Assert.Equal(0, watcher.Waits);
    }
}
