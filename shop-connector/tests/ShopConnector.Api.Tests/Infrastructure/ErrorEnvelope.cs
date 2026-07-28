using System.Net;
using System.Text.Json;
using Connector.Kit.Errors;

namespace ShopConnector.Api.Tests.Infrastructure;

/// <summary>
/// The error contract, asserted structurally rather than by example.
///
/// Two rules from the spec are checked on every failure the suite provokes: the
/// body is the envelope and nothing but the envelope, and no field carries
/// user-facing prose. The prose check is a whitespace check, which sounds crude
/// and is exactly right - every legal value in the envelope is a code, a key or
/// an id, and not one of them contains a space.
/// </summary>
internal static class ErrorEnvelope
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "code", "retriable", "user_action", "message_key", "detail_id", "retry_after_seconds",
    };

    private static readonly HashSet<string> Required = new(StringComparer.Ordinal)
    {
        "code", "retriable", "user_action", "message_key",
    };

    private static readonly HashSet<string> KnownCodes =
        [.. Enum.GetValues<ErrorCode>().Select(ErrorCatalog.Wire)];

    /// <summary>
    /// Asserts the whole contract and hands back the error object, so a caller
    /// that wants to say more about it does not have to read the body twice.
    /// </summary>
    public static async Task<JsonElement> AssertAsync(HttpResponseMessage response, HttpStatusCode expectedStatus, string? expectedCode = null)
    {
        ArgumentNullException.ThrowIfNull(response);

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/json", response.ContentType()?.MediaType);

        var body = await response.JsonAsync();

        Assert.Equal(JsonValueKind.Object, body.ValueKind);
        Assert.Equal("error", Assert.Single(body.EnumerateObject()).Name);

        var error = body.GetProperty("error");
        var present = error.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        Assert.True(present.IsSubsetOf(Allowed), $"unexpected error fields: {string.Join(", ", present.Except(Allowed))}");
        Assert.True(Required.IsSubsetOf(present), $"missing error fields: {string.Join(", ", Required.Except(present))}");

        var code = error.Text("code");
        Assert.Contains(code, KnownCodes);
        Assert.Equal($"connect.error.{code}", error.Text("message_key"));

        var retriable = error.GetProperty("retriable").ValueKind;
        Assert.True(retriable is JsonValueKind.True or JsonValueKind.False, "retriable must be a boolean");

        foreach (var property in error.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String) continue;

            var value = property.Value.GetString()!;
            Assert.False(
                value.Any(char.IsWhiteSpace),
                $"error.{property.Name} = '{value}' reads as prose; the connector emits keys, never English");
        }

        if (expectedCode is not null) Assert.Equal(expectedCode, code);
        return error;
    }
}
