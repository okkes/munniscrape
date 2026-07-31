using System.Text.Json;
using ShopConnector.Api.Tests.Infrastructure;

namespace ShopConnector.Api.Tests;

/// <summary>
/// What the reference says each route answers with.
///
/// Every one of the 36 operations used to document a bodyless 200. Request
/// bodies, path parameters and query parameters were all inferred perfectly;
/// responses alone were empty, because minimal APIs read the handler's DECLARED
/// return type and every helper in this service returned <c>IResult</c>. A
/// reference that names what to send and stays silent about what comes back is
/// the half a caller cannot guess from a curl.
///
/// The assertions are exact <c>$ref</c> strings rather than substring checks.
/// The two that existed before this file - "the document mentions /v1/health"
/// and "/v1/providers" - passed happily while every response in it was empty,
/// which is the failure mode a document test exists to catch.
/// </summary>
[Collection(ShopApiCollection.Name)]
public sealed class ApiDocumentTests(ShopApiFactory factory)
{
    /// <summary>
    /// Routes whose success carries no body at all, and why. Anything outside
    /// this list that documents an empty success is a route nobody annotated -
    /// which is exactly the state the whole service was in.
    /// </summary>
    private static readonly HashSet<string> BodylessOnPurpose = new(StringComparer.Ordinal)
    {
        // A frame the agent posted. 200 either way, and deliberately empty: a
        // stale frame is the network's fault, and an error would make an agent
        // retry a photograph that is already out of date.
        "post /agent/v1/jobs/{jobId}/live/frame 200",
    };

    private async Task<JsonElement> DocumentAsync()
    {
        using var http = factory.CreateClient();
        using var stream = await http.GetStreamAsync("/openapi/v1.json");
        using var parsed = await JsonDocument.ParseAsync(stream);
        return parsed.RootElement.Clone();
    }

    private static JsonElement Response(JsonElement document, string path, string method, string status) =>
        document.GetProperty("paths").GetProperty(path).GetProperty(method).GetProperty("responses")
            .GetProperty(status);

    // ---- the bodies --------------------------------------------------------

    [Theory]
    // The catalogue, health and the kill switch.
    [InlineData("/v1/health", "get", "200", "HealthResponse")]
    [InlineData("/v1/providers", "get", "200", "CatalogResponse")]
    [InlineData("/v1/status", "get", "200", "StatusResponse")]
    [InlineData("/v1/admin/providers/{id}/status", "post", "200", "ProviderStatus")]
    // Connecting. The 202 is the same body under a different promise: poll or
    // subscribe rather than "the bundle is in your hands".
    [InlineData("/v1/{provider}/login", "post", "200", "SessionResponse")]
    [InlineData("/v1/{provider}/login", "post", "202", "SessionResponse")]
    [InlineData("/v1/{provider}/login/{sessionId}", "get", "200", "SessionResponse")]
    [InlineData("/v1/{provider}/login/{sessionId}/answer", "post", "200", "SessionResponse")]
    [InlineData("/v1/{provider}/login/{sessionId}/cancel", "post", "200", "SessionResponse")]
    [InlineData("/v1/{provider}/sessions/resume", "post", "200", "ResumeResponse")]
    // Fetching: the data, or a handle to the job still producing it.
    [InlineData("/v1/{provider}/{resource}", "get", "200", "DataResponse")]
    [InlineData("/v1/{provider}/{resource}", "get", "202", "JobAcceptedResponse")]
    [InlineData("/v1/{provider}/{resource}:fetch", "post", "200", "DataResponse")]
    [InlineData("/v1/{provider}/{resource}:fetch", "post", "202", "JobAcceptedResponse")]
    [InlineData("/v1/{provider}/{resource}/ack", "post", "200", "AckResponse")]
    [InlineData("/v1/{provider}/jobs/{jobId}", "get", "200", "JobResponse")]
    [InlineData("/v1/{provider}/jobs/{jobId}/answer", "post", "200", "JobResponse")]
    // Bring-your-own agents, from the consumer's side.
    [InlineData("/v1/agents", "get", "200", "AgentListResponse")]
    [InlineData("/v1/agents/enrollment", "post", "200", "AgentEnrollmentResponse")]
    // The agent protocol.
    [InlineData("/agent/v1/enroll", "post", "200", "EnrollResponse")]
    [InlineData("/agent/v1/heartbeat", "post", "200", "HeartbeatResponse")]
    [InlineData("/agent/v1/jobs/lease", "post", "200", "LeasedJob")]
    [InlineData("/agent/v1/jobs/{jobId}/renew", "post", "200", "RenewResponse")]
    [InlineData("/agent/v1/jobs/{jobId}/challenge", "post", "200", "RaiseChallengeResponse")]
    [InlineData("/agent/v1/jobs/{jobId}/answer", "get", "200", "ChallengeAnswer")]
    [InlineData("/agent/v1/jobs/{jobId}/result", "post", "200", "AgentAckResponse")]
    [InlineData("/agent/v1/jobs/{jobId}/fail", "post", "200", "AgentFailResponse")]
    [InlineData("/agent/v1/jobs/{jobId}/live/input", "get", "200", "LiveInputBatch")]
    public async Task A_json_route_names_the_schema_it_returns(
        string path, string method, string status, string schema)
    {
        var document = await DocumentAsync();

        var body = Response(document, path, method, status)
            .GetProperty("content").GetProperty("application/json").GetProperty("schema");

        Assert.Equal($"#/components/schemas/{schema}", body.GetProperty("$ref").GetString());
    }

