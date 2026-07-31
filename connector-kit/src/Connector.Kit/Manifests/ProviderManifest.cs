using System.Text.Json.Serialization;

namespace Connector.Kit.Manifests;

/// <summary>
/// What a provider needs from a user, and what it can deliver.
///
/// This is the only contract a consumer codes against: it renders login
/// forms from <see cref="Auth"/>, decides which resources to offer from
/// <see cref="Resources"/>, and knows before asking whether scheduled sync
/// is even offerable from <see cref="UnattendedFetch"/>. Adding a provider is a
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

    /// <summary>
    /// Can a FETCH complete with no human present?
    ///
    /// The fetch axis and nothing else. It was called <c>Unattended</c>, which
    /// read as a claim about the whole provider and was believed as one: Albert
    /// Heijn and Lidl Plus both declare it true while neither login can finish
    /// without a person - AH streams its own page to them, Lidl sends them to
    /// sign in in their own browser. Both are still true here, because a stored
    /// refresh token really does fetch on its own at three in the morning; what
    /// was wrong was the name promising something about the login too.
    ///
    /// Whether the LOGIN needs a human, and where that human has to be
    /// standing, is <see cref="LoginNeedsHeadedAgent"/> and the challenge list
    /// on <see cref="Auth"/>.
    /// </summary>
    public required bool UnattendedFetch { get; init; }

    /// <summary>
    /// True when this login can meet a wall only a human sitting at the agent's
    /// own browser can pass.
    ///
    /// False does not mean the login needs nobody. It means whoever is needed
    /// can be reached from anywhere - a captcha photographed and relayed to a
    /// phone, a live view of the provider's own page - so any agent will do.
    /// True means the wall is interactive and unrelayable, and the only person
    /// who can pass it is one with hands on that machine.
    ///
    /// Declared rather than discovered. The condition already exists at run
    /// time as <c>IJobContext.Attended</c>, which comes from the agent's own
    /// headless setting, so today a pooled headless agent leases an Amazon
    /// login, drives it for two minutes, meets the widget and fails it
    /// <c>blocked_by_provider</c> - having learnt nothing the catalogue could
    /// not have said up front.
    /// </summary>
    public bool LoginNeedsHeadedAgent { get; init; }

    /// <summary>
    /// What disconnecting does to the account upstream, if anything.
    ///
    /// There was no field, so <c>DELETE /sessions/{id}</c> called
    /// <c>LogoutAsync</c> on every provider - a no-op for fourteen of the
    /// sixteen, each costing a job row, a lease and an agent round trip - and
    /// the consuming app told the user "logged out upstream" every time,
    /// including when nothing of the sort had happened.
    /// </summary>
    public LogoutSupport Logout { get; init; } = LogoutSupport.None;

    /// <summary>
    /// Whether a successful login hands back a sealed copy of what the human
    /// typed, for their own device to keep.
    /// </summary>
    /// <remarks>
    /// For one shape of provider only: a session that cannot be refreshed, so a
    /// fresh username and password is wanted again within a day. Jumbo is the
    /// case - its Auth0 cookie is not refreshable and it wants a real sign-in
    /// roughly daily, so the alternative is asking the same human for the same
    /// password every morning.
    /// <para>
    /// The connector keeps no copy: the bundle is sealed to the user's subject
    /// and handed over once, exactly as a session bundle is. What it costs is
    /// real and belongs written down - a password re-submitted by machine on a
    /// schedule is one that can be wrong without anybody watching, and this
    /// platform never retries a submitted credential precisely because that is
    /// how accounts get locked.
    /// </para>
    /// <para>
    /// Refused at boot for a provider that declares no fields (there is nothing
    /// to store), for one whose session IS refreshable (the refresh already
    /// removes the reason), and for anything but client custody.
    /// </para>
    /// </remarks>
    public bool OffersCredentialStore { get; init; }

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

    /// <summary>The service vault holds it, envelope-encrypted. Only where UnattendedFetch is true.</summary>
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

/// <summary>
/// What disconnecting reaches beyond this platform, if anything.
///
/// The three values are three different promises to make to the person
/// pressing the button, and telling them apart matters most at the far end:
/// signing somebody out of the grocery app on their own phone because they
/// tidied up a connection here is not a tidy-up, it is a surprise.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<LogoutSupport>))]
public enum LogoutSupport
{
    /// <summary>
    /// Nothing upstream. Disconnect purges what is held here and the provider
    /// never learns of it. The default, and true of most adapters.
    /// </summary>
    None,

    /// <summary>
    /// Only the credential this connection holds stops working. The user's own
    /// app, on their own phone, is untouched.
    /// </summary>
    Session,

    /// <summary>
    /// The account's other sessions go too. Declaring this is what lets a
    /// consumer warn somebody before it happens.
    /// </summary>
    Account,
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
