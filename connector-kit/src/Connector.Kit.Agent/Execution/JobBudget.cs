namespace Connector.Kit.Agent.Execution;

/// <summary>
/// A job's time budget, measured in WORK rather than in wall clock.
///
/// The budget exists to stop a wedged adapter holding an agent forever, and
/// that is worth having. But a job parked on a challenge is the opposite of
/// wedged - it is doing precisely what it was built to do, and a challenge
/// already carries its own mandatory expiry, which is what bounds a human's
/// thinking time. Two clocks racing to kill the same run is how somebody
/// walking back to their desk loses their login, so this one stops while that
/// one runs.
///
/// A run that never parks never gets an extension, and that is the whole
/// point: an adapter that raises no challenge and simply hangs still dies on
/// schedule.
/// </summary>
internal sealed class JobBudget : IDisposable
{
    /// <summary>
    /// Floor on how often the watchdog re-checks WHILE PARKED. Parked time
    /// makes the remaining budget stand still, so a job that parked with a
    /// millisecond left would otherwise wake every millisecond until the human
    /// answered. A working job is not floored and dies exactly on its deadline.
    /// </summary>
    private static readonly TimeSpan MinimumRecheck = TimeSpan.FromSeconds(1);

    private readonly TimeSpan _budget;
    private readonly TimeProvider _time;
    private readonly DateTimeOffset _startedAt;
    private readonly CancellationTokenSource _expired = new();
    private readonly Lock _gate = new();

    private TimeSpan _parked;
    private DateTimeOffset? _parkedSince;
    private int _parkDepth;
    private int _disposed;

    public JobBudget(TimeSpan budget, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);

        _budget = budget < TimeSpan.Zero ? TimeSpan.Zero : budget;
        _time = time;
        _startedAt = time.GetUtcNow();
    }

    /// <summary>Cancelled once the job has spent its whole working budget.</summary>
    public CancellationToken Token => _expired.Token;

    /// <summary>True when this budget, rather than anything else, ended the run.</summary>
    public bool Expired => _expired.IsCancellationRequested;

    /// <summary>
    /// How long this job has been parked on a human in total, including a wait
    /// that is still going on. Reported in the failure detail so a run that
    /// does hit its budget is diagnosable rather than merely final.
    /// </summary>
    public TimeSpan HumanWait
    {
        get { lock (_gate) return ParkedAt(_time.GetUtcNow()); }
    }

    /// <summary>
    /// Stops the clock until the returned scope is disposed.
    ///
    /// Nesting is counted rather than assumed away: an adapter asks one
    /// question at a time today, but an overlapping or unbalanced scope must
    /// not be able to suspend the budget for the rest of the run.
    /// </summary>
    public IDisposable Park()
    {
        lock (_gate)
        {
            if (_parkDepth++ == 0) _parkedSince = _time.GetUtcNow();
        }

        return new ParkScope(this);
    }

    /// <summary>
    /// Cancels <see cref="Token"/> once the job has WORKED for its whole
    /// budget.
    ///
    /// A loop that re-reads the deadline every pass rather than a timer armed
    /// once, because parking moves the deadline and nothing can know in
    /// advance how far it will move.
    /// </summary>
    /// <param name="stop">Cancelled when the job reaches a terminal state.</param>
    public async Task WatchAsync(CancellationToken stop)
    {
        try
        {
            while (true)
            {
                var (remaining, parked) = Snapshot();
                if (remaining <= TimeSpan.Zero) break;

                await Task.Delay(parked && remaining < MinimumRecheck ? MinimumRecheck : remaining, _time, stop)
                    .ConfigureAwait(false);
            }

            // The job finished on the very last pass. Cancelling now would
            // dress an ordinary ending up as a timeout.
            if (stop.IsCancellationRequested) return;

            await _expired.CancelAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The job reached a terminal state first; there is nothing to kill.
        }
        catch (ObjectDisposedException)
        {
            // The same, one instruction later.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _expired.Dispose();
    }

    /// <summary>Budget left, and whether the clock is currently stopped.</summary>
    private (TimeSpan Remaining, bool Parked) Snapshot()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            return (_budget - ((now - _startedAt) - ParkedAt(now)), _parkedSince is not null);
        }
    }

    /// <summary>Callers hold <see cref="_gate"/>.</summary>
    private TimeSpan ParkedAt(DateTimeOffset now) =>
        _parkedSince is { } since ? _parked + (now - since) : _parked;

    private void Unpark()
    {
        lock (_gate)
        {
            if (_parkDepth == 0 || --_parkDepth > 0) return;

            if (_parkedSince is { } since) _parked += _time.GetUtcNow() - since;
            _parkedSince = null;
        }
    }

    private sealed class ParkScope(JobBudget budget) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            budget.Unpark();
        }
    }
}
