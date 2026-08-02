using System.Globalization;
using System.Net;
using System.Text.Json;
using ShopConnector.Adapters.Mock;
using ShopConnector.Api.Tests.Infrastructure;

namespace ShopConnector.Api.Tests;

/// <summary>
/// api-spec §2.4 and §8 steps 4-5: the ticket, the user's requested
/// <c>GET …?since=…</c> shape, the rotated bundle, and the ack that makes the
/// connector forget what it just handed over.
/// </summary>
[Collection(ShopApiCollection.Name)]
public sealed class ReceiptsTests(ShopApiFactory factory)
{
    private static readonly Dictionary<string, string> Credentials = new(StringComparer.Ordinal)
    {
        ["username"] = "shopper",
        ["password"] = "hunter2",
    };

    private static readonly Dictionary<string, string> RotatingCredentials = new(StringComparer.Ordinal)
    {
        ["username"] = RotatingStoreAdapter.Username,
        ["password"] = RotatingStoreAdapter.Password,
    };

    /// <summary>
    /// The shipped fixture is anchored to July 2026, so this window holds every
    /// receipt in it until mid-2027 - after which the fixture, not the test,
    /// needs moving.
    /// </summary>
    private const string Since = "2026-06-01";

    [Fact]
    public async Task A_ticketed_fetch_returns_normalised_receipts_and_an_ack_purges_them()
    {
        const string provider = MockStoreAdapters.Simple;

        using var http = factory.CreateAuthorizedClient();
        var connection = await Flows.ConnectAsync(http, provider, Flows.NewSubject("receipts"), Credentials);
        var ticket = await Flows.ResumeAsync(http, provider, connection);

        var page = await Flows.FetchPageAsync(
            http, provider, $"/v1/{provider}/receipts?since={Since}&include=items", ticket);

        Assert.Equal("receipts", page.Text("resource"));
        Assert.True(page.GetProperty("complete").GetBoolean(), "the fixture fits inside one pass");

        var cursor = page.Text("cursor");
        Assert.StartsWith("cur_", cursor, StringComparison.Ordinal);

        var receipts = page.GetProperty("data").EnumerateArray().ToList();
        Assert.Equal(3, receipts.Count);

        // One schema for every provider: a caller never learns which one answered
        // beyond the merchant it asked about.
        foreach (var receipt in receipts)
        {
            Assert.StartsWith("rcp_", receipt.Text("id"), StringComparison.Ordinal);
            Assert.StartsWith("sha256:", receipt.Text("content_hash"), StringComparison.Ordinal);
            Assert.Equal(provider, receipt.GetProperty("merchant").Text("id"));
            Assert.Equal("EUR", receipt.GetProperty("total").Text("currency"));

            // A real offset, never a bare date: a near-midnight purchase would
            // otherwise land on the wrong day on the consumer's side.
            Assert.True(receipt.GetProperty("purchased_at").TryGetDateTimeOffset(out var purchasedAt));
            Assert.NotEqual(TimeSpan.Zero, purchasedAt.Offset);

            // Stated on every receipt rather than only where it is true, so a
            // consumer can read one field instead of guessing from its absence.
            // This provider states its own totals, so ours is never a sum of
            // the lines - which is what makes `reconciled` below mean something.
            Assert.False(receipt.GetProperty("total_is_derived").GetBoolean());
        }

        var byExternalId = receipts.ToDictionary(r => r.Text("external_id"), StringComparer.Ordinal);

        // Money is minor units and an ISO-4217 code, never a float.
        Assert.Equal(1085, byExternalId["mock-2026-07-19-0001"].GetProperty("total").GetProperty("value").GetInt64());

        // Discount lines are negative, because without them the items do not sum
        // to the total and reconciliation would be meaningless.
        var discount = byExternalId["mock-2026-07-19-0001"].GetProperty("items")[0].GetProperty("discount");
        Assert.Equal(-30, discount.GetProperty("amount").GetProperty("value").GetInt64());

        // A receipt whose items do not reconcile is emitted with the verdict
        // rather than dropped: dropping it would hide a real purchase, and
        // trusting it silently would hand over a total we know is inconsistent.
        Assert.False(byExternalId["mock-2026-06-28-0003"].GetProperty("reconciled").GetBoolean());
        Assert.True(byExternalId["mock-2026-07-19-0001"].GetProperty("reconciled").GetBoolean());

        // This adapter reported no refreshed material, so nothing is re-issued.
        // A bundle appearing here would mean the connector was handing back
        // something it had merely been holding.
        Assert.False(page.TryGetProperty("session", out _), "an unrotated fetch must not re-issue a bundle");

        Assert.Equal(3, Db.StagedRows(factory, connection.SessionId));

        using (var ack = await Flows.PostWithTicketAsync(
                   http, $"/v1/{provider}/receipts/ack", new { Cursor = cursor }, ticket))
        {
            Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
            Assert.Equal(3, (await ack.JsonAsync()).GetProperty("purged").GetInt32());
        }

        // "Delivered, purge it" is literal. This is what keeps the connector a
        // pipe rather than a second copy of the consumer's database.
        Assert.Equal(0, Db.StagedRows(factory, connection.SessionId));

        using var replay = await Flows.PostWithTicketAsync(
            http, $"/v1/{provider}/receipts/ack", new { Cursor = cursor }, ticket);

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(0, (await replay.JsonAsync()).GetProperty("purged").GetInt32());
    }