    [Theory]
    // A stream of the very view the poll returns, one per event. Without the
    // frame type a consumer is told a subscription returns nothing.
    [InlineData("/v1/{provider}/login/{sessionId}/events", "get", "text/event-stream", "SessionResponse")]
    [InlineData("/v1/{provider}/jobs/{jobId}/events", "get", "text/event-stream", "JobResponse")]
    public async Task A_stream_names_its_media_type_and_the_view_it_carries(
        string path, string method, string mediaType, string schema)
    {
        var document = await DocumentAsync();

        var content = Response(document, path, method, "200").GetProperty("content");

        // The media type is the assertion: documenting a 10-minute event
        // stream as application/json would send a consumer to read it wrong.
        Assert.Equal(mediaType, Assert.Single(content.EnumerateObject()).Name);
        Assert.Equal(
            $"#/components/schemas/{schema}",
            content.GetProperty(mediaType).GetProperty("schema").GetProperty("$ref").GetString());
    }

    [Theory]
    // A relayed captcha, and a live login's frames. Bytes, not JSON.
    [InlineData("/v1/{provider}/login/{sessionId}/challenges/{challengeId}/image", "image/png")]
    [InlineData("/v1/{provider}/login/{sessionId}/challenges/{challengeId}/live/frame", "image/jpeg")]
    public async Task A_picture_is_documented_as_binary_under_its_own_media_type(string path, string mediaType)
    {
        var document = await DocumentAsync();

        var content = Response(document, path, "get", "200").GetProperty("content");

        // Under its own media type and no other: a picture offered as
        // application/json is a consumer trying to parse a PNG.
        Assert.Equal(mediaType, Assert.Single(content.EnumerateObject()).Name);

        var schema = content.GetProperty(mediaType).GetProperty("schema");
        Assert.Equal("string", schema.GetProperty("type").GetString());
        Assert.Equal("byte", schema.GetProperty("format").GetString());
    }

    /// <summary>
    /// The manifest is serialised through a node so the document's shape stays
    /// the spec's, so there is no C# type to point at - and the list endpoint
    /// has always declared its providers the same free-form way.
    /// </summary>
    [Fact]
    public async Task A_manifest_is_documented_as_the_free_form_object_it_is()
    {
        var document = await DocumentAsync();

        var schema = Response(document, "/v1/providers/{id}", "get", "200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema");

        Assert.Equal("#/components/schemas/JsonObject", schema.GetProperty("$ref").GetString());

        // And that schema really is open: an object with no declared
        // properties, which is the honest description of a document whose
        // shape is the manifest spec's rather than ours.
        var jsonObject = document.GetProperty("components").GetProperty("schemas").GetProperty("JsonObject");
        Assert.Equal("object", jsonObject.GetProperty("type").GetString());
        Assert.False(jsonObject.TryGetProperty("properties", out _));
    }

    [Fact]
    public async Task A_list_of_profiles_is_documented_as_an_array_of_them()
    {
        var document = await DocumentAsync();

        var schema = Response(document, "/v1/agents/{agentId}/profiles", "get", "200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema");

        Assert.Equal("array", schema.GetProperty("type").GetString());
        Assert.Equal(
            "#/components/schemas/ProfileView",
            schema.GetProperty("items").GetProperty("$ref").GetString());
    }

    // ---- the statuses ------------------------------------------------------

    [Theory]
    // Each of these returns 204 and nothing else. The default inference claims
    // a 200 for any handler it cannot read, so until they were annotated the
    // document promised a body on routes that never send one.
    [InlineData("/v1/{provider}/sessions/{sessionId}", "delete")]
    [InlineData("/v1/agents/{agentId}", "delete")]
    [InlineData("/agent/v1/jobs/{jobId}/progress", "post")]
    public async Task A_route_that_only_ever_returns_204_does_not_advertise_a_200(string path, string method)
    {
        var document = await DocumentAsync();

        var responses = Response(document, path, method, "204");
        Assert.False(responses.TryGetProperty("content", out _));

        var codes = document.GetProperty("paths").GetProperty(path).GetProperty(method)
            .GetProperty("responses").EnumerateObject().Select(p => p.Name);

        Assert.DoesNotContain("200", codes);
    }

