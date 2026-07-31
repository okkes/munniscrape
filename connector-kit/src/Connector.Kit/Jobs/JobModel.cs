using System.Text.Json.Serialization;

namespace Connector.Kit.Jobs;

[JsonConverter(typeof(JsonStringEnumConverter<JobKind>))]
public enum JobKind
{
    /// <summary>
    /// The only kind that submits a credential. Never enqueued by a
    /// scheduler, and never retried after a credential has gone upstream.
    /// </summary>
    Login,

    Fetch,

    /// <summary>A silent session renewal. Only where the manifest says reauth is cheap.</summary>
    Refresh,

    Logout,
}

[JsonConverter(typeof(JsonStringEnumConverter<JobState>))]
public enum JobState
{
    Queued,
    Leased,
    Running,
    AwaitingInput,
    Succeeded,
    Failed,
    Expired,
}

/// <summary>
/// The typed progress vocabulary. A closed enum rather than free text: a
/// status string cannot be translated, cannot drive a progress bar, and
/// becomes a de-facto API the moment a consumer matches on it.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<JobStep>))]
public enum JobStep
{
    Queued,
    AgentAssigned,
    OpeningProvider,
    Authenticating,
    AwaitingHuman,
    SelectingAccounts,
    Downloading,
    Parsing,
    Normalizing,
    Finalizing,
    LoggingOut,
}

public static class JobStateMachine
{
    private static readonly Dictionary<JobState, JobState[]> Allowed = new()
    {
        [JobState.Queued] = [JobState.Leased, JobState.Expired, JobState.Failed],
        [JobState.Leased] = [JobState.Running, JobState.Queued, JobState.Failed, JobState.Expired],
        [JobState.Running] =
        [
            JobState.AwaitingInput, JobState.Succeeded, JobState.Failed, JobState.Expired,
            // a lost lease returns the job to the queue - exactly once
            JobState.Queued,
        ],
        [JobState.AwaitingInput] = [JobState.Running, JobState.Failed, JobState.Expired],
        [JobState.Succeeded] = [],
        [JobState.Failed] = [],
        [JobState.Expired] = [],
    };

    public static bool CanTransition(JobState from, JobState to) =>
        Allowed.TryGetValue(from, out var next) && next.Contains(to);

    public static bool IsTerminal(JobState state) =>
        state is JobState.Succeeded or JobState.Failed or JobState.Expired;

    public static void EnsureTransition(JobState from, JobState to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidOperationException($"illegal job transition {from} -> {to}");
        }
    }
}

/// <summary>
/// What the caller asked for, validated against the resource's
/// <c>ParamSpec</c> before it ever reaches an adapter.
/// </summary>
public sealed record ResourceRequest
{
    public required string ResourceId { get; init; }

    public DateOnly? Since { get; init; }

    public DateOnly? Until { get; init; }

    /// <summary>Multi-valued params, e.g. <c>accounts=savings,credit_card</c>.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Selections { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    public IReadOnlyList<string> Include =>
        Selections.TryGetValue("include", out var v) ? v : [];

    public IReadOnlyList<string> Accounts =>
        Selections.TryGetValue("accounts", out var v) ? v : [];

    public bool WantsItems => Include.Contains("items", StringComparer.Ordinal);

    /// <summary>
    /// The provider's own answer, kept beside the normalised record.
    ///
    /// Opt-in and never a default, because raw is strictly the more sensitive
    /// of the two: normalisation drops the fields nobody asked for, and this
    /// puts them back. It exists so a shape change can be diagnosed from real
    /// traffic instead of guessed at - when a provider renames a field, the
    /// normalised record simply loses a value and says nothing about why.
    ///
    /// It rides the same row as the normalised record on purpose, so the ack
    /// that purges one purges the other and there is no second lifetime for
    /// anybody to get wrong.
    /// </summary>
    public bool WantsRaw => Include.Contains("raw", StringComparer.Ordinal);
}
