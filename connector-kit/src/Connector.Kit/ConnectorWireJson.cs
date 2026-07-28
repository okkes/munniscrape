using System.Text.Json;
using System.Text.Json.Serialization;

namespace Connector.Kit;

/// <summary>
/// The one JSON encoding the whole platform speaks.
///
/// It lives in the frozen contract because it IS part of the contract. The
/// control plane and the agent previously built their own options objects
/// that were *described* as identical and were not: the control plane used
/// <see cref="JsonNamingPolicy.SnakeCaseLower"/>, the agent used web
/// defaults. Most of the protocol survived that, because the DTOs in
/// <c>Connector.Kit.AgentProtocol</c> carry explicit
/// <see cref="JsonPropertyNameAttribute"/>s that both sides honour - so the
/// mismatch only bit the one shape that does not,
/// <see cref="Jobs.ResourceRequest"/>.
///
/// The result was a bug that hid almost perfectly: a login job carries no
/// <c>Request</c>, so logins leased and ran; a fetch job carries one, so
/// every agent-served fetch failed to deserialize and the job sat queued
/// until it expired, for every provider. Only inline mock providers, which
/// never cross this wire, appeared to work.
///
/// Two definitions that must stay identical are a defect waiting for the
/// next edit. There is now one.
/// </summary>
public static class ConnectorWireJson
{
    public static JsonSerializerOptions Options { get; } = Build();

    /// <summary>
    /// A mutable copy, for a host that needs to add its own converters. The
    /// naming policy must not be changed - that is the part both ends agree on.
    /// </summary>
    public static JsonSerializerOptions Clone()
    {
        var options = new JsonSerializerOptions(Options);
        return options;
    }

    private static JsonSerializerOptions Build()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,

            // Dictionary keys are DATA - provider config keys, adapter inputs,
            // provider ids. Renaming them would quietly corrupt a payload
            // rather than fail it.
            DictionaryKeyPolicy = null,

            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }
}
