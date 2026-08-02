using System.Text.Json.Serialization;

namespace Connector.Kit.Manifests;

/// <summary>
/// One fetchable resource, e.g. <c>GET /v1/lidl/receipts?since=…</c>.
/// Routes are generated from this: there is exactly one generic handler,
/// never a controller per provider.
/// </summary>
public sealed record ResourceSpec
{
    /// <summary>Path segment: <c>/v1/{provider}/{id}</c>.</summary>
    public required string Id { get; init; }

    public required ResourceShape Returns { get; init; }

    public IReadOnlyList<ParamSpec> Params { get; init; } = [];

    public int? MaxHistoryDays { get; init; }

    /// <summary>Rough expectation, so a consumer can choose to poll or stream.</summary>
    public int TypicalDurationSeconds { get; init; } = 30;

    /// <summary>
    /// Upper bound on records in one pass. Beyond this the fetch paginates
    /// with a cursor rather than running for minutes on a heavy account.
    /// </summary>
    public int MaxRecordsPerFetch { get; init; } = 200;

    public ParamSpec? Param(string key) =>
        Params.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.Ordinal));
}

[JsonConverter(typeof(JsonStringEnumConverter<ResourceShape>))]
public enum ResourceShape
{
    Account,
    Transaction,
    Receipt,
    Profile,

    /// <summary>
    /// One credit as a registry states it: who lent, what kind, how much, and
    /// whether it is still running.
    /// </summary>
    CreditRegistration,
}

public sealed record ParamSpec
{
    public required string Key { get; init; }

    public required ParamType Type { get; init; }

    public bool Required { get; init; }

    /// <summary>Whether the caller may pass a comma-separated list.</summary>
    public bool Multi { get; init; }

    /// <summary>Allowed values for <see cref="ParamType.Enum"/>.</summary>
    public IReadOnlyList<string>? Values { get; init; }

    /// <summary>
    /// True when the adapter uses this but callers may not set it - it is
    /// documented for operators, and rejected from a query string.
    /// </summary>
    public bool Internal { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<ParamType>))]
public enum ParamType
{
    Date,
    Enum,
    Text,
    Number,
    Bool,
}
