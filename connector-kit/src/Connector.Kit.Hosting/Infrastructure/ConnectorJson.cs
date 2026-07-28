using System.Text.Json;
using System.Text.Json.Serialization;

namespace Connector.Kit.Hosting.Infrastructure;

/// <summary>
/// The wire encoding, in one place.
///
/// Snake case everywhere, including enum members: the spec's vocabulary is
/// <c>browser_once</c>, <c>awaiting_input</c>, <c>mfa_code</c>, and a
/// consumer that has to know which fields were named in C# is a consumer
/// coding against our internals. The enum converter is registered in
/// <see cref="JsonSerializerOptions.Converters"/> rather than left to the
/// kit's type-level attributes precisely because a converter in the
/// collection wins - that is how the kit's naming-free defaults are
/// overridden without touching the kit.
/// </summary>
public static class ConnectorJson
{
    public static JsonSerializerOptions Options { get; } = Build();

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>
    /// Reads a JSON column that may be absent or corrupt without taking the
    /// whole request down: a malformed staged row is an operator problem, not
    /// a caller's.
    /// </summary>
    public static T DeserializeOr<T>(string? json, T fallback)
    {
        if (string.IsNullOrWhiteSpace(json)) return fallback;

        try
        {
            return JsonSerializer.Deserialize<T>(json, Options) ?? fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    private static JsonSerializerOptions Build()
    {
        // The canonical encoding lives in the frozen contract, because the
        // agent has to speak exactly the same one. Building a second set here
        // is how the two ends drifted apart and silently broke every
        // agent-served fetch - see ConnectorWireJson.
        var options = ConnectorWireJson.Clone();
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
