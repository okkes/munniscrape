using System.Net;
using System.Security.Cryptography;
using System.Text;
using Connector.Kit.Adapters;
using Connector.Kit.Challenges;
using Connector.Kit.Errors;
using Connector.Kit.Jobs;
using Connector.Kit.Manifests;
using Connector.Kit.Normalization;
using Connector.Kit.Security;
using ShopConnector.Adapters.Coolblue;
using ShopConnector.Adapters.Support;
using ShopConnector.Adapters.Tests.Support;
using Xunit;

namespace ShopConnector.Adapters.Tests;

/// <summary>
/// Coolblue, offline: no browser, no network, no credentials.
///
/// This provider is deliberately half-finished and the suite is shaped around
/// that split. The login is CONFIRMED from Coolblue's own OIDC discovery
/// document and its live redirect chain, so it is tested the way a login that
/// may only ever be attempted once has to be - the PKCE pair surviving a whole
/// page login, the state check on the callback, the credential latch, and every
/// path that must never become invalid_credentials.
///
/// The order fetch is CONFIRMED BY NOBODY, so what is tested is the refusal:
/// that it costs Coolblue no packet at all, that it names what to capture, and
/// that no amount of arming it lets an undeclared money unit through. The
/// parser is exercised against a SYNTHETIC fixture written to the shape the
/// options guess at - it proves the machinery, not the guess, and it says so.
/// </summary>
public sealed class CoolblueAdapterTests
{
    private const string Username = "shopper@example.com";
    private const string Password = "hunter2";
    private const string Code = "coolblue-authorization-code";

    private const string TokenPath = "/oauth/token";
    private const string UserInfoPath = "/oauth/userinfo";
    private const string OrdersPath = "/mijn-coolblue-account/api/orders";

    private static readonly DateTimeOffset Now = new(2026, 7, 28, 3, 0, 0, TimeSpan.Zero);

    private static readonly CoolblueOptions Options = new();

    /// <summary>The three boxes the login form is confirmed to carry.</summary>
    private static readonly string UsernameInput = Options.UsernameSelectors[0];
    private static readonly string PasswordInput = Options.PasswordSelectors[0];
    private static readonly string Submit = Options.SubmitSelectors[0];

    /// <summary>
    /// The fetch, armed exactly as an operator would arm it after a capture:
    /// an endpoint they watched, and both money units they read off it.
    /// </summary>
    private static readonly CoolblueOptions Armed = new()
    {
        OrdersEndpointConfirmed = true,
        OrdersUrl = "https://www.coolblue.nl" + OrdersPath,
        TotalUnit = MoneyUnit.MajorDecimal,
        ItemUnit = MoneyUnit.MajorDecimal,
    };

    private static CoolblueAdapter Adapter(CoolblueOptions? options = null) =>
        new(options ?? Options, new FixedTimeProvider(Now));

    // ---- the manifest ------------------------------------------------------

    [Fact]
    public void The_manifest_validates_as_an_unattended_refreshable_t2_provider()
    {
        var manifest = Adapter().Describe();

        // Throws with every failing rule listed. A manifest that cannot boot
        // the host must not pass the suite either.
        ManifestValidator.Validate(manifest);

        Assert.Equal("coolblue", manifest.Id);
        Assert.Equal(ProviderKind.Store, manifest.Kind);
        Assert.Equal("NL", manifest.Country);

        // T2: a browser drives one login and the refresh token serves the rest.
        // The validator requires an agent for any browser runtime.
        Assert.Equal(ProviderRuntime.BrowserOnce, manifest.Runtime);
        Assert.True(manifest.Agent.Required);
        Assert.Equal(AgentClass.Pooled, manifest.Agent.Class);

        // 'any', not 'residential', and that is a finding rather than an
        // oversight: no bot management was found anywhere on coolblue.nl, so
        // demanding a residential line would buy a real cost against a wall
        // nobody has seen.
        Assert.NotNull(manifest.Agent.Egress);
        Assert.Equal("NL", manifest.Agent.Egress.Country);
        Assert.Equal("any", manifest.Agent.Egress.Kind);

        // offline_access plus a refresh_token grant are CONFIRMED, and the
        // validator only allows unattended: true because of them.
        Assert.True(manifest.UnattendedFetch);
        Assert.True(manifest.Auth.Session.Refreshable);
        Assert.True(manifest.Auth.Session.RotatesOnUse);
        Assert.True(manifest.Auth.Reauth.Cheap);

        // UNCONFIRMED lifetime, stated conservatively. Under-promising makes a
        // consumer offer a reconnect slightly early; over-promising makes it
        // promise months of silent syncing and then fail every day.
        Assert.Equal(2_592_000, manifest.Auth.Session.TtlSeconds);

        Assert.Equal(SecretCustody.Client, manifest.SecretCustody);
        Assert.Equal(WebSupport.Ephemeral, manifest.WebSupport);
    }

