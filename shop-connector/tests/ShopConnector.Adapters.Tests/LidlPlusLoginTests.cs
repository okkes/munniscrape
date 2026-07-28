using System.Net;
using System.Security.Cryptography;
using System.Text;
using Connector.Kit.Challenges;
using Connector.Kit.Errors;
using Connector.Kit.Jobs;
using ShopConnector.Adapters.LidlPlus;
using ShopConnector.Adapters.Support;
using ShopConnector.Adapters.Tests.Support;
using Xunit;

namespace ShopConnector.Adapters.Tests;

/// <summary>
/// The Lidl Plus login, driven offline through the page seam.
///
/// Three defects here each failed every real login on their own, and this
/// suite exists because the only other way to find that out is to spend a
/// live attempt - which on this platform is a thing that happens once, is
/// never retried, and locks an account when it goes wrong.
///
/// 1. No PKCE at all. The reference builds a code_challenge for the authorize
///    call and replays the verifier at the exchange; we sent neither, and an
///    authorization server that recorded a challenge refuses the exchange.
/// 2. An empty client secret, so the Basic header named a client Lidl does
///    not have.
/// 3. No way to sign in with an e-mail address. The README says "username
///    (phone number)"; the source shows the page has input-email beside
///    input-phone, and the owner of this repo signs in with the former.
///
/// No browser, no network, no credentials.
/// </summary>
public sealed class LidlPlusLoginTests
{
    private const string Redirect = "com.lidlplus.app://callback?code=lidl-authorization-code&state=x";
    private const string TokenPath = "/connect/token";

    private const string Email = "shopper@example.com";
    private const string Phone = "+31612345678";

    /// <summary>The six digits the human reads off their phone or out of their inbox.</summary>
    private const string Code = "590287";

    private static readonly DateTimeOffset Now = new(2026, 7, 26, 9, 0, 0, TimeSpan.Zero);

    private static readonly LidlPlusOptions Options = new();

    /// <summary>The reference's own four names, which is what the page really has.</summary>
    private static readonly string EmailInput = Options.EmailSelectors[0];
    private static readonly string PhoneInput = Options.PhoneSelectors[0];
    private static readonly string PasswordInput = Options.PasswordSelectors[0];
    private static readonly string Submit = Options.SubmitSelectors[0];

    /// <summary>
    /// The password screen's own button. Separate from <see cref="Submit"/>
    /// because the live page has two, and a stub with one cannot catch an
    /// adapter that clicks the wrong one.
    /// </summary>
    private static readonly string PasswordSubmit = Options.PasswordSubmitSelectors[0];
    private static readonly string CodeInput = Options.VerificationCodeSelectors[0];

    /// <summary>
    /// The code screen's submit. Distinct from both the identifier and the
    /// password buttons: on the live page all three used to collapse onto
    /// one selector, which is exactly why a stub could not tell them apart.
    /// </summary>
    private static readonly string CodeSubmit = Options.VerificationSubmitSelectors[0];

