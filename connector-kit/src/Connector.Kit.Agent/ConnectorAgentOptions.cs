using Connector.Kit.Adapters;
using Connector.Kit.AgentProtocol;
using Connector.Kit.Manifests;
using Microsoft.Extensions.DependencyInjection;

namespace Connector.Kit.Agent;

/// <summary>
/// Everything that makes one agent different from another.
///
/// A pooled agent on the operator's NAS and a user's own BYO agent run the
/// identical image and differ only by what is in here: which providers they
/// advertise, which runtimes they can serve, where their egress is, and
/// whether they hold persistent profiles.
/// </summary>
public sealed class ConnectorAgentOptions
{
    public const string SectionName = "ConnectorAgent";

    private readonly List<ServiceDescriptor> _adapters = [];

    /// <summary>The control plane's root, e.g. <c>https://ledgerbridge.internal/</c>.</summary>
    public Uri? ControlPlaneBaseUrl { get; set; }

    /// <summary>
    /// The one-time enrollment code. Only needed until the agent has enrolled;
    /// after that the state file carries the identity and this is ignored.
    /// </summary>
    public string? EnrollmentCode { get; set; }

    /// <summary>Operator-facing, shown in the user's agent list.</summary>
    public string AgentName { get; set; } = Environment.MachineName;

    /// <summary>Provider ids this agent serves. Empty means "everything registered here".</summary>
    public IList<string> Providers { get; } = [];

    /// <summary>Runtime tiers this agent can serve. Empty means "everything registered here".</summary>
    public IList<ProviderRuntime> Runtimes { get; } = [];

    /// <summary>Where this agent's traffic leaves from. Null means it makes no claim.</summary>
    public EgressRequirement? Egress { get; set; }

    public int MaxConcurrency { get; set; } = 1;

    public AgentClass Class { get; set; } = AgentClass.Pooled;

    /// <summary>
    /// Where the agent id and token are kept between restarts.
    ///
    /// This path, <see cref="ProfileRootDirectory"/> and
    /// <see cref="WorkRootDirectory"/> must be unique per agent INSTANCE. Two
    /// agents sharing them would share one enrollment and sweep each other's
    /// scratch directories out from under running jobs.
    /// </summary>
    public string StateFilePath { get; set; } = Path.Combine(DefaultDataRoot(), "agent-state.json");

    /// <summary>The root under which T4 persistent browser profiles live.</summary>
    public string ProfileRootDirectory { get; set; } = Path.Combine(DefaultDataRoot(), "profiles");

