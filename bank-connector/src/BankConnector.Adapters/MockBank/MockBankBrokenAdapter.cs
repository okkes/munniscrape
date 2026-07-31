using Connector.Kit.Adapters;
using Connector.Kit.Errors;
using Connector.Kit.Jobs;
using Connector.Kit.Manifests;
using Connector.Kit.Security;

namespace BankConnector.Adapters.MockBank;

/// <summary>
/// The provider that is always broken.
///
/// It exists to exercise the operator alert path:
/// <see cref="ErrorCode.ProviderChanged"/> is non-retriable, pages an
/// operator, and flips the provider's status to degraded so the next user
/// gets a real message instead of a mystery spinner. A scraper fleet
/// without that signal is unmaintainable, so the signal itself needs a
/// provider that produces it on demand.
///
/// T1/inline on purpose: the alert path can then be tested in the control
/// plane alone, with no agent and no browser image in the loop.
/// </summary>
public sealed class MockBankBrokenAdapter : MockBankAdapter
{
    public const string ProviderId = "mock-bank-broken";

    private const int TtlSeconds = 3_600;

    private static readonly ProviderManifest Instance = new()
    {
        Id = ProviderId,
        Name = "Mock Bank (broken)",
        Kind = ProviderKind.Bank,
        Country = "NL",
        ManifestVersion = 1,
        Runtime = ProviderRuntime.Http,
        Agent = AgentRequirement.Inline,
        UnattendedFetch = false,
        SecretCustody = SecretCustody.Client,
        WebSupport = WebSupport.Ephemeral,
        LogoRef = MockBankManifests.LogoRef,
        NotesKey = "connect.mock_bank.notes",
        Auth = new AuthSpec
        {
            Flow = AuthFlow.Password,
            Steps = [MockBankManifests.Credentials],
            Challenges = [],
            Session = new SessionSpec { TtlSeconds = TtlSeconds, Refreshable = false, RotatesOnUse = false },
            Reauth = new ReauthSpec { Cheap = false },
        },
        Resources = MockBankManifests.Resources(typicalDurationSeconds: 2),
        Limits = MockBankManifests.Limits(),
    };

    public MockBankBrokenAdapter(TimeProvider? time = null) : base(time)
    {
    }

    /// <summary>
    /// The deliberately inconsistent fixture. The fetch failure below is
    /// therefore produced by the real verifier on real data rather than by a
    /// hand-thrown exception - which is what makes it a test of the chain
    /// check as well as of the alert path.
    /// </summary>
    protected override MockBankLedger Ledger => MockBankLedger.BrokenChain;

    public override ProviderManifest Describe() => Instance;

    public override Task<LoginResult> LoginAsync(IJobContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ct.ThrowIfCancellationRequested();

        ctx.Progress(JobStep.OpeningProvider);

        // No credential is ever submitted: the shape moved before we got as
        // far as a password, which is also the honest ordering for a real
        // adapter whose login selector vanished.
        throw ConnectorException.ProviderChanged(
            $"{ProviderId}: login form selector '#logon-form' is no longer present");
    }

    public override Task<FetchResult> FetchAsync(IJobContext ctx, ResourceRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ct.ThrowIfCancellationRequested();

        // Serve runs the broken ledger through BalanceChain.Verify, which
        // raises provider_changed naming the offending external id. The
        // throw below only covers the accounts resource, which has no chain
        // to break.
        var result = Serve(ctx, request);

        throw ConnectorException.ProviderChanged(
            $"{ProviderId}: served {result.Count} records whose shape no longer matches the adapter");
    }
}
