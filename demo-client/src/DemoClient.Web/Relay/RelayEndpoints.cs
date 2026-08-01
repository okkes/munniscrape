using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;

namespace DemoClient.Web.Relay;

/// <summary>
/// The <c>/api/*</c> surface: everything the browser is allowed to know about.
///
/// The tab never learns a connector hostname, never holds a connector
/// credential, never sees a ticket, and never gets a CORS grant. It sends a
/// bundle and gets a bundle back. That is the whole topology, and this file is
/// the part munni would own.
///
/// Two things the relay does rather than pass through:
/// <list type="bullet">
///   <item><b>resume is hidden.</b> A fetch is resume → ticket → GET, all
///   inside one handler. The ticket is a bearer capability over the session
///   and stays in a local.</item>
///   <item><b>the subject is minted here.</b> A client cannot choose who it
///   claims to be, and the two connectors get different pseudonyms for the
///   same person.</item>
/// </list>
/// Everything else - manifests, errors, data, challenges - is the connector's
/// bytes, unaltered. The relay is deliberately dumb: it does not interpret
/// manifests, normalise data, or make retry decisions.
/// </summary>
public static class RelayEndpoints
{
    /// <summary>
    /// The bundle a browser holds dies with the tab session, so the connector
    /// caps its TTL at an hour. Declaring the class honestly is what makes a
    /// bundle that escapes the tab a small problem rather than a large one.
    /// </summary>
    private const string WebDeviceClass = "web";

    public static WebApplication MapRelayApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var api = app.MapGroup("/api").AddEndpointFilter<RelayExceptionFilter>();

        MapServices(api);
        MapCatalogue(api);
        MapAgents(api);
        MapLogin(api);
        MapFetch(api);
        MapJobs(api);
        MapPrefill(api);

