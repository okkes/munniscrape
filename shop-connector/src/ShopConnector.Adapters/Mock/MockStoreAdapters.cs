using Connector.Kit.Adapters;

namespace ShopConnector.Adapters.Mock;

/// <summary>
/// The registered mock providers. Named constants rather than literals
/// because these ids appear in test assertions, seeded fixtures and stack
/// configuration, and a typo in any of them is a silent "unknown provider".
/// </summary>
public static class MockStoreAdapters
{
    public const string Simple = "mock-store-simple";
    public const string Sms = "mock-store-sms";
    public const string Captcha = "mock-store-captcha";
    public const string Slow = "mock-store-slow";
    public const string Broken = "mock-store-broken";
    public const string Persistent = "mock-store-persistent";

    public static IReadOnlyList<string> Ids { get; } = [Simple, Sms, Captcha, Slow, Broken, Persistent];

    public static IReadOnlyList<IProviderAdapter> All(MockStoreOptions? options = null, TimeProvider? time = null) =>
        [.. Profiles(options).Select(p => new MockStoreAdapter(p, time))];

    public static MockStoreAdapter Create(string id, MockStoreOptions? options = null, TimeProvider? time = null)
    {
        var profile = Profiles(options).FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal))
                      ?? throw new ArgumentOutOfRangeException(nameof(id), id, "no such mock provider");

        return new MockStoreAdapter(profile, time);
    }

    public static IReadOnlyList<MockStoreProfile> Profiles(MockStoreOptions? options = null)
    {
        var settings = options ?? new MockStoreOptions();

        return
        [
            Profile(Simple, "Mock Store", MockStoreBehaviour.Instant, settings),
            Profile(Sms, "Mock Store (SMS)", MockStoreBehaviour.SmsChallenge, settings),
            Profile(Captcha, "Mock Store (captcha)", MockStoreBehaviour.ImageChallenge, settings),
            Profile(Slow, "Mock Store (slow)", MockStoreBehaviour.Slow, settings) with
            {
                StepDelay = settings.SlowStepDelay,
            },
            Profile(Broken, "Mock Store (broken)", MockStoreBehaviour.Broken, settings),
            Profile(Persistent, "Mock Store (always-on)", MockStoreBehaviour.Persistent, settings),
        ];
    }

    private static MockStoreProfile Profile(
        string id, string name, MockStoreBehaviour behaviour, MockStoreOptions settings) => new()
    {
        Id = id,
        Name = name,
        Behaviour = behaviour,
        SmsCode = settings.SmsCode,
        CaptchaAnswer = settings.CaptchaAnswer,
        RejectedPassword = settings.RejectedPassword,
    };
}
