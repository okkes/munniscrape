using System.Reflection;
using Connector.Kit.Adapters;
using Connector.Kit.Manifests;
using Microsoft.Extensions.DependencyInjection;

namespace Connector.Kit.Hosting;

/// <summary>
/// What a host chooses. Everything else is configuration or a default.
///
/// The list is short by design: both products are meant to be a
/// <c>Program.cs</c> that registers its adapters and calls three extension
/// methods. Anything a host has to decide twice is something this library
/// should have decided once.
/// </summary>
public sealed class ConnectorPlatformOptions
{
    private readonly List<Action<IServiceCollection>> _adapters = [];

    /// <summary><c>bank</c> or <c>store</c>. Reported in the catalogue.</summary>
    public ProviderKind ServiceKind { get; set; } = ProviderKind.Store;

    public string ServiceVersion { get; set; } =
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0";

    /// <summary>
    /// Whether this process runs inline-class adapters itself. Off in a
    /// deployment that keeps every adapter on an agent, on by default because
    /// an HTTP-tier provider needing an agent fleet is infrastructure with no
    /// purpose.
    /// </summary>
    public bool RunInlineJobs { get; set; } = true;

    /// <summary>Off only for a test that wants to drive expiry by hand.</summary>
    public bool RunExpiryService { get; set; } = true;

    /// <summary>
    /// When true, a login without recorded consent is refused. The connector
    /// cannot see what terms the consumer showed, so it can only check that
    /// something was recorded and that it is current.
    /// </summary>
    public bool RequireConsent { get; set; }

    /// <summary>The terms version a recorded consent must match, when consent is required.</summary>
    public string? ConsentTermsVersion { get; set; }

    /// <summary>Registers an adapter resolved from DI, so it may take dependencies.</summary>
    public ConnectorPlatformOptions AddAdapter<TAdapter>()
        where TAdapter : class, IProviderAdapter
    {
        _adapters.Add(services => services.AddSingleton<IProviderAdapter, TAdapter>());
        return this;
    }

    /// <summary>Registers an already-built adapter. The straightforward case, and what tests use.</summary>
    public ConnectorPlatformOptions AddAdapter(IProviderAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        _adapters.Add(services => services.AddSingleton(adapter));
        return this;
    }

    internal void Apply(IServiceCollection services)
    {
        foreach (var register in _adapters) register(services);
    }
}
