using System.Text.Json.Serialization;

namespace Connector.Kit.Hosting;

/// <summary>
/// Everything the control plane reads from configuration, bound from the
/// <c>Connector</c> section.
///
/// The one rule that is not a preference: in <see cref="ConnectorMode.Production"/>
/// the service refuses to start without real bundle keys, a client-certificate
/// allowlist and a JWT authority. A misconfigured connector that boots is worse
/// than one that does not, because it boots holding people's credentials.
/// </summary>
public sealed class ConnectorOptions
{
    public const string SectionName = "Connector";

    public ConnectorMode Mode { get; set; } = ConnectorMode.Development;

    public ConnectorDatabaseOptions Database { get; set; } = new();

    public ConnectorAuthOptions Auth { get; set; } = new();

    public BundleKeyOptions Bundle { get; set; } = new();

    public ConnectorTimeouts Timeouts { get; set; } = new();

    /// <summary>
    /// HMAC key for one-time agent enrollment codes. Generated per stack; a
    /// development default is minted in memory so a local run still works.
    /// </summary>
    public string? EnrollmentHmacKey { get; set; }

    /// <summary>
    /// Subjects whose agents are the operator's own fleet and may therefore
    /// serve every user. Every other agent serves only the subject that
    /// enrolled it.
    ///
    /// This is operator configuration and not something an agent can say about
    /// itself, which is the whole point. <see cref="AgentClass"/> looks like
    /// the natural gate and is not one: an agent declares its own class at
    /// enrollment and overwrites it on every heartbeat, so a machine that
    /// wanted other people's jobs would simply call itself pooled.
    /// <c>OwnerSubject</c> is trustworthy because it rides the one-time
    /// enrollment code rather than the request - but the operator's own fleet
    /// enrolls through that same endpoint, so it needs naming here.
    ///
    /// Empty means every agent is owner-scoped, which is the safe default: the
    /// failure mode is a pooled job that nobody leases, not a credential
    /// handed to the wrong machine.
    /// </summary>
    public IList<string> FleetSubjects { get; set; } = [];

    /// <summary>
    /// Development only: a fixed enrollment code, seeded into the enrollment
    /// table at start-up so an agent in the same compose file can enroll
    /// unattended.
    ///
    /// Real enrollment codes are minted one at a time over an authenticated
    /// admin call, are single-use and expire in fifteen minutes. That is
    /// correct and it is also unusable from a compose file: the agent starts
    /// beside the control plane with nobody present to carry a code between
    /// them, and a `docker compose up` that needs a human in the middle is not
    /// a stack anybody will run twice.
    ///
    /// Seeded against <see cref="DevFleetSubject"/> rather than a real user's
    /// pseudonym, because the local agent has to serve whoever happens to be
    /// clicking. Listing that subject in <see cref="FleetSubjects"/> is what
    /// actually grants it - this option only creates the enrollment.
    ///
    /// <see cref="ConnectorPlatform"/> refuses to start if this is set in
    /// production. A reusable code is a password, and this one is written in a
    /// checked-in compose file.
    /// </summary>
    public string? DevEnrollmentCode { get; set; }

    /// <summary>
    /// The subject a <see cref="DevEnrollmentCode"/> enrollment belongs to.
    /// Not a pseudonym of any real person: no HMAC of any user id can collide
    /// with it, so it cannot silently become somebody's owner scope.
    /// </summary>
    public const string DevFleetSubject = "dev-fleet";

    public bool IsProduction => Mode == ConnectorMode.Production;
}

[JsonConverter(typeof(JsonStringEnumConverter<ConnectorMode>))]
public enum ConnectorMode
{
    Development,
    Production,
}

public sealed class ConnectorDatabaseOptions
{
    public ConnectorDatabaseProvider Provider { get; set; } = ConnectorDatabaseProvider.Sqlite;

    public string ConnectionString { get; set; } = "Data Source=connector.db";
}

/// <summary>
/// The same context runs on both. Postgres in production, Sqlite for tests
/// and a local run - which is only possible because every mapping is
/// provider-agnostic: strings for enums and JSON, <see cref="DateTimeOffset"/>
/// in UTC for time, and no Postgres-only column type anywhere.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConnectorDatabaseProvider>))]
public enum ConnectorDatabaseProvider
{
    Sqlite,
    Postgres,
}

public sealed class ConnectorAuthOptions
{
    /// <summary>Development only: the value expected in <c>X-Connector-Key</c>.</summary>
    public string? SharedSecret { get; set; }

    /// <summary>Production: the OIDC authority that issues the M2M token.</summary>
    public string? Authority { get; set; }

    public string? Audience { get; set; }

