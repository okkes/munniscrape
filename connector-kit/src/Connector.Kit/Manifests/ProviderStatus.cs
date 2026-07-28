using System.Text.Json.Serialization;

namespace Connector.Kit.Manifests;

/// <summary>
/// The kill switch, and the thing that lets a consumer degrade honestly -
/// "Jumbo connections are paused, we're fixing it" instead of a mystery
/// spinner. Provider definitions are code; only their health is state.
/// </summary>
public sealed record ProviderStatus
{
    public required string ProviderId { get; init; }

    public required ProviderState State { get; init; }

    public required DateTimeOffset Since { get; init; }

    /// <summary>Consumer-owned copy key. Never prose.</summary>
    public string? ReasonKey { get; init; }

    public static ProviderStatus Healthy(string providerId, DateTimeOffset at) =>
        new() { ProviderId = providerId, State = ProviderState.Healthy, Since = at };

    /// <summary>Whether new work may be started for this provider.</summary>
    public bool AcceptsWork => State is ProviderState.Healthy or ProviderState.Degraded;
}

[JsonConverter(typeof(JsonStringEnumConverter<ProviderState>))]
public enum ProviderState
{
    Healthy,

    /// <summary>Something is wrong and an operator knows; work still flows.</summary>
    Degraded,

    /// <summary>Work is refused. Set by an operator, or automatically after a block.</summary>
    Paused,

    /// <summary>Gone for good. Existing sessions are invalid.</summary>
    Retired,
}
