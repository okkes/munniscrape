using System.Net;
using Connector.Kit.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ShopConnector.Api.Tests.Infrastructure;

namespace ShopConnector.Api.Tests;

/// <summary>
/// The API reference is a development affordance, and the thing worth testing
/// is not that it renders - it is that it is ABSENT in production.
///
/// It is absent rather than authenticated on purpose. Production /v1/* demands
/// an allowlisted client certificate and an audience-checked M2M token, so a
/// reference page reachable from a browser would describe an API that browser
/// cannot call - and the login it would need in order to be useful is a second
/// way in to a service whose entire design is that there is one.
/// </summary>
[Collection(ShopApiCollection.Name)]
public sealed class ApiReferenceTests(ShopApiFactory factory)
{
    [Theory]
    [InlineData("/scalar")]
    [InlineData("/openapi/v1.json")]
    public async Task Development_serves_the_reference(string path)
    {
        using var http = factory.CreateClient();

        // No X-Connector-Key: the reference is not behind the consumer filter,
        // because a browser has no consumer credential to present.
        using var response = await http.GetAsync(path);

        Assert.True(
            response.IsSuccessStatusCode,
            $"expected {path} to be served in development, got {(int)response.StatusCode}");
    }

    /// <summary>
    /// The document has to describe THIS service, not an empty shell. A
    /// generator that silently produced `{"paths":{}}` would pass a mere
    /// status-code check while documenting nothing.
    /// </summary>
    [Fact]
    public async Task The_document_describes_the_routes_this_service_actually_has()
    {
        using var http = factory.CreateClient();

        var document = await http.GetStringAsync("/openapi/v1.json");

        Assert.Contains("/v1/health", document, StringComparison.Ordinal);
        Assert.Contains("/v1/providers", document, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one that matters. Built the same way the real host builds itself, so
    /// this asserts the wiring rather than a copy of it.
    /// </summary>
    [Theory]
    [InlineData("/scalar")]
    [InlineData("/openapi/v1.json")]
    public async Task Production_serves_no_reference_at_all(string path)
    {
        await using var app = BuildProductionHost();
        await app.StartAsync();

        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        using var response = await http.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// A production host, minus the parts that need real infrastructure.
    /// Sqlite and a bundle key are enough to get past ConnectorPlatform's
    /// start-up refusals; nothing here reaches a database or a provider.
    /// </summary>
    private static WebApplication BuildProductionHost()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Connector:Mode"] = "Production",
            ["Connector:Database:Provider"] = "Postgres",
            ["Connector:Database:ConnectionString"] = "Host=127.0.0.1;Database=unused",
            ["Connector:Bundle:CurrentKid"] = "k1",
            ["Connector:Bundle:Keys:k1"] = Convert.ToBase64String(new byte[32]),
            ["Connector:EnrollmentHmacKey"] = "test-enrollment-hmac",
            ["Connector:Auth:Authority"] = "https://issuer.invalid",
            ["Connector:Auth:Audience"] = "connector.shop",
            ["Connector:Auth:ClientCertificateThumbprints:0"] = new string('a', 40),
        });

        builder.Services.AddConnectorPlatform(builder.Configuration, platform =>
            platform.AddAdapter(new RotatingStoreAdapter()));

        var app = builder.Build();

        // Deliberately NOT UseConnectorPlatform(): that migrates a database.
        // The reference mapping is what is under test and it reads options
        // only.
        app.MapConnectorReference();
        app.MapConnectorApi();

        return app;
    }
}
