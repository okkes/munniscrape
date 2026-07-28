using System.Text.Json.Serialization;

namespace Connector.Kit.Manifests;

/// <summary>
/// What a provider needs from a user, and what it can deliver.
///
/// This is the only contract a consumer codes against: it renders login
/// forms from <see cref="Auth"/>, decides which resources to offer from
/// <see cref="Resources"/>, and knows before asking whether scheduled sync
/// is even offerable from <see cref="Unattended"/>. Adding a provider is a
/// manifest plus an adapter - never a change in the consuming app.
/// </summary>
public sealed record ProviderManifest
{
    /// <summary>Route namespace: <c>/v1/{id}/*</c>. Lowercase, kebab-case.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required ProviderKind Kind { get; init; }

    /// <summary>ISO-3166 alpha-2, uppercase.</summary>
    public required string Country { get; init; }

    /// <summary>
    /// Bumped on any breaking change to the session material's shape. Sealed
    /// into every bundle as AAD, so a bundle minted before the change is
    /// rejected rather than misinterpreted.
    /// </summary>
    public required int ManifestVersion { get; init; }

    /// <summary>How the data is actually obtained. Drives agent routing.</summary>
    public required ProviderRuntime Runtime { get; init; }

    public required AgentRequirement Agent { get; init; }

    /// <summary>Can a fetch complete with no human present?</summary>
    public required bool Unattended { get; init; }

    /// <summary>Where the long-lived secret lives at rest.</summary>
    public required SecretCustody SecretCustody { get; init; }

    /// <summary>
    /// Whether browser clients may connect. Web bundles are never persisted
    /// across a browser restart, so web users re-authenticate each visit.
    /// </summary>
    public WebSupport WebSupport { get; init; } = WebSupport.Ephemeral;

    public required AuthSpec Auth { get; init; }

    public required IReadOnlyList<ResourceSpec> Resources { get; init; }

    public ProviderLimits Limits { get; init; } = new();

    /// <summary>Consumer-owned copy key for any provider-specific caveat.</summary>
    public string? NotesKey { get; init; }

    /// <summary>Asset hint; the consumer resolves the actual image itself.</summary>
    public string? LogoRef { get; init; }

    public ResourceSpec? Resource(string resourceId) =>
        Resources.FirstOrDefault(r => string.Equals(r.Id, resourceId, StringComparison.Ordinal));
}

[JsonConverter(typeof(JsonStringEnumConverter<ProviderKind>))]
public enum ProviderKind
{
    Bank,
    Store,
}

/// <summary>
/// The four runtime tiers. A provider's tier is a finding, not a plan - it
/// is established by reading the provider, and it may move in either
/// direction as the provider changes.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProviderRuntime>))]
public enum ProviderRuntime
{
    /// <summary>T1. No browser, ever.</summary>
    Http,

    /// <summary>T2. Browser drives login once; a refresh token serves every fetch after.</summary>
    BrowserOnce,

    /// <summary>T3. Browser whenever the session is stale; the run may stop to ask a human.</summary>
    BrowserInteractive,

    /// <summary>T4. Persistent profile that stays logged in. The human authenticates once, ever.</summary>
    BrowserPersistent,
}

[JsonConverter(typeof(JsonStringEnumConverter<SecretCustody>))]
public enum SecretCustody
{
    /// <summary>The user's device holds an opaque sealed bundle. The default.</summary>
    Client,

    /// <summary>The service vault holds it, envelope-encrypted. Only where Unattended is true.</summary>
    Server,

    /// <summary>A BYO agent holds it. The control plane never has it at all.</summary>
    Agent,
}

[JsonConverter(typeof(JsonStringEnumConverter<WebSupport>))]
public enum WebSupport
{
    /// <summary>Web may connect; the bundle dies with the tab session.</summary>
    Ephemeral,

    /// <summary>Web may not connect - the login is too heavy to repeat per visit.</summary>
    None,
}

[JsonConverter(typeof(JsonStringEnumConverter<AgentClass>))]
public enum AgentClass
{
    /// <summary>Runs in the control plane's own process. HTTP-only providers.</summary>
    Inline,

    /// <summary>The operator's agent fleet.</summary>
    Pooled,

    /// <summary>The user's own machine.</summary>
    Byo,
}

public sealed record AgentRequirement
{
    public required bool Required { get; init; }

    public required AgentClass Class { get; init; }

    public EgressRequirement? Egress { get; init; }

    public static AgentRequirement Inline { get; } = new() { Required = false, Class = AgentClass.Inline };
}

public sealed record EgressRequirement
{
    /// <summary>ISO-3166 alpha-2, uppercase.</summary>
    public required string Country { get; init; }

    /// <summary><c>residential</c> or <c>any</c>.</summary>
    public required string Kind { get; init; }
}

public sealed record ProviderLimits
{
    /// <summary>
    /// Enforced server-side, not advisory. Six hours by default - human
    /// plausible, not a firehose.
    /// </summary>
    public int MinIntervalSeconds { get; init; } = 21_600;

    /// <summary>Per session. Always 1; the field exists to make that explicit.</summary>
    public int Concurrency { get; init; } = 1;

    public int MaxHistoryDays { get; init; } = 365;

    /// <summary>
    /// How long a provider may take to surface a transaction that settles
    /// late. Any caller-supplied <c>since</c> is widened by this, because
    /// fetching strictly since the last sync loses late-settling rows
    /// permanently and invisibly.
    /// </summary>
    public int SettlementLagDays { get; init; }
}