    /// <summary>
    /// Production: SHA-1 thumbprints of the client certificates allowed to
    /// reach this service. Empty in production is a refusal to start - mTLS is
    /// the layer that survives a compromised container on the same network.
    /// </summary>
    public IList<string> ClientCertificateThumbprints { get; set; } = [];

    /// <summary>
    /// Where TLS terminates upstream, the proxy forwards the verified
    /// thumbprint in this header instead of the connection carrying a cert.
    /// </summary>
    public string ClientCertificateHeader { get; set; } = "X-Client-Cert-Thumbprint";
}

public sealed class BundleKeyOptions
{
    public string? CurrentKid { get; set; }

    /// <summary>kid to base64 AES-256 key. Rotation is a kid bump, never a mass re-login.</summary>
    public IDictionary<string, string> Keys { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public bool HasKeys => !string.IsNullOrWhiteSpace(CurrentKid) && Keys.Count > 0;
}

public sealed class ConnectorTimeouts
{
    /// <summary>Ticket lifetime. Short on purpose: it is a bearer capability over one connection.</summary>
    public int TicketSeconds { get; set; } = 900;

    public int LeaseSeconds { get; set; } = 120;

    public int HeartbeatSeconds { get; set; } = 30;

    /// <summary>How long an agent's lease long-poll may hang before returning 204.</summary>
    public int AgentPollSeconds { get; set; } = 30;

    /// <summary>
    /// How long <c>POST /login</c> waits for an inline provider to finish
    /// before answering 202. Kept small: the caller has SSE and polling.
    /// </summary>
    public int LoginWaitSeconds { get; set; } = 3;

    /// <summary>How long a fetch waits inline before degrading to a 202 job handle.</summary>
    public int FetchWaitSeconds { get; set; } = 25;

    public int JobTimeoutSeconds { get; set; } = 300;

    public int PolitenessMs { get; set; } = 800;

    /// <summary>Grace after a challenge expires before its image bytes are purged.</summary>
    public int ChallengeGraceSeconds { get; set; } = 300;

    /// <summary>
    /// How long a consumer's live-frame poll may hang before answering 204.
    ///
    /// Seconds rather than the agent's thirty: this leg is on somebody's phone,
    /// and a request that hangs for half a minute is one a mobile network, a
    /// screen lock or a backgrounded tab will kill for us at a moment we did
    /// not choose. Short enough to reconnect cheaply, long enough that the
    /// common case is a frame rather than a 204.
    /// </summary>
    public int LiveFramePollSeconds { get; set; } = 5;

    /// <summary>Staged rows die after this regardless of whether the caller ever acked.</summary>
    public int ResultRetentionDays { get; set; } = 7;

    public int EnrollmentCodeSeconds { get; set; } = 900;

    public int ExpirySweepSeconds { get; set; } = 15;

    /// <summary>
    /// Web-issued bundles are capped at this regardless of the manifest TTL.
    /// Null - the default - means the manifest's own TTL stands.
    ///
    /// This caps the SEALED WRAPPER, not the credential inside it. A provider
    /// with a refresh token still holds one good for weeks; all this decides
    /// is how long a blob that escaped a browser tab stays usable.
    ///
    /// It was one hour, then twelve, and the reasoning that moved it the first
    /// time was never finished. An hour was "friction with no security bought:
    /// the credential was never the thing expiring", and twelve hours is the
    /// same sentence with a bigger number. Overnight is longer than twelve
    /// hours, so the ordinary case - close the laptop, come back next morning -
    /// still demanded a full sign-in whose only effect was to re-seal a refresh
    /// token that had never stopped working.
    ///
    /// And there is nothing this cap can do that the browser is not already
    /// doing. A web bundle lives in sessionStorage: it dies with the tab, and a
    /// new tab has to sign in again whatever this number says. A second
    /// deadline INSIDE that boundary only ever fires while somebody still has
    /// the tab open - which is to say, only against the person using it
    /// correctly.
    ///
    /// Nor can renewing it help, and that is worth writing down because it
    /// looks like the obvious middle path. The control plane holds credential
    /// material on <c>JobRow</c> between enqueue and the terminal state and
    /// nowhere else, so it cannot refresh anything in the background - the
    /// bundle in the tab is the only copy. Letting a client swap an expired
    /// bundle for a fresh one would work, and would also let anyone holding a
    /// leaked bundle renew it for ever: strictly weaker than an honestly long
    /// TTL, while looking stricter.
    ///
    /// So: the tab is the boundary, and this exists for a deployment that
    /// wants a second one anyway.
    /// </summary>
    public int? WebBundleMaxTtlSeconds { get; set; }
}
