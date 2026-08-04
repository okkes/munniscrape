using System.Globalization;
using Connector.Kit;
using Connector.Kit.Adapters;
using Connector.Kit.Challenges;
using Connector.Kit.Errors;
using Connector.Kit.Jobs;
using Connector.Kit.Manifests;
using Connector.Kit.Normalization;
using Connector.Kit.Security;

namespace RegistryConnector.Adapters.Mock;

/// <summary>
/// The offline backbone for the registry service. No network, no browser, no
/// account.
///
/// This exists before BKR does and stays useful afterwards, because it is the
/// only way to exercise the shape a credit registry actually has - a stored
/// password plus a fresh authenticator code on EVERY sync - end to end. That
/// combination is new to this platform: every provider before it either syncs
/// unattended or needs nobody after the first login, and this one needs six
/// digits from a human every single time.
/// </summary>
public static class MockRegistryAdapters
{
    public const string Simple = "mock-registry-simple";
    public const string TwoFactor = "mock-registry-2fa";

    /// <summary>The code the fixture accepts. Anything else is refused.</summary>
    public const string AcceptedCode = "314159";

    public static IReadOnlyList<IProviderAdapter> All(TimeProvider? time = null) =>
    [
        new MockRegistryAdapter(Simple, "Mock registry", twoFactor: false, time),
        new MockRegistryAdapter(TwoFactor, "Mock registry (2FA every sync)", twoFactor: true, time),
    ];
}

public sealed class MockRegistryAdapter : IProviderAdapter
{
    public const string CreditsResource = "credits";

    private readonly string _id;
    private readonly bool _twoFactor;
    private readonly ProviderManifest _manifest;
    private readonly TimeProvider _time;

    public MockRegistryAdapter(string id, string name, bool twoFactor, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        _id = id;
        _twoFactor = twoFactor;
        _time = time ?? TimeProvider.System;
        _manifest = MockRegistryManifest.Build(id, name, twoFactor);
    }

    public ProviderManifest Describe() => _manifest;

    public async Task<LoginResult> LoginAsync(IJobContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        ctx.Progress(JobStep.Authenticating);

        var username = Required(ctx, "username");
        _ = Required(ctx, "password");

        ctx.CredentialSubmitted();

        if (_twoFactor) await AnswerCodeAsync(ctx, ct).ConfigureAwait(false);

        ctx.Progress(JobStep.Finalizing);

        return new LoginResult
        {
            Material = new SessionMaterial { AccessToken = $"mock-registry-token-{username}" },
            Account = new ProviderAccount { DisplayName = _manifest.Name, ExternalId = "81426000" },
            ExpiresAt = _time.GetUtcNow().AddSeconds(MockRegistryManifest.SessionTtlSeconds),
        };
    }

    /// <summary>
    /// The part worth having offline: a fetch that stops to ask for six
    /// digits.
    ///
    /// A registry that re-authenticates per sync exercises a path nothing else
    /// on this platform does - the consumer has to render a challenge during a
    /// FETCH rather than only during a connect, and a scheduled sync has to be
    /// refused outright rather than silently failing at 3am.
    /// </summary>
    public async Task<FetchResult> FetchAsync(IJobContext ctx, ResourceRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(request);

        if (_twoFactor) await AnswerCodeAsync(ctx, ct).ConfigureAwait(false);

        ctx.Progress(JobStep.Downloading);
        ctx.Progress(JobStep.Normalizing);

        return new FetchResult { Registrations = [.. Credits(ctx.SessionId)], Via = "fixture" };
    }

    private async Task AnswerCodeAsync(IJobContext ctx, CancellationToken ct)
    {
        ctx.Progress(JobStep.AwaitingHuman);

        var answer = await ctx.AskAsync(
            new Challenge
            {
                Type = ChallengeType.MfaCode,
                PromptKey = "connect.challenge.authenticator_code",
                Length = 6,
                ExpiresAt = _time.GetUtcNow().AddMinutes(5),
            },
            ct).ConfigureAwait(false);

        if (!string.Equals(answer.Value?.Trim(), MockRegistryAdapters.AcceptedCode, StringComparison.Ordinal))
        {
            // A wrong code is not a wrong password: the credential is fine and
            // the person simply mistyped six digits or let them expire.
            // Telling them to reset a password would send them somewhere that
            // cannot help.
            throw new ConnectorException(
                ErrorCode.MfaFailed,
                $"{_id}: the fixture accepts only {MockRegistryAdapters.AcceptedCode}");
        }
    }

    /// <summary>
    /// Deterministic given the session, so a test can assert on ids and
    /// content hashes rather than on shapes. The four kinds are the ones a
    /// Dutch credit register actually distinguishes.
    /// </summary>
    private static IEnumerable<CreditRegistration> Credits(string sessionId)
    {
        yield return Credit(sessionId, "MOCK-0001", "Mock Bank N.V.", CreditKind.Revolving,
            200_000, CreditStatus.Running, "Doorlopend krediet", null, null);

        yield return Credit(sessionId, "MOCK-0002", "Mock Telecom B.V.", CreditKind.DeferredPayment,
            99_600, CreditStatus.Running, "Verzendhuiskrediet",
            new DateOnly(2026, 1, 15), new DateOnly(2028, 1, 15));

        // The one that matters most to whoever reads it. Carried verbatim and
        // never interpreted: what an A2 means for somebody's mortgage
        // application is not a connector's judgement to make.
        yield return Credit(sessionId, "MOCK-0003", "Mock Finance B.V.", CreditKind.Instalment,
            530_400, CreditStatus.Ended, "Aflopend krediet",
            new DateOnly(2023, 3, 1), new DateOnly(2025, 9, 1), arrears: "A2");
    }

    private static CreditRegistration Credit(
        string sessionId, string externalId, string creditor, CreditKind kind, long minor,
        CreditStatus status, string label, DateOnly? from, DateOnly? to, string? arrears = null) =>
        new()
        {
            Id = Ids.ForRecord(Ids.CreditRegistration, sessionId, externalId),
            ExternalId = externalId,
            Creditor = creditor,
            Kind = kind,
            KindLabel = label,
            Amount = new Money(minor, "EUR"),
            Status = status,
            StartedOn = from,
            EndsOn = to,
            ArrearsCode = arrears,
        };

    private static string Required(IJobContext ctx, string key) =>
        ctx.Inputs.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw ConnectorException.InvalidRequest(
                string.Create(CultureInfo.InvariantCulture, $"mock-registry: '{key}' is required"));
}
