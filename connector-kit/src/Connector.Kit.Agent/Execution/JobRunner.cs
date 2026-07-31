using Connector.Kit.Adapters;
using Connector.Kit.Agent.Browsing;
using Connector.Kit.Agent.Networking;
using Connector.Kit.Agent.Transport;
using Connector.Kit.AgentProtocol;
using Connector.Kit.Errors;
using Connector.Kit.Jobs;
using Connector.Kit.Manifests;
using Connector.Kit.Security;
using Microsoft.Extensions.Logging;

namespace Connector.Kit.Agent.Execution;

/// <summary>
/// Runs one leased job to a terminal state.
///
/// The invariant it exists to hold is that a leased job ALWAYS reaches one:
/// a result, a failure, or a lease the control plane has already taken back.
/// Silently abandoning one leaves a session stuck in <c>running</c> until a
/// TTL notices, and for a login that is a browser sitting open on a provider.
///
/// The second invariant is that the job's budget measures WORK. Waiting on the
/// human who owns the account is not work, is bounded by the challenge's own
/// expiry, and does not count - see <see cref="JobBudget"/>.
/// </summary>
public sealed class JobRunner
{
    private static readonly TimeSpan TerminalPostTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ArtifactTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MinimumRenewInterval = TimeSpan.FromSeconds(5);

    private readonly IProviderRegistry _registry;
    private readonly ControlPlaneClient _control;
    private readonly PolitenessGate _gate;
    private readonly ProfileStore _profiles;
    private readonly AgentIdentity _identity;
    private readonly ConnectorAgentOptions _options;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<JobRunner> _logger;
    private readonly TimeProvider _time;

    public JobRunner(
        IProviderRegistry registry,
        ControlPlaneClient control,
        PolitenessGate gate,
        ProfileStore profiles,
        AgentIdentity identity,
        ConnectorAgentOptions options,
        ILoggerFactory loggers,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggers);
        ArgumentNullException.ThrowIfNull(time);

