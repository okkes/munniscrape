using System.Net;
using Connector.Kit.Errors;
using Connector.Kit.Security;
using ShopConnector.Adapters.LidlPlus;
using ShopConnector.Adapters.Support;
using ShopConnector.Adapters.Tests.Support;
using Xunit;

namespace ShopConnector.Adapters.Tests;

/// <summary>
/// The Lidl fetch path: after the one browser login, everything is plain
/// HTTP against the ticket API, which means all of it is testable offline.
/// </summary>
public sealed class LidlPlusAdapterTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 26, 9, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyDictionary<string, string> DutchConfig =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["country"] = "NL", ["language"] = "nl" };

    private static HttpResponseMessage Route(RecordedRequest request, int _)
    {
        if (request.Path.StartsWith("/api/v3/", StringComparison.Ordinal))
        {
            return Stub.Fixture("lidl/ticket-detail.json");
        }

        if (request.Path.StartsWith("/api/v2/", StringComparison.Ordinal))
        {
            return request.Query.Contains("pageNumber=1", StringComparison.Ordinal)
                ? Stub.Fixture("lidl/tickets-page-1.json")
                : Stub.Fixture("lidl/tickets-page-2.json");
        }

        return Stub.Status(HttpStatusCode.NotFound);
    }

    private static LidlPlusAdapter Adapter() => new(new LidlPlusOptions(), new FixedTimeProvider(Now));

    private static FakeJobContext Context(HttpMessageHandler handler) => new(handler)
    {
        Config = DutchConfig,
        Material = new SessionMaterial
        {
            AccessToken = "lidl-access-token-fixture",
            RefreshToken = "lidl-refresh-token-fixture",
        },
    };

    [Fact]
    public async Task Fetch_walks_the_v2_list_then_the_v3_detail()
    {
        var handler = new StubHttpHandler(Route);
        using var ctx = Context(handler);

        var result = await Adapter().FetchAsync(
            ctx, Requests.Receipts(since: Requests.Day(2026, 7, 10)), CancellationToken.None);

        var receipt = Assert.Single(result.Receipts);
        Assert.Equal("lidl-2026-07-18-8801", receipt.ExternalId);
        Assert.Equal("lidl", receipt.Merchant.Id);
        Assert.Equal("Lidl Utrecht Kanaleneiland", receipt.Merchant.StoreName);
        Assert.Equal(1427, receipt.Total.Value);
        Assert.Equal(TimeSpan.FromHours(2), receipt.PurchasedAt.Offset);
        Assert.Equal(4, receipt.Items.Count);
        Assert.True(receipt.Reconciled);
        Assert.Equal("4821", receipt.Payment?.CardLast4);
        Assert.True(result.Complete);
        Assert.Equal("tickets-v2", result.Via);

        // Two list pages (the second is empty and terminates the walk) and
        // exactly one detail call - one per receipt inside the window, never
        // the whole history.
        Assert.Equal(
            ["/api/v2/NL/tickets", "/api/v2/NL/tickets", "/api/v3/NL/tickets/lidl-2026-07-18-8801"],
            handler.Requests.Select(r => r.Path));
    }

    [Fact]
    public async Task Every_ticket_call_carries_the_app_headers_the_api_routes_on()
    {
        var handler = new StubHttpHandler(Route);
        using var ctx = Context(handler);

        await Adapter().FetchAsync(ctx, Requests.Receipts(since: Requests.Day(2026, 7, 10)), CancellationToken.None);

        // Sent because the API needs them to route, not to disguise anything.
        // The casing of "iOs" is the provider's, and is confirmed.
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("999.99.9", request.Header("App-Version"));
            Assert.Equal("iOs", request.Header("Operating-System"));
            Assert.Equal("com.lidl.eci.lidl.plus", request.Header("App"));
            Assert.Equal("NL", request.Header("Country"));
            Assert.Equal("nl", request.Header("Accept-Language"));
            Assert.Equal("Bearer lidl-access-token-fixture", request.Header("Authorization"));
        });
    }

    [Fact]
    public async Task A_ticket_outside_the_window_is_not_fetched_in_detail()
    {
        var handler = new StubHttpHandler(Route);
        using var ctx = Context(handler);

        var result = await Adapter().FetchAsync(
            ctx, Requests.Receipts(since: Requests.Day(2026, 7, 1), until: Requests.Day(2026, 7, 10)),
            CancellationToken.None);

        var receipt = Assert.Single(result.Receipts);
        Assert.Equal("lidl-2026-07-05-8720", receipt.ExternalId);
        Assert.Single(handler.Requests, r => r.Path.StartsWith("/api/v3/", StringComparison.Ordinal));
    }

    [Fact]
    public void The_authorize_url_carries_the_confirmed_parameters_and_the_pkce_pair()
    {
        // A verifier chosen here rather than generated, so the challenge in
        // the URL is a value this test can compute independently.
        var pkce = PkceChallenge.For("k0PBJmBFPYRe0S6dr1RxD1bcVYh7z7iHFbSC6X1IH0M");

        var url = Adapter().AuthorizeUrl(pkce, new LidlSettings("NL", "nl"));

        Assert.StartsWith("https://accounts.lidl.com/connect/authorize?", url, StringComparison.Ordinal);
        Assert.Contains("client_id=LidlPlusNativeClient", url, StringComparison.Ordinal);
        Assert.Contains("response_type=code", url, StringComparison.Ordinal);
        Assert.Contains("redirect_uri=com.lidlplus.app%3A%2F%2Fcallback", url, StringComparison.Ordinal);

        // offline_access is what yields the refresh token, and the refresh
        // token is the whole T2 story.
        Assert.Contains("offline_access", url, StringComparison.Ordinal);

        // RFC 7636, and the reason a real login could not have worked: an
        // authorization server that recorded a challenge refuses to exchange
        // the code without the verifier.
        Assert.Contains($"code_challenge={Uri.EscapeDataString(pkce.Challenge)}", url, StringComparison.Ordinal);
        Assert.Contains("code_challenge_method=S256", url, StringComparison.Ordinal);

        // OBSERVED live: without these accounts.lidl.com does not render a
        // login page at all, it redirects to /error. Capitalised bare country,
        // lower-case full locale - and "nl" alone for the language is another
        // /error, so the two spellings are asserted rather than assumed.
        Assert.Contains("Country=NL", url, StringComparison.Ordinal);
        Assert.Contains("language=nl-NL", url, StringComparison.Ordinal);

        // The verifier is the secret half and must never appear in a URL any
        // browser, proxy or log can see.
        Assert.DoesNotContain(pkce.Verifier, url, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("country", "ZZ")]
    [InlineData("language", "xx")]
    public async Task Config_outside_the_manifest_s_options_is_refused_before_any_call(string key, string value)
    {
        var config = new Dictionary<string, string>(DutchConfig, StringComparer.Ordinal) { [key] = value };
        var handler = new StubHttpHandler(Route);

        using var ctx = new FakeJobContext(handler)
        {
            Config = config,
            Material = new SessionMaterial { AccessToken = "live" },
        };

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().FetchAsync(ctx, Requests.Receipts(), CancellationToken.None));

        Assert.Equal(ErrorCode.InvalidRequest, error.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Country_and_language_fall_back_to_the_copy_sealed_into_the_material()
    {
        var handler = new StubHttpHandler(Route);

        // A caller that resumed a session without resending config still has
        // to be serviceable: every ticket URL and two headers need these.
        using var ctx = new FakeJobContext(handler)
        {
            Material = new SessionMaterial
            {
                AccessToken = "live",
                Extra = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["country"] = "nl",
                    ["language"] = "NL",
                },
            },
        };

        await Adapter().FetchAsync(ctx, Requests.Receipts(items: false), CancellationToken.None);

        // Country uppercase, language lowercase: both appear verbatim in URLs
        // and headers and Lidl is case-sensitive about them.
        Assert.All(handler.Requests, request =>
        {
            Assert.Contains("/NL/", request.Path, StringComparison.Ordinal);
            Assert.Equal("nl", request.Header("Accept-Language"));
        });
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task A_refusal_from_the_ticket_api_is_reported_as_a_refusal(HttpStatusCode status)
    {
        var handler = new StubHttpHandler((_, _) => Stub.Status(status));
        using var ctx = Context(handler);

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => Adapter().FetchAsync(ctx, Requests.Receipts(), CancellationToken.None));

        Assert.Equal(ErrorCode.BlockedByProvider, error.Code);
        Assert.NotEqual(ErrorCode.InvalidCredentials, error.Code);
    }

    [Fact]
    public async Task A_list_that_keeps_answering_the_same_page_stops_instead_of_looping()
    {
        // The symptom of a pagination parameter that stopped advancing
        // upstream. Without the guard this is the same request twenty times
        // against a defended endpoint.
        var handler = new StubHttpHandler((_, _) => Stub.Fixture("lidl/tickets-page-1.json"));
        using var ctx = Context(handler);

        var result = await Adapter().FetchAsync(ctx, Requests.Receipts(items: false), CancellationToken.None);

        Assert.Equal(2, result.Receipts.Count);
        Assert.Equal(2, handler.Requests.Count);
    }
}
