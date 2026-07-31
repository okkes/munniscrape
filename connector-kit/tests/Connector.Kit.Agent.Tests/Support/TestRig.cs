using Connector.Kit.Adapters;
using Connector.Kit.Agent.Browsing;
using Connector.Kit.Agent.Execution;
using Connector.Kit.Agent.Networking;
using Connector.Kit.Agent.Transport;
using Connector.Kit.AgentProtocol;
using Connector.Kit.Challenges;
using Connector.Kit.Errors;
using Connector.Kit.Jobs;
using Connector.Kit.Manifests;
using Connector.Kit.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace Connector.Kit.Agent.Tests;

/// <summary>
/// Everything a <see cref="JobRunner"/> needs, wired to fakes, plus the
/// scratch directories it insists on owning.
/// </summary>
internal sealed class TestRig : IDisposable
{
    public const string ProviderId = "test-provider";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "connector-agent-tests", Guid.NewGuid().ToString("N"));

    public TestRig(IProviderAdapter adapter, bool headless = true)
    {
        Adapter = adapter;
        Control = new FakeControlPlane();

        Options = new ConnectorAgentOptions
        {
            ControlPlaneBaseUrl = new Uri("https://control-plane.test/"),
            WorkRootDirectory = Path.Combine(_root, "work"),
            ProfileRootDirectory = Path.Combine(_root, "profiles"),
            StateFilePath = Path.Combine(_root, "agent-state.json"),
            Headless = headless,
            // The poll gap is what the parked wait actually spends its time in;
            // the production default of two seconds would make every test that
            // parks slower than the thing it is testing.
            AnswerPollInterval = TimeSpan.FromMilliseconds(20),
        };

        var identity = new AgentIdentity();
        identity.Set(new AgentEnrollment
        {
            AgentId = "agt_test",
            Token = "tok_test",
            ControlPlane = "https://control-plane.test/",
            EnrolledAt = DateTimeOffset.UtcNow,
        });

        Runner = new JobRunner(
            new SingleAdapterRegistry(adapter),
            new ControlPlaneClient(Control, NullLogger<ControlPlaneClient>.Instance),
            new PolitenessGate(),
            new ProfileStore(Options.ProfileRootDirectory, NullLogger<ProfileStore>.Instance),
            identity,
            Options,
            NullLoggerFactory.Instance,
            TimeProvider.System);
    }

    public IProviderAdapter Adapter { get; }

    public FakeControlPlane Control { get; }

    public ConnectorAgentOptions Options { get; }

    public JobRunner Runner { get; }

    /// <summary>
    /// A login job with a budget expressed in whole seconds, as the wire has
    /// it. <paramref name="leaseSeconds"/> drives how often the runner renews:
    /// it renews at half the remaining lease, floored at five seconds.
    /// </summary>
    public static LeasedJob Login(int budgetSeconds, int leaseSeconds = 120) => new()
    {
        JobId = "job_" + Guid.NewGuid().ToString("N")[..16],
        SessionId = "ses_" + Guid.NewGuid().ToString("N"),
        Provider = ProviderId,
        Kind = JobKind.Login,
        LeaseExpiresAt = DateTimeOffset.UtcNow.AddSeconds(leaseSeconds),
        Inputs = new Dictionary<string, string>(StringComparer.Ordinal) { ["password"] = "hunter2" },
        Limits = new JobLimits { TimeoutSeconds = budgetSeconds, PolitenessMs = 0 },
    };

    /// <summary>A context built the way the runner builds one, minus the runner.</summary>
    public AgentJobContext Context(LeasedJob job) => new(
        new JobContextOptions
        {
            Job = job,
            Manifest = Manifest,
            WorkRootDirectory = Options.WorkRootDirectory,
            Browser = new BrowserLeaseOptions { Headless = Options.Headless },
            AnswerPollInterval = Options.AnswerPollInterval,
            HttpTimeout = Options.ProviderHttpTimeout,
        },
        new ControlPlaneClient(Control, NullLogger<ControlPlaneClient>.Instance),
        new PolitenessGate(),
        NullLoggerFactory.Instance,
        TimeProvider.System);

    public Task RunAsync(LeasedJob job, int leaseTtlSeconds = 120, CancellationToken ct = default) =>
        Runner.RunAsync(job, TimeSpan.FromSeconds(leaseTtlSeconds), ct);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory the OS is still holding is not a test failure.
        }
    }

    /// <summary>
    /// The simplest manifest an agent will accept: browser-interactive so the
    /// runner takes the login path, with one secret field so the redactor and
    /// the scrubber both have something to do.
    /// </summary>
    public static ProviderManifest Manifest { get; } = new()
    {
        Id = ProviderId,
        Name = "Test Provider",
        Kind = ProviderKind.Store,
        Country = "NL",
        ManifestVersion = 1,
        Runtime = ProviderRuntime.BrowserInteractive,
        Agent = new AgentRequirement { Required = true, Class = AgentClass.Pooled },
        UnattendedFetch = false,
        SecretCustody = SecretCustody.Client,
        Auth = new AuthSpec
        {
            Flow = AuthFlow.Password,
            Steps =
            [
                new AuthStep
                {
                    Id = "credentials",
                    Fields =
                    [
                        new FieldSpec
                        {
                            Key = "password",
                            Type = FieldType.Password,
                            Secret = true,
                            LabelKey = "connect.field.password",
                        },
                    ],
                },
            ],
            Session = new SessionSpec { TtlSeconds = 3600, Refreshable = false },
        },
        Resources = [new ResourceSpec { Id = "receipts", Returns = ResourceShape.Receipt }],
    };

    private sealed class SingleAdapterRegistry(IProviderAdapter adapter) : IProviderRegistry
    {
        public IReadOnlyList<ProviderManifest> Manifests { get; } = [Manifest];

        public string CatalogDigest => "sha256:test";

        public bool TryGetManifest(string providerId, out ProviderManifest manifest)
        {
            manifest = Manifest;
            return string.Equals(providerId, ProviderId, StringComparison.Ordinal);
        }

        public ProviderManifest RequireManifest(string providerId) =>
            TryGetManifest(providerId, out var m) ? m : throw ConnectorException.Unsupported(providerId);

        public bool TryGetAdapter(string providerId, out IProviderAdapter found)
        {
            found = adapter;
            return string.Equals(providerId, ProviderId, StringComparison.Ordinal);
        }
    }
}