    private static readonly IReadOnlyDictionary<string, string> DutchConfig =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["country"] = "NL", ["language"] = "nl" };

    private static LidlPlusAdapter Adapter() => new(Options, new FixedTimeProvider(Now));

    // ---- defect one: PKCE --------------------------------------------------

    [Fact]
    public void The_challenge_is_a_correct_s256_of_the_verifier()
    {
        var pkce = PkceChallenge.Create();

        // BASE64URL(SHA256(ASCII(verifier))), computed here from first
        // principles rather than by calling the code under test.
        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(pkce.Verifier)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        Assert.Equal(expected, pkce.Challenge);

        // S256, never "plain": plain puts the secret straight in the URL and
        // protects nothing at all.
        Assert.Equal("S256", PkceChallenge.Method);

        // RFC 7636 sizes the verifier at 43-128 characters of the unreserved
        // set, so it survives a URL and a form body untouched.
        Assert.InRange(pkce.Verifier.Length, 43, 128);
        Assert.Matches("^[A-Za-z0-9._~-]+$", pkce.Verifier);

        // Fresh per login. A reused verifier is a reused secret.
        Assert.NotEqual(pkce.Verifier, PkceChallenge.Create().Verifier);
    }

    [Fact]
    public async Task The_verifier_that_answers_the_challenge_is_the_one_replayed_at_the_exchange()
    {
        var page = FullyLoggedInPage();
        var handler = new StubHttpHandler(Route);

        using var ctx = Context(handler, Email);

        await Adapter().LoginAsync(ctx, page, Arrives(), CancellationToken.None);

        // The challenge went up with the authorize request...
        var authorize = Query(Assert.Single(page.Visited));
        var challenge = authorize["code_challenge"];

        Assert.NotEmpty(challenge);
        Assert.Equal("S256", authorize["code_challenge_method"]);

        // ...and the verifier came back down with the code, on the other side
        // of a whole browser login. Surviving that journey is the entire
        // point: the two halves are minted together and used minutes apart.
        var exchange = Form(Assert.Single(handler.Requests, r => r.Path == TokenPath));

        Assert.True(exchange.TryGetValue("code_verifier", out var verifier),
            "the token exchange carried no code_verifier");
        Assert.Equal(challenge, PkceChallenge.For(verifier).Challenge);

        Assert.Equal("authorization_code", exchange["grant_type"]);
        Assert.Equal("lidl-authorization-code", exchange["code"]);
        Assert.Equal("com.lidlplus.app://callback", exchange["redirect_uri"]);
    }

    [Fact]
    public async Task The_authorize_call_still_carries_the_four_confirmed_parameters()
    {
        var page = FullyLoggedInPage();
        using var ctx = Context(new StubHttpHandler(Route), Email);

        await Adapter().LoginAsync(ctx, page, Arrives(), CancellationToken.None);

        var url = Assert.Single(page.Visited);
        Assert.StartsWith("https://accounts.lidl.com/connect/authorize?", url, StringComparison.Ordinal);

        var query = Query(url);
        Assert.Equal("LidlPlusNativeClient", query["client_id"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("com.lidlplus.app://callback", query["redirect_uri"]);

        // offline_access is what yields the refresh token, and the refresh
        // token is the whole T2 story.
        Assert.Contains("offline_access", query["scope"], StringComparison.Ordinal);
    }

    // ---- defect two: the client secret -------------------------------------

    [Fact]
    public async Task The_basic_header_names_the_client_secret_the_reference_confirms()
    {
        var handler = new StubHttpHandler(Route);
        using var ctx = Context(handler, Email);

        await Adapter().LoginAsync(ctx, FullyLoggedInPage(), Arrives(), CancellationToken.None);

        var exchange = Assert.Single(handler.Requests, r => r.Path == TokenPath);
        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes("LidlPlusNativeClient:secret"));

        Assert.Equal($"Basic {expected}", exchange.Header("Authorization"));

        // The value that used to go out: the client id, a colon, and nothing.
        // That is a different client as far as the token endpoint cares, and
        // it is bug two of the three.
        var empty = Convert.ToBase64String(Encoding.UTF8.GetBytes("LidlPlusNativeClient:"));
        Assert.NotEqual($"Basic {empty}", exchange.Header("Authorization"));
    }

    // ---- defect three: an e-mail is a username too --------------------------

    [Fact]
    public async Task An_email_goes_into_the_email_input_and_never_into_the_phone_one()
    {
        // Both boxes are on the page, exactly as the reference's two selectors
        // say they are.
        var page = FullyLoggedInPage();
        using var ctx = Context(new StubHttpHandler(Route), Email);

        await Adapter().LoginAsync(ctx, page, Arrives(), CancellationToken.None);

        Assert.Equal(Email, page.Filled[EmailInput]);
        Assert.DoesNotContain(PhoneInput, page.Filled.Keys);
        Assert.Equal("hunter2", page.Filled[PasswordInput]);
    }

    [Fact]
    public async Task A_phone_number_goes_into_the_phone_input_and_never_into_the_email_one()
    {
        var page = FullyLoggedInPage();
        using var ctx = Context(new StubHttpHandler(Route), Phone);

        await Adapter().LoginAsync(ctx, page, Arrives(), CancellationToken.None);

        Assert.Equal(Phone, page.Filled[PhoneInput]);
        Assert.DoesNotContain(EmailInput, page.Filled.Keys);
    }

    [Theory]
    [InlineData(Email, "e-mail field")]
    [InlineData(Phone, "phone field")]
    public async Task The_expected_input_missing_is_a_shape_change_and_never_a_fallback(
        string username, string expected)
    {
        // The page has the other box, the password box and the button - so a
        // filler willing to settle for "some input" would find one.
        var other = username == Email ? PhoneInput : EmailInput;
        var page = StubLoginPage.Showing(other, PasswordInput, Submit, PasswordSubmit);

        var handler = new StubHttpHandler(Route);
        using var ctx = Context(handler, username);

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().LoginAsync(ctx, page, Arrives(), CancellationToken.None));

        // Named, so the next breakage arrives already diagnosed.
        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains(expected, error.Detail, StringComparison.Ordinal);

        // Nothing was typed anywhere. Putting an address into the phone box
        // spends a login attempt that is never retried and tells the user
        // their credentials are wrong when they are not.
        Assert.Empty(page.Filled);
        Assert.False(ctx.CredentialWasSubmitted);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("shopper@example.com", true)]
    [InlineData("first.last+lidl@sub.example.co.uk", true)]
    [InlineData("+31612345678", false)]
    [InlineData("+4915112345678", false)]
    public void The_shape_of_the_value_decides_which_box_it_belongs_in(string username, bool email)
    {
        // The same single test the manifest's pattern is built around: only
        // one of the two shapes it admits can hold an '@'.
        Assert.Equal(email, LidlPlusAdapter.LooksLikeEmail(username));
        Assert.Matches(LidlPlusManifest.UsernamePattern, username);
    }

    [Theory]
    [InlineData("username")]
    [InlineData("password")]
    public async Task A_login_missing_a_credential_is_refused_before_the_page_is_touched(string missing)
    {
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["username"] = Email,
            ["password"] = "hunter2",
        };
        inputs.Remove(missing);

        var page = FullyLoggedInPage();
        var handler = new StubHttpHandler(Route);

        using var ctx = new FakeJobContext(handler)
        {
            Config = DutchConfig,
            Inputs = inputs,
        };

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().LoginAsync(ctx, page, Arrives(), CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidRequest, error.Code);
        Assert.False(ctx.CredentialWasSubmitted);
        Assert.Empty(page.Visited);
        Assert.Empty(handler.Requests);
    }

    // ---- the code, and which way it arrived --------------------------------

    [Theory]
    [InlineData(Email, "email")]
    [InlineData(Phone, "sms")]
    public async Task The_delivery_on_the_challenge_follows_what_the_user_actually_supplied(
        string username, string delivery)
    {
        var page = FullyLoggedInPage();
        using var ctx = Context(new StubHttpHandler(Route), username, answer: _ => Code);

        await Adapter().LoginAsync(ctx, page, Arrives(afterWaits: 1), CancellationToken.None);

        var challenge = Assert.Single(ctx.Asked);
        Assert.Equal(ChallengeType.MfaCode, challenge.Type);

        // The whole point. "sms" for everybody tells an e-mail user to watch
        // a phone that will never ring while the code sits in their inbox,
        // and the challenge expires while they wait.
        Assert.Equal(delivery, challenge.Delivery);

        Assert.Equal(6, challenge.Length);
        Assert.Equal(Options.CodeLength, challenge.Length);
        Assert.Equal(Now.AddSeconds(300), challenge.ExpiresAt);

        // A key, never prose - and not the sms key, which would be the same
        // lie one layer down.
        Assert.Equal("connect.challenge.verification_code", challenge.PromptKey);

        // The code goes into the reference's own input, and role_next submits
        // it exactly as the reference clicks it.
        Assert.Equal(Code, page.Filled[CodeInput]);
        Assert.Equal(Options.VerificationSubmitSelectors[0], page.Clicked[^1]);
        Assert.Contains(JobStep.AwaitingHuman, ctx.Steps);
    }

    [Fact]
    public async Task A_page_that_states_it_mailed_the_code_beats_the_inference_about_a_phone_login()
    {
        // Signed in with a phone number, but the page says the code went to
        // the mailbox. The page knows and we are guessing, so it wins.
        var page = StubLoginPage.Showing(
            PhoneInput, PasswordInput, Submit, PasswordSubmit, CodeInput, Options.EmailDeliverySelectors[0]);

        using var ctx = Context(new StubHttpHandler(Route), Phone, answer: _ => Code);

        await Adapter().LoginAsync(ctx, page, Arrives(afterWaits: 1), CancellationToken.None);

        Assert.Equal("email", Assert.Single(ctx.Asked).Delivery);
    }

    [Fact]
    public async Task A_device_lidl_already_trusts_skips_the_code_and_nobody_is_asked_anything()
    {
        // No code box at all, and no wall either: Lidl goes straight to the
        // callback. Treating that as a failure would break the one login that
        // went perfectly.
        var page = StubLoginPage.Showing(EmailInput, PasswordInput, Submit, PasswordSubmit);
        using var ctx = Context(new StubHttpHandler(Route), Email);

        var result = await Adapter().LoginAsync(ctx, page, Arrives(), CancellationToken.None);

        Assert.Empty(ctx.Asked);
        Assert.Equal("lidl-access-token-fixture", result.Material.AccessToken);
        Assert.Equal("lidl-refresh-token-fixture", result.Material.RefreshToken);

        // Country and language are sealed in alongside the tokens: every
        // ticket URL and two headers need them, and a fetch that arrives
        // without config would otherwise be unserviceable.
        Assert.Equal("NL", result.Material.Extra["country"]);
        Assert.Equal("nl", result.Material.Extra["language"]);
    }

    [Fact]
    public async Task The_latch_closes_before_the_credentials_leave_the_machine()
    {
        var page = FullyLoggedInPage();
        using var ctx = Context(new StubHttpHandler(Route), Email, answer: _ => Code);

        await Adapter().LoginAsync(ctx, page, Arrives(afterWaits: 1), CancellationToken.None);

        // After this a lost lease fails the job rather than requeuing it: a
        // retried login is how an account gets locked, and Lidl has by then
        // already sent a code.
        Assert.True(ctx.CredentialWasSubmitted);
    }

    // ---- the wall, if Lidl ever puts one up --------------------------------

    [Fact]
    public async Task An_unattended_interactive_captcha_is_refused_at_once_and_nobody_is_asked()
    {
        // No code box, because the widget is standing where it would be.
        var page = StubLoginPage.Showing(
            EmailInput, PasswordInput, Submit, PasswordSubmit, Options.InteractiveCaptchaSelectors[0]);

        using var ctx = new FakeJobContext(new StubHttpHandler(Route))
        {
            Config = DutchConfig,
            Inputs = Credentials(Email),
            Browser = new StubBrowserLease(page),
        };

        var watcher = new StubRedirectWaiter(redirect: null, afterWaits: 0);

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().LoginAsync(ctx, page, watcher, CancellationToken.None));

        // Blocked, not internal and not invalid_credentials: nothing is wrong
        // with the password. There is simply nobody at this browser.
        Assert.Equal(ErrorCode.BlockedByProvider, error.Code);
        Assert.Contains("unattended", error.Detail, StringComparison.Ordinal);

        // Not one question, because there is nobody to answer it, and no
        // waiting on a redirect that is never coming.
        Assert.Empty(ctx.Asked);
        Assert.Equal(0, watcher.Waits);

        // The password is off the page even though nothing photographed it:
        // the runner may capture an artifact on the way out of a failed job.
        Assert.False(page.HoldsSecret);
    }

    [Fact]
    public async Task An_attended_interactive_captcha_asks_the_human_and_waits_for_the_redirect()
    {
        var page = StubLoginPage.Showing(
            EmailInput, PasswordInput, Submit, PasswordSubmit, Options.InteractiveCaptchaSelectors[0]);

        using var ctx = new FakeJobContext(new StubHttpHandler(Route))
        {
            Config = DutchConfig,
            Inputs = Credentials(Email),
            Attended = true,
            Browser = new StubBrowserLease(page),
            // Nobody ever answers: the person who passes the widget in the
            // browser has no reason to go back to the consumer's UI.
            AnswersNothing = true,
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await Adapter().LoginAsync(
            ctx, page, new StubRedirectWaiter(Redirect, afterWaits: 1), cts.Token);

        var challenge = Assert.Single(ctx.Asked);

        // Passive: there is nothing to type back, because the widget hands
        // its token to Lidl and not to us.
        Assert.Equal(ChallengeType.AppApproval, challenge.Type);
        Assert.True(challenge.IsPassive);
        Assert.Null(challenge.Image);

        Assert.Equal("lidl-access-token-fixture", result.Material.AccessToken);
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// The provider, answering from its recorded payload. Only the token
    /// endpoint is ever reached by a login.
    /// </summary>
    private static HttpResponseMessage Route(RecordedRequest request, int _) =>
        request.Path == TokenPath ? Stub.Fixture("lidl/token.json") : Stub.Status(HttpStatusCode.NotFound);

    /// <summary>Every box the reference names, all on one page.</summary>
    private static StubLoginPage FullyLoggedInPage() =>
        StubLoginPage.Showing(EmailInput, PhoneInput, PasswordInput, Submit, PasswordSubmit, CodeInput, CodeSubmit);

    private static StubRedirectWaiter Arrives(int afterWaits = 0) => new(Redirect, afterWaits);

    private static IReadOnlyDictionary<string, string> Credentials(string username) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["username"] = username,
            ["password"] = "hunter2",
        };

    /// <summary>
    /// A job whose human answers the code challenge. A test that expects no
    /// challenge at all asserts <c>Empty(ctx.Asked)</c> rather than relying on
    /// the answer being refused, so the assertion says what it means.
    /// </summary>
    private static FakeJobContext Context(
        HttpMessageHandler handler, string username, Func<Challenge, string>? answer = null) =>
        new(handler)
        {
            Config = DutchConfig,
            Inputs = Credentials(username),
            Answer = answer ?? (_ => Code),
        };

    private static IReadOnlyDictionary<string, string> Query(string url) =>
        Pairs(new Uri(url).Query.TrimStart('?'));

    private static IReadOnlyDictionary<string, string> Form(RecordedRequest request)
    {
        Assert.NotNull(request.Body);
        return Pairs(request.Body);
    }

    /// <summary>
    /// A form body or a query string, unescaped. Hand-rolled rather than
    /// pulled from a web stack: the assertions here are about exactly which
    /// characters went onto the wire, so the reader wants to see the decoding
    /// rather than trust a helper's opinion of it.
    /// </summary>
    private static IReadOnlyDictionary<string, string> Pairs(string encoded)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var pair in encoded.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0) continue;

            map[Uri.UnescapeDataString(pair[..separator])] =
                Uri.UnescapeDataString(pair[(separator + 1)..].Replace('+', ' '));
        }

        return map;
    }
}
