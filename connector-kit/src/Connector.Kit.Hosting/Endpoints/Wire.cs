using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Connector.Kit.Adapters;
using Connector.Kit.Challenges;
using Connector.Kit.Errors;
using Connector.Kit.Hosting.Challenges;
using Connector.Kit.Hosting.Data;
using Connector.Kit.Hosting.Infrastructure;
using Connector.Kit.Jobs;
using Connector.Kit.Manifests;
using Connector.Kit.Sessions;

namespace Connector.Kit.Hosting.Endpoints;

// The public wire shapes, in one file. Snake case comes from the serializer,
// so these read as ordinary C# and still land on the contract in the spec.

public sealed record CatalogResponse
{
    public required IReadOnlyList<JsonObject> Providers { get; init; }

    public required ServiceDescriptor Service { get; init; }
}

public sealed record ServiceDescriptor
{
    /// <summary><c>bank</c> or <c>store</c>.</summary>
    public required ProviderKind Kind { get; init; }

    public required string Version { get; init; }

    public required string ManifestDigest { get; init; }
}

public sealed record LoginRequest
{
    public required string Subject { get; init; }

    /// <summary>Echoed back so a user can tell two connections apart. Never sent upstream.</summary>
    public string? Label { get; init; }

    public IReadOnlyDictionary<string, string> Inputs { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// A credential bundle this connector sealed earlier, offered instead of
    /// asking the human again.
    ///
    /// Read only when <see cref="Inputs"/> is empty, so a caller that sends
    /// both is taken at the word of what it typed rather than what it
    /// remembered. What comes out is fed through the manifest's own validator
    /// exactly as posted inputs are, so a bundle cannot carry a field the
    /// provider never declared.
    /// </summary>
    public string? CredentialBundle { get; init; }

    /// <summary>Non-secret settings the provider needs on every call.</summary>
    public IReadOnlyDictionary<string, string> Config { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public ConsentRecord? Consent { get; init; }

    /// <summary>Required for a <c>device_persistent</c> provider.</summary>
    public string? PreferAgent { get; init; }
}

public sealed record ConsentRecord
{
    public DateTimeOffset? AcceptedAt { get; init; }

    public string? TermsVersion { get; init; }
}

public sealed record SessionResponse
{
    public required string SessionId { get; init; }

    public required SessionState State { get; init; }

    /// <summary>Present exactly once, when the session first becomes usable or rotates.</summary>
    public string? Bundle { get; init; }

    /// <summary>
    /// What the human typed, sealed for this device to keep - present exactly
    /// once, and only where the provider declares
    /// <c>offers_credential_store</c>.
    ///
    /// Offer it back as <c>credential_bundle</c> on the next login and the same
    /// human is not asked again. Store it the way the device class allows: on
    /// web that is the tab and nothing longer, because a sealed password
    /// surviving a browser restart is the thing the whole custody model exists
    /// to avoid.
    /// </summary>
    public string? CredentialBundle { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public ProviderAccount? ProviderAccount { get; init; }

    public ChallengeView? Challenge { get; init; }

    public ProgressView? Progress { get; init; }

    /// <summary><c>ephemeral</c> for a web client, so the consumer can say "sign in again to sync".</summary>
    public string? Custody { get; init; }

    public string? Label { get; init; }

    public IReadOnlyDictionary<string, string>? Config { get; init; }
}

public sealed record ChallengeView
{
    public required string Id { get; init; }

    public required ChallengeType Type { get; init; }

    /// <summary>
    /// Tells the consumer to draw a tappable picture instead of a text box.
    /// This is the last hop before munni, and the only field on this view that
    /// changes what the human is shown rather than what they are told.
    /// </summary>
    public ChallengeAnswerKind AnswerKind { get; init; } = ChallengeAnswerKind.Text;

    public string? PromptKey { get; init; }

    /// <summary>Authenticated, and only while the challenge is live.</summary>
    public string? ImageUrl { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public string? Code { get; init; }

    public string? Delivery { get; init; }

    public int? Length { get; init; }

    public IReadOnlyList<ChallengeOption>? Options { get; init; }

    public string? Url { get; init; }

    public string? ReturnPattern { get; init; }

    public static ChallengeView From(ChallengeRow row, string providerId, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(row);
        var payload = ConnectorJson.DeserializeOr(row.PayloadJson, new ChallengePayload());

        return new ChallengeView
        {
            Id = row.Id,
            Type = row.Type,
            AnswerKind = payload.AnswerKind,
            PromptKey = payload.PromptKey,
            ImageUrl = row.ImageBytes is null
                ? null
                : $"/v1/{providerId}/login/{sessionId}/challenges/{row.Id}/image",
            ExpiresAt = row.ExpiresAt,
            Code = payload.Code,
            Delivery = payload.Delivery,
            Length = payload.Length,
            Options = payload.Options,
            Url = payload.Url,
            ReturnPattern = payload.ReturnPattern,
        };
    }
}

/// <summary>
/// Typed progress, never prose. A free-text status cannot be translated,
/// cannot drive a progress bar, and becomes a de-facto API the moment a
/// consumer string-matches it.
/// </summary>
public sealed record ProgressView
{
    public required JobStep Step { get; init; }

    public required IReadOnlyList<JobStep> StepsDone { get; init; }

    public static ProgressView From(JobRow job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return new ProgressView
        {
            Step = job.Step,
            StepsDone = ConnectorJson.DeserializeOr<IReadOnlyList<JobStep>>(job.StepsDoneJson, []),
        };
    }
}

public sealed record AnswerRequest
{
    public required string ChallengeId { get; init; }

    /// <summary>Empty for a passive challenge: the human acted somewhere we cannot observe.</summary>
    public string Value { get; init; } = string.Empty;
}

public sealed record ResumeRequest
{
    public required string Subject { get; init; }

    public required string Bundle { get; init; }
}

/// <summary>
/// The optional body of <c>DELETE /v1/{provider}/sessions/{id}</c>.
///
/// Optional because disconnecting must always succeed: a caller that has lost
/// its bundle, or never held one, is still entitled to remove the connection.
/// Supplying it is what makes an upstream logout possible at all - custody is
/// the user's device, so the only copy of the credential is the one they hand
/// back here, and a provider declaring <c>logout: none</c> ignores it entirely.
/// </summary>
public sealed record DisconnectRequest
{
    public string? Bundle { get; init; }
}

public sealed record ResumeResponse
{
    public required string Ticket { get; init; }

    public required string SessionId { get; init; }

    public required int ExpiresIn { get; init; }

    public required SessionState State { get; init; }
}

public sealed record OneShotFetchRequest
{
    public required string Subject { get; init; }

    public required string Bundle { get; init; }

    /// <summary>Same vocabulary as the query string, validated against the same ParamSpec.</summary>
    public IReadOnlyDictionary<string, JsonNode?> Params { get; init; } =
        new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
}

public sealed record DataResponse
{
    public required string Resource { get; init; }

    public required JsonArray Data { get; init; }

    public string? Cursor { get; init; }

    public required bool Complete { get; init; }

    public SessionBundle? Session { get; init; }
}

public sealed record SessionBundle
{
    public required string Bundle { get; init; }

    public required bool Rotated { get; init; }
}

public sealed record JobAcceptedResponse
{
    public required string JobId { get; init; }

    public required JobState State { get; init; }

    public string? Poll { get; init; }

    public string? Events { get; init; }

    public ChallengeView? Challenge { get; init; }

    public ProgressView? Progress { get; init; }
}

public sealed record JobResponse
{
    public required string JobId { get; init; }

    public required string SessionId { get; init; }

    public required JobState State { get; init; }

    public string? Resource { get; init; }

    public required ProgressView Progress { get; init; }

    public ChallengeView? Challenge { get; init; }

    public JsonArray? Data { get; init; }

    public string? Cursor { get; init; }

    /// <summary>False when more remains beyond this pass.</summary>
    public bool Complete { get; init; } = true;

    public SessionBundle? Session { get; init; }

    public ConnectorError? Error { get; init; }
}

public sealed record AckRequest
{
    public required string Cursor { get; init; }
}

public sealed record AgentEnrollmentRequest
{
    public required string Subject { get; init; }

    public required string Name { get; init; }
}

public sealed record AgentEnrollmentResponse
{
    public required string Code { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }
}

public sealed record AgentView
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required AgentClass Class { get; init; }

    public required bool Revoked { get; init; }

    public required DateTimeOffset LastHeartbeatAt { get; init; }

    public required bool Online { get; init; }

    public required IReadOnlyList<ProfileView> Profiles { get; init; }
}

public sealed record ProfileView
{
    public required string Id { get; init; }

    public required string Provider { get; init; }

    public string? SessionId { get; init; }

    public required bool Healthy { get; init; }

    public DateTimeOffset? LastOkAt { get; init; }
}

public sealed record AgentListResponse
{
    public required IReadOnlyList<AgentView> Agents { get; init; }
}

public sealed record HealthResponse
{
    public required string Status { get; init; }

    public required string Version { get; init; }
}

public sealed record StatusResponse
{
    public required ServiceDescriptor Service { get; init; }

    public required IReadOnlyList<ProviderStatus> Providers { get; init; }

    public required AgentPoolView Agents { get; init; }

    public required QueueView Queue { get; init; }
}

public sealed record AgentPoolView
{
    public required int Total { get; init; }

    public required int Online { get; init; }

    public required int Revoked { get; init; }
}

public sealed record QueueView
{
    public required int Queued { get; init; }

    public required int Running { get; init; }

    public required int AwaitingInput { get; init; }
}

public sealed record ProviderStatusUpdate
{
    public required ProviderState State { get; init; }

    public string? ReasonKey { get; init; }
}

/// <summary>
/// The agent's challenge upload. The image arrives base64 rather than as a
/// multipart part: an agent posts JSON everywhere else, and one encoding for
/// the whole protocol is worth the third of a byte.
/// </summary>
public sealed record AgentChallengeRequest
{
    public required ChallengeType Type { get; init; }

    /// <summary>
    /// Must mirror <see cref="Connector.Kit.AgentProtocol.RaiseChallengeRequest"/>
    /// field for field - that record is what the agent SENDS, this one is what
    /// the server BINDS, and nothing but review keeps the two in step.
    /// </summary>
    public ChallengeAnswerKind AnswerKind { get; init; } = ChallengeAnswerKind.Text;

    public string? PromptKey { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public string? Code { get; init; }

    public string? Delivery { get; init; }

    public int? Length { get; init; }

    public IReadOnlyList<ChallengeOption>? Options { get; init; }

    public string? Url { get; init; }

    public string? ReturnPattern { get; init; }

    [JsonPropertyName("image_base64")]
    public string? ImageBase64 { get; init; }
}

public sealed record RenewResponse
{
    public required DateTimeOffset LeaseExpiresAt { get; init; }
}
