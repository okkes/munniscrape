using System.Globalization;
using System.Net;
using System.Text.Json;
using ShopConnector.Adapters.Mock;
using ShopConnector.Api.Tests.Infrastructure;

namespace ShopConnector.Api.Tests;

/// <summary>
/// api-spec §1.6. The catalogue is the only contract a consumer codes against,
/// so it has to be both complete and cacheable: complete because a provider
/// missing from it cannot be connected at all, cacheable because it changes on
/// a deploy at most and a consumer should not refetch it per session.
/// </summary>
[Collection(ShopApiCollection.Name)]
public sealed class CatalogueTests(ShopApiFactory factory)
{
    [Fact]
    public async Task Providers_answers_with_the_manifests_and_an_etag_that_is_the_digest()
    {
        using var http = factory.CreateAuthorizedClient();

        using var response = await http.GetAsync("/v1/providers");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.JsonAsync();
        var service = body.GetProperty("service");

        Assert.Equal("store", service.Text("kind"));
        var digest = service.Text("manifest_digest");
        Assert.StartsWith("sha256:", digest, StringComparison.Ordinal);

        // The ETag opens with the digest, so a consumer can still recognise it
        // from a value it already parsed. It does not END there: provider
        // health is part of this document and therefore part of what the ETag
        // identifies, so a second component moves when health does.
        var etag = Assert.Single(response.RawHeader("ETag"));
        Assert.StartsWith($"\"{digest}:", etag, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Providers_describes_a_manifest_in_the_specs_own_vocabulary()
    {
        using var http = factory.CreateAuthorizedClient();

        using var response = await http.GetAsync("/v1/providers");
        var providers = (await response.JsonAsync()).GetProperty("providers");

        var captcha = Find(providers, MockStoreAdapters.Captcha);

        // Every one of these is an enum in C# and snake case on the wire. A
        // consumer that had to know which is which would be coding against our
        // internals rather than against the published contract.
        Assert.Equal("store", captcha.Text("kind"));
        Assert.Equal("http", captcha.Text("runtime"));
        Assert.Equal("client", captcha.Text("secret_custody"));
        Assert.Equal("ephemeral", captcha.Text("web_support"));
        Assert.Equal("password", captcha.GetProperty("auth").Text("flow"));
        Assert.Equal("receipt", captcha.GetProperty("resources")[0].Text("returns"));
        Assert.Equal("inline", captcha.GetProperty("agent").Text("class"));

        // The consumer renders the login form from this and nothing else.
        var fields = captcha.GetProperty("auth").GetProperty("steps")[0].GetProperty("fields");
        Assert.Equal(new[] { "username", "password" }, fields.EnumerateArray().Select(f => f.Text("key")).ToArray());
        Assert.True(fields[1].GetProperty("secret").GetBoolean(), "a password field must be marked secret");

        // What may interrupt the happy path, so the consumer knows to build for it.
        var challenges = captcha.GetProperty("auth").GetProperty("challenges")
            .EnumerateArray().Select(c => c.GetString()!).ToArray();
        Assert.Equal(new[] { "image" }, challenges);

        // Health is grafted on to the manifest: this is what lets a consumer say
        // "paused, we're fixing it" instead of showing a spinner.
        Assert.Equal("healthy", captcha.GetProperty("status").Text("state"));
    }

    /// <summary>
    /// The catalogue carries provider health, so a consumer holding a cached
    /// copy has to be told when that health moves.
    ///
    /// Hashing only the manifests made this document permanently stale: a
    /// provider degraded, an operator fixed it, and every conditional request
    /// still answered 304 with the old verdict. The consumer's own card said
    /// "a run that succeeds clears this" and no run ever could - the only
    /// escape was a hard refresh, which a caching client has no reason to do.
    /// </summary>
    [Fact]
    public async Task Health_moving_invalidates_a_cached_catalogue()
    {
        using var http = factory.CreateAuthorizedClient();

        using var before = await http.GetAsync("/v1/providers");
        var stale = Assert.Single(before.RawHeader("ETag"));

        await SetStatusAsync(http, MockStoreAdapters.Simple, "degraded", "connect.provider.changed");

        try
        {
            // The same conditional request that was a 304 a moment ago.
            using var request = Wire.Get("/v1/providers");
            request.AddHeader("If-None-Match", stale);

            using var after = await http.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, after.StatusCode);

            var fresh = Assert.Single(after.RawHeader("ETag"));
            Assert.NotEqual(stale, fresh);

            var provider = Find((await after.JsonAsync()).GetProperty("providers"), MockStoreAdapters.Simple);
            Assert.Equal("degraded", provider.GetProperty("status").Text("state"));
        }
        finally
        {
            await SetStatusAsync(http, MockStoreAdapters.Simple, "healthy", null);
        }
    }

    /// <summary>
    /// And back again: recovering has to move the tag too, or a consumer that
    /// cached the degraded document would keep showing a warning about a
    /// provider that is working.
    /// </summary>
    [Fact]
    public async Task Recovering_also_invalidates_a_cached_catalogue()
    {
        using var http = factory.CreateAuthorizedClient();

        await SetStatusAsync(http, MockStoreAdapters.Captcha, "degraded", "connect.provider.changed");

        string degradedTag;
        try
        {
            using var degraded = await http.GetAsync("/v1/providers");
            degradedTag = Assert.Single(degraded.RawHeader("ETag"));
        }
        finally
        {
            await SetStatusAsync(http, MockStoreAdapters.Captcha, "healthy", null);
        }

        using var request = Wire.Get("/v1/providers");
        request.AddHeader("If-None-Match", degradedTag);

        using var after = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, after.StatusCode);

        var provider = Find((await after.JsonAsync()).GetProperty("providers"), MockStoreAdapters.Captcha);
        Assert.Equal("healthy", provider.GetProperty("status").Text("state"));
    }

