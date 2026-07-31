using System.Reflection;
using Connector.Kit.Errors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
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
    /// <summary>
    /// A 200 carrying <typeparamref name="T"/>, and saying so in the reference.
    ///
    /// The return type is the whole point. <c>Results.Json</c> hands back a
    /// bare <see cref="IResult"/>, and ASP.NET only asks a handler's DECLARED
    /// return type what it produces - so every route in this service documented
    /// a 200 with no body at all, while its request schema was inferred
    /// perfectly. A reference that names what to send and stays silent about
    /// what comes back is the half that cannot be guessed from a curl.
    /// </summary>
    public static ConnectorJsonResult<T> Json<T>(T value) => new(value, StatusCodes.Status200OK);

    /// <summary>
    /// The same body under a status the handler picks at run time.
    ///
    /// Deliberately <see cref="IResult"/> and therefore deliberately
    /// undocumented: <see cref="IEndpointMetadataProvider.PopulateMetadata"/>
    /// is static, so a type cannot describe a status chosen per call - it could
    /// only ever claim 200, which for the 202 callers here would be a document
    /// that is confidently wrong. Every caller of this overload has more than
    /// one outcome, and those name their responses at the <c>MapX</c> call
    /// where all of them are visible at once.
    /// </summary>
    public static IResult Json<T>(T value, int status) => new ConnectorJsonResult<T>(value, status);

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

/// <summary>
/// The funnel's own result: the same bytes <c>Results.Json</c> would have
/// written, and a type the reference can read.
///
/// It exists because response documentation in minimal APIs is inferred from
/// the handler's declared return type, and every helper that returns
/// <see cref="IResult"/> erases it. <c>TypedResults.Json</c> is not the way
/// out - its <c>JsonHttpResult&lt;T&gt;</c> contributes no response-type
/// metadata at all - so the funnel that already owns "how a body is written"
/// takes on "what the body is" as well.
///
/// Declaring it once here rather than as <c>.Produces&lt;T&gt;()</c> on every
/// route is the same call the rest of this file makes: a wire property should
/// be a consequence of the code path, not a line somebody has to remember on
/// route thirty-seven.
/// </summary>
public sealed class ConnectorJsonResult<T> : IResult, IEndpointMetadataProvider, IStatusCodeHttpResult, IValueHttpResult<T>
{
    private readonly T _value;

    internal ConnectorJsonResult(T value, int statusCode)
    {
        _value = value;
        StatusCode = statusCode;
    }

    public T? Value => _value;

    public int? StatusCode { get; }

    public Task ExecuteAsync(HttpContext httpContext) =>
        Results.Json(_value, ConnectorJson.Options, contentType: null, statusCode: StatusCode)
            .ExecuteAsync(httpContext);

    /// <summary>
    /// Static, so it can only ever claim the default status - which is exactly
    /// why <see cref="ConnectorResults.Json{T}(T, int)"/> returns a bare
    /// <see cref="IResult"/> instead of coming through here.
    /// </summary>
    static void IEndpointMetadataProvider.PopulateMetadata(MethodInfo method, EndpointBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Metadata.Add(new ProducesResponseTypeMetadata(
            StatusCodes.Status200OK, typeof(T), ["application/json"]));
    }
}
