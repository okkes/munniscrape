using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DemoClient.Web.Relay;

/// <summary>
/// The connector is not answering at all - refused, unresolvable, or silent
/// past the deadline. Distinct from an error the connector chose to return,
/// because that one already carries its own envelope and the relay must not
/// reword it.
/// </summary>
public sealed class ConnectorUnreachableException(string service, string detail, Exception? inner = null)
    : Exception(detail, inner)
{
    public string Service { get; } = service;
}

/// <summary>
/// One connector answer, held as bytes so it can be handed on untouched.
/// </summary>
public sealed record ConnectorReply
{
    public required int Status { get; init; }

    public string? ContentType { get; init; }

    public required byte[] Body { get; init; }

    public string? ManifestVersion { get; init; }

    /// <summary>
    /// Response headers this relay carries through verbatim.
    ///
    /// A live frame is bytes plus two numbers - which frame it is and what
    /// size it was taken at - and the numbers travel as headers because the
    /// body is a JPEG. Dropping them turns a working stream into a picture the
    /// consumer cannot place: it cannot tell a new frame from one it has
    /// already drawn, and it cannot turn a tap on its own screen back into a
    /// fraction of the agent's viewport.
    ///
    /// That is not hypothetical. This relay forwarded status, content type and
    /// body and nothing else, so the first end-to-end run reached the browser
    /// as "live frame: no X-Live-Sequence header" - a stream that was working
    /// on both ends and unusable in the middle.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>
    /// An allowlist, not a copy of everything. A relay that forwarded whatever
    /// the connector happened to send would eventually forward something that
    /// means one thing on that hop and another on this one - a cookie, an auth
    /// challenge, a caching directive computed for a different caller.
    /// </summary>
    private static readonly string[] ForwardedHeaders = ["X-Live-Sequence", "X-Live-Size"];

    internal static Dictionary<string, string>? Forwarded(HttpResponseMessage response)
    {
        Dictionary<string, string>? carried = null;

        foreach (var name in ForwardedHeaders)
        {
            if (!response.Headers.TryGetValues(name, out var values)) continue;
            if (values.FirstOrDefault() is not { Length: > 0 } value) continue;

            carried ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            carried[name] = value;
        }

        return carried;
    }

    public bool IsSuccess => Status is >= 200 and < 300;

