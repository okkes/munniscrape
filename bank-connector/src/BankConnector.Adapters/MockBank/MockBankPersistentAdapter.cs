using Connector.Kit;
using Connector.Kit.Adapters;
using Connector.Kit.Challenges;
using Connector.Kit.Errors;
using Connector.Kit.Jobs;
using Connector.Kit.Manifests;
using Connector.Kit.Security;

namespace BankConnector.Adapters.MockBank;

/// <summary>
/// T4: the bring-your-own agent case, and the strongest privacy story in
/// the platform.
///
/// The human authenticates once, ever, into a persistent browser profile on
/// hardware they own. The sealed bundle that comes back contains no secret
/// at all - only <c>{agent_id, profile_id}</c> - so the connector never
/// holds a credential for this provider and a breach of the connector
/// yields nothing. Jobs route exclusively to that agent; if it is off, the
/// honest answer is <see cref="ErrorCode.AgentUnavailable"/> with
/// <see cref="UserAction.StartYourAgent"/>, not a retry loop.
/// </summary>
public sealed class MockBankPersistentAdapter : MockBankAdapter
{
    public const string ProviderId = "mock-bank-persistent";

    /// <summary>Which enrolled agent holds the profile. Required at connect time.</summary>
    public const string AgentIdKey = "agent_id";

    private const int TtlSeconds = 31_536_000;   // a year

    private static readonly ProviderManifest Instance = new()
    {
        Id = ProviderId,
        Name = "Mock Bank (always-on)",
        Kind = ProviderKind.Bank,
        Country = "NL",
        ManifestVersion = 1,
        Runtime = ProviderRuntime.BrowserPersistent,
        Agent = new AgentRequirement { Required = true, Class = AgentClass.Byo },
        // The payoff: no human in the loop after the first authentication.
        UnattendedFetch = true,
        SecretCustody = SecretCustody.Agent,
        // A bundle holding only a pointer is safe anywhere, so a web client
        // with a BYO agent gets a fully persistent connection - strictly
        // better than a native client without one.
        WebSupport = WebSupport.Ephemeral,
        LogoRef = MockBankManifests.LogoRef,
        NotesKey = "connect.mock_bank.persistent_notes",
        Auth = new AuthSpec
        {
            Flow = AuthFlow.DevicePersistent,
            Steps =
            [
                new AuthStep
                {
                    Id = "agent",
                    LabelKey = "connect.mock_bank.step.pick_agent",
                    Fields =
                    [
                        new FieldSpec
                        {
                            Key = AgentIdKey,
                            Type = FieldType.Text,
                            Secret = false,
                            LabelKey = "connect.mock_bank.field.agent_id",
                            Pattern = "^agt_[0-9a-f]{32}$",
                        },
                    ],
                },
            ],
            // Raised once, at enrollment, and never again.
            Challenges = [ChallengeType.QrDisplay],
            Session = new SessionSpec { TtlSeconds = TtlSeconds, Refreshable = true, RotatesOnUse = false },
            Reauth = new ReauthSpec { Cheap = true },
        },
        Resources = MockBankManifests.Resources(typicalDurationSeconds: 20),
        Limits = MockBankManifests.Limits(),
    };

    public MockBankPersistentAdapter(TimeProvider? time = null) : base(time)
    {
    }

    public override ProviderManifest Describe() => Instance;

    public override Task<LoginResult> LoginAsync(IJobContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ct.ThrowIfCancellationRequested();

        var agentId = RequireInput(ctx, AgentIdKey);
        if (!Ids.Is(agentId, Ids.Agent))
        {
            throw ConnectorException.InvalidRequest($"'{AgentIdKey}' must be an agent id");
        }

        ctx.Progress(JobStep.OpeningProvider);

        // Note what is absent: no CredentialSubmitted call. Nothing is
        // submitted upstream here - the human already authenticated into the
        // profile on their own machine, and this job only records which
        // profile that was.
        ctx.Progress(JobStep.Finalizing);

        return Task.FromResult(Connected(ctx, SessionMaterial.ForAgent(agentId, Ids.New(Ids.Profile)), TtlSeconds));
    }

    public override Task<FetchResult> FetchAsync(IJobContext ctx, ResourceRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ct.ThrowIfCancellationRequested();

        if (ctx.Material?.AgentId is null || ctx.Material.ProfileId is null)
        {
            throw new ConnectorException(
                ErrorCode.AgentUnavailable,
                $"{ProviderId}: this session's material names no agent and profile, so no host holds its login");
        }

        ctx.Progress(JobStep.OpeningProvider);
        return Task.FromResult(Serve(ctx, request));
    }
}
