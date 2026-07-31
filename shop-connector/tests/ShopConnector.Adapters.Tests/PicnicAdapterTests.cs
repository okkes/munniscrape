using System.Net;
using Connector.Kit.Challenges;
using Connector.Kit.Errors;
using Connector.Kit.Jobs;
using Connector.Kit.Manifests;
using Connector.Kit.Security;
using ShopConnector.Adapters.Fixtures;
using ShopConnector.Adapters.Picnic;
using ShopConnector.Adapters.Tests.Support;
using Xunit;

namespace ShopConnector.Adapters.Tests;

/// <summary>
/// Picnic end to end, offline.
///
/// Everything this provider does is plain JSON over HTTPS - there is no
/// browser, no OAuth redirect and no captcha - so unlike every other store in
/// this suite the WHOLE adapter is reachable from a stubbed handler, the login
/// included. That is worth spending: a login is the one path a real account may
/// only survive being got wrong once.
/// </summary>
public sealed class PicnicAdapterTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 26, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The password these tests type, and its MD5 - which is the value that actually goes upstream.</summary>
    private const string Password = "hunter2";

    private const string PasswordDigest = "2ab96390c7dbe3439de74d0c9b0b1767";

    private static readonly IReadOnlyDictionary<string, string> DutchConfig =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["country"] = "NL" };

    private static readonly IReadOnlyDictionary<string, string> Credentials =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["username"] = "shopper@example.com",
            ["password"] = Password,
        };

    private static PicnicAdapter Adapter() => new(new PicnicOptions(), new FixedTimeProvider(Now));

    private static HttpResponseMessage WithToken(HttpResponseMessage response, string token)
    {
        response.Headers.TryAddWithoutValidation("x-picnic-auth", token);
        return response;
    }

    private static HttpResponseMessage Route(RecordedRequest request, int _)
    {
        if (request.Path.EndsWith("/deliveries/summary", StringComparison.Ordinal))
        {
            return Stub.Fixture("picnic/deliveries-summary.json");
        }

        if (request.Path.EndsWith("/deliveries/dlv-7788-aaa", StringComparison.Ordinal))
        {
            return Stub.Fixture("picnic/delivery-detail.json");
        }

        if (request.Path.EndsWith("/deliveries/dlv-1145-bbb", StringComparison.Ordinal))
        {
            return Stub.Fixture("picnic/delivery-detail-split.json");
        }

        return Stub.Status(HttpStatusCode.NotFound);
    }

    /// <summary>The three-response 2FA login: credentials, generate, verify.</summary>
    private static HttpResponseMessage SecondFactorRoute(RecordedRequest request, int index) => index switch
    {
        0 => WithToken(Stub.Fixture("picnic/login-2fa.json"), "token-pre-2fa"),
        1 => WithToken(Stub.Status(HttpStatusCode.OK), "token-mid-2fa"),
        // Confirmed: verify answers 204 with an EMPTY body and the new token in
        // the header, so nothing here may try to parse a response.
        2 => WithToken(new HttpResponseMessage(HttpStatusCode.NoContent), "token-after-2fa"),
        _ => Stub.Status(HttpStatusCode.NotFound),
    };

    private static FakeJobContext Fetching(HttpMessageHandler handler) => new(handler)
    {
        Config = DutchConfig,
        Material = new SessionMaterial
        {
            AccessToken = "picnic-auth-token-fixture",
            DeviceId = "3C417201548B2E3B",
        },
    };

    private static FakeJobContext SigningIn(HttpMessageHandler handler) => new(handler)
    {
        Config = DutchConfig,
        Inputs = Credentials,
    };

    // ---- the manifest: the first store that needs no agent ------------------

    [Fact]
    public void The_manifest_validates()
    {
        // Throws with every failing rule listed. A manifest that cannot boot
        // the host must not pass the suite either.
        ManifestValidator.Validate(Adapter().Describe());
    }

    [Fact]
    public void Picnic_runs_inline_with_no_browser_and_no_agent()
    {
        var manifest = Adapter().Describe();

        // The headline. Picnic's mobile API is plain JSON with no edge
        // protection, so there is nothing for Chromium to do - and `http` is
        // the only runtime the validator lets run in the control plane's own
        // process.
        Assert.Equal(ProviderRuntime.Http, manifest.Runtime);
        Assert.False(manifest.Agent.Required);
        Assert.Equal(AgentClass.Inline, manifest.Agent.Class);

        // No agent means no egress demand to make: an inline adapter runs
        // wherever the control plane does.
        Assert.Null(manifest.Agent.Egress);

        Assert.Equal("picnic", manifest.Id);
        Assert.Equal(ProviderKind.Store, manifest.Kind);
        Assert.Equal("NL", manifest.Country);
        Assert.Equal(SecretCustody.Client, manifest.SecretCustody);
    }

    [Fact]
    public void Picnic_is_unattended_because_its_token_renews_itself_by_being_used()
    {
        var manifest = Adapter().Describe();

        Assert.True(manifest.UnattendedFetch);

        // There is no refresh grant, and this is still honest: every response
        // may carry a re-issued x-picnic-auth which the client swaps in, so the
        // session renews with no human anywhere near it.
        Assert.True(manifest.Auth.Session.Refreshable);
        Assert.True(manifest.Auth.Session.RotatesOnUse);
        Assert.True(manifest.Auth.Reauth.Cheap);
        Assert.Equal(new[] { "session_expired" }, manifest.Auth.Reauth.TriggerCodes);
    }

    [Fact]
    public void The_login_form_is_an_email_and_a_password_that_is_marked_secret()
    {
        var manifest = Adapter().Describe();

        // Not password_sms. The second factor is a per-ACCOUNT setting, and
        // telling every user to expect a text leaves the majority who never get
        // one staring at a phone that will not ring.
        Assert.Equal(AuthFlow.Password, manifest.Auth.Flow);
        Assert.Equal(new[] { ChallengeType.MfaCode }, manifest.Auth.Challenges);

        var step = Assert.Single(manifest.Auth.Steps);
        Assert.Equal(2, step.Fields.Count);

        var username = Assert.Single(step.Fields, f => f.Key == "username");
        Assert.Equal(FieldType.Text, username.Type);
        Assert.False(username.Secret);

        var password = Assert.Single(step.Fields, f => f.Key == "password");
        Assert.Equal(FieldType.Password, password.Type);

        // Hashing it before it leaves the process makes it no less of a secret:
        // the digest is a password-equivalent, and redaction keys off this flag.
        Assert.True(password.Secret, "an unmarked password is logged and screenshotted");

        var country = Assert.Single(manifest.Auth.Config);
        Assert.Equal("country", country.Key);
        Assert.Equal(FieldType.Select, country.Type);
        Assert.False(country.Secret);
        Assert.Equal(new[] { "NL", "DE", "FR" }, country.Options);
    }

    [Fact]
    public void The_receipts_resource_matches_the_shape_every_store_offers()
    {
        var manifest = Adapter().Describe();
        var receipts = manifest.Resource("receipts");

        Assert.NotNull(receipts);
        Assert.Equal(ResourceShape.Receipt, receipts.Returns);

        var since = receipts.Param("since");
        Assert.NotNull(since);
        Assert.Equal(ParamType.Date, since.Type);
        Assert.True(since.Required);

        Assert.NotNull(receipts.Param("until"));

        var include = receipts.Param("include");
        Assert.NotNull(include);
        Assert.True(include.Multi);
        Assert.Equal(new[] { "items" }, include.Values);

        // An order is placed days before the van arrives and before the direct
        // debit settles, so a caller fetching strictly since its last sync would
        // lose late-settling orders permanently and invisibly.
        Assert.Equal(7, manifest.Limits.SettlementLagDays);

        // A connector never emits user-facing English.
        Assert.StartsWith("connect.", manifest.NotesKey, StringComparison.Ordinal);
        foreach (var field in manifest.Auth.AllFields())
        {
            Assert.StartsWith("connect.", field.LabelKey, StringComparison.Ordinal);
        }
    }

    // ---- login --------------------------------------------------------------

    [Fact]
    public async Task Login_sends_the_md5_of_the_password_and_never_the_password()
    {
        var handler = new StubHttpHandler((_, _) =>
            WithToken(Stub.Fixture("picnic/login.json"), "token-after-login"));

        using var ctx = SigningIn(handler);

        var result = await Adapter().LoginAsync(ctx, CancellationToken.None);

        var login = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, login.Method);
        Assert.Equal("/api/15/user/login", login.Path);

        // Confirmed body: key, the MD5 hex of the password, and the client id
        // as a NUMBER.
        Assert.Contains("\"key\":\"shopper@example.com\"", login.Body, StringComparison.Ordinal);
        Assert.Contains($"\"secret\":\"{PasswordDigest}\"", login.Body, StringComparison.Ordinal);
        Assert.Contains("\"client_id\":30100", login.Body, StringComparison.Ordinal);

        // The plaintext never leaves this process. Picnic compares the digest,
        // so sending the password itself would be both wrong and a leak.
        Assert.DoesNotContain(Password, login.Body, StringComparison.Ordinal);

        // Confirmed: the token is a RESPONSE HEADER. A reader that only looks
        // at the body finds a perfectly valid login with no credential in it.
        Assert.Equal("token-after-login", result.Material.AccessToken);
        Assert.Equal("111-222-3333", result.Account?.ExternalId);

        // Minted once and carried: 16 hex characters, the shape the reference
        // uses - and never the reference's own hard-coded literal, which would
        // hand every user of this connector one shared device identity.
        var deviceId = result.Material.DeviceId ?? string.Empty;
        Assert.Equal(16, deviceId.Length);
        Assert.All(deviceId, c => Assert.True(Uri.IsHexDigit(c)));
        Assert.NotEqual("3C417201548B2E3B", deviceId);

        // The country is the API host, so it is sealed into the material for a
        // caller that later resumes without resending config.
        Assert.Equal("NL", result.Material.Extra["country"]);
    }

    [Fact]
    public async Task The_credential_latch_closes_before_the_password_leaves_the_machine()
    {
        // The transport fails, so the latch can only have been set on the way
        // into the call rather than after a successful one.
        var handler = new StubHttpHandler((_, _) => throw new HttpRequestException("connection reset"));
        using var ctx = SigningIn(handler);

        await Assert.ThrowsAsync<ConnectorException>(() => Adapter().LoginAsync(ctx, CancellationToken.None));

        // A retried login is how an account gets locked, and Picnic may already
        // have counted this one.
        Assert.True(ctx.CredentialWasSubmitted);
    }

    [Fact]
    public async Task A_missing_input_is_refused_before_anything_is_sent()
    {
        var handler = new StubHttpHandler((_, _) => Stub.Fixture("picnic/login.json"));

        using var ctx = new FakeJobContext(handler)
        {
            Config = DutchConfig,
            Inputs = new Dictionary<string, string>(StringComparer.Ordinal) { ["username"] = "shopper@example.com" },
        };

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().LoginAsync(ctx, CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidRequest, error.Code);
        Assert.Empty(handler.Requests);
        Assert.False(ctx.CredentialWasSubmitted);
    }

    [Fact]
    public async Task Login_relays_the_sms_code_when_the_account_has_a_second_factor()
    {
        var handler = new StubHttpHandler(SecondFactorRoute);

        using var ctx = new FakeJobContext(handler)
        {
            Config = DutchConfig,
            Inputs = Credentials,
            Answer = _ => "123456",
        };

        var result = await Adapter().LoginAsync(ctx, CancellationToken.None);

        Assert.Equal(
            new[] { "/api/15/user/login", "/api/15/user/2fa/generate", "/api/15/user/2fa/verify" },
            handler.Requests.Select(r => r.Path));

        Assert.Contains("\"channel\":\"SMS\"", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Contains("\"otp\":\"123456\"", handler.Requests[2].Body, StringComparison.Ordinal);

        // The newest token wins, all the way through the exchange.
        Assert.Equal("token-after-2fa", result.Material.AccessToken);

        var challenge = Assert.Single(ctx.Asked);
        Assert.Equal(ChallengeType.MfaCode, challenge.Type);

        // SMS is the only channel the generate call is asked for, so the human
        // is told to look where the code actually is.
        Assert.Equal("sms", challenge.Delivery);
        Assert.Equal(6, challenge.Length);
        Assert.Equal("connect.challenge.verification_code", challenge.PromptKey);
    }

    [Fact]
    public async Task Only_the_2fa_calls_carry_the_picnic_agent_and_device_headers()
    {
        var handler = new StubHttpHandler(SecondFactorRoute);

        using var ctx = new FakeJobContext(handler)
        {
            Config = DutchConfig,
            Inputs = Credentials,
            Answer = _ => "123456",
        };

        await Adapter().LoginAsync(ctx, CancellationToken.None);

        // Confirmed: the pair is required for the 2FA endpoints and nowhere else
        // this adapter calls. Sending them everywhere would be inventing traffic
        // the reference does not.
        Assert.Null(handler.Requests[0].Header("x-picnic-agent"));
        Assert.Null(handler.Requests[0].Header("x-picnic-did"));

        foreach (var request in handler.Requests.Skip(1))
        {
            Assert.Equal("30100;1.236.1-15553;", request.Header("x-picnic-agent"));
            Assert.Equal(16, request.Header("x-picnic-did")?.Length);
        }
    }

    [Fact]
    public async Task A_rejected_sms_code_is_mfa_failed_and_not_a_wrong_password()
    {
        var handler = new StubHttpHandler((request, index) => index == 2
            ? Stub.Status(HttpStatusCode.BadRequest)
            : SecondFactorRoute(request, index));

        using var ctx = new FakeJobContext(handler)
        {
            Config = DutchConfig,
            Inputs = Credentials,
            Answer = _ => "000000",
        };

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().LoginAsync(ctx, CancellationToken.None));

        // The password was already accepted to get this far. Saying otherwise
        // sends the user to reset one that works.
        Assert.Equal(ErrorCode.MfaFailed, error.Code);
        Assert.NotEqual(ErrorCode.InvalidCredentials, error.Code);
    }

    [Fact]
    public async Task Invalid_credentials_are_reported_only_when_picnic_states_the_code()
    {
        var handler = new StubHttpHandler((_, _) =>
            Stub.Fixture("picnic/auth-error.json", HttpStatusCode.Unauthorized));

        using var ctx = SigningIn(handler);

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().LoginAsync(ctx, CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidCredentials, error.Code);
    }

    [Fact]
    public async Task A_rejection_with_no_stated_code_is_never_read_as_a_wrong_password()
    {
        // The rule that matters: invalid_credentials is never retried, so a
        // false one is permanent for the connect attempt AND sends the user off
        // to reset a password that was fine.
        var handler = new StubHttpHandler((_, _) => Stub.Status(HttpStatusCode.Unauthorized));
        using var ctx = SigningIn(handler);

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().LoginAsync(ctx, CancellationToken.None));

        Assert.NotEqual(ErrorCode.InvalidCredentials, error.Code);
        Assert.Equal(ErrorCode.SessionExpired, error.Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task A_bot_wall_at_the_login_is_reported_as_a_refusal(HttpStatusCode status)
    {
        // The body even claims a credential error, and the status still wins.
        // The research records no bot protection on this host, so a refusal here
        // is a finding - and reporting it as a wrong password would both hide it
        // and waste the user's one connect attempt.
        var handler = new StubHttpHandler((_, _) => Stub.Fixture("picnic/auth-error.json", status));
        using var ctx = SigningIn(handler);

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().LoginAsync(ctx, CancellationToken.None));

        Assert.Equal(ErrorCode.BlockedByProvider, error.Code);
        Assert.NotEqual(ErrorCode.InvalidCredentials, error.Code);
    }

    [Fact]
    public async Task A_login_that_carries_no_auth_header_is_a_shape_change()
    {
        // 200, a perfectly valid body, and no credential anywhere in it.
        var handler = new StubHttpHandler((_, _) => Stub.Fixture("picnic/login.json"));
        using var ctx = SigningIn(handler);

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().LoginAsync(ctx, CancellationToken.None));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains("x-picnic-auth", error.Detail, StringComparison.Ordinal);
    }

    // ---- fetch --------------------------------------------------------------

    [Fact]
    public async Task Fetch_reads_the_summary_then_one_detail_per_delivery()
    {
        var handler = new StubHttpHandler(Route);
        using var ctx = Fetching(handler);

        var result = await Adapter().FetchAsync(
            ctx, Requests.Receipts(since: Requests.Day(2026, 7, 1)), CancellationToken.None);

        // Three orders across two deliveries inside the window; the June one is
        // outside it and is never asked about in detail.
        Assert.Equal(3, result.Receipts.Count);
        Assert.Equal(
            new[] { "001-201-7788", "002-330-9902", "002-330-1145" },
            result.Receipts.Select(r => r.ExternalId));

        // The line items live nowhere but the detail, and the detail is per
        // DELIVERY - two orders sharing a slot cost one call, not two.
        Assert.Equal(
            new[]
            {
                "/api/15/deliveries/summary",
                "/api/15/deliveries/dlv-7788-aaa",
                "/api/15/deliveries/dlv-1145-bbb",
            },
            handler.Requests.Select(r => r.Path));

        Assert.True(result.Complete);
        Assert.Equal("deliveries-summary+detail", result.Via);

        // Picnic has no shops, so there is no store name to state - null, rather
        // than a fulfilment-hub code dressed up as one.
        Assert.All(result.Receipts, r =>
        {
            Assert.Equal("picnic", r.Merchant.Id);
            Assert.Equal("Picnic", r.Merchant.Name);
            Assert.Null(r.Merchant.StoreName);
        });
    }

    [Fact]
    public async Task Totals_are_cents_and_every_receipt_reconciles()
    {
        var handler = new StubHttpHandler(Route);
        using var ctx = Fetching(handler);

        var result = await Adapter().FetchAsync(
            ctx, Requests.Receipts(since: Requests.Day(2026, 7, 1)), CancellationToken.None);

        // 1469 cents = EUR 14.69. Declared, never sniffed: Picnic's own types
        // document these fields "in cents", and a recorded response is only
        // self-consistent as cents.
        Assert.Equal(new long[] { 1469, 2455, 6290 }, result.Receipts.Select(r => r.Total.Value));
        Assert.All(result.Receipts, r => Assert.Equal("EUR", r.Total.Currency));

        // The whole point of the deposit line and the negative discounts: the
        // items net of promotions sum to exactly the stated total.
        Assert.All(result.Receipts, receipt =>
        {
            Assert.True(receipt.Reconciled, $"{receipt.ExternalId} did not reconcile");

            var summed = receipt.Items.Sum(i => i.Total.Value + (i.Discount?.Amount.Value ?? 0));
            Assert.Equal(receipt.Total.Value, summed);
        });
    }

    [Fact]
    public async Task A_receipt_that_does_not_reconcile_is_flagged_and_still_emitted()
    {
        // The summary's stated total is made to disagree with the detail's own
        // lines. Dropping the receipt would hide a real purchase; trusting it
        // would hand over a total we know is inconsistent with its contents.
        var summary = FixtureCatalog.Read("picnic/deliveries-summary.json")
            .Replace("\"total_price\": 1469", "\"total_price\": 9999", StringComparison.Ordinal);

        var handler = new StubHttpHandler((request, index) =>
            request.Path.EndsWith("/deliveries/summary", StringComparison.Ordinal)
                ? Stub.Json(summary)
                : Route(request, index));

        using var ctx = Fetching(handler);

        var result = await Adapter().FetchAsync(
            ctx, Requests.Receipts(since: Requests.Day(2026, 7, 15)), CancellationToken.None);

        var receipt = Assert.Single(result.Receipts);
        Assert.Equal("001-201-7788", receipt.ExternalId);
        Assert.Equal(9999, receipt.Total.Value);
        Assert.False(receipt.Reconciled);
        Assert.NotEmpty(receipt.Items);
    }

    [Fact]
    public async Task The_window_is_applied_in_amsterdam_time_not_utc()
    {
        var handler = new StubHttpHandler(Route);
        using var ctx = Fetching(handler);

        // Order 002-330-9902 was placed at 00:18:55+02:00 on the 11th, which is
        // 22:18 UTC on the 10th. A window applied to the UTC instant would drop
        // it - a real purchase silently lost to a time zone.
        var result = await Adapter().FetchAsync(
            ctx, Requests.Receipts(since: Requests.Day(2026, 7, 11)), CancellationToken.None);

        Assert.Equal(new[] { "001-201-7788", "002-330-9902" }, result.Receipts.Select(r => r.ExternalId));
    }

    [Fact]
    public async Task Items_are_not_fetched_when_the_caller_did_not_ask_for_them()
    {
        var handler = new StubHttpHandler(Route);
        using var ctx = Fetching(handler);

        var result = await Adapter().FetchAsync(
            ctx, Requests.Receipts(since: Requests.Day(2026, 7, 1), items: false), CancellationToken.None);

        // One call for three receipts. The detail is the expensive part of this
        // provider and the only place the N+1 lives.
        Assert.Single(handler.Requests);
        Assert.Equal(3, result.Receipts.Count);
        Assert.All(result.Receipts, r => Assert.Empty(r.Items));
        Assert.Equal("deliveries-summary", result.Via);
    }

    [Fact]
    public async Task Every_call_carries_the_confirmed_headers_and_the_api_version_segment()
    {
        var handler = new StubHttpHandler(Route);
        using var ctx = Fetching(handler);

        await Adapter().FetchAsync(ctx, Requests.Receipts(since: Requests.Day(2026, 7, 1)), CancellationToken.None);

        Assert.All(handler.Requests, request =>
        {
            // The country is a HOST label and the version is a PATH segment.
            Assert.Equal("storefront-prod.nl.picnicinternational.com", request.Uri.Host);
            Assert.StartsWith("/api/15/", request.Path, StringComparison.Ordinal);

            Assert.Equal("okhttp/4.9.0", request.Header("User-Agent"));
            Assert.Equal("nl", request.Header("Accept-Language"));
            Assert.Equal("picnic-auth-token-fixture", request.Header("x-picnic-auth"));

            // Not Authorization: Bearer. Picnic's credential travels in its own
            // header, and nothing here is OAuth.
            Assert.Null(request.Header("Authorization"));

            // The agent/device pair belongs to the 2FA endpoints and nowhere
            // else, even though the session carries a device id.
            Assert.Null(request.Header("x-picnic-agent"));
            Assert.Null(request.Header("x-picnic-did"));
        });

        var summary = handler.Requests[0];

        // Exactly the confirmed casing. StringContent would have written
        // "charset=utf-8".
        Assert.Equal("application/json; charset=UTF-8", summary.Header("Content-Type"));

        // The status filter IS the request body: COMPLETED deliveries only,
        // because a CURRENT one has not been charged and a CANCELLED one never
        // will be.
        Assert.Equal("[\"COMPLETED\"]", summary.Body);
    }

    [Fact]
    public async Task A_reissued_token_is_adopted_and_handed_back_to_be_persisted()
    {
        var handler = new StubHttpHandler((request, index) =>
            index == 0
                ? WithToken(Stub.Fixture("picnic/deliveries-summary.json"), "picnic-auth-token-rotated")
                : Route(request, index));

        using var ctx = Fetching(handler);

        var result = await Adapter().FetchAsync(
            ctx, Requests.Receipts(since: Requests.Day(2026, 7, 15)), CancellationToken.None);

        // Picnic re-issues opportunistically. A client that keeps presenting the
        // token it was first handed gets older and older until the day it stops
        // being accepted.
        Assert.NotNull(result.RefreshedMaterial);
        Assert.Equal("picnic-auth-token-rotated", result.RefreshedMaterial.AccessToken);

        // The device id is carried through, not rediscovered.
        Assert.Equal("3C417201548B2E3B", result.RefreshedMaterial.DeviceId);

        // And the newest token is what the next call in the same pass uses.
        Assert.Equal("picnic-auth-token-rotated", handler.Requests[^1].Header("x-picnic-auth"));
    }

    [Fact]
    public async Task Nothing_is_handed_back_when_nothing_rotated()
    {
        var handler = new StubHttpHandler(Route);
        using var ctx = Fetching(handler);

        var result = await Adapter().FetchAsync(
            ctx, Requests.Receipts(since: Requests.Day(2026, 7, 1)), CancellationToken.None);

        // Null, not a copy: the caller's stored bundle is still current, and
        // re-sealing it for nothing churns every device that syncs.
        Assert.Null(result.RefreshedMaterial);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, ErrorCode.BlockedByProvider)]
    [InlineData(HttpStatusCode.BadGateway, ErrorCode.BlockedByProvider)]
    [InlineData(HttpStatusCode.GatewayTimeout, ErrorCode.BlockedByProvider)]
    [InlineData(HttpStatusCode.TooManyRequests, ErrorCode.RateLimited)]
    [InlineData(HttpStatusCode.Unauthorized, ErrorCode.SessionExpired)]
    public async Task A_refusal_from_the_delivery_api_is_reported_as_a_refusal(
        HttpStatusCode status, ErrorCode expected)
    {
        var handler = new StubHttpHandler((_, _) => Stub.Status(status));
        using var ctx = Fetching(handler);

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().FetchAsync(ctx, Requests.Receipts(), CancellationToken.None));

        Assert.Equal(expected, error.Code);

        // Never, under any status. A fetch submits no credential at all, so
        // nothing here is evidence about a password.
        Assert.NotEqual(ErrorCode.InvalidCredentials, error.Code);
    }

    [Fact]
    public async Task A_block_page_where_json_was_promised_is_a_shape_change()
    {
        var handler = new StubHttpHandler((_, _) => Stub.Html());
        using var ctx = Fetching(handler);

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().FetchAsync(ctx, Requests.Receipts(), CancellationToken.None));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
    }

    [Fact]
    public async Task A_fetch_with_no_token_asks_for_a_login_rather_than_calling()
    {
        var handler = new StubHttpHandler(Route);

        using var ctx = new FakeJobContext(handler)
        {
            Config = DutchConfig,
            Material = new SessionMaterial(),
        };

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().FetchAsync(ctx, Requests.Receipts(), CancellationToken.None));

        Assert.Equal(ErrorCode.SessionExpired, error.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task The_country_falls_back_to_the_copy_sealed_into_the_material()
    {
        var handler = new StubHttpHandler(Route);

        // A caller that resumed a session without resending config still has to
        // be serviceable: the country is the API host.
        using var ctx = new FakeJobContext(handler)
        {
            Material = new SessionMaterial
            {
                AccessToken = "live",
                Extra = new Dictionary<string, string>(StringComparer.Ordinal) { ["country"] = "de" },
            },
        };

        await Adapter().FetchAsync(ctx, Requests.Receipts(items: false), CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("storefront-prod.de.picnicinternational.com", request.Uri.Host);
        Assert.Equal("de", request.Header("Accept-Language"));
    }

    [Fact]
    public async Task A_country_outside_the_manifest_s_options_is_refused_before_any_call()
    {
        var handler = new StubHttpHandler(Route);

        using var ctx = new FakeJobContext(handler)
        {
            Config = new Dictionary<string, string>(StringComparer.Ordinal) { ["country"] = "ZZ" },
            Material = new SessionMaterial { AccessToken = "live" },
        };

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().FetchAsync(ctx, Requests.Receipts(), CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidRequest, error.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task An_unknown_resource_is_refused()
    {
        var handler = new StubHttpHandler(Route);
        using var ctx = Fetching(handler);

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().FetchAsync(
                ctx, new ResourceRequest { ResourceId = "invoices" }, CancellationToken.None));

        Assert.Equal(ErrorCode.UnsupportedResource, error.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_logout_that_fails_upstream_still_lets_the_user_disconnect()
    {
        var handler = new StubHttpHandler((_, _) => Stub.Status(HttpStatusCode.InternalServerError));
        using var ctx = Fetching(handler);

        // Never fatal: a user disconnecting must always succeed locally,
        // whatever Picnic thinks about it.
        await Adapter().LogoutAsync(ctx, CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/api/15/user/logout", request.Path);
    }
}
