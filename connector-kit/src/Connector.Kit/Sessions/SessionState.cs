using System.Text.Json.Serialization;

namespace Connector.Kit.Sessions;

/// <summary>
/// Deliberately small, and every terminal state has a user-facing meaning.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SessionState>))]
public enum SessionState
{
    Queued,
    Running,
    AwaitingInput,

    /// <summary>Usable. A bundle exists.</summary>
    Active,

    /// <summary>The provider stopped accepting the session; a human must sign in.</summary>
    NeedsReauth,

    /// <summary>The provider is refusing us for this user.</summary>
    Blocked,

    /// <summary>Turned off by the user or an operator.</summary>
    Disabled,

    Failed,
    Expired,
}

public static class SessionStateMachine
{
    private static readonly Dictionary<SessionState, SessionState[]> Allowed = new()
    {
        [SessionState.Queued] = [SessionState.Running, SessionState.Failed, SessionState.Expired],
        [SessionState.Running] =
        [
            SessionState.AwaitingInput, SessionState.Active, SessionState.Failed,
            SessionState.Blocked, SessionState.Expired,
        ],
        [SessionState.AwaitingInput] = [SessionState.Running, SessionState.Failed, SessionState.Expired],
        [SessionState.Active] =
        [
            SessionState.NeedsReauth, SessionState.Blocked, SessionState.Disabled,
            SessionState.Running, SessionState.Expired,
        ],
        [SessionState.NeedsReauth] = [SessionState.Running, SessionState.Disabled, SessionState.Expired],
        [SessionState.Blocked] = [SessionState.Disabled, SessionState.Running],
        // terminal
        [SessionState.Disabled] = [],
        [SessionState.Failed] = [],
        [SessionState.Expired] = [],
    };

    public static bool CanTransition(SessionState from, SessionState to) =>
        Allowed.TryGetValue(from, out var next) && next.Contains(to);

    public static bool IsTerminal(SessionState state) =>
        state is SessionState.Disabled or SessionState.Failed or SessionState.Expired;

    /// <summary>A session that can serve a fetch without a human first.</summary>
    public static bool IsUsable(SessionState state) => state is SessionState.Active;

    public static void EnsureTransition(SessionState from, SessionState to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidOperationException($"illegal session transition {from} -> {to}");
        }
    }
}