    [Fact]
    public void The_manifest_says_in_its_notes_key_that_the_order_fetch_is_unproven()
    {
        var manifest = Adapter().Describe();

        // A key, never prose - the consuming app owns the copy in every
        // language it ships. The key still has to NAME the caveat, because a
        // consumer that renders it is what tells a user, before they connect,
        // that signing in will work and orders will not arrive yet.
        Assert.Equal("connect.coolblue.notes.orders_unproven", manifest.NotesKey);
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

    [Fact]
    public void The_manifest_asks_for_an_email_and_a_password_on_one_screen()
    {
        var manifest = Adapter().Describe();

        // One screen, both boxes - CONFIRMED. Not TwoStep: Coolblue is not
        // Lidl, and a consumer rendering a wizard for a single form would ask
        // the user to submit twice.
        Assert.Equal(AuthFlow.Password, manifest.Auth.Flow);

        var step = Assert.Single(manifest.Auth.Steps);
        Assert.Equal(2, step.Fields.Count);

        var username = Assert.Single(step.Fields, f => f.Key == "username");
        Assert.Equal(FieldType.Text, username.Type);
        Assert.False(username.Secret);
        Assert.True(username.Required);

        var password = Assert.Single(step.Fields, f => f.Key == "password");
        Assert.Equal(FieldType.Password, password.Type);
        Assert.True(password.Secret, "an unmarked password is logged and screenshotted");

        // Declared against the evidence rather than for it: no captcha was
        // found on this form. A consumer with no UI for a challenge its
        // provider suddenly raises strands the user at the worst moment.
        Assert.Equal(
            new[] { ChallengeType.Image, ChallengeType.AppApproval },
            manifest.Auth.Challenges);
    }

    [Fact]
    public void The_receipts_resource_matches_the_shape_every_shop_provider_offers()
    {
        var receipts = Adapter().Describe().Resource("receipts");

        Assert.NotNull(receipts);
        Assert.Equal(ResourceShape.Receipt, receipts.Returns);

        var since = receipts.Param("since");
        Assert.NotNull(since);
        Assert.Equal(ParamType.Date, since.Type);
        Assert.True(since.Required);

        var until = receipts.Param("until");
        Assert.NotNull(until);
        Assert.Equal(ParamType.Date, until.Type);

        var include = receipts.Param("include");
        Assert.NotNull(include);
        Assert.Equal(ParamType.Enum, include.Type);
        Assert.True(include.Multi);
        Assert.Equal(new[] { "items" }, include.Values);
    }

    // ---- the confirmed half: OIDC + PKCE -----------------------------------

    [Fact]
    public async Task The_authorize_call_carries_every_parameter_coolblues_own_client_sends()
    {
        var page = LoginForm();
        using var ctx = Context(new StubHttpHandler(Route));

        await Adapter().LoginAsync(ctx, page, Callback(page), CancellationToken.None);

        var url = Assert.Single(page.Visited);
        Assert.StartsWith("https://accounts.coolblue.nl/connect/authorize?", url, StringComparison.Ordinal);

        var query = Query(url);
        Assert.Equal("Webshop", query["client_id"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("https://www.coolblue.nl/inloggen/oidc", query["redirect_uri"]);
        Assert.Equal("nl", query["ui_locales"]);

        // offline_access is the whole T2 story: it is what yields the refresh
        // token, and the refresh token is what makes this provider unattended.
        Assert.Contains("offline_access", query["scope"], StringComparison.Ordinal);
        Assert.Contains("openid:customerid", query["scope"], StringComparison.Ordinal);

        // S256, never "plain" - plain puts the secret straight in the URL and
        // protects nothing.
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.NotEmpty(query["code_challenge"]);

        // Anti-replay values, and both are in Coolblue's own authorize request.
        Assert.NotEmpty(query["state"]);
        Assert.NotEmpty(query["nonce"]);
        Assert.NotEqual(query["state"], query["nonce"]);
    }

    [Fact]
    public async Task The_verifier_that_answers_the_challenge_is_the_one_replayed_at_the_exchange()
    {
        var page = LoginForm();
        var handler = new StubHttpHandler(Route);
        using var ctx = Context(handler);

        await Adapter().LoginAsync(ctx, page, Callback(page), CancellationToken.None);

        // The challenge went up with the authorize request...
        var challenge = Query(Assert.Single(page.Visited))["code_challenge"];

        // ...and the verifier came back down with the code, on the far side of
        // a whole browser login. Surviving that journey is the entire point:
        // the two halves are minted together and used minutes apart, and an
        // authorization server that recorded a challenge refuses an exchange
        // that produces no verifier.
        var exchange = Form(Assert.Single(handler.Requests, r => r.Path == TokenPath));

        Assert.True(exchange.TryGetValue("code_verifier", out var verifier),
            "the token exchange carried no code_verifier");
        Assert.Equal(challenge, PkceChallenge.For(verifier).Challenge);

        // Computed here from first principles rather than by calling the code
        // under test.
        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        Assert.Equal(expected, challenge);

        Assert.Equal("authorization_code", exchange["grant_type"]);
        Assert.Equal(Code, exchange["code"]);
        Assert.Equal("https://www.coolblue.nl/inloggen/oidc", exchange["redirect_uri"]);
    }

    [Fact]
    public async Task The_exchange_goes_to_the_oauth_token_endpoint_and_never_to_connect_token()
    {
        var handler = new StubHttpHandler(Route);
        using var ctx = Context(handler);
        var page = LoginForm();

        await Adapter().LoginAsync(ctx, page, Callback(page), CancellationToken.None);

        // Coolblue authorizes under /connect/ and takes tokens under /oauth/.
        // Both spellings came out of the same discovery document, and
        // "tidying" the token path to match the authorize path is a 404 that
        // costs a whole login - which on this platform is spent once.
        var exchange = Assert.Single(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.Equal("/oauth/token", exchange.Path);
        Assert.Equal("accounts.coolblue.nl", exchange.Uri.Host);
    }

    [Fact]
    public async Task A_public_client_names_itself_in_the_body_and_sends_no_basic_header()
    {
        var handler = new StubHttpHandler(Route);
        using var ctx = Context(handler);
        var page = LoginForm();

        await Adapter().LoginAsync(ctx, page, Callback(page), CancellationToken.None);

        var exchange = Assert.Single(handler.Requests, r => r.Path == TokenPath);

        // Webshop redirects to an https URI from a browser and sends S256
        // PKCE, which is the public-client shape - PKCE is what replaces a
        // secret. Sending an empty Basic header instead would name a client
        // the token endpoint does not have, which is exactly the bug that
        // failed every Lidl login before it was found.
        Assert.Null(exchange.Header("Authorization"));
        Assert.Equal("Webshop", Form(exchange)["client_id"]);
    }

    [Fact]
    public async Task A_configured_client_secret_switches_the_exchange_to_basic()
    {
        var options = Options with { ClientSecret = "not-a-real-secret" };
        var handler = new StubHttpHandler(Route);
        using var ctx = Context(handler);
        var page = LoginForm();

        await Adapter(options).LoginAsync(ctx, page, Callback(page), CancellationToken.None);

        var exchange = Assert.Single(handler.Requests, r => r.Path == TokenPath);
        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes("Webshop:not-a-real-secret"));

        // The override exists because only a live exchange can settle whether
        // this client is public or confidential.
        Assert.Equal($"Basic {expected}", exchange.Header("Authorization"));
        Assert.DoesNotContain("client_id", Form(exchange).Keys);
    }

    [Fact]
    public async Task The_login_seals_the_tokens_and_names_the_account_from_userinfo()
    {
        var page = LoginForm();
        using var ctx = Context(new StubHttpHandler(Route));

        var result = await Adapter().LoginAsync(ctx, page, Callback(page), CancellationToken.None);

        Assert.Equal("coolblue-access-token-fixture", result.Material.AccessToken);
        Assert.Equal("coolblue-refresh-token-fixture", result.Material.RefreshToken);
        Assert.Equal(Now.AddSeconds(3600 - 60), result.Material.AccessTokenExpiresAt);

        // So a user can tell two connections apart. openid:customerid is in
        // the confirmed scope list, so the customer id is the value most
        // likely to survive a name change.
        Assert.NotNull(result.Account);
        Assert.Equal("S. Shopper", result.Account.DisplayName);
        Assert.Equal("10293847", result.Account.ExternalId);

        // Deliberately unset: the access token's hour says nothing about how
        // long the CONNECTION lasts, because the refresh token outlives it.
        Assert.Null(result.ExpiresAt);

        Assert.Contains(JobStep.Authenticating, ctx.Steps);
        Assert.Contains(JobStep.Finalizing, ctx.Steps);
    }

    [Fact]
    public async Task A_userinfo_failure_never_fails_a_login_that_already_holds_tokens()
    {
        // The tokens are in hand and the credential latch has closed by the
        // time userinfo is called, so failing the login over a display label
        // would strand a user who has successfully signed in and whose login
        // is never retried.
        var handler = new StubHttpHandler((request, _) => request.Path == TokenPath
            ? Stub.Fixture("coolblue/token.json")
            : Stub.Status(HttpStatusCode.InternalServerError));

        using var ctx = Context(handler);
        var page = LoginForm();

        var result = await Adapter().LoginAsync(ctx, page, Callback(page), CancellationToken.None);

        Assert.Equal("coolblue-access-token-fixture", result.Material.AccessToken);
        Assert.NotNull(result.Account);
        Assert.Equal("Coolblue", result.Account.DisplayName);
        Assert.Null(result.Account.ExternalId);
    }

    [Fact]
    public async Task The_latch_closes_before_the_credentials_leave_the_machine()
    {
        var page = LoginForm();
        using var ctx = Context(new StubHttpHandler(Route));

        await Adapter().LoginAsync(ctx, page, Callback(page), CancellationToken.None);

        // After this a lost lease fails the job rather than requeuing it: a
        // retried login is how an account gets locked.
        Assert.True(ctx.CredentialWasSubmitted);
    }

    [Theory]
    [InlineData("username")]
    [InlineData("password")]
    public async Task A_login_missing_a_credential_is_refused_before_the_page_is_touched(string missing)
    {
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["username"] = Username,
            ["password"] = Password,
        };
        inputs.Remove(missing);

        var page = LoginForm();
        var handler = new StubHttpHandler(Route);
        using var ctx = new FakeJobContext(handler) { Inputs = inputs };

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().LoginAsync(ctx, page, Callback(page), CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidRequest, error.Code);
        Assert.False(ctx.CredentialWasSubmitted);
        Assert.Empty(page.Visited);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("username field")]
    [InlineData("password field")]
    [InlineData("login submit")]
    public async Task A_missing_element_on_the_login_form_is_a_shape_change_that_names_itself(string expected)
    {
        // Everything except the one under test, so a filler willing to settle
        // for "some input" would find one.
        var page = expected switch
        {
            "username field" => StubLoginPage.Showing(PasswordInput, Submit),
            "password field" => StubLoginPage.Showing(UsernameInput, Submit),
            _ => StubLoginPage.Showing(UsernameInput, PasswordInput),
        };

        var handler = new StubHttpHandler(Route);
        using var ctx = Context(handler);

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().LoginAsync(ctx, page, Callback(page), CancellationToken.None));

        // Named with the candidate list, so the next breakage arrives already
        // diagnosed - and never invalid_credentials, which is permanent.
        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains(expected, error.Detail, StringComparison.Ordinal);

        // Nothing was exchanged either way.
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_callback_carrying_someone_elses_state_is_refused_and_the_code_is_never_exchanged()
    {
        var page = LoginForm();
        var handler = new StubHttpHandler(Route);
        using var ctx = Context(handler);

        // The authorization server answers with a state this login did not
        // mint - a cross-session mix-up, or a callback that is not ours.
        var waiter = new CallbackWaiter(page, forcedState: "not-the-state-we-minted");

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().LoginAsync(ctx, page, waiter, CancellationToken.None));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains("state", error.Detail, StringComparison.Ordinal);

        // The whole point of minting a state: a code that is not ours to spend
        // is not spent at all rather than spent hopefully.
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task An_error_on_the_callback_is_a_shape_change_and_never_invalid_credentials()
    {
        var page = LoginForm();
        var handler = new StubHttpHandler(Route);
        using var ctx = Context(handler);

        var waiter = new CallbackWaiter(page,
            url: "https://www.coolblue.nl/inloggen/oidc?error=invalid_scope&error_description=unknown+scope");

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().LoginAsync(ctx, page, waiter, CancellationToken.None));

        // A wrong password never leaves the login form - the authorization
        // server only reaches the callback once it has accepted the human - so
        // an error here is about this client, redirect URI or scope list.
        // Reporting invalid_credentials would send the user to reset a
        // password that was fine, permanently, since it is never retried.
        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.NotEqual(ErrorCode.InvalidCredentials, error.Code);
        Assert.Contains("invalid_scope", error.Detail, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_page_that_states_a_credential_error_is_the_only_route_to_invalid_credentials()
    {
        var page = StubLoginPage.Showing(
            UsernameInput, PasswordInput, Submit, Options.LoginErrorSelectors[0]);

        var handler = new StubHttpHandler(Route);
        using var ctx = Context(handler);

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().LoginAsync(ctx, page, Never(), CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidCredentials, error.Code);

        // It really did go upstream, which is what makes this reading honest
        // and what stops anything from retrying it.
        Assert.True(ctx.CredentialWasSubmitted);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Silence_after_the_submit_is_a_shape_change_and_never_a_guess_at_the_password()
    {
        // No redirect, no stated error, no wall: the login simply did not
        // resolve. Guessing "wrong password" from silence is the mistake this
        // branch exists to refuse.
        //
        // The settle budget is zeroed because the suite's clock does not move:
        // the loop ends on a deadline, which is exactly right against a real
        // TimeProvider and never reached against a frozen one. Zero seconds
        // takes the same branch on the first pass.
        var options = Options with { LoginSettleSeconds = 0 };

        var page = LoginForm();
        using var ctx = Context(new StubHttpHandler(Route));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter(options).LoginAsync(ctx, page, Never(), cts.Token));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.NotEqual(ErrorCode.InvalidCredentials, error.Code);
        Assert.Contains("neither redirected", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unattended_interactive_captcha_is_refused_at_once_and_nobody_is_asked()
    {
        // None has ever been observed on this form. The probe is here so that
        // if one appears the adapter names it in one poll instead of waiting
        // out a whole job budget and then reporting the wrong thing.
        var page = StubLoginPage.Showing(
            UsernameInput, PasswordInput, Submit, Options.InteractiveCaptchaSelectors[0]);

        using var ctx = new FakeJobContext(new StubHttpHandler(Route))
        {
            Inputs = Credentials(),
            Browser = new StubBrowserLease(page),
        };

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().LoginAsync(ctx, page, Never(), CancellationToken.None));

        // Blocked, not internal and not invalid_credentials: nothing is wrong
        // with the password. There is simply nobody at this browser.
        Assert.Equal(ErrorCode.BlockedByProvider, error.Code);
        Assert.Contains("unattended", error.Detail, StringComparison.Ordinal);

        // Not one question, because there is nobody to answer it.
        Assert.Empty(ctx.Asked);

        // The password is off the page even though nothing photographed it:
        // the runner may capture an artifact on the way out of a failed job.
        Assert.False(page.HoldsSecret);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData((HttpStatusCode)429)]
    public async Task A_wall_on_the_token_endpoint_is_blocked_and_never_a_dead_session(HttpStatusCode status)
    {
        var handler = new StubHttpHandler((_, _) => Stub.Status(status));
        using var ctx = Context(handler);
        var page = LoginForm();

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().LoginAsync(ctx, page, Callback(page), CancellationToken.None));

        // A wall answers a token endpoint the way it answers anything else.
        // Calling it a dead session would send the user round a reconnect loop
        // that cannot succeed; calling it a wrong password would be worse.
        Assert.Equal(ErrorCode.BlockedByProvider, error.Code);
    }

    [Fact]
    public async Task The_browser_session_is_sealed_alongside_the_tokens()
    {
        // Which credential the order call needs is unknown, and this is the one
        // that cannot be recovered later without a second login: the tokens can
        // always be refreshed, but a cookie not captured at login time is gone.
        const string state = """{"cookies":[{"name":"Coolblue-Session","value":"x","domain":".coolblue.nl"}]}""";

        using var ctx = new FakeJobContext { Browser = new StorageOnlyLease(state) };

        Assert.Equal(state, await Adapter().StorageStateAsync(ctx, CancellationToken.None));

        // Off by option, and absent when no browser was ever started.
        Assert.Null(await Adapter(Options with { CaptureStorageState = false })
            .StorageStateAsync(ctx, CancellationToken.None));

        using var noBrowser = new FakeJobContext();
        Assert.Null(await Adapter().StorageStateAsync(noBrowser, CancellationToken.None));
    }

    // ---- the unproven half: the refusal ------------------------------------

    [Fact]
    public async Task An_unconfigured_fetch_refuses_before_a_single_packet_leaves()
    {
        // No material either: the refusal must be identical whether or not this
        // deployment happens to hold a session, because the endpoint being
        // unknown has nothing to do with the user.
        using var ctx = new FakeJobContext();

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().FetchAsync(ctx, Requests.Receipts(), CancellationToken.None));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Equal(0, ((ThrowingHttpHandler)ctx.Handler).Calls);
    }

    [Fact]
    public async Task The_refusal_names_exactly_what_to_capture_and_which_option_it_fills_in()
    {
        using var ctx = new FakeJobContext();

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().FetchAsync(ctx, Requests.Receipts(), CancellationToken.None));

        var detail = error.Detail;
        Assert.NotNull(detail);

        // Where to look...
        Assert.Contains("mijn-coolblue-account/bestellingen", detail, StringComparison.Ordinal);
        Assert.Contains("Network tab", detail, StringComparison.Ordinal);

        // ...what to write down, named as the option it fills in...
        Assert.Contains("OrdersUrl", detail, StringComparison.Ordinal);
        Assert.Contains("OrderPaths", detail, StringComparison.Ordinal);
        Assert.Contains("ExtraHeaders", detail, StringComparison.Ordinal);
        Assert.Contains("Credential = Cookie", detail, StringComparison.Ordinal);
        Assert.Contains("TotalUnit", detail, StringComparison.Ordinal);

        // ...the case this adapter cannot handle at all...
        Assert.Contains("server-rendered HTML", detail, StringComparison.Ordinal);

        // ...and the one mistake reconciliation cannot catch.
        Assert.Contains("cents", detail, StringComparison.Ordinal);
        Assert.Contains("hundredth", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Arming_the_fetch_without_declaring_the_money_unit_is_refused()
    {
        // The one value a plausible-looking sync can never expose.
        // Reconciliation compares a total against its own lines, so it catches
        // an inconsistent PAIR and never a consistently wrong one: a total and
        // its lines both misread as cents reconcile perfectly at a hundredth of
        // the real money, silently, forever.
        var options = Options with
        {
            OrdersEndpointConfirmed = true,
            OrdersUrl = "https://www.coolblue.nl" + OrdersPath,
        };

        using var ctx = Fetching(new StubHttpHandler((_, _) => Stub.Fixture("coolblue/orders-hypothetical.json")));

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter(options).FetchAsync(ctx, Requests.Receipts(items: false), CancellationToken.None));

        // Ours, not theirs: an operator armed the fetch without finishing its
        // configuration, so it must not degrade the provider or tell a user to
        // reconnect.
        Assert.Equal(ErrorCode.Internal, error.Code);
        Assert.Contains("total_unit", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Asking_for_items_without_an_item_unit_is_refused_too()
    {
        // Its own declaration, because an API that states a total in one unit
        // and its lines in another is not hypothetical.
        var options = Armed with { ItemUnit = null };

        using var ctx = Fetching(new StubHttpHandler((_, _) => Stub.Fixture("coolblue/orders-hypothetical.json")));

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter(options).FetchAsync(ctx, Requests.Receipts(), CancellationToken.None));

        Assert.Equal(ErrorCode.Internal, error.Code);
        Assert.Contains("item_unit", error.Detail, StringComparison.Ordinal);

        // Totals alone are still servable: only the items needed the missing
        // declaration.
        var totalsOnly = await Adapter(options)
            .FetchAsync(ctx, Requests.Receipts(items: false), CancellationToken.None);
        Assert.Equal(3, totalsOnly.Receipts.Count);
    }

    [Fact]
    public async Task An_unknown_resource_is_unsupported()
    {
        using var ctx = new FakeJobContext();

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter(Armed).FetchAsync(
                ctx, new ResourceRequest { ResourceId = "invoices" }, CancellationToken.None));

        Assert.Equal(ErrorCode.UnsupportedResource, error.Code);
    }

    // ---- parsing, against a fixture that says it is synthetic ---------------

    [Fact]
    public async Task An_order_page_normalises_into_receipts_with_offsets_items_and_discounts()
    {
        using var ctx = Fetching(new StubHttpHandler((_, _) => Stub.Fixture("coolblue/orders-hypothetical.json")));

        var result = await Adapter(Armed).FetchAsync(ctx, Requests.Receipts(), CancellationToken.None);

        Assert.Equal("orders-json/bearer", result.Via);
        Assert.True(result.Complete);
        Assert.Equal(3, result.Receipts.Count);

        // Newest first.
        var laptop = result.Receipts[0];
        Assert.Equal("20260719-4711", laptop.ExternalId);
        Assert.Equal("coolblue", laptop.Merchant.Id);
        Assert.Equal("Coolblue", laptop.Merchant.Name);
        Assert.Null(laptop.Merchant.StoreName);

        // A real offset, stated by the payload and honoured rather than
        // reinterpreted.
        Assert.Equal(new DateTimeOffset(2026, 7, 19, 17, 42, 0, TimeSpan.FromHours(2)), laptop.PurchasedAt);

        // MajorDecimal, DECLARED on the options and never sniffed from the
        // value's shape: 1148.90 euros is 114890 minor units.
        Assert.Equal(114_890, laptop.Total.Value);
        Assert.Equal("EUR", laptop.Total.Currency);

        Assert.Equal(2, laptop.Items.Count);
        var mac = laptop.Items[0];
        Assert.Equal("Apple MacBook Air 13 inch M4 16GB/256GB", mac.Name);
        Assert.Equal(1m, mac.Quantity);
        Assert.Equal(114_900, mac.Total.Value);
        Assert.NotNull(mac.UnitPrice);
        Assert.Equal(114_900, mac.UnitPrice.Value.Value);

        // Negative by contract whichever sign Coolblue chose, so that summing a
        // receipt is unconditional.
        Assert.NotNull(mac.Discount);
        Assert.Equal(-5_000, mac.Discount.Amount.Value);
        Assert.Equal("Actiekorting", mac.Discount.Label);

        Assert.Equal("card", laptop.Payment?.Method);
        Assert.Equal("4242", laptop.Payment?.CardLast4);

        // Items plus discounts sum to the stated total.
        Assert.True(laptop.Reconciled);
        Assert.NotEmpty(laptop.ContentHash);
    }

    [Fact]
    public async Task A_bare_wall_clock_and_a_bare_date_are_both_read_in_europe_amsterdam()
    {
        using var ctx = Fetching(new StubHttpHandler((_, _) => Stub.Fixture("coolblue/orders-hypothetical.json")));

        var result = await Adapter(Armed).FetchAsync(ctx, Requests.Receipts(), CancellationToken.None);

        // Coolblue has never been observed stating an offset, so a wall clock
        // is interpreted in the provider's zone rather than the agent's - a
        // near-midnight order dated a day out is worse than one never matched.
        var hue = result.Receipts[1];
        Assert.Equal(new DateTimeOffset(2026, 7, 3, 9, 15, 0, TimeSpan.FromHours(2)), hue.PurchasedAt);

        // And a bare DATE gets a real offset too, at that date's own zone rule:
        // February is +01:00, not the +02:00 the summer rows carry.
        var sonos = result.Receipts[2];
        Assert.Equal(new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.FromHours(1)), sonos.PurchasedAt);
        Assert.NotEqual(TimeSpan.Zero, sonos.PurchasedAt.Offset);
    }

    [Fact]
    public async Task An_order_level_discount_becomes_its_own_negative_line_and_the_store_survives()
    {
        using var ctx = Fetching(new StubHttpHandler((_, _) => Stub.Fixture("coolblue/orders-hypothetical.json")));

        var result = await Adapter(Armed).FetchAsync(ctx, Requests.Receipts(), CancellationToken.None);
        var hue = result.Receipts[1];

        // Nothing links an order-level discount to a product, so it becomes its
        // own line rather than being attached to a guess. Both readings
        // reconcile identically; only one invents a fact the order never stated.
        Assert.Equal(2, hue.Items.Count);
        Assert.Equal(11_995, hue.Items[0].Total.Value);
        Assert.Equal("Kortingscode ZOMER10", hue.Items[1].Name);
        Assert.Equal(-1_000, hue.Items[1].Total.Value);

        Assert.Equal(10_995, hue.Total.Value);
        Assert.True(hue.Reconciled);

        Assert.Equal("Coolblue Rotterdam Beurs", hue.Merchant.StoreName);
        Assert.Equal("ideal", hue.Payment?.Method);
        Assert.Null(hue.Payment?.CardLast4);
    }

    [Fact]
    public async Task A_receipt_whose_lines_do_not_sum_to_its_total_is_flagged_and_never_dropped()
    {
        using var ctx = Fetching(new StubHttpHandler((_, _) => Stub.Fixture("coolblue/orders-hypothetical.json")));

        var result = await Adapter(Armed).FetchAsync(ctx, Requests.Receipts(), CancellationToken.None);
        var sonos = result.Receipts[2];

        // Dropping it would hide a real purchase; trusting it silently would
        // hand over a total we know is inconsistent with its own contents. The
        // consumer decides, having been told.
        Assert.Equal("20260228-4501", sonos.ExternalId);
        Assert.False(sonos.Reconciled);
        Assert.Equal(27_900, sonos.Total.Value);
        Assert.Equal(24_900, Assert.Single(sonos.Items).Total.Value);
    }

    [Fact]
    public async Task The_money_unit_is_declared_per_field_and_never_sniffed_from_the_value()
    {
        var handler = new StubHttpHandler((_, _) => Stub.Fixture("coolblue/orders-hypothetical.json"));

        using var major = Fetching(handler);
        var asEuros = await Adapter(Armed).FetchAsync(ctx: major, Requests.Receipts(items: false), CancellationToken.None);

        using var minor = Fetching(handler);
        var asCents = await Adapter(Armed with { TotalUnit = MoneyUnit.Minor })
            .FetchAsync(minor, Requests.Receipts(items: false), CancellationToken.None);

        // The same bytes, read two ways, because the unit comes from the
        // declaration and nothing else. There is no overload anywhere in this
        // service that inspects the value and decides - which is the whole
        // reason the wrong declaration has to be caught by a human reading a
        // capture rather than by a sync that looks fine.
        Assert.Equal(114_890, asEuros.Receipts[0].Total.Value);
        Assert.Equal(1_148, asCents.Receipts[0].Total.Value);
    }

    [Fact]
    public async Task A_window_narrows_the_pass_without_touching_the_provider_twice()
    {
        using var ctx = Fetching(new StubHttpHandler((_, _) => Stub.Fixture("coolblue/orders-hypothetical.json")));

        var result = await Adapter(Armed).FetchAsync(
            ctx, Requests.Receipts(since: Requests.Day(2026, 7, 1)), CancellationToken.None);

        Assert.Equal(2, result.Receipts.Count);
        Assert.DoesNotContain(result.Receipts, r => r.ExternalId == "20260228-4501");
        Assert.Single(ctx.Requests());
    }

    // ---- the parse layer refusing to invent --------------------------------

    [Fact]
    public async Task A_response_with_no_order_array_names_every_path_it_tried()
    {
        // The realistic first failure of an endpoint nobody has captured: the
        // call answers, and it is not shaped the way the guesses assume.
        using var ctx = Fetching(new StubHttpHandler((_, _) => Stub.Json("""{"data":{"purchases":[]}}""")));

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter(Armed).FetchAsync(ctx, Requests.Receipts(items: false), CancellationToken.None));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains("data.orders", error.Detail, StringComparison.Ordinal);
        Assert.Contains("order_paths", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_order_that_carries_no_id_is_a_shape_change_and_is_never_skipped()
    {
        var payload = Fixture.Object("coolblue/orders-hypothetical.json");
        payload["data"]!["orders"]![0]!.AsObject().Remove("orderNumber");

        using var ctx = Fetching(new StubHttpHandler((_, _) => Stub.Json(payload.ToJsonString())));

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter(Armed).FetchAsync(ctx, Requests.Receipts(items: false), CancellationToken.None));

        // Refused rather than skipped. On a payload nobody has ever seen, a row
        // without an id is far likelier to mean a path matched some other array
        // than to mean one odd order - and a parser that skips its way to an
        // empty list reports a shape change as an empty history.
        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains("carries no id", error.Detail, StringComparison.Ordinal);
        Assert.Contains("orderNumber", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_order_that_states_no_total_is_a_shape_change()
    {
        var payload = Fixture.Object("coolblue/orders-hypothetical.json");
        payload["data"]!["orders"]![0]!.AsObject().Remove("totalAmount");

        using var ctx = Fetching(new StubHttpHandler((_, _) => Stub.Json(payload.ToJsonString())));

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter(Armed).FetchAsync(ctx, Requests.Receipts(items: false), CancellationToken.None));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains("total", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_order_that_states_no_date_is_a_shape_change()
    {
        var payload = Fixture.Object("coolblue/orders-hypothetical.json");
        payload["data"]!["orders"]![0]!.AsObject().Remove("orderDate");

        using var ctx = Fetching(new StubHttpHandler((_, _) => Stub.Json(payload.ToJsonString())));

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter(Armed).FetchAsync(ctx, Requests.Receipts(items: false), CancellationToken.None));

        // Never a bare "now" and never the agent's own clock: a receipt with an
        // invented date is worse than no receipt.
        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains("date", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Items_asked_for_and_nowhere_to_be_found_is_a_shape_change()
    {
        var payload = Fixture.Object("coolblue/orders-hypothetical.json");
        foreach (var order in payload["data"]!["orders"]!.AsArray())
        {
            order!.AsObject().Remove("orderLines");
        }

        using var ctx = Fetching(new StubHttpHandler((_, _) => Stub.Json(payload.ToJsonString())));

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter(Armed).FetchAsync(ctx, Requests.Receipts(), CancellationToken.None));

        // Emitting the receipts anyway would hand the consumer totals that
        // reconcile trivially - an empty item list always reconciles - which is
        // the most convincing kind of wrong.
        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains("items_names", error.Detail, StringComparison.Ordinal);
    }

    // ---- transport: walls, sessions and interstitials -----------------------

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData((HttpStatusCode)429)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task Bot_protection_statuses_are_blocked_and_never_invalid_credentials(HttpStatusCode status)
    {
        using var ctx = Fetching(new StubHttpHandler((_, _) => Stub.Status(status)));

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter(Armed).FetchAsync(ctx, Requests.Receipts(items: false), CancellationToken.None));

        // 429 is in this set on purpose and the shared default does not put it
        // there: Coolblue sits behind CloudFront, an AWS WAF rate rule answers
        // 429 with no Retry-After to honour, and treating a wall as "try again
        // shortly" is how a client earns a permanent one.
        Assert.Equal(ErrorCode.BlockedByProvider, error.Code);
        Assert.NotEqual(ErrorCode.InvalidCredentials, error.Code);
    }

    [Fact]
    public async Task An_interstitial_where_json_was_promised_is_blocked_rather_than_a_parse_failure()
    {
        const string wall =
            "<html><head><title>ERROR: The request could not be satisfied</title></head>" +
            "<body>Request blocked. Generated by cloudfront (CloudFront)</body></html>";

        using var ctx = Fetching(new StubHttpHandler((_, _) => Html(wall)));

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter(Armed).FetchAsync(ctx, Requests.Receipts(items: false), CancellationToken.None));

        Assert.Equal(ErrorCode.BlockedByProvider, error.Code);
    }

    [Fact]
    public async Task A_bounce_to_the_login_page_is_a_dead_session_rather_than_a_wall()
    {
        // The two readings of an HTML body want opposite things from the user:
        // sign in again, or stop and wait. The login reading is tried first
        // because Coolblue's own pages legitimately mention their CDN.
        const string login =
            "<html><body><form action=\"https://accounts.coolblue.nl/connect/authorize\">" +
            "<input name=\"csrf\" value=\"x\"></form></body></html>";

        using var ctx = Fetching(new StubHttpHandler((_, _) => Html(login)));

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter(Armed).FetchAsync(ctx, Requests.Receipts(items: false), CancellationToken.None));

        Assert.Equal(ErrorCode.SessionExpired, error.Code);
    }

    [Fact]
    public async Task A_401_is_refreshed_once_and_the_rotated_material_comes_back()
    {
        var handler = new StubHttpHandler((request, index) =>
        {
            if (request.Path == TokenPath) return Stub.Fixture("coolblue/token.json");
            return index == 0
                ? Stub.Status(HttpStatusCode.Unauthorized)
                : Stub.Fixture("coolblue/orders-hypothetical.json");
        });

        using var ctx = Fetching(handler);

        var result = await Adapter(Armed).FetchAsync(ctx, Requests.Receipts(items: false), CancellationToken.None);

        Assert.Equal(3, result.Receipts.Count);

        // The caller must persist the new bundle: a refresh that is not sealed
        // back is a refresh token spent for nothing.
        Assert.NotNull(result.RefreshedMaterial);
        Assert.Equal("coolblue-access-token-fixture", result.RefreshedMaterial.AccessToken);
        Assert.Equal("coolblue-refresh-token-fixture", result.RefreshedMaterial.RefreshToken);

        var refresh = Form(Assert.Single(handler.Requests, r => r.Path == TokenPath));
        Assert.Equal("refresh_token", refresh["grant_type"]);
        Assert.Equal("stored-refresh-token", refresh["refresh_token"]);
    }

    [Fact]
    public async Task A_401_that_survives_the_refresh_is_a_dead_session()
    {
        var handler = new StubHttpHandler((request, _) => request.Path == TokenPath
            ? Stub.Fixture("coolblue/token.json")
            : Stub.Status(HttpStatusCode.Unauthorized));

        using var ctx = Fetching(handler);

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter(Armed).FetchAsync(ctx, Requests.Receipts(items: false), CancellationToken.None));

        // One refresh, one retry. Repeatedly presenting a dead session is how a
        // provider starts treating a client as hostile.
        Assert.Equal(ErrorCode.SessionExpired, error.Code);
    }