/// <summary>
/// An adapter whose login does exactly one scripted thing, so a test can say
/// "park on a human" or "never come back" without a fixture.
/// </summary>
internal sealed class ScriptedAdapter(Func<IJobContext, CancellationToken, Task> login) : IProviderAdapter
{
    /// <summary>Raises one challenge and returns as soon as it is answered.</summary>
    public static ScriptedAdapter AsksAHuman(TimeSpan window) => new(async (ctx, ct) =>
    {
        await ctx.AskAsync(new Challenge
        {
            Type = ChallengeType.MfaCode,
            PromptKey = "connect.challenge.sms",
            ExpiresAt = DateTimeOffset.UtcNow + window,
        }, ct);
    });

    /// <summary>Raises nothing and never finishes. A wedged adapter, exactly.</summary>
    public static ScriptedAdapter Wedges() => new((_, ct) => Task.Delay(Timeout.Infinite, ct));

    public ProviderManifest Describe() => TestRig.Manifest;

    public async Task<LoginResult> LoginAsync(IJobContext ctx, CancellationToken ct)
    {
        await login(ctx, ct);
        return new LoginResult { Material = new SessionMaterial { StorageState = "{\"cookies\":[],\"origins\":[]}" } };
    }

    public Task<FetchResult> FetchAsync(IJobContext ctx, ResourceRequest request, CancellationToken ct) =>
        Task.FromResult(FetchResult.Empty);
}
