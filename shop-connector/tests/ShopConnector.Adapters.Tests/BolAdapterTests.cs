using System.Text.Json;
using System.Net;
using Connector.Kit.Adapters;
using Connector.Kit.Challenges;
using Connector.Kit.Errors;
using Connector.Kit.Jobs;
using Connector.Kit.Manifests;
using Connector.Kit.Security;
using Microsoft.Playwright;
using ShopConnector.Adapters.Bol;
using ShopConnector.Adapters.Fixtures;
using ShopConnector.Adapters.Support;
using ShopConnector.Adapters.Tests.Support;
using Xunit;

namespace ShopConnector.Adapters.Tests;

/// <summary>
/// bol.com: the manifest, the login, and the difference between being blocked
/// and being wrong.
///
/// The whole provider is built on one confirmed fact and one open question.
/// The fact is that <c>login.bol.com/wsp/login</c> is a Spring Security form
/// posting <c>j_username</c> and <c>j_password</c> - read off the public page,
/// no account touched. The question is whether the account's order list is
/// HTML or JSON, which cannot be settled without an account; both shapes are
/// built and an option chooses, which is what
/// <see cref="BolParsingTests"/> covers.
///
/// What this file guards is the part where a wrong answer costs a user
/// something. bol has no refresh grant, so a login is expensive and is never
/// retried; a bot wall reported as a bad password sends somebody to reset a
/// password that was fine; and a captcha met by an agent nobody is sitting at
/// has to say so immediately rather than hold an agent until its budget runs
/// out. No browser, no network, no credentials.
/// </summary>
public sealed class BolAdapterTests
{
    private const string Email = "shopper@example.com";
    private const string Password = "hunter2";
    private const string Code = "418362";

    private static readonly DateTimeOffset Now = new(2026, 7, 28, 3, 0, 0, TimeSpan.Zero);

    private static readonly BolOptions Options = new();

    /// <summary>
    /// For the tests that serve an HTML fixture. The default shape is the
    /// captured GraphQL one, so a test about the page walk has to say that it
    /// means the page.
    /// </summary>
    private static readonly BolOptions Pages = Options with { OrdersShape = BolOrdersShape.Html };

    private static readonly string Username = Options.UsernameSelectors[0];
    private static readonly string PasswordBox = Options.PasswordSelectors[0];
    private static readonly string Submit = Options.SubmitSelectors[0];
    private static readonly string CodeBox = Options.VerificationCodeSelectors[0];
    private static readonly string CodeSubmit = Options.VerificationSubmitSelectors[0];
    private static readonly string Recaptcha = Options.InteractiveCaptchaSelectors[0];

    private static BolAdapter Adapter(BolOptions? options = null) =>
        new(options ?? Options, new FixedTimeProvider(Now));

    // ---- the manifest -------------------------------------------------------

    [Fact]
    public void The_manifest_validates_and_the_registry_accepts_it()
    {
        // The same construction the host performs at startup, which refuses to
        // boot on an invalid manifest. A provider that cannot be registered is
        // not shippable however good its parser is.
        var registry = new ProviderRegistry([BolAdapters.Create()]);

        var manifest = registry.RequireManifest(BolAdapter.ProviderId);
        ManifestValidator.Validate(manifest);

        Assert.Equal("bol", manifest.Id);
        Assert.Equal("bol.com", manifest.Name);
        Assert.Equal(ProviderKind.Store, manifest.Kind);
        Assert.Equal("NL", manifest.Country);
    }

    [Fact]
    public void Bol_signs_in_through_a_browser_once_on_a_dutch_residential_line()
    {
        var manifest = Adapter().Describe();

        // T3, not T2. The login is a JavaScript page with a reCAPTCHA site key
        // in its config blob, so it happens in a browser - and what comes out
        // is an ordinary cookie jar, so every fetch after that is plain HTTP.
        // But T2 is defined as "a refresh token serves every fetch after", and
        // bol issues no token: the jar dies in a day and the browser has to
        // come back, which is T3. Jumbo declares T3 for identical mechanics.
        Assert.Equal(ProviderRuntime.BrowserInteractive, manifest.Runtime);
        Assert.True(manifest.Agent.Required);
        Assert.Equal(AgentClass.Pooled, manifest.Agent.Class);
        Assert.NotNull(manifest.Agent.Egress);
        Assert.Equal("NL", manifest.Agent.Egress.Country);
        Assert.Equal("residential", manifest.Agent.Egress.Kind);
        Assert.Equal(SecretCustody.Client, manifest.SecretCustody);
    }

    [Fact]
    public void Bol_does_not_claim_unattended_operation_it_cannot_deliver()
    {
        var manifest = Adapter().Describe();

        // bol issues consumers no token of any kind - only a session cookie -
        // so there is nothing to refresh and a human is needed when it dies.
        // Claiming otherwise would make the consuming app promise scheduled
        // syncing and then fail, which is worse than not supporting bol at all.
        Assert.False(manifest.UnattendedFetch);
        Assert.False(manifest.Auth.Session.Refreshable);
        Assert.False(manifest.Auth.Reauth.Cheap);

        // The field defaults to TRUE, so this one has to be asserted or a
        // manifest that simply never mentions it goes on telling consumers to
        // "persist the bundle from every response" - for a provider that never
        // re-issues one, because bol hands out no token at all.
        Assert.False(
            manifest.Auth.Session.RotatesOnUse,
            "no fetch returns RefreshedMaterial, so nothing is ever re-issued");
        Assert.Equal(new[] { "session_expired" }, manifest.Auth.Reauth.TriggerCodes);

        // A day, and honestly a guess: nobody outside bol knows the session's
        // real life. Short beats long - being wrong short costs a sign-in,
        // being wrong long costs the user's trust in the connection.
        Assert.Equal(86_400, manifest.Auth.Session.TtlSeconds);
    }