    [Fact]
    public async Task A_cookie_mode_fetch_sends_the_stored_browser_session_and_never_a_bearer()
    {
        var options = Armed with { Credential = CoolblueOrderCredential.Cookie };
        var handler = new StubHttpHandler((_, _) => Stub.Fixture("coolblue/orders-hypothetical.json"));

        using var ctx = new FakeJobContext(handler)
        {
            Material = new SessionMaterial
            {
                StorageState =
                    """
                    {"cookies":[
                      {"name":"Coolblue-Session","value":"session-fixture","domain":".coolblue.nl","expires":-1},
                      {"name":"stale","value":"x","domain":".coolblue.nl","expires":1},
                      {"name":"elsewhere","value":"x","domain":".example.test"}
                    ]}
                    """,
            },
        };

        var result = await Adapter(options).FetchAsync(ctx, Requests.Receipts(items: false), CancellationToken.None);

        Assert.Equal("orders-json/cookie", result.Via);

        var call = Assert.Single(handler.Requests);
        var cookie = call.Header("Cookie");

        // The session cookie a login issues has a negative expiry and must not
        // be filtered out as "already expired"; a genuinely stale one and
        // another domain's must not travel at all.
        Assert.Equal("Coolblue-Session=session-fixture", cookie);
        Assert.Null(call.Header("Authorization"));

        // Nothing to exchange a cookie for, so nothing rotated.
        Assert.Null(result.RefreshedMaterial);
    }