    [Fact]
    public async Task A_fetch_that_rotated_the_provider_session_re_issues_the_bundle()
    {
        const string provider = RotatingStoreAdapter.ProviderId;

        using var http = factory.CreateAuthorizedClient();
        var connection = await Flows.ConnectAsync(http, provider, Flows.NewSubject("rotate"), RotatingCredentials);
        var ticket = await Flows.ResumeAsync(http, provider, connection);

        var since = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var page = await Flows.FetchPageAsync(
            http, provider, $"/v1/{provider}/receipts?since={since}", ticket);

        Assert.Equal(2, page.GetProperty("data").GetArrayLength());

        var session = page.GetProperty("session");
        Assert.True(session.GetProperty("rotated").GetBoolean());

        var rotated = session.Text("bundle");
        Assert.StartsWith("sb_v1.", rotated, StringComparison.Ordinal);
        Assert.NotEqual(connection.Bundle, rotated);

        // The re-issued bundle is a real one, not a token that merely looks like
        // it: the caller can hand it straight back and keep working.
        var ticketFromRotated = await Flows.ResumeAsync(http, provider, connection with { Bundle = rotated });
        Assert.StartsWith("tkt_", ticketFromRotated, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_one_shot_form_needs_no_ticket_and_answers_the_same_way()
    {
        const string provider = MockStoreAdapters.Simple;

        using var http = factory.CreateAuthorizedClient();
        var connection = await Flows.ConnectAsync(http, provider, Flows.NewSubject("one-shot"), Credentials);

        using var request = Wire.Post($"/v1/{provider}/receipts:fetch", new
        {
            Subject = connection.Subject,
            Bundle = connection.Bundle,
            Params = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["since"] = Since,
                ["include"] = new[] { "items" },
            },
        });

        using var response = await http.SendAsync(request);
        var page = await response.JsonAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("receipts", page.Text("resource"));
        Assert.Equal(3, page.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public async Task A_fetch_without_a_ticket_is_refused()
    {
        using var http = factory.CreateAuthorizedClient();

        using var response = await http.GetAsync($"/v1/{MockStoreAdapters.Simple}/receipts?since={Since}");
        await ErrorEnvelope.AssertAsync(response, HttpStatusCode.BadRequest, "invalid_request");
    }

    [Fact]
    public async Task A_ticket_minted_for_one_provider_is_not_a_ticket_for_another()
    {
        using var http = factory.CreateAuthorizedClient();
        var connection = await Flows.ConnectAsync(
            http, MockStoreAdapters.Simple, Flows.NewSubject("ticket-scope"), Credentials);

        var ticket = await Flows.ResumeAsync(http, MockStoreAdapters.Simple, connection);

        using var response = await Flows.FetchAsync(
            http, $"/v1/{MockStoreAdapters.Sms}/receipts?since={Since}", ticket);

        await ErrorEnvelope.AssertAsync(response, HttpStatusCode.Unauthorized, "session_expired");
    }
}
