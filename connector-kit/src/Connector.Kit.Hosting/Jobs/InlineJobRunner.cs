using Connector.Kit.Adapters;
using Connector.Kit.AgentProtocol;
using Connector.Kit.Errors;
using Connector.Kit.Hosting.Infrastructure;
using Connector.Kit.Jobs;
using Connector.Kit.Manifests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Connector.Kit.Hosting.Jobs;

/// <summary>
/// The in-process agent. It leases through the same queue, renews the same
/// lease and reports through the same outcome path as a remote one - the only
/// difference is that it never has a browser and never leaves the process.
/// </summary>
public sealed class InlineJobRunner(
    IServiceScopeFactory scopes,
    IProviderRegistry registry,
    IHttpClientFactory httpClients,
    ConnectorSignals signals,
    IOptions<ConnectorOptions> options,
    ILogger<InlineJobRunner> logger) : IInlineJobRunner
{
    /// <summary>The lease owner an in-process run claims jobs under.</summary>
    public const string AgentId = "agt_inline";

    /// <summary>The politeness-limited client every adapter is handed.</summary>
    public const string HttpClientName = "connector.provider";

    private readonly ConnectorOptions _options = options.Value;

    public IReadOnlyList<string> Providers { get; } =
    [
        .. registry.Manifests
            .Where(m => m.Agent.Class == AgentClass.Inline && !m.Agent.Required && registry.TryGetAdapter(m.Id, out _))
            .Select(m => m.Id),
    ];

    public bool CanRun(ProviderManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return Providers.Contains(manifest.Id, StringComparer.Ordinal);
    }

    public void Dispatch() => signals.Signal(ConnectorSignals.Queue);

    /// <summary>What the pump advertises when it asks the queue for work.</summary>
    public AgentCapabilities Capabilities => new()
    {
        Providers = Providers,
        Runtimes = [ProviderRuntime.Http],
        Class = AgentClass.Inline,
        MaxConcurrency = Math.Max(1, Environment.ProcessorCount / 2),
    };

    public async Task RunAsync(LeasedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (!registry.TryGetAdapter(job.Provider, out var adapter))
        {
            await ReportFailureAsync(job, ErrorCode.Internal, $"no adapter registered for '{job.Provider}'", ct);
            return;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(job.Limits.TimeoutSeconds));

        await using var context = new InlineJobContext(
            job, scopes, httpClients.CreateClient(HttpClientName), logger, budget.Token);

        using var renewal = StartLeaseRenewal(job.JobId, budget.Token);

        try
        {
            var result = await ExecuteAsync(adapter, context, job, budget.Token);
            await ReportSuccessAsync(job, result, ct);
        }
        catch (ConnectorException ex)
        {
            await ReportFailureAsync(job, ex.Code, ex.Detail, ct);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            await ReportFailureAsync(job, ErrorCode.ProviderUnavailable, "inline job exceeded its time budget", ct);
        }
        catch (Exception ex)
        {
            // Anything escaping an adapter is ours, not theirs: the caller
            // gets `internal` and a correlation id, never a stack trace.
            logger.LogError(ex, "adapter for {Provider} threw an unhandled exception on job {JobId}",
                job.Provider, job.JobId);
            await ReportFailureAsync(job, ErrorCode.Internal, ex.GetType().Name, ct);
        }
    }

    private static async Task<JobResultRequest> ExecuteAsync(
        IProviderAdapter adapter, InlineJobContext context, LeasedJob job, CancellationToken ct)
    {
        switch (job.Kind)
        {
            case JobKind.Login:
            {
                var login = await adapter.LoginAsync(context, ct);
                return new JobResultRequest
                {
                    SessionMaterial = login.Material,
                    ProviderAccount = login.Account,
                    Reachable = login.Reachable,
                    ExpiresAt = login.ExpiresAt,
                };
            }

            case JobKind.Fetch or JobKind.Refresh:
            {
                var request = job.Request
                              ?? throw ConnectorException.InvalidRequest($"{job.Kind} job carries no resource request");
                var fetch = await adapter.FetchAsync(context, request, ct);
                return new JobResultRequest
                {
                    Accounts = fetch.Accounts,
                    Transactions = fetch.Transactions,
                    Receipts = fetch.Receipts,
                    Registrations = fetch.Registrations,
                    SessionMaterial = fetch.RefreshedMaterial,
                    Complete = fetch.Complete,
                    Via = fetch.Via,
                    Raw = fetch.Raw,
                };
            }

            case JobKind.Logout:
                await adapter.LogoutAsync(context, ct);
                return new JobResultRequest();

            default:
                throw ConnectorException.InvalidRequest($"unknown job kind '{job.Kind}'");
        }
    }

    private async Task ReportSuccessAsync(LeasedJob job, JobResultRequest result, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var outcomes = scope.ServiceProvider.GetRequiredService<JobOutcomeService>();
        await outcomes.SucceedAsync(job.JobId, AgentId, result, ct);
    }

    private async Task ReportFailureAsync(LeasedJob job, ErrorCode code, string? detail, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var outcomes = scope.ServiceProvider.GetRequiredService<JobOutcomeService>();
        await outcomes.FailAsync(job.JobId, AgentId, new JobFailRequest
        {
            Code = ErrorCatalog.Wire(code),
            Detail = detail,
        }, ct);
    }

    /// <summary>
    /// An inline run holds its own lease exactly as a remote agent does, so
    /// the expiry sweeper treats a wedged in-process job identically to a dead
    /// machine - including the rule that it never comes back twice.
    /// </summary>
    private CancellationTokenSource StartLeaseRenewal(string jobId, CancellationToken ct)
    {
        var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ttl = TimeSpan.FromSeconds(_options.Timeouts.LeaseSeconds);

        _ = Task.Run(async () =>
        {
            var interval = ttl / 3;
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await Task.Delay(interval, stop.Token);
                    using var scope = scopes.CreateScope();
                    var queue = scope.ServiceProvider.GetRequiredService<ILeasedJobQueue>();
                    if (await queue.RenewLeaseAsync(jobId, AgentId, ttl, stop.Token) is null) return;
                }
            }
            catch (OperationCanceledException)
            {
                // Normal: the job finished.
            }
        }, CancellationToken.None);

        return stop;
    }
}