    /// <summary>
    /// The root under which per-job scratch directories are created and
    /// deleted, and which is swept clean at startup. Temp rather than the data
    /// root: nothing here is meant to survive a job, let alone a restart.
    /// </summary>
    public string WorkRootDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "connector-agent-work");

    public bool Headless { get; set; } = true;

    /// <summary>
    /// An honest Playwright device descriptor, e.g. <c>Pixel 5</c>, for a
    /// provider that serves a different site to a phone. Null is desktop
    /// Chromium as it ships.
    /// </summary>
    public string? BrowserDevice { get; set; }

    public string? BrowserLocale { get; set; }

    public string? BrowserTimezoneId { get; set; }

    /// <summary>How long a single provider HTTP call may take.</summary>
    public TimeSpan ProviderHttpTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Backstop between challenge-answer long-polls.</summary>
    public TimeSpan AnswerPollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long a control-plane call may take, long-polls included.</summary>
    public TimeSpan ControlPlaneTimeout { get; set; } = TimeSpan.FromSeconds(70);

    /// <summary>
    /// How long a shutdown waits for in-flight jobs before aborting them. Long
    /// enough for a fetch to finish; a login waiting on a human is not worth
    /// holding a deploy for, and it fails cleanly when the window closes.
    ///
    /// This plus <see cref="ShutdownAbortGrace"/> must fit inside the host's
    /// own <c>HostOptions.ShutdownTimeout</c> (30s by default) - raise both or
    /// the host stops waiting first and the drain buys nothing.
    /// </summary>
    public TimeSpan ShutdownDrainTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>How long aborted jobs get to report their failure upstream.</summary>
    public TimeSpan ShutdownAbortGrace { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>Longest wait between failed lease polls.</summary>
    public TimeSpan MaxLeaseBackoff { get; set; } = TimeSpan.FromSeconds(30);

    internal IReadOnlyList<ServiceDescriptor> AdapterDescriptors => _adapters;

    /// <summary>Registers an adapter to be constructed from the container.</summary>
    public ConnectorAgentOptions AddAdapter<T>() where T : class, IProviderAdapter
    {
        _adapters.Add(ServiceDescriptor.Singleton<IProviderAdapter, T>());
        return this;
    }

    /// <summary>Registers an already-constructed adapter.</summary>
    public ConnectorAgentOptions AddAdapter(IProviderAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        _adapters.Add(ServiceDescriptor.Singleton(adapter));
        return this;
    }

    /// <summary>
    /// The base address with a trailing slash, so relative agent paths resolve
    /// instead of silently replacing the last segment.
    /// </summary>
    public Uri RequireBaseAddress()
    {
        var url = ControlPlaneBaseUrl ?? throw new InvalidOperationException("ControlPlaneBaseUrl is not configured");
        return url.AbsoluteUri.EndsWith('/') ? url : new Uri(url.AbsoluteUri + "/");
    }

    /// <summary>
    /// What this agent advertises.
    ///
    /// An unset provider or runtime list is filled from the adapters actually
    /// registered rather than left empty. Empty means "any" on the wire, and
    /// claiming to serve any provider when the image contains three adapters
    /// earns jobs this agent can only fail.
    /// </summary>
    public AgentCapabilities BuildCapabilities(IReadOnlyList<ProviderManifest> manifests)
    {
        ArgumentNullException.ThrowIfNull(manifests);

        return new AgentCapabilities
        {
            Providers = Providers.Count > 0
                ? [.. Providers]
                : [.. manifests.Select(m => m.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            Runtimes = Runtimes.Count > 0
                ? [.. Runtimes]
                : [.. manifests.Select(m => m.Runtime).Distinct().Order()],
            Egress = Egress,
            MaxConcurrency = MaxConcurrency,
            Class = Class,
        };
    }

    public void Validate()
    {
        var errors = new List<string>();

        if (ControlPlaneBaseUrl is null) errors.Add("ControlPlaneBaseUrl is required");
        else if (!ControlPlaneBaseUrl.IsAbsoluteUri) errors.Add("ControlPlaneBaseUrl must be absolute");

        if (string.IsNullOrWhiteSpace(AgentName)) errors.Add("AgentName is required");
        if (MaxConcurrency < 1) errors.Add("MaxConcurrency must be at least 1");
        if (string.IsNullOrWhiteSpace(StateFilePath)) errors.Add("StateFilePath is required");
        if (string.IsNullOrWhiteSpace(ProfileRootDirectory)) errors.Add("ProfileRootDirectory is required");
        if (string.IsNullOrWhiteSpace(WorkRootDirectory)) errors.Add("WorkRootDirectory is required");
        if (_adapters.Count == 0) errors.Add("register at least one adapter with AddAdapter");

        if (Egress is { } egress)
        {
            if (egress.Kind is not ("residential" or "any"))
            {
                errors.Add($"Egress.Kind '{egress.Kind}' must be 'residential' or 'any'");
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"connector agent configuration is invalid:{Environment.NewLine}- " +
                string.Join(Environment.NewLine + "- ", errors));
        }
    }

    /// <summary>
    /// A per-user data directory, falling back to the install directory in a
    /// container where the user profile is empty.
    /// </summary>
    private static string DefaultDataRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = string.IsNullOrEmpty(local) ? AppContext.BaseDirectory : local;
        return Path.Combine(root, "connector-agent");
    }
}
