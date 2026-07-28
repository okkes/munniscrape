using System.Text.Json.Serialization;

namespace Connector.Kit.Errors;

/// <summary>
/// The only exception type that crosses an adapter boundary. Anything else
/// escaping an adapter is caught and mapped to <see cref="ErrorCode.Internal"/>,
/// so a caller never sees a stack trace or a bare 500.
/// </summary>
public sealed class ConnectorException : Exception
{
    public ConnectorException(ErrorCode code, string? detail = null, Exception? inner = null)
        : base(detail ?? ErrorCatalog.Wire(code), inner)
    {
        Code = code;
        Detail = detail;
    }

    public ErrorCode Code { get; }

    /// <summary>Operator-facing. Never shown to an end user, never localised.</summary>
    public string? Detail { get; }

    public int HttpStatus => ErrorCatalog.HttpStatus(Code);

    public bool Retriable => ErrorCatalog.IsRetriable(Code);

    public static ConnectorException InvalidCredentials(string? detail = null) =>
        new(ErrorCode.InvalidCredentials, detail);

    public static ConnectorException SessionExpired(string? detail = null) =>
        new(ErrorCode.SessionExpired, detail);

    /// <summary>
    /// The provider's shape moved - a selector vanished, a field changed
    /// type, an export gained a column. Distinct from a transient failure
    /// because it needs a human to fix an adapter, and it degrades the
    /// provider so the next user gets a real message instead of a retry.
    /// </summary>
    public static ConnectorException ProviderChanged(string detail) =>
        new(ErrorCode.ProviderChanged, detail);

    public static ConnectorException Blocked(string? detail = null) =>
        new(ErrorCode.BlockedByProvider, detail);

    public static ConnectorException Unsupported(string detail) =>
        new(ErrorCode.UnsupportedResource, detail);

    public static ConnectorException InvalidRequest(string detail) =>
        new(ErrorCode.InvalidRequest, detail);

    public ConnectorError ToError(string? detailId = null, int? retryAfterSeconds = null) => new()
    {
        Code = ErrorCatalog.Wire(Code),
        Retriable = Retriable,
        UserAction = ErrorCatalog.ActionFor(Code),
        MessageKey = ErrorCatalog.MessageKey(Code),
        DetailId = detailId,
        RetryAfterSeconds = retryAfterSeconds,
    };
}

/// <summary>The wire shape. Note there is no free-text message field.</summary>
public sealed record ConnectorError
{
    public required string Code { get; init; }

    public required bool Retriable { get; init; }

    [JsonPropertyName("user_action")]
    public required UserAction UserAction { get; init; }

    /// <summary>
    /// A key, not prose - the consuming app owns the copy in every language
    /// it ships.
    /// </summary>
    [JsonPropertyName("message_key")]
    public required string MessageKey { get; init; }

    /// <summary>Correlates to an operator-visible log entry. Safe to show.</summary>
    [JsonPropertyName("detail_id")]
    public string? DetailId { get; init; }

    [JsonPropertyName("retry_after_seconds")]
    public int? RetryAfterSeconds { get; init; }
}

/// <summary>The envelope: <c>{ "error": { … } }</c>.</summary>
public sealed record ConnectorErrorEnvelope
{
    public required ConnectorError Error { get; init; }
}