    /// <summary>
    /// The guard against route thirty-seven. A new endpoint that never says
    /// what it returns lands here rather than in the reference, silently
    /// documented as answering nothing.
    /// </summary>
    [Fact]
    public async Task No_route_documents_a_success_with_no_body_unless_it_has_none()
    {
        var document = await DocumentAsync();

        var empty = new List<string>();
        var operations = 0;

        foreach (var path in document.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                operations++;

                foreach (var response in operation.Value.GetProperty("responses").EnumerateObject())
                {
                    // 204 and 202 say "no body" by their own meaning; every
                    // other success is a promise of one.
                    if (!response.Name.StartsWith('2') || response.Name.Length != 3) continue;
                    if (response.Name is "204" or "202") continue;
                    if (response.Value.TryGetProperty("content", out _)) continue;

                    empty.Add($"{operation.Name} {path.Name} {response.Name}");
                }
            }
        }

        Assert.Equal(BodylessOnPurpose, empty.ToHashSet(StringComparer.Ordinal));

        // The count is asserted so a route that vanishes from the document is
        // as loud as one that arrives undocumented.
        Assert.Equal(36, operations);
    }

    [Theory]
    [InlineData("400")]
    [InlineData("401")]
    [InlineData("500")]
    public async Task Every_public_route_documents_the_envelope_it_fails_into(string status)
    {
        var document = await DocumentAsync();

        var missing = new List<string>();

        foreach (var path in document.GetProperty("paths").EnumerateObject())
        {
            // The /v1 group is where the exception filter lives, so it is the
            // group whose failure shape is a promise. Liveness is mapped on the
            // root instead - it carries no auth and no data on purpose, so it
            // sits outside both filters and has no envelope to promise.
            if (!path.Name.StartsWith("/v1/", StringComparison.Ordinal)) continue;
            if (path.Name == "/v1/health") continue;

            foreach (var operation in path.Value.EnumerateObject())
            {
                var found = operation.Value.GetProperty("responses")
                    .TryGetProperty(status, out var response)
                    && response.GetProperty("content").GetProperty("application/json")
                        .GetProperty("schema").GetProperty("$ref").GetString()
                    == "#/components/schemas/ConnectorErrorEnvelope";

                if (!found) missing.Add($"{operation.Name} {path.Name}");
            }
        }

        Assert.Empty(missing);
    }

    /// <summary>
    /// The other half of the rule above, asserted rather than assumed: liveness
    /// answers one way and one way only. A probe that needs a credential is a
    /// probe that fails for the wrong reason, so it is mapped outside the group
    /// that owns the envelope - and the document should say so.
    /// </summary>
    [Fact]
    public async Task Liveness_promises_nothing_but_the_one_answer_it_gives()
    {
        var document = await DocumentAsync();

        var codes = document.GetProperty("paths").GetProperty("/v1/health").GetProperty("get")
            .GetProperty("responses").EnumerateObject().Select(p => p.Name);

        Assert.Equal("200", Assert.Single(codes));
    }

    // ---- the encoding ------------------------------------------------------

    /// <summary>
    /// The schema generator reads the JSON options registered in DI, never the
    /// ones handed to <c>Results.Json</c>. So this passes only because
    /// <c>AddConnectorPlatform</c> copies the wire policy into
    /// <c>ConfigureHttpJsonOptions</c> - and without it the document would
    /// confidently describe every field under a name the service never sends,
    /// which is worse than describing no body at all.
    /// </summary>
    [Fact]
    public async Task The_documented_field_names_are_the_ones_that_go_on_the_wire()
    {
        var document = await DocumentAsync();

        var properties = document.GetProperty("components").GetProperty("schemas")
            .GetProperty("SessionResponse").GetProperty("properties")
            .EnumerateObject().Select(p => p.Name).ToList();

        Assert.Contains("session_id", properties);
        Assert.Contains("expires_at", properties);
        Assert.Contains("provider_account", properties);

        // The camelCase a default generator would have produced.
        Assert.DoesNotContain("sessionId", properties);
        Assert.DoesNotContain("expiresAt", properties);
        Assert.DoesNotContain("providerAccount", properties);
    }

    [Fact]
    public async Task The_documented_enum_members_are_the_ones_that_go_on_the_wire()
    {
        var document = await DocumentAsync();

        var members = document.GetProperty("components").GetProperty("schemas")
            .GetProperty("JobStep").GetProperty("enum")
            .EnumerateArray().Select(m => m.GetString()).ToList();

        // snake_case, from the converter the wire policy carries. A document
        // naming these "OpeningProvider" describes an API this service does
        // not serve.
        Assert.Contains("opening_provider", members);
        Assert.Contains("awaiting_human", members);
        Assert.DoesNotContain("OpeningProvider", members);
    }
}
