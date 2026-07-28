using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Connector.Kit.Errors;
using Connector.Kit.Security;
using ShopConnector.Adapters.Jumbo;
using ShopConnector.Adapters.Tests.Support;
using Xunit;

namespace ShopConnector.Adapters.Tests;

/// <summary>
/// Jumbo's transport, where a wrong answer is a user-facing bug.
///
/// www.jumbo.com is fronted by Akamai. Bot protection answers 403, and an edge
/// that tarpits a request reports it as 502 or 504. Reading any of those as a
/// credential failure sends a user to reset a password that was fine, leaves
/// the block undiagnosed, and - because a credential failure is never retried
/// by anything - is permanent for that session.
/// </summary>
public sealed class JumboTransportTests
{
    private static readonly JumboOptions Options = new();

    private static JumboGraphQlRequest Operation() => new()
    {
        OperationName = "GetOnlineOrdersAndStoreReceipts",
        Query = "query GetOnlineOrdersAndStoreReceipts { x }",
        Variables = new JsonObject
        {
            ["ordersInput"] = new JsonObject { ["offset"] = 0, ["limit"] = 10 },
            ["page"] = 0,
            ["pageSize"] = 10,
        },
    };

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task A_refusal_is_blocked_by_provider_and_never_invalid_credentials(HttpStatusCode status)
    {
        var handler = new StubHttpHandler((_, _) => Stub.Status(status));
        using var ctx = new FakeJobContext(handler) { Material = JumboFixtures.LiveSession };

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => new HttpJumboGraphQlClient(Options).ExecuteAsync(
                ctx, Operation(), "device-fixture", CancellationToken.None));

        Assert.Equal(ErrorCode.BlockedByProvider, error.Code);
        Assert.NotEqual(ErrorCode.InvalidCredentials, error.Code);

