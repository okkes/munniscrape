using System.Text.Json.Serialization;
using Connector.Kit.Challenges;

namespace Connector.Kit.Manifests;

/// <summary>
/// The login interface, in enough detail that a consumer can render a
/// complete, validated, localised form without knowing anything about the
/// provider.
/// </summary>
public sealed record AuthSpec
{
    public required AuthFlow Flow { get; init; }

    /// <summary>
    /// Non-secret settings the provider needs on every call - Lidl's country
    /// and language, for instance, which appear in its URLs and headers.
    /// Neither credentials nor challenges, so they get their own section;
    /// without it the alternative is a provider-specific hack in the
    /// consuming app, which is what the manifest exists to prevent.
    /// </summary>
    public IReadOnlyList<FieldSpec> Config { get; init; } = [];

    public required IReadOnlyList<AuthStep> Steps { get; init; }

    /// <summary>
    /// What this provider <em>may</em> raise. <see cref="Flow"/> describes
    /// the happy path; a password flow can still hit a CAPTCHA.
    /// </summary>
    public IReadOnlyList<ChallengeType> Challenges { get; init; } = [];

    public required SessionSpec Session { get; init; }

    public ReauthSpec Reauth { get; init; } = new();

    /// <summary>Every field across every step, plus config. Ordered.</summary>
    public IEnumerable<FieldSpec> AllFields() => Config.Concat(Steps.SelectMany(s => s.Fields));
}

/// <summary>
/// The login shapes we have actually encountered. Each implies a different
/// consumer UI and nothing else.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AuthFlow>))]
public enum AuthFlow
{
    /// <summary>One step: username + password.</summary>
    Password,

    /// <summary>Credentials, then a code delivered out of band to a phone.</summary>
    PasswordSms,

    /// <summary>Credentials, then a TOTP code.</summary>
    PasswordTotp,

    /// <summary>Username step, provider responds, then a secondary step.</summary>
    TwoStep,

    /// <summary>Credentials, then wait for approval in the provider's app.</summary>
    MobileApproval,

    /// <summary>Provider shows a number; a device or app turns it into a response code.</summary>
    ChallengeResponse,

    /// <summary>Provider shows a QR; the human scans it with the provider's app.</summary>
    QrScan,

    /// <summary>The human logs in on the provider's own site and hands back the redirect.</summary>
    OauthRedirect,

    /// <summary>Authenticated once into a BYO agent's persistent profile; no login endpoint after.</summary>
    DevicePersistent,

    /// <summary>
    /// The provider's own login page, streamed to the human, driven by them.
    ///
    /// There is no form for a consumer to draw and no credential for one to
    /// collect: the page is rendered in the agent's browser, photographed, and
    /// the human's pointer and keystrokes are replayed into it. They sign in
    /// exactly as they would on their own laptop.
    ///
    /// A provider declaring this MUST declare no fields at all, and the
    /// validator refuses one that does. That refusal is the point - it is what
    /// turns "the credential never enters the platform" from a claim in a
    /// design document into something a manifest cannot contradict. A field
    /// here would mean a form somewhere, and a form means a password crossing
    /// the wire and resting in a job's inputs, which is the entire thing this
    /// flow exists to stop.
    /// </summary>
    RemoteBrowser,
}

public sealed record AuthStep
{
    public required string Id { get; init; }

    public string? LabelKey { get; init; }

    public required IReadOnlyList<FieldSpec> Fields { get; init; }
}

/// <summary>
/// One input. <see cref="LabelKey"/> rather than prose because the consumer
/// owns the copy in every language it ships; a connector never emits
/// user-facing English.
/// </summary>
public sealed record FieldSpec
{
    public required string Key { get; init; }

    public required FieldType Type { get; init; }

    /// <summary>
    /// True when the value must never reach a log, a screenshot or an
    /// artifact. Enforced by the redactor, not by adapter discipline.
    /// </summary>
    public bool Secret { get; init; }

    public bool Required { get; init; } = true;

    public string? LabelKey { get; init; }

    /// <summary>Anchored regex for client-side validation.</summary>
    public string? Pattern { get; init; }

    /// <summary>Choices for <see cref="FieldType.Select"/>.</summary>
    public IReadOnlyList<string>? Options { get; init; }

    /// <summary>HTML autocomplete hint, e.g. <c>username</c>.</summary>
    public string? Autofill { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<FieldType>))]
public enum FieldType
{
    Text,
    Password,
    Number,
    Date,
    Select,
    Iban,
    Phone,
}

public sealed record SessionSpec
{
    /// <summary>How long a bundle stays valid before a full re-login.</summary>
    public required int TtlSeconds { get; init; }

    /// <summary>Whether the session can be renewed without a human.</summary>
    public required bool Refreshable { get; init; }

    /// <summary>
    /// When true, every response may carry a re-issued bundle and the client
    /// must persist the newest.
    /// </summary>
    public bool RotatesOnUse { get; init; } = true;
}

public sealed record ReauthSpec
{
    /// <summary>True when the session can be renewed silently, with no human.</summary>
    public bool Cheap { get; init; }

    public IReadOnlyList<string> TriggerCodes { get; init; } = [];
}