    [Fact]
    public void The_credential_is_an_email_address_and_the_password_is_marked_secret()
    {
        var manifest = Adapter().Describe();
        var step = Assert.Single(manifest.Auth.Steps);

        Assert.Equal(AuthFlow.Password, manifest.Auth.Flow);
        Assert.Equal(2, step.Fields.Count);

        // CONFIRMED from the login page: <input type="email" name="j_username">.
        // A consumer that renders a member number or a phone keypad spends a
        // failed attempt against a page that may be scoring the browser.
        var username = Assert.Single(step.Fields, f => f.Key == "username");
        Assert.Equal(FieldType.Text, username.Type);
        Assert.False(username.Secret);
        Assert.NotNull(username.Pattern);
        Assert.Matches(username.Pattern, Email);
        Assert.DoesNotMatch(username.Pattern, "+31612345678");

        var password = Assert.Single(step.Fields, f => f.Key == "password");
        Assert.Equal(FieldType.Password, password.Type);
        Assert.True(password.Secret, "an unmarked password is logged and screenshotted");
    }

    [Fact]
    public void Every_challenge_bol_might_raise_is_declared_rather_than_discovered()
    {
        var manifest = Adapter().Describe();

        // MfaCode because bol is assumed to send a code on a new device and
        // nobody has proved otherwise; Image for a plain captcha, which a
        // relay can carry; AppApproval for the reCAPTCHA whose site key is
        // confirmed present, which cannot be relayed at all. A consumer with
        // no UI for a challenge its provider raises strands the user at the
        // worst possible moment.
        Assert.Equal(
            new[] { ChallengeType.MfaCode, ChallengeType.Image, ChallengeType.AppApproval },
            manifest.Auth.Challenges);
    }

    [Fact]
    public void The_receipts_resource_matches_the_shape_every_other_store_offers()
    {
        var receipts = Adapter().Describe().Resource(BolAdapter.ReceiptsResource);

        Assert.NotNull(receipts);
        Assert.Equal(ResourceShape.Receipt, receipts.Returns);
        Assert.True(receipts.Param("since")!.Required);
        Assert.Equal(ParamType.Date, receipts.Param("until")!.Type);
        // raw as well as items, now that the orders come from an API and there
        // is a document to hand back rather than markup we picked apart.
        Assert.Equal(new[] { "items", "raw" }, receipts.Param("include")!.Values);

        // A marketplace order surfaces when the seller ships it, so a row can
        // appear days after the date it will carry. Fetching strictly since
        // the last sync would lose it permanently and invisibly.
        Assert.Equal(7, Adapter().Describe().Limits.SettlementLagDays);
    }

    [Fact]
    public void Every_string_a_human_reads_is_a_key_and_never_prose()
    {
        var manifest = Adapter().Describe();

        Assert.StartsWith("connect.", manifest.NotesKey, StringComparison.Ordinal);
        foreach (var step in manifest.Auth.Steps)
        {
            Assert.StartsWith("connect.", step.LabelKey, StringComparison.Ordinal);
        }

        foreach (var field in manifest.Auth.AllFields())
        {
            Assert.StartsWith("connect.", field.LabelKey, StringComparison.Ordinal);
        }
    }

    // ---- the login ----------------------------------------------------------

    [Fact]
    public async Task The_spring_form_field_names_are_the_ones_that_get_filled()
    {
        var page = SignedInPage();
        using var ctx = LoginContext(page);

        var result = await Adapter().LoginAsync(ctx, page, Arrives(), CancellationToken.None);

        // j_username and j_password: Servlet/Spring Security's own names, read
        // off the public login page. Getting these wrong is a failed attempt
        // that is never retried.
        Assert.Equal(Email, page.Filled[Username]);
        Assert.Equal(Password, page.Filled[PasswordBox]);
        Assert.Contains("j_username", Username, StringComparison.Ordinal);
        Assert.Contains("j_password", PasswordBox, StringComparison.Ordinal);

        // The whole credential is the cookie jar. No token, so nothing to seal
        // but the storage state - and an expiry stated from this login rather
        // than from whenever the bundle is next read.
        Assert.NotNull(result.Material.StorageState);
        Assert.Null(result.Material.AccessToken);
        Assert.Null(result.Material.RefreshToken);
        Assert.Equal(Now.AddSeconds(86_400), result.ExpiresAt);
        Assert.Equal("bol.com", result.Account!.DisplayName);
    }

    [Fact]
    public async Task The_latch_closes_before_the_credentials_leave_the_machine()
    {
        var page = SignedInPage();
        using var ctx = LoginContext(page);

        await Adapter().LoginAsync(ctx, page, Arrives(), CancellationToken.None);

        // After this a lost lease fails the job rather than requeuing it. bol
        // gives out no refresh grant, so a retried login is a second real
        // sign-in attempt against an account that may already have been sent
        // a code.
        Assert.True(ctx.CredentialWasSubmitted);
        Assert.Equal(Submit, page.Clicked[^1]);
    }