        return app;
    }

    // ── Which connectors are actually there ─────────────────────────────────

    private static void MapServices(RouteGroupBuilder api)
    {
        // Probing the catalogue rather than /v1/health on purpose: health
        // carries no auth, so it would report a connector reachable that
        // rejects every real call for a bad shared key. The UI has to be able
        // to say "the bank connector is not running" instead of spinning.
        api.MapGet("/services", async (ConnectorClients clients, CancellationToken ct) =>
        {
            var probes = clients.All.Select(async client =>
            {
                try
                {
                    var reply = await client.GetAsync("/v1/providers", ticket: null, ct);

                    return new ServiceView
                    {
                        Key = client.Service,
                        Name = client.Options.Name,
                        Kind = Text(Property(Property(reply.Json(), "service"), "kind")) ?? client.Options.Kind,
                        Reachable = reply.IsSuccess,
                        Error = reply.IsSuccess ? null : $"connector answered {reply.Status}",
                    };
                }
                catch (ConnectorUnreachableException ex)
                {
                    return new ServiceView
                    {
                        Key = client.Service,
                        Name = client.Options.Name,
                        Kind = client.Options.Kind,
                        Reachable = false,
                        Error = ex.Message,
                    };
                }
            });

            return Results.Json(await Task.WhenAll(probes), RelayJson.Options);
        });
    }

    // ── The catalogue, verbatim ─────────────────────────────────────────────

    private static void MapCatalogue(RouteGroupBuilder api)
    {
        // The manifest is the only contract the SPA codes against: it renders
        // login forms from it and decides which resources to offer. Rewriting
        // any of it here would be the relay inventing a second contract.
        api.MapGet("/{service}/providers", async (
            string service,
            ConnectorClients clients,
            ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            if (!clients.TryGet(service, out var client)) return RelayErrors.UnknownService(service, Log(loggers));

            return Pass(await client.GetAsync("/v1/providers", ticket: null, ct));
        });
    }

    private static void MapAgents(RouteGroupBuilder api)
    {
        api.MapGet("/{service}/agents", async (
            string service,
            ConnectorClients clients,
            SubjectMinter subjects,
            ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            if (!clients.TryGet(service, out var client)) return RelayErrors.UnknownService(service, Log(loggers));

            var reply = await client.GetAsync(
                $"/v1/agents?subject={Uri.EscapeDataString(subjects.For(service))}", ticket: null, ct);

            if (!reply.IsSuccess) return Pass(reply);

            // The connector keys an agent as `id`, which is unambiguous inside
            // its own document and is not once a client is holding session
            // ids, job ids and agent ids at the same time. Everything else on
            // the row is copied through untouched.
            var agents = new JsonArray();

            if (Property(reply.Json(), "agents") is JsonArray rows)
            {
                foreach (var row in rows)
                {
                    if (row is not JsonObject agent) continue;

                    var view = new JsonObject { ["agent_id"] = agent["id"]?.DeepClone() };
                    foreach (var (key, value) in agent)
                    {
                        if (string.Equals(key, "id", StringComparison.Ordinal)) continue;
                        view[key] = value?.DeepClone();
                    }

                    agents.Add(view);
                }
            }

            return Results.Json(agents, RelayJson.Options);
        });
    }

    // ── Connecting, and driving an interactive login ────────────────────────

    private static void MapLogin(RouteGroupBuilder api)
    {
        api.MapPost("/{service}/{provider}/login", async (
            string service,
            string provider,
            LoginBody? body,
            ConnectorClients clients,
            SubjectMinter subjects,
            ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            if (!clients.TryGet(service, out var client)) return RelayErrors.UnknownService(service, Log(loggers));

            var request = new ConnectorLoginRequest
            {
                // Minted, not accepted. A client that could name its own
                // subject could read another user's sessions.
                Subject = subjects.For(service),
                Label = body?.Label,
                Inputs = body?.Inputs ?? Empty,
                Config = body?.Config ?? Empty,
                PreferAgent = body?.PreferAgent,

                // Revealed only here, into the outbound body. The relay is a
                // pipe for this exactly as it is for a session bundle.
                CredentialBundle = body?.CredentialBundle.IsPresent == true
                    ? body.CredentialBundle.Reveal()
                    : null,

                // Only when the operator states that this consumer shows
                // terms. Inventing a consent record the user never saw would
                // be a lie told in JSON.
                Consent = client.Options.ConsentTermsVersion is { Length: > 0 } terms
                    ? new ConnectorConsent { AcceptedAt = DateTimeOffset.UtcNow, TermsVersion = terms }
                    : null,
            };

            return Pass(await client.PostAsync(
                $"/v1/{Segment(provider)}/login", request, ticket: null, deviceClass: WebDeviceClass, ct));
        });

        api.MapGet("/{service}/{provider}/login/{sessionId}", async (
            string service,
            string provider,
            string sessionId,
            ConnectorClients clients,
            ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            if (!clients.TryGet(service, out var client)) return RelayErrors.UnknownService(service, Log(loggers));

            return Pass(await client.GetAsync(
                $"/v1/{Segment(provider)}/login/{Segment(sessionId)}", ticket: null, ct));
        });

        api.MapGet("/{service}/{provider}/login/{sessionId}/events", (
            HttpContext http,
            string service,
            string provider,
            string sessionId,
            ConnectorClients clients,
            ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            if (!clients.TryGet(service, out var client)) return Task.FromResult(RelayErrors.UnknownService(service, Log(loggers)));

            return ProxyEventsAsync(
                http, client, $"/v1/{Segment(provider)}/login/{Segment(sessionId)}/events", ct);
        });

        api.MapGet("/{service}/{provider}/login/{sessionId}/challenges/{challengeId}/image", async (
            string service,
            string provider,
            string sessionId,
            string challengeId,
            ConnectorClients clients,
            ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            if (!clients.TryGet(service, out var client)) return RelayErrors.UnknownService(service, Log(loggers));

            // The image is authenticated and one-shot at the connector, so the
            // relay is the only thing that can fetch it - which is why a tab
            // cannot be handed the URL directly.
            return Pass(await client.GetAsync(
                $"/v1/{Segment(provider)}/login/{Segment(sessionId)}/challenges/{Segment(challengeId)}/image",
                ticket: null, ct));
        });

        // ── the live view ───────────────────────────────────────────────
        //
        // Two more proxies, for the same reason the image needs one: the
        // browser never learns a connector hostname and never holds a
        // connector credential, so every byte of a streamed login goes through
        // here. These were the missing hop - the agent was photographing the
        // page and the client was asking for pictures, and the relay in
        // between had no route to carry them, so a working stream on both ends
        // met a 404 in the middle.

        api.MapGet("/{service}/{provider}/login/{sessionId}/challenges/{challengeId}/live/frame", async (
            HttpContext http,
            string service,
            string provider,
            string sessionId,
            string challengeId,
            ConnectorClients clients,
            ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            if (!clients.TryGet(service, out var client)) return RelayErrors.UnknownService(service, Log(loggers));

            // The cursor rides through unchanged. It is the consumer's own
            // "highest frame I have already drawn", and the connector answers
            // 204 when it has nothing newer - which is most polls on a login
            // form nobody is touching.
            var after = http.Request.Query["after"].ToString();
            var query = string.IsNullOrEmpty(after) ? string.Empty : $"?after={Uri.EscapeDataString(after)}";

            return Pass(await client.GetAsync(
                $"/v1/{Segment(provider)}/login/{Segment(sessionId)}/challenges/{Segment(challengeId)}/live/frame{query}",
                ticket: null, ct));
        });

        api.MapPost("/{service}/{provider}/login/{sessionId}/challenges/{challengeId}/live/input", async (
            HttpContext http,
            string service,
            string provider,
            string sessionId,
            string challengeId,
            ConnectorClients clients,
            ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            if (!clients.TryGet(service, out var client)) return RelayErrors.UnknownService(service, Log(loggers));

            // Forwarded as raw JSON rather than through a typed body, so the
            // relay never has to know what an input event is. It is a pipe
            // here, and a pipe that understood the payload would be a second
            // place for the grammar to drift from the one that enforces it.
            var batch = await ReadOptionalAsync<System.Text.Json.Nodes.JsonNode>(http, ct);

            return Pass(await client.PostAsync(
                $"/v1/{Segment(provider)}/login/{Segment(sessionId)}/challenges/{Segment(challengeId)}/live/input",
                batch, ticket: null, deviceClass: null, ct));
        });

        api.MapPost("/{service}/{provider}/login/{sessionId}/answer", async (
            string service,
            string provider,
            string sessionId,
            AnswerBody? body,
            ConnectorClients clients,
            ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            if (!clients.TryGet(service, out var client)) return RelayErrors.UnknownService(service, Log(loggers));
            if (body?.ChallengeId is not { Length: > 0 } challengeId)
            {
                return RelayErrors.InvalidRequest("an answer needs a challenge_id", Log(loggers));
            }

            var request = new ConnectorAnswerRequest { ChallengeId = challengeId, Value = body.Value ?? string.Empty };

            return Pass(await client.PostAsync(
                $"/v1/{Segment(provider)}/login/{Segment(sessionId)}/answer", request, ticket: null, deviceClass: null, ct));
        });

        api.MapDelete("/{service}/{provider}/sessions/{sessionId}", async (
            HttpContext http,
            string service,
            string provider,
            string sessionId,
            ConnectorClients clients,
            SubjectMinter subjects,
            ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            if (!clients.TryGet(service, out var client)) return RelayErrors.UnknownService(service, Log(loggers));

            var body = await ReadOptionalAsync<DisconnectBody>(http, ct);

            if (body?.Bundle.IsPresent == true)
            {
                // Resuming first proves the caller actually holds this
                // connection's bundle. The connector's DELETE does not check
                // that - it trusts its consumer to have checked - so the
                // consumer is where the check belongs.
                var resumed = await ResumeAsync(client, subjects.For(service), provider, body.Bundle, ct);
                if (!resumed.IsSuccess) return Pass(resumed);
            }

            var reply = await client.DeleteAsync(
                $"/v1/{Segment(provider)}/sessions/{Segment(sessionId)}", ct);

            return reply.IsSuccess
                ? Results.Json(new DisconnectedView { Disconnected = true }, RelayJson.Options)
                : Pass(reply);
        });
    }

    // ── Fetching, and acknowledging ─────────────────────────────────────────

    private static void MapFetch(RouteGroupBuilder api)
    {
        api.MapPost("/{service}/{provider}/{resource}/fetch", async (
            string service,
            string provider,
            string resource,
            FetchBody? body,
            ConnectorClients clients,
            SubjectMinter subjects,
            ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            var log = Log(loggers);
            if (!clients.TryGet(service, out var client)) return RelayErrors.UnknownService(service, log);
            if (body?.Bundle.IsPresent != true) return RelayErrors.InvalidRequest("a fetch needs a bundle", log);

            var resumed = await ResumeAsync(client, subjects.For(service), provider, body.Bundle, ct);

            // A dead bundle comes back as the connector's own session_expired
            // envelope. Rewording it would cost the SPA the message_key it
            // maps "sign in again" from.
            if (!resumed.IsSuccess) return Pass(resumed);

            if (Text(Property(resumed.Json(), "ticket")) is not { Length: > 0 } ticket)
            {
                return RelayErrors.Internal("resume succeeded without a ticket", log);
            }

            // Interpolating the bundle logs the redacted form - see Bundle.
            log.LogInformation("fetch {Service}/{Provider}/{Resource} for {Bundle}",
                service, provider, resource, body.Bundle);

            // The ticket lives in that local and dies with this method. It is
            // never written to a response, a cookie, or a log.
            return Pass(await client.GetAsync(
                $"/v1/{Segment(provider)}/{Segment(resource)}{Query(body.Params)}", ticket, ct));
        });

        api.MapPost("/{service}/{provider}/{resource}/ack", async (
            string service,
            string provider,
            string resource,
            AckBody? body,
            ConnectorClients clients,
            SubjectMinter subjects,
            ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            var log = Log(loggers);
            if (!clients.TryGet(service, out var client)) return RelayErrors.UnknownService(service, log);
            if (body?.Bundle.IsPresent != true) return RelayErrors.InvalidRequest("an ack needs a bundle", log);
            if (body.Cursor is not { Length: > 0 } cursor) return RelayErrors.InvalidRequest("an ack needs a cursor", log);

            var resumed = await ResumeAsync(client, subjects.For(service), provider, body.Bundle, ct);
            if (!resumed.IsSuccess) return Pass(resumed);

            if (Text(Property(resumed.Json(), "ticket")) is not { Length: > 0 } ticket)
            {
                return RelayErrors.Internal("resume succeeded without a ticket", log);
            }

            // Acking is what keeps the connector a pipe: the rows it just
            // handed over are deleted the moment the consumer says it has
            // them. Skipping this leaves them to die on a 7-day timer instead.
            return Pass(await client.PostAsync(
                $"/v1/{Segment(provider)}/{Segment(resource)}/ack",
                new ConnectorAckRequest { Cursor = cursor }, ticket, deviceClass: null, ct));
        });
    }

    // ── Following a fetch that did not finish inside its window ─────────────

    private static void MapJobs(RouteGroupBuilder api)
    {
        api.MapGet("/{service}/{provider}/jobs/{jobId}", async (
            string service,
            string provider,
            string jobId,
            ConnectorClients clients,
            ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            if (!clients.TryGet(service, out var client)) return RelayErrors.UnknownService(service, Log(loggers));

            return Pass(await client.GetAsync($"/v1/{Segment(provider)}/jobs/{Segment(jobId)}", ticket: null, ct));
        });

        api.MapGet("/{service}/{provider}/jobs/{jobId}/events", (
            HttpContext http,
            string service,
            string provider,
            string jobId,
            ConnectorClients clients,
            ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            if (!clients.TryGet(service, out var client)) return Task.FromResult(RelayErrors.UnknownService(service, Log(loggers)));

            return ProxyEventsAsync(http, client, $"/v1/{Segment(provider)}/jobs/{Segment(jobId)}/events", ct);
        });

        api.MapPost("/{service}/{provider}/jobs/{jobId}/answer", async (
            string service,
            string provider,
            string jobId,
            AnswerBody? body,
            ConnectorClients clients,
            ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            if (!clients.TryGet(service, out var client)) return RelayErrors.UnknownService(service, Log(loggers));
            if (body?.ChallengeId is not { Length: > 0 } challengeId)
            {
                return RelayErrors.InvalidRequest("an answer needs a challenge_id", Log(loggers));
            }

            var request = new ConnectorAnswerRequest { ChallengeId = challengeId, Value = body.Value ?? string.Empty };

            return Pass(await client.PostAsync(
                $"/v1/{Segment(provider)}/jobs/{Segment(jobId)}/answer", request, ticket: null, deviceClass: null, ct));
        });
    }

    // ── Development convenience ─────────────────────────────────────────────

    private static void MapPrefill(RouteGroupBuilder api)
    {
        api.MapGet("/prefill/{service}/{provider}", (
            string service,
            string provider,
            PrefillStore store,
            IHostEnvironment environment) =>
        {
            if (!environment.IsDevelopment())
            {
                // Not a feature flag - a refusal. An endpoint that hands out
                // stored credentials must not exist outside a developer's
                // machine, so it answers as though it does not.
                return RelayErrors.Envelope(StatusCodes.Status404NotFound, new RelayError
                {
                    Code = "unsupported_resource",
                    Retriable = false,
                    UserAction = "none",
                    MessageKey = "connect.error.unsupported_resource",
                });
            }

            var entry = store.Find(service, provider);

            // Absent is `{}`, not an empty shape: the form should fall back to
            // its manifest defaults, not to two empty maps it has to inspect.
            return entry is null
                ? Results.Json(new JsonObject(), RelayJson.Options)
                : Results.Json(entry, RelayJson.Options);
        });
    }

    // ── Plumbing ────────────────────────────────────────────────────────────

    private static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private static ILogger Log(ILoggerFactory loggers) => loggers.CreateLogger("Relay");

    private static IResult Pass(ConnectorReply reply) => new PassThroughResult(reply);

    /// <summary>
    /// Route values arrive decoded, so they go back out encoded. A provider id
    /// of <c>../admin/providers/jumbo/status</c> would otherwise be a browser
    /// steering a call at the connector's kill switch.
    /// </summary>
    private static string Segment(string value) => Uri.EscapeDataString(value);

    private static Task<ConnectorReply> ResumeAsync(
        ConnectorClient client, string subject, string provider, Bundle bundle, CancellationToken ct) =>
        client.PostAsync(
            $"/v1/{Segment(provider)}/sessions/resume",
            new ConnectorResumeRequest { Subject = subject, Bundle = bundle },
            ticket: null,
            deviceClass: null,
            ct);

    /// <summary>
    /// The JSON params body becomes the query-string vocabulary the connector
    /// validates: <c>{"include":["items"]}</c> is <c>include=items</c>, and a
    /// multi-valued param repeats its key rather than joining on a comma - a
    /// value containing one would otherwise silently become two.
    /// </summary>
    private static string Query(IReadOnlyDictionary<string, JsonNode?>? parameters)
    {
        if (parameters is null || parameters.Count == 0) return string.Empty;

        var query = new StringBuilder();

        foreach (var (key, node) in parameters)
        {
            switch (node)
            {
                case null:
                    continue;

                case JsonArray array:
                    foreach (var item in array)
                    {
                        if (item is not null) Append(query, key, item.ToString());
                    }

                    break;

                default:
                    Append(query, key, node.ToString());
                    break;
            }
        }

        return query.Length == 0 ? string.Empty : "?" + query;

        static void Append(StringBuilder query, string key, string value)
        {
            if (query.Length > 0) query.Append('&');
            query.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
        }
    }

    /// <summary>
    /// Streams an event stream through without buffering it anywhere.
    ///
    /// Buffering turns live progress into one blob at the end, which is the
    /// one thing it must not be: an interactive bank login runs for ninety
    /// seconds and stops to ask questions.
    /// </summary>
    private static async Task<IResult> ProxyEventsAsync(
        HttpContext http, ConnectorClient client, string path, CancellationToken ct)
    {
        using var upstream = await client.StreamAsync(path, ct);

        if (!upstream.IsSuccessStatusCode)
        {
            // Not a stream at all - an envelope. Hand it over as one.
            return Pass(new ConnectorReply
            {
                Status = (int)upstream.StatusCode,
                ContentType = upstream.Content.Headers.ContentType?.ToString(),
                Body = await upstream.Content.ReadAsByteArrayAsync(ct),
            });
        }

        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        // Tells a reverse proxy the same thing DisableBuffering tells Kestrel.
        http.Response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        await using var stream = await upstream.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[4096];

        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                await http.Response.Body.WriteAsync(buffer.AsMemory(0, read), ct);
                await http.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // The tab navigated away mid-login. Expected, not a failure.
        }

        return Results.Empty;
    }

    /// <summary>A body that may legitimately be absent - DELETE with no bundle.</summary>
    private static async Task<TBody?> ReadOptionalAsync<TBody>(HttpContext http, CancellationToken ct)
        where TBody : class
    {
        if (!http.Request.HasJsonContentType()) return null;

        try
        {
            return await http.Request.ReadFromJsonAsync<TBody>(RelayJson.Options, ct);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads one property without assuming the connector answered an object.
    /// A malformed body is a failure to report, never an exception to leak.
    /// </summary>
    private static JsonNode? Property(JsonNode? node, string name) =>
        node is JsonObject json && json.TryGetPropertyValue(name, out var value) ? value : null;

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

// ── The relay's own wire shapes ─────────────────────────────────────────────

public sealed record ServiceView
{
    public required string Key { get; init; }

    public required string Name { get; init; }

    public required string Kind { get; init; }

    public required bool Reachable { get; init; }

    /// <summary>Operator-facing diagnostic, present only when unreachable.</summary>
    public string? Error { get; init; }
}

public sealed record DisconnectedView
{
    public required bool Disconnected { get; init; }
}

public sealed record LoginBody
{
    public IReadOnlyDictionary<string, string>? Inputs { get; init; }

    public IReadOnlyDictionary<string, string>? Config { get; init; }

    public string? Label { get; init; }

    /// <summary>Required by a <c>device_persistent</c> provider, absent for every other.</summary>
    public string? PreferAgent { get; init; }

    /// <summary>
    /// A sealed credential the browser kept from an earlier login, offered
    /// instead of typing the password again.
    /// </summary>
    /// <remarks>
    /// Passed through and never stored, exactly like a session bundle - and
    /// wearing <see cref="Bundle"/> for the same reason, so it redacts itself
    /// rather than turning up whole in a log line. Only a provider declaring
    /// <c>offers_credential_store</c> ever mints one, and the connector reads
    /// it only when <see cref="Inputs"/> is empty.
    /// </remarks>
    public Bundle CredentialBundle { get; init; }
}

public sealed record FetchBody
{
    public Bundle Bundle { get; init; }

    public Dictionary<string, JsonNode?>? Params { get; init; }
}

public sealed record AckBody
{
    public Bundle Bundle { get; init; }

    public string? Cursor { get; init; }
}

public sealed record AnswerBody
{
    public string? ChallengeId { get; init; }

    public string? Value { get; init; }
}

public sealed record DisconnectBody
{
    public Bundle Bundle { get; init; }
}

// ── What goes out to a connector ────────────────────────────────────────────

internal sealed record ConnectorLoginRequest
{
    public required string Subject { get; init; }

    public string? Label { get; init; }

    public required IReadOnlyDictionary<string, string> Inputs { get; init; }

    public required IReadOnlyDictionary<string, string> Config { get; init; }

    public string? PreferAgent { get; init; }

    /// <summary>Forwarded verbatim; the relay cannot open it and keeps no copy.</summary>
    public string? CredentialBundle { get; init; }

    public ConnectorConsent? Consent { get; init; }
}

internal sealed record ConnectorConsent
{
    public required DateTimeOffset AcceptedAt { get; init; }

    public required string TermsVersion { get; init; }
}

internal sealed record ConnectorResumeRequest
{
    public required string Subject { get; init; }

    public required Bundle Bundle { get; init; }
}

internal sealed record ConnectorAnswerRequest
{
    public required string ChallengeId { get; init; }

    public required string Value { get; init; }
}

internal sealed record ConnectorAckRequest
{
    public required string Cursor { get; init; }
}