/// <summary>
/// Pulls inline-eligible work off the same queue a remote agent uses.
///
/// Modelled as a pump rather than "run this job now" on purpose: a login
/// endpoint that ran the adapter on the request thread would tie the run's
/// lifetime to a socket, and the first challenge - which can take a human
/// minutes - would kill it.
/// </summary>
public sealed class InlineJobPump(
    IInlineJobRunner runner,
    IServiceScopeFactory scopes,
    ConnectorSignals signals,
    IOptions<ConnectorOptions> options,
    ILogger<InlineJobPump> logger) : BackgroundService
{
    private static readonly IReadOnlyList<JobKind> AllKinds =
        [JobKind.Login, JobKind.Fetch, JobKind.Refresh, JobKind.Logout];

    private readonly ConnectorOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (runner.Providers.Count == 0) return;

        logger.LogInformation("inline runner serving {Providers}", string.Join(", ", runner.Providers));

        var capabilities = runner.Capabilities;
        // Deliberately not disposed: a job still finishing at shutdown releases
        // its slot, and releasing a disposed semaphore would turn an orderly
        // stop into an exception storm.
        var slots = new SemaphoreSlim(Math.Max(1, capabilities.MaxConcurrency));
        var ttl = TimeSpan.FromSeconds(_options.Timeouts.LeaseSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            await slots.WaitAsync(stoppingToken);

            LeasedJob? job = null;
            try
            {
                using var scope = scopes.CreateScope();
                var queue = scope.ServiceProvider.GetRequiredService<ILeasedJobQueue>();
                // No owner scope: this is the control plane running the job in
                // its own process, not a machine somebody enrolled. It already
                // holds every session's material by definition.
                job = await queue.TryLeaseAsync(
                    InlineJobRunner.AgentId, null, capabilities, AllKinds, ttl, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "inline lease attempt failed");
            }

            if (job is null)
            {
                slots.Release();
                await signals.WaitAsync(ConnectorSignals.Queue, TimeSpan.FromSeconds(2), stoppingToken);
                continue;
            }

            var leased = job;
            _ = Task.Run(async () =>
            {
                try
                {
                    await runner.RunAsync(leased, stoppingToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "inline job {JobId} escaped its own error handling", leased.JobId);
                }
                finally
                {
                    slots.Release();
                    signals.Signal(ConnectorSignals.Queue);
                }
            }, CancellationToken.None);
        }
    }
}
