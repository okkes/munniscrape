using Connector.Kit.Adapters;
using Connector.Kit.Hosting.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ShopConnector.Api.Tests.Infrastructure;

/// <summary>
/// Boots the real <c>ShopConnector.Api</c> host - its own <c>Program.cs</c>,
/// its own composition, its own start-up refusals - against Sqlite and
/// development auth.
/// </summary>
public sealed class ShopApiFactory : WebApplicationFactory<Program>
{
    /// <summary>What <c>X-Connector-Key</c> must carry. Absent, every call is a 401.</summary>
    public const string SharedSecret = "test-connector-key";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "shop-api-tests", Guid.NewGuid().ToString("N"));

    public ShopApiFactory()
    {
        Directory.CreateDirectory(_root);

        // Environment variables rather than UseSetting or ConfigureAppConfiguration.
        // AddConnectorPlatform binds its options eagerly inside Program.cs, and the
        // default builder places environment variables after appsettings.Development.json -
        // so this is the only override guaranteed to be both early enough and last.
        Set("Connector__Mode", "Development");
        Set("Connector__Database__Provider", "Sqlite");
        Set("Connector__Database__ConnectionString", $"Data Source={Path.Combine(_root, "connector.db")}");
        Set("Connector__Auth__SharedSecret", SharedSecret);

        // The captcha provider parks on a challenge a few hundred milliseconds in.
        // The default three seconds is enough on a developer's machine and not
        // always on a loaded CI agent; widening it only makes the 202 deterministic,
        // since the wait returns the moment the session settles either way.
        Set("Connector__Timeouts__LoginWaitSeconds", "15");
    }

    /// <summary>A client that is authenticated. Use <see cref="WebApplicationFactory{T}.CreateClient()"/> for the 401 case.</summary>
    public HttpClient CreateAuthorizedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(ConnectorAuth.DevelopmentHeader, SharedSecret);
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Development");

        builder.ConfigureTestServices(services =>
        {
            // None of the registered mock providers rotate their material, so the
            // manifest's own `rotates_on_use: true` contract - a fetch may re-issue
            // the bundle and the caller must persist the newest - has nothing in the
            // shipped fleet to exercise it. This provider exists only for that.
            services.AddSingleton<IProviderAdapter>(new RotatingStoreAdapter());
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Sqlite's connection pool may still hold the file. A leftover
            // temp directory is not worth failing a test run over.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void Set(string key, string value) => Environment.SetEnvironmentVariable(key, value);
}

/// <summary>
/// One host for the whole assembly. Booting a fresh one per class would cost
/// more than the isolation is worth, and every test already scopes itself with
/// its own subject.
/// </summary>
[CollectionDefinition(ShopApiCollection.Name)]
public sealed class ShopApiCollection : ICollectionFixture<ShopApiFactory>
{
    public const string Name = "shop-api";
}
