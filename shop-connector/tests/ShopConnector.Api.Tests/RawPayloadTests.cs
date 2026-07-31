using System.Net;
using System.Text.Json;
using Connector.Kit.Hosting.Data;
using Microsoft.Extensions.DependencyInjection;
using ShopConnector.Api.Tests.Infrastructure;

namespace ShopConnector.Api.Tests;

/// <summary>
/// The provider's own answer, kept beside the normalised record.
///
/// Nothing retained raw at all: an adapter parsed the provider's document into
/// records and the document was gone. When a provider renames a field the
/// normalised record simply loses a value and says nothing about why, and the
/// only way to see what actually arrived was to reproduce the fetch by hand
/// against a live account.
///
/// It is opt-in and it rides the same row as the record it belongs to. Both of
/// those are the design: raw is strictly the more sensitive of the two -
/// normalisation drops the fields nobody asked for and this puts them back -
/// so it must never be a default, and it must never acquire a lifetime of its
/// own. The ack that purges the record purges the document with it, for free,
/// because there is nothing else to purge.
/// </summary>
[Collection(ShopApiCollection.Name)]
public sealed class RawPayloadTests(ShopApiFactory factory)
{
    private const string Provider = RotatingStoreAdapter.ProviderId;

    private static readonly Dictionary<string, string> Credentials = new(StringComparer.Ordinal)
    {
        ["username"] = RotatingStoreAdapter.Username,
        ["password"] = RotatingStoreAdapter.Password,
    };

    [Fact]
    public async Task A_fetch_that_asks_for_raw_gets_the_providers_own_document_beside_each_record()
    {
        using var http = factory.CreateAuthorizedClient();
        var connection = await ConnectAsync(http, "raw-on");

        var page = await FetchAsync(http, connection, "include=items,raw");

        var records = page.GetProperty("data").EnumerateArray().ToList();
        Assert.NotEmpty(records);

        foreach (var record in records)
        {
            var externalId = record.GetProperty("external_id").GetString();

            // Grafted onto the record itself, so a caller reading a row has
            // the document that produced it without a second lookup.
            Assert.Equal(externalId, record.GetProperty("raw").GetProperty("provider_said").GetString());
        }

        // And it really is at rest beside the record, not assembled on the way
        // out - which is what makes the ack below the thing that removes it.
        Assert.All(
            StagedRaw(connection.SessionId),
            raw => Assert.Contains("provider_said", raw!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_fetch_that_does_not_ask_for_raw_stores_none_and_returns_none()
    {
        using var http = factory.CreateAuthorizedClient();
        var connection = await ConnectAsync(http, "raw-off");

        var page = await FetchAsync(http, connection, "include=items");

        var records = page.GetProperty("data").EnumerateArray().ToList();
        Assert.NotEmpty(records);
        Assert.All(records, record => Assert.False(record.TryGetProperty("raw", out _)));

        // Never a default. A caller that did not ask has not consented to the
        // fields normalisation exists to drop.
        Assert.All(StagedRaw(connection.SessionId), Assert.Null);
    }

    /// <summary>
    /// The reason raw lives on the record's own row. A later pass that does not
    /// ask for it must not leave an earlier pass's document lying there.
    /// </summary>
    [Fact]
    public async Task A_later_fetch_without_raw_clears_what_an_earlier_one_left()
    {
        using var http = factory.CreateAuthorizedClient();
        var connection = await ConnectAsync(http, "raw-cleared");

        await FetchAsync(http, connection, "include=items,raw");
        Assert.All(StagedRaw(connection.SessionId), Assert.NotNull);

        await FetchAsync(http, connection, "include=items");
        Assert.All(StagedRaw(connection.SessionId), Assert.Null);
    }

    [Fact]
    public async Task Acknowledging_the_pass_takes_the_document_with_it()
    {
        using var http = factory.CreateAuthorizedClient();
        var connection = await ConnectAsync(http, "raw-ack");

        var page = await FetchAsync(http, connection, "include=items,raw");
        var cursor = page.GetProperty("cursor").GetString();

        Assert.NotEmpty(StagedRaw(connection.SessionId));

        var ticket = await Flows.ResumeAsync(http, Provider, connection);
        using var ack = await Flows.PostWithTicketAsync(
            http, $"/v1/{Provider}/receipts/ack", new { cursor }, ticket);

        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);

        // Nothing left to hold a document. The connector is a pipe, and raw is
        // the part of the traffic it would least want to be a pipe's residue.
        Assert.Equal(0, Db.StagedRows(factory, connection.SessionId));
    }

    // ---- helpers -----------------------------------------------------------

    private static Task<Connection> ConnectAsync(HttpClient http, string label) =>
        Flows.ConnectAsync(http, Provider, Flows.NewSubject(label), Credentials);

    private static async Task<JsonElement> FetchAsync(HttpClient http, Connection connection, string include)
    {
        var ticket = await Flows.ResumeAsync(http, Provider, connection);
        var since = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30).ToString("O");

        return await Flows.FetchPageAsync(
            http, Provider, $"/v1/{Provider}/receipts?since={since}&{include}", ticket);
    }

    /// <summary>
    /// The stored documents, straight from the table. Asserted directly because
    /// "it was never written" and "it was written and then hidden on the way
    /// out" are the same thing through the API and very different at rest.
    /// </summary>
    private List<string?> StagedRaw(string sessionId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();

        return [.. db.Results.Where(r => r.SessionId == sessionId).Select(r => r.RawJson)];
    }
}
