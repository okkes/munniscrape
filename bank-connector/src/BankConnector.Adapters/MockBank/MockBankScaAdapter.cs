using Connector.Kit.Adapters;
using Connector.Kit.Challenges;
using Connector.Kit.Errors;
using Connector.Kit.Jobs;
using Connector.Kit.Manifests;
using Connector.Kit.Security;

namespace BankConnector.Adapters.MockBank;

/// <summary>
/// T3: strong authentication on every login, in two challenges.
///
/// First a <see cref="ChallengeType.CodeDisplay"/> - the one case where the
/// code travels OUTWARD, from us to the human, who keys it into a device
/// and reads back what it returns. Then a passive
/// <see cref="ChallengeType.AppApproval"/>, where nothing is typed and we
/// simply wait for the human to act somewhere we cannot observe. Between
/// them they cover both halves of the challenge protocol.
/// </summary>
public sealed class MockBankScaAdapter : MockBankAdapter
{
    public const string ProviderId = "mock-bank-sca";

    private const int TtlSeconds = 604_800;   // 7 days of session reuse

    private static readonly ProviderManifest Instance = new()
    {
        Id = ProviderId,
        Name = "Mock Bank (SCA)",
        Kind = ProviderKind.Bank,
        Country = "NL",
        ManifestVersion = 1,
        Runtime = ProviderRuntime.BrowserInteractive,
        Agent = MockBankManifests.ResidentialPooled,
        // Strong authentication every login means a human is needed by
        // definition, which in turn is why custody is client and the vault
        // is not offered: storing a secret that cannot be used without a
        // human present is pure risk for zero feature.
        UnattendedFetch = false,
        SecretCustody = SecretCustody.Client,
        WebSupport = WebSupport.Ephemeral,
        LogoRef = MockBankManifests.LogoRef,
        NotesKey = "connect.mock_bank.notes",
        Auth = new AuthSpec
        {
            Flow = AuthFlow.ChallengeResponse,
            Steps = [MockBankManifests.Credentials],
            Challenges = [ChallengeType.CodeDisplay, ChallengeType.AppApproval],
            Session = new SessionSpec { TtlSeconds = TtlSeconds, Refreshable = false, RotatesOnUse = true },
            Reauth = new ReauthSpec { Cheap = false, TriggerCodes = ["session_expired"] },
        },
        Resources = MockBankManifests.Resources(typicalDurationSeconds: 45),
        Limits = MockBankManifests.Limits(),
    };

    public MockBankScaAdapter(TimeProvider? time = null) : base(time)
    {
    }

    public override ProviderManifest Describe() => Instance;

    public override async Task<LoginResult> LoginAsync(IJobContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        ctx.Progress(JobStep.OpeningProvider);
        _ = RequireInput(ctx, "username");
        var password = RequireInput(ctx, "password");

        ctx.Progress(JobStep.Authenticating);
        ctx.CredentialSubmitted();

        if (string.Equals(password, MockBankCredentials.RejectedPassword, StringComparison.Ordinal))
        {
            throw ConnectorException.InvalidCredentials($"{ProviderId}: fixture rejects this password");
        }

        await AuthenticateStronglyAsync(ctx, ct).ConfigureAwait(false);

        ctx.Progress(JobStep.Finalizing);

        return Connected(ctx, new SessionMaterial { StorageState = MockBankCredentials.StorageState }, TtlSeconds);
    }

    /// <summary>
    /// A T3 provider re-authenticates whenever its session is stale, so a
    /// fetch can stop to ask a question too. It only does so when the
    /// material carries no browser session: reusing the stored storage state
    /// is what turns T3 into T2-in-practice for most of a session's life,
    /// and skipping the challenge when it is present is the point.
    /// </summary>
    public override async Task<FetchResult> FetchAsync(IJobContext ctx, ResourceRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        ctx.Progress(JobStep.OpeningProvider);

        if (string.IsNullOrEmpty(ctx.Material?.StorageState))
        {
            ctx.Progress(JobStep.Authenticating);
            await AuthenticateStronglyAsync(ctx, ct).ConfigureAwait(false);
        }

        ctx.Progress(JobStep.Downloading);
        return Serve(ctx, request);
    }

    private async Task AuthenticateStronglyAsync(IJobContext ctx, CancellationToken ct)
    {
        ctx.Progress(JobStep.AwaitingHuman);

        var response = await ctx.AskAsync(new Challenge
        {
            Type = ChallengeType.CodeDisplay,
            PromptKey = "connect.mock_bank.challenge.code_display",
            Code = MockBankCredentials.DisplayedCode,
            // Mandatory, and short. A challenge holds a live browser
            // hostage; a stale one has to fail cleanly and release the agent
            // rather than pinning it for the whole job timeout.
            ExpiresAt = Time.GetUtcNow().AddMinutes(3),
        }, ct).ConfigureAwait(false);

        if (!IsResponseCode(response.Value))
        {
            throw new ConnectorException(ErrorCode.MfaFailed, $"{ProviderId}: expected a six-digit device response");
        }

        ctx.Progress(JobStep.AwaitingHuman);

        // Passive: the human approves in an app and there is nothing to
        // type, so the answer's value is empty by design.
        _ = await ctx.AskAsync(new Challenge
        {
            Type = ChallengeType.AppApproval,
            PromptKey = "connect.mock_bank.challenge.app_approval",
            ExpiresAt = Time.GetUtcNow().AddMinutes(3),
        }, ct).ConfigureAwait(false);
    }

    private static bool IsResponseCode(string? value) =>
        value is { Length: 6 } && value.All(char.IsAsciiDigit);
}
