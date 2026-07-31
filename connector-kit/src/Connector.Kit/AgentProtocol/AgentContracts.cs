using System.Text.Json.Serialization;
using Connector.Kit.Challenges;
using Connector.Kit.Jobs;
using Connector.Kit.Manifests;
using Connector.Kit.Normalization;
using Connector.Kit.Security;

namespace Connector.Kit.AgentProtocol;

/// <summary>
/// The wire contract between the control plane and an agent.
///
/// Every call is outbound FROM the agent: the control plane never initiates
/// a connection to one. That single property is what lets an agent sit
/// behind NAT on a residential line, in a home lab, or on a user's own
/// machine, and it is why bring-your-own hardware needs no redesign.
/// </summary>
public sealed record AgentCapabilities
{
    /// <summary>Provider ids this agent can serve. Empty means any.</summary>
    public IReadOnlyList<string> Providers { get; init; } = [];

    public IReadOnlyList<ProviderRuntime> Runtimes { get; init; } = [];

    public EgressRequirement? Egress { get; init; }

    [JsonPropertyName("max_concurrency")]
    public int MaxConcurrency { get; init; } = 1;

    [JsonPropertyName("class")]
    public AgentClass Class { get; init; } = AgentClass.Pooled;

    /// <summary>
    /// Whether this agent's browser is one a human could actually reach.
    ///
    /// False by default, exactly as headless is the default everywhere: an
    /// agent in a rack has nobody in front of it, and one that has a person
    /// there has to say so. What it buys is that a login declaring
    /// <see cref="ProviderManifest.LoginNeedsHeadedAgent"/> is never offered
    /// to a machine that cannot finish it - previously such an agent leased it,
    /// drove the sign-in for two minutes, met the widget and failed
    /// <c>blocked_by_provider</c>, having learnt nothing the catalogue could
    /// not have said before it started.
    /// </summary>
    public bool Headed { get; init; }

    /// <summary>
    /// Deliberately NOT a function of <see cref="Headed"/>. This answers "can
    /// this agent serve this provider at all", and the headed requirement is
    /// about one job kind: a headless agent is perfectly able to run Amazon's
    /// FETCHES, and excluding it here would idle it for a constraint that
    /// applies only to the login.
    /// </summary>
    public bool CanServe(ProviderManifest manifest) =>
        (Providers.Count == 0 || Providers.Contains(manifest.Id, StringComparer.Ordinal))
        && (Runtimes.Count == 0 || Runtimes.Contains(manifest.Runtime));
}

public sealed record EnrollRequest
{
    /// <summary>One-time, HMAC-signed, and carries the subject - so a BYO agent can only serve whoever enrolled it.</summary>
    public required string Code { get; init; }

    public required string Name { get; init; }

    public required AgentCapabilities Capabilities { get; init; }
}

public sealed record EnrollResponse
{
    [JsonPropertyName("agent_id")]
    public required string AgentId { get; init; }

    /// <summary>Scoped to /agent/v1/* and nothing else. Revocable individually.</summary>
    public required string Token { get; init; }

    [JsonPropertyName("heartbeat_seconds")]
    public int HeartbeatSeconds { get; init; } = 30;
}

public sealed record HeartbeatRequest
{
    public required AgentCapabilities Capabilities { get; init; }

    /// <summary>Persistent browser profiles this agent holds, for T4 routing.</summary>
    public IReadOnlyList<ProfileHealth> Profiles { get; init; } = [];

    public int Running { get; init; }
}

public sealed record ProfileHealth
{
    public required string Id { get; init; }

    public required string Provider { get; init; }

    public required bool Healthy { get; init; }

    [JsonPropertyName("last_ok")]
    public DateTimeOffset? LastOk { get; init; }
}

public sealed record HeartbeatResponse
{
    [JsonPropertyName("lease_ttl_seconds")]
    public int LeaseTtlSeconds { get; init; } = 120;

    /// <summary>An agent that learns it is revoked stops and wipes its profiles.</summary>
    public bool Revoked { get; init; }
}

public sealed record LeaseRequest
{
    public IReadOnlyList<JobKind> Accept { get; init; } =
        [JobKind.Login, JobKind.Fetch, JobKind.Refresh, JobKind.Logout];
}

/// <summary>
/// A leased job, with its credential material unsealed exactly once. The
/// agent holds it in memory for the life of the job and never persists it.
/// </summary>
public sealed record LeasedJob
{
    [JsonPropertyName("job_id")]
    public required string JobId { get; init; }

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    public required string Provider { get; init; }

