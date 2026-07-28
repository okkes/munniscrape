using System.Text.Json.Serialization;

namespace Connector.Kit.Errors;

/// <summary>
/// The closed set of things that can go wrong. Never a bare 500.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ErrorCode>))]
public enum ErrorCode
{
    /// <summary>The provider rejected the credentials. NEVER retried. See <see cref="ErrorCatalog"/>.</summary>
    InvalidCredentials,

    /// <summary>The stored session is no longer accepted; a human must reconnect.</summary>
    SessionExpired,

    /// <summary>A challenge answer was wrong.</summary>
    MfaFailed,

    /// <summary>Nobody answered a challenge in time.</summary>
    MfaTimeout,

    /// <summary>A challenge outlived its window before it was answered.</summary>
    ChallengeExpired,

    /// <summary>The provider is deliberately refusing us. We stop; we do not escalate.</summary>
    BlockedByProvider,

    /// <summary>The provider's shape moved. Pages an operator and degrades the provider.</summary>
    ProviderChanged,

    /// <summary>The provider is down or unreachable.</summary>
    ProviderUnavailable,

    /// <summary>We are being rate limited.</summary>
    RateLimited,

    /// <summary>No agent can serve this job - typically a BYO agent that is switched off.</summary>
    AgentUnavailable,

    /// <summary>The caller asked for something this provider or session cannot give.</summary>
    UnsupportedResource,

    /// <summary>The caller's request was malformed.</summary>
    InvalidRequest,

    /// <summary>Recorded consent is missing or stale.</summary>
    ConsentExpired,

    /// <summary>An emitted record failed its own integrity check. We refuse to hand over data we cannot vouch for.</summary>
    ReconciliationFailed,

    /// <summary>Ours, not theirs.</summary>
    Internal,
}

/// <summary>What the human on the other end should do about it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UserAction>))]
public enum UserAction
{
    /// <summary>Nothing they can do.</summary>
    None,

    /// <summary>Try again; it may work.</summary>
    Retry,

    /// <summary>Sign in again.</summary>
    Reauth,

    /// <summary>Set the connection up from scratch.</summary>
    Reconnect,

    /// <summary>Wait - the problem is on the provider's side.</summary>
    Wait,

    /// <summary>Turn their own agent on.</summary>
    StartYourAgent,
}
