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

    /// <summary>Staged rows die after this regardless of whether the caller ever acked.</summary>
    public int ResultRetentionDays { get; set; } = 7;

    public int EnrollmentCodeSeconds { get; set; } = 900;

    public int ExpirySweepSeconds { get; set; } = 15;

    /// <summary>
    /// Web-issued bundles are capped at this regardless of the manifest TTL.
    ///
    /// This caps the SEALED WRAPPER, not the credential inside it. A provider
    /// with a refresh token still holds one good for weeks; all this decides
    /// is how long a blob that escaped a browser tab stays usable.
    ///
    /// It was one hour, and that was too aggressive to be honest. A connection
    /// would sit in the UI marked "Connected", stop working an hour later, and
    /// demand a full sign-in whose only effect was to re-seal the very same
    /// refresh token. That is friction with no security bought: the credential
    /// was never the thing expiring.
    ///
    /// Twelve hours instead, which is about the length of a browser tab's life
    /// and still ~180x shorter than the native custody a manifest asks for.
    /// The real web boundary remains sessionStorage - the bundle dies with the
    /// tab - and this is the belt-and-braces bound on one that escapes it.
    /// </summary>
    public int WebBundleMaxTtlSeconds { get; set; } = 43_200;
}
