using Connector.Kit.AgentProtocol;
using Connector.Kit.Errors;
using Connector.Kit.Hosting.Data;
using Connector.Kit.Jobs;
using Connector.Kit.Security;

namespace Connector.Kit.Hosting.Jobs;

/// <summary>
/// The queue that makes outbound-only agents possible.
///
/// Its retry policy is the highest-consequence code in the platform, so it
/// lives here where an adapter author cannot reach it. Two rules are
/// absolute:
///
/// <list type="bullet">
/// <item>A job whose lease expires returns to the queue <b>exactly once</b>,
/// and only while no credential has gone upstream. After
/// <c>CredentialSubmitted</c> a lost lease fails permanently - retrying a
/// login that may already have counted is how bank accounts get locked.</item>
/// <item>No code in <see cref="ErrorCatalog.NeverRetry"/> is ever retried by
/// any path through this type.</item>
/// </list>
/// </summary>
public interface ILeasedJobQueue
{
    Task<JobRow> EnqueueAsync(NewJob job, CancellationToken ct);

    /// <summary>
    /// Hands one job to an agent, or null when nothing matches.
    ///
    /// Respects, in order: provider status <c>AcceptsWork</c>, the agent's
    /// declared capabilities, T4 profile affinity, and per-session
    /// concurrency of one. Claiming is a conditional update guarded on the
    /// state and lease columns, so two agents racing for the same job produce
    /// one winner and one retry rather than one job run twice.
    /// </summary>
    Task<LeasedJob?> TryLeaseAsync(
        string agentId,
        AgentCapabilities capabilities,
        IReadOnlyList<JobKind> accept,
        TimeSpan leaseTtl,
        CancellationToken ct);

    /// <summary>Extends a lease the caller still owns. Null when it no longer does.</summary>
    Task<DateTimeOffset?> RenewLeaseAsync(string jobId, string agentId, TimeSpan leaseTtl, CancellationToken ct);

    Task<JobRow> CompleteAsync(string jobId, string? leaseOwner, CancellationToken ct);

    /// <summary>
    /// Records a failure and decides - here, once - whether it may be tried
    /// again. Returns the job in its new state.
    /// </summary>
    Task<JobRow> FailAsync(string jobId, string? leaseOwner, ErrorCode code, string? detail, CancellationToken ct);

    /// <summary>
    /// Reclaims jobs whose agent went away. Returns how many rows moved, in
    /// either direction.
    /// </summary>
    Task<int> RequeueExpiredLeasesAsync(CancellationToken ct);

    /// <summary>Records typed progress and the credential latch. Never prose.</summary>
    Task ProgressAsync(string jobId, string? leaseOwner, ProgressReport report, CancellationToken ct);
}

public sealed record NewJob
{
    public required string SessionId { get; init; }

    public required string ProviderId { get; init; }

    public required JobKind Kind { get; init; }

    public string? ResourceId { get; init; }

    public ResourceRequest? Request { get; init; }

    /// <summary>Login inputs. Nothing else ever carries a credential into the queue.</summary>
    public IReadOnlyDictionary<string, string>? Inputs { get; init; }

    public IReadOnlyDictionary<string, string> Config { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public SessionMaterial? Material { get; init; }

    /// <summary>T4: routes this job to exactly one agent.</summary>
    public string? ProfileId { get; init; }
}
