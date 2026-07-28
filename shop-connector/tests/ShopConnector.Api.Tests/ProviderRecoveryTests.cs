using System.Diagnostics;
using System.Net;
using ShopConnector.Api.Tests.Infrastructure;
using Xunit;

namespace ShopConnector.Api.Tests;

/// <summary>
/// Provider health has to be able to go both ways.
///
/// It could only ever get worse: a single unrecognised shape set `degraded`
/// and nothing cleared it, so the catalogue went on telling every user "the
/// provider's site changed and an engineer is on it" for a provider that had
/// been working again for hours. Consumers are told to render that status, so
/// one that cannot recover actively misleads them.
///
/// The recovery is deliberately narrow, and the last test is the important
/// one: an operator's pause must survive a job succeeding, or the kill switch
/// stops working precisely when someone has just pulled it.
///
/// PROVIDER STATUS IS GLOBAL, which makes this file the one place in the suite
/// that mutates state its neighbours can see. Two consequences, both learned
/// the hard way when the first version of these tests went in:
///
///   - It joins the shared collection. On <c>IClassFixture</c> xUnit gives the
///     class its own implicit collection and runs it CONCURRENTLY with the
///     others, against a second copy of the whole host. Two apps racing over
///     one status table is a flake generator.
///   - Every test restores what it changed in a <c>finally</c>. A test that
///     leaves a provider degraded does not fail itself - it fails whichever
///     unrelated test runs next, which is the most expensive kind of failure
///     to diagnose.
/// </summary>
[Collection(ShopApiCollection.Name)]
public sealed class ProviderRecoveryTests(ShopApiFactory factory)
{
    /// <summary>
    /// Status is written by the job that finished, not by the request that
    /// started it, so "the call returned" and "the status has moved" are two
    /// different moments. Polling states the actual expectation - it settles -
    /// rather than asserting on a race and calling the loser a bug.
    /// </summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task A_provider_whose_shape_moved_is_degraded()
    {
        var http = factory.CreateAuthorizedClient();
        try
        {
            // mock-store-broken signs in happily and reports the shape change
            // on the FETCH, which is the realistic shape of this failure: a
            // login page that still works and a data endpoint that moved.
            var connection = await Flows.ConnectAsync(http, "mock-store-broken", Flows.NewSubject("degrade"),
                new Dictionary<string, string> { ["username"] = "u", ["password"] = "p" });

            var ticket = await Flows.ResumeAsync(http, "mock-store-broken", connection);
            using var fetch = await Flows.FetchAsync(
                http, "/v1/mock-store-broken/receipts?since=2026-01-01", ticket);

            Assert.NotEqual(HttpStatusCode.OK, fetch.StatusCode);
            await AwaitStatusAsync(http, "mock-store-broken", "degraded");
        }
        finally
        {
            await ResetAsync(http, "mock-store-broken");
        }
    }

    [Fact]
    public async Task A_successful_job_clears_degraded_without_being_asked()
    {
        var http = factory.CreateAuthorizedClient();
        try
        {
            await SetAsync(http, "mock-store-simple", "degraded", "connect.provider.changed");
            await AwaitStatusAsync(http, "mock-store-simple", "degraded");

            await Flows.ConnectAsync(http, "mock-store-simple", Flows.NewSubject("recover"),
                new Dictionary<string, string> { ["username"] = "u", ["password"] = "p" });

            await AwaitStatusAsync(http, "mock-store-simple", "healthy");
        }
        finally
        {
            await ResetAsync(http, "mock-store-simple");
        }
    }

    [Fact]
    public async Task A_paused_provider_stays_paused_and_refuses_work()
    {
        var http = factory.CreateAuthorizedClient();
        try
        {
            await SetAsync(http, "mock-store-sms", "paused", "connect.provider.paused");

            // A paused provider refuses work outright, which is itself the
            // guarantee: there is no successful job to heal it with. What must
            // never happen is the state quietly reverting on its own.
            using var request = Wire.Post("/v1/mock-store-sms/login", new
            {
                subject = Flows.NewSubject("paused"),
                inputs = new { username = "u", password = "p" },
            });
            using var response = await http.SendAsync(request);

            Assert.True(response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Accepted),
                $"a paused provider accepted work: {(int)response.StatusCode}");
            Assert.Equal("paused", await StateOfAsync(http, "mock-store-sms"));
        }
        finally
        {
            await ResetAsync(http, "mock-store-sms");
        }
    }

    private static async Task SetAsync(HttpClient http, string providerId, string state, string? reasonKey)
    {
        using var request = Wire.Post($"/v1/admin/providers/{providerId}/status", new { state, reasonKey });
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static Task ResetAsync(HttpClient http, string providerId) =>
        SetAsync(http, providerId, "healthy", null);

    private static async Task AwaitStatusAsync(HttpClient http, string providerId, string expected)
    {
        var clock = Stopwatch.StartNew();
        var seen = "unknown";

        while (clock.Elapsed < Settle)
        {
            seen = await StateOfAsync(http, providerId);
            if (string.Equals(seen, expected, StringComparison.Ordinal)) return;
            await Task.Delay(100);
        }

        Assert.Fail($"{providerId} was still '{seen}' after {Settle.TotalSeconds:0}s, expected '{expected}'");
    }

    private static async Task<string> StateOfAsync(HttpClient http, string providerId)
    {
        using var response = await http.GetAsync("/v1/status");
        var body = await response.JsonAsync();

        foreach (var provider in body.GetProperty("providers").EnumerateArray())
        {
            if (provider.GetProperty("provider_id").GetString() == providerId)
            {
                return provider.GetProperty("state").GetString()!;
            }
        }

        throw new InvalidOperationException($"no status row for '{providerId}'");
    }
}