    /// <summary>
    /// The body as JSON, for the two places the relay must read one: pulling
    /// the ticket out of a resume, and the service kind out of a catalogue.
    /// Returns null rather than throwing - a connector that answered something
    /// unparseable is a failure to report, not an exception to leak.
    /// </summary>
    public JsonNode? Json()
    {
        if (Body.Length == 0) return null;

        try
        {
            return JsonNode.Parse(Body);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// The typed client for one connector: base address, credential, deadline.
///
/// Every path it is given is a <c>/v1/…</c> path built by the relay from route
/// values it has escaped itself, so a browser cannot steer a call at
/// <c>/v1/admin/*</c> by putting <c>..</c> in a provider id.
/// </summary>
public sealed class ConnectorClient(string service, ConnectorEndpointOptions options, HttpClient http)
{
    public const string DevelopmentKeyHeader = "X-Connector-Key";
    public const string TicketHeader = "X-Connector-Ticket";
    public const string DeviceClassHeader = "X-Device-Class";
    public const string ManifestVersionHeader = "X-Manifest-Version";

    public string Service { get; } = service;

    public ConnectorEndpointOptions Options { get; } = options;

    public Task<ConnectorReply> GetAsync(string path, string? ticket, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, path, content: null, ticket, deviceClass: null, ct);

    public Task<ConnectorReply> PostAsync<TBody>(
        string path, TBody body, string? ticket, string? deviceClass, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, path, JsonBody(body), ticket, deviceClass, ct);

    public Task<ConnectorReply> DeleteAsync(string path, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, path, content: null, ticket: null, deviceClass: null, ct);

    /// <summary>
    /// An unbuffered response, for the SSE proxy. No deadline is applied: the
    /// whole point of an event stream is that it outlives one.
    /// </summary>
    public async Task<HttpResponseMessage> StreamAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        Authorize(request);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        try
        {
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ConnectorUnreachableException(Service, Describe(ex), ex);
        }
    }

    private async Task<ConnectorReply> SendAsync(
        HttpMethod method, string path, HttpContent? content, string? ticket, string? deviceClass, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        Authorize(request);

        // A ticket is a bearer capability over one session. It is minted, used
        // and dropped inside a single relay call and never travels further.
        if (ticket is { Length: > 0 })
        {
            request.Headers.TryAddWithoutValidation(TicketHeader, ticket);
        }

        if (deviceClass is { Length: > 0 })
        {
            request.Headers.TryAddWithoutValidation(DeviceClassHeader, deviceClass);
        }

        // The deadline is per request rather than on the HttpClient because the
        // SSE proxy shares this client: HttpClient.Timeout would abort a live
        // event stream mid-login.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(Options.TimeoutSeconds));

        try
        {
            using var response = await http.SendAsync(request, deadline.Token);
            var body = await response.Content.ReadAsByteArrayAsync(deadline.Token);

            return new ConnectorReply
            {
                Status = (int)response.StatusCode,
                ContentType = response.Content.Headers.ContentType?.ToString(),
                Body = body,
                ManifestVersion = response.Headers.TryGetValues(ManifestVersionHeader, out var versions)
                    ? versions.FirstOrDefault()
                    : null,
                Headers = ConnectorReply.Forwarded(response),
            };
        }
        catch (HttpRequestException ex)
        {
            throw new ConnectorUnreachableException(Service, Describe(ex), ex);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ConnectorUnreachableException(
                Service, $"no answer within {Options.TimeoutSeconds}s");
        }
    }

    /// <summary>
    /// Development: one shared header, no PKI. An absent secret means no header
    /// at all, which is exactly what the connectors accept on localhost.
    ///
    /// Production is two independent layers and neither is wired here - see
    /// <see cref="ConnectorEndpointOptions"/>: an audience-checked M2M bearer
    /// token added at this line from a cached credential, and a client
    /// certificate attached to the primary handler in Program.cs. Either alone
    /// is a weak answer; the certificate survives a stolen token and the token
    /// survives a stolen certificate.
    /// </summary>
    private void Authorize(HttpRequestMessage request)
    {
        if (Options.SharedSecret is { Length: > 0 } secret)
        {
            request.Headers.TryAddWithoutValidation(DevelopmentKeyHeader, secret);
        }
    }

    private static HttpContent JsonBody<TBody>(TBody body) =>
        new StringContent(JsonSerializer.Serialize(body, RelayJson.Options), Encoding.UTF8, "application/json");

    /// <summary>
    /// The innermost cause, because "An error occurred while sending the
    /// request" is not a diagnosis and "connection refused" is.
    /// </summary>
    private static string Describe(Exception exception)
    {
        var cause = exception;
        while (cause.InnerException is { } inner) cause = inner;
        return cause.Message;
    }
}

/// <summary>
/// The connectors, resolved by service key.
///
/// Named clients rather than <c>AddHttpClient&lt;ConnectorClient&gt;</c>
/// because there are two instances of one type: a typed client registration
/// would give DI a single winner and silently point both services at whichever
/// one was registered last.
/// </summary>
public sealed class ConnectorClients
{
    private readonly Dictionary<string, ConnectorClient> _clients;

    public ConnectorClients(RelayOptions options, IHttpClientFactory factory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(factory);

        _clients = options.Connectors.ToDictionary(
            entry => entry.Key,
            entry => new ConnectorClient(entry.Key, entry.Value, factory.CreateClient(HttpClientName(entry.Key))),
            StringComparer.OrdinalIgnoreCase);

        All = [.. _clients.Values.OrderBy(c => c.Options.Order).ThenBy(c => c.Service, StringComparer.Ordinal)];
    }

    /// <summary>Every configured connector, in a stable order.</summary>
    public IReadOnlyList<ConnectorClient> All { get; }

    public static string HttpClientName(string service) => "connector:" + service.ToLowerInvariant();

    public bool TryGet(string service, [NotNullWhen(true)] out ConnectorClient? client) =>
        _clients.TryGetValue(service, out client);
}
