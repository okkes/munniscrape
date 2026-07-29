using Connector.Kit.Adapters;
using Connector.Kit.Hosting.Auth;
using Connector.Kit.Hosting.Challenges;
using Connector.Kit.Hosting.Data;
using Connector.Kit.Hosting.Endpoints;
using Connector.Kit.Hosting.Infrastructure;
using Connector.Kit.Hosting.Jobs;
using Connector.Kit.Hosting.Live;
using Connector.Kit.Hosting.Providers;
using Connector.Kit.Hosting.Staging;
using Connector.Kit.Hosting.Sessions;
using Connector.Kit.Hosting.Tickets;
using Connector.Kit.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Connector.Kit.Hosting;

/// <summary>
/// The whole control plane, as four calls.
///
/// Both products' APIs are meant to be thin shells over this - register
/// adapters, map the routes - so that everything a control plane does exists
/// once. A behaviour that lived in one product's <c>Program.cs</c> would be a
/// behaviour the other one silently lacks.
/// </summary>
public static class ConnectorPlatform
{
    public static IServiceCollection AddConnectorPlatform(
        this IServiceCollection services,
        IConfiguration config,
        Action<ConnectorPlatformOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        var platform = new ConnectorPlatformOptions();
        configure?.Invoke(platform);
        platform.Apply(services);
        services.AddSingleton(platform);

        var options = config.GetSection(ConnectorOptions.SectionName).Get<ConnectorOptions>() ?? new ConnectorOptions();
        Validate(options);
        services.AddSingleton(Options.Create(options));

        services.TryAddDefaults();

        services.AddDbContext<ConnectorDbContext>(builder => ConnectorDbContext.Configure(builder, options.Database));

        services.AddSingleton<IProviderRegistry>(sp =>
            new ProviderRegistry(sp.GetServices<IProviderAdapter>()));

        services.AddSingleton<IBundleKeyRing>(_ => BuildKeyRing(options));
        services.AddSingleton(sp => new SealedBundleCodec(
            sp.GetRequiredService<IBundleKeyRing>(), sp.GetRequiredService<TimeProvider>()));

        services.AddSingleton<ConnectorSignals>();

        // A singleton because a streamed login's frames and its input have to
        // meet in one place, and process memory is the only place either may
        // be: a frame written to the database would put the provider's
        // authenticated pixels at rest in the control plane, which is the
        // custody problem streaming exists to remove.
        services.AddSingleton(sp => new LiveChannel(sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ITicketStore>(sp => new InMemoryTicketStore(sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IIdempotencyStore>(sp => new InMemoryIdempotencyStore(sp.GetRequiredService<TimeProvider>()));

        services.AddScoped<ILeasedJobQueue, EfLeasedJobQueue>();
        services.AddScoped<SessionService>();
        services.AddScoped<ChallengeService>();
        services.AddScoped<ResultService>();
        services.AddScoped<ProviderStatusService>();
        services.AddScoped<JobOutcomeService>();
        services.AddScoped<ViewBuilder>();
        services.AddScoped<FetchRunner>();

        services.AddSingleton<ConnectorAuth>();
        services.AddScoped<AgentAuth>();
        services.AddSingleton<ConnectorAuthFilter>();
        services.AddSingleton<AgentAuthFilter>();
        services.AddSingleton<ConnectorExceptionFilter>();

        // Every outbound provider call goes through the politeness limiter.
        // Registering it on the named client rather than leaving it to
        // adapters is what makes "human-plausible cadence, never a firehose"
        // a property of the platform.
        services.AddTransient<PolitenessHandler>();
        services.AddHttpClient(InlineJobRunner.HttpClientName)
            .AddHttpMessageHandler<PolitenessHandler>();

        services.AddSingleton<IInlineJobRunner, InlineJobRunner>();
        if (platform.RunInlineJobs) services.AddHostedService<InlineJobPump>();
        if (platform.RunExpiryService) services.AddHostedService<ExpiryService>();

        if (options.IsProduction)
        {
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(jwt =>
                {
                    jwt.Authority = options.Auth.Authority;
                    jwt.Audience = options.Auth.Audience;
                    jwt.RequireHttpsMetadata = true;
                });
        }

        // Responses written through ConnectorResults already carry the wire
        // encoding; configuring it here as well means a host that adds its own
        // endpoint gets the same one instead of camelCase by accident.
        services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.PropertyNamingPolicy = ConnectorJson.Options.PropertyNamingPolicy;
            json.SerializerOptions.DefaultIgnoreCondition = ConnectorJson.Options.DefaultIgnoreCondition;
            foreach (var converter in ConnectorJson.Options.Converters) json.SerializerOptions.Converters.Add(converter);
        });

        return services;
    }

    /// <summary>
    /// The public API: the catalogue, sessions, resources, jobs and agents,
    /// all provider-namespaced and all served by one generic handler per
    /// shape. Adding a provider never touches this method.
    /// </summary>
    public static IEndpointRouteBuilder MapConnectorApi(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var platform = app.ServiceProvider.GetRequiredService<ConnectorPlatformOptions>();

        var api = app.MapGroup("/v1")
            // Order matters: the exception filter is outermost so an auth
            // failure comes back as an envelope like everything else.
            .AddEndpointFilter<ConnectorExceptionFilter>()
            .AddEndpointFilter<ConnectorAuthFilter>();

        CatalogEndpoints.Map(api, app, platform);
        LoginEndpoints.Map(api, platform);
        LiveEndpoints.MapConsumer(api);
        ResourceEndpoints.Map(api);
        JobEndpoints.Map(api);
        AgentAdminEndpoints.Map(api);

        return app;
    }

    /// <summary>
    /// The agent protocol, on its own path and its own identity class. A
    /// per-agent token reaches this and nothing else.
    /// </summary>
    public static IEndpointRouteBuilder MapAgentApi(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        AgentApiEndpoints.Map(app);
        return app;
    }

    /// <summary>
    /// Brings the schema up and gives every registered provider a health row.
    /// Called once at start-up, before the first request.
    /// </summary>
    public static WebApplication UseConnectorPlatform(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ConnectorDbContext>();
        db.EnsureCreatedOrMigrateAsync().GetAwaiter().GetResult();

        var statuses = scope.ServiceProvider.GetRequiredService<ProviderStatusService>();
        statuses.SeedAsync(CancellationToken.None).GetAwaiter().GetResult();

        // Development only, and Validate has already refused it anywhere else.
        var options = scope.ServiceProvider.GetRequiredService<IOptions<ConnectorOptions>>().Value;
        if (!options.IsProduction && options.DevEnrollmentCode is { Length: > 0 } devCode)
        {
            scope.ServiceProvider.GetRequiredService<AgentAuth>()
                .SeedDevEnrollmentAsync(devCode, CancellationToken.None).GetAwaiter().GetResult();
        }

        // Touching the registry here rather than lazily means a manifest that
        // lies fails the deploy instead of the first user.
        _ = scope.ServiceProvider.GetRequiredService<IProviderRegistry>().CatalogDigest;

        return app;
    }

    private static void TryAddDefaults(this IServiceCollection services)
    {
        if (services.All(d => d.ServiceType != typeof(TimeProvider)))
        {
            services.AddSingleton(TimeProvider.System);
        }

        services.AddLogging();
        services.AddRouting();
    }

    /// <summary>
    /// The start-up refusals.
    ///
    /// A production service that boots without real bundle keys would seal
    /// every user's credentials under a key that dies with the process, and
    /// would look completely healthy doing it. Refusing here is the only
    /// place that failure is cheap.
    /// </summary>
    private static void Validate(ConnectorOptions options)
    {
        if (!options.IsProduction) return;

        var problems = new List<string>();

        if (!options.Bundle.HasKeys)
        {
            problems.Add("Connector:Bundle:CurrentKid and Connector:Bundle:Keys are required in production");
        }

        if (options.Auth.ClientCertificateThumbprints.Count == 0)
        {
            problems.Add("Connector:Auth:ClientCertificateThumbprints must list at least one certificate in production");
        }

        if (string.IsNullOrWhiteSpace(options.Auth.Authority))
        {
            problems.Add("Connector:Auth:Authority is required in production");
        }

        if (string.IsNullOrWhiteSpace(options.Auth.Audience))
        {
            problems.Add("Connector:Auth:Audience is required in production");
        }

        if (string.IsNullOrWhiteSpace(options.EnrollmentHmacKey))
        {
            problems.Add("Connector:EnrollmentHmacKey is required in production");
        }

        if (options.Database.Provider != ConnectorDatabaseProvider.Postgres)
        {
            problems.Add("Connector:Database:Provider must be 'postgres' in production");
        }

        // A fixed, reusable, never-expiring enrollment code is a password, and
        // the one this option exists for is written in a compose file in the
        // repository. Anything holding it could enroll an agent that leases
        // real jobs and receives real credentials.
        if (!string.IsNullOrWhiteSpace(options.DevEnrollmentCode))
        {
            problems.Add("Connector:DevEnrollmentCode must not be set in production");
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "the connector platform refuses to start:" + Environment.NewLine + "- " +
                string.Join(Environment.NewLine + "- ", problems));
        }
    }

    private static IBundleKeyRing BuildKeyRing(ConnectorOptions options)
    {
        if (options.Bundle.HasKeys)
        {
            return BundleKeyRing.FromBase64(options.Bundle.CurrentKid!, options.Bundle.Keys.AsReadOnly());
        }

        // Development only, and unreachable in production - Validate has
        // already refused to start. A restart invalidates every bundle, which
        // is the correct local behaviour and an unacceptable deployed one.
        return BundleKeyRing.Ephemeral();
    }
}
