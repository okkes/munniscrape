using System.Text;
using Connector.Kit.Errors;
using Connector.Kit.Hosting.Data;
using Connector.Kit.Hosting.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Connector.Kit.Hosting.Endpoints;

/// <summary>
/// The outermost filter on every route. Nothing gets past it as an
/// unmodelled failure, which is what makes "never a bare 500" structural
/// rather than aspirational.
/// </summary>
public sealed class ConnectorExceptionFilter(ILoggerFactory loggers) : IEndpointFilter
{
    private readonly ILogger _logger = loggers.CreateLogger("Connector.Endpoints");

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
            // envelope; SSE and image streams are the cases. Aborting is the
            // only honest signal left.
            if (context.HttpContext.Response.HasStarted)
            {
                _logger.LogError(ex, "failure after the response had started; aborting");
                context.HttpContext.Abort();
                return null;
            }

            return ConnectorResults.FromException(ex, _logger);
        }
    }
}

public static class RequestContext
{
    public const string DeviceClassHeader = "X-Device-Class";
    public const string TicketHeader = "X-Connector-Ticket";
    public const string ManifestVersionHeader = "X-Manifest-Version";
    public const string IdempotencyHeader = "Idempotency-Key";

    /// <summary>
    /// Reads the declared device class. Absent means native - the historical
    /// default and the safe one, since the web rules only ever shorten a
    /// bundle's life. Anything else is rejected rather than guessed.
    /// </summary>
    public static DeviceClass DeviceClassOf(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        var raw = http.Request.Headers[DeviceClassHeader].ToString();
        if (string.IsNullOrWhiteSpace(raw)) return DeviceClass.Native;

        return raw.Trim().ToLowerInvariant() switch
        {
            "native" => DeviceClass.Native,
            "web" => DeviceClass.Web,
            _ => throw ConnectorException.InvalidRequest($"{DeviceClassHeader} must be 'native' or 'web'"),
        };
    }

    public static string RequireTicket(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        var ticket = http.Request.Headers[TicketHeader].ToString();
        return string.IsNullOrWhiteSpace(ticket)
            ? throw ConnectorException.InvalidRequest($"{TicketHeader} is required")
            : ticket;
    }

    /// <summary>Every response that touched a provider says which contract version answered.</summary>
    public static void StampManifestVersion(HttpContext http, int manifestVersion)
    {
        ArgumentNullException.ThrowIfNull(http);
        http.Response.Headers[ManifestVersionHeader] =
            manifestVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string? IdempotencyKey(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var key = http.Request.Headers[IdempotencyHeader].ToString();
        return string.IsNullOrWhiteSpace(key) ? null : key;
    }

    /// <summary>The query string as the binder wants it: every key, every repeat.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Query(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (key, values) in http.Request.Query)
        {
            result[key] = [.. values.Where(v => v is not null).Select(v => v!)];
        }

        return result;
    }
}

/// <summary>
/// Server-sent events, written by hand rather than through a helper.
///
/// The stream re-reads state and emits on change; it holds no subscription
/// that could go stale, so a consumer that reconnects sees the current truth
/// immediately instead of waiting for the next transition.
/// </summary>
public static class EventStream
{
    public static async Task WriteAsync<TState>(
        HttpContext http,
        Func<CancellationToken, Task<TState?>> read,
        Func<TState, string> render,
        Func<TState, bool> isFinal,
        ConnectorSignals signals,
        string signalKey,
        TimeSpan maximum,
        CancellationToken ct)
        where TState : class
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(render);
        ArgumentNullException.ThrowIfNull(isFinal);
        ArgumentNullException.ThrowIfNull(signals);

        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        // Buffering an event stream turns live progress into a single blob at
        // the end, which is the one thing it must not be.
        http.Response.Headers["X-Accel-Buffering"] = "no";

        var deadline = DateTimeOffset.UtcNow + maximum;
        string? last = null;

        while (!ct.IsCancellationRequested && DateTimeOffset.UtcNow < deadline)
        {
            var state = await read(ct);
            if (state is null) break;

            var rendered = render(state);
            if (!string.Equals(rendered, last, StringComparison.Ordinal))
            {
                await Emit(http, "state", rendered, ct);
                last = rendered;
            }

            if (isFinal(state)) break;

            if (!await signals.WaitAsync(signalKey, TimeSpan.FromSeconds(5), ct))
            {
                // A comment frame keeps proxies and load balancers from
                // reaping an idle stream during a two-minute bank login.
                await Emit(http, null, null, ct);
            }
        }
    }

    private static async Task Emit(HttpContext http, string? name, string? data, CancellationToken ct)
    {
        var frame = new StringBuilder();
        if (name is null)
        {
            frame.Append(": keep-alive\n\n");
        }
        else
        {
            frame.Append("event: ").Append(name).Append('\n');
            frame.Append("data: ").Append(data).Append("\n\n");
        }

        await http.Response.WriteAsync(frame.ToString(), Encoding.UTF8, ct);
        await http.Response.Body.FlushAsync(ct);
    }
}