    [Theory]
    [InlineData("username")]
    [InlineData("password")]
    public async Task A_login_missing_a_credential_is_refused_before_the_page_is_touched(string missing)
    {
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["username"] = Email,
            ["password"] = Password,
        };
        inputs.Remove(missing);

        var page = SignedInPage();
        using var ctx = new FakeJobContext { Inputs = inputs, Browser = new BolBrowserStub(page) };

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().LoginAsync(ctx, page, Arrives(), CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidRequest, error.Code);
        Assert.False(ctx.CredentialWasSubmitted);
        Assert.Empty(page.Visited);
        Assert.Empty(page.Filled);
    }

    [Fact]
    public async Task A_missing_password_box_is_a_shape_change_and_spends_no_attempt()
    {
        // The e-mail box, the button and a code box are all there, so a filler
        // willing to settle for "some input" would find one.
        var page = StubLoginPage.Showing(Username, Submit, CodeBox);
        using var ctx = LoginContext(page);

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().LoginAsync(ctx, page, Arrives(), CancellationToken.None));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains("password field", error.Detail, StringComparison.Ordinal);
        Assert.Contains("j_password", error.Detail, StringComparison.Ordinal);

        // Nothing was submitted and nothing was clicked: the attempt was never
        // spent, which is what makes this recoverable by fixing a selector.
        Assert.False(ctx.CredentialWasSubmitted);
        Assert.Empty(page.Clicked);
    }

    [Fact]
    public async Task The_login_enters_through_the_account_page_and_falls_back_to_the_form_itself()
    {
        var page = SignedInPage();
        using var ctx = LoginContext(page);

        await Adapter().LoginAsync(ctx, page, Arrives(), CancellationToken.None);

        // bol's own 302 chain is the flow a shopper takes, so that is the way
        // in - and it is the address that brings the browser back to the
        // account page afterwards.
        Assert.Equal("https://www.bol.com/nl/nl/rnwy/account/bestellingen", Assert.Single(page.Visited));
    }

    [Fact]
    public async Task A_redirect_chain_that_leads_nowhere_still_reaches_the_login_form()
    {
        // The form's address is one of the few confirmed facts about bol, so a
        // 302 chain that stops leading to it costs one navigation rather than
        // the whole connection.
        var page = StubLoginPage.Showing(Username, PasswordBox, Submit);
        using var ctx = LoginContext(page);

        await Adapter(Options with { UsernameSelectors = [Username] })
            .LoginAsync(ctx, page, Arrives(), CancellationToken.None);

        Assert.Equal("https://www.bol.com/nl/nl/rnwy/account/bestellingen", page.Visited[0]);

        // The page had the box on the first try here, so the fallback stayed
        // unused - which is the assertion that keeps it a fallback.
        Assert.Single(page.Visited);

        // And when the box is not there, the second address is tried before
        // anything is called a shape change.
        var blank = StubLoginPage.Showing(PasswordBox, Submit);
        using var second = LoginContext(blank);

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().LoginAsync(second, blank, Arrives(), CancellationToken.None));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Equal(
            new[] { "https://www.bol.com/nl/nl/rnwy/account/bestellingen", "https://login.bol.com/wsp/login" },
            blank.Visited);

        Assert.Contains("either the account page's redirect or the login form", error.Detail,
            StringComparison.Ordinal);
        Assert.False(second.CredentialWasSubmitted);
    }

    [Fact]
    public async Task A_login_that_leaves_no_cookies_for_the_orders_host_is_a_shape_change()
    {
        // bol sets JSESSIONID on login.bol.com. A browser would not carry that
        // to www.bol.com, and neither does this - so a login that leaves only
        // that behind has produced nothing a fetch can use, and saying so here
        // beats discovering it on every sync afterwards.
        var page = SignedInPage();
        using var ctx = LoginContext(page, storageState: LoginHostOnlyState);

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().LoginAsync(ctx, page, Arrives(), CancellationToken.None));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains("www.bol.com", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_one_time_code_is_relayed_to_the_human_and_never_solved()
    {
        var page = StubLoginPage.Showing(Username, PasswordBox, Submit, CodeBox, CodeSubmit);
        using var ctx = LoginContext(page, answer: _ => Code);

        // The code box is up on the first pass and gone on the second, which
        // is what answering it looks like from here.
        await Adapter().LoginAsync(ctx, page, Arrives(afterWaits: 1), CancellationToken.None);

        var challenge = Assert.Single(ctx.Asked);
        Assert.Equal(ChallengeType.MfaCode, challenge.Type);

        // bol's credential is an e-mail address, so the mailbox is where it
        // can reach them. Saying "sms" would send somebody to watch a phone
        // that never rings while the challenge expires.
        Assert.Equal("email", challenge.Delivery);
        Assert.Equal("connect.challenge.verification_code", challenge.PromptKey);

        // Nothing is claimed about the length: nobody has seen the screen, and
        // a wrong length makes the consumer reject a valid code before it is
        // ever tried.
        Assert.Null(challenge.Length);
        Assert.Equal(Now.AddSeconds(300), challenge.ExpiresAt);

        Assert.Equal(Code, page.Filled[CodeBox]);
        Assert.Contains(JobStep.AwaitingHuman, ctx.Steps);
    }

    [Fact]
    public async Task A_device_bol_already_trusts_is_never_asked_for_anything()
    {
        var page = SignedInPage();
        using var ctx = LoginContext(page);

        var result = await Adapter().LoginAsync(ctx, page, Arrives(), CancellationToken.None);

        Assert.Empty(ctx.Asked);
        Assert.NotNull(result.Material.StorageState);
    }

    [Fact]
    public async Task An_unattended_recaptcha_is_refused_at_once_and_nobody_is_asked()
    {
        var page = StubLoginPage.Showing(Username, PasswordBox, Submit, Recaptcha);
        using var ctx = LoginContext(page);

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().LoginAsync(ctx, page, Never(), CancellationToken.None));

        // Blocked, never invalid_credentials: nothing is wrong with the
        // password. There is simply nobody at this browser, and a widget mints
        // its token inside its own JavaScript - no screenshot and no typed
        // answer can ever pass one.
        Assert.Equal(ErrorCode.BlockedByProvider, error.Code);
        Assert.Contains("unattended", error.Detail, StringComparison.Ordinal);
        Assert.Empty(ctx.Asked);

        // The password is off the page even though nothing photographed it:
        // the runner may capture an artifact on the way out of a failed job.
        Assert.False(page.HoldsSecret);
    }

    [Fact]
    public async Task An_attended_recaptcha_asks_the_human_and_waits_for_the_session_to_appear()
    {
        var page = StubLoginPage.Showing(Username, PasswordBox, Submit, Recaptcha);

        using var ctx = new FakeJobContext
        {
            Inputs = Credentials(),
            Browser = new BolBrowserStub(page),
            Attended = true,
            // Nobody ever answers: the person who passes the widget in the
            // browser has no reason to go back to the consumer's UI.
            AnswersNothing = true,
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await Adapter().LoginAsync(ctx, page, Arrives(afterWaits: 1), cts.Token);

        var challenge = Assert.Single(ctx.Asked);
        Assert.Equal(ChallengeType.AppApproval, challenge.Type);
        Assert.True(challenge.IsPassive);
        Assert.Null(challenge.Image);
        Assert.NotNull(result.Material.StorageState);
    }

    [Fact]
    public async Task Only_a_page_that_states_a_credential_error_may_report_one()
    {
        var page = StubLoginPage.Showing(Username, PasswordBox, Submit, Options.LoginErrorSelectors[0]);
        using var ctx = LoginContext(page);

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().LoginAsync(ctx, page, Never(), CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidCredentials, error.Code);
        Assert.False(page.HoldsSecret);
    }

    [Fact]
    public async Task A_notice_that_names_no_cause_is_a_block_and_not_a_bad_password()
    {
        // The shape Lidl's login turned out to answer an automated browser
        // with, and reading it as a wrong password was a real bug: the same
        // credentials sign in by hand. An operator who finds bol doing this
        // adds the selector to BlockedNoticeSelectors, and the outcome becomes
        // the truth instead of an accusation.
        var options = Options with { BlockedNoticeSelectors = ["[data-test='generic-error']"] };
        var page = StubLoginPage.Showing(Username, PasswordBox, Submit, "[data-test='generic-error']");

        using var ctx = LoginContext(page);

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter(options).LoginAsync(ctx, page, Never(), CancellationToken.None));

        Assert.Equal(ErrorCode.BlockedByProvider, error.Code);
        Assert.NotEqual(ErrorCode.InvalidCredentials, error.Code);
        Assert.False(page.HoldsSecret);
    }

    [Fact]
    public async Task A_login_that_says_nothing_at_all_is_a_shape_change_and_not_a_bad_password()
    {
        // No session, no error, no captcha, no code box. Guessing "wrong
        // password" from silence sends a user to reset a password that was
        // fine - and because a credential error is never retried, the mistake
        // is permanent for that session too.
        var page = StubLoginPage.Showing(Username, PasswordBox, Submit);
        using var ctx = LoginContext(page);

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter(Options with { LoginSettleSeconds = 0 })
                .LoginAsync(ctx, page, Never(), CancellationToken.None));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains("neither completed nor stated", error.Detail, StringComparison.Ordinal);
    }

    // ---- "are we in yet?" ---------------------------------------------------

    [Fact]
    public async Task The_session_watcher_waits_for_a_cookie_rather_than_a_redirect()
    {
        // Albert Heijn and Lidl finish by navigating to a scheme Chromium
        // cannot open. bol has no such moment - it simply stops being the
        // login page - so the cookie jar is the signal, and wearing the same
        // interface is what lets the shared CaptchaGate be reused unchanged.
        var page = SignedInPage();

        using var signedIn = new FakeJobContext { Browser = new BolBrowserStub(page) };
        var watcher = new BolSessionWatcher(signedIn, page, Options, new FixedTimeProvider(Now));

        Assert.NotNull(await watcher.WaitAsync(TimeSpan.FromMilliseconds(1), CancellationToken.None));

        // Still on login.bol.com means nothing has happened yet, whatever
        // cookies are lying around - and knowing that costs no round trip.
        using var stillOut = new FakeJobContext { Browser = new BolBrowserStub(page) };

        var stuck = new BolSessionWatcher(
            stillOut, new BolPageAt("https://login.bol.com/wsp/login"), Options, new FixedTimeProvider(Now));

        Assert.Null(await stuck.WaitAsync(TimeSpan.FromMilliseconds(1), CancellationToken.None));
    }

    [Fact]
    public void Only_cookies_a_browser_would_send_to_the_orders_host_are_sent()
    {
        var header = BolCookies.Header(FixtureCatalog.Read("bol/storage-state.json"), "www.bol.com");

        Assert.NotNull(header);

        // Scoped to .bol.com and to www.bol.com respectively.
        Assert.Contains("XSC=", header, StringComparison.Ordinal);
        Assert.Contains("shopping_session_id=", header, StringComparison.Ordinal);

        // JSESSIONID is set on login.bol.com with no leading dot. A browser
        // would not carry it across hosts, and a fetch that only works because
        // we over-sent is a fetch lying about which cookie authenticates it.
        Assert.DoesNotContain("JSESSIONID", header, StringComparison.Ordinal);

        // An expired cookie and a cookie for another site entirely.
        Assert.DoesNotContain("expired_marker", header, StringComparison.Ordinal);
        Assert.DoesNotContain("affiliate", header, StringComparison.Ordinal);
    }

    /// <summary>
    /// One named cookie, read out of the jar on its own so the CSRF token can
    /// be echoed in a header.
    ///
    /// The name is matched case-insensitively, which RFC 6265 does not require
    /// - it says names are case-sensitive. The leniency is deliberate and
    /// matches how this file already treats <c>SessionCookieNames</c>: bol
    /// shipping <c>Xsrf-Token</c> one day should cost a fetch nothing, and two
    /// cookies differing only in case is not a thing that happens.
    /// </summary>
    [Fact]
    public void A_single_cookie_can_be_read_by_name_whatever_its_casing()
    {
        var jar = FixtureCatalog.Read("bol/storage-state.json");

        Assert.Equal("fixture-xsrf-token", BolCookies.Value(jar, "www.bol.com", "XSRF-TOKEN"));
        Assert.Equal("fixture-xsrf-token", BolCookies.Value(jar, "www.bol.com", "xsrf-token"));

        // Absent, and absent because it is out of scope - JSESSIONID is in the
        // jar but belongs to login.bol.com.
        Assert.Null(BolCookies.Value(jar, "www.bol.com", "no-such-cookie"));
        Assert.Null(BolCookies.Value(jar, "www.bol.com", "JSESSIONID"));
    }

    // ---- the fetch, and what a refusal is -----------------------------------

    [Fact]
    public async Task A_fetch_sends_the_cookie_jar_and_says_which_shape_answered()
    {
        var handler = new StubHttpHandler((_, _) => Html(FixtureCatalog.Read("bol/orders-page.html")));
        using var ctx = FetchContext(handler);

        var result = await Adapter(Pages).FetchAsync(ctx, Requests.Receipts(), CancellationToken.None);

        Assert.Equal("html", result.Via);
        Assert.Equal(3, result.Receipts.Count);

        var request = handler.Requests[0];
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Contains("/nl/nl/rnwy/account/bestellingen", request.Path, StringComparison.Ordinal);
        Assert.Contains("XSC=", request.Header("Cookie"), StringComparison.Ordinal);
        Assert.Contains("nl-NL", request.Header("Accept-Language"), StringComparison.Ordinal);

        // Nothing rotates: bol issues no token, so the stored bundle stays
        // exactly as it is until it expires.
        Assert.Null(result.RefreshedMaterial);
        Assert.True(result.Complete);
    }

    /// <summary>
    /// The default shape, and the only one written against a payload anyone has
    /// seen. A GET would come back with bol's GraphQL playground rather than
    /// orders, so the operation reaching the wire as a POST body is the whole
    /// claim.
    /// </summary>
    [Fact]
    public async Task The_default_shape_posts_bol_s_own_persisted_operation()
    {
        var handler = new StubHttpHandler((_, i) => i == 0
            ? Stub.Fixture("bol/orders-graphql.json")
            : Stub.Json("""{"data":{"me":{"orders":[]}}}"""));

        using var ctx = FetchContext(handler);

        var result = await Adapter().FetchAsync(ctx, Requests.Receipts(), CancellationToken.None);

        Assert.Equal("graphql", result.Via);

        var request = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://www.bol.com/api/graphql", request.Uri.ToString());
        Assert.Contains("application/json", request.Header("Content-Type"), StringComparison.Ordinal);

        using var sent = JsonDocument.Parse(request.Body!);
        Assert.Equal("OrdersOverviewClient", sent.RootElement.GetProperty("operationName").GetString());
        Assert.Equal(
            Options.OrdersPersistedQueryHash,
            sent.RootElement.GetProperty("extensions").GetProperty("persistedQuery")
                .GetProperty("sha256Hash").GetString());

        // The cursor advances with the walk, which is what stops page two from
        // being page one again.
        using var second = JsonDocument.Parse(handler.Requests[1].Body!);
        Assert.Equal("0", sent.RootElement.GetProperty("variables").GetProperty("after").GetString());
        Assert.Equal("5", second.RootElement.GetProperty("variables").GetProperty("after").GetString());
    }

    /// <summary>
    /// The two headers bol's edge demands, and the reason the first live run of
    /// this adapter failed.
    ///
    /// Both were established against the real endpoint, signed out, one header
    /// at a time: the operation named ONLY in the body earns
    /// <c>400 {"code":"InvalidRequest"}</c> before GraphQL sees the request at
    /// all, and adding <c>bol-app-operation-name</c> alone turns that into a
    /// 200. Fixing it uncovered the second: no <c>x-xsrf-token</c> and bol
    /// answers <c>403 XSRF failed</c>. Neither is guessable from the payload,
    /// which is why they are asserted rather than trusted.
    /// </summary>
    [Fact]
    public async Task The_api_call_names_its_operation_in_a_header_and_echoes_the_xsrf_token()
    {
        var handler = new StubHttpHandler((_, i) => i == 0
            ? Stub.Fixture("bol/orders-graphql.json")
            : Stub.Json("""{"data":{"me":{"orders":[]}}}"""));

        using var ctx = FetchContext(handler);

        await Adapter().FetchAsync(ctx, Requests.Receipts(), CancellationToken.None);

        var request = handler.Requests[0];

        // The same operation as the body, stated twice because bol wants it twice.
        Assert.Equal("OrdersOverviewClient", request.Header("bol-app-operation-name"));

        // Double-submitted: the value comes off the cookie the login stored,
        // so a hard-coded token here would pass while the real thing failed.
        Assert.Equal("fixture-xsrf-token", request.Header("x-xsrf-token"));
        Assert.Contains("XSRF-TOKEN=fixture-xsrf-token", request.Header("Cookie"), StringComparison.Ordinal);
    }

    /// <summary>
    /// A page GET is not a write and a browser sends no token on one. Sending
    /// it anyway would be this adapter inventing traffic bol never sees from
    /// its own front end.
    /// </summary>
    [Fact]
    public async Task A_page_fetch_carries_neither_header_because_it_asks_no_operation()
    {
        var handler = new StubHttpHandler((_, _) => Html(FixtureCatalog.Read("bol/orders-page.html")));
        using var ctx = FetchContext(handler);

        await Adapter(Pages).FetchAsync(ctx, Requests.Receipts(), CancellationToken.None);

        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        Assert.Null(handler.Requests[0].Header("x-xsrf-token"));
        Assert.Null(handler.Requests[0].Header("bol-app-operation-name"));
    }

    /// <summary>
    /// Without the token bol answers 403, which this adapter reads as bol
    /// blocking us - a dead end for whoever has to act on it. A login sets the
    /// cookie, so session_expired both names the problem and asks for the thing
    /// that fixes it.
    /// </summary>
    [Fact]
    public async Task A_session_missing_the_xsrf_cookie_asks_for_a_login_instead_of_being_refused()
    {
        var jar = FixtureCatalog.Read("bol/storage-state.json")
            .Replace("XSRF-TOKEN", "SOMETHING-ELSE", StringComparison.Ordinal);

        var handler = new StubHttpHandler((_, _) => Stub.Fixture("bol/orders-graphql.json"));

        using var ctx = new FakeJobContext(handler)
        {
            Material = new SessionMaterial { StorageState = jar },
        };

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().FetchAsync(ctx, Requests.Receipts(), CancellationToken.None));

        Assert.Equal(ErrorCode.SessionExpired, error.Code);
        Assert.Contains("XSRF-TOKEN", error.Detail, StringComparison.Ordinal);

        // And it never went to the wire: a request that cannot succeed is not
        // worth spending against an account bol may be scoring.
        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// The manifest offers "raw", so a caller that asks has to get one - a
    /// declared include that always arrives empty is worse than one never
    /// offered, because the consumer builds a screen for it.
    /// </summary>
    [Fact]
    public async Task Raw_is_the_order_exactly_as_bol_sent_it_and_only_when_asked()
    {
        var handler = new StubHttpHandler((_, i) => i == 0
            ? Stub.Fixture("bol/orders-graphql.json")
            : Stub.Json("""{"data":{"me":{"orders":[]}}}"""));

        using var ctx = FetchContext(handler);

        var asked = await Adapter().FetchAsync(
            ctx, Requests.Receipts(raw: true), CancellationToken.None);

        // Keyed by the external id its receipt carries, or a consumer cannot
        // pair the two up.
        var document = Assert.Contains("A000TEST001", (IDictionary<string, string>)asked.Raw!);

        using var parsed = JsonDocument.Parse(document);
        Assert.Equal("A000TEST001", parsed.RootElement.GetProperty("reference").GetString());

        // Verbatim, not our reading of it: the whole value of raw is that it
        // still says what bol said when our parser stops agreeing with it.
        Assert.True(parsed.RootElement.TryGetProperty("items", out _));
    }

    [Fact]
    public async Task A_caller_that_did_not_ask_for_raw_is_not_handed_somebody_s_purchase()
    {
        var handler = new StubHttpHandler((_, i) => i == 0
            ? Stub.Fixture("bol/orders-graphql.json")
            : Stub.Json("""{"data":{"me":{"orders":[]}}}"""));

        using var ctx = FetchContext(handler);

        var result = await Adapter().FetchAsync(
            ctx, Requests.Receipts(raw: false), CancellationToken.None);

        Assert.NotEmpty(result.Receipts);
        Assert.Empty(result.Raw ?? new Dictionary<string, string>(StringComparer.Ordinal));
    }

    /// <summary>
    /// A token that was good at login and is not any more.
    ///
    /// bol says so with a 403, and 403 is otherwise exactly how bol blocks a
    /// client - so the two are told apart by the body, which names itself.
    /// Getting this backwards marks the provider degraded and tells the user to
    /// wait for something that will never clear without a sign-in.
    /// </summary>
    [Fact]
    public async Task A_stale_csrf_token_asks_for_a_login_rather_than_reading_as_a_block()
    {
        var handler = new StubHttpHandler((_, _) => Stub.Json("XSRF failed", HttpStatusCode.Forbidden));
        using var ctx = FetchContext(handler);

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().FetchAsync(ctx, Requests.Receipts(), CancellationToken.None));

        Assert.Equal(ErrorCode.SessionExpired, error.Code);
    }

    /// <summary>
    /// And a 403 that says anything else is still bol refusing us. Without
    /// this, every block would present as an expired session and send the user
    /// through a sign-in that changes nothing.
    /// </summary>
    [Fact]
    public async Task A_403_that_is_not_about_the_token_is_still_a_refusal()
    {
        var handler = new StubHttpHandler((_, _) => Stub.Json("Access Denied", HttpStatusCode.Forbidden));
        using var ctx = FetchContext(handler);

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().FetchAsync(ctx, Requests.Receipts(), CancellationToken.None));

        Assert.Equal(ErrorCode.BlockedByProvider, error.Code);
    }

    /// <summary>
    /// bol states no order total, so the receipt's is our own sum and the flag
    /// has to survive all the way out of the adapter. A receipt that arrived
    /// reconciled here would be telling a downstream matcher that bol confirmed
    /// a number bol never said.
    /// </summary>
    [Fact]
    public async Task A_receipt_bol_never_totalled_leaves_the_adapter_saying_so()
    {
        var handler = new StubHttpHandler((_, i) => i == 0
            ? Stub.Fixture("bol/orders-graphql.json")
            : Stub.Json("""{"data":{"me":{"orders":[]}}}"""));

        using var ctx = FetchContext(handler);

        var result = await Adapter().FetchAsync(ctx, Requests.Receipts(), CancellationToken.None);

        Assert.NotEmpty(result.Receipts);
        Assert.All(result.Receipts, r => Assert.True(r.TotalIsDerived));
        Assert.All(result.Receipts, r => Assert.False(r.Reconciled));

        // And the sum itself is still right - "not reconciled" is a statement
        // about what was checked, not a licence to be wrong.
        var order = result.Receipts.Single(r => r.ExternalId == "A000TEST002");
        Assert.Equal(4248 + 1199, order.Total.Value);
    }

    [Fact]
    public async Task The_json_shape_is_a_configuration_edit_and_not_a_release()
    {
        var options = Options with { OrdersShape = BolOrdersShape.Json };
        var handler = new StubHttpHandler((_, _) => Stub.Fixture("bol/orders.json"));

        using var ctx = FetchContext(handler);

        var result = await Adapter(options).FetchAsync(ctx, Requests.Receipts(), CancellationToken.None);

        Assert.Equal("json", result.Via);
        Assert.Equal(3, result.Receipts.Count);
        Assert.Contains("/rnwy/api/account/orders", handler.Requests[0].Path, StringComparison.Ordinal);
        Assert.Contains("application/json", handler.Requests[0].Header("Accept"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_fetch_with_no_stored_cookies_asks_for_a_new_login()
    {
        var handler = new StubHttpHandler((_, _) => Stub.Status(HttpStatusCode.OK));

        using var ctx = new FakeJobContext(handler)
        {
            Material = new SessionMaterial { StorageState = LoginHostOnlyState },
        };

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().FetchAsync(ctx, Requests.Receipts(), CancellationToken.None));

        // session_expired, which asks the platform for a login job. There is
        // no credential during a fetch, so nothing here could recover it.
        Assert.Equal(ErrorCode.SessionExpired, error.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task An_unknown_resource_is_refused_without_a_request()
    {
        var handler = new StubHttpHandler((_, _) => Stub.Status(HttpStatusCode.OK));
        using var ctx = FetchContext(handler);

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().FetchAsync(
                ctx, new ResourceRequest { ResourceId = "transactions" }, CancellationToken.None));

        Assert.Equal(ErrorCode.UnsupportedResource, error.Code);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData((HttpStatusCode)429)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task Bot_protection_statuses_are_a_block_and_never_a_credential_error(HttpStatusCode status)
    {
        var handler = new StubHttpHandler((_, _) => Stub.Status(status));
        using var ctx = FetchContext(handler);

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().FetchAsync(ctx, Requests.Receipts(), CancellationToken.None));

        // Telling a user their password is wrong when the truth is bot
        // protection sends them to reset a password that was fine and leaves
        // the real block undiagnosed.
        Assert.Equal(ErrorCode.BlockedByProvider, error.Code);
        Assert.NotEqual(ErrorCode.InvalidCredentials, error.Code);
    }

    [Fact]
    public async Task A_rejected_session_is_session_expired_and_a_dead_upstream_is_unavailable()
    {
        var unauthorized = new StubHttpHandler((_, _) => Stub.Status(HttpStatusCode.Unauthorized));
        using (var ctx = FetchContext(unauthorized))
        {
            var error = await Assert.ThrowsAsync<ConnectorException>(
                () => Adapter().FetchAsync(ctx, Requests.Receipts(), CancellationToken.None));

            Assert.Equal(ErrorCode.SessionExpired, error.Code);
        }

        var broken = new StubHttpHandler((_, _) => Stub.Status(HttpStatusCode.InternalServerError));
        using (var ctx = FetchContext(broken))
        {
            var error = await Assert.ThrowsAsync<ConnectorException>(
                () => Adapter().FetchAsync(ctx, Requests.Receipts(), CancellationToken.None));

            // Retriable, because a 500 is bol failing rather than refusing.
            Assert.Equal(ErrorCode.ProviderUnavailable, error.Code);
            Assert.True(error.Retriable);
        }
    }

    [Fact]
    public async Task A_two_hundred_that_is_really_a_wall_is_still_a_block()
    {
        var handler = new StubHttpHandler((_, _) => Html(FixtureCatalog.Read("bol/bot-wall.html")));
        using var ctx = FetchContext(handler);

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().FetchAsync(ctx, Requests.Receipts(), CancellationToken.None));

        // bol runs no such vendor today - confirmed from response headers -
        // so this is defence against a change, and the change must not
        // present itself as a bad password.
        Assert.Equal(ErrorCode.BlockedByProvider, error.Code);
        Assert.Contains("cf-browser-verification", error.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_two_hundred_that_is_really_the_login_form_asks_for_a_new_login()
    {
        var handler = new StubHttpHandler((_, _) => Html(FixtureCatalog.Read("bol/login-interstitial.html")));
        using var ctx = FetchContext(handler);

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().FetchAsync(ctx, Requests.Receipts(), CancellationToken.None));

        // The cookie died. That is session_expired - a new login job - and not
        // a parser failure that would page an operator about a session which
        // simply ran out.
        Assert.Equal(ErrorCode.SessionExpired, error.Code);
        Assert.Contains("j_username", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_page_that_repeats_itself_stops_the_walk_instead_of_looping()
    {
        // bol's pagination parameter is a guess. Without this guard, a `page`
        // it ignores presents as the same page fetched twenty times against a
        // live account, which is the request pattern most likely to earn a
        // block.
        var handler = new StubHttpHandler((_, _) => Html(FixtureCatalog.Read("bol/orders-page.html")));
        using var ctx = FetchContext(handler);

        var result = await Adapter(Pages).FetchAsync(ctx, Requests.Receipts(), CancellationToken.None);

        Assert.Equal(3, result.Receipts.Count);
        Assert.Equal(2, handler.Requests.Count);
    }

    // ---- helpers ------------------------------------------------------------

    /// <summary>Every box a completed bol login would have shown.</summary>
    private static StubLoginPage SignedInPage() =>
        StubLoginPage.Showing(Username, PasswordBox, Submit);

    private static StubRedirectWaiter Arrives(int afterWaits = 0) =>
        new("https://www.bol.com/nl/nl/rnwy/account/bestellingen", afterWaits);

    private static StubRedirectWaiter Never() => new(redirect: null, afterWaits: 0);

    private static IReadOnlyDictionary<string, string> Credentials() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["username"] = Email,
            ["password"] = Password,
        };

    private static FakeJobContext LoginContext(
        StubLoginPage page, Func<Challenge, string>? answer = null, string? storageState = null) =>
        new()
        {
            Inputs = Credentials(),
            Browser = new BolBrowserStub(page, storageState),
            Answer = answer ?? (_ => Code),
        };

    private static FakeJobContext FetchContext(HttpMessageHandler handler) =>
        new(handler)
        {
            Material = new SessionMaterial { StorageState = FixtureCatalog.Read("bol/storage-state.json") },
        };

    private static HttpResponseMessage Html(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "text/html"),
        };

    /// <summary>A jar holding only the cookie login.bol.com sets, and nothing www.bol.com would receive.</summary>
    private const string LoginHostOnlyState =
        """{"cookies":[{"name":"JSESSIONID","value":"x","domain":"login.bol.com","path":"/","expires":-1}],"origins":[]}""";
}

/// <summary>
/// A browser lease that hands back a cookie jar, which for bol is the entire
/// credential.
///
/// Its own rather than the shared <c>StubBrowserLease</c>: that one refuses
/// <c>StorageStateAsync</c> outright, which is right for the token providers
/// and wrong here - bol's login produces nothing else. The redactor's rule is
/// reproduced exactly, so a test that gets picture bytes back has proved the
/// password left the DOM first.
/// </summary>
internal sealed class BolBrowserStub : IBrowserLease
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47];

    private readonly StubLoginPage _page;
    private readonly string _storageState;

    public BolBrowserStub(StubLoginPage page, string? storageState = null)
    {
        _page = page;
        _storageState = storageState ?? FixtureCatalog.Read("bol/storage-state.json");
    }

    public bool Started => true;

    public Task<string> StorageStateAsync(CancellationToken ct) => Task.FromResult(_storageState);

    public Task<byte[]> ScreenshotAsync(CropRegion? crop, CancellationToken ct)
    {
        _page.Record("screenshot");
        return Task.FromResult(_page.HoldsSecret ? Array.Empty<byte>() : Png);
    }

    public Task<IPage> PageAsync(CancellationToken ct) =>
        throw new InvalidOperationException("this test drives the page through the adapter's seam");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// A page that is nothing but its address.
///
/// The cheapest half of "are we in yet?" is the URL: while it still names
/// login.bol.com nothing has happened, and no cookie jar needs reading to
/// know it. This is the fixture for that half.
/// </summary>
internal sealed class BolPageAt : ILoginPage
{
    public BolPageAt(string url) => Url = url;

    public string Url { get; }

    public Task GotoAsync(string url, CancellationToken ct) => Task.CompletedTask;

    public Task ClearSecretsAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<PageMatch?> FindAsync(IReadOnlyList<string> selectors, int timeoutMs, CancellationToken ct) =>
        Task.FromResult<PageMatch?>(null);

    public Task<bool> FillAsync(IReadOnlyList<string> selectors, string value, int timeoutMs, CancellationToken ct) =>
        Task.FromResult(false);

    public Task<bool> ClickAsync(IReadOnlyList<string> selectors, int timeoutMs, CancellationToken ct) =>
        Task.FromResult(false);

    public Task<bool> AnswerAsync(IReadOnlyList<string> selectors, string value, int timeoutMs, CancellationToken ct) =>
        Task.FromResult(false);
}
