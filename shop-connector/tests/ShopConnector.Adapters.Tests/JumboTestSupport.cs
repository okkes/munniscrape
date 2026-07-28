using System.Text.Json;
using System.Text.Json.Nodes;
using Connector.Kit.Adapters;
using Connector.Kit.Challenges;
using Connector.Kit.Security;
using Microsoft.Playwright;
using ShopConnector.Adapters.Fixtures;
using ShopConnector.Adapters.Jumbo;
using ShopConnector.Adapters.Tests.Support;

namespace ShopConnector.Adapters.Tests;

/// <summary>
/// The pieces every Jumbo test needs, in one place.
///
/// Jumbo speaks one endpoint with three operations, so a fake that answered
/// a flat queue of bodies would hand an order-detail response to a receipt
/// call the moment the fetch loop's shape changed. Answering by operation
/// name - and, for the two detail calls, by the id in the variables - is what
/// keeps these tests about the adapter rather than about the order its calls
/// happen to come out in.
/// </summary>
internal sealed class FakeJumboGraphQl : IJumboGraphQlClient
{
    private readonly string[] _listPages;
    private readonly List<JumboGraphQlRequest> _calls = [];
    private readonly List<string?> _deviceIds = [];

    private int _listCalls;

    public FakeJumboGraphQl(params string[] listPages) =>
        _listPages = listPages.Length > 0 ? listPages : [FixtureCatalog.Read("jumbo/orders-and-receipts.json")];

    /// <summary>Keyed by order id; <c>*</c> answers anything else.</summary>
    public Dictionary<string, string> OrderDetails { get; } = new(StringComparer.Ordinal);

    /// <summary>Keyed by transaction id; <c>*</c> answers anything else.</summary>
    public Dictionary<string, string> DigitalReceipts { get; } = new(StringComparer.Ordinal);

    public IReadOnlyList<JumboGraphQlRequest> Calls => _calls;

    public IReadOnlyList<string?> DeviceIds => _deviceIds;

    public IReadOnlyList<JumboGraphQlRequest> ListCalls =>
        [.. _calls.Where(c => string.Equals(c.OperationName, "GetOnlineOrdersAndStoreReceipts", StringComparison.Ordinal))];

    /// <summary>The three fixtures a whole fetch needs, wired up.</summary>
    public static FakeJumboGraphQl Recorded(params string[] listPages)
    {
        var fake = new FakeJumboGraphQl(listPages);

        fake.OrderDetails["90211"] = FixtureCatalog.Read("jumbo/order-detail.json");
        fake.OrderDetails["90118"] = FixtureCatalog.Read("jumbo/order-detail-90118.json");
        fake.OrderDetails["*"] = FixtureCatalog.Read("jumbo/order-detail.json");
        fake.DigitalReceipts["*"] = FixtureCatalog.Read("jumbo/digital-receipt.json");

        return fake;
    }

    public Task<JsonDocument> ExecuteAsync(
        IJobContext ctx, JumboGraphQlRequest request, string? deviceId, CancellationToken ct)
    {
        _calls.Add(request);
        _deviceIds.Add(deviceId);

        var body = request.OperationName switch
        {
            "GetOnlineOrdersAndStoreReceipts" => _listPages[Math.Min(_listCalls++, _listPages.Length - 1)],
            "OrderPagesOrder" => Lookup(OrderDetails, Variable(request, "orderId"), request.OperationName),
            "GetDigitalReceipt" => Lookup(DigitalReceipts, Variable(request, "transactionId"), request.OperationName),
            _ => throw new InvalidOperationException($"unexpected operation '{request.OperationName}'"),
        };

        return Task.FromResult(JsonDocument.Parse(body));
    }

    private static string Variable(JumboGraphQlRequest request, string name) =>
        request.Variables.TryGetPropertyValue(name, out var node) && node is not null
            ? node.ToJsonString().Trim('"')
            : string.Empty;

    private static string Lookup(Dictionary<string, string> bodies, string key, string operation)
    {
        if (bodies.TryGetValue(key, out var exact)) return exact;
        if (bodies.TryGetValue("*", out var fallback)) return fallback;

        throw new InvalidOperationException($"no recorded body for {operation}('{key}')");
    }
}

/// <summary>
/// A browser lease that can do the two things a Jumbo login needs and nothing
/// else: hand back a storage state, and refuse to photograph a page that is
/// still holding a password.
///
/// The refusal is reproduced rather than assumed - a page holding a secret
/// produces no bytes at all, exactly as the agent's redactor does - so a test
/// that gets a picture back has proved the password was gone first.
/// </summary>
internal sealed class JumboBrowserLease : IBrowserLease
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47];

    private readonly StubLoginPage _page;
    private readonly string _storageState;

    public JumboBrowserLease(StubLoginPage page, string? storageState = null)
    {
        _page = page;
        _storageState = storageState ?? FixtureCatalog.Read("jumbo/storage-state.json");
    }

    public bool Started => true;

    public int Captures { get; private set; }

    public CropRegion? LastCrop { get; private set; }

    public Task<IPage> PageAsync(CancellationToken ct) =>
        throw new InvalidOperationException("this test drives the page through the adapter's seam");

    public Task<string> StorageStateAsync(CancellationToken ct) => Task.FromResult(_storageState);

    public Task<byte[]> ScreenshotAsync(CropRegion? crop, CancellationToken ct)
    {
        Captures++;
        LastCrop = crop;
        _page.Record("screenshot");

        return Task.FromResult(_page.HoldsSecret ? Array.Empty<byte>() : Png);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal static class JumboFixtures
{
    /// <summary>The session a fetch runs on: jumbo.com cookies plus a carried device id.</summary>
    public static SessionMaterial LiveSession => new()
    {
        StorageState = FixtureCatalog.Read("jumbo/storage-state.json"),
        DeviceId = "device-fixture",
    };

    public static IReadOnlyDictionary<string, string> Credentials { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["username"] = "shopper@example.test",
            ["password"] = "correct horse battery staple",
        };

    /// <summary>The first row of an order list page, as the parser sees it.</summary>
    public static JsonNode OrdersFixture() =>
        JsonNode.Parse(FixtureCatalog.Read("jumbo/orders-and-receipts.json"))!;

    public static JsonArray Orders(JsonNode root) => root["data"]!["onlineOrders"]!["orders"]!.AsArray();

    public static JsonArray StoreReceipts(JsonNode root) => root["data"]!["storeReceipts"]!["receipts"]!.AsArray();
}
