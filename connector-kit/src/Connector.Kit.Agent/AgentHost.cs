using System.Collections.Concurrent;
using Connector.Kit.Adapters;
using Connector.Kit.Agent.Browsing;
using Connector.Kit.Agent.Execution;
using Connector.Kit.Agent.Transport;
using Connector.Kit.AgentProtocol;
using Connector.Kit.Jobs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Connector.Kit.Agent;

/// <summary>
/// The lease loop: enroll once, heartbeat, long-poll for work, run it, repeat.
///
/// Every call in here is outbound. That single property is what lets an agent
/// sit behind NAT on a residential line, in a home lab, or on a user's own
/// machine with no inbound port and no redesign - and it is why bring-your-own
/// hardware is the same protocol from day one rather than a bolt-on.
/// </summary>
public sealed class AgentHost : BackgroundService
{
    private static readonly TimeSpan MinimumBackoff = TimeSpan.FromSeconds(1);

    private readonly ConnectorAgentOptions _options;
    private readonly ControlPlaneClient _control;
    private readonly AgentIdentity _identity;
    private readonly AgentStateStore _state;
    private readonly IProviderRegistry _registry;
    private readonly ProfileStore _profiles;
    private readonly JobRunner _runner;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<AgentHost> _logger;
    private readonly TimeProvider _time;

    private readonly ConcurrentDictionary<string, Task> _inflight = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _abort = new();
    private readonly SemaphoreSlim _slots;

    private TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(30);
    private TimeSpan _leaseTtl = TimeSpan.FromSeconds(120);
    private int _running;

    public AgentHost(
        ConnectorAgentOptions options,
        ControlPlaneClient control,
        AgentIdentity identity,
        AgentStateStore state,
        IProviderRegistry registry,
        ProfileStore profiles,
        JobRunner runner,
        IHostApplicationLifetime lifetime,
        ILogger<AgentHost> logger,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(time);

        _options = options;
        _control = control;
        _identity = identity;
        _state = state;
        _registry = registry;
        _profiles = profiles;
        _runner = runner;
        _lifetime = lifetime;
        _logger = logger;
        _time = time;
        _slots = new SemaphoreSlim(options.MaxConcurrency, options.MaxConcurrency);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SweepWorkRoot();

        if (!await EnsureEnrolledAsync(stoppingToken).ConfigureAwait(false))
        {
            // An agent that cannot enroll has nothing to do. Stopping the host
            // is an honest exit code for an operator or a container runtime.
            _lifetime.StopApplication();
            return;
        }

        _logger.LogInformation(
            "agent {AgentId} online: {Providers} at concurrency {Concurrency}",
            _identity.AgentId,
            string.Join(", ", Capabilities().Providers),
            _options.MaxConcurrency);

        var heartbeat = HeartbeatLoopAsync(stoppingToken);
        await LeaseLoopAsync(stoppingToken).ConfigureAwait(false);
        await heartbeat.ConfigureAwait(false);
    }

    /// <summary>
    /// Finishes or fails every in-flight job. A job that is simply dropped
    /// leaves a session stuck until a TTL notices it, and for a login that is a
    /// browser sitting open on a provider with a half-submitted form.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        var inflight = _inflight.Values.ToArray();
        if (inflight.Length == 0) return;

        _logger.LogInformation("draining {Count} in-flight job(s)", inflight.Length);

        var all = Task.WhenAll(inflight);
        var finished = await Task
            .WhenAny(all, Task.Delay(_options.ShutdownDrainTimeout, _time, CancellationToken.None))
            .ConfigureAwait(false);

        if (finished != all)
        {
            _logger.LogWarning(
                "the drain window elapsed; aborting {Count} job(s), each of which reports its own failure",
                _inflight.Count);

            await _abort.CancelAsync().ConfigureAwait(false);
            try
            {
                await all.WaitAsync(_options.ShutdownAbortGrace, _time, CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogError("{Count} job(s) did not stop within the abort grace period", _inflight.Count);
            }
        }
    }

    public override void Dispose()
    {
        _abort.Dispose();
        _slots.Dispose();
        base.Dispose();
    }

