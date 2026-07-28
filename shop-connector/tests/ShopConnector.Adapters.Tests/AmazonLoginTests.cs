using Connector.Kit.Challenges;
using Connector.Kit.Errors;
using ShopConnector.Adapters.Amazon;
using ShopConnector.Adapters.Tests.Support;
using Xunit;

namespace ShopConnector.Adapters.Tests;

/// <summary>
/// Amazon's sign-in chain, offline.
///
/// Every branch here decides how a human is treated - whether a wall is
/// relayed or refused, whether silence is called a wrong password, whether a
/// credential that already went upstream can be sent again - and none of them
/// is reachable without a live Chromium and a real account. On this platform a
/// real Amazon login attempt is something that can be spent about once, so the
/// decisions live behind a seam and are asserted here instead.
/// </summary>
public sealed class AmazonLoginTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 28, 2, 0, 0, TimeSpan.Zero);

    private static readonly AmazonOptions Options = new();

    private const string Email = "input#ap_email";
    private const string Password = "input#ap_password";
    private const string Continue = "input#continue";
    private const string Submit = "input#signInSubmit";
    private const string Otp = "input#auth-mfa-otpcode";
    private const string OtpSubmit = "input#auth-signin-button";

    private static AmazonAdapter Adapter(AmazonOptions? options = null) =>
        new(options ?? Options, new FixedTimeProvider(Now));

    private static FakeJobContext Context(AmazonStubPage page, bool attended = false) => new()
    {
        Inputs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["username"] = "j.devries@example.nl",
            ["password"] = "correct horse battery staple",
        },
        Browser = new AmazonStubBrowser(page),
        Attended = attended,
    };

    /// <summary>
    /// The waiter production actually passes: amazon.nl has no callback the
    /// browser cannot follow, so nothing ever arrives here and the page itself
    /// is what says whether the sign-in worked. Every test below therefore
    /// drives success the way a live run does - the form goes away and the URL
    /// becomes the order list - rather than through a signal this provider
    /// does not have.
    /// </summary>
    private static StubRedirectWaiter Never() => new(null, int.MaxValue);

    // ---- the entry point ---------------------------------------------------

    [Fact]
    public async Task The_sign_in_starts_at_the_order_list_and_never_at_a_hand_built_openid_url()
    {
        // The reference hardcodes openid.assoc_handle=usflex - a US value its
        // own docstring admits is not adjusted for other domains - and has no
        // 'nl' entry in either its region-language or region-currency table.
        // Navigating to the order list instead lets amazon.nl build its own
        // sign-in chain with its own parameters.
        var page = AmazonStubPage.Showing(Email, Password, Submit).SignedInAfter(Submit);
        using var ctx = Context(page);

        await Adapter().LoginAsync(ctx, page, Never(), CancellationToken.None);

        var first = Assert.Single(page.Visited);
        Assert.StartsWith("https://www.amazon.nl/your-orders/orders", first, StringComparison.Ordinal);
        Assert.DoesNotContain("assoc_handle", first, StringComparison.Ordinal);
        Assert.DoesNotContain("openid", first, StringComparison.Ordinal);
    }

    // ---- the two screens ---------------------------------------------------

    [Fact]
    public async Task The_password_screen_is_reached_by_advancing_the_wizard()
    {
        // Amazon asks for the address and the password on two screens. Probed
        // rather than assumed, so a single-screen form still works and neither
        // layout needs a release.
        var page = AmazonStubPage
            .Showing(Email, Continue)
            .RevealingAfter(Continue, Password, Submit)
            .SignedInAfter(Submit);

        using var ctx = Context(page);

        await Adapter().LoginAsync(ctx, page, Never(), CancellationToken.None);

        Assert.Equal("j.devries@example.nl", page.Filled[Email]);
        Assert.Equal("correct horse battery staple", page.Filled[Password]);

        string[] wizard = [Continue, Submit];
        Assert.Equal(wizard, page.Clicked);
        Assert.True(ctx.CredentialWasSubmitted);
    }

    [Fact]
    public async Task A_single_screen_form_is_filled_without_advancing_anything()
    {
        var page = AmazonStubPage.Showing(Email, Password, Submit).SignedInAfter(Submit);
        using var ctx = Context(page);

        await Adapter().LoginAsync(ctx, page, Never(), CancellationToken.None);

        // The continue button is never touched: on a one-screen form clicking
        // it would submit the wrong thing.
        string[] once = [Submit];
        Assert.Equal(once, page.Clicked);
        Assert.True(ctx.CredentialWasSubmitted);
    }

    [Fact]
    public async Task Advancing_the_wizard_latches_the_credential_before_the_click_not_after()
    {
        // The button is gone, so the click fails - and the e-mail address had
        // already been typed for it. The latch has to have happened BEFORE the
        // click, because a lease lost at exactly this point must fail the job
        // rather than requeue a login that may already have reached Amazon.
        var page = AmazonStubPage.Showing(Email);
        using var ctx = Context(page);

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().LoginAsync(ctx, page, Never(), CancellationToken.None));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains("continue button", error.Detail, StringComparison.Ordinal);
        Assert.True(ctx.CredentialWasSubmitted, "the identifier had already gone upstream");
    }

    [Fact]
    public async Task A_missing_email_field_is_a_shape_change_and_costs_no_login_attempt()
    {
        var page = AmazonStubPage.Showing(Submit);
        using var ctx = Context(page);

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().LoginAsync(ctx, page, Never(), CancellationToken.None));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains("e-mail field", error.Detail, StringComparison.Ordinal);
        Assert.Contains("ap_email", error.Detail, StringComparison.Ordinal);

        // Nothing was typed anywhere, so nothing was spent.
        Assert.False(ctx.CredentialWasSubmitted);
        Assert.Empty(page.Filled);
    }

    [Fact]
    public async Task A_session_that_is_already_signed_in_costs_no_credential_at_all()
    {
        // The lease restored a session that still works. Typing a password at
        // a page that never asked for one is a login attempt spent for nothing.
        var page = AmazonStubPage.Showing();
        page.Url = "https://www.amazon.nl/your-orders/orders?timeFilter=year-2026&startIndex=0";

        using var ctx = Context(page);

        await Adapter().LoginAsync(ctx, page, Never(), CancellationToken.None);

        Assert.Empty(page.Filled);
        Assert.False(ctx.CredentialWasSubmitted, "this job is still safe to requeue");
    }

    // ---- the one-time code -------------------------------------------------

    [Fact]
    public async Task The_one_time_code_is_relayed_to_the_human_and_never_solved()
    {
        // The code screen appears only after the password is submitted, and
        // the order list only after the code is.
        var page = AmazonStubPage
            .Showing(Email, Password, Submit, ":text('tekstbericht')")
            .RevealingAfter(Submit, Otp, OtpSubmit)
            .SignedInAfter(OtpSubmit);

        using var ctx = new FakeJobContext
        {
            Inputs = Credentials(),
            Browser = new AmazonStubBrowser(page),
            Answer = challenge => challenge.Type == ChallengeType.MfaCode
                ? "314159"
                : throw new InvalidOperationException($"unexpected challenge {challenge.Type}"),
        };

        await Adapter().LoginAsync(ctx, page, Never(), CancellationToken.None);

        var asked = Assert.Single(ctx.Asked);
        Assert.Equal(ChallengeType.MfaCode, asked.Type);
        Assert.Equal(6, asked.Length);
        Assert.Equal("sms", asked.Delivery);
        Assert.StartsWith("connect.", asked.PromptKey, StringComparison.Ordinal);

        // Typed into Amazon's own box; nothing was computed from a stored
        // shared secret, because no such field exists on this provider.
        Assert.Equal("314159", page.Filled[Otp]);
        Assert.Contains(OtpSubmit, page.Clicked);
    }

    [Fact]
    public async Task A_code_whose_delivery_the_page_does_not_state_is_left_generic()
    {
        // A prompt that says "the code we texted you" to somebody whose code is
        // in an authenticator app sends them to stare at a phone that will
        // never buzz, and the challenge expires while they wait. Worse copy,
        // better outcome.
        var page = AmazonStubPage
            .Showing(Email, Password, Submit)
            .RevealingAfter(Submit, Otp, OtpSubmit)
            .SignedInAfter(OtpSubmit);

        using var ctx = new FakeJobContext
        {
            Inputs = Credentials(),
            Browser = new AmazonStubBrowser(page),
            Answer = _ => "271828",
        };

        await Adapter().LoginAsync(ctx, page, Never(), CancellationToken.None);

        Assert.Null(Assert.Single(ctx.Asked).Delivery);
    }

    // ---- verdicts ----------------------------------------------------------

    [Fact]
    public async Task Only_a_stated_credential_error_ever_reports_a_wrong_password()
    {
        var page = AmazonStubPage.Showing(Email, Password, Submit, "#auth-password-invalid-password-alert");
        using var ctx = Context(page);

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().LoginAsync(ctx, page, Never(), CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidCredentials, error.Code);
    }

    [Fact]
    public async Task A_locked_account_is_a_refusal_and_not_a_wrong_password()
    {
        // Both alerts are on the page, which is what Amazon actually renders
        // when it locks an account after a sign-in attempt. Reporting a
        // credential error here sends somebody to change a password that is
        // fine and will not help - and nothing retries a credential error, so
        // the mistake is permanent for that session too.
        var page = AmazonStubPage.Showing(
            Email, Password, Submit, "#auth-account-locked-alert", "#auth-password-invalid-password-alert");

        using var ctx = Context(page);

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().LoginAsync(ctx, page, Never(), CancellationToken.None));

        Assert.Equal(ErrorCode.BlockedByProvider, error.Code);
        Assert.NotEqual(ErrorCode.InvalidCredentials, error.Code);
    }

    [Fact]
    public async Task Silence_after_the_submit_is_a_shape_change_and_never_a_guess_at_the_password()
    {
        // No order list, no stated error, no wall: the sign-in simply did not
        // resolve. Guessing "wrong password" from silence is the mistake this
        // branch exists to refuse.
        //
        // The settle budget is zeroed because the suite's clock does not move:
        // the loop ends on a deadline, which is right against a real
        // TimeProvider and never reached against a frozen one.
        var page = AmazonStubPage.Showing(Email, Password, Submit);
        using var ctx = Context(page);

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter(Options with { LoginSettleSeconds = 0 })
                .LoginAsync(ctx, page, Never(), CancellationToken.None));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.NotEqual(ErrorCode.InvalidCredentials, error.Code);
        Assert.Contains("neither reached the order list", error.Detail, StringComparison.Ordinal);
    }

    // ---- walls -------------------------------------------------------------

    [Fact]
    public async Task An_unattended_agent_refuses_the_acic_widget_and_asks_nobody()
    {
        // AWS WAF and ACIC are widgets: they want drags and tile clicks and
        // mint their token inside their own JavaScript, so no screenshot and no
        // typed answer can pass one however faithfully the page is
        // photographed. The reference's answer is three paid solving services.
        // Ours is to say so and stop.
        var page = AmazonStubPage.Showing(Email, Password, Submit, "#aa-challenge-page-captcha-container");
        using var ctx = Context(page, attended: false);

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().LoginAsync(ctx, page, Never(), CancellationToken.None));

        Assert.Equal(ErrorCode.BlockedByProvider, error.Code);
        Assert.Empty(ctx.Asked);

        // And the password left the DOM before anything could photograph it.
        Assert.False(page.HoldsSecret);
        Assert.Contains("clear-secrets", page.Calls);
    }

    [Fact]
    public async Task An_attended_browser_is_asked_to_pass_the_widget_itself()
    {
        var page = AmazonStubPage.Showing(Email, Password, Submit, "#aa-challenge-page-captcha-container");

        using var ctx = new FakeJobContext
        {
            Inputs = Credentials(),
            Browser = new AmazonStubBrowser(page),
            Attended = true,
            // The human passes the puzzle in the window in front of them and
            // Amazon carries on. There is nothing for them to type back at us:
            // a widget hands its token to the provider, not to the agent, so
            // the page changing is the only observation there is.
            Answer = _ =>
            {
                page.SignIn();
                return string.Empty;
            },
        };

        await Adapter().LoginAsync(ctx, page, Never(), CancellationToken.None);

        var asked = Assert.Single(ctx.Asked);
        Assert.Equal(ChallengeType.AppApproval, asked.Type);
        Assert.True(asked.IsPassive);
    }

    [Fact]
    public async Task An_image_captcha_is_relayed_only_after_the_password_has_left_the_page()
    {
        // The one wall a relay can carry: a picture and a box. The redactor
        // refuses to photograph a page while a secret-declared field still
        // holds content, so a picture coming back at all is proof the
        // credentials were cleared first.
        var page = AmazonStubPage
            .Showing(Email, Password, Submit, "#auth-captcha-image", "input#auth-captcha-guess")
            .SignedInAfterAnswer();

        var browser = new AmazonStubBrowser(page);
        using var ctx = new FakeJobContext
        {
            Inputs = Credentials(),
            Browser = browser,
            Answer = challenge => challenge.Type == ChallengeType.Image
                ? "MKPHTX"
                : throw new InvalidOperationException($"unexpected challenge {challenge.Type}"),
        };

        await Adapter().LoginAsync(ctx, page, Never(), CancellationToken.None);

        var asked = Assert.Single(ctx.Asked);
        Assert.Equal(ChallengeType.Image, asked.Type);
        Assert.NotNull(asked.Image);
        Assert.NotEmpty(asked.Image);

        Assert.Equal(1, browser.Captures);
        Assert.False(page.HoldsSecret);

        // Only the captcha is answered. The credentials are deliberately not
        // resubmitted to make the form look complete: a second submission of a
        // password that may already have counted is how an account gets locked.
        Assert.Equal("MKPHTX", page.Answered);
    }

    // ---- inputs ------------------------------------------------------------

    [Theory]
    [InlineData("username")]
    [InlineData("password")]
    public async Task A_missing_credential_is_refused_before_a_browser_is_ever_leased(string missing)
    {
        var inputs = Credentials();
        inputs.Remove(missing);

        var page = AmazonStubPage.Showing(Email, Password, Submit);
        using var ctx = new FakeJobContext { Inputs = inputs, Browser = new AmazonStubBrowser(page) };

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().LoginAsync(ctx, page, Never(), CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidRequest, error.Code);
        Assert.False(ctx.CredentialWasSubmitted);
        Assert.Empty(page.Visited);
    }

    private static Dictionary<string, string> Credentials() => new(StringComparer.Ordinal)
    {
        ["username"] = "j.devries@example.nl",
        ["password"] = "correct horse battery staple",
    };
}
