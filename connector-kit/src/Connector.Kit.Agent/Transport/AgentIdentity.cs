using System.Text.Json.Serialization;

namespace Connector.Kit.Agent.Transport;

/// <summary>
/// Who this agent is, once it has enrolled. Held in memory and read by the
/// auth handler on every outbound call.
/// </summary>
public sealed class AgentIdentity
{
    private AgentEnrollment? _current;

    public AgentEnrollment? Current => Volatile.Read(ref _current);

    public bool IsEnrolled => Current is not null;

    /// <summary>The agent id, for the T4 material pointer. Throws before enrollment.</summary>
    public string AgentId =>
        Current?.AgentId ?? throw new InvalidOperationException("the agent has not enrolled yet");

    public void Set(AgentEnrollment enrollment)
    {
        ArgumentNullException.ThrowIfNull(enrollment);
        Volatile.Write(ref _current, enrollment);
    }

    public void Clear() => Volatile.Write(ref _current, null);
}

/// <summary>
/// What survives a restart. The token is the only secret this agent holds at
/// rest, and it is scoped to <c>/agent/v1/*</c> and revocable on its own.
/// </summary>
public sealed record AgentEnrollment
{
    [JsonPropertyName("agent_id")]
    public required string AgentId { get; init; }

    public required string Token { get; init; }

    /// <summary>
    /// The control plane this token was issued by. Pointing an agent at a
    /// different host must force a fresh enrollment rather than send one
    /// service's token to another.
    /// </summary>
    [JsonPropertyName("control_plane")]
    public required string ControlPlane { get; init; }

    [JsonPropertyName("enrolled_at")]
    public required DateTimeOffset EnrolledAt { get; init; }

    [JsonPropertyName("heartbeat_seconds")]
    public int HeartbeatSeconds { get; init; } = 30;
}