    private async Task LeaseLoopAsync(CancellationToken stoppingToken)
    {
        var backoff = MinimumBackoff;
        var accept = new LeaseRequest { Accept = [JobKind.Login, JobKind.Fetch, JobKind.Refresh, JobKind.Logout] };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _slots.WaitAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            LeasedJob? job;
            try
            {
                job = await _control.LeaseAsync(accept, stoppingToken).ConfigureAwait(false);
                backoff = MinimumBackoff;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                ReleaseSlot();
                return;
            }
            catch (Exception ex)
            {
                ReleaseSlot();

                if (ex is ControlPlaneException { IsAuthFailure: true })
                {
                    await RevokeAsync("the control plane rejected our token").ConfigureAwait(false);
                    return;
                }

                _logger.LogWarning(ex, "lease poll failed; retrying in {Backoff}", backoff);
                await SafeDelayAsync(backoff, stoppingToken).ConfigureAwait(false);
                backoff = Next(backoff);
                continue;
            }

            if (job is null)
            {
                ReleaseSlot();
                continue;
            }

            Start(job);
        }
    }

    private void Start(LeasedJob job)
    {
        Interlocked.Increment(ref _running);

        // Registered before the work starts, and completed by the work itself.
        // Publishing the Task afterwards would let a fast job remove an entry
        // that had not been added yet and leak it for the life of the process.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _inflight[job.JobId] = completion.Task;

        _ = Task.Run(async () =>
        {
            try
            {
                await _runner.RunAsync(job, _leaseTtl, _abort.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The runner reports its own failures; anything reaching here
                // would otherwise take the whole lease loop down with it.
                _logger.LogError(ex, "job {JobId} ended unexpectedly", job.JobId);
            }
            finally
            {
                Interlocked.Decrement(ref _running);
                _inflight.TryRemove(job.JobId, out _);
                ReleaseSlot();
                completion.SetResult();
            }
        });
    }

    /// <summary>
    /// Releasing a slot after the host has been disposed is a shutdown race,
    /// not an error - and throwing here would surface as an unobserved task
    /// exception rather than anything anyone can act on.
    /// </summary>
    private void ReleaseSlot()
    {
        try
        {
            _slots.Release();
        }
        catch (ObjectDisposedException)
        {
            // The host is gone; there is nothing left to admit.
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var response = await _control.HeartbeatAsync(new HeartbeatRequest
                {
                    Capabilities = Capabilities(),
                    Profiles = _profiles.Snapshot(),
                    Running = Volatile.Read(ref _running),
                }, stoppingToken).ConfigureAwait(false);

                if (response.LeaseTtlSeconds > 0) _leaseTtl = TimeSpan.FromSeconds(response.LeaseTtlSeconds);

                if (response.Revoked)
                {
                    await RevokeAsync("the control plane revoked this agent").ConfigureAwait(false);
                    return;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (ControlPlaneException ex) when (ex.IsAuthFailure)
            {
                await RevokeAsync("the control plane rejected our token").ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                // A missed heartbeat is survivable: the control plane's lease
                // TTL handles a genuinely dead agent, and a flapping home line
                // must not tear down running jobs.
                _logger.LogDebug(ex, "heartbeat failed");
            }

            await SafeDelayAsync(_heartbeatInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Enrollment happens exactly once. The code is one-time and HMAC-signed,
    /// so re-enrolling on every restart would leave a BYO user unable to bring
    /// their own agent back without asking for a new one.
    /// </summary>
    private async Task<bool> EnsureEnrolledAsync(CancellationToken ct)
    {
        var controlPlane = _options.RequireBaseAddress().AbsoluteUri;

        if (_state.Load(controlPlane) is { } stored)
        {
            _identity.Set(stored);
            _heartbeatInterval = TimeSpan.FromSeconds(Math.Max(5, stored.HeartbeatSeconds));
            _logger.LogInformation("resumed enrollment as {AgentId}", stored.AgentId);
            return true;
        }

        if (string.IsNullOrWhiteSpace(_options.EnrollmentCode))
        {
            _logger.LogError(
                "no enrollment state at {Path} and no enrollment code configured; mint one and set ConnectorAgent:EnrollmentCode",
                _state.FilePath);
            return false;
        }

        var request = new EnrollRequest
        {
            Code = _options.EnrollmentCode,
            Name = _options.AgentName,
            Capabilities = Capabilities(),
        };

        var backoff = MinimumBackoff;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var response = await _control.EnrollAsync(request, ct).ConfigureAwait(false);
                var enrollment = new AgentEnrollment
                {
                    AgentId = response.AgentId,
                    Token = response.Token,
                    ControlPlane = controlPlane,
                    EnrolledAt = _time.GetUtcNow(),
                    HeartbeatSeconds = response.HeartbeatSeconds,
                };

                // Persisted BEFORE the identity is used, so a crash between the
                // two cannot burn the one-time code.
                _state.Save(enrollment);
                _identity.Set(enrollment);
                _heartbeatInterval = TimeSpan.FromSeconds(Math.Max(5, response.HeartbeatSeconds));

                _logger.LogInformation("enrolled as {AgentId}", response.AgentId);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return false;
            }
            catch (ControlPlaneException ex) when (!ex.IsTransient)
            {
                // A rejected code is a verdict: it was wrong, used, or expired.
                // Hammering it would not change that and would look like abuse.
                _logger.LogError(ex, "enrollment was rejected; the code is wrong, used, or expired");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "enrollment could not reach the control plane; retrying in {Backoff}", backoff);
                await SafeDelayAsync(backoff, ct).ConfigureAwait(false);
                backoff = Next(backoff);
            }
        }

        return false;
    }

    /// <summary>
    /// A revoked agent stops and wipes its profiles. Those profiles hold live
    /// provider sessions the control plane can no longer route to, and leaving
    /// them on disk after the user has revoked the agent is exactly the residue
    /// the custody model exists to avoid.
    /// </summary>
    private async Task RevokeAsync(string why)
    {
        _logger.LogWarning("shutting down: {Why}", why);

        _profiles.WipeAll();
        _state.Clear();
        _identity.Clear();

        await _abort.CancelAsync().ConfigureAwait(false);
        _lifetime.StopApplication();
    }

    private AgentCapabilities Capabilities() => _options.BuildCapabilities(_registry.Manifests);

    /// <summary>
    /// Scratch directories left by a run that did not get to clean up after
    /// itself - a kill -9, a power cut. The residue rule applies to those too.
    /// </summary>
    private void SweepWorkRoot()
    {
        try
        {
            Directory.CreateDirectory(_options.WorkRootDirectory);

            foreach (var directory in Directory.EnumerateDirectories(_options.WorkRootDirectory))
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                    _logger.LogInformation("swept work directory {Directory} left by a previous run", directory);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "could not sweep {Directory}", directory);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "could not prepare the work root {Root}", _options.WorkRootDirectory);
        }
    }

    private TimeSpan Next(TimeSpan backoff)
    {
        var doubled = backoff * 2;
        return doubled > _options.MaxLeaseBackoff ? _options.MaxLeaseBackoff : doubled;
    }

    private async Task SafeDelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, _time, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down mid-wait is ordinary.
        }
    }
}
