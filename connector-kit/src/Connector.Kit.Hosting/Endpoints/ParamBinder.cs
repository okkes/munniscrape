using System.Globalization;
using Connector.Kit.Errors;
using Connector.Kit.Jobs;
using Connector.Kit.Manifests;

namespace Connector.Kit.Hosting.Endpoints;

/// <summary>
/// Turns a caller's query string into a validated <see cref="ResourceRequest"/>.
///
/// Nothing an adapter sees has ever been unvalidated: the manifest declares
/// every legal parameter and this is the only door. An unknown key is
/// rejected rather than ignored, because a caller that misspells
/// <c>since</c> and silently gets a full history has not been told anything
/// went wrong.
/// </summary>
public static class ParamBinder
{
    public const string Since = "since";
    public const string Until = "until";

    public static ResourceRequest Bind(
        ProviderManifest manifest,
        ResourceSpec resource,
        IReadOnlyDictionary<string, IReadOnlyList<string>> query,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(query);

        foreach (var key in query.Keys)
        {
            var spec = resource.Param(key)
                       ?? throw ConnectorException.InvalidRequest($"unknown parameter '{key}' for resource '{resource.Id}'");

            // Internal params are documented for operators and rejected from a
            // query string. A caller that could set one could reach past the
            // manifest, which is exactly what the manifest exists to prevent.
            if (spec.Internal)
            {
                throw ConnectorException.InvalidRequest($"parameter '{key}' is not settable by a caller");
            }
        }

        foreach (var spec in resource.Params.Where(p => p is { Required: true, Internal: false }))
        {
            if (!query.ContainsKey(spec.Key) || query[spec.Key].Count == 0)
            {
                throw ConnectorException.InvalidRequest($"parameter '{spec.Key}' is required");
            }
        }

        DateOnly? since = null;
        DateOnly? until = null;
        var selections = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var (key, rawValues) in query)
        {
            var spec = resource.Param(key)!;
            var values = Expand(spec, rawValues);

            switch (key)
            {
                case Since:
                    since = ParseDate(key, values[0]);
                    break;
                case Until:
                    until = ParseDate(key, values[0]);
                    break;
                default:
                    foreach (var value in values) Validate(spec, value);
                    selections[key] = values;
                    break;
            }
        }

        if (since is { } from && until is { } to && to < from)
        {
            throw ConnectorException.InvalidRequest("until is before since");
        }

        var limits = manifest.Limits;
        since = Widen(since, limits, resource, now);

        return new ResourceRequest
        {
            ResourceId = resource.Id,
            Since = since,
            Until = until,
            Selections = selections,
        };
    }

    /// <summary>
    /// Widens the window backwards by the provider's settlement lag, then
    /// clamps it to the history the provider actually offers.
    ///
    /// The widening is not optional and not the caller's job. Fetching
    /// strictly since the last sync loses rows that settle late -
    /// permanently and invisibly, because nothing ever asks for that window
    /// again. Overlap is free: <c>(session, external_id)</c> uniqueness and
    /// the content hash mean a repeat never duplicates.
    /// </summary>
    private static DateOnly? Widen(DateOnly? since, ProviderLimits limits, ResourceSpec resource, DateTimeOffset now)
    {
        var maxHistoryDays = resource.MaxHistoryDays ?? limits.MaxHistoryDays;
        var earliest = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-maxHistoryDays);

        if (since is null) return null;

        var widened = since.Value.AddDays(-limits.SettlementLagDays);
        return widened < earliest ? earliest : widened;
    }

    /// <summary>
    /// A multi-valued param may arrive as repeated keys or as one
    /// comma-separated value; both are the user's requested shape
    /// (<c>accounts=savings,credit_card</c>) and both mean the same thing.
    /// </summary>
    private static IReadOnlyList<string> Expand(ParamSpec spec, IReadOnlyList<string> raw)
    {
        var values = new List<string>();
        foreach (var value in raw)
        {
            if (spec.Multi)
            {
                values.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
            else
            {
                values.Add(value.Trim());
            }
        }

        if (values.Count == 0)
        {
            throw ConnectorException.InvalidRequest($"parameter '{spec.Key}' has no value");
        }

        if (!spec.Multi && values.Count > 1)
        {
            throw ConnectorException.InvalidRequest($"parameter '{spec.Key}' accepts one value");
        }

        return values;
    }

    private static void Validate(ParamSpec spec, string value)
    {
        switch (spec.Type)
        {
            case ParamType.Enum:
                if (spec.Values is null || !spec.Values.Contains(value, StringComparer.Ordinal))
                {
                    throw ConnectorException.InvalidRequest($"parameter '{spec.Key}' does not accept '{value}'");
                }

                break;

            case ParamType.Number:
                if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                {
                    throw ConnectorException.InvalidRequest($"parameter '{spec.Key}' must be a number");
                }

                break;

            case ParamType.Bool:
                if (!bool.TryParse(value, out _))
                {
                    throw ConnectorException.InvalidRequest($"parameter '{spec.Key}' must be true or false");
                }

                break;

            case ParamType.Date:
                _ = ParseDate(spec.Key, value);
                break;

            case ParamType.Text:
                if (value.Length > 256)
                {
                    throw ConnectorException.InvalidRequest($"parameter '{spec.Key}' is too long");
                }

                break;

            default:
                throw ConnectorException.InvalidRequest($"parameter '{spec.Key}' has an unsupported type");
        }
    }

    private static DateOnly ParseDate(string key, string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : throw ConnectorException.InvalidRequest($"parameter '{key}' must be a date as YYYY-MM-DD");
}
