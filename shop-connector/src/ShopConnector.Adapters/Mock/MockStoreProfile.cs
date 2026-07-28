namespace ShopConnector.Adapters.Mock;

/// <summary>
/// Which part of the protocol a mock provider exercises. One behaviour per
/// hard thing, so a failure names the thing that broke.
/// </summary>
public enum MockStoreBehaviour
{
    /// <summary>T1, no challenge, instant. The smoke test.</summary>
    Instant,

    /// <summary>Relays an <c>mfa_code</c> challenge.</summary>
    SmsChallenge,

    /// <summary>Relays an <c>image</c> challenge with real PNG bytes.</summary>
    ImageChallenge,

    /// <summary>Runs long enough to exercise SSE progress and lease renewal.</summary>
    Slow,

    /// <summary>Always fails a fetch with <c>provider_changed</c>.</summary>
    Broken,

    /// <summary>T4 on a BYO agent: the bundle holds a pointer and no secret.</summary>
    Persistent,
}

/// <summary>
/// One mock provider's identity and behaviour. Everything that could vary
/// per test lives here so the registered set stays realistic while a test
/// can construct a fast one.
/// </summary>
public sealed record MockStoreProfile
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required MockStoreBehaviour Behaviour { get; init; }

    public string FixtureName { get; init; } = "mock/receipts.json";

    /// <summary>Pause between progress steps. Only the slow profile uses it.</summary>
    public TimeSpan StepDelay { get; init; } = TimeSpan.Zero;

    /// <summary>The only answer the SMS mock accepts. Anything else is <c>mfa_failed</c>.</summary>
    public string SmsCode { get; init; } = "123456";

    /// <summary>The only answer the image mock accepts, compared case-insensitively.</summary>
    public string CaptchaAnswer { get; init; } = "MOCK1";

    /// <summary>
    /// The password that makes a mock login fail with
    /// <c>invalid_credentials</c>. It exists so the rule that matters most -
    /// a credential failure is never retried by anything - has an offline
    /// test that needs no real account.
    /// </summary>
    public string RejectedPassword { get; init; } = "wrong";
}

/// <summary>Host-bindable settings for the whole mock set.</summary>
public sealed record MockStoreOptions
{
    public TimeSpan SlowStepDelay { get; init; } = TimeSpan.FromSeconds(2);

    public string SmsCode { get; init; } = "123456";

    public string CaptchaAnswer { get; init; } = "MOCK1";

    public string RejectedPassword { get; init; } = "wrong";
}
