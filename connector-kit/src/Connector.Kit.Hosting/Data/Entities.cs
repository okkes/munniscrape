using System.Text.Json.Serialization;
using Connector.Kit.Challenges;
using Connector.Kit.Errors;
using Connector.Kit.Jobs;
using Connector.Kit.Manifests;
using Connector.Kit.Sessions;

namespace Connector.Kit.Hosting.Data;

/// <summary>
/// Where the bundle lives, and therefore how long it may live. Custody says
/// <em>who</em> holds the secret; device class says <em>how long</em>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeviceClass>))]
public enum DeviceClass
{
    /// <summary>An encrypted store on the user's device. The bundle survives restarts.</summary>
    Native,

    /// <summary>The tab session only. The bundle's TTL is capped hard.</summary>
    Web,
}

/// <summary>
/// A connection. Under <c>client</c> custody this row holds no secret at
/// all - only who it is for, what it is, and whether it still works.
/// </summary>
public sealed class SessionRow
{
    public string Id { get; set; } = string.Empty;

    public string ProviderId { get; set; } = string.Empty;

    /// <summary>Pseudonymous and consumer-minted. The connector cannot map it to a person.</summary>
    public string Subject { get; set; } = string.Empty;

    public SessionState State { get; set; } = SessionState.Queued;

    /// <summary>Sealed into every bundle as AAD, so a stale bundle is rejected rather than misread.</summary>
    public int ManifestVersion { get; set; }

    /// <summary>T4: the agent that owns this connection's profile.</summary>
    public string? AgentId { get; set; }

    /// <summary>T4: the persistent browser profile. Jobs for this session route only there.</summary>
    public string? ProfileId { get; set; }

    /// <summary>Non-secret provider settings (country, language). Loggable.</summary>
    public string ConfigJson { get; set; } = "{}";

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Echoed back so a user can tell two connections apart. Never sent upstream.</summary>
    public string? Label { get; set; }

    public DateTimeOffset? ConsentAcceptedAt { get; set; }

    public string? ConsentTermsVersion { get; set; }

    public DeviceClass DeviceClass { get; set; } = DeviceClass.Native;

    /// <summary>
    /// What the provider called this connection, as the adapter reported it.
    /// Not a secret and not data - it exists so a user with two connections to
    /// the same store can tell which is which, which is the only thing that
    /// makes a second connection manageable.
    /// </summary>
    public string? ProviderAccountJson { get; set; }

    /// <summary>
    /// A sealed bundle waiting to be collected.
    ///
    /// A login finishes on an agent, minutes after the request that started it
    /// returned, so the result has to survive until the caller comes back for
    /// it. This is ciphertext the control plane cannot read without its own
    /// key, and it is cleared the moment it is handed over - the connector
    /// forgets what it has delivered.
    /// </summary>
    public string? PendingBundle { get; set; }
}

/// <summary>
/// One unit of work for an agent. The lease columns are what make remote,
/// outbound-only agents possible at all.
/// </summary>
public sealed class JobRow
{
    public string Id { get; set; } = string.Empty;

    public string SessionId { get; set; } = string.Empty;

    /// <summary>Denormalised from the session: leasing filters on it and must stay one table.</summary>
    public string ProviderId { get; set; } = string.Empty;

    public JobKind Kind { get; set; }

    public JobState State { get; set; } = JobState.Queued;

    /// <summary>The validated <see cref="ResourceRequest"/>. Never raw caller input.</summary>
    public string ParamsJson { get; set; } = "{}";

    public string? ResourceId { get; set; }

    public string? LeaseOwner { get; set; }

    public DateTimeOffset? LeaseExpiresAt { get; set; }

    /// <summary>Incremented on every lease. Two is the ceiling; see the queue.</summary>
    public int Attempts { get; set; }

    /// <summary>
    /// Latches true once a credential has gone upstream. After this a lost
    /// lease fails the job permanently instead of requeuing it - retrying a
    /// login that may already have counted is how bank accounts get locked.
    /// </summary>
    public bool CredentialSubmitted { get; set; }

    public JobStep Step { get; set; } = JobStep.Queued;

    public string StepsDoneJson { get; set; } = "[]";

    /// <summary>
    /// False when the adapter stopped short of the end of the window. A first
    /// connect on a heavy account paginates rather than running for ten
    /// minutes, and the caller has to be told which of the two it got.
    /// </summary>
    public bool Complete { get; set; } = true;

    public ErrorCode? ErrorCode { get; set; }