    [Fact]
    public async Task A_cookie_mode_fetch_with_no_stored_session_is_a_dead_session()
    {
        var options = Armed with { Credential = CoolblueOrderCredential.Cookie };
        var handler = new StubHttpHandler((_, _) => Stub.Fixture("coolblue/orders-hypothetical.json"));

        using var ctx = new FakeJobContext(handler) { Material = new SessionMaterial { AccessToken = "irrelevant" } };

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter(options).FetchAsync(ctx, Requests.Receipts(items: false), CancellationToken.None));

        Assert.Equal(ErrorCode.SessionExpired, error.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task The_captured_headers_travel_with_the_order_call()
    {
        var options = Armed with
        {
            ExtraHeaders = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["x-csrf-token"] = "captured-token",
            },
        };

        var handler = new StubHttpHandler((_, _) => Stub.Fixture("coolblue/orders-hypothetical.json"));
        using var ctx = Fetching(handler);

        await Adapter(options).FetchAsync(ctx, Requests.Receipts(items: false), CancellationToken.None);

        var call = Assert.Single(handler.Requests);

        // A _csrfSecret cookie and a csrf hidden field are CONFIRMED on the
        // login form, so Coolblue does use CSRF tokens; whether a read needs
        // one is exactly what the capture settles.
        Assert.Equal("captured-token", call.Header("x-csrf-token"));
        Assert.Equal("Bearer stored-access-token", call.Header("Authorization"));
        Assert.Equal("/mijn-coolblue-account/api/orders", call.Path);
        Assert.Equal(HttpMethod.Get, call.Method);
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// The provider, answering from its recorded token and userinfo payloads.
    /// A login reaches nothing else.
    /// </summary>
    private static HttpResponseMessage Route(RecordedRequest request, int _) => request.Path switch
    {
        TokenPath => Stub.Fixture("coolblue/token.json"),
        UserInfoPath => Stub.Fixture("coolblue/userinfo.json"),
        _ => Stub.Status(HttpStatusCode.NotFound),
    };

    private static HttpResponseMessage Html(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    /// <summary>Every box the confirmed form carries, all on one screen.</summary>
    private static StubLoginPage LoginForm() =>
        StubLoginPage.Showing(UsernameInput, PasswordInput, Submit);

    /// <summary>
    /// The authorization server answering the way one does: echoing back the
    /// state this login minted, which is the only thing that makes the
    /// callback ours to spend.
    /// </summary>
    private static CallbackWaiter Callback(StubLoginPage page) => new(page);

    /// <summary>A login that never resolves - no redirect, ever.</summary>
    private static CallbackWaiter Never() => new(page: null);

    private static IReadOnlyDictionary<string, string> Credentials() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["username"] = Username,
            ["password"] = Password,
        };

    private static FakeJobContext Context(HttpMessageHandler handler) =>
        new(handler) { Inputs = Credentials() };

    /// <summary>A job holding the session a completed login would have sealed.</summary>
    private static FakeJobContext Fetching(HttpMessageHandler handler) =>
        new(handler)
        {
            Material = new SessionMaterial
            {
                AccessToken = "stored-access-token",
                RefreshToken = "stored-refresh-token",
            },
        };

    private static IReadOnlyDictionary<string, string> Query(string url) =>
        Pairs(new Uri(url).Query.TrimStart('?'));

    private static IReadOnlyDictionary<string, string> Form(RecordedRequest request)
    {
        Assert.NotNull(request.Body);
        return Pairs(request.Body);
    }

    /// <summary>
    /// A form body or a query string, unescaped. Hand-rolled rather than pulled
    /// from a web stack: the assertions here are about exactly which characters
    /// went onto the wire, so the reader wants to see the decoding rather than
    /// trust a helper's opinion of it.
    /// </summary>
    private static Dictionary<string, string> Pairs(string encoded)
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

    /// <summary>
    /// The callback, built the way an authorization server builds one.
    ///
    /// It reads the state out of the authorize URL the adapter actually
    /// navigated to, because a stub that invented its own would test nothing:
    /// the state check exists precisely to reject a callback that does not
    /// belong to this login, and only a waiter that echoes can prove the happy
    /// path still works.
    /// </summary>
    private sealed class CallbackWaiter : IRedirectWaiter
    {
        private readonly StubLoginPage? _page;
        private readonly string? _forcedState;
        private readonly string? _url;

        public CallbackWaiter(StubLoginPage? page, string? forcedState = null, string? url = null)
        {
            _page = page;
            _forcedState = forcedState;
            _url = url;
        }

        public int Waits { get; private set; }

        public Task<string?> WaitAsync(TimeSpan timeout, CancellationToken ct)
        {
            Waits++;

            if (_url is not null) return Task.FromResult<string?>(_url);
            if (_page is null) return Task.FromResult<string?>(null);

            var state = _forcedState ?? Query(_page.Visited[0])["state"];

            return Task.FromResult<string?>(
                "https://www.coolblue.nl/inloggen/oidc" +
                $"?code={Code}&state={Uri.EscapeDataString(state)}&session_state=opaque");
        }
    }

    /// <summary>
    /// A lease that has a storage state and nothing else. The shared stub
    /// refuses this call, correctly - it exists for tests that must not start a
    /// browser - but sealing the browser session away IS the behaviour under
    /// test here.
    /// </summary>
    private sealed class StorageOnlyLease : IBrowserLease
    {
        private readonly string _state;

        public StorageOnlyLease(string state) => _state = state;

        public bool Started => true;

        public Task<string> StorageStateAsync(CancellationToken ct) => Task.FromResult(_state);

        public Task<Microsoft.Playwright.IPage> PageAsync(CancellationToken ct) => throw Refused();

        public Task<byte[]> ScreenshotAsync(CropRegion? crop, CancellationToken ct) => throw Refused();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static InvalidOperationException Refused() => new("this test must not start a browser");
    }
}

internal static class CoolblueTestExtensions
{
    /// <summary>The calls a job's stub handler actually recorded.</summary>
    public static IReadOnlyList<RecordedRequest> Requests(this FakeJobContext ctx) =>
        ctx.Handler is StubHttpHandler stub ? stub.Requests : [];
}
