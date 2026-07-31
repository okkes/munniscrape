using System.Net;
using System.Text.Json.Nodes;
using Connector.Kit.Errors;
using Connector.Kit.Jobs;
using Connector.Kit.Security;
using ShopConnector.Adapters.AlbertHeijn;
using ShopConnector.Adapters.Tests.Support;
using Xunit;

namespace ShopConnector.Adapters.Tests;

/// <summary>
/// Albert Heijn with a stubbed transport: the token exchange, the whole
/// GraphQL fetch path and the refresh-and-retry, with no network, no browser
/// and no account.
///
/// The browser login itself is exercised by the human on a live run - what is
/// asserted here is everything that made the previous live run fail: the
/// client id, the header block, and receipts being GraphQL rather than REST.
/// </summary>
public sealed class AlbertHeijnAdapterTests
{
    private const string TokenPath = "/mobile-auth/v1/auth/token";
    private const string RefreshPath = "/mobile-auth/v1/auth/token/refresh";
    private const string GraphQlPath = "/graphql";

    private static readonly DateTimeOffset Now = new(2026, 7, 26, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The whole provider, answering from its recorded payloads. List and
    /// detail share one path and one method - which operation was asked for
    /// is in the body, exactly as it is upstream.
    /// </summary>
    private static HttpResponseMessage Route(RecordedRequest request, int _) => request.Path switch
    {
        RefreshPath => Stub.Fixture("ah/token.json"),
        TokenPath => Stub.Fixture("ah/token.json"),
        GraphQlPath => IsDetail(request) ? Stub.Fixture("ah/receipt-detail.json") : Stub.Fixture("ah/receipts-list.json"),
        _ => Stub.Status(HttpStatusCode.NotFound),
    };

    private static bool IsDetail(RecordedRequest request) =>
        request.Body?.Contains("posReceiptDetails", StringComparison.Ordinal) == true;

    /// <summary>
    /// The TYPED login by default, because that is what almost everything in
    /// this file describes: filling two boxes, meeting a captcha, settling.
    /// Albert Heijn now streams its own page by default in production, and
    /// that path asks for nothing - so a test about a missing credential has
    /// no credential to miss. Pass true to exercise the streamed one.
    /// </summary>
    private static AlbertHeijnAdapter Adapter(bool liveLogin = false) =>
        new(new AlbertHeijnOptions { LiveLogin = liveLogin }, new FixedTimeProvider(Now));

    // ---- the three corrections --------------------------------------------

    [Fact]
    public void The_authorize_url_names_the_corrected_client_id()
    {
        var url = Adapter().AuthorizeUrl();

        Assert.StartsWith("https://login.ah.nl/login?", url, StringComparison.Ordinal);
        Assert.Contains("client_id=appie-ios", url, StringComparison.Ordinal);
        Assert.Contains("response_type=code", url, StringComparison.Ordinal);

        // Verbatim, exactly as the reference sends it. Percent-encoding the
        // scheme is the kind of "correction" that fails an authorize request
        // for a reason nobody can reconstruct later.
        Assert.Contains("redirect_uri=appie://login-exit", url, StringComparison.Ordinal);
        Assert.DoesNotContain("appie%3A%2F%2F", url, StringComparison.Ordinal);

        // The value the adapter used to send, which is what made a real login
        // fail. The suffix is not cosmetic.
        Assert.DoesNotContain("client_id=appie&", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_call_carries_the_header_block_the_api_answers_to()
    {
        var handler = new StubHttpHandler(Route);
        using var ctx = new FakeJobContext(handler)
        {
            Material = new SessionMaterial { AccessToken = "live" },
        };

        await Adapter().FetchAsync(ctx, Requests.Receipts(since: Requests.Day(2026, 7, 15)), CancellationToken.None);

        // Sent because the API needs them to route and to answer at all, not
        // to disguise anything. The adapter sent none of these before, which
        // is bug two of the three that failed a real login.
        Assert.All(handler.Requests, request =>
        {
            AssertUserAgent(request);
            Assert.Equal("appie-ios", request.Header("x-client-name"));
            Assert.Equal("9.28", request.Header("x-client-version"));
            Assert.Equal("AHWEBSHOP", request.Header("x-application"));
            Assert.Equal("application/json", request.Header("Accept"));
            Assert.StartsWith("application/json", request.Header("Content-Type"), StringComparison.Ordinal);
            Assert.Equal("Bearer live", request.Header("Authorization"));
        });
    }

    [Fact]
    public async Task Receipts_are_graphql_and_never_a_rest_path()
    {
        var handler = new StubHttpHandler(Route);
        using var ctx = new FakeJobContext(handler)
        {
            Material = new SessionMaterial { AccessToken = "live" },
        };

        await Adapter().FetchAsync(ctx, Requests.Receipts(since: Requests.Day(2026, 7, 15)), CancellationToken.None);

        // Bug three: /mobile-services/v2/receipts does not exist.
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(GraphQlPath, request.Path);
            Assert.Equal(HttpMethod.Post, request.Method);
        });

        var list = handler.Requests[0].Body;
        Assert.NotNull(list);
        Assert.Contains("posReceiptsPage", list, StringComparison.Ordinal);
        Assert.Contains("\"offset\":0", list, StringComparison.Ordinal);
        Assert.Contains("\"limit\":50", list, StringComparison.Ordinal);

        // Confirmed: query and variables, and no operationName at all.
        Assert.DoesNotContain("operationName", list, StringComparison.Ordinal);

        var detail = handler.Requests[1].Body;
        Assert.NotNull(detail);
        Assert.Contains("posReceiptDetails", detail, StringComparison.Ordinal);
        Assert.Contains("ah-2026-07-19-4711", detail, StringComparison.Ordinal);
    }

    // ---- login -------------------------------------------------------------

    [Fact]
    public async Task A_redirect_handed_over_up_front_needs_no_browser_and_no_human()
    {
        var handler = new StubHttpHandler(Route);
        using var ctx = new FakeJobContext(handler)
        {
            // The escape hatch: a native consumer that intercepted the
            // appie:// scheme itself already holds the answer.
            Inputs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["redirect_url"] = "appie://login-exit?code=already-collected",
            },
        };

        var result = await Adapter().LoginAsync(ctx, CancellationToken.None);

        Assert.Empty(ctx.Asked);
        Assert.False(ctx.Browser.Started);

        // An OAuth code is single-use, so the latch has to close before it
        // goes upstream: after this a lost lease must fail the job rather
        // than replay a code that may already have been spent.
        Assert.True(ctx.CredentialWasSubmitted);

        var exchange = Assert.Single(handler.Requests, r => r.Path == TokenPath);
        Assert.Equal(HttpMethod.Post, exchange.Method);
        Assert.NotNull(exchange.Body);
        Assert.Contains("\"clientId\":\"appie-ios\"", exchange.Body, StringComparison.Ordinal);
        Assert.Contains("already-collected", exchange.Body, StringComparison.Ordinal);
        AssertUserAgent(exchange);

        Assert.Equal("ah-access-token-fixture", result.Material.AccessToken);
        Assert.Equal("ah-refresh-token-fixture", result.Material.RefreshToken);

        // Deliberately unset: the access token's own expiry says nothing
        // about how long the connection lasts, and claiming the short one
        // would make the consumer prompt for a reconnect that is not needed.
        Assert.Null(result.ExpiresAt);
    }