    /// <summary>Operator-facing. Never localised, never shown to an end user.</summary>
    public string? ErrorDetail { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Login inputs - a password, in plain text, by choice: no converter, no
    /// encryption, no key.
    ///
    /// Deliberately not cleared at lease time: a lease lost before the
    /// credential went upstream requeues once, and the retry needs them. What
    /// bounds it instead is four paths, and for a long time it was three - a
    /// job reaching a terminal state, a lost lease being burnt, and a
    /// disconnect purging the session. All three key on a job that got as far
    /// as being LEASED, so a login nobody ever took was reached by none of
    /// them and kept its password for as long as the row existed, which is
    /// forever. <c>ExpireAbandonedAsync</c> is the fourth.
    /// </summary>
    public string? InputsJson { get; set; }

    /// <summary>
    /// The unsealed session material, under the same four rules as
    /// <see cref="InputsJson"/>. For a cookie-jar provider this is the whole
    /// jar.
    ///
    /// This row is the only place the control plane holds credential material
    /// UNSEALED. The sealed kind rests elsewhere - a session's pending bundle
    /// is one - but that is a blob bound to a subject and a manifest version,
    /// which is a different thing from a password somebody could read.
    /// </summary>
    public string? MaterialJson { get; set; }

    /// <summary>Non-secret provider settings, copied from the session at enqueue.</summary>
    public string ConfigJson { get; set; } = "{}";

    /// <summary>T4 affinity, denormalised from the session for the same reason as the provider.</summary>
    public string? ProfileId { get; set; }
}

public sealed class ChallengeRow
{
    public string Id { get; set; } = string.Empty;

    public string JobId { get; set; } = string.Empty;

    public ChallengeType Type { get; set; }

    /// <summary>The non-image part of the challenge, as the consumer will see it.</summary>
    public string PayloadJson { get; set; } = "{}";

    /// <summary>Redacted PNG. Purged on answer or expiry; never logged, never in a webhook.</summary>
    public byte[]? ImageBytes { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? AnsweredAt { get; set; }

    public string? AnswerValue { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Staged output, not owned data. Purged on ack, or after a hard TTL if the
/// caller never acks. The connector stays a pipe.
/// </summary>
public sealed class ResultRow
{
    public string Id { get; set; } = string.Empty;

    public string JobId { get; set; } = string.Empty;

    public string SessionId { get; set; } = string.Empty;

    public string Resource { get; set; } = string.Empty;

    /// <summary>The provider's own id. With the session it is the dedupe key.</summary>
    public string ExternalId { get; set; } = string.Empty;

    public string PayloadJson { get; set; } = "{}";

    /// <summary>
    /// The provider's own answer for this record, when <c>include=raw</c> asked
    /// for it. Null otherwise, which is almost always.
    ///
    /// On this row rather than a table of its own, deliberately: the ack and
    /// the retention sweep both work on rows, so raw is purged by the machinery
    /// that already exists instead of by a second lifetime somebody has to
    /// remember. It is the more sensitive of the two - normalisation drops what
    /// nobody asked for and this puts it back - so it must never outlive the
    /// record it belongs to.
    /// </summary>
    public string? RawJson { get; set; }

    /// <summary>Changes when any meaningful value does, so a re-fetch overlaps for free.</summary>
    public string ContentHash { get; set; } = string.Empty;

    public string Cursor { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class AgentRow
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public AgentClass Class { get; set; } = AgentClass.Pooled;

    /// <summary>
    /// The subject that enrolled this agent, taken from the one-time code and
    /// never from the agent's own request. An agent may only ever serve this
    /// subject - see <c>ConnectorOptions.FleetSubjects</c> for the operator's
    /// own fleet, which is the one exception and is named in configuration.
    ///
    /// Set for every agent, not only BYO ones: the pooled fleet enrolls
    /// through the same endpoint with an operator-chosen subject.
    /// </summary>
    public string? OwnerSubject { get; set; }

    public string CapabilitiesJson { get; set; } = "{}";

    /// <summary>SHA-256 of the bearer token. The token itself is shown exactly once.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTimeOffset LastHeartbeatAt { get; set; }

    public bool Revoked { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>T4 persistent-profile registry. One row per connection that lives on a BYO agent.</summary>
public sealed class ProfileRow
{
    public string Id { get; set; } = string.Empty;

    public string AgentId { get; set; } = string.Empty;

    public string ProviderId { get; set; } = string.Empty;

    public string? SessionId { get; set; }

    public bool Healthy { get; set; }

    public DateTimeOffset? LastOkAt { get; set; }
}

/// <summary>Providers are code; only their health is state. This table is the kill switch.</summary>
public sealed class ProviderStatusRow
{
    public string ProviderId { get; set; } = string.Empty;

    public ProviderState State { get; set; } = ProviderState.Healthy;

    public DateTimeOffset Since { get; set; }

    /// <summary>Consumer-owned copy key. Never prose.</summary>
    public string? ReasonKey { get; set; }
}

/// <summary>
/// A one-time agent enrollment code. Only its hash is stored, so the table is
/// useless to anyone who reads it, and the subject rides along so a BYO agent
/// can only ever serve whoever enrolled it.
/// </summary>
public sealed class EnrollmentRow
{
    public string CodeHash { get; set; } = string.Empty;

    public string Subject { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RedeemedAt { get; set; }
}