    public required JobKind Kind { get; init; }

    /// <summary>Login inputs. Present only for a login job.</summary>
    public IReadOnlyDictionary<string, string> Inputs { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> Config { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Unsealed session material. Null on a first login.</summary>
    public SessionMaterial? Material { get; init; }

    /// <summary>What to fetch. Null for a login or logout job.</summary>
    public ResourceRequest? Request { get; init; }

    /// <summary>T4: the persistent profile this job must use.</summary>
    [JsonPropertyName("profile_id")]
    public string? ProfileId { get; init; }

    [JsonPropertyName("lease_expires_at")]
    public required DateTimeOffset LeaseExpiresAt { get; init; }

    public JobLimits Limits { get; init; } = new();
}

public sealed record JobLimits
{
    [JsonPropertyName("timeout_seconds")]
    public int TimeoutSeconds { get; init; } = 300;

    /// <summary>Minimum gap between outbound provider calls.</summary>
    [JsonPropertyName("politeness_ms")]
    public int PolitenessMs { get; init; } = 800;
}

public sealed record ProgressReport
{
    public required JobStep Step { get; init; }

    [JsonPropertyName("steps_done")]
    public IReadOnlyList<JobStep> StepsDone { get; init; } = [];

    /// <summary>
    /// Latches true once a credential has gone upstream. After this a lost
    /// lease fails the job permanently rather than requeuing it - retrying a
    /// login that may already have counted is how accounts get locked.
    /// </summary>
    [JsonPropertyName("credential_submitted")]
    public bool CredentialSubmitted { get; init; }
}

public sealed record RaiseChallengeRequest
{
    public required ChallengeType Type { get; init; }

    /// <summary>
    /// What the answer must contain. Without it every challenge an AGENT
    /// raises reaches the consumer as <see cref="ChallengeAnswerKind.Text"/> -
    /// inline providers would relay taps correctly and browser-tier ones,
    /// which are the only providers that ever have a captcha, would not.
    /// </summary>
    [JsonPropertyName("answer_kind")]
    public ChallengeAnswerKind AnswerKind { get; init; } = ChallengeAnswerKind.Text;

    [JsonPropertyName("prompt_key")]
    public string? PromptKey { get; init; }

    [JsonPropertyName("expires_at")]
    public required DateTimeOffset ExpiresAt { get; init; }

    public string? Code { get; init; }

    public string? Delivery { get; init; }

    public int? Length { get; init; }

    public IReadOnlyList<ChallengeOption>? Options { get; init; }

    public string? Url { get; init; }

    [JsonPropertyName("return_pattern")]
    public string? ReturnPattern { get; init; }

    /// <summary>Base64 PNG, already redacted by the agent before it leaves the machine.</summary>
    [JsonPropertyName("image_base64")]
    public string? ImageBase64 { get; init; }
}

public sealed record RaiseChallengeResponse
{
    [JsonPropertyName("challenge_id")]
    public required string ChallengeId { get; init; }
}

public sealed record JobResultRequest
{
    public IReadOnlyList<Account> Accounts { get; init; } = [];

    public IReadOnlyList<Transaction> Transactions { get; init; } = [];

    public IReadOnlyList<Receipt> Receipts { get; init; } = [];

    /// <summary>Sealed by the control plane into a new bundle for the caller.</summary>
    [JsonPropertyName("session_material")]
    public SessionMaterial? SessionMaterial { get; init; }

    [JsonPropertyName("provider_account")]
    public Adapters.ProviderAccount? ProviderAccount { get; init; }

    public IReadOnlyList<ReachableAccount> Reachable { get; init; } = [];

    [JsonPropertyName("expires_at")]
    public DateTimeOffset? ExpiresAt { get; init; }

    public bool Complete { get; init; } = true;

    public string? Via { get; init; }
}

public sealed record JobFailRequest
{
    /// <summary>Wire form of an <see cref="Errors.ErrorCode"/>.</summary>
    public required string Code { get; init; }

    /// <summary>Operator-facing. Never localised, never shown to an end user.</summary>
    public string? Detail { get; init; }

    /// <summary>
    /// Redacted screenshot and DOM digest. What makes a broken adapter
    /// fixable; never captured while a secret field holds content.
    /// </summary>
    public FailureArtifacts? Artifacts { get; init; }
}

public sealed record FailureArtifacts
{
    [JsonPropertyName("screenshot_base64")]
    public string? ScreenshotBase64 { get; init; }

    [JsonPropertyName("dom_digest")]
    public string? DomDigest { get; init; }
}
