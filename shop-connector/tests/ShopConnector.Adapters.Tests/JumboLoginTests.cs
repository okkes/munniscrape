using Connector.Kit.Challenges;
using Connector.Kit.Errors;
using ShopConnector.Adapters.Jumbo;
using ShopConnector.Adapters.Tests.Support;
using Xunit;

namespace ShopConnector.Adapters.Tests;

/// <summary>
/// The Jumbo login, driven offline through the page seam.
///
/// The login moved and the adapter did not follow it: <c>jumbo.com/inloggen</c>
/// is a 404, and the live chain is <c>/account/inloggen</c> to
/// <c>/api/auth/login</c> to Auth0 Universal Login on a different host
/// entirely. That last part is why the old "am I still on a page whose URL
/// says inloggen?" test could never say yes.
///
/// Everything below decides how a human is treated - whether a wall is
/// relayed or refused, whether silence is called a wrong password, and
/// whether a credential that already went upstream can be sent twice - and
/// none of it was reachable without a live Chromium and real credentials.
/// </summary>
public sealed class JumboLoginTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 26, 9, 0, 0, TimeSpan.Zero);

    private const string Signed = "https://www.jumbo.com/mijn-jumbo/bestellingen";

    private static JumboAdapter Adapter(JumboOptions? options = null) =>
        new(options ?? new JumboOptions(), new FakeJumboGraphQl(), new FixedTimeProvider(Now));

    /// <param name="answersNothing">
    /// For the paths that now end in a streamed page. A live view is passive -
    /// there is no string to type back - so the human acts where we cannot
    /// observe and never returns an answer.
    /// </param>
    private static FakeJobContext Context(
        StubLoginPage page, string? storageState = null, bool attended = false, bool answersNothing = false) =>
        new()
        {
            Inputs = JumboFixtures.Credentials,
            Browser = new JumboBrowserLease(page, storageState),
            Attended = attended,
            AnswersNothing = answersNothing,
        };

    private static StubLoginPage FormPage(params string[] extra) =>
        StubLoginPage.Showing([.. new[] { "input#username", "input#password", "button[type='submit']" }, .. extra]);

    // ---- the login chain, corrected ---------------------------------------

    /// <summary>
    /// CONFIRMED-live 2026-07-28. The address the adapter used to open returns
    /// 404, and the page it ends on is not on jumbo.com at all - so a marker
    /// of "inloggen" could never match it.
    /// </summary>
    [Fact]
    public void The_login_starts_at_the_address_that_still_exists()
    {
        var options = new JumboOptions();

        Assert.Equal("https://www.jumbo.com/account/inloggen", options.LoginUrl);
        Assert.Contains("auth.jumbo.com", options.LoginUrlMarkers);
        Assert.Contains("/u/login", options.LoginUrlMarkers);
    }

    [Theory]
    [InlineData("https://www.jumbo.com/account/inloggen", false)]
    [InlineData("https://www.jumbo.com/api/auth/login?sourceUrl=/", false)]
    [InlineData("https://auth.jumbo.com/authorize?client_id=rWRgjhmeqWGeMLyJwf2Zf863o18XiDk0", false)]
    [InlineData("https://auth.jumbo.com/u/login?state=abc", false)]
    [InlineData("about:blank", false)]
    [InlineData("https://www.jumbo.com/", true)]
    [InlineData("https://www.jumbo.com/mijn-jumbo/bestellingen", true)]
    public void A_session_exists_only_back_on_the_shop_and_off_every_login_marker(string url, bool signedIn)
    {
        // Both halves matter: auth.jumbo.com ends in the shop's own domain but
        // is the login, and about:blank is on neither.
        Assert.Equal(signedIn, JumboReturnWatcher.IsSignedIn(url, new JumboOptions()));
    }

    [Fact]
    public async Task A_login_navigates_to_the_login_url_types_both_credentials_and_seals_the_cookies()
    {
        var page = FormPage();
        using var ctx = Context(page);

        var result = await Adapter().LoginAsync(
            ctx, page, new StubRedirectWaiter(Signed, afterWaits: 0), CancellationToken.None);

        Assert.Equal("https://www.jumbo.com/account/inloggen", Assert.Single(page.Visited));
        Assert.Equal("shopper@example.test", page.Filled["input#username"]);
        Assert.Equal("correct horse battery staple", page.Filled["input#password"]);
        Assert.Contains("button[type='submit']", page.Clicked);

        Assert.NotNull(result.Material.StorageState);
        Assert.False(string.IsNullOrWhiteSpace(result.Material.DeviceId));

        // Stated from this login rather than left to the manifest, so the
        // consumer's countdown starts now.
        Assert.Equal(Now.AddSeconds(86_400), result.ExpiresAt);
    }

    /// <summary>
    /// The latch is what stops a lost lease from requeuing a login that may
    /// already have counted against the account.
    /// </summary>
    [Fact]
    public async Task The_credential_latch_is_set_before_anything_is_submitted()
    {
        var page = FormPage();
        using var ctx = Context(page);

        await Adapter().LoginAsync(ctx, page, new StubRedirectWaiter(Signed, afterWaits: 0), CancellationToken.None);

        Assert.True(ctx.CredentialWasSubmitted);
    }

    /// <summary>
    /// A box that is not where it was is no longer a dead end. The human on a
    /// streamed page can see what this could not, so a page the adapter cannot
    /// drive is handed over rather than failed - and nothing was typed or
    /// clicked on the way, so nothing has counted against the account.
    /// </summary>
    [Fact]
    public async Task A_missing_username_box_hands_the_page_over_rather_than_failing()
    {
        var page = StubLoginPage.Showing("button[type='submit']");
        using var ctx = Context(page, answersNothing: true);

        var result = await Adapter().LoginAsync(ctx, page, new StubRedirectWaiter(Signed, 0), CancellationToken.None);

        Assert.Equal(ChallengeType.LiveView, Assert.Single(ctx.Asked).Type);
        Assert.False(ctx.CredentialWasSubmitted);
        Assert.NotNull(result.Material.StorageState);
    }

    /// <summary>
    /// Auth0 can ask for the identifier and the password on two screens. When
    /// the password box never appears on either, both layouts have been tried
    /// and this is a real shape change - but the identifier has already been
    /// submitted to get to the second screen, so the latch must already be
    /// down.
    /// </summary>
    [Fact]
    public async Task A_password_box_on_neither_screen_hands_over_with_the_latch_already_down()
    {
        var page = StubLoginPage.Showing("input#username", "button[type='submit']");
        using var ctx = Context(page, answersNothing: true);

        var result = await Adapter().LoginAsync(ctx, page, new StubRedirectWaiter(Signed, 0), CancellationToken.None);

        Assert.Equal(ChallengeType.LiveView, Assert.Single(ctx.Asked).Type);

        // The identifier was already submitted to reach the second screen, so
        // the account has been touched and the latch must stay down whatever
        // happens next.
        Assert.True(ctx.CredentialWasSubmitted);
        Assert.NotNull(result.Material.StorageState);
    }

    /// <summary>
    /// Both credentials are optional now, so a login with neither is a first
    /// connect rather than a bad request: the page is simply streamed to the
    /// person who has one.
    /// </summary>
    [Fact]
    public async Task A_login_with_no_credentials_streams_the_page_instead_of_refusing()
    {
        var page = FormPage();
        using var ctx = new FakeJobContext
        {
            AnswersNothing = true,
            Browser = new JumboBrowserLease(page),
        };

        var result = await Adapter().LoginAsync(ctx, page, new StubRedirectWaiter(Signed, 0), CancellationToken.None);

        Assert.Equal(ChallengeType.LiveView, Assert.Single(ctx.Asked).Type);
        Assert.Empty(page.Filled);
        Assert.NotNull(result.Material.StorageState);
    }

    // ---- what the page answers with ---------------------------------------

    [Fact]
    public async Task A_stated_credential_error_is_the_only_path_to_invalid_credentials()
    {
        var page = FormPage(":text('Verkeerde e-mail of wachtwoord')");
        using var ctx = Context(page);

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().LoginAsync(ctx, page, new StubRedirectWaiter(null, 99), CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidCredentials, error.Code);
    }

    /// <summary>
    /// Guessing "wrong password" from silence sends the user to reset a
    /// password that was fine, and a credential error is never retried - so
    /// the mistake would be permanent for that session too.
    /// </summary>
    /// <summary>
    /// Silence is still never called a wrong password - it is now handed to
    /// the human instead, which is the one move that can actually resolve it.
    /// Guessing "wrong password" would send them to reset one that was fine,
    /// and a credential error is never retried.
    /// </summary>
    [Fact]
    public async Task Silence_hands_the_page_over_and_is_never_a_wrong_password()
    {
        var page = FormPage();
        using var ctx = Context(page, answersNothing: true);

        var result = await Adapter(new JumboOptions { LoginSettleSeconds = 0 })
            .LoginAsync(ctx, page, new StubRedirectWaiter(Signed, 1), CancellationToken.None);

        Assert.Equal(ChallengeType.LiveView, Assert.Single(ctx.Asked).Type);
        Assert.NotNull(result.Material.StorageState);
    }

    [Fact]
    public async Task A_login_that_leaves_no_jumbo_cookies_is_a_shape_change()
    {
        var page = FormPage();
        using var ctx = Context(page, storageState: """{"cookies":[],"origins":[]}""");

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().LoginAsync(ctx, page, new StubRedirectWaiter(Signed, 0), CancellationToken.None));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains("no jumbo.com cookies", error.Detail, StringComparison.Ordinal);
    }

    // ---- the wall ----------------------------------------------------------

    /// <summary>
    /// Auth0 Universal Login ships hCaptcha, reCAPTCHA and Turnstile and
    /// activates one on risk score. None of them can be relayed: a widget
    /// wants drags and tile clicks and mints its token inside its own
    /// JavaScript, so a photograph and a typed answer cannot pass one however
    /// faithfully the page is captured. On a pooled agent there is nobody to
    /// ask, and saying so at once is worth far more than a hang.
    /// </summary>
    [Fact]
    public async Task An_interactive_widget_hands_the_page_to_the_human_who_can_pass_it()
    {
        var page = FormPage("iframe[src*='hcaptcha']");
        using var ctx = Context(page, answersNothing: true);

        var result = await Adapter().LoginAsync(ctx, page, new StubRedirectWaiter(Signed, 0), CancellationToken.None);

        Assert.Equal(ChallengeType.LiveView, Assert.Single(ctx.Asked).Type);
        Assert.NotNull(result.Material.StorageState);

        // The password never went into the box, because the wall was seen
        // first - and the redactor refuses to photograph a page holding one,
        // so a filled box would have relayed a live view of nothing.
        Assert.DoesNotContain("input#password", page.Filled.Keys);
        Assert.False(page.HoldsSecret);
    }

    [Fact]
    public async Task A_picture_captcha_is_relayed_only_after_the_password_leaves_the_dom()
    {
        var page = FormPage("img[alt*='captcha' i]", "input[name='captcha']");

        using var ctx = new FakeJobContext
        {
            Inputs = JumboFixtures.Credentials,
            Browser = new JumboBrowserLease(page),
            Answer = _ => "8421",
        };

        var result = await Adapter().LoginAsync(
            ctx, page, new StubRedirectWaiter(Signed, afterWaits: 1), CancellationToken.None);

        var challenge = Assert.Single(ctx.Asked);
        Assert.Equal(ChallengeType.Image, challenge.Type);
        Assert.NotNull(challenge.Image);
        Assert.Equal("8421", page.Answered);

        // The redactor produces no bytes at all while a secret is on the page,
        // so a picture that reached the human proves the clearing happened
        // first.
        var calls = page.Calls.ToList();
        var clear = calls.IndexOf("clear-secrets");
        var shot = calls.IndexOf("screenshot");
        Assert.True(clear >= 0 && clear < shot, "the password must leave the DOM before anything photographs it");

        Assert.NotNull(result.Material.StorageState);
    }

    /// <summary>
    /// The one-time code is on the account owner's phone or in their mailbox.
    /// It is relayed, never guessed, and the prompt deliberately does not
    /// claim which way it arrived.
    /// </summary>
    [Fact]
    public async Task A_one_time_code_is_relayed_to_the_human()
    {
        var page = FormPage("input[name='code']");

        using var ctx = new FakeJobContext
        {
            Inputs = JumboFixtures.Credentials,
            Browser = new JumboBrowserLease(page),
            Answer = _ => "123456",
        };

        await Adapter().LoginAsync(ctx, page, new StubRedirectWaiter(Signed, afterWaits: 1), CancellationToken.None);

        var challenge = Assert.Single(ctx.Asked);
        Assert.Equal(ChallengeType.MfaCode, challenge.Type);
        Assert.Equal("connect.challenge.verification_code", challenge.PromptKey);

        // Not "the code we texted you": Auth0 sends it the way the account is
        // configured, and telling somebody to watch a phone that will never
        // ring is a failure they cannot recover from.
        Assert.Null(challenge.Delivery);

        Assert.Equal("123456", page.Answered);
    }
}