    /// <summary>
    /// The single-provider document carries the same health, and a consumer
    /// polling one provider it cares about is exactly who would be pinned to a
    /// stale answer longest.
    /// </summary>
    [Fact]
    public async Task Health_moving_invalidates_a_cached_single_provider_document()
    {
        using var http = factory.CreateAuthorizedClient();
        var url = $"/v1/providers/{MockStoreAdapters.Sms}";

        using var before = await http.GetAsync(url);
        var stale = Assert.Single(before.RawHeader("ETag"));

        await SetStatusAsync(http, MockStoreAdapters.Sms, "degraded", "connect.provider.changed");

        try
        {
            using var request = Wire.Get(url);
            request.AddHeader("If-None-Match", stale);

            using var after = await http.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, after.StatusCode);
            Assert.NotEqual(stale, Assert.Single(after.RawHeader("ETag")));
            Assert.Equal("degraded", (await after.JsonAsync()).GetProperty("status").Text("state"));
        }
        finally
        {
            await SetStatusAsync(http, MockStoreAdapters.Sms, "healthy", null);
        }
    }

    private static async Task SetStatusAsync(HttpClient http, string providerId, string state, string? reasonKey)
    {
        using var request = Wire.Post($"/v1/admin/providers/{providerId}/status", new { state, reasonKey });
        using var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_conditional_request_that_matches_the_etag_is_answered_304()
    {
        using var http = factory.CreateAuthorizedClient();

        using var first = await http.GetAsync("/v1/providers");
        var etag = Assert.Single(first.RawHeader("ETag"));

        using var request = Wire.Get("/v1/providers");
        request.AddHeader("If-None-Match", etag);

        using var second = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Empty(await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_etag_is_stable_across_calls()
    {
        using var http = factory.CreateAuthorizedClient();

        using var first = await http.GetAsync("/v1/providers");
        using var second = await http.GetAsync("/v1/providers");

        Assert.Equal(Assert.Single(first.RawHeader("ETag")), Assert.Single(second.RawHeader("ETag")));
    }

    [Fact]
    public async Task A_single_provider_carries_its_own_etag_and_manifest_version_header()
    {
        using var http = factory.CreateAuthorizedClient();

        using var response = await http.GetAsync($"/v1/providers/{MockStoreAdapters.Captcha}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.JsonAsync();
        Assert.Equal(MockStoreAdapters.Captcha, body.Text("id"));

        // Every response that touched a provider says which contract answered.
        var version = Assert.Single(response.RawHeader("X-Manifest-Version"));
        Assert.Equal(
            body.GetProperty("manifest_version").GetInt32().ToString(CultureInfo.InvariantCulture),
            version);

        using var request = Wire.Get($"/v1/providers/{MockStoreAdapters.Captcha}");
        request.AddHeader("If-None-Match", Assert.Single(response.RawHeader("ETag")));

        using var revalidated = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotModified, revalidated.StatusCode);
    }

    private static JsonElement Find(JsonElement providers, string id)
    {
        foreach (var provider in providers.EnumerateArray())
        {
            if (string.Equals(provider.Text("id"), id, StringComparison.Ordinal)) return provider;
        }

        Assert.Fail($"the catalogue does not list '{id}'");
        return default;
    }
}
