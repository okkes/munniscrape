using System.Text.Json;
using System.Text.Json.Nodes;
using Connector.Kit.Jobs;
using ShopConnector.Adapters.Fixtures;

namespace ShopConnector.Adapters.Tests.Support;

/// <summary>
/// Fixture access, plus the two mutations every defensive-parsing test
/// needs: add a field the parser has never seen, and take away one it
/// requires. Both start from the recorded payload rather than a hand-written
/// one, so a fixture that drifts breaks these tests too.
/// </summary>
internal static class Fixture
{
    public static JsonDocument Doc(string name) => JsonDocument.Parse(FixtureCatalog.Read(name));

    public static JsonNode Node(string name) =>
        JsonNode.Parse(FixtureCatalog.Read(name))
        ?? throw new InvalidOperationException($"fixture '{name}' parsed to null");

    public static JsonObject Object(string name) =>
        Node(name) as JsonObject
        ?? throw new InvalidOperationException($"fixture '{name}' is not a JSON object");

    public static JsonArray Array(string name) =>
        Node(name) as JsonArray
        ?? throw new InvalidOperationException($"fixture '{name}' is not a JSON array");

    /// <summary>Reparses a mutated node so the parser under test sees a real document.</summary>
    public static JsonDocument Reparse(JsonNode node) => JsonDocument.Parse(node.ToJsonString());
}

internal static class Requests
{
    /// <summary>
    /// The receipts request every shopping provider serves. <c>items</c>
    /// defaults on because the line items and their discounts are what the
    /// reconciliation invariant is checked against.
    /// </summary>
    public static ResourceRequest Receipts(
        DateOnly? since = null, DateOnly? until = null, bool items = true) => new()
    {
        ResourceId = "receipts",
        Since = since,
        Until = until,
        Selections = items
            ? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["include"] = ["items"] }
            : new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
    };

    public static DateOnly Day(int year, int month, int day) => new(year, month, day);
}
