using System.Collections.Concurrent;

namespace Connector.Kit.Agent.Networking;

/// <summary>
/// The agent-wide pacing state: one gate per provider, shared by every job
/// running in this process.
///
/// Two properties, and both are per PROVIDER rather than per job. Serialising
/// only within a job would let two concurrent sessions for the same provider
/// double the call rate that provider sees, which is exactly the cadence a
/// bot-protection system is looking for. The gap is honoured across jobs for
/// the same reason.
/// </summary>
public sealed class PolitenessGate
{
    /// <summary>
    /// Jitter as a fraction of the configured gap. A fleet whose calls land on
    /// an exact interval is more machine-shaped than one that does not.
    /// </summary>
    private const double JitterFraction = 0.5;

    private readonly ConcurrentDictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public PolitenessGate(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>
    /// Waits for this provider's turn, then for the remaining gap. The returned
    /// ticket must be disposed when the call completes: disposal is what stamps
    /// the next allowed time and releases the next caller.
    /// </summary>
    public async Task<IDisposable> EnterAsync(string providerId, TimeSpan minGap, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        var slot = _slots.GetOrAdd(providerId, static _ => new Slot());
        await slot.Gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            var wait = slot.NextAllowedAt - _time.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, _time, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            // A cancelled wait must not leave the provider's gate held forever.
            slot.Gate.Release();
            throw;
        }

        return new Ticket(this, slot, minGap < TimeSpan.Zero ? TimeSpan.Zero : minGap);
    }

    /// <summary>
    /// Pushes the next allowed time out further than the normal gap - what a
    /// provider's own <c>Retry-After</c> asked for. Backing off when told to is
    /// the difference between a rate limit and a block.
    /// </summary>
    public void Penalise(string providerId, TimeSpan delay)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        if (delay <= TimeSpan.Zero) return;

        var slot = _slots.GetOrAdd(providerId, static _ => new Slot());
        Extend(slot, _time.GetUtcNow() + delay);
    }

    private static void Extend(Slot slot, DateTimeOffset until)
    {
        if (until > slot.NextAllowedAt) slot.NextAllowedAt = until;
    }

    private sealed class Slot
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        /// <summary>Only ever mutated by the holder of <see cref="Gate"/>.</summary>
        public DateTimeOffset NextAllowedAt { get; set; } = DateTimeOffset.MinValue;
    }

    private sealed class Ticket : IDisposable
    {
        private readonly PolitenessGate _owner;
        private readonly Slot _slot;
        private readonly TimeSpan _gap;
        private int _disposed;

        public Ticket(PolitenessGate owner, Slot slot, TimeSpan gap)
        {
            _owner = owner;
            _slot = slot;
            _gap = gap;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

            var jitter = TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * _gap.TotalMilliseconds * JitterFraction);
            Extend(_slot, _owner._time.GetUtcNow() + _gap + jitter);
            _slot.Gate.Release();
        }
    }
}
