using Connector.Kit.Adapters;
using Connector.Kit.Agent.Browsing;
using Connector.Kit.Agent.Execution;
using Connector.Kit.Agent.Networking;
using Connector.Kit.Agent.Transport;
using Connector.Kit.Manifests;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Connector.Kit.Agent;

/// <summary>
/// The whole composition surface of the data plane. Both products' agents are
/// a <c>Program.cs</c> that calls this and lists their adapters.
/// </summary>
public static class AgentServiceCollectionExtensions
{
    /// <summary>
    /// Registers the agent: the lease loop, the adapter registry, the politeness
    /// limiter, the profile store and the control-plane client.
    /// </summary>
    /// <param name="config">
    /// Bound from the <c>ConnectorAgent</c> section. Anything in
    /// <paramref name="configure"/> wins, so a host can put the endpoint in
    /// configuration and its adapters in code.
    /// </param>
    public static IServiceCollection AddConnectorAgent(
        this IServiceCollection services,
        IConfiguration config,
        Action<ConnectorAgentOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        var options = new ConnectorAgentOptions();
        Bind(options, config.GetSection(ConnectorAgentOptions.SectionName));
        configure?.Invoke(options);

        // Fails at startup rather than on the first lease: a misconfigured
        // agent that looks healthy and never picks up work is worse than one
        // that refuses to boot.
        options.Validate();

        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);

        foreach (var adapter in options.AdapterDescriptors)
        {
            services.Add(adapter);
        }

        services.AddSingleton<IProviderRegistry>(sp => new ProviderRegistry(sp.GetServices<IProviderAdapter>()));
        services.AddSingleton<AgentIdentity>();
        services.AddSingleton(sp => new AgentStateStore(
            options.StateFilePath, sp.GetRequiredService<ILogger<AgentStateStore>>()));
        services.AddSingleton(sp => new ProfileStore(
            options.ProfileRootDirectory, sp.GetRequiredService<ILogger<ProfileStore>>()));
        services.AddSingleton(sp => new PolitenessGate(sp.GetRequiredService<TimeProvider>()));

        services.AddTransient<AgentAuthHandler>();
        services.AddHttpClient(ControlPlaneClient.ClientName, client =>
            {
                client.BaseAddress = options.RequireBaseAddress();
                client.Timeout = options.ControlPlaneTimeout;
            })
            .AddHttpMessageHandler<AgentAuthHandler>();

        services.AddSingleton<ControlPlaneClient>();
        services.AddSingleton<JobRunner>();
        services.AddHostedService<AgentHost>();

        return services;
    }

    /// <summary>
    /// Bound by hand rather than through the configuration binder: the wire
    /// spells runtimes <c>browser_once</c> and the CLR spells them
    /// <c>BrowserOnce</c>, and the binder would simply reject the former.
    /// </summary>
    private static void Bind(ConnectorAgentOptions options, IConfigurationSection section)
    {
        if (!section.Exists()) return;

        if (section["ControlPlaneBaseUrl"] is { Length: > 0 } url &&
            Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            options.ControlPlaneBaseUrl = parsed;
        }

        if (section["EnrollmentCode"] is { Length: > 0 } code) options.EnrollmentCode = code;
        if (section["AgentName"] is { Length: > 0 } name) options.AgentName = name;
        if (section["StateFilePath"] is { Length: > 0 } state) options.StateFilePath = state;
        if (section["ProfileRootDirectory"] is { Length: > 0 } profiles) options.ProfileRootDirectory = profiles;
        if (section["WorkRootDirectory"] is { Length: > 0 } work) options.WorkRootDirectory = work;
        if (section["BrowserDevice"] is { Length: > 0 } device) options.BrowserDevice = device;
        if (section["BrowserLocale"] is { Length: > 0 } locale) options.BrowserLocale = locale;
        if (section["BrowserTimezoneId"] is { Length: > 0 } timezone) options.BrowserTimezoneId = timezone;

        if (int.TryParse(section["MaxConcurrency"], out var concurrency)) options.MaxConcurrency = concurrency;
        if (bool.TryParse(section["Headless"], out var headless)) options.Headless = headless;
        if (TryParseEnum<AgentClass>(section["Class"], out var agentClass)) options.Class = agentClass;

        foreach (var provider in section.GetSection("Providers").GetChildren())
        {
            if (provider.Value is { Length: > 0 } id) options.Providers.Add(id);
        }

        foreach (var runtime in section.GetSection("Runtimes").GetChildren())
        {
            if (TryParseEnum<ProviderRuntime>(runtime.Value, out var tier)) options.Runtimes.Add(tier);
        }

        var egress = section.GetSection("Egress");
        if (egress["Country"] is { Length: > 0 } country && egress["Kind"] is { Length: > 0 } kind)
        {
            options.Egress = new EgressRequirement { Country = country, Kind = kind };
        }
    }

    /// <summary>Accepts both the wire's <c>snake_case</c> and the CLR's <c>PascalCase</c>.</summary>
    private static bool TryParseEnum<T>(string? value, out T parsed) where T : struct, Enum
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        return Enum.TryParse(value.Replace("_", string.Empty, StringComparison.Ordinal), ignoreCase: true, out parsed);
    }
}