        // We stop; we do not escalate. There is no retry through a block.
        Assert.False(error.Retriable);
        Assert.Contains(ErrorCode.BlockedByProvider, ErrorCatalog.NeverRetry);
    }

    [Fact]
    public async Task An_akamai_interstitial_where_json_was_promised_is_a_shape_change()
    {
        var handler = new StubHttpHandler((_, _) => Stub.Html());
        using var ctx = new FakeJobContext(handler) { Material = JumboFixtures.LiveSession };

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => new HttpJumboGraphQlClient(Options).ExecuteAsync(
                ctx, Operation(), "device-fixture", CancellationToken.None));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
    }

    [Fact]
    public async Task A_rejected_cookie_ends_the_session_rather_than_blaming_a_password()
    {
        var handler = new StubHttpHandler((_, _) => Stub.Status(HttpStatusCode.Unauthorized));
        using var ctx = new FakeJobContext(handler) { Material = JumboFixtures.LiveSession };

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => new HttpJumboGraphQlClient(Options).ExecuteAsync(
                ctx, Operation(), "device-fixture", CancellationToken.None));

        // The human never gave us a password for a fetch, so a rejection here
        // is a dead session, not a wrong credential.
        Assert.Equal(ErrorCode.SessionExpired, error.Code);
        Assert.NotEqual(ErrorCode.InvalidCredentials, error.Code);
    }

    [Fact]
    public async Task Rate_limiting_is_reported_as_rate_limiting()
    {
        var handler = new StubHttpHandler((_, _) => Stub.Status(HttpStatusCode.TooManyRequests));
        using var ctx = new FakeJobContext(handler) { Material = JumboFixtures.LiveSession };

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => new HttpJumboGraphQlClient(Options).ExecuteAsync(
                ctx, Operation(), "device-fixture", CancellationToken.None));

        Assert.Equal(ErrorCode.RateLimited, error.Code);
    }

    /// <summary>
    /// The header block the confirmed working client sends. Two of these were
    /// wrong: the client name and source said <c>JUMBO_MOBILE-orders</c>, which
    /// is corroborated nowhere, and the version said <c>30.14.0</c> - a mobile
    /// version string beside a web client name, which is an inconsistent
    /// fingerprint before it is a wrong value.
    /// </summary>
    [Fact]
    public async Task Every_call_carries_the_web_client_headers_and_the_cookies()
    {
        var handler = new StubHttpHandler((_, _) => Stub.Fixture("jumbo/orders-and-receipts.json"));
        using var ctx = new FakeJobContext(handler) { Material = JumboFixtures.LiveSession };

        using var document = await new HttpJumboGraphQlClient(Options)
            .ExecuteAsync(ctx, Operation(), "device-fixture", CancellationToken.None);

        var request = Assert.Single(handler.Requests);

        Assert.Equal("JUMBO_WEB-orders", request.Header("apollographql-client-name"));
        Assert.Equal("JUMBO_WEB-orders", request.Header("x-source"));
        Assert.Equal("master-v29.2.0-web", request.Header("apollographql-client-version"));

        // A real browser UA, because Akamai scores requests that have none.
        Assert.Contains("Mozilla/5.0", request.Header("User-Agent"), StringComparison.Ordinal);

        // Only jumbo.com cookies, and the session cookie (expires: -1) is not
        // filtered out as "already expired".
        var cookies = request.Header("Cookie");
        Assert.NotNull(cookies);
        Assert.Contains("JMB_SESSION=jumbo-session-cookie-fixture", cookies, StringComparison.Ordinal);
        Assert.Contains("jmb-consent=all", cookies, StringComparison.Ordinal);
        Assert.DoesNotContain("should-not-be-sent", cookies, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sent when the session has one, because a device that changes identity
    /// every run is a fraud signal - and simply omitted when it does not,
    /// because the confirmed working client sends no such header at all and it
    /// is therefore no reason to refuse a fetch.
    /// </summary>
    [Fact]
    public async Task The_device_header_is_carried_when_there_is_one_and_omitted_when_there_is_not()
    {
        var handler = new StubHttpHandler((_, _) => Stub.Fixture("jumbo/orders-and-receipts.json"));
        using var ctx = new FakeJobContext(handler) { Material = JumboFixtures.LiveSession };

        using (await new HttpJumboGraphQlClient(Options)
                   .ExecuteAsync(ctx, Operation(), "device-fixture", CancellationToken.None))
        {
            Assert.Equal("device-fixture", handler.Requests[0].Header("jmb-device-id"));
        }

        using (await new HttpJumboGraphQlClient(Options)
                   .ExecuteAsync(ctx, Operation(), null, CancellationToken.None))
        {
            Assert.Null(handler.Requests[1].Header("jmb-device-id"));
        }
    }

    [Fact]
    public async Task An_operator_can_stop_sending_the_unconfirmed_device_header()
    {
        var handler = new StubHttpHandler((_, _) => Stub.Fixture("jumbo/orders-and-receipts.json"));
        using var ctx = new FakeJobContext(handler) { Material = JumboFixtures.LiveSession };

        using var document = await new HttpJumboGraphQlClient(Options with { SendDeviceIdHeader = false })
            .ExecuteAsync(ctx, Operation(), "device-fixture", CancellationToken.None);

        Assert.Null(Assert.Single(handler.Requests).Header("jmb-device-id"));
    }

    [Fact]
    public async Task A_session_with_no_jumbo_cookies_is_expired_before_any_call()
    {
        var handler = new StubHttpHandler((_, _) => Stub.Fixture("jumbo/orders-and-receipts.json"));
        using var ctx = new FakeJobContext(handler)
        {
            Material = new SessionMaterial { StorageState = """{"cookies":[],"origins":[]}""" },
        };

        var error = await Assert.ThrowsAsync<ConnectorException>(
            () => new HttpJumboGraphQlClient(Options).ExecuteAsync(
                ctx, Operation(), "device-fixture", CancellationToken.None));

        // The cookies ARE the credential; there is nothing else to present.
        Assert.Equal(ErrorCode.SessionExpired, error.Code);
        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// CONFIRMED: the working client posts a plain document and never an
    /// <c>extensions.persistedQuery</c> object. There is no handshake to speak
    /// and no hash to capture.
    /// </summary>
    [Fact]
    public void The_request_body_is_a_plain_document_with_no_persisted_query_extension()
    {
        var body = HttpJumboGraphQlClient.Body(Operation());

        Assert.Equal("GetOnlineOrdersAndStoreReceipts", body["operationName"]!.GetValue<string>());
        Assert.Equal(
            "query GetOnlineOrdersAndStoreReceipts { x }", body["query"]!.GetValue<string>());
        Assert.Equal(10, body["variables"]!["ordersInput"]!["limit"]!.GetValue<int>());
        Assert.Equal(0, body["variables"]!["page"]!.GetValue<int>());

        Assert.False(body.ContainsKey("extensions"));
    }

    [Fact]
    public async Task The_document_reaches_the_wire_intact()
    {
        var handler = new StubHttpHandler((_, _) => Stub.Fixture("jumbo/orders-and-receipts.json"));
        using var ctx = new FakeJobContext(handler) { Material = JumboFixtures.LiveSession };

        var options = new JumboOptions();
        using var document = await new HttpJumboGraphQlClient(options).ExecuteAsync(
            ctx,
            new JumboGraphQlRequest
            {
                OperationName = options.ListOperationName,
                Query = options.ListDocument,
                Variables = new JsonObject(),
            },
            "device-fixture",
            CancellationToken.None);

        var sent = JsonNode.Parse(Assert.Single(handler.Requests).Body!)!;

        // The two aliases the whole parse layer is addressed by.
        var query = sent["query"]!.GetValue<string>();
        Assert.Contains("storeReceipts: receiptOverview", query, StringComparison.Ordinal);
        Assert.Contains("onlineOrders: orders(input: $ordersInput)", query, StringComparison.Ordinal);
        Assert.DoesNotContain("persistedQuery", Assert.Single(handler.Requests).Body!, StringComparison.Ordinal);
    }

    // ---- errors transported in a 200 body ----------------------------------

    [Fact]
    public void A_successful_body_carries_no_errors_to_raise()
    {
        using var document = Fixture.Doc("jumbo/orders-and-receipts.json");

        JumboGraphQlErrors.Throw(document.RootElement);
    }

    [Theory]
    [InlineData("UNAUTHENTICATED")]
    [InlineData("FORBIDDEN")]
    [InlineData("NOT_AUTHENTICATED")]
    public void An_auth_error_in_the_body_is_a_dead_session(string code)
    {
        using var document = JsonDocument.Parse(Errors(code, "no"));

        // GraphQL transports failures in a 200 body, so the errors array has
        // to be read before the data is trusted.
        var error = Assert.Throws<ConnectorException>(() => JumboGraphQlErrors.Throw(document.RootElement));

        Assert.Equal(ErrorCode.SessionExpired, error.Code);
        Assert.NotEqual(ErrorCode.InvalidCredentials, error.Code);
    }

    [Fact]
    public void Any_other_error_in_the_body_is_a_shape_change()
    {
        using var document = JsonDocument.Parse(
            Errors("GRAPHQL_VALIDATION_FAILED", "Unknown field \"getOnlineOrdersAndStoreReceipts\""));

        var error = Assert.Throws<ConnectorException>(() => JumboGraphQlErrors.Throw(document.RootElement));

        Assert.Equal(ErrorCode.ProviderChanged, error.Code);
    }

    private static string Errors(string code, string message) => new JsonObject
    {
        ["errors"] = new JsonArray(new JsonObject
        {
            ["message"] = message,
            ["extensions"] = new JsonObject { ["code"] = code },
        }),
    }.ToJsonString();
}