        _registry = registry;
        _control = control;
        _gate = gate;
        _profiles = profiles;
        _identity = identity;
        _options = options;
        _loggers = loggers;
        _logger = loggers.CreateLogger<JobRunner>();
        _time = time;
    }

    /// <param name="abort">
    /// Cancelled when the agent is shutting down and the drain window has
    /// elapsed. Cancelling it fails the job cleanly upstream rather than
    /// dropping it.
    /// </param>
    public async Task RunAsync(LeasedJob job, TimeSpan leaseTtl, CancellationToken abort)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (!_registry.TryGetManifest(job.Provider, out var manifest) ||
            !_registry.TryGetAdapter(job.Provider, out var adapter))
        {
            // Routed to the wrong agent. Non-retriable, because retrying will
            // not conjure an adapter this image does not contain.
            await PostFailureAsync(job, ErrorCode.UnsupportedResource,
                $"this agent has no adapter for provider '{job.Provider}'", artifacts: null).ConfigureAwait(false);
            return;
        }

        string? profileId;
        try
        {
            profileId = ResolveProfile(job, manifest);
        }
        catch (ConnectorException ex)
        {
            await PostFailureAsync(job, ex.Code, ex.Detail, artifacts: null).ConfigureAwait(false);
            return;
        }

        using var budget = new JobBudget(TimeSpan.FromSeconds(Math.Max(1, job.Limits.TimeoutSeconds)), _time);

        AgentJobContext prepared;
        try
        {
            prepared = new AgentJobContext(
                new JobContextOptions
                {
                    Job = job,
                    Manifest = manifest,
                    WorkRootDirectory = _options.WorkRootDirectory,
                    Browser = BrowserOptionsFor(job, manifest, profileId),
                    AnswerPollInterval = _options.AnswerPollInterval,
                    HttpTimeout = _options.ProviderHttpTimeout,
                    Budget = budget,
                },
                _control,
                _gate,
                _loggers,
                _time);
        }
        catch (Exception ex)
        {
            // An unusable scratch directory or profile path is still a leased
            // job, and a leased job always reaches a terminal state.
            _logger.LogError(ex, "job {JobId}: the run could not be set up", job.JobId);
            await PostFailureAsync(job, ErrorCode.Internal,
                $"job setup failed: {ex.GetType().Name}", artifacts: null).ConfigureAwait(false);
            return;
        }

        using var lease = CancellationTokenSource.CreateLinkedTokenSource(abort, budget.Token);
        var leaseLost = false;

        // One stop signal for both background loops: the lease renewal and the
        // budget watchdog live exactly as long as the job does. Neither is tied
        // to the job's own cancellation - a job parked on a human still has to
        // hold its lease, or the control plane takes it back mid-wait and the
        // patience above buys nothing.
        using var finished = new CancellationTokenSource();
        var renewing = RenewWhileRunningAsync(job, leaseTtl, () => leaseLost = true, lease, finished.Token);
        var watching = budget.WatchAsync(finished.Token);

        await using var context = prepared;

        try
        {
            context.Progress(JobStep.AgentAssigned);

            var result = await ExecuteAsync(adapter, manifest, job, context, profileId, lease.Token).ConfigureAwait(false);

            context.Progress(JobStep.Finalizing);
            await context.FlushProgressAsync().ConfigureAwait(false);

            // Deliberately not inside the failure mapping below. The run
            // succeeded; a post that will not land is a delivery problem, and
            // reporting it as a failed job would throw away real data the
            // adapter already went and got.
            if (await SendAsync(ct => _control.ResultAsync(job.JobId, result, ct), $"result for {job.JobId}")
                    .ConfigureAwait(false))
            {
                if (profileId is not null) _profiles.MarkOk(profileId, job.Provider, _time.GetUtcNow());
                _logger.LogInformation("job {JobId} ({Kind}/{Provider}) succeeded", job.JobId, job.Kind, job.Provider);
            }
            else
            {
                // The control plane's lease TTL is the documented backstop for
                // an agent that goes silent, and it returns the job to the
                // queue exactly once.
                _logger.LogError("job {JobId}: succeeded but the result did not land; leaving it to the lease TTL",
                    job.JobId);
            }
        }
        catch (ConnectorException ex)
        {
            MarkProfile(profileId, job.Provider, ex.Code);
            await FailAsync(job, context, ex.Code, ex.Detail ?? ErrorCatalog.Wire(ex.Code)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (leaseLost)
        {
            // The control plane already took this job back and has decided what
            // happens to it. Posting anything now would race its own bookkeeping.
            _logger.LogWarning("job {JobId}: the lease was lost mid-run; stopping without a terminal post", job.JobId);
        }
        catch (OperationCanceledException) when (abort.IsCancellationRequested)
        {
            await FailAsync(job, context, ErrorCode.AgentUnavailable, "the agent shut down while this job was running")
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (budget.Expired)
        {
            // Deliberately not `internal`. That code means "ours, not theirs",
            // is retriable, and renders as "something went wrong" - it pages an
            // operator over a run nobody can fix and tells the user nothing.
            //
            // A budget that counts only WORK can now only run out ON work, and
            // work that never finishes is overwhelmingly the far end not
            // answering: a page that never loads, a call that never returns.
            // `provider_unavailable` says exactly that - 503, wait, and a retry
            // genuinely may help later, which is true here and is not true of a
            // malformed request or a rejected password. It is also what the
            // inline runner already reports for the same situation.
            await FailAsync(job, context, ErrorCode.ProviderUnavailable,
                    $"the adapter used its whole {job.Limits.TimeoutSeconds}s working budget; " +
                    $"{budget.HumanWait.TotalSeconds:F0}s spent waiting on a human did not count against it")
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Not the budget, not the lease, and not a shutdown: something
            // inside the run cancelled a token it was handed. Ours by
            // elimination.
            await FailAsync(job, context, ErrorCode.Internal, "the run was cancelled without a reason")
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Anything escaping an adapter that is not a ConnectorException is
            // ours by definition, and the caller sees a code rather than a
            // stack trace.
            //
            // Logged as scrubbed TEXT rather than as an exception object: a
            // provider library that echoes its request body into a message is
            // ordinary, and a password in an operator's log aggregator is just
            // as leaked as one on the wire.
            _logger.LogError("job {JobId}: the adapter threw{NewLine}{Error}",
                job.JobId, Environment.NewLine, SecretScrubber.Scrub(ex.ToString(), context.SecretValues));

            await FailAsync(job, context, ErrorCode.Internal, $"{ex.GetType().Name}: {ex.Message}").ConfigureAwait(false);
        }
        finally
        {
            await finished.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(renewing, watching).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: both background loops are stopped by cancellation.
            }
        }
    }

    private async Task<JobResultRequest> ExecuteAsync(
        IProviderAdapter adapter,
        ProviderManifest manifest,
        LeasedJob job,
        AgentJobContext context,
        string? profileId,
        CancellationToken ct)
    {
        switch (job.Kind)
        {
            case JobKind.Login:
            case JobKind.Refresh:
            {
                // A refresh is a login that starts from existing material and
                // no inputs. The adapter distinguishes the two by looking at
                // ctx.Material and ctx.Inputs, which is the only signal the
                // frozen adapter contract offers - and the right one, since a
                // refresh has no credential to submit.
                context.Progress(JobStep.OpeningProvider);
                var login = await adapter.LoginAsync(context, ct).ConfigureAwait(false);

                return new JobResultRequest
                {
                    SessionMaterial = MaterialFor(login.Material, manifest, profileId),
                    ProviderAccount = login.Account,
                    Reachable = login.Reachable,
                    ExpiresAt = login.ExpiresAt,
                    Complete = true,
                };
            }

            case JobKind.Fetch:
            {
                if (job.Request is not { } request)
                {
                    throw ConnectorException.InvalidRequest("a fetch job arrived with no resource request");
                }

                context.Progress(JobStep.OpeningProvider);
                var fetch = await adapter.FetchAsync(context, request, ct).ConfigureAwait(false);

                return new JobResultRequest
                {
                    Accounts = fetch.Accounts,
                    Transactions = fetch.Transactions,
                    Receipts = fetch.Receipts,
                    SessionMaterial = fetch.RefreshedMaterial is null
                        ? null
                        : MaterialFor(fetch.RefreshedMaterial, manifest, profileId),
                    Complete = fetch.Complete,
                    Via = fetch.Via,
                    Raw = fetch.Raw,
                };
            }

            case JobKind.Logout:
            {
                context.Progress(JobStep.LoggingOut);
                try
                {
                    await adapter.LogoutAsync(context, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A user disconnecting must always succeed locally, so an
                    // upstream logout that fails is logged and nothing more.
                    _logger.LogWarning("job {JobId}: upstream logout failed; disconnecting anyway{NewLine}{Error}",
                        job.JobId, Environment.NewLine, SecretScrubber.Scrub(ex.ToString(), context.SecretValues));
                }

                return new JobResultRequest { Complete = true };
            }

            default:
                throw ConnectorException.InvalidRequest($"unknown job kind '{job.Kind}'");
        }
    }

    /// <summary>
    /// For a persistent-profile provider the material is a pointer and nothing
    /// else, and only the agent knows what it points at. Stamping it here means
    /// a T4 adapter never has to know it is on a BYO agent.
    /// </summary>
    private SessionMaterial MaterialFor(SessionMaterial material, ProviderManifest manifest, string? profileId)
    {
        if (manifest.Runtime != ProviderRuntime.BrowserPersistent || profileId is null) return material;

        return material with { AgentId = _identity.AgentId, ProfileId = profileId };
    }

    private string? ResolveProfile(LeasedJob job, ProviderManifest manifest)
    {
        if (manifest.Runtime != ProviderRuntime.BrowserPersistent) return null;

        var profileId = job.ProfileId ?? job.Material?.ProfileId;

        // A first connect has nothing to point at yet. The agent owns the
        // profile, so the agent mints its id and returns it in the material.
        if (profileId is null && job.Kind is JobKind.Login)
        {
            profileId = Ids.New(Ids.Profile);
        }

        if (profileId is null)
        {
            throw ConnectorException.InvalidRequest(
                $"a {job.Kind} job for browser_persistent provider '{job.Provider}' carries no profile id");
        }

        return profileId;
    }

    private BrowserLeaseOptions BrowserOptionsFor(LeasedJob job, ProviderManifest manifest, string? profileId) => new()
    {
        Headless = _options.Headless,
        DeviceName = _options.BrowserDevice,
        Locale = _options.BrowserLocale,
        TimezoneId = _options.BrowserTimezoneId,
        StorageState = job.Material?.StorageState,
        ProfileDirectory = profileId is null ? null : _profiles.DirectoryFor(profileId, manifest.Id),
    };

    private void MarkProfile(string? profileId, string providerId, ErrorCode code)
    {
        if (profileId is null) return;

        if (code is ErrorCode.SessionExpired or ErrorCode.InvalidCredentials or ErrorCode.MfaFailed)
        {
            // The profile stopped authenticating; only a human can fix it, and
            // the heartbeat is how they find out.
            _profiles.MarkUnhealthy(profileId, providerId);
        }
    }

    private async Task FailAsync(LeasedJob job, AgentJobContext context, ErrorCode code, string? detail)
    {
        await context.FlushProgressAsync().ConfigureAwait(false);

        using var artifactTimeout = new CancellationTokenSource(ArtifactTimeout);
        var artifacts = await context.CaptureArtifactsAsync(artifactTimeout.Token).ConfigureAwait(false);

        await PostFailureAsync(job, code, SecretScrubber.Detail(detail, context.SecretValues), artifacts)
            .ConfigureAwait(false);
    }

    private async Task PostFailureAsync(LeasedJob job, ErrorCode code, string? detail, FailureArtifacts? artifacts)
    {
        _logger.LogWarning("job {JobId} ({Kind}/{Provider}) failed: {Code} {Detail}",
            job.JobId, job.Kind, job.Provider, ErrorCatalog.Wire(code), detail);

        var failure = new JobFailRequest
        {
            Code = ErrorCatalog.Wire(code),
            Detail = detail,
            Artifacts = artifacts,
        };

        if (!await SendAsync(ct => _control.FailAsync(job.JobId, failure, ct), $"failure for {job.JobId}")
                .ConfigureAwait(false))
        {
            _logger.LogError("job {JobId}: the failure report did not reach the control plane", job.JobId);
        }
    }

    /// <summary>
    /// A terminal post, retried while the failure looks like the link rather
    /// than the request. Uses its own timeout rather than the job's tokens:
    /// telling the control plane how a job ended must survive the job ending,
    /// including a shutdown that aborted it.
    /// </summary>
    private async Task<bool> SendAsync(Func<CancellationToken, Task> post, string what)
    {
        const int Attempts = 3;

        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TerminalPostTimeout);
                await post(timeout.Token).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is ControlPlaneException or OperationCanceledException)
            {
                var transient = (ex as ControlPlaneException)?.IsTransient ?? true;
                if (!transient || attempt == Attempts)
                {
                    _logger.LogWarning(ex, "posting the {What} failed on attempt {Attempt}", what, attempt);
                    return false;
                }

                await Task.Delay(TimeSpan.FromSeconds(attempt), _time).ConfigureAwait(false);
            }
        }

        return false;
    }

    /// <summary>
    /// Keeps the lease alive for as long as the job runs, and cancels the job
    /// the moment the control plane says the lease is no longer ours. Two
    /// agents driving the same login is the failure this prevents.
    ///
    /// "As long as the job runs" includes the whole time it is parked on a
    /// human: <paramref name="stop"/> is the job's own completion, never its
    /// budget or its cancellation. An unrenewed parked job is swept up by the
    /// control plane's lease expiry and handed to a second agent, which would
    /// undo the patience the budget now allows.
    /// </summary>
    private async Task RenewWhileRunningAsync(
        LeasedJob job, TimeSpan leaseTtl, Action onLost, CancellationTokenSource lease, CancellationToken stop)
    {
        var expiresAt = job.LeaseExpiresAt;

        try
        {
            while (!stop.IsCancellationRequested)
            {
                var remaining = expiresAt - _time.GetUtcNow();
                var delay = remaining / 2;
                if (delay < MinimumRenewInterval) delay = MinimumRenewInterval;

                await Task.Delay(delay, _time, stop).ConfigureAwait(false);

                bool held;
                try
                {
                    held = await _control.RenewAsync(job.JobId, stop).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The call did not produce a verdict, so the lease may well
                    // still be ours. Let the next round try again rather than
                    // killing a healthy job over a dropped connection.
                    _logger.LogDebug(ex, "job {JobId}: renew failed transiently", job.JobId);
                    continue;
                }

                if (!held)
                {
                    _logger.LogWarning("job {JobId}: the control plane no longer holds our lease", job.JobId);
                    onLost();
                    await lease.CancelAsync().ConfigureAwait(false);
                    return;
                }

                expiresAt = _time.GetUtcNow() + leaseTtl;
            }
        }
        catch (OperationCanceledException)
        {
            // The job finished; renewal stops with it.
        }
    }
}
