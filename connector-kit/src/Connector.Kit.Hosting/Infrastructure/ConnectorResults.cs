using Connector.Kit.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Connector.Kit.Hosting.Infrastructure;

/// <summary>
/// Carries an HTTP status the error catalogue does not model.
///
/// The catalogue maps a code to exactly one status, which is right for every
/// case but one: answering a challenge twice is a conflict, and the closed
/// code set has no <c>conflict</c> member. Rather than widen the frozen
/// taxonomy this wraps the real error and states the status the wire
/// contract requires.
/// </summary>
public sealed class ConnectorHttpException : Exception
{
    public ConnectorHttpException(int status, ConnectorException error)
        : base(error?.Message, error)
    {
        ArgumentNullException.ThrowIfNull(error);
        Status = status;
        Error = error;
    }

    public int Status { get; }

    public ConnectorException Error { get; }

    public static ConnectorHttpException Conflict(string detail) =>
        new(StatusCodes.Status409Conflict, new ConnectorException(ErrorCode.InvalidRequest, detail));
}

/// <summary>
/// The single funnel every response goes through, so that "never a bare 500"
/// is a property of the code path rather than a rule people remember.
/// </summary>
public static class ConnectorResults
{
    public static IResult Json<T>(T value, int status = StatusCodes.Status200OK) =>
        Results.Json(value, ConnectorJson.Options, contentType: null, statusCode: status);

    public static IResult Error(ConnectorException exception, string? detailId = null, int? retryAfterSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Envelope(exception, exception.HttpStatus, detailId, retryAfterSeconds);
    }

    public static IResult Error(ConnectorHttpException exception, string? detailId = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Envelope(exception.Error, exception.Status, detailId, null);
    }

    /// <summary>
    /// Maps anything that escaped to an envelope. An unexpected exception
    /// becomes <see cref="ErrorCode.Internal"/> with a correlation id and
    /// nothing else - a caller never receives a stack trace or a message we
    /// did not choose.
    /// </summary>
    public static IResult FromException(Exception exception, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(logger);

        switch (exception)
        {
            case ConnectorHttpException http:
                logger.LogInformation("connector error {Code} ({Status}): {Detail}",
                    ErrorCatalog.Wire(http.Error.Code), http.Status, http.Error.Detail);
                return Error(http);

            case ConnectorException connector:
                logger.LogInformation("connector error {Code}: {Detail}",
                    ErrorCatalog.Wire(connector.Code), connector.Detail);
                return Error(connector);

            case OperationCanceledException:
                // The caller went away. Nothing to report and nobody to report to.
                return Envelope(new ConnectorException(ErrorCode.Internal, "request cancelled"),
                    StatusCodes.Status499ClientClosedRequest, null, null);

            default:
            {
                var detailId = Ids.New(Ids.Error);
                logger.LogError(exception, "unhandled control-plane failure {DetailId}", detailId);
                return Envelope(new ConnectorException(ErrorCode.Internal, "unhandled"),
                    StatusCodes.Status500InternalServerError, detailId, null);
            }
        }
    }

    private static IResult Envelope(ConnectorException error, int status, string? detailId, int? retryAfterSeconds) =>
        Results.Json(
            new ConnectorErrorEnvelope { Error = error.ToError(detailId, retryAfterSeconds) },
            ConnectorJson.Options,
            contentType: null,
            statusCode: status);
}
