using System.Text.Json;
using System.Text.Json.Serialization;

namespace DemoClient.Web.Relay;

/// <summary>
/// The wire encoding, in one place. Snake case everywhere, matching the
/// connectors byte for byte - a client that had to know which side of the
/// relay named a field would be coding against the relay's internals.
/// </summary>
public static class RelayJson
{
    public static JsonSerializerOptions Options { get; } = Build();

    private static JsonSerializerOptions Build()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            // Never on dictionary keys: `inputs` and `config` carry the
            // provider's own field names, and renaming them would break a form
            // the manifest declared.
            DictionaryKeyPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

/// <summary>
/// The connector's error shape, reproduced because the relay is not allowed to
/// reference the connector's types - and passed through unchanged whenever it
/// comes from a connector. The relay only ever mints one of these itself when
/// the connector could not answer at all.
///
/// Note there is no free-text message field. <c>message_key</c> is the whole
/// contract: the SPA owns the English, so a connector - or a relay - that
/// emitted prose would have taken a decision that is not its to take.
/// </summary>
public sealed record RelayError
{
    public required string Code { get; init; }

    public required bool Retriable { get; init; }

    public required string UserAction { get; init; }

    public required string MessageKey { get; init; }

    /// <summary>Correlates to a relay log line. Safe to show; carries no detail.</summary>
    public string? DetailId { get; init; }

    public int? RetryAfterSeconds { get; init; }
}

/// <summary>The envelope: <c>{ "error": { … } }</c>.</summary>
public sealed record RelayErrorEnvelope
{
    public required RelayError Error { get; init; }
}

/// <summary>
/// The relay's own errors, using the connector taxonomy so the SPA needs one
/// mapping table rather than two.
/// </summary>
public static class RelayErrors
{
    public static IResult ProviderUnavailable(string service, string detail, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var detailId = NewDetailId();
        logger.LogWarning("connector '{Service}' unreachable ({DetailId}): {Detail}", service, detailId, detail);

        return Envelope(StatusCodes.Status503ServiceUnavailable, new RelayError
        {
            Code = "provider_unavailable",
            Retriable = true,
            UserAction = "retry",
            MessageKey = "connect.error.provider_unavailable",
            DetailId = detailId,
            RetryAfterSeconds = 5,
        });
    }

    /// <summary>An unknown <c>{service}</c>. 404 because the route genuinely does not exist.</summary>
    public static IResult UnknownService(string service, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        logger.LogInformation("no connector configured for service '{Service}'", service);

        return Envelope(StatusCodes.Status404NotFound, new RelayError
        {
            Code = "unsupported_resource",
            Retriable = false,
            UserAction = "none",
            MessageKey = "connect.error.unsupported_resource",
        });
    }

    public static IResult InvalidRequest(string detail, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        logger.LogInformation("rejected a relay call: {Detail}", detail);

        return Envelope(StatusCodes.Status400BadRequest, new RelayError
        {
            Code = "invalid_request",
            Retriable = false,
            UserAction = "none",
            MessageKey = "connect.error.invalid_request",
        });
    }

    public static IResult Internal(string detail, ILogger logger, Exception? exception = null)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var detailId = NewDetailId();
        logger.LogError(exception, "relay failure ({DetailId}): {Detail}", detailId, detail);

        return Envelope(StatusCodes.Status500InternalServerError, new RelayError
        {
            Code = "internal",
            Retriable = true,
            UserAction = "retry",
            MessageKey = "connect.error.internal",
            DetailId = detailId,
        });
    }

    public static IResult Envelope(int status, RelayError error) =>
        Results.Json(new RelayErrorEnvelope { Error = error }, RelayJson.Options, statusCode: status);

    private static string NewDetailId() => "err_" + Guid.NewGuid().ToString("n")[..12];
}

/// <summary>
/// The connector's answer, byte for byte.
///
/// Deserialising and re-serialising would be the moment the relay started
/// having opinions about a payload it does not own - a dropped field, a
/// reordered array, a rounded number. The relay copies status, content type
/// and body, and that is all.
/// </summary>
public sealed class PassThroughResult(ConnectorReply reply) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(reply);

        httpContext.Response.StatusCode = reply.Status;

        if (reply.ContentType is { Length: > 0 } contentType)
        {
            httpContext.Response.ContentType = contentType;
        }

        // Every response that touched a provider says which contract version
        // answered it, all the way to the tab.
        if (reply.ManifestVersion is { Length: > 0 } manifestVersion)
        {
            httpContext.Response.Headers[ConnectorClient.ManifestVersionHeader] = manifestVersion;
        }

        if (reply.Body.Length > 0)
        {
            await httpContext.Response.Body.WriteAsync(reply.Body, httpContext.RequestAborted);
        }
    }
}

/// <summary>
/// The outermost filter on every relay route, so an unreachable connector is a
/// clean envelope rather than an unhandled 500 - and never a hang.
/// </summary>
public sealed class RelayExceptionFilter(ILoggerFactory loggers) : IEndpointFilter
{
    private readonly ILogger _logger = loggers.CreateLogger("Relay");

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        try
        {
            return await next(context);
        }
        catch (Exception ex)
        {
            // A response that has already begun cannot be replaced by an
            // envelope; the SSE proxy and the challenge image are the cases.
            if (context.HttpContext.Response.HasStarted)
            {
                _logger.LogError(ex, "failure after the response had started; aborting");
                context.HttpContext.Abort();
                return null;
            }

            return ex switch
            {
                ConnectorUnreachableException unreachable =>
                    RelayErrors.ProviderUnavailable(unreachable.Service, unreachable.Message, _logger),

                // The tab went away. Nothing to report and nobody to report to.
                OperationCanceledException => Results.StatusCode(StatusCodes.Status499ClientClosedRequest),

                _ => RelayErrors.Internal("unhandled relay failure", _logger, ex),
            };
        }
    }
}
