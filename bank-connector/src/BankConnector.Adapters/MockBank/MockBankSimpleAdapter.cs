using Connector.Kit;
using Connector.Kit.Adapters;
using Connector.Kit.Errors;
using Connector.Kit.Jobs;
using Connector.Kit.Manifests;
using Connector.Kit.Security;

namespace BankConnector.Adapters.MockBank;

/// <summary>
/// T1: password in, session out, no browser and no human.
///
/// This is munni's demo identity, and the only provider in the fleet that
/// runs inline in the control plane. That combination is deliberate: it
/// means the full connect-fetch-ack loop can be demonstrated on a machine
/// with no agent, no browser binaries and no network at all.
/// </summary>
public sealed class MockBankSimpleAdapter : MockBankAdapter
{
    public const string ProviderId = "mock-bank-simple";

    private const int TtlSeconds = 2_592_000;   // 30 days

    private static readonly ProviderManifest Instance = new()
    {
        Id = ProviderId,
        Name = "Mock Bank (simple)",
        Kind = ProviderKind.Bank,
        Country = "NL",
        ManifestVersion = 1,
        Runtime = ProviderRuntime.Http,
        Agent = AgentRequirement.Inline,
        Unattended = true,
        SecretCustody = SecretCustody.Client,
        WebSupport = WebSupport.Ephemeral,
        LogoRef = MockBankManifests.LogoRef,
        NotesKey = "connect.mock_bank.notes",
        Auth = new AuthSpec
        {
            Flow = AuthFlow.Password,
            Steps = [MockBankManifests.Credentials],
            Challenges = [],
            Session = new SessionSpec { TtlSeconds = TtlSeconds, Refreshable = true, RotatesOnUse = true },
            Reauth = new ReauthSpec { Cheap = true, TriggerCodes = ["session_expired"] },
        },
        Resources = MockBankManifests.Resources(typicalDurationSeconds: 2),
        Limits = MockBankManifests.Limits(),
    };

    public MockBankSimpleAdapter(TimeProvider? time = null) : base(time)
    {
    }

    public override ProviderManifest Describe() => Instance;

    public override Task<LoginResult> LoginAsync(IJobContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ct.ThrowIfCancellationRequested();

        ctx.Progress(JobStep.OpeningProvider);
        _ = RequireInput(ctx, "username");
        var password = RequireInput(ctx, "password");

        ctx.Progress(JobStep.Authenticating);

        // Latched before the verdict, not after. Once a credential has gone
        // upstream a lost lease must fail the job permanently rather than
        // requeue it, and that is true whether or not the attempt succeeded.
        ctx.CredentialSubmitted();

        if (string.Equals(password, MockBankCredentials.RejectedPassword, StringComparison.Ordinal))
        {
            throw ConnectorException.InvalidCredentials($"{ProviderId}: fixture rejects this password");
        }

        ctx.Progress(JobStep.Finalizing);

        return Task.FromResult(Connected(ctx, new SessionMaterial
        {
            AccessToken = Ids.New("mockat"),
            RefreshToken = Ids.New("mockrt"),
            AccessTokenExpiresAt = Time.GetUtcNow().AddHours(1),
            // Minted once and carried for the life of the session: rotating
            // a device identifier every run is itself a fraud signal.
            DeviceId = Ids.New(Ids.Profile),
        }, TtlSeconds));
    }
}