    [Theory]
    [InlineData("username")]
    [InlineData("password")]
    public async Task A_login_missing_a_credential_is_refused_before_any_browser_or_call(string missing)
    {
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["username"] = "shopper@example.com",
            ["password"] = "hunter2",
        };
        inputs.Remove(missing);

        var handler = new StubHttpHandler(Route);
        using var ctx = new FakeJobContext(handler) { Inputs = inputs };

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().LoginAsync(ctx, CancellationToken.None));

        // Nothing went upstream, so nothing counted against the user - and no
        // browser was started to find that out.
        Assert.Equal(ErrorCode.InvalidRequest, error.Code);
        Assert.False(ctx.CredentialWasSubmitted);
        Assert.False(ctx.Browser.Started);
        Assert.Empty(handler.Requests);
    }

    // ---- fetch -------------------------------------------------------------

    [Fact]
    public async Task Fetch_returns_a_normalised_reconciled_receipt_with_its_discounts_folded_in()
    {
        var handler = new StubHttpHandler(Route);
        using var ctx = new FakeJobContext(handler)
        {
            Material = new SessionMaterial { AccessToken = "live", RefreshToken = "r" },
        };

        var result = await Adapter().FetchAsync(
            ctx, Requests.Receipts(since: Requests.Day(2026, 7, 15)), CancellationToken.None);

        var receipt = Assert.Single(result.Receipts);
        Assert.Equal("ah-2026-07-19-4711", receipt.ExternalId);
        Assert.Equal("ah", receipt.Merchant.Id);

        // The confirmed operations select no store, so there is nothing to
        // state. Null, not a guess.
        Assert.Null(receipt.Merchant.StoreName);

        Assert.Equal(636, receipt.Total.Value);
        Assert.Equal("EUR", receipt.Total.Currency);
        Assert.Equal(TimeSpan.FromHours(2), receipt.PurchasedAt.Offset);

        // Three products and the bonus, which AH states in its own array with
        // nothing linking it to a product.
        Assert.Equal(4, receipt.Items.Count);
        Assert.True(receipt.Reconciled);

        Assert.NotNull(receipt.Payment);
        Assert.Equal("card", receipt.Payment.Method);

        // Explicitly null rather than omitted: the operation selects nothing
        // that could carry a tail, and the consumer's match is weaker for it.
        Assert.Null(receipt.Payment.CardLast4);
        Assert.Null(receipt.Payment.IbanTail);

        Assert.True(result.Complete);
        Assert.Equal("graphql", result.Via);

        // Nothing rotated, so the caller's stored bundle is still current and
        // must not be needlessly re-sealed.
        Assert.Null(result.RefreshedMaterial);

        // A fetch is plain HTTP: the browser is only ever for the one login.
        Assert.False(ctx.Browser.Started);
        Assert.Contains(JobStep.Normalizing, ctx.Steps);
    }

    [Fact]
    public async Task Fetch_without_items_skips_the_detail_call_entirely()
    {
        var handler = new StubHttpHandler(Route);
        using var ctx = new FakeJobContext(handler)
        {
            Material = new SessionMaterial { AccessToken = "live" },
        };

        var result = await Adapter().FetchAsync(ctx, Requests.Receipts(items: false), CancellationToken.None);

        // The detail call is the expensive part and the one most likely to
        // trip protection, so it only happens when items were asked for.
        Assert.Single(handler.Requests);
        Assert.Equal(2, result.Receipts.Count);
        Assert.All(result.Receipts, r => Assert.Empty(r.Items));
    }

    [Fact]
    public async Task An_account_with_no_receipts_yields_no_records_rather_than_an_error()
    {
        var handler = new StubHttpHandler((_, _) => Stub.Fixture("ah/receipts-empty.json"));
        using var ctx = new FakeJobContext(handler)
        {
            Material = new SessionMaterial { AccessToken = "live" },
        };

        var result = await Adapter().FetchAsync(ctx, Requests.Receipts(items: false), CancellationToken.None);

        Assert.Empty(result.Receipts);
        Assert.True(result.Complete);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task An_errors_array_under_a_200_is_a_shape_change_and_never_an_empty_history()
    {
        var handler = new StubHttpHandler((_, _) => Stub.Fixture("ah/graphql-errors.json"));
        using var ctx = new FakeJobContext(handler)
        {
            Material = new SessionMaterial { AccessToken = "live" },
        };

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().FetchAsync(ctx, Requests.Receipts(items: false), CancellationToken.None));

        // GraphQL puts the failure in the body with a 200. A client that only
        // reads the status code hands back nothing and calls it "you have
        // never shopped here" - a worse lie than an outage.
        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
        Assert.Contains("Member is not entitled", error.Detail, StringComparison.Ordinal);
    }

    // ---- raw ---------------------------------------------------------------

    [Fact]
    public async Task The_providers_own_document_comes_back_only_when_it_was_asked_for()
    {
        var handler = new StubHttpHandler(Route);
        using var ctx = new FakeJobContext(handler)
        {
            Material = new SessionMaterial { AccessToken = "live" },
        };

        var result = await Adapter().FetchAsync(
            ctx, Requests.Receipts(since: Requests.Day(2026, 7, 15), raw: true), CancellationToken.None);

        var receipt = Assert.Single(result.Receipts);
        var raw = Assert.Contains(receipt.ExternalId, result.Raw);

        // AH's own detail document, verbatim - which is the point. When AH
        // renames a field the normalised receipt simply loses a value and says
        // nothing about why; this is the thing to read.
        Assert.Contains("posReceiptDetails", raw, StringComparison.Ordinal);

        // The detail call's answer and nothing else. The list page carries
        // every other receipt in the pass, and handing one caller another
        // shopper's rows would be a worse leak than the one this diagnoses.
        Assert.DoesNotContain("posReceiptsPage", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_fetch_that_did_not_ask_for_raw_carries_none()
    {
        var handler = new StubHttpHandler(Route);
        using var ctx = new FakeJobContext(handler)
        {
            Material = new SessionMaterial { AccessToken = "live" },
        };

        var result = await Adapter().FetchAsync(
            ctx, Requests.Receipts(since: Requests.Day(2026, 7, 15)), CancellationToken.None);

        // Raw is strictly the more sensitive of the two: normalisation drops
        // the fields nobody asked for and this puts them back. Never a default.
        Assert.NotEmpty(result.Receipts);
        Assert.Empty(result.Raw);
    }

    [Fact]
    public async Task Raw_without_items_has_nothing_to_carry_and_says_so()
    {
        var handler = new StubHttpHandler(Route);
        using var ctx = new FakeJobContext(handler)
        {
            Material = new SessionMaterial { AccessToken = "live" },
        };

        var result = await Adapter().FetchAsync(
            ctx, Requests.Receipts(items: false, raw: true), CancellationToken.None);

        // The detail call is the only place a raw document exists, and it only
        // happens when items were asked for. Empty rather than the list page:
        // one receipt's row is not the same thing as everybody's.
        Assert.Equal(2, result.Receipts.Count);
        Assert.Empty(result.Raw);
        Assert.Single(handler.Requests);
    }

    // ---- pagination --------------------------------------------------------

    [Fact]
    public async Task A_history_longer_than_one_pass_stops_at_the_cap_and_says_so()
    {
        var handler = new StubHttpHandler((request, ordinal) => FullPage(ordinal));
        using var ctx = new FakeJobContext(handler)
        {
            Material = new SessionMaterial { AccessToken = "live" },
        };

        var result = await Adapter().FetchAsync(ctx, Requests.Receipts(items: false), CancellationToken.None);

        // The resource's own cap, not a number this test invented.
        var cap = Adapter().Describe().Resource("receipts")!.MaxRecordsPerFetch;
        Assert.Equal(cap, result.Receipts.Count);

        // False is the whole point: the caller comes back for the rest rather
        // than believing it has the lot.
        Assert.False(result.Complete);

        // offset/limit, advancing by the page size on every call.
        Assert.Equal(5, handler.Requests.Count);
        Assert.Collection(
            handler.Requests.Select(r => r.Body!),
            b => Assert.Contains("\"offset\":0", b, StringComparison.Ordinal),
            b => Assert.Contains("\"offset\":50", b, StringComparison.Ordinal),
            b => Assert.Contains("\"offset\":100", b, StringComparison.Ordinal),
            b => Assert.Contains("\"offset\":150", b, StringComparison.Ordinal),
            b => Assert.Contains("\"offset\":200", b, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_list_that_keeps_answering_the_same_page_stops_instead_of_looping()
    {
        // The symptom of an offset that stopped advancing anything upstream.
        // Without the guard this is the same request five times against a
        // defended endpoint.
        var handler = new StubHttpHandler((_, _) => FullPage(0));
        using var ctx = new FakeJobContext(handler)
        {
            Material = new SessionMaterial { AccessToken = "live" },
        };

        var result = await Adapter().FetchAsync(ctx, Requests.Receipts(items: false), CancellationToken.None);

        Assert.Equal(50, result.Receipts.Count);
        Assert.Equal(2, handler.Requests.Count);
    }

    // ---- session -----------------------------------------------------------

    [Fact]
    public async Task An_expired_access_token_refreshes_once_retries_once_and_rotates_the_material()
    {
        var handler = new StubHttpHandler((request, ordinal) => request.Path switch
        {
            // The first list call finds a dead token; the refresh succeeds and
            // the retry is served.
            GraphQlPath when ordinal == 0 => Stub.Status(HttpStatusCode.Unauthorized),
            _ => Route(request, ordinal),
        });

        using var ctx = new FakeJobContext(handler)
        {
            Material = new SessionMaterial { AccessToken = "stale", RefreshToken = "still-good" },
        };

        var result = await Adapter().FetchAsync(ctx, Requests.Receipts(items: false), CancellationToken.None);

        Assert.Equal(2, result.Receipts.Count);
        Assert.Equal([GraphQlPath, RefreshPath, GraphQlPath], handler.Requests.Select(r => r.Path));

        var refresh = handler.Requests[1];
        Assert.NotNull(refresh.Body);
        Assert.Contains("\"clientId\":\"appie-ios\"", refresh.Body, StringComparison.Ordinal);
        Assert.Contains("still-good", refresh.Body, StringComparison.Ordinal);

        // The caller must persist the newest bundle, so a rotation has to be
        // reported rather than silently kept in memory.
        Assert.NotNull(result.RefreshedMaterial);
        Assert.Equal("ah-access-token-fixture", result.RefreshedMaterial.AccessToken);
        Assert.Equal("ah-refresh-token-fixture", result.RefreshedMaterial.RefreshToken);
    }

    [Fact]
    public async Task A_second_rejection_after_a_refresh_ends_the_session_rather_than_looping()
    {
        var handler = new StubHttpHandler((request, ordinal) => request.Path switch
        {
            GraphQlPath => Stub.Status(HttpStatusCode.Unauthorized),
            _ => Route(request, ordinal),
        });

        using var ctx = new FakeJobContext(handler)
        {
            Material = new SessionMaterial { AccessToken = "stale", RefreshToken = "dead" },
        };

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().FetchAsync(ctx, Requests.Receipts(items: false), CancellationToken.None));

        // One refresh, one retry. Repeatedly presenting a dead session is how
        // a provider starts treating a client as hostile.
        Assert.Equal(ErrorCode.SessionExpired, error.Code);
        Assert.Equal(3, handler.Requests.Count);
        Assert.False(error.Retriable);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task A_refusal_from_the_provider_is_reported_as_a_refusal(HttpStatusCode status)
    {
        var handler = new StubHttpHandler((request, ordinal) => request.Path switch
        {
            GraphQlPath => Stub.Status(status),
            _ => Route(request, ordinal),
        });

        using var ctx = new FakeJobContext(handler)
        {
            Material = new SessionMaterial { AccessToken = "live" },
        };

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().FetchAsync(ctx, Requests.Receipts(items: false), CancellationToken.None));

        // Telling a user their password is wrong when the truth is bot
        // protection sends them to reset a password that was fine.
        Assert.Equal(ErrorCode.BlockedByProvider, error.Code);
        Assert.NotEqual(ErrorCode.InvalidCredentials, error.Code);
    }

    [Fact]
    public async Task An_interstitial_where_json_was_promised_is_a_shape_change()
    {
        var handler = new StubHttpHandler((request, ordinal) => request.Path switch
        {
            GraphQlPath => Stub.Html(),
            _ => Route(request, ordinal),
        });

        using var ctx = new FakeJobContext(handler)
        {
            Material = new SessionMaterial { AccessToken = "live" },
        };

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().FetchAsync(ctx, Requests.Receipts(items: false), CancellationToken.None));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
    }

    [Fact]
    public async Task A_fetch_with_no_stored_token_ends_the_session_before_any_call()
    {
        var handler = new StubHttpHandler(Route);
        using var ctx = new FakeJobContext(handler);

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().FetchAsync(ctx, Requests.Receipts(), CancellationToken.None));

        Assert.Equal(ErrorCode.SessionExpired, error.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task An_unknown_resource_is_refused_without_contacting_the_provider()
    {
        var handler = new StubHttpHandler(Route);
        using var ctx = new FakeJobContext(handler)
        {
            Material = new SessionMaterial { AccessToken = "live" },
        };

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().FetchAsync(
                ctx, new ResourceRequest { ResourceId = "transactions" }, CancellationToken.None));

        Assert.Equal(ErrorCode.UnsupportedResource, error.Code);
        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// The user agent, asserted as its two product tokens.
    ///
    /// <see cref="HttpRequestMessage"/> parses a User-Agent into tokens, so
    /// the recorder here sees them separately; on the wire they are re-joined
    /// with a space and arrive exactly as the reference sends them. Asserting
    /// the tokens keeps the recorder's comma out of an expectation that would
    /// then look wrong to the next reader.
    /// </summary>
    private static void AssertUserAgent(RecordedRequest request)
    {
        var agent = request.Header("User-Agent");
        Assert.NotNull(agent);
        Assert.StartsWith("Appie/9.28", agent, StringComparison.Ordinal);
        Assert.Contains("(iPhone17,3; iPhone; CPU OS 26_1 like Mac OS X)", agent, StringComparison.Ordinal);
    }

    /// <summary>
    /// A full page of distinct receipts, all inside any window. The ordinal
    /// makes each page's ids unique, so a walk that really advances is told
    /// apart from one that keeps re-reading page one.
    /// </summary>
    private static HttpResponseMessage FullPage(int ordinal, int size = 50)
    {
        var rows = new JsonArray();
        for (var i = 0; i < size; i++)
        {
            rows.Add(new JsonObject
            {
                ["id"] = $"ah-{ordinal:D2}-{i:D3}",
                ["dateTime"] = "2026-07-19T17:42:00+02:00",
                ["totalAmount"] = new JsonObject { ["amount"] = 1.00 },
            });
        }

        var body = new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["posReceiptsPage"] = new JsonObject { ["posReceipts"] = rows },
            },
        };

        return Stub.Json(body.ToJsonString());
    }
}
